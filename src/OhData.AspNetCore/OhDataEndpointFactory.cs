using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.AspNetCore.OData.Query.Validator;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.UriParser;


namespace OhData;

// #203: per-entity-set write-body-size limit, attached as route-group endpoint metadata (see
// MapEntitySet) and enforced by the group-level write-body-size filter in MapAll. Absent metadata
// means "no OhData-level limit" — the host's Kestrel MaxRequestBodySize still applies.
internal sealed record OhDataBodyLimitMetadata(long MaxBytes);

internal static class OhDataEndpointFactory
{
    private static readonly MethodInfo _mapEntitySetMethod =
        typeof(OhDataEndpointFactory)
            .GetMethod(nameof(MapEntitySet), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly string[] PatchMethod = new[] { "PATCH" };

    // V3: compiled key-accessor cache for $ref GET reference building. Keyed by (childType,
    // propertyName) since a single navigation route may see multiple concrete child types
    // (e.g. EF Core proxies). Expression.Compile() is expensive; caching avoids recompiling
    // per request, mirroring the compiled-delegate cache pattern used for ETag/key-to-string
    // in EntitySetProfile.
    private static readonly ConcurrentDictionary<(Type ChildType, string PropertyName), Func<object, object?>>
        s_navRefKeyAccessorCache = new();

    private static Func<object, object?> GetOrCompileNavRefKeyAccessor(Type childType, string propertyName)
    {
        return s_navRefKeyAccessorCache.GetOrAdd((childType, propertyName), key =>
        {
            var (type, propName) = key;
            PropertyInfo? prop = type.GetProperty(
                propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null)
            {
                // No matching property on this concrete type — always return null.
                return static _ => null;
            }

            ParameterExpression param = Expression.Parameter(typeof(object), "obj");
            UnaryExpression cast = Expression.Convert(param, type);
            MemberExpression propAccess = Expression.Property(cast, prop);
            UnaryExpression boxed = Expression.Convert(propAccess, typeof(object));
            return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
        });
    }

    private static string SanitizeLogValue(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);

    // A StringWriter reports UTF-16 as its Encoding (the CLR string's native encoding), which
    // XmlWriter stamps into the CSDL prolog as encoding="utf-16". But the document is served as
    // UTF-8 bytes (see the /$metadata route), so the prolog would contradict the wire encoding and
    // a strict XML consumer (e.g. an OData codegen client) would try to decode UTF-8 as UTF-16 and
    // fail (#180). Overriding Encoding to UTF-8 makes XmlWriter emit encoding="utf-8" so the prolog,
    // the served bytes, and the response charset all agree.
    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb) { }
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static string BuildMetadataXml(IEdmModel model)
    {
        var sb = new StringBuilder();
        using var stringWriter = new Utf8StringWriter(sb);
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true });
        if (!CsdlWriter.TryWriteCsdl(model, xmlWriter, CsdlTarget.OData, out var errors))
        {
            throw new InvalidOperationException(
                "Failed to generate OData CSDL metadata: " +
                string.Join("; ", errors.Select(e => e.ToString())));
        }

        xmlWriter.Flush();
        return sb.ToString();
    }

    // #468: one entry of the OData service document (JSON Format section 5). The JSON names are
    // pinned with [JsonPropertyName] rather than left to a naming policy, because the wire names
    // are lower-case by spec whatever any policy says. (#495: the route no longer serializes
    // through Results.Ok / the host's JsonOptions -- it pre-renders with
    // _frameworkEnvelopeSerializerOptions -- but the pinned names stay, for the same reason.)
    private sealed record ServiceDocumentEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("url")] string Url);

    // #468: project one EDM entity-container element onto its service-document entry, or null for
    // an element the spec keeps out of the document. Action imports are never listed (they are not
    // GET-addressable); a function import is listed exactly when its own IncludeInServiceDocument
    // flag says so, which is what makes the document and $metadata agree by construction rather
    // than by two hand-maintained lists happening to match.
    private static ServiceDocumentEntry? ServiceDocumentEntryFor(IEdmEntityContainerElement element) => element switch
    {
        IEdmEntitySet set => new ServiceDocumentEntry(set.Name, "EntitySet", set.Name),
        IEdmSingleton singleton => new ServiceDocumentEntry(singleton.Name, "Singleton", singleton.Name),
        IEdmFunctionImport { IncludeInServiceDocument: true } fi =>
            new ServiceDocumentEntry(fi.Name, "FunctionImport", fi.Name),
        _ => null,
    };

    // #468: CSDL validation of the built EDM, run once at MapOhData() alongside the other startup
    // validation passes. EdmValidator was called nowhere in this assembly, which is how
    // IncludeInServiceDocument="true" on a parameterized function import -- illegal per CSDL 4.0
    // section 13.6 -- reached the wire unnoticed. Note the reader-vs-validator asymmetry that
    // hid it: CsdlReader.TryParse accepts an invalid identifier and this rule alike, so a
    // consumer that merely parses the document survives while a codegen tool that validates does
    // not. Failing here turns "the customer's codegen tool rejects your $metadata" into a startup
    // exception naming the offending construct.
    private static void ValidateEdmModelOrThrow(IEdmModel model, string prefix)
    {
        if (Microsoft.OData.Edm.Validation.EdmValidator.Validate(model, out var errors)) return;

        string detail = string.Join("; ", errors.Select(e =>
            $"{e.ErrorCode} at {e.ErrorLocation}: {e.ErrorMessage}"));
        throw new InvalidOperationException(
            $"OhData: the EDM model for the registration at prefix '{prefix}' is not valid CSDL. " +
            "A consumer that validates $metadata (most codegen tools do) will reject it. " +
            $"Offending construct(s): {detail}");
    }

    internal static string BuildBaseUrl(HttpContext ctx, string prefix) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";

    // Canonical entity-id URL: {base}/{set}({key}), with the key formatted URL-safely (single-quoted
    // + percent-encoded for string keys) exactly as ODataKeyParser expects to read it back in.
    private static string BuildEntityId(string baseUrl, string setName, object key) =>
        $"{baseUrl}/{setName}({ODataEntityKeyUrlFormatter.Format(key)})";

    private static string BuildEntityId(HttpContext ctx, string prefix, string setName, object key) =>
        BuildEntityId(BuildBaseUrl(ctx, prefix), setName, key);

    private static string BuildNextPageLink(HttpContext ctx, string skiptoken)
    {
        var req = ctx.Request;
        var query = HttpUtility.ParseQueryString(req.QueryString.ToString());
        query.Remove("$skip");
        query["$skiptoken"] = skiptoken;
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}?{query}";
    }

    // #201: continuation link for the GetAll path, expressed as $skip rather than the opaque
    // $skiptoken BuildNextPageLink emits. GetAll applies $skip itself (ApplyGetAllPaging), so the
    // framework can stand behind a $skip continuation there: every hop it emits, it also honours.
    // NOT used by the Priority-1 path — see BuildFrameworkSkipLink for why.
    internal static string BuildNextPageLinkWithSkip(HttpContext ctx, int skip)
    {
        var req = ctx.Request;
        var query = HttpUtility.ParseQueryString(req.QueryString.ToString());
        query.Remove("$skiptoken");
        query.Remove(FrameworkSkipOption);
        query["$skip"] = skip.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}?{query}";
    }

    // #360: the Priority-1 continuation offset, carried as a CUSTOM (non-'$') query option.
    //
    // On that path the profile owns query application, so a `$skip=N` continuation is unhonourable by
    // the framework -- it emits the link but never applies the skip, and a profile that does not
    // re-apply the incoming options serves the identical first page forever, so a client walking
    // nextLink never terminates.
    //
    // Whether the profile applied $skip is not reliably observable (ApplyTo is virtual, but
    // options.Skip.ApplyTo bypasses any override, and expression probing false-negatives on the very
    // common "materialize then AsQueryable()" shape). So the framework carries its OWN offset and
    // applies it itself. The incoming $skip is left verbatim on every hop, so a profile that DOES
    // apply it re-establishes the same base page and the framework offset accumulates on top -- no
    // double-skip either way.
    //
    // It cannot be $skiptoken: ApplyTo THROWS on one it has no handler for, which would break every
    // profile that calls it. A custom option is ignored by ApplyTo and by OhData's own gating, and a
    // nextLink is opaque to clients by spec (§11.2.5.7).
    private const string FrameworkSkipOption = "ohdata-skiptoken";

    private static string BuildFrameworkSkipLink(HttpContext ctx, int skip)
    {
        var req = ctx.Request;
        var query = HttpUtility.ParseQueryString(req.QueryString.ToString());
        query[FrameworkSkipOption] = Convert.ToBase64String(BitConverter.GetBytes(skip));
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}?{query}";
    }

    // Reads the framework continuation offset back off a follow-up request. Returns false only when
    // the value is present but unreadable (a hand-edited/corrupted opaque link) → 400, mirroring the
    // Priority-2 $skiptoken handling.
    private static bool TryReadFrameworkSkip(HttpContext ctx, out int skip)
    {
        skip = 0;
        if (!ctx.Request.Query.TryGetValue(FrameworkSkipOption, out var raw)) return true;
        try
        {
            byte[] bytes = Convert.FromBase64String(Uri.UnescapeDataString(raw.ToString()));
            skip = BitConverter.ToInt32(bytes, 0);
        }
        // The two ways a hand-edited token fails, and the only two these three calls raise:
        // FormatException from Convert.FromBase64String (non-base64 character, bad padding), and
        // ArgumentException from BitConverter.ToInt32 when the decode yields fewer than 4 bytes —
        // as ArgumentOutOfRangeException for an EMPTY array ("?ohdata-skiptoken=", which decodes
        // to zero bytes) and as plain ArgumentException for 1-3 bytes. Uri.UnescapeDataString
        // throws for none of this (a malformed "%zz" is passed through verbatim), and would raise
        // UriFormatException : FormatException if it ever did. Deliberately NOT a bare catch: an
        // unrelated failure here is a bug, and should surface as a 500 rather than be laundered
        // into "the client sent a bad token".
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
        return skip >= 0;
    }

    private static bool PrefersMinimal(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("Prefer", out var prefer) &&
        prefer.ToString().Contains("return=minimal", StringComparison.OrdinalIgnoreCase);

    // §8.2.8.7: Prefer: return=representation is an explicit opt-in for behaviour that is already
    // OhData's default (write handlers return the representation). Acknowledge it in the response
    // header when the client asked — the symmetric counterpart to PrefersMinimal above.
    private static void EchoReturnRepresentationPreference(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("Prefer", out var prefer)
            && prefer.ToString().Contains("return=representation", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Preference-Applied"] = "return=representation";
        }
    }

    // #203: the write methods that carry a request body OhData deserializes. DELETE is excluded
    // (its $ref variant reads only a small link body and no body-size limit is meaningful there).
    private static bool IsBodyBearingWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

    // #200: derive the telemetry dimensions from the matched endpoint. entitySet comes from the
    // route's WithTags(name) metadata; route is the raw template (the precise identity, mirroring
    // ASP.NET Core's http.route); operation is a coarse method/shape label for convenient grouping.
    private static (string? entitySet, string? route, string operation) DescribeOhDataEndpoint(HttpContext http)
    {
        Endpoint? endpoint = http.GetEndpoint();
        string? route = (endpoint as RouteEndpoint)?.RoutePattern.RawText;
        string? entitySet = endpoint?.Metadata.GetMetadata<ITagsMetadata>()?.Tags is { Count: > 0 } tags
            ? tags[0]
            : null;
        return (entitySet, route, ClassifyOperation(http.Request.Method, route));
    }

    private static string ClassifyOperation(string method, string? route)
    {
        route ??= "";
        if (route.EndsWith("/$metadata", StringComparison.Ordinal)) return "metadata";
        if (route.EndsWith("/$count", StringComparison.Ordinal)) return "read-count";
        if (route.EndsWith("/$value", StringComparison.Ordinal)) return "read-value";
        if (route.EndsWith("/$ref", StringComparison.Ordinal))
            return HttpMethods.IsGet(method) ? "read-ref" : HttpMethods.IsDelete(method) ? "delete-ref" : "write-ref";

        int keyEnd = route.IndexOf("({key})", StringComparison.Ordinal);
        bool hasKey = keyEnd >= 0;
        if (hasKey && route.IndexOf('/', keyEnd) >= 0) // a segment after the key → navigation/property
        {
            return method switch
            {
                _ when HttpMethods.IsGet(method) => "read-navigation",
                _ when HttpMethods.IsPost(method) => "create-navigation",
                _ when HttpMethods.IsDelete(method) => "delete-navigation",
                _ => "update-navigation",
            };
        }
        if (hasKey)
        {
            return method switch
            {
                _ when HttpMethods.IsGet(method) => "read-entity",
                _ when HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) => "update-entity",
                _ when HttpMethods.IsDelete(method) => "delete-entity",
                _ => "entity",
            };
        }
        // no key: collection routes plus bound/unbound operations (the http.route tag disambiguates).
        return HttpMethods.IsGet(method) ? "read-collection" : HttpMethods.IsPost(method) ? "create" : "collection";
    }

    // BUG 1 fix: POST/PUT/PATCH bodies are read and deserialized manually (see below) rather
    // than via a `TModel model` minimal-API parameter, so content-type negotiation must be done
    // by hand too -- otherwise a mismatched Content-Type would either be silently ignored (we'd
    // try to parse non-JSON as JSON) or, if left to ASP.NET Core's implicit binder/`.Accepts<T>()`
    // metadata, would short-circuit with an empty 415 body before this OData error-formatting
    // code ever runs. Media-type parameters (e.g. ";odata.metadata=full", ";charset=utf-8") are
    // stripped before comparison since they don't affect whether the payload is JSON.
    private static bool IsJsonContentType(HttpContext ctx)
    {
        string? contentType = ctx.Request.ContentType;
        if (string.IsNullOrEmpty(contentType)) return false;
        string mediaType = contentType.Split(';')[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult UnsupportedMediaTypeError(HttpContext ctx) =>
        ODataError(415, "UnsupportedMediaType",
            $"The content type '{ctx.Request.ContentType ?? "(none)"}' is not supported. " +
            "Use 'application/json'.");

    // Deep insert (§32/§11.4.2.2): `prop@odata.bind` (JSON format §8.5 — link to an *existing*
    // entity instead of creating a new one) is documented non-support for 1.0.0. Detect the
    // annotation anywhere in the POST body (top level or nested inside a deep-insert child) and
    // reject explicitly rather than silently ignoring it, so a client relying on link-by-bind
    // doesn't get a response that looks successful but didn't do what it asked for.
    private static bool ContainsODataBindAnnotation(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name.EndsWith("@odata.bind", StringComparison.Ordinal)) return true;
                    if (ContainsODataBindAnnotation(prop.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray()
                    .Where(ContainsODataBindAnnotation)
                    .Any();
            default:
                return false;
        }
    }

    private static ReadOnlySpan<byte> ODataBindSuffixUtf8 =>
        new[]
        {
            (byte)'@', (byte)'o', (byte)'d', (byte)'a', (byte)'t', (byte)'a',
            (byte)'.', (byte)'b', (byte)'i', (byte)'n', (byte)'d',
        };

    // #511: the contract the write path's binder resolves, which is where the body-name tables take
    // their keys from. A probe COPY, not the real instance: resolving a JsonTypeInfo calls
    // MakeReadOnly(), and startup must stay free to keep configuring the registration's options.
    // Null on failure falls back to the EDM/CLR aliases -- a model whose contract cannot be built
    // cannot be deserialized either. One probe per options instance, held weakly.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions>
        s_writeContractProbeCache = new();

    private static JsonTypeInfo? TryResolveWriteContract(
        Type modelType, JsonSerializerOptions? jsonOptions)
    {
        JsonSerializerOptions source = jsonOptions ?? _pascalCaseSerializerOptions;
        JsonSerializerOptions probe =
            s_writeContractProbeCache.GetValue(source, static s => new JsonSerializerOptions(s));
        try
        {
            return probe.GetTypeInfo(modelType);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // #355: one structural property the EDM declares Nullable="false", paired with the CLR member a
    // bound instance carries it on.
    private sealed record EdmRequiredProperty(string EdmName, PropertyInfo Clr);

    // #355: "which properties does the framework's OWN $metadata say cannot be null?", asked of the
    // EDM once per type at startup. #544 scopes what is done with the answer: a property is checked
    // only where the request BODY NAMED it, and this set is just the vocabulary of that question.
    //
    // THE EDM AND NOTHING ELSE. Deriving "required" from the CLR type, [Required] or NRT annotations
    // is the second-independently-derived-model hazard (#454/#458/#511), and it was not hypothetical:
    // the structural-property write route had its own check built on IsNullableClrType, for which
    // EVERY reference type is nullable, so a Nullable="false" string sailed through. Before #355 a
    // null for such a property reached the handler and surfaced as EF's 500.
    //
    // THREE DELIBERATE EXCLUSIONS, each of which would otherwise reject a legal request: a
    // non-nullable VALUE type (a JSON null is already a JsonException, and checking costs a boxing
    // read to answer a question with one possible answer); a member no readable CLR property backs;
    // and anything the EDM does not declare, which is what makes Ignore()d properties exempt for free.
    //
    // #557: the KEY used to be a fourth. It was excluded because a service-generated key is routinely
    // OMITTED (§11.4.2) -- correct while the gate also rejected omission, and vestigial once #544
    // removed that leg, since the namedByBody intersection now provides the omission exemption. What
    // it still did was hide an explicit null: a REFERENCE-typed key sailed through, RAN THE HANDLER,
    // and then died in ODataEntityKeyUrlFormatter.Format, so a persisting Post had already persisted
    // when the 500 arrived. Measured, on properties $metadata describes identically:
    // {"Code":null} -> 500 handler-reached, {"Name":null} -> 400 handler-not-reached.
    // A value-typed key needs no exclusion of its own -- it is covered by the value-type rule below.
    //
    // TOP LEVEL ONLY: a null inside a nested complex value is not checked. Widening has its own
    // recursion and cycle decisions.
    private static EdmRequiredProperty[] BuildEdmRequiredProperties(
        IEdmStructuredType? edmType, Type clrType)
    {
        if (edmType is null) return Array.Empty<EdmRequiredProperty>();

        var required = new List<EdmRequiredProperty>();
        foreach (IEdmStructuralProperty edmProp in edmType.StructuralProperties())
        {
            if (edmProp.Type.IsNullable) continue;

            // The EDM name IS the [JsonPropertyName]-or-CLR name (#253), which is exactly what
            // FindClrPropertyByEdmName resolves. The string comes from the model, never from a
            // request, so this is a bounded startup-time use of the memoizing helper (#510).
            PropertyInfo? clr = ODataPropertyNaming.FindClrPropertyByEdmName(clrType, edmProp.Name);
            if (clr is null || !clr.CanRead) continue;
            if (clr.PropertyType.IsValueType && Nullable.GetUnderlyingType(clr.PropertyType) is null)
                continue;

            required.Add(new EdmRequiredProperty(edmProp.Name, clr));
        }

        return required.Count == 0 ? Array.Empty<EdmRequiredProperty>() : required.ToArray();
    }

    /// <summary>
    /// #355/#544: the whole-instance check, for the routes that produce a complete entity — the
    /// collection <c>POST</c>, <c>PUT</c>, and the navigation-<c>POST</c> create route. Only a
    /// property the request body actually NAMED is checked, and only when the value it bound to is
    /// <c>null</c>: an explicit <c>null</c> for a <c>Nullable="false"</c> property is the violation,
    /// and an omission is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#544 — why the omitted-property leg is gone.</b> It shipped citing §11.4.2, which
    /// requires nothing of the kind: its only MUST-fail is <i>"The service MUST fail if unable to
    /// persist all property values <b>specified in the request</b>"</i> — about values that were
    /// sent. §11.4.2 also permits omission outright, though for exactly two categories rather than
    /// the broad set once claimed here: <i>"Properties computed by the service (annotated with the
    /// term Core.Computed …) and properties that are tied to properties of the principal entity by
    /// a referential constraint, can be omitted and MUST ignored if included in the request."</i>
    /// <b>No clause anywhere in Part 1 mandates a <c>400</c> for an omitted property.</b> The
    /// nearest one is §11.4.3, is <b>PUT-only</b>, and prescribes the opposite remedy:
    /// <i>"Missing non-key, updatable structural properties not defined as dependent properties
    /// within a referential constraint MUST be set to their default values."</i> That is a
    /// statement about what the service stores, not about refusing the request, and OhData leaves
    /// it to the handler, which owns persistence. <c>Microsoft.AspNetCore.OData</c> lands in the
    /// same place — <c>ApplyStructuralProperties</c> loops over payload properties only and has no
    /// reverse pass, while <c>ValidateNullValueAllowed</c> rejects an explicit <c>null</c>.
    /// </para>
    /// <para>
    /// <b>Why that also RESOLVES the incoherence #545 recorded.</b> Three properties the
    /// framework's own <c>$metadata</c> describes identically as <c>Nullable="false"</c> —
    /// <c>string X = ""</c>, <c>string X = null!</c> and <c>int Year</c> — used to answer
    /// <c>201</c>, <c>400</c> and <c>201</c> on an omission, i.e. the wire behaviour was decided by
    /// a CLR initializer and by value-versus-reference, neither of which appears in the published
    /// contract. All three now accept an omission and reject an explicit <c>null</c>.
    /// </para>
    /// <para>
    /// <b>It still reads the BOUND INSTANCE</b>, intersected with the names the body carried, so the
    /// check remains a statement about what the handler would have received. That intersection is a
    /// strict narrowing of the pre-#544 condition: nothing newly rejects, which is what keeps #355's
    /// own defect (<c>POST {"Title":null}</c> reaching EF as a <c>500</c>) closed.
    /// </para>
    /// </remarks>
    // #569/#558: ONE envelope for one condition. Four sites used to answer it three different ways,
    // two of them with different `code` values -- POST/PUT/nav-POST said "cannot be null",
    // PATCH said "cannot be set to null", and the property writes said `BadRequest` with "is not
    // nullable". A client moving from PATCH /Set(1) to PUT /Set(1)/Prop got a different code for the
    // same rejection, against the rule #543 states.
    //
    // The wording has to be true of THREE arrivals at that condition, which is why it is not simply
    // PATCH's:
    //   * the body named the property with an explicit null;
    //   * the body sent a value under a spelling the binder ignored, so nothing bound and the CLR
    //     default is null -- #558, reachable under a non-case-preserving PropertyNamingPolicy,
    //     because the body-name table carries EDM and CLR aliases the binder does not honour. Those
    //     aliases must STAY (dropping them fails OPEN), so the message is what gets fixed;
    //   * DELETE /Set(key)/Prop, which supplies no value at all.
    // "did not supply a non-null value" is true of all three; "cannot be null" is false of the second.
    private static IResult NonNullablePropertyError(string edmName) =>
        ODataError(400, "InvalidBody",
            $"Property '{edmName}' is declared non-nullable by the service metadata, and the " +
            "request did not supply a non-null value for it.", target: edmName);

    private static IResult? ValidateEdmRequiredProperties(
        EdmRequiredProperty[] required, object instance, HashSet<string>? namedByBody)
    {
        foreach (EdmRequiredProperty p in required)
        {
            if (namedByBody is null || !namedByBody.Contains(p.Clr.Name)) continue;
            if (p.Clr.GetValue(instance) is null)
            {
                return NonNullablePropertyError(p.EdmName);
            }
        }
        return null;
    }

    /// <summary>
    /// #355: the partial-update twin, for <c>PATCH</c> and the structural-property writes. Only a
    /// property the body actually NAMED is checked — a <c>Delta&lt;T&gt;</c> is a change set, so an
    /// absent property is "leave it alone", not "set it to nothing", and rejecting it would break
    /// every ordinary partial update.
    /// </summary>
    private static IResult? ValidateEdmRequiredDelta<TDelta>(
        EdmRequiredProperty[] required,
        Microsoft.AspNetCore.OData.Deltas.Delta<TDelta> delta)
        where TDelta : class
    {
        if (required.Length == 0) return null;

        var changed = new HashSet<string>(delta.GetChangedPropertyNames(), StringComparer.Ordinal);
        foreach (EdmRequiredProperty p in required)
        {
            if (!changed.Contains(p.Clr.Name)) continue;
            if (delta.TryGetPropertyValue(p.Clr.Name, out object? value) && value is null)
            {
                return NonNullablePropertyError(p.EdmName);
            }
        }
        return null;
    }

    private static ReadOnlySpan<byte> Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF };

    // #511: the one place either span scanner gets a reader. A scanner that reads the body
    // differently from the binder is a FAIL-OPEN, not a mismatch: both swallow their JsonException
    // so DeserializeAsync stays the sole author of the malformed-body message (#389 L1), and that
    // swallow is only safe while the two accept the same bodies. Measured divergences: a leading
    // UTF-8 BOM, and any host-relaxed ReadCommentHandling/AllowTrailingCommas/MaxDepth -- in each
    // case the scan stopped early and silently reported "nothing found". These three members are
    // what JsonSerializerOptions.GetReaderOptions() derives for DeserializeAsync. .NET 10's
    // AllowDuplicateProperties is deliberately not derived: net8.0 lacks it and the residual runs
    // the safe way.
    private static Utf8JsonReader CreateBinderParityReader(
        ReadOnlySpan<byte> utf8Json, JsonSerializerOptions? jsonOptions)
    {
        if (utf8Json.StartsWith(Utf8Bom)) utf8Json = utf8Json.Slice(Utf8Bom.Length);

        JsonSerializerOptions options = jsonOptions ?? _pascalCaseSerializerOptions;
        return new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = options.AllowTrailingCommas,
            CommentHandling = options.ReadCommentHandling,
            MaxDepth = options.MaxDepth,
        });
    }

    // #514: the JsonDocument half of #511's rule. Every place the write path materialises a body
    // with JsonDocument shadows the same binder, and a default JsonDocumentOptions makes it a
    // SECOND authority on what well-formed JSON is: startupJsonOptions copies the host's Http.Json
    // options, so DeserializeAsync honours ReadCommentHandling.Skip, AllowTrailingCommas and a
    // raised MaxDepth, and JsonDocument.ParseAsync with the defaults does not.
    //
    // Measured on the pre-fix tree with a host setting Skip + AllowTrailingCommas: the same bytes
    // answered 200 on PUT (which streams into the binder) and 400 on the collection POST — the
    // per-verb divergence this milestone spent ten PRs removing, one option over from #456's.
    //
    // It fails CLOSED, which is the whole reason it is a lower-severity issue than #511 and not a
    // safety one: the stricter reader rejects a request rather than silently disabling a guard. The
    // three members are the same three CreateBinderParityReader derives and for the same reason —
    // they are what JsonSerializerOptions.GetReaderOptions() derives internally for
    // DeserializeAsync, so this is parity rather than a second guess. MaxDepth needs no translation:
    // 0 means "the 64 default" on both types. .NET 10's AllowDuplicateProperties is deliberately not
    // derived, exactly as in CreateBinderParityReader — net8.0 has neither member, and the residual
    // runs the safe way.
    private static JsonDocumentOptions CreateBinderParityDocumentOptions(
        JsonSerializerOptions? jsonOptions)
    {
        JsonSerializerOptions options = jsonOptions ?? _pascalCaseSerializerOptions;
        return new JsonDocumentOptions
        {
            AllowTrailingCommas = options.AllowTrailingCommas,
            CommentHandling = options.ReadCommentHandling,
            MaxDepth = options.MaxDepth,
        };
    }

    private static bool ContainsODataBindAnnotation(
        ReadOnlySpan<byte> utf8Json, JsonSerializerOptions? jsonOptions)
    {
        Utf8JsonReader reader = CreateBinderParityReader(utf8Json, jsonOptions);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueSpan.EndsWith(ODataBindSuffixUtf8)) return true;
                if (reader.ValueIsEscaped &&
                    reader.GetString()!.EndsWith("@odata.bind", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Deliberately swallowed -- see the note above. The deserializer re-reads the same bytes
            // and produces the canonical 400.
        }

        return false;
    }

    // #506/#511/#544: "which spelling would the BINDER have matched this member against?" -- the
    // one table every top-level body-presence question on the write path is asked in.
    //
    // ONE builder for its three callers (the deep-write strip's navigation table, and the required-
    // property tables for TModel and the navigation-POST child type). A second transcription is the
    // independently-derived-second-model hazard #454/#458/#511 record -- and this site has already
    // realised it once: #536 exists only because #511 rebuilt one of these tables and not the other.
    //
    // #511: the primary key is JsonTypeInfo.Properties[].Name, the string STJ itself matches against.
    // It used to be the policy-FREE EDM name plus the CLR name, while the binder matches the
    // POLICY-CONVERTED name -- camelCase differs from the CLR name only by case, so OrdinalIgnoreCase
    // hid it for the one policy anyone configured. Measured: under SnakeCaseLower a body naming
    // `back_orders` was bound by the deserializer and missed here, so the strip never fired.
    //
    // Adding ConvertName() as a third key would close that policy, not the CLASS.
    private static Dictionary<string, PropertyInfo> BuildBinderBodyNameTable(
        (string EdmName, PropertyInfo Clr)[] members,
        Type clrType,
        JsonSerializerOptions? jsonOptions)
    {
        var table = new Dictionary<string, PropertyInfo>(
            (jsonOptions ?? _pascalCaseSerializerOptions).PropertyNameCaseInsensitive
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        if (members.Length == 0) return table;

        JsonTypeInfo? contract = TryResolveWriteContract(clrType, jsonOptions);
        if (contract is not null)
        {
            foreach (JsonPropertyInfo contractProp in contract.Properties)
            {
                if (contractProp.AttributeProvider is not PropertyInfo clrMember) continue;
                foreach ((string _, PropertyInfo clr) in members)
                {
                    if (!clrMember.HasSameMetadataDefinitionAs(clr)) continue;
                    table[contractProp.Name] = clr;
                    break;
                }
            }
        }

        foreach ((string edmName, PropertyInfo clr) in members) table.TryAdd(edmName, clr);
        foreach ((string _, PropertyInfo clr) in members) table.TryAdd(clr.Name, clr);

        return table;
    }

    // #506/#544: "which of these members did the root object actually NAME?" -- asked by the
    // deep-write strip before it nulls anything, and by #355's required-property gate before it
    // reports anything. One implementation for both: the two tables differ, the question does not.
    //
    // The STRIP needs it because nulling a navigation the body never mentioned DESTROYS state -- a
    // `List<Child> Kids { get; private set; } = new()` reached the handler as null rather than the
    // constructor's empty list on a PUT naming no navigation, and a handler diff-syncing it reads
    // that as "clear the relationship" or throws.
    //
    // #355's GATE needs it because reading the BOUND INSTANCE alone made "the body sent null" and
    // "the body said nothing and the CLR declaration left it null" the same observation -- which made
    // the wire behaviour depend on a CLR initializer that appears nowhere in $metadata.
    //
    // TOP LEVEL ONLY: both tables hold properties of the ROOT type, so a member named inside a nested
    // object belongs to another type. Returns CLR property names (ordinal), so a caller can test
    // PropertyInfo.Name directly and two spellings of one member collapse to one entry.
    private static HashSet<string> CollectPresentBodyMemberClrNames(
        JsonElement body, Dictionary<string, PropertyInfo> byBodyName)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        // A non-object body names nothing. It cannot reach either caller in practice — the
        // deserializer rejects it first — but EnumerateObject() throws
        // InvalidOperationException for any other ValueKind, and an unhandled one of those is a
        // 500 (BUG 2 on the PATCH route, same shape).
        if (body.ValueKind != JsonValueKind.Object) return present;

        foreach (JsonProperty prop in body.EnumerateObject())
        {
            if (byBodyName.TryGetValue(prop.Name, out PropertyInfo? member))
                present.Add(member.Name);
        }

        return present;
    }

    // #506/#544: the same question asked of RAW UTF-8, for the two write paths with neither a
    // JsonDocument nor a JsonElement on their default branch (PUT and the nav-POST create route),
    // only the buffer #456 already made.
    //
    // The reader's JsonException is SWALLOWED so DeserializeAsync stays the sole author of the
    // malformed-body message (#389 L1 -- it appends "Path: $", JsonDocument does not). A body this
    // reader cannot finish is one the deserializer is about to reject.
    //
    // CurrentDepth == 1 is exactly "a member of the root object": the root StartObject is depth 0 and
    // its property names depth 1, so nested members and objects inside a root-level array are skipped
    // without a state machine.
    //
    // GetString() rather than ValueTextEquals: matching is case-INSENSITIVE whenever the binder's
    // PropertyNameCaseInsensitive is set, and ValueTextEquals is ordinal.
    //
    // #511: the reader comes from CreateBinderParityReader, so it accepts what the binder accepts. A
    // DEFAULT one made every configuration divergence a silent "this body names nothing" -- which is
    // the SAFE direction for #544's caller and the opposite for the strip. Parity makes both true.
    private static HashSet<string> CollectPresentBodyMemberClrNames(
        ReadOnlySpan<byte> utf8Json, Dictionary<string, PropertyInfo> byBodyName,
        JsonSerializerOptions? jsonOptions)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        Utf8JsonReader reader = CreateBinderParityReader(utf8Json, jsonOptions);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;
                if (byBodyName.TryGetValue(reader.GetString()!, out PropertyInfo? member))
                    present.Add(member.Name);
            }
        }
        catch (JsonException)
        {
            // Deliberately swallowed -- see the note above.
        }

        return present;
    }

    // #456: a rewindable copy so the '@odata.bind' scan and the deserializer can both read the body.
    // The deserializer keeps the SAME DeserializeAsync(Stream) overload, which is what holds every
    // malformed-body message byte-identical (#389 L1). GetBuffer() at the call sites, not ToArray().
    //
    // PipeReader/ReadOnlySequence was considered and does not win: it avoids a copy, not the
    // materialisation, and feeding it to the deserializer means a different overload. A pass-through
    // scanning stream WOULD keep streaming and is rejected on correctness -- it makes the answer
    // depend on where in the body the annotation sits, which is the per-verb divergence #456 removes.
    //
    // THE CAPACITY HINT IS CLAMPED, and that is the point of the line. Content-Length is a client
    // claim that arrives before any body byte: pre-sizing from it hands an unauthenticated caller a
    // remote allocation primitive. 81,920 is Stream.CopyTo's default buffer size AND under the
    // 85,000-byte LOH threshold, so an honest body lands in one right-sized allocation and a bogus
    // hint costs a collectable gen0 array.
    //
    // What bounds the copy is #474: EntitySetDefaults.MaxRequestBodyBytes now defaults to 30,000,000
    // (Kestrel's own number, so a default host sees no change). Before it, #203's filter only ran
    // when the metadata existed, which required a profile to have set the limit.
    private const int BufferedBodyCapacityHintCap = 81_920;

    private static async Task<MemoryStream> BufferRequestBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        int hint = ctx.Request.ContentLength is long declared && declared > 0
            ? (int)Math.Min(declared, BufferedBodyCapacityHintCap)
            : 0;
        var buffer = new MemoryStream(hint);
        await ctx.Request.Body.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    // The answer every route gives for '@odata.bind', so the four write routes wired in by #398
    // review MEDIUM-1 cannot drift from the collection POST's long-standing one. Deliberately does
    // NOT mention AllowDeepWrites: that flag decides whether a nested graph the client SENT reaches
    // the handler, and '@odata.bind' sends no graph — it names an entity to link. Enabling the flag
    // would not make this request work on any verb, so offering it here would be advice that does
    // not apply. The collection POST keeps its own richer message, which names the entity set and
    // mentions the flag for the adjacent case (the client meant to create the related entity
    // inline). #457 widened the flag to PUT/PATCH; it did not make it a remedy for this error.
    private static IResult ODataBindNotImplementedError() =>
        ODataError(501, "NotImplemented",
            "'@odata.bind' is not supported. Use the $ref endpoints to link an existing entity " +
            "(OData §11.4.2.2).");

    // #389: policing dynamic-property names on the way in. Gated on OpenTypesActive, not
    // OpenTypesEnabled (#389 L1) -- with the flag now defaulting to true, the EDM half is the whole
    // gate, and everyone else pays one bool test.
    //
    // Called from every route binding a body that can reach a bag. The action routes pass the
    // individual PARAMETER value and its declared type, never the {"paramName": value} envelope --
    // those keys are parameter names, not dynamic keys.
    //
    // #398 widened this from "reject a bad key" to "prepare the body": one walk answers both a
    // grammar rejection (400) and a key that must not become a dynamic property but is NOT a client
    // error -- control information, or a name Ignore() withholds -- which is re-emitted out of the
    // body silently. Dropping is not enough on its own, since STJ would bag either one, so it has to
    // be a real edit to the body the binder sees.
    //
    // A non-null returned JsonDocument MUST be disposed; PreparedWriteBody makes `using` at the call
    // site cover it. On the common path it is null and Body is the caller's own element. (Disposal is
    // right, but not for the reason once given: Parse over a ReadOnlyMemory wraps the caller's memory
    // and pools only the metadata database.)
    private readonly record struct PreparedWriteBody(
        IResult? Error, JsonElement Body, JsonDocument? Rewritten) : IDisposable
    {
        public void Dispose() => Rewritten?.Dispose();
    }

    private static PreparedWriteBody PrepareWriteBody(
        OhDataRegistration registration, JsonElement body, Type declaredType,
        JsonSerializerOptions? jsonOptions)
    {
        // #456: ABOVE the OpenTypesActive gate, and that placement is the whole fix. Added below it by
        // #398 review MEDIUM-1, so on any registration without an open complex type -- the majority --
        // PrepareWriteBody returned first and '@odata.bind' on PUT, PATCH, nav-POST or a property
        // write was accepted with 200/201 and silently discarded.
        //
        // Safe above the gate because it needs nothing the gate protects: a pure JsonElement walk over
        // an already-materialised body. What the gate really bought -- PUT and nav-POST streaming
        // instead of buffering -- is not obtained by skipping the check, since those two never call
        // PrepareWriteBody on the non-open path and now do their own buffered scan.
        //
        // It cannot move DOWN either: #398 stage 2 classifies any key containing '@' as control
        // information and STRIPS it, so a bind annotation reaching ScanWriteBody would be dropped
        // rather than reported.
        //
        // 501, not 400, and the same 501 the collection POST gives: deep insert by reference is
        // UNIMPLEMENTED on every verb. The old 400 was incidental -- it came from '@' failing the
        // odataIdentifier grammar, not from anything that knew what @odata.bind meant.
        if (ContainsODataBindAnnotation(body))
            return new PreparedWriteBody(ODataBindNotImplementedError(), body, null);

        if (!registration.OpenTypesActive || jsonOptions is null)
            return new PreparedWriteBody(null, body, null);

        OpenTypeJsonOptions.WriteBodyScan scan = OpenTypeJsonOptions.ScanWriteBody(
            body, declaredType, jsonOptions, registration.IgnoredJsonNamesByType);

        if (scan.InvalidKey is { } key)
            return new PreparedWriteBody(InvalidDynamicKeyError(key), body, null);

        if (!scan.CarriesUnbindableKeys) return new PreparedWriteBody(null, body, null);

        JsonDocument rewritten = OpenTypeJsonOptions.RewriteWithoutUnbindableKeys(
            body, declaredType, jsonOptions, registration.IgnoredJsonNamesByType);
        return new PreparedWriteBody(null, rewritten.RootElement, rewritten);
    }

    // Plain terms, not the ABNF's Unicode category codes -- this is read by an API consumer, not
    // by a spec implementer, and docs/open-types.md carries the formal grammar.
    //
    // The '@' clause is GONE from this message, and its absence is the point: since #398 stage 2 a
    // name containing '@' is classified as control information and skipped, so it can no longer
    // arrive here. Leaving the clause in would have documented a rejection that no longer happens.
    private static IResult InvalidDynamicKeyError(string key) =>
        ODataError(400, "InvalidBody",
            $"'{key}' is not a valid dynamic property name. A dynamic property of an OData open " +
            "type must be a simple identifier: it starts with a letter (in any script) or '_', " +
            "continues with letters, digits, combining marks or '_', and is at most 128 characters " +
            "long. '.', '-' and spaces are not allowed.",
            target: key);

    // Splits a comma-separated If-Match / If-None-Match list (RFC 7232 §3.1/§3.2, RFC 9110
    // §13.1.1/§13.1.2) into (value, isWeak) pairs, with the surrounding quotes removed.
    //
    // The "W/" sentinel is detected case-INSENSITIVELY even though RFC 9110 §8.8.3 spells it
    // %s"W/" (case-sensitive). Being lenient here is the fail-closed direction for both headers:
    // a lowercase w/"x" is classified WEAK, which can only ever cause an If-Match to be refused
    // and an If-None-Match to be honoured. Tightening this to Ordinal would instead let w/"x"
    // through as an ETag literally named `w/x`, which is the direction that silently mis-answers.
    private static IEnumerable<(string Value, bool IsWeak)> SplitETagList(string raw)
    {
        return raw.Split(',').Select(s =>
        {
            string t = s.Trim();
            bool isWeak = t.StartsWith("W/", StringComparison.OrdinalIgnoreCase);
            if (isWeak) t = t.Substring(2);
            return (t.Trim('"'), isWeak);
        });
    }

    // WEAK-comparison reader (RFC 9110 §8.8.3.2 "weak comparison"): the W/ prefix is stripped and
    // ignored, so W/"x" and "x" are equivalent. This is the correct function for If-None-Match --
    // both the conditional-GET 304 path and the write-path precondition -- per §13.1.2.
    private static IEnumerable<string> ParseETagList(string raw) =>
        SplitETagList(raw).Select(e => e.Value);

    // STRONG-comparison reader (RFC 9110 §8.8.3.2 "strong comparison"): a weak validator can never
    // participate in a strong comparison, so every weak entry is DROPPED rather than unwrapped.
    // §13.1.1 requires strong comparison for If-Match, which means `If-Match: W/"<current>"` must
    // evaluate false and answer 412 -- it used to be unwrapped here and answered 200 (#478). "*"
    // is never weak, so the wildcard survives this filter unchanged.
    //
    // This is a DELIBERATE DIVERGENCE from Microsoft.AspNetCore.OData, not a case of matching it.
    // Verified against the MS source at a05e1ad0: DefaultODataETagHandler.ParseETag
    // (Formatter/DefaultODataETagHandler.cs:67-95) reads EntityTagHeaderValue.Tag only and never
    // inspects IsWeak -- the sole `isweak` occurrence in the whole product tree is the
    // `isWeak: true` it passes when CONSTRUCTING one (:64), so MS both emits weak ETags
    // unconditionally and then compares them as if they were strong. That pairing is jointly
    // non-conformant with §13.1.1, and the standing "work with MS conventions" policy does not
    // extend to reproducing a non-conformance. It is safe here for a reason specific to OhData:
    // OhData never emits a weak ETag at all (ETagValueFormatter has no weak path), so a W/ entry
    // arriving in an If-Match was necessarily fabricated by the client or forwarded from some
    // other server, and refusing it costs no legitimate caller anything. Two tests that pinned
    // the old unwrapping behaviour were inverted with this change -- see
    // EndpointMappingTests.ETag_WeakPrefix_IsRejectedByIfMatch.
    private static IEnumerable<string> ParseStrongETagList(string raw) =>
        SplitETagList(raw).Where(e => !e.IsWeak).Select(e => e.Value);

    // #372: OData 4.0 spells this preference `odata.maxpagesize` (Protocol §8.2.8.5). The bare
    // `maxpagesize` is the 4.01 rename, and this service reports `OData-Version: 4.0` -- so echoing
    // the 4.01 token in Preference-Applied told a 4.0 client the server had applied a preference that
    // version does not define. Only the ECHO uses this constant: ParseMaxPageSize matches the bare
    // suffix, so a request carrying either spelling is still honoured.
    internal const string MaxPageSizePreference = "odata.maxpagesize";

    internal static int? ParseMaxPageSize(HttpContext ctx)
    {
        // Honour Prefer: maxpagesize=N (§8.2.8.3).
        if (!ctx.Request.Headers.TryGetValue("Prefer", out var prefer)) return null;
        const string prefix = "maxpagesize=";
        string val = prefer.ToString();
        int idx = val.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string num = val.Substring(idx + prefix.Length).Split(new[] { ',', ';' })[0].Trim();
        return int.TryParse(num, out int n) && n > 0 ? n : (int?)null;
    }

    // round() spec compliance (OData Part 2 §5.1.1.9 — round-half-away-from-zero).
    // Microsoft.OData's ApplyTo binder emits .NET's single-argument Math.Round(double)/
    // Math.Round(decimal), which default to banker's rounding (round-half-to-even) and deviate
    // from the spec on exact midpoints (2.5 -> 2, not 3). This visitor rewrites those call nodes
    // in the post-ApplyTo expression tree to the two-argument
    // Math.Round(value, MidpointRounding.AwayFromZero) overload. Only reaches the base-class
    // GetQueryable path (and its $count companion) where the factory owns the ApplyTo call — see
    // EntitySetProfile.RoundingMode's XML doc for why the Priority-1 ODataEntitySetProfile path
    // isn't covered.
    private static readonly MethodInfo s_mathRoundDouble =
        typeof(Math).GetMethod(nameof(Math.Round), new[] { typeof(double) })!;
    private static readonly MethodInfo s_mathRoundDecimal =
        typeof(Math).GetMethod(nameof(Math.Round), new[] { typeof(decimal) })!;
    private static readonly MethodInfo s_mathRoundDoubleAwayFromZero =
        typeof(Math).GetMethod(nameof(Math.Round), new[] { typeof(double), typeof(MidpointRounding) })!;
    private static readonly MethodInfo s_mathRoundDecimalAwayFromZero =
        typeof(Math).GetMethod(nameof(Math.Round), new[] { typeof(decimal), typeof(MidpointRounding) })!;

    private sealed class RoundAwayFromZeroVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == s_mathRoundDouble)
            {
                Expression arg = Visit(node.Arguments[0]);
                return Expression.Call(
                    s_mathRoundDoubleAwayFromZero, arg, Expression.Constant(MidpointRounding.AwayFromZero));
            }
            if (node.Method == s_mathRoundDecimal)
            {
                Expression arg = Visit(node.Arguments[0]);
                return Expression.Call(
                    s_mathRoundDecimalAwayFromZero, arg, Expression.Constant(MidpointRounding.AwayFromZero));
            }
            return base.VisitMethodCall(node);
        }
    }

    private static readonly RoundAwayFromZeroVisitor s_roundAwayFromZeroVisitor = new();

    /// <summary>
    /// Applies the round-half-away-from-zero rewrite to <paramref name="queryable"/> when
    /// <paramref name="mode"/> resolves to <see cref="RoundingMode.SpecCompliant"/>.
    /// A no-op (including for <see cref="RoundingMode.BankersRounding"/>) when the
    /// expression tree contains no single-argument <c>Math.Round</c> calls, so it is safe to call
    /// unconditionally on every collection query.
    /// </summary>
    private static IQueryable<TModel> ApplyRoundingMode<TModel>(IQueryable<TModel> queryable, RoundingMode mode)
    {
        if (mode == RoundingMode.BankersRounding) return queryable;
        Expression rewritten = s_roundAwayFromZeroVisitor.Visit(queryable.Expression);
        return ReferenceEquals(rewritten, queryable.Expression)
            ? queryable
            : queryable.Provider.CreateQuery<TModel>(rewritten);
    }

    // #241: reports whether the result order is already established by a top-level ordering operator,
    // so the stabilizing key order below never overrides a profile that pre-orders its own IQueryable.
    // Walks only the outer method-call spine (following the source argument) — an OrderBy buried inside
    // a $filter predicate or a nav-collection subquery lambda does not govern the result order, so it
    // must not suppress key injection (that would leave the LIMIT unordered — the very #241 bug).
    private static bool ResultOrderIsEstablished(Expression expression)
    {
        while (expression is MethodCallExpression call)
        {
            if ((call.Method.DeclaringType == typeof(Queryable) || call.Method.DeclaringType == typeof(Enumerable))
                && call.Method.Name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                return true;
            }
            // Descend the source (first argument) only — never into predicate/selector lambdas.
            expression = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
        }
        return false;
    }

    // #241: entity-key-ascending selector used to give server paging a deterministic total order.
    // Built fresh per use: this only assembles three expression nodes (never Expression.Compile),
    // which the LINQ provider then translates — EF's own query-plan cache dedupes the translation,
    // so a delegate cache here would buy nothing.
    private static Expression<Func<TModel, TKey>> BuildKeyOrderExpression<TModel, TKey>(string keyPropertyName)
    {
        ParameterExpression param = Expression.Parameter(typeof(TModel), "e");
        Expression body = Expression.Property(param, keyPropertyName);
        if (body.Type != typeof(TKey)) body = Expression.Convert(body, typeof(TKey));
        return Expression.Lambda<Func<TModel, TKey>>(body, param);
    }

    // #241: guarantees the deterministic total order server paging requires (OData §11.2.5.2).
    // - Client supplied $orderby: append the entity key as a final tiebreaker so paging is stable
    //   even when the client sorts on a non-unique column.
    // - No client $orderby and the result order is not already established: order by the entity key
    //   ascending, so the framework's LIMIT never rides an unordered scan (EF warning 10102).
    // - No client $orderby but the profile pre-orders its own queryable: left untouched — the
    //   profile's order stands, and we never silently override it.
    private static IQueryable<TModel> EnsureStableOrder<TModel, TKey>(
        IQueryable<TModel> filtered, bool clientOrdered, bool sourceAlreadyOrdered, string keyPropertyName)
    {
        if (!clientOrdered && sourceAlreadyOrdered)
            return filtered;
        Expression<Func<TModel, TKey>> keyOrder = BuildKeyOrderExpression<TModel, TKey>(keyPropertyName);
        if (clientOrdered)
            return filtered is IOrderedQueryable<TModel> ordered ? ordered.ThenBy(keyOrder) : filtered;
        return filtered.OrderBy(keyOrder);
    }

    public static RouteGroupBuilder MapAll(IEndpointRouteBuilder routes, OhDataRegistration registration)
    {
        string prefix = registration.Prefix;
        var group = routes.MapGroup(prefix);
        // Resolve the host's JsonOptions once at startup so handlers don't pay a DI lookup per
        // request. Any custom converters/encoder the host registered are honoured; only the
        // property-naming policy is OhData-owned (see below).
        var hostJsonOptions = routes.ServiceProvider
            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            ?.Value?.SerializerOptions;

        // #252: OhData owns its response casing. Derive a registration-scoped options instance from
        // the host's (preserving its converters/encoder) but force PropertyNamingPolicy to OhData's
        // own setting — null (PascalCase) by default so payloads match $metadata (OData §4.4),
        // JsonNamingPolicy.CamelCase when the profile opts in via WithJsonPropertyNamingPolicy.
        // The host's camelCase HttpJsonOptions default is deliberately NOT inherited: since
        // camelCase is ASP.NET Core's own default it cannot be distinguished from an explicit
        // choice, so OhData's setting is the single source of truth.
        var startupJsonOptions = new JsonSerializerOptions(hostJsonOptions ?? _pascalCaseSerializerOptions)
        {
            PropertyNamingPolicy = registration.JsonPropertyNamingPolicy,
        };

        // #226: registration-wide ignored-property suppression. Validates same-model-type
        // conflicts, then — only when at least one profile declares ignores — derives a single
        // options instance whose resolver modifier removes the ignored members. When no profile
        // ignores anything the owned options are threaded through unchanged.
        var ignoredByType = IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap(registration.Profiles);

        // #458: same hazard shape as the line above, for the model-bound allowlists. Two profiles
        // over one CLR model type write the same per-TYPE ModelBoundQuerySettings, so divergent
        // FilterProperties/OrderByProperties/SelectProperties/ExpandProperties declarations union
        // and each entity set silently accepts what the other allows. Refused here rather than at
        // request time -- see ModelBoundAllowlists for why per-entity-set settings do not exist.
        ModelBoundAllowlists.Validate(registration.Profiles);

        // #398 stage 1: capture the withheld members' JSON names BEFORE the modifier below removes
        // them from their contracts. Afterwards the JSON name is not recoverable — which is exactly
        // why an open type's extension data can capture a withheld member and echo it back under the
        // withheld name. Read off the real pre-ignore contract rather than re-derived from the naming
        // policy; see BuildIgnoredJsonNameMap. Empty in, empty out, so a registration that ignores
        // nothing allocates nothing and resolves no JsonTypeInfo here.
        registration.IgnoredJsonNamesByType =
            IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap(ignoredByType, startupJsonOptions);

        // #462: the CLR-name map crosses into Build as an InheritedNameSets too, for the same reason
        // the JSON-name map does — the modifier it installs resolves the RUNTIME type's contract.
        // Its sets are ordinal (CLR member names, matched against PropertyInfo.Name), which is the
        // comparer a multi-level union must use; the withheld JSON-name map above carries the
        // BINDER's comparer instead, and the two must never be merged (see WithheldNameComparer).
        var ignoredClrNames = new InheritedNameSets(ignoredByType, StringComparer.Ordinal);
        JsonSerializerOptions effectiveJsonOptions =
            IgnoredPropertyJsonOptions.Build(startupJsonOptions, ignoredClrNames);

        // Resolved once here (rather than down at the per-profile loop) so the group-level
        // exception filter below can log through the same "OhData" category every other
        // handler uses.
        var loggerFactory = routes.ServiceProvider.GetService<ILoggerFactory>();
        var groupLogger = loggerFactory?.CreateLogger("OhData");

        // #389: open COMPLEX types, ON BY DEFAULT; WithOpenTypes(false) is the escape hatch. Marks the
        // DynamicPropertyDictionaryAnnotation's member as STJ extension data, so the bag serialises and
        // binds FLAT with no attribute on the consumer's model.
        //
        // Default-on because a complex type with a dictionary member IS an open type -- this builder
        // has always emitted OpenType="true" for it -- and because MS's AppendDynamicProperties reads
        // the SAME annotation with no opt-in flag. Flattening RE-BINDS a body an adopter already
        // sends, and the echo is byte-identical to the correct one, so WarnWireShapeIsFlat is the only
        // available signal before the stored data is wrong.
        //
        // ORDERING IS AN INVARIANT -- after the ignored-property modifier, before nav suppression --
        // and OpenTypeModifierOrderingTests asserts it. (1) This modifier snapshots its declared-name
        // collision set from typeInfo.Properties, so it must run while EDM navigations are still on
        // the contract; deriving nav suppression from startupJsonOptions would put the removal first
        // and turn a bag key shadowing a navigation from a hard 500 into a leak. (2) It must run AFTER
        // the ignored-property modifier, whose removals are what make Ignore()d names invisible to it
        // -- which is why those names are threaded in separately as DATA rather than read off the
        // contract.
        var openTypeContainers = registration.OpenTypesEnabled
            ? OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(registration.EdmModel)
            : OpenTypeJsonOptions.OpenComplexTypeContainers.Empty;
        effectiveJsonOptions = OpenTypeJsonOptions.Build(
            effectiveJsonOptions, openTypeContainers, registration.IgnoredJsonNamesByType);
        OpenTypeJsonOptions.ValidateOrThrow(effectiveJsonOptions, openTypeContainers);

        // Named after ValidateOrThrow so a registration that is about to fail startup does not first
        // emit migration advice for a contract it will never serve. Silent when the model has no open
        // complex type, which is what keeps an unaffected app's log untouched.
        OpenTypeJsonOptions.WarnWireShapeIsFlat(openTypeContainers, groupLogger);

        // #482: map every EDM entity type to its CLR type NOW, on the very options instance every
        // route closure below is handed, so the nav-suppression resolver modifier has the whole
        // schema before the first request rather than after whichever request happens to arrive
        // first. Without this the seeding still happens (GetNavSuppressedOptions does it defensively)
        // but it happens on a request thread, and the defect this closes is exactly a
        // whichever-thread-got-there-first defect. Must be the LAST thing done to
        // effectiveJsonOptions' nav-suppression state and must come after the line above that
        // finalises effectiveJsonOptions itself — it keys off that instance. Fills a dictionary only:
        // no JsonTypeInfo is resolved and no modifier is added, so the ignore -> open-type ->
        // nav-suppression ordering invariant is untouched.
        ExpandEngine.PrimeNavSuppression(effectiveJsonOptions, registration.EdmModel);

        // #389 L1: every per-request open-type path gates on this, NOT on OpenTypesEnabled. The flag
        // says what the consumer asked for; this says whether the model gave it anything to do. Now
        // that the flag defaults to TRUE this is the ONLY thing keeping a model with no dictionary
        // member byte-identical to a pre-#389 build -- gating the write paths on the flag alone made
        // that false even when it was opt-in. See the remarks on OhDataRegistration.OpenTypesActive
        // for the measured difference.
        registration.OpenTypesActive = !openTypeContainers.IsEmpty;

        // #200: observability. The outermost group filter opens an ActivitySource span per OData
        // request and records the request-duration histogram + active-request up/down counter (both
        // on the "OhData" Meter). Added first so it wraps every other filter and the handler; the
        // final HTTP status is read via Response.OnCompleted (an endpoint filter cannot see it after
        // next() because the IResult executes later). Near-free when no OTel listener is attached:
        // StartActivity returns null and the instruments no-op.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            HttpContext http = ctx.HttpContext;

            // Gap 1 / #496 finding 2: OData-Version: 4.0 on EVERY response (§8.1.5, §8.2.6),
            // including one an inner filter short-circuits. It used to be set by the
            // $format/Accept filter, which is registered FOURTH -- so the #203 body-limit filter's
            // Content-Length fast-reject (registered third) returned its 413 without ever reaching
            // it, and that response shipped with no OData-Version at all (measured). A response
            // header that must be universal has to be written by the outermost filter, which is
            // this one; there is nothing above it that can short-circuit. Its placement here is
            // therefore load-bearing, and Issue496ErrorHandlingTests pins it from the outside by
            // asserting the header on the fast-reject 413.
            http.Response.Headers["OData-Version"] = "4.0";

            (string? entitySet, string? route, string operation) = DescribeOhDataEndpoint(http);

            Activity? activity = OhDataDiagnostics.ActivitySource.StartActivity(
                $"{http.Request.Method} {route ?? http.Request.Path.ToString()}", ActivityKind.Server);
            if (activity is not null)
            {
                if (entitySet is not null) activity.SetTag("odata.entity_set", entitySet);
                if (route is not null) activity.SetTag("http.route", route);
                activity.SetTag("odata.operation", operation);
                activity.SetTag("http.request.method", http.Request.Method);
            }

            long startTs = Stopwatch.GetTimestamp();
            var activeTags = new TagList { { "odata.entity_set", entitySet }, { "odata.operation", operation } };
            OhDataDiagnostics.ActiveRequests.Add(1, activeTags);

            http.Response.OnCompleted(() =>
            {
                int status = http.Response.StatusCode;
                double seconds = Stopwatch.GetElapsedTime(startTs).TotalSeconds;
                OhDataDiagnostics.RequestDuration.Record(seconds, new TagList
                {
                    { "odata.entity_set", entitySet },
                    { "odata.operation", operation },
                    { "http.response.status_code", status },
                });
                OhDataDiagnostics.ActiveRequests.Add(-1, activeTags);
                if (activity is not null)
                {
                    activity.SetTag("http.response.status_code", status);
                    if (status >= 500) activity.SetStatus(ActivityStatusCode.Error);
                    activity.Dispose();
                }
                return Task.CompletedTask;
            });

            return await next(ctx);
        });

        // S7: the last-resort net. A handler that THROWS (as opposed to returning an ODataError,
        // which every deliberate error path here does) used to escape as an empty, envelope-less 500
        // with no logging -- the most common production failure there is. Converts it to the same
        // OData envelope with a GENERIC message, never ex.Message or the stack (which could leak
        // connection strings, type names, paths), and logs the real exception.
        //
        // Covers every route handler and every filter registered AFTER it. #496 finding 3: it is NOT
        // the outermost group filter, though this comment used to say so -- the #200 observability
        // filter is added first and wraps it, so an exception in that filter's own body or its
        // Response.OnCompleted callback escapes this envelope. Accepted, not overlooked: reordering
        // would move the LogError outside the request's Activity and lose trace correlation on the
        // single most important log line the framework emits.
        //
        group.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            // #203: Kestrel throws BadHttpRequestException (StatusCode 413) when a body without a
            // usable Content-Length (e.g. chunked) exceeds the per-request MaxRequestBodySize set by
            // the write-body-size filter below. Map it to the OData 413 envelope instead of a 500.
            catch (BadHttpRequestException bhre) when (bhre.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                CloseConnectionAfterUnreadBody(ctx.HttpContext);
                return ODataError(413, "RequestEntityTooLarge",
                    "The request body exceeds the maximum allowed size.");
            }
            // #581: a rejection a ConfigureExceptions mapping resolved at the seam. Warning rather
            // than Error -- it is a client error, not a server fault -- but logged WITH the original
            // exception, because turning a fault into a 4xx removes it from error dashboards and
            // this line is the only thing left of it.
            catch (OhDataRejectionException rejection)
            {
                if (rejection.InnerException is { } mappedFrom)
                {
                    groupLogger?.LogWarning(mappedFrom,
                        "OhData: mapped {Exception} to {Status} processing {Method} {Path}",
                        mappedFrom.GetType().Name,
                        rejection.Result.StatusCode,
                        SanitizeLogValue(ctx.HttpContext.Request.Method),
                        SanitizeLogValue(ctx.HttpContext.Request.Path.ToString()));
                }
                else
                {
                    // Returned, not mapped: an ordinary outcome the handler chose, so Debug. A
                    // Warning per business rejection would drown the signal the branch above carries.
                    groupLogger?.LogDebug(
                        "OhData: handler returned {Status} processing {Method} {Path}",
                        rejection.Result.StatusCode,
                        SanitizeLogValue(ctx.HttpContext.Request.Method),
                        SanitizeLogValue(ctx.HttpContext.Request.Path.ToString()));
                }
                return ODataError(
                    rejection.Result.StatusCode, rejection.Result.ErrorCode,
                    rejection.Result.Message, rejection.Result.Target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !ctx.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // #496 finding 4: unwrap the HandlerFaultException marker so the operator sees the
                // exception the profile actually threw, not the envelope that kept a read route's
                // narrow catch from misclassifying it as a client error.
                groupLogger?.LogError(HandlerFaultException.Unwrap(ex),
                    "OhData: unhandled exception processing {Method} {Path}",
                    SanitizeLogValue(ctx.HttpContext.Request.Method),
                    SanitizeLogValue(ctx.HttpContext.Request.Path.ToString()));
                return ODataError(500, "InternalServerError",
                    "An unexpected error occurred while processing the request.");
            }
        });

        // #203: enforce the per-entity-set write-body-size limit (attached as OhDataBodyLimitMetadata
        // in MapEntitySet). Runs only for body-bearing write methods (POST/PUT/PATCH). Sets Kestrel's
        // per-request MaxRequestBodySize — which bounds a chunked/no-Content-Length body during read
        // (a resulting BadHttpRequestException is mapped to 413 by the filter above) — and
        // fast-rejects an oversized Content-Length before the handler reads the body. Sits inside the
        // exception filter above so its 413 mapping covers the streamed-body case.
        // #474: the fallback for a route that carries no per-entity-set metadata — i.e. an UNBOUND
        // action, which belongs to no profile and so had no limit to resolve. Every entity-set route
        // still carries its own metadata and that still wins; this only fills the gap.
        long? registrationBodyLimit = registration.DefaultMaxRequestBodyBytes;
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (IsBodyBearingWriteMethod(http.Request.Method)
                && (http.GetEndpoint()?.Metadata.GetMetadata<OhDataBodyLimitMetadata>()?.MaxBytes
                    ?? registrationBodyLimit) is long limit)
            {
                IHttpMaxRequestBodySizeFeature? sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (sizeFeature is { IsReadOnly: false })
                {
                    // #474: a limit the FRAMEWORK chose may only lower the host's own ceiling, never
                    // raise it. #203 assigns this unconditionally, which was right while the limit
                    // could only come from the adopter — "this set accepts up to 4 MB" is a
                    // deliberate per-route override and still behaves that way. But now that
                    // EntitySetDefaults.MaxRequestBodyBytes defaults to 30,000,000, an unconditional
                    // assignment would RAISE the ceiling on a host that had deliberately lowered
                    // Kestrel's below it — a security fix loosening a hardening step, on a
                    // registration that configured nothing.
                    //
                    // "The framework chose it" is read as "the resolved value IS the framework's
                    // constant" rather than tracked through a separate configured/not-configured
                    // flag. The only case that misreads is an adopter who explicitly sets exactly
                    // 30,000,000 on a host with a lower limit and wants it raised, and clamping is
                    // the safe direction there. A null host limit (the host disabled it) is where
                    // #474 has the most to do, and the assignment still happens.
                    sizeFeature.MaxRequestBodySize =
                        limit == EntitySetDefaults.DefaultMaxRequestBodyBytes
                        && sizeFeature.MaxRequestBodySize is long hostLimit
                            ? Math.Min(hostLimit, limit)
                            : limit;
                }

                if (http.Request.ContentLength is long len && len > limit)
                {
                    CloseConnectionAfterUnreadBody(http);
                    return ODataError(413, "RequestEntityTooLarge",
                        $"The request body ({len} bytes) exceeds the maximum allowed size ({limit} bytes).");
                }
            }
            return await next(ctx);
        });

        // Gap 1: Add OData-Version: 4.0 header to all responses (§8.2.6).
        // Batch 4: Return 406 Not Acceptable when the client cannot accept application/json (§8.2.3).
        // Batch 5: Validate $format query option (§11.2.10); it overrides the Accept header.
        // $metadata returns application/xml, so it is exempted from the JSON-only checks.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            // #496 finding 2: the OData-Version header used to be set HERE, and this filter is
            // fourth of five -- the two filters registered between it and the outermost one can
            // both short-circuit. It is set in the outermost filter now; see the note there.
            string path = ctx.HttpContext.Request.Path.Value ?? "";
            bool isMetadata = path.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase);

            // #580: /$count negotiates NOTHING. §11.2.9 is explicit on both halves -- the body "MUST
            // ... [be] a simple scalar integer value with media type text/plain", and "Content
            // negotiation using the Accept request header or the $format system query option is not
            // allowed with the path segment /$count". A segment that may not be negotiated has no
            // business 406-ing a client that tried: the answer is text/plain either way.
            //
            // This REVERSES a ruling recorded in CLAUDE.md -- "Accept: application/xml still 406s,
            // unchanged ... §11.2.9 forbids the CLIENT to negotiate, not the server to decline a
            // media type the client refused (RFC 9110 §12.5.1)". That reading is defensible in
            // isolation but was not checked against Microsoft.AspNetCore.OData, which settles it the
            // other way: ODataCountMediaTypeMapping matches every /$count path at quality 1, and
            // ODataOutputFormatter.CanWriteResult then OVERRIDES the content type ("If a media
            // mapping was found, use that and override the value specified by the controller"). MS
            // never negotiates here and never 406s. §11.2.9 is the specific rule over minimal item
            // 5.1's general "conform to Accept or fail", and this is the direction that un-breaks a
            // working client rather than breaking one.
            bool isCount = path.EndsWith("/$count", StringComparison.OrdinalIgnoreCase);

            if (!isMetadata && !isCount)
            {
                // §11.2.10: $format overrides Accept. Only application/json (and the shorthand
                // "json") are supported; any other value is rejected with 400.
                bool formatAccepted = false;
                if (ctx.HttpContext.Request.Query.TryGetValue("$format", out var formatParam))
                {
                    string fmt = Uri.UnescapeDataString(formatParam.ToString()).Trim();
                    bool isJsonFormat =
                        string.Equals(fmt, "json", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fmt, "application/json", StringComparison.OrdinalIgnoreCase);
                    if (!isJsonFormat)
                    {
                        return ODataError(400, "UnsupportedFormat",
                            $"The requested format '{fmt}' is not supported. " +
                            "Only application/json (or the shorthand 'json') is produced.");
                    }

                    formatAccepted = true;
                }

                if (!formatAccepted)
                {
                    // §8.2.1 / RFC 7231 §5.3.2 (issue #182): reject Accept headers that don't include
                    // a media range this route can satisfy. Most routes produce application/json, but
                    // the raw-value routes are exceptions (like $metadata's application/xml above):
                    // /$count returns the count as text/plain (§11.2.9), and /{property}/$value
                    // returns the raw value as text/plain for scalars or application/octet-stream for
                    // byte[] (§11.2.3.1), so those segments can satisfy the corresponding types too.
                    // A client (e.g. Swagger UI, reading the content types those routes advertise in
                    // the OpenAPI document) that asks for text/plain on /$count is making a valid
                    // request and must not get a 406. Negotiation goes through AcceptHeaderPermits,
                    // which parses real media ranges and honors q-values rather than substring-scanning
                    // the header — so "application/*" and "text/*" match the way RFC 7231 requires, and
                    // "application/json;q=0" (meaning "not acceptable") correctly 406s.
                    string accept = ctx.HttpContext.Request.Headers.Accept.ToString();
                    if (!string.IsNullOrEmpty(accept))
                    {
                        bool isValue = path.EndsWith("/$value", StringComparison.OrdinalIgnoreCase);

                        // Producible sets are unchanged from the substring version — only the matching
                        // rule changed. $value produces JSON, text/plain, or octet-stream; $count
                        // produces JSON or text/plain; every other route produces JSON.
                        string[] producible = isValue
                            ? new[] { "application/json", "text/plain", "application/octet-stream" }
                            : new[] { "application/json" };

                        if (!AcceptHeaderPermits(accept, producible))
                        {
                            string producibleList = isValue
                                ? "application/json, text/plain, or application/octet-stream"
                                : "application/json";
                            return ODataError(406, "NotAcceptable",
                                $"The server can only produce {producibleList} responses for this resource. " +
                                "Set a matching Accept header or omit it.");
                        }
                    }
                }
            }

            return await next(ctx);
        });

        // #5: Honor the OData-MaxVersion request header or reject the request (§8.2.7).
        // Applies to every route under this group -- service document, $metadata, and all
        // entity-set/bound-operation routes -- since a client capping its acceptable response
        // version below what this service emits (4.0) cannot be honored anywhere in the surface.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            IResult? error = ODataMaxVersionFilter.Validate(ctx.HttpContext);
            if (error is not null) return error;
            return await next(ctx);
        });

        // #468: validate the EDM before anything is generated from it. Both generators below --
        // the CSDL writer and the service document -- read this model, and CsdlWriter.TryWriteCsdl
        // does NOT run these rules (it reports only serialization errors), so without this pass an
        // invalid construct is written out verbatim and only fails at the consumer.
        ValidateEdmModelOrThrow(registration.EdmModel, prefix);

        // Pre-compute static responses that are determined at startup.
        string metadataXml = BuildMetadataXml(registration.EdmModel);

        // #468: the service document is built from the SAME EDM container $metadata is written
        // from, not from registration.Profiles. Two generators over one model is what let
        // $metadata assert IncludeInServiceDocument="true" for every unbound function while the
        // hand-rolled document listed entity sets and nothing else -- an advertise-vs-serve
        // divergence that could only ever grow. Reading the container makes the two agree by
        // construction: an operation import appears here exactly when its own flag says it
        // should, and the flag is set in OhDataBuilder (parameterless only -- CSDL 4.0 section
        // 13.6). Entity sets keep coming out in profile-registration order, since that is the
        // order they were added to the builder in.
        var serviceDocEntitySets = registration.EdmModel.EntityContainer is null
            ? Array.Empty<ServiceDocumentEntry>()
            : registration.EdmModel.EntityContainer.Elements
                .Select(ServiceDocumentEntryFor)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToArray();

        // Service document -- lists available entity sets
        group.MapGet("", (HttpContext ctx) =>
        {
            string baseUrl = BuildBaseUrl(ctx, prefix);
            // #495: rendered here with OhData's own options, not deferred to Results.Ok and the
            // HOST's HttpJsonOptions. `@odata.context`/`value` are contractual dictionary keys and
            // a host DictionaryKeyPolicy rewrote both; the entries themselves are framework-
            // generated strings, so nothing here is payload a host converter should be shaping.
            return PreRenderedJson(new Dictionary<string, object>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata",
                ["value"] = serviceDocEntitySets
            }, _frameworkEnvelopeSerializerOptions);
        }).ExcludeFromDescription();

        // $metadata -- CSDL XML describing the EDM model
        group.MapGet("/$metadata", () => Results.Content(metadataXml, "application/xml; charset=utf-8"))
            .ExcludeFromDescription();

        // One set of CRUD routes per registered profile
        foreach (var profile in registration.Profiles)
        {
            try
            {
                _mapEntitySetMethod
                    .MakeGenericMethod(profile.KeyType, profile.ModelType)
                    .Invoke(null, new object?[] { group, profile, registration, loggerFactory, effectiveJsonOptions });
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                // Unwrap reflection wrapper so callers see the real exception (e.g. InvalidOperationException
                // from startup validation) rather than a TargetInvocationException.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
        }

        // #487: computed once, consulted by every diagnostic below and by the unbound-operation
        // auth application. A registration that requires authorization NOWHERE is simply a public
        // service and has no hole to report.
        bool registrationRequiresAuth = RegistrationRequiresAuthorizationSomewhere(registration);

        // Gap 7: Unbound functions/actions — registered once at service root level (§11.5.1)
        MapUnboundOperations(
            group, registration.UnboundOperations, effectiveJsonOptions, registration,
            registrationRequiresAuth);

        // #313: named LAST, after every route has mapped, so a registration whose startup validation
        // is about to throw does not first emit advice about a surface it will never serve. Same
        // rationale as WarnWireShapeIsFlat above, which sits after ValidateOrThrow for the same reason.
        WarnUnboundedBareExpand(registration, groupLogger);
        // #440: same placement rationale — after every route has mapped, so a registration whose
        // startup validation is about to throw does not first emit advice about a surface it will
        // never serve.
        WarnUndeclaredConventionNavigations(registration, groupLogger);
        // #489: same placement rationale again — after every route has mapped.
        WarnIgnoredPropertiesStillInEdm(registration, groupLogger);
        // #487: registered last, and unlike its three neighbours it does not warn HERE — it
        // installs a Finally convention that warns at endpoint-build time, once the host has had
        // its chance to apply group-level authorization to the group this method returns. See
        // AttachAnonymousRouteAudit.
        AttachAnonymousRouteAudit(group, groupLogger);

        // #481/#368: same placement rationale again. It runs AFTER
        // WarnUndeclaredConventionNavigations, which reports the complementary case, so reading them
        // in that order is how a log tells you which one a navigation fell into.
        //
        // #549: they do NOT partition the EDM's navigations, and this comment used to say they did.
        // WarnUndeclaredConventionNavigations is gated on ExpandEnabled and this one is not, so on
        // an expand-disabled profile neither covers an undeclared navigation. Harmless in effect --
        // per #440/#446 such a navigation is served by nothing at all, so there is no exposure to
        // report -- but the gate asymmetry is deliberate on both sides and the claim was not true.
        WarnNavigationTargetAuthorization(registration, groupLogger);
        // #378: same placement rationale as its neighbours -- after every route has mapped.
        WarnShadowedCollectionHandlers(registration, groupLogger);
        return group;
    }

    // #489: Ignore() loses its EDM half under AdvancedConfigure, and the consequence is a value
    // oracle no reader derives from either half alone. The EDM removal rides the _configurators
    // pipeline; the override returns from VisitModelBuilder before that pipeline runs, while runtime
    // suppression still applies. That is the stated contract of the hatch and is deliberately not
    // "fixed": re-imposing Ignore() would defeat it, and singling out that configurator would be
    // arbitrary when HasOptional/HasRequired/HasMany ride the same pipeline and stay ejected.
    //
    // What was missing is the SIGNAL. With both in play the property is back in $metadata and
    // query-addressable while the wire omits it, so $filter over it answers truthfully one predicate
    // at a time. Without the hatch the EDM removal makes it indistinguishable from a property that
    // never existed, so the 400 cannot confirm existence.
    //
    // GATED ON THE EDM AS BUILT, not on the presence of the override: re-applying
    // `configuration.EntityType.Ignore(...)` by hand is exactly what the docs prescribe, and warning
    // on the correct configuration teaches developers to tune the warning out.
    //
    // The query-capability half is deliberately not in the gate -- $metadata discloses the name and
    // type regardless, and a capability added later must not silently un-warn.
    private static void WarnIgnoredPropertiesStillInEdm(OhDataRegistration registration, ILogger? logger)
    {
        if (logger is null) return;

        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            if (!profile.IsAdvancedConfigureOverridden || profile.IgnoredPropertyNames.Count == 0)
            {
                continue;
            }

            IEdmEntityType? entityType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (entityType is null) continue;

            foreach (string clrName in profile.IgnoredPropertyNames)
            {
                // IgnoredPropertyNames holds CLR names; the EDM advertises the resolved EDM name,
                // which a [JsonPropertyName] rename makes different. Resolve through the same single
                // source of truth every other CLR->EDM name question in this file goes through.
                PropertyInfo? clrProperty =
                    ODataPropertyNaming.FindClrPropertyByEdmName(profile.ModelType, clrName);
                string edmName = clrProperty is not null
                    ? ODataPropertyNaming.ResolveEdmName(clrProperty)
                    : clrName;

                if (!entityType.Properties().Any(
                        p => string.Equals(p.Name, edmName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // the override re-applied the EDM removal by hand -- nothing to say
                }

                // Each placeholder appears EXACTLY once — Microsoft.Extensions.Logging binds a
                // template positionally, so a repeated one would consume an argument that is not
                // there. Repeated VALUES are passed again under a distinct name.
                logger.LogWarning(
                    "OhData: '{EntitySet}' calls Ignore() for '{Property}', but '{Property2}' is still " +
                    "declared in the EDM because this profile overrides AdvancedConfigure — which ejects " +
                    "every automatic EDM configuration step, Ignore()'s EDM removal among them. Runtime " +
                    "suppression still applies, so the property is omitted from every response body, has " +
                    "no property routes, and is never bound from a write body. But $metadata advertises " +
                    "its name and type, and it stays addressable in $filter/$orderby/$select wherever " +
                    "this override re-enabled those capabilities. A withheld-but-addressable property is " +
                    "a VALUE ORACLE: the value is never served, yet '?$filter={Property3} eq …' answers " +
                    "truthfully, so it can be probed one predicate at a time. If '{Property4}' is hidden " +
                    "for tidiness this may be fine; if it is hidden for SECURITY, re-apply the removal " +
                    "inside the override — configuration.EntityType.Ignore(x => x.{Property5}) — or drop " +
                    "the AdvancedConfigure override. See docs/ignoring-properties.md.",
                    profile.EntitySetName, clrName, edmName, edmName, clrName, clrName);
            }
        }
    }

    // #313: the startup diagnostic standing in for the ceiling MaxExpandTop no longer defaults to.
    // A bare ?$expand=Children materializes the whole child collection and answers 200, so nothing
    // else points at it. Deliberately prescribes no number -- picking one is the mistake stage 1 undid.
    //
    // The conditions are ALL of the conditions under which the exposure is live, which is what keeps
    // it from being noise: ExpandEnabled (false by default, and the load-bearing one),
    // HasGetQueryable, ExpandPushdownEnabled (with it off no EngagedExpand is built, so there is no
    // materialization to bound), collection-valued, ServeRaw, and MaxExpandTop null.
    //
    // ServeRaw is resolved through the SAME ResolveNavTreatment and the SAME candidate set the route
    // registration uses, so the warning and the routes cannot drift. #415/#421: that set is
    // `new[] { profile }`, not the sibling union -- under Model B the URL-named set has authority
    // over its own navigations, so a union-based check went SILENT for exactly the profile that
    // needed the warning whenever any sibling delegated the nav.
    private static void WarnUnboundedBareExpand(OhDataRegistration registration, ILogger? logger)
    {
        if (logger is null) return;

        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            if (!profile.ExpandEnabled || !profile.HasGetQueryable ||
                !profile.ExpandPushdownEnabled || profile.MaxExpandTop is not null)
            {
                continue;
            }

            IEdmEntityType? entityType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (entityType is null) continue;

            // #421: `new[] { profile }` — the URL-named set ALONE, byte-for-byte the array
            // ApplyCollectionPipelineAsync passes as the root level's `levelSources`. This diagnostic
            // describes what `GET /{Set}?$expand={Nav}` materializes, so it must be resolved from the
            // same candidate set that request resolves from. See the ServeRaw clause above.
            IReadOnlyList<IEntitySetEndpointSource> candidates = new[] { profile };

            foreach (IEdmNavigationProperty nav in entityType.NavigationProperties())
            {
                if (nav.TargetMultiplicity() != EdmMultiplicity.Many) continue;
                if (ExpandEngine.ResolveNavTreatment(nav.Name, candidates).Treatment != ExpandEngine.NavTreatment.ServeRaw) continue;

                // The two knobs are named in the order they must be set: MaxExpandTop FIRST and alone
                // is a complete answer (over-ceiling 400s), and ExpandPagingEnabled is inert without
                // it. The message still prescribes no NUMBER — that is the mistake stage 1 undid.
                //
                // Each placeholder appears EXACTLY once: Microsoft.Extensions.Logging binds a template
                // positionally, so a repeated one would consume an argument that is not there.
                logger.LogWarning(
                    "OhData: '{EntitySet}' allows $expand and its navigation '{Navigation}' is a " +
                    "delegate-less collection served straight from GetQueryable, so '?$expand={Nav}' " +
                    "materializes the ENTIRE related collection for every row of the page — with no " +
                    "ceiling, because MaxExpandTop resolves to null. OhData does not guess a limit: it " +
                    "cannot know how large this collection gets, and only you can. Set MaxExpandTop to " +
                    "bound it; an over-ceiling $expand is then rejected with 400. If your clients follow " +
                    "nested continuation links, also set ExpandPagingEnabled to serve the first " +
                    "MaxExpandTop children plus a 'Nav@odata.nextLink' instead of that 400 — it is inert " +
                    "on its own, and a link is worse than a 400 for a client that ignores it. Leaving " +
                    "both unset is a valid choice for a collection you know is small — this warning " +
                    "informs that choice, it does not make it.",
                    profile.EntitySetName, nav.Name, nav.Name);
            }
        }
    }

    // #440: the profile's navigation set and the EDM's disagree -- the ordinary
    // `public Publisher? Publisher { get; set; }` with no declaration. A WARNING, not a throw:
    // throwing would break startup for every adopter who has one, with no migration.
    //
    // It reports the disagreement, not a defect: $metadata advertises a navigation this set will
    // never serve and never accept, so a generated client asks for data it cannot receive.
    //
    // KEEP THE MESSAGE AND THE BEHAVIOUR IN STEP. Every time a consequence is closed, the sentence
    // naming it comes out in the same commit -- already gone: pushdown disqualification (#322),
    // the structural-property routes (#440 symptom 2), and "answers 200 with null", which is now
    // "omitted". A test asserts the message never says "pushdown", "$filter" or "Include".
    //
    // Gated on ExpandEnabled: the remaining consequence has to be reachable, and that flag is false
    // by default.
    // #378: the collection GET dispatches GetODataQueryable > GetQueryable > GetAll through an
    // unguarded else-if chain, so a lower handler set alongside a higher one is DEAD -- measured,
    // invoked zero times on the collection GET, on /$count, and on /$count's $filter fallback --
    // and nothing said so at any log level. EntitySetProfile documents the precedence in an XML
    // doc comment, which the developer reading their own dead handler has no reason to consult.
    //
    // A warning rather than a throw: the precedence is documented and long-standing, so an app in
    // this state is working as specified, merely carrying configuration that does nothing.
    private static void WarnShadowedCollectionHandlers(OhDataRegistration registration, ILogger? logger)
    {
        if (logger is null) return;

        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            bool priority1 = profile is IODataEntitySetEndpointSource ods && ods.HasGetODataQueryable;
            string winner = priority1 ? "GetODataQueryable"
                : profile.HasGetQueryable ? "GetQueryable"
                : null!;
            if (winner is null) continue; // GetAll alone, or no collection handler -- nothing shadowed.

            var shadowed = new List<string>();
            if (priority1 && profile.HasGetQueryable) shadowed.Add("GetQueryable");
            if (profile.HasGetAll) shadowed.Add("GetAll");
            if (shadowed.Count == 0) continue;

            logger.LogWarning(
                "OhData: '{EntitySet}' sets {Shadowed} as well as {Winner}, and {Winner2} takes " +
                "precedence -- so {Shadowed2} is never invoked, on the collection GET, on " +
                "'/{EntitySet2}/$count', or anywhere else. This is the documented precedence, not a " +
                "failure: the configuration simply does nothing. Remove it, or remove {Winner3} if " +
                "the one you meant to serve is {Shadowed3}.",
                profile.EntitySetName, string.Join(" and ", shadowed), winner, winner,
                profile.EntitySetName, string.Join(" and ", shadowed), winner,
                string.Join(" or ", shadowed));
        }
    }

    private static void WarnUndeclaredConventionNavigations(OhDataRegistration registration, ILogger? logger)
    {
        if (logger is null) return;

        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            if (!profile.ExpandEnabled) continue;

            IEdmEntityType? entityType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (entityType is null) continue;

            foreach (IEdmNavigationProperty nav in entityType.NavigationProperties())
            {
                // The profile's OWN declared set — never a sibling's. Unlike WarnUnboundedBareExpand
                // this is not a Model B question: the defect is that THIS profile's route table and
                // expansion set were built from a name list that does not contain this navigation,
                // so no candidate set and no ResolveNavTreatment decision enter into it.
                // OrdinalIgnoreCase because both sides are EDM identifiers.
                if (profile.NavigationPropertyNames.Any(
                        n => string.Equals(n, nav.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Each placeholder appears EXACTLY once — Microsoft.Extensions.Logging binds a
                // template positionally, so a repeated one would consume an argument that is not
                // there. Repeated VALUES are passed again under a distinct name.
                logger.LogWarning(
                    "OhData: '{EntitySet}' has a navigation '{Navigation}' that the OData convention " +
                    "builder discovered on '{Model}' but the profile never declared with HasOptional/" +
                    "HasRequired/HasMany. $metadata advertises it as a navigation, yet only a DECLARED " +
                    "navigation is ever loaded, routed or written, so this entity set will never serve " +
                    "it and will never accept a value for it: '?$expand={Nav}' is accepted and answers " +
                    "200 with the navigation OMITTED from every entity, there is no " +
                    "'GET /{EntitySet2}({{key}})/{Nav2}' behind it either, and a nested value for it " +
                    "in a POST, PUT or PATCH body is discarded before the write handler runs — " +
                    "exactly as a declared " +
                    "navigation's is, unless AllowDeepWrites is enabled. A client that reads $metadata " +
                    "will keep asking to read and write related data it can " +
                    "never exchange. Declare it with HasOptional/HasRequired/HasMany (adding an expand " +
                    "delegate if loading it needs real logic), or Ignore() it if it should not be " +
                    "exposed at all — Ignore() takes it out of $metadata as well, so $metadata and " +
                    "the served surface agree again. OhData does not choose for you: both are valid " +
                    "answers and only you know which.",
                    profile.EntitySetName, nav.Name, profile.ModelType.Name, nav.Name,
                    profile.EntitySetName, nav.Name);
            }
        }
    }

    // #481/#368: authorization is PER PROFILE and does not compose across a navigation -- every
    // navigation-family route and every $expand call site runs under the DECLARING set's rule.
    //
    // A WARNING ONLY, by owner ruling: MS OData contains no authorization code at all and routes the
    // navigation onto the parent's controller, enforcing would break the idiomatic scoped-navigation
    // shape with no opt-out, and it collides with #293's frozen Model B. The declaration IS the
    // opt-in; this makes it loud.
    //
    // Three targeting rules, each a probe result -- change any and it goes silently wrong:
    //   1. target = the EDM navigation's own target type via ResolveProfilesForEdmType. NOT
    //      ChildEntitySetName (unset by the batchGetAll overload) and NOT NavItemType (unset by
    //      HasOptional/HasRequired).
    //   2. fires on the DECLARED navigation, not the routed one -- a bare HasMany with no handler
    //      still leaks via $expand. An UNDECLARED convention navigation is deliberately silent;
    //      WarnUndeclaredConventionNavigations already names it.
    //   3. the union, never the binding -- two or more exposing sets yield an EdmUnknownEntitySet
    //      placeholder, so a binding-only check breaks when an unrelated second set is registered.
    //
    // Compared per category, because a target with Read(AllowAnonymous) + Writes(RequireRole) is an
    // ordinary shape a read-only navigation loses nothing to. Read always; Create only with a post
    // handler; Update only with $ref handlers; Delete/Invoke never map a nav route.
    private static void WarnNavigationTargetAuthorization(OhDataRegistration registration, ILogger? logger)
    {
        if (logger is null) return;

        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            IEdmEntityType? entityType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (entityType is null) continue;

            foreach (IEdmNavigationProperty nav in entityType.NavigationProperties())
            {
                // Rule 2. OrdinalIgnoreCase, and against NavigationPropertyNames rather than a
                // separately derived EDM-name set, so this and WarnUndeclaredConventionNavigations
                // partition the navigations between them and can never both fire (or both stay
                // silent) for one declaration.
                if (!profile.NavigationPropertyNames.Any(
                        n => string.Equals(n, nav.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                NavigationRouteDefinition? route = profile.NavigationRoutes.FirstOrDefault(
                    r => string.Equals(r.PropertyName, nav.Name, StringComparison.OrdinalIgnoreCase));

                List<OhDataOperation> categories = new(3) { OhDataOperation.Read };
                if (route?.PostChild is not null) categories.Add(OhDataOperation.Create);
                if (route is not null && (route.AddRef is not null || route.RemoveRef is not null))
                    categories.Add(OhDataOperation.Update);

                foreach (IEntitySetEndpointSource target in
                         ExpandEngine.ResolveProfilesForEdmType(nav.ToEntityType(), registration))
                {
                    // A self-referential navigation, or one into this profile's own entity set,
                    // resolves to this profile: it is governed by its own rule by definition.
                    if (ReferenceEquals(target, profile)) continue;

                    string? unapplied = DescribeUnappliedRequirements(profile, target, categories);
                    if (unapplied is null) continue;

                    // Each placeholder appears EXACTLY once — Microsoft.Extensions.Logging binds a
                    // template positionally, so a repeated one would consume an argument that is
                    // not there. Repeated VALUES are passed again under a distinct name.
                    logger.LogWarning(
                        "OhData: '{EntitySet}' declares the navigation '{Navigation}', whose target " +
                        "entity type is also exposed as the entity set '{TargetEntitySet}' — and that " +
                        "set's own profile requires {Unapplied}, which is NOT applied here, because " +
                        "authorization is not applied across a navigation. Every route OhData maps " +
                        "for '{EntitySet2}' runs under '{EntitySet3}'s own rule and never " +
                        "'{TargetEntitySet2}'s: 'GET /{EntitySet4}({{key}})/{Nav}', its '/$count' and " +
                        "'/$ref' routes, the '$ref' and navigation-POST writes, and every " +
                        "'$expand={Nav2}' — the last of which needs no route at all, so the " +
                        "navigation declaration is the opt-in, by itself. Microsoft.AspNetCore.OData behaves the " +
                        "same way (its navigation action lives on the parent's controller, and it " +
                        "has no per-set authorization concept at all), so this is the OData norm " +
                        "rather than a gap OhData will close: it will NOT start consulting " +
                        "'{TargetEntitySet3}'s rule at request time. If the data reached through " +
                        "this navigation needs that protection, configure the same requirement on " +
                        "'{EntitySet5}' with RequireAuthorization/RequireRoles/ConfigureAuthorization, " +
                        "or split the surface so the protected rows are not reachable from an " +
                        "unprotected parent. If reaching them from here is intended — a scoped " +
                        "navigation off a parent the caller is already allowed to read — this is a " +
                        "valid design and nothing needs to change. See docs/authorization.md.",
                        profile.EntitySetName, nav.Name, target.EntitySetName, unapplied,
                        profile.EntitySetName, profile.EntitySetName, target.EntitySetName,
                        profile.EntitySetName, nav.Name, nav.Name,
                        target.EntitySetName, profile.EntitySetName);
                }
            }
        }
    }

    // Renders "one of the roles (admin) on reads and updates" for the categories in which
    // <paramref name="target"/> requires something <paramref name="declaring"/> does not; null when
    // nothing goes unapplied. Categories sharing an identical unapplied set are grouped so the
    // profile-wide authorization model — where every category carries the same requirements —
    // produces one clause rather than three copies of it.
    private static string? DescribeUnappliedRequirements(
        IEntitySetEndpointSource declaring,
        IEntitySetEndpointSource target,
        IReadOnlyList<OhDataOperation> categories)
    {
        List<(OhDataOperation Category, string Requirements)> unapplied = new(categories.Count);
        foreach (OhDataOperation category in categories)
        {
            IReadOnlyList<string> missing =
                NavigationTargetAuthorization.RequirementsNotApplied(declaring, target, category);
            if (missing.Count > 0) unapplied.Add((category, string.Join(", ", missing)));
        }
        if (unapplied.Count == 0) return null;

        List<string> clauses = new();
        foreach (var group in unapplied.GroupBy(u => u.Requirements, StringComparer.Ordinal))
        {
            string subjects = JoinWithAnd(group.Select(u => CategoryNoun(u.Category)).ToList());
            clauses.Add($"{group.Key} on {subjects}");
        }
        return string.Join("; ", clauses);
    }

    private static string CategoryNoun(OhDataOperation category) => category switch
    {
        OhDataOperation.Create => "creates",
        OhDataOperation.Update => "updates",
        _ => "reads",
    };

    private static string JoinWithAnd(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[parts.Count - 1]}",
    };

    // Leg 3 (docs-fidelity): an unbound function/action's success response is the bare
    // Invoke() result (no @odata.context envelope — see MapUnboundOperations below), so the
    // most honest static schema available is the operation's own declared return type
    // (UnboundOperationDefinition.ReturnType/ReturnsCollection, already unwrapped from
    // Task&lt;T&gt;/ValueTask&lt;T&gt; and, for a collection return, down to its element type, at
    // registration time). A void/Task-returning operation has no 200 response at all — every
    // call to it produces 204 — so ReturnType is null there and only 204 is registered.
    //
    // #498: that null case is reachable for unbound ACTIONS only. CSDL requires a function to
    // declare a return type, so AddFunction now refuses a void/Task/ValueTask handler at
    // registration (OperationSignatureValidation), where it previously killed GetEdmModel() with a
    // raw ArgumentNullException naming nothing. The sentence above used to imply both kinds.
    private static void AddUnboundOperationProduces(RouteHandlerBuilder rb, UnboundOperationDefinition op)
    {
        if (op.ReturnType is not null)
        {
            Type docType = op.ReturnsCollection
                ? typeof(IEnumerable<>).MakeGenericType(op.ReturnType)
                : op.ReturnType;
            rb.Produces(200, docType, "application/json");
        }
        rb.Produces(204);
    }

    /// <summary>
    /// #199/#487: replays the ENDPOINT-GATE half of an <see cref="OperationAuthRule"/> onto a route.
    /// Shared by the per-entity-set <c>ApplyOperationAuth</c> and by <see cref="MapUnboundOperations"/>,
    /// which authorize the same <see cref="AuthRequirement"/> list and must not translate it twice.
    /// <para>
    /// <see cref="AuthRequirementKind.Resource"/> is deliberately absent: it is not an endpoint gate
    /// at all but a per-request check against the loaded <c>{key}</c> entity (#199 Layer B), applied
    /// by <c>AttachResourceFilter</c> on the entity-set half and refused outright on the unbound half.
    /// </para>
    /// </summary>
    private static void ApplyAuthRequirements(
        IEndpointConventionBuilder rb, IReadOnlyList<AuthRequirement> requirements)
    {
        // Named policies apply as separate RequireAuthorization(name) calls (they stack -> AND).
        foreach (var req in requirements.Where(r => r.Kind == AuthRequirementKind.Policy))
        {
            rb.RequireAuthorization(req.Name!);
        }

        // Inline requirements (authenticated/role/claim) replay onto one AuthorizationPolicyBuilder.
        var inlineRequirements = requirements
            .Where(r => r.Kind is AuthRequirementKind.AuthenticatedUser
                               or AuthRequirementKind.Role
                               or AuthRequirementKind.Claim)
            .ToList();
        if (inlineRequirements.Count > 0)
        {
            rb.RequireAuthorization(policy =>
            {
                foreach (var req in inlineRequirements)
                {
                    switch (req.Kind)
                    {
                        case AuthRequirementKind.AuthenticatedUser:
                            policy.RequireAuthenticatedUser();
                            break;
                        case AuthRequirementKind.Role:
                            policy.RequireRole(req.Values!.ToArray());
                            break;
                        case AuthRequirementKind.Claim:
                            if (req.Values is { Count: > 0 })
                                policy.RequireClaim(req.Name!, req.Values);
                            else
                                policy.RequireClaim(req.Name!);
                            break;
                    }
                }
            });
        }
    }

    /// <summary>
    /// #487: does this registration require authorization ANYWHERE? The gate on every diagnostic
    /// below, and the reason none of them fires on a service that is simply public.
    /// <para>
    /// "Anywhere" means a profile that really imposes a requirement - the legacy profile-wide model
    /// with <see cref="AuthorizationConfig.Required"/>, or at least one <c>ConfigureAuthorization</c>
    /// category carrying a <c>Require*</c>. An <c>AllowAnonymous()</c> category is a statement that
    /// something is open, never that anything is closed, so it does not count.
    /// </para>
    /// </summary>
    private static bool RegistrationRequiresAuthorizationSomewhere(OhDataRegistration registration) =>
        registration.Profiles.Any(ProfileRequiresAuthorizationSomewhere);

    private static bool ProfileRequiresAuthorizationSomewhere(IEntitySetEndpointSource profile)
    {
        if (profile.Authorization is { Required: true }) return true;
        IReadOnlyList<OperationAuthRule>? rules = profile.OperationAuthorization;
        return rules is not null
            && rules.Any(r => !r.AllowAnonymous && r.Requirements.Count > 0);
    }

    /// <summary>
    /// #487: emits the startup <c>Warning</c> for every route carrying an
    /// <see cref="OhDataAnonymousRouteAudit"/> that is still anonymous once the HOST has finished
    /// configuring the group.
    /// <para>
    /// <b>Why <c>Finally</c> and not <c>MapAll</c>.</b> <c>MapOhData()</c> hands the group back and
    /// the host applies its own <c>RequireAuthorization()</c> to it afterwards - the mitigation
    /// <c>docs/authorization.md</c> recommends for exactly these routes. At map time a route with
    /// no rule of its own is therefore indistinguishable from one about to inherit that backstop,
    /// and warning there would fire on the correct configuration. A <c>Finally</c> convention runs
    /// after every convention on the endpoint, the host's included, so it can ask the question that
    /// actually matters: is anything going to authorize this endpoint at all? Measured on .NET
    /// 10.0.11 - a <c>Finally</c> registered before <c>group.RequireAuthorization()</c> observes
    /// that requirement's <c>IAuthorizeData</c> on every endpoint under the group.
    /// </para>
    /// <para>
    /// One warning per <see cref="OhDataAnonymousRouteAudit.Key"/>, not per endpoint: a category
    /// covers a dozen routes and the developer has one decision to make about all of them.
    /// </para>
    /// </summary>
    private static void AttachAnonymousRouteAudit(RouteGroupBuilder group, ILogger? logger)
    {
        if (logger is null) return;

        var reported = new HashSet<string>(StringComparer.Ordinal);
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            OhDataAnonymousRouteAudit? audit =
                endpoint.Metadata.OfType<OhDataAnonymousRouteAudit>().FirstOrDefault();
            if (audit is null) return;

            // The documented mitigation, applied: a group-level requirement covers this route, so
            // there is no hole and nothing to say. An explicit group-level AllowAnonymous is the
            // host stating the opposite intent just as deliberately, and is equally not a hole.
            if (endpoint.Metadata.OfType<IAuthorizeData>().Any()) return;
            if (endpoint.Metadata.OfType<IAllowAnonymous>().Any()) return;

            if (!reported.Add(audit.Key)) return;

            logger.LogWarning(
                "OhData: {Subject} {Detail} Nothing in this registration authorizes it, yet other " +
                "parts of the same registration require authorization - so securing everything you " +
                "named has still left a hole you did not name. {Remedy}",
                audit.Subject, audit.Detail, audit.Remedy);
        });
    }

    private static void MapUnboundOperations(
        RouteGroupBuilder group,
        IReadOnlyList<UnboundOperationDefinition> unboundOps,
        JsonSerializerOptions? jsonOptions,
        OhDataRegistration registration,
        bool registrationRequiresAuthSomewhere)
    {
        foreach (var op in unboundOps)
        {
            var opCapture = op;
            RouteHandlerBuilder unboundRb;
            if (!op.IsAction)
            {
                // Unbound function: GET /{prefix}/{FunctionName}?params
                var rb = group.MapGet($"/{op.Name}", async (HttpContext ctx, CancellationToken ct) =>
                {
                    // #359: the sigil gate, before parameter binding and before the handler
                    // delegate runs. See s_unboundOperationImplementedOptions.
                    IResult? opUnsupported = QueryOptionGate.CheckUnsupportedSystemQueryOptions(
                        ctx, QueryOptionGate.s_unboundOperationImplementedOptions);
                    if (opUnsupported is not null) return opUnsupported;

                    object?[] args = new object?[opCapture.Parameters.Length];
                    for (int i = 0; i < opCapture.Parameters.Length; i++)
                    {
                        var param = opCapture.Parameters[i];
                        if (ctx.Request.Query.TryGetValue(param.Name!, out var val))
                        {
                            try
                            {
                                var targetType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
                                var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
                                args[i] = converter.ConvertFromInvariantString(val.ToString() ?? "");
                            }
                            catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or OverflowException or ArgumentException)
                            {
                                return ODataError(400, "InvalidParameter",
                                    $"Cannot convert parameter '{param.Name}' value to {param.ParameterType.Name}.",
                                    target: param.Name);
                            }
                        }
                        else if (param.HasDefaultValue)
                        {
                            args[i] = param.DefaultValue;
                        }
                        else
                        {
                            return ODataError(400, "MissingParameter",
                                $"Required parameter '{param.Name}' is missing.", target: param.Name);
                        }
                    }
                    object? result = await opCapture.Invoke(args, ct);
                    // #396: an unbound operation returns an arbitrary CLR graph and gets no
                    // JsonNode stage, so it is serialized here, inside the endpoint-filter
                    // pipeline, rather than deferred to IResult execution. See PreRenderedJson.
                    return result is not null ? PreRenderedJson(result, jsonOptions ?? _pascalCaseSerializerOptions) : Results.NoContent();
                }).Produces(400).Produces(501);
                AddUnboundOperationProduces(rb, opCapture);
                // Issue #181: document the function's query-string parameters.
                var unboundFnQueryParams = BoundOperationResults.BuildFunctionQueryParametersMetadata(opCapture.Parameters, skipKey: false);
                if (unboundFnQueryParams is not null) rb.WithMetadata(unboundFnQueryParams);
                unboundRb = rb;
            }
            else
            {
                // Unbound action: POST /{prefix}/{ActionName} with JSON body
                var rb = group.MapPost($"/{op.Name}", async (HttpContext ctx, CancellationToken ct) =>
                {
                    // #359: the sigil gate, before parameter binding and before the handler
                    // delegate runs. See s_unboundOperationImplementedOptions.
                    IResult? opUnsupported = QueryOptionGate.CheckUnsupportedSystemQueryOptions(
                        ctx, QueryOptionGate.s_unboundOperationImplementedOptions);
                    if (opUnsupported is not null) return opUnsupported;

                    object?[] args = new object?[opCapture.Parameters.Length];
                    if (opCapture.Parameters.Length > 0)
                    {
                        // B2 fix: mirrors the PATCH/property-write pattern -- a wrong Content-Type
                        // gets a proper 415 envelope instead of either being silently parsed as
                        // JSON anyway or short-circuited by the implicit binder with an empty body.
                        if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);
                        try
                        {
                            var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                                ctx.Request.Body, cancellationToken: ct);

                            // B2 fix: a syntactically valid JSON payload that isn't a JSON object
                            // (array, string, number, bool, null) would previously reach
                            // TryGetJsonProperty -> JsonElement.EnumerateObject(), which throws
                            // InvalidOperationException for any non-Object ValueKind -- an
                            // uncaught 500. Reject it here as a normal 400 instead.
                            if (body.ValueKind != JsonValueKind.Object)
                            {
                                return ODataError(400, "InvalidBody", "Request body must be a JSON object.");
                            }

                            for (int i = 0; i < opCapture.Parameters.Length; i++)
                            {
                                var param = opCapture.Parameters[i];
                                if (TryGetJsonProperty(body, param.Name!, out var val))
                                {
                                    // #389 H2: same per-parameter dynamic-key check the bound
                                    // actions run. An unbound action's parameters bind into the same
                                    // CLR types and reach the same handlers.
                                    using PreparedWriteBody opPrepared = PrepareWriteBody(
                                        registration, val, param.ParameterType, jsonOptions);
                                    if (opPrepared.Error is not null) return opPrepared.Error;
                                    args[i] = opPrepared.Body.Deserialize(param.ParameterType, jsonOptions);
                                }
                                else if (param.HasDefaultValue)
                                {
                                    args[i] = param.DefaultValue;
                                }
                                else
                                {
                                    return ODataError(400, "MissingParameter",
                                        $"Required parameter '{param.Name}' is missing.", target: param.Name);
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            return ODataError(400, "InvalidBody", ex.Message);
                        }
                    }
                    object? result = await opCapture.Invoke(args, ct);
                    // #396: an unbound operation returns an arbitrary CLR graph and gets no
                    // JsonNode stage, so it is serialized here, inside the endpoint-filter
                    // pipeline, rather than deferred to IResult execution. See PreRenderedJson.
                    return result is not null ? PreRenderedJson(result, jsonOptions ?? _pascalCaseSerializerOptions) : Results.NoContent();
                }).Produces(400).Produces(415).Produces(501);
                AddUnboundOperationProduces(rb, opCapture);
                // Leg 2: an action's parameters are deserialized by name out of a JSON body object
                // (see the loop above), not a single bound CLR type. #184: synthesize a POCO whose
                // properties are exactly those parameters so the OpenAPI body schema shows the real
                // shape instead of an empty {}. The prose description is retained alongside it.
                if (opCapture.Parameters.Length > 0)
                {
                    rb.WithMetadata(new OhDataRequestBodyMetadata
                    {
                        // #499/#547: the key must carry registration IDENTITY -- "Unbound.{Name}"
                        // alone is scoped to nothing but the operation name, so two registrations
                        // declaring an unbound operation of the same name collided even when
                        // nothing else about them overlapped (the worst of the three sites, per
                        // #499/#425, since it doesn't even carry an entity set name). The
                        // registration is passed as the identity; the route key is scoped to it.
                        BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                            registration, $"Unbound.{opCapture.Name}", opCapture.Parameters),
                        Description = "JSON object with the action's parameters: " +
                            string.Join(", ", opCapture.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                    });
                }
                unboundRb = rb;
            }

            // #487 seam 1: an unbound operation is not scoped to an entity set, so no profile's
            // ConfigureAuthorization can reach it and no per-profile RequireAuthorization applies to
            // it. Until the `authorize` overloads of AddFunction/AddAction existed there was no way
            // to state a requirement for one at all: a registration in which EVERY profile required
            // authorization still served every unbound action anonymously, and measured on the
            // pre-fix tree that meant POST /{prefix}/{Action} -> 204 with the handler executed.
            ApplyUnboundOperationAuth(unboundRb, opCapture, registrationRequiresAuthSomewhere);
        }
    }

    /// <summary>
    /// #487: applies an unbound operation's own rule, or - when it declared none and the
    /// registration requires authorization somewhere else - marks the route for the startup
    /// diagnostic. See <see cref="AttachAnonymousRouteAudit"/> for why the diagnostic cannot be
    /// decided here.
    /// </summary>
    private static void ApplyUnboundOperationAuth(
        IEndpointConventionBuilder rb,
        UnboundOperationDefinition op,
        bool registrationRequiresAuthSomewhere)
    {
        string kind = op.IsAction ? "action" : "function";

        if (op.Authorization is { } rule)
        {
            if (rule.AllowAnonymous)
            {
                // Deliberately anonymous, and said so. Note this is the one place the metadata is
                // NOT applied as AllowAnonymousAttribute: doing that would let an unbound operation
                // tunnel out from under a host group requirement (seam 3) - a hole the host cannot
                // see and did not ask for. Stating "anonymous" here means "I am not adding a
                // requirement", not "I am removing yours".
                return;
            }

            // #220 parity with the entity-set half: expose the resolved requirements so the opt-in
            // OpenAPI/NSwag auth-requirements filters can render them.
            rb.WithMetadata(new OhDataOperationAuthMetadata(rule.Requirements));
            ApplyAuthRequirements(rb, rule.Requirements);
            return;
        }

        if (!registrationRequiresAuthSomewhere) return;

        rb.WithMetadata(new OhDataAnonymousRouteAudit(
            Key: $"unbound:{op.Name}",
            Subject: $"the unbound {kind} '{op.Name}' ({(op.IsAction ? "POST" : "GET")} /{{prefix}}/{op.Name}) is ANONYMOUS.",
            Detail: "An unbound operation is not scoped to an entity set, so no profile's " +
                    "RequireAuthorization()/RequireRoles()/ConfigureAuthorization(...) reaches it.",
            Remedy: op.IsAction
                ? $"Declare a requirement on the operation itself - AddAction({op.Name}, a => a.RequireAuthenticatedUser()) - " +
                  "or apply one to the whole surface with app.MapOhData().RequireAuthorization(). " +
                  $"If it is meant to be public, say so with AddAction({op.Name}, a => a.AllowAnonymous()) and this warning stops; " +
                  "on an UNBOUND operation that means 'I am not adding a requirement' and does NOT " +
                  "remove a host group requirement, unlike the same call on an entity-set category. See #572."
                : $"Declare a requirement on the operation itself - AddFunction({op.Name}, a => a.RequireAuthenticatedUser()) - " +
                  "or apply one to the whole surface with app.MapOhData().RequireAuthorization(). " +
                  $"If it is meant to be public, say so with AddFunction({op.Name}, a => a.AllowAnonymous()) and this warning stops; " +
                  "on an UNBOUND operation that means 'I am not adding a requirement' and does NOT " +
                  "remove a host group requirement, unlike the same call on an entity-set category. See #572."));
    }

    // #495: options for envelopes whose ENTIRE content is framework-generated -- the OData error
    // envelope and the service document. Deliberately host-free rather than the registration's
    // jsonOptions, which carries the host's converters and key policy by design (#252).
    //
    // Their members are contractual identifiers, not names a policy may rewrite, and there is no user
    // model data anywhere -- so nothing for a host to have an opinion about, and two things to break.
    // SHAPE: these are Dictionary<string,...>, so a host DictionaryKeyPolicy applies to the keys --
    // measured under SnakeCaseUpper, {"ERROR":{"CODE":...}} on every error response. FAULT: a host
    // converter that THROWS took the envelope with it, at IResult-execute time (the #396 hazard), so
    // the group filter could neither catch nor log it -- including for its OWN 500 envelope, the one
    // response that must never fail.
    //
    // The Encoder is set EXPLICITLY. ASP.NET Core's own Http.Json.JsonOptions overrides the Web
    // default with UnsafeRelaxedJsonEscaping, and a bare `new JsonSerializerOptions()` is NOT the
    // same bytes -- measured, a 404 quoting the requested key went 78 -> 88 bytes with every
    // apostrophe escaped. ErrorEnvelopeFidelityTests pins the bytes rather than the reasoning.
    private static readonly JsonSerializerOptions _frameworkEnvelopeSerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static IResult ODataError(
        int status, string code, string message,
        string? target = null)
    {
        var errorObj = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (target is not null) errorObj["target"] = target;

        var body = new Dictionary<string, object> { ["error"] = errorObj };
        return PreRenderedJson(body, _frameworkEnvelopeSerializerOptions, status);
    }

    // #396: serialize NOW, inside the filter pipeline, and return the bytes. RequestDelegateFactory
    // executes an IResult AFTER the filter chain unwinds, so a fault during result execution is
    // outside the group filter's try and the status line is already on the wire -- measured, a bound
    // function faulting during serialization logged "Request finished ... - 200" and shipped a
    // truncated body. A success status with a malformed body is worse than an envelope-less 500.
    //
    // Most routes are already immune because they build a JsonNode inside the handler, so user code
    // runs where the filter can see it. The routes that handed a RAW CLR object to Results.Json had no
    // such stage; this gives them one.
    //
    // Byte-identical by construction: same declared TValue, so the same JsonTypeInfo resolves -- do
    // NOT "simplify" the call sites to pass object. Content-Length is now explicit.
    //
    // Cost (OperationResultBufferingBenchmarks): a small DTO gets FASTER (0.74x -- the async state
    // machine dominates a tiny payload); 189 KB costs +18%, 9.4 MB +74%, all of it the extra byte[].
    // A pooled Utf8JsonWriter beats every arm and is deliberately not shipped: it needs the
    // JsonSerializerOptions -> JsonWriterOptions mapping transcribed by hand, and one wrong member
    // changes the response bytes, against a fix whose whole requirement is byte-identity.
    internal static IResult PreRenderedJson<TValue>(
        TValue value, JsonSerializerOptions options, int statusCode = StatusCodes.Status200OK)
        => new Utf8JsonHttpResult(JsonSerializer.SerializeToUtf8Bytes(value, options), statusCode);

    // #495: the owned options, with the OData envelope's contractual dictionary keys protected.
    //
    // Every success envelope here is a Dictionary<string,...> whose keys are identifiers the format
    // defines, and STJ applies DictionaryKeyPolicy to them -- measured under SnakeCaseUpper, a plain
    // collection GET came back as {"@ODATA.CONTEXT":...,"VALUE":[...]}, parseable JSON no OData
    // client can read. (The entity routes emit a JsonObject, whose member names STJ writes verbatim.)
    // The registration's options inherit the policy from the host by construction (#252), so clearing
    // it has to happen here.
    //
    // Everything else is left as the host configured it, because the VALUES in these envelopes are
    // payload -- #252's division is that OhData owns the names and the host owns value formatting.
    //
    // Free on a host that set no policy: the source instance is returned by reference, so nothing is
    // copied and no second JsonTypeInfo cache is created.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions>
        _envelopeOptionsCache = new();

    internal static JsonSerializerOptions EnvelopeOptions(JsonSerializerOptions source)
        => source.DictionaryKeyPolicy is null
            ? source
            : _envelopeOptionsCache.GetValue(
                source, static s => new JsonSerializerOptions(s) { DictionaryKeyPolicy = null });

    // #495: every framework envelope that carries payload goes out through here, so the contractual
    // keys are governed by OhData's options rather than the host's. Deliberately still DEFERRED
    // (Results.Json, not PreRenderedJson): the `value` member of these envelopes is a JsonArray the
    // handler already materialized, so there is no user code left to fault, and buffering a whole
    // collection page would be a real cost for no gain -- see PreRenderedJson's measured note. The
    // envelopes whose content is entirely framework-generated (errors, the service document) DO
    // pre-render, because there the payoff is the group filter being able to see a fault at all.
    private static IResult ODataEnvelopeResult(
        Dictionary<string, object?> envelope, JsonSerializerOptions? jsonOptions)
        => Results.Json(envelope, EnvelopeOptions(jsonOptions ?? _pascalCaseSerializerOptions));

    // The pre-rendered counterpart to JsonHttpResult: holds bytes that are already final, so its
    // ExecuteAsync runs no serialization and therefore cannot fail after the status line commits.
    private sealed class Utf8JsonHttpResult : IResult
    {
        private readonly byte[] _utf8Json;
        private readonly int _statusCode;

        internal Utf8JsonHttpResult(byte[] utf8Json, int statusCode)
        {
            _utf8Json = utf8Json;
            _statusCode = statusCode;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = _statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            httpContext.Response.ContentLength = _utf8Json.Length;
            return httpContext.Response.Body.WriteAsync(
                _utf8Json, 0, _utf8Json.Length, httpContext.RequestAborted);
        }
    }

    // Peels off the ODataKeyFormatException ODataKeyParser.Parse throws and maps it to the 400
    // envelope. `withTarget` preserves the split: entity-addressed routes point target at "key",
    // navigation routes omit it.
    //
    // #496 finding 4: this used to catch a bare FormatException, and the routes' try covers the WHOLE
    // handler body -- so a profile handler throwing FormatException from its own code was answered
    // with this 400, asserting the client's key was malformed for a request whose key had parsed one
    // line earlier. The dedicated type makes the clause exactly as wide as its message.
    //
    // AsHandlerFault marks a profile handler's fault so a read route's narrow catches cannot claim
    // it -- the marker sits on USER code, not on the framework's own option-touching calls. It takes
    // the already-started Task rather than a Func, so no closure is allocated on the hottest routes.
    // Safe because every Invoke* member is an `async` method: a delegate that throws SYNCHRONOUSLY
    // has its exception captured into the returned Task. Issue496ErrorHandlingTests uses exactly that
    // fixture shape, so the invariant is pinned rather than assumed.
    // #581: resolves this profile's ConfigureExceptions mappings at the SEAM, where the request
    // state is still in scope. It cannot be done at the group filter: that catch has no idea
    // whether the throw came from Post or from a nav batchGetAll three levels into an $expand, and
    // HandlerFaultException is no help either -- IsMisclassifiable wraps only ODataException and
    // FormatException, so the exceptions worth mapping reach the filter raw.
    //
    // Not async: a profile with no mappings -- every profile that has ever existed until now --
    // gets its Task back untouched, with no extra state machine. The context struct is built inside
    // the exception filter, so a request that does not throw allocates nothing.
    private static Task<T> WithExceptionMapping<T>(
        Task<T> handlerCall,
        IEntitySetEndpointSource source,
        HttpContext http,
        OhDataOperation operation,
        object? key = null,
        object? model = null,
        object? delta = null,
        string? navigation = null)
    {
        if (!source.HasExceptionMappings) return handlerCall;
        return Awaited(handlerCall, source, http, operation, key, model, delta, navigation);

        static async Task<T> Awaited(
            Task<T> call, IEntitySetEndpointSource src, HttpContext ctx, OhDataOperation op,
            object? k, object? m, object? d, string? nav)
        {
            OhDataResult? mapped = null;
            try
            {
                return await call.ConfigureAwait(false);
            }
            catch (Exception ex) when (TryMap(src, ctx, op, k, m, d, nav, ex, out mapped))
            {
                throw new OhDataRejectionException(mapped!, ex);
            }
        }

        static bool TryMap(
            IEntitySetEndpointSource src, HttpContext ctx, OhDataOperation op,
            object? k, object? m, object? d, string? nav, Exception ex, out OhDataResult? result)
        {
            result = null;

            // #493: a request the client actually aborted is never a client error to report -- there
            // is no response left to write. The same condition the group filter declines on.
            if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
                return false;

            var data = new ExceptionSeamData(
                op, ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null,
                k, m, d, nav);
            result = src.TryMapException(ex, in data);
            return result is not null;
        }
    }

    internal static async Task<T> AsHandlerFault<T>(Task<T> handlerCall)
    {
        try
        {
            return await handlerCall.ConfigureAwait(false);
        }
        catch (Exception ex) when (HandlerFaultException.IsMisclassifiable(ex))
        {
            throw new HandlerFaultException(ex);
        }
    }

    private static IResult BadKeyError(ILogger? logger, Exception ex, string key, string name, bool withTarget = true)
    {
        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: withTarget ? "key" : null);
    }

    // RFC 7231 §5.3.2 Accept negotiation (issue #182). Parses the Accept header into media
    // ranges with q-values and returns true when at least one range with q>0 matches a media
    // type this route can actually produce. Replaces the earlier substring scan, which mishandled
    // media ranges ("application/*" wrongly 406'd a JSON route), sub-type wildcards ("text/*" on
    // /$count) and q-values ("application/json;q=0" — which means "not acceptable" — wrongly 200'd).
    //
    // A media range's q-value applies to a candidate type via RFC 7231's specificity precedence:
    // the most specific matching range wins (exact type/subtype > type/* > */*). So
    // "application/json;q=0, application/*" excludes application/json even though "application/*"
    // would otherwise allow it.
    //
    // The caller has already special-cased the absent/empty header ("no constraint" → 200) before
    // reaching here, so a present-but-unparseable header is a genuinely malformed request: we treat
    // it as not-acceptable (406) — the safe, spec-defensible choice, and one that leaves every
    // existing well-formed-header test unchanged.
    private static bool AcceptHeaderPermits(string acceptHeader, IReadOnlyList<string> producibleTypes)
    {
        if (!MediaTypeHeaderValue.TryParseList(new[] { acceptHeader }, out IList<MediaTypeHeaderValue>? ranges)
            || ranges is null || ranges.Count == 0)
        {
            return false;
        }

        foreach (string producible in producibleTypes)
        {
            // Pick the most specific range matching this candidate; that range's q-value decides.
            int bestSpecificity = -1;
            double bestQuality = 0;
            foreach (MediaTypeHeaderValue range in ranges)
            {
                int specificity = MediaRangeSpecificity(range, producible);
                if (specificity < 0) continue; // this range does not match the candidate

                double quality = range.Quality ?? 1.0; // absent q ⇒ 1.0 (RFC 7231 §5.3.1)
                if (specificity > bestSpecificity
                    || (specificity == bestSpecificity && quality > bestQuality))
                {
                    bestSpecificity = specificity;
                    bestQuality = quality;
                }
            }

            if (bestSpecificity >= 0 && bestQuality > 0)
            {
                return true;
            }
        }

        return false;
    }

    // Returns how specifically an Accept media range matches a concrete "type/subtype" candidate:
    // 2 = exact (application/json), 1 = subtype wildcard (application/*), 0 = full wildcard (*/*),
    // -1 = no match. Higher wins under RFC 7231 §5.3.2 precedence.
    private static int MediaRangeSpecificity(MediaTypeHeaderValue range, string producibleType)
    {
        int slash = producibleType.IndexOf('/');
        string producibleMainType = producibleType.Substring(0, slash);
        string producibleSubType = producibleType.Substring(slash + 1);

        if (range.MatchesAllTypes) return 0;                                              // */*
        if (!range.Type.Equals(producibleMainType, StringComparison.OrdinalIgnoreCase)) return -1;
        if (range.MatchesAllSubTypes) return 1;                                           // type/*
        if (!range.SubType.Equals(producibleSubType, StringComparison.OrdinalIgnoreCase)) return -1;
        return 2;                                                                         // type/subtype
    }

    // #601: a 413 answered without reading the request body leaves the connection unusable, and
    // Kestrel closes it. RFC 9110 §7.6.1 requires the sender that intends to close to say so;
    // without the header a keep-alive client reuses a dead socket and its next request fails —
    // never retried when it is a POST. Measured: resp1 413 with no Connection header, resp2 either
    // no data or an aborted connection.
    private static void CloseConnectionAfterUnreadBody(HttpContext http)
    {
        if (!http.Response.HasStarted) http.Response.Headers.Connection = "close";
    }

    // Property allowlists: FilterProperties/OrderByProperties/SelectProperties/ExpandProperties are
    // wired into the EDM at startup as NotFilterable/NotSortable/NotSelectable/NotExpandable, and
    // those are enforced only when something calls Validate -- ApplyTo alone ignores them. The coarse
    // per-category gate is CheckCollectionQueryOptionCapabilities' job, with its own code and message;
    // this only needs to surface A 400, so MS's default wording is fine.
    //
    // Runs ONLY the three property-scoped validators, deliberately NOT ODataQueryOptions.Validate:
    // the whole-options validator also runs the Top validator, and the mere presence of model-bound
    // settings makes model-bound MaxTop default to 0, which would reject every $top outright.
    // $top/$skip/$count have their own enforcement in this file.
    private static void ValidatePropertyAllowlists<TModel>(ODataQueryOptions<TModel> options, ODataValidationSettings settings)
    {
        options.Filter?.Validate(settings);
        options.OrderBy?.Validate(settings);
        options.SelectExpand?.Validate(settings);
    }

    // #402: the try scope is EXACTLY the construction and the catch is deliberately broad. Every
    // failure inside it is a statement about the request URL, so 400 is right for the whole set --
    // and the scope had to be tightened first, because the old whole-handler try also contains
    // InvokeGetQueryableAsync, where a broad catch would relabel a database outage as a 400.
    //
    // Do NOT replace this with a type list. `$skiptoken=` throws ArgumentException from
    // SkipTokenQueryOption's ctor, not ODataException, and the throw set of somebody else's
    // constructors is not ours to enumerate. ODataException keeps its message pass-through so the
    // empty-value cases stay byte-identical; anything else is generic + logged at Warning.
    //
    // #426: the ODataQueryContext is built HERE, per request, and this takes the IEdmModel rather
    // than a context so no caller can hand it a shared one. ODataQueryOptions' constructor WRITES
    // context.RequestContainer/Request and Initialize reads Request back off that field, so a shared
    // context races and a valid request intermittently 400s. Measured 16-89 failures per 32,000
    // constructions across 16 threads sharing one; 0 with a fresh one.
    //
    // The (IEdmModel, IEdmType, ODataPath) overload is not a cheap alternative -- it leaves
    // ElementClrType null, which ODataQueryOptions<TEntity> throws on.
    private static bool TryBuildQueryOptions<TModel>(
        IEdmModel model, HttpContext ctx, ILogger? logger,
        [NotNullWhen(true)] out ODataQueryOptions<TModel>? options,
        [NotNullWhen(false)] out IResult? error)
    {
        // Deliberately outside the try: a model that does not contain TModel is a server
        // misconfiguration, not a statement about the request URL, so it must not be relabelled
        // 400. It cannot happen for a registered profile (the EDM is built by visiting TModel), and
        // if it ever does the group filter turns it into a logged 500 + OData error envelope.
        var context = new ODataQueryContext(model, typeof(TModel), null);

        try
        {
            options = new ODataQueryOptions<TModel>(context, ctx.Request);
            error = null;
            return true;
        }
        catch (Microsoft.OData.ODataException ex)
        {
            options = null;
            error = ODataError(400, "InvalidQueryOption", ex.Message);
            return false;
        }
        // #493: same refinement as the group filter -- decline the OCE family only when the client
        // really did abort. A cancellation raised by something INSIDE the construction while the
        // request is still live is a fault, not a disconnect, and belongs in the same 400 as the
        // rest of the measured throw set. The stakes are lower here than at the group filter (no
        // handler runs inside this try), but the asymmetry would invite the same bug back.
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !ctx.RequestAborted.IsCancellationRequested)
        {
            options = null;
            logger?.LogWarning(ex,
                "OhData: query options for {Method} {Path} could not be parsed",
                SanitizeLogValue(ctx.Request.Method),
                SanitizeLogValue(ctx.Request.Path.ToString()));
            error = ODataError(400, "InvalidQueryOption",
                "One or more system query options in the request URL could not be parsed.");
            return false;
        }
    }

    /// <remarks>
    /// <para>
    /// This check is advisory, not atomic. Between the ETag read and the caller's write,
    /// another request may modify the resource. For true atomic concurrency, use
    /// data-store-level concurrency tokens (e.g., EF Core [Timestamp] / SQL WHERE RowVersion = @expected).
    /// The HTTP ETag mechanism provides a best-effort conflict signal, not a transaction guarantee.
    /// </para>
    /// <para>
    /// #478: this is the single precondition gate for every state-changing route the framework
    /// owns and can key: entity PUT/PATCH/DELETE, the structural-property writes, the three
    /// $ref link-management routes, and the navigation-POST create route. Bound and unbound
    /// ACTIONS are deliberately outside it -- see the exclusion note at the entity-level bound
    /// action route and docs/etags.md.
    /// </para>
    /// </remarks>
    private static async Task<IResult?> CheckETagAsync(
        IEntitySetEndpointSource structuralSource,
        IEntitySetEndpointSource requestSource,
        HttpContext ctx,
        object parsedKey,
        CancellationToken ct)
    {
        if (!structuralSource.HasETag) return null;
        if (!structuralSource.HasGetById) return null;

        bool hasIfMatch = ctx.Request.Headers.TryGetValue("If-Match", out var ifMatch);
        bool hasIfNoneMatch = ctx.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch);
        if (!hasIfMatch && !hasIfNoneMatch) return null;

        // m6: the existence check must happen before the wildcard short-circuit. Per
        // RFC 7232 §3.1 / Protocol §11.4.1.1, If-Match -- including "*" -- fails with 412 when
        // no current representation exists; it must NOT fall through to whatever 404 the
        // caller's own "not found" handling would otherwise produce.
        object? current = await requestSource.InvokeGetByIdAsync(parsedKey!, ct);

        // RFC 9110 §13.2.2 fixes the evaluation order: If-Match is evaluated first, and
        // If-None-Match is evaluated ONLY when If-Match is absent. A request carrying both is
        // therefore not an AND -- If-Match wins outright.
        if (hasIfMatch)
        {
            if (current is null)
            {
                return ODataError(412, "PreconditionFailed",
                    "If-Match precondition failed: the resource does not exist.");
            }

            // RFC 7232 §3.1: If-Match may carry a comma-separated list of ETags.
            // The precondition is satisfied if the current ETag STRONGLY matches any one of them
            // (§13.1.1) -- ParseStrongETagList drops weak entries so they can never satisfy it.
            var etagList = ParseStrongETagList(ifMatch.ToString()).ToList();

            if (etagList.Contains("*")) return null; // wildcard -- matches any existing representation

            string currentETag = requestSource.InvokeGetETag(current);
            if (!etagList.Contains(currentETag))
                return ODataError(412, "PreconditionFailed", "The ETag does not match the current resource version.");
            return null; // OK to proceed
        }

        // If-None-Match on a state-changing method (RFC 9110 §13.1.2): the condition is FALSE --
        // and the method MUST NOT be performed -- when "*" is given and a current representation
        // exists, or when any listed validator matches under WEAK comparison. When nothing
        // matches, or the resource does not exist, the condition is true and the write proceeds
        // (a missing resource is exactly what "*" is asking for; see the AllowUpsert create-guard
        // on PUT, which covers the same case for a profile with no UseETag at all).
        if (current is null) return null;

        var noneMatchList = ParseETagList(ifNoneMatch.ToString()).ToList();
        if (noneMatchList.Contains("*"))
        {
            return ODataError(412, "PreconditionFailed",
                "If-None-Match: * precondition failed: a resource already exists at this key.");
        }

        if (noneMatchList.Contains(requestSource.InvokeGetETag(current)))
        {
            return ODataError(412, "PreconditionFailed",
                "If-None-Match precondition failed: the ETag matches the current resource version.");
        }

        return null; // OK to proceed
    }

    // -- JsonNode $select post-processing helpers ---------------------------------

    // #252: fallback serializer used only if a code path is somehow reached without the owned
    // options (they are threaded through every handler, so this is defensive). PascalCase
    // (PropertyNamingPolicy = null) — OhData's default — so it can never silently reintroduce
    // camelCase. JsonArray/JsonObject nodes pre-serialised here are written as-is by Results.Ok,
    // bypassing the ASP.NET Core pipeline, so casing must be baked in at this stage.
    // PropertyNameCaseInsensitive mirrors the host-options behavior so write-body binding through
    // this fallback stays case-insensitive (the server binds request bodies regardless of casing).
    internal static readonly JsonSerializerOptions _pascalCaseSerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
    };

    // M3: appends the OData JSON §10.7/§10.8 projection suffix to a context segment when a
    // $select projection narrowed the response, e.g. "Widgets" -> "Widgets(Id,Name)". A no-op
    // (segment returned unchanged) when no projection is in effect.
    private static string AppendSelectSuffix(string segment, IReadOnlyList<string>? selectedProps) =>
        selectedProps is { Count: > 0 } ? $"{segment}({string.Join(",", selectedProps)})" : segment;

    // M-3: apply $orderby to a navigation collection's in-memory results. Consistent with how
    // $top/$skip are already applied on this path (property-name based, not pushed down to the
    // handler or to SQL). Supports multiple sort keys ("Prop1 asc,Prop2 desc") and is
    // case-insensitive on the property name so it works the same whether the client sends the
    // CLR (PascalCase) name or the name the response serializer emits under the configured
    // naming policy. An unknown
    // property name returns (null, 400 InvalidQueryOption), mirroring the $select validation below.
    private static (IEnumerable<object>? Items, IResult? Error) ApplyNavOrderBy(
        IEnumerable<object> items, Type? navItemType, string orderByParam)
    {
        IOrderedEnumerable<object>? ordered = null;
        foreach (string clause in orderByParam.Split(',').Select(c => c.Trim()).Where(c => c.Length != 0))
        {
            string[] parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string propName = parts[0];
            bool descending = parts.Length > 1 && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);

            // #253: $orderby names are OData names — resolve to the CLR property by EDM name (honors
            // [JsonPropertyName]) and reject the renamed property's CLR name exactly as the main path.
            PropertyInfo? prop = null;
            if (navItemType is not null && !ODataPropertyNaming.TryResolveEdmName(navItemType, propName, out prop))
            {
                return (null, ODataError(400, "InvalidQueryOption",
                    $"Property '{propName}' does not exist on type '{navItemType.Name}'."));
            }

            object? KeySelector(object item) => prop?.GetValue(item);

            ordered = ordered is null
                ? (descending ? items.OrderByDescending(KeySelector) : items.OrderBy(KeySelector))
                : (descending ? ordered.ThenByDescending(KeySelector) : ordered.ThenBy(KeySelector));
        }

        return ((IEnumerable<object>?)ordered ?? items, null);
    }

    // Batch 3: build the navigation collection envelope, applying $select if present.
    // Returns (envelope, null) on success or (null, errorResult) when $select contains
    // an unknown property name.
    private static (Dictionary<string, object?>? Envelope, IResult? Error) BuildNavEnvelope(
        string baseUrl, string name, string key, string navPropertyName,
        long? navCount, object[] itemArray, HttpContext ctx, Type? navItemType,
        JsonSerializerOptions? jsonOptions, IEdmEntityType? navElementEdmType, IEdmModel? edmModel)
    {
        var navSerializerOptions = jsonOptions ?? _pascalCaseSerializerOptions;

        // #179: serialize the items up front (previously the no-$select path returned the raw CLR
        // objects) so un-expanded navigations on the nav element type can be stripped. Nav-collection
        // routes take no $expand, so every declared navigation on the element type is omitted per
        // OData JSON §4.5.1 / §11.2.4.2 — matching a top-level collection GET of that type instead
        // of leaking each item's whole CLR graph. Runs before $select so projection has final say.
        // #325/#326 (Option B): SerializeBounded with clause: null never hands ANY navigation to
        // System.Text.Json in the first place (bounded, not stripped-after), so a cyclic/tracked
        // nav item type is safe here too.
        var json = new JsonArray();
        foreach (object item in itemArray)
        {
            json.Add(ExpandEngine.SerializeBounded(item, navElementEdmType, edmModel, clause: null, navSerializerOptions));
        }
        // #184: navItemType is the CLR element type, so [JsonPropertyName] renames on its
        // navigations are honored when computing which keys to omit. Defence-in-depth (#325/#326):
        // a practical no-op now that SerializeBounded never wrote an un-expanded navigation, kept
        // in case a future caller ever hands this a clause again without checking.
        ExpandEngine.OmitUnexpandedNavigations(json, navElementEdmType, clause: null, navItemType, navSerializerOptions);

        // Apply $select post-processing for navigation results if requested.
        // We parse the $select query param directly (navigation routes don't go through
        // ODataQueryOptions) and filter the serialized items.
        List<string>? selectedProps = null;
        if (ctx.Request.Query.TryGetValue("$select", out var selectParam) && !string.IsNullOrEmpty(selectParam))
        {
            // M3: preserve request order (deduplicated) so the projected context URL lists
            // properties in the order the client asked for them.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            selectedProps = selectParam.ToString().Split(',')
                .Select(raw => raw.Trim())
                .Where(p => p.Length > 0)
                .Where(p => seen.Add(p))
                .ToList();

            // Validate each requested property exists on the nav item type.
            if (navItemType is not null)
            {
                foreach (string propName in selectedProps)
                {
                    // #253: $select names are OData names — validate against the EDM name (honors
                    // [JsonPropertyName]) so a renamed child property's name is accepted and its CLR
                    // name is rejected. The strip below keys off the payload (which is the rename too),
                    // so accepting only the EDM name keeps the two in agreement (no silent drop).
                    if (!ODataPropertyNaming.IsKnownEdmName(navItemType, propName))
                    {
                        return (null, ODataError(400, "InvalidQueryOption",
                            $"Property '{propName}' does not exist on type '{navItemType.Name}'."));
                    }
                }
            }

            foreach (JsonObject obj in json.OfType<JsonObject>())
            {
                var toRemove = obj.Select(p => p.Key)
                                 .Where(k => !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                                 .ToList();
                foreach (string? k in toRemove) obj.Remove(k);
            }
        }

        var envelope = new Dictionary<string, object?>();
        // M3: append the projection suffix when $select narrowed the response (JSON §10.7).
        // m10 (declared-not-fixed): the segment itself stays a path shape ("Set(key)/nav")
        // rather than the target entity set — see docs/spec-compliance.md.
        envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix($"{name}({key})/{navPropertyName}", selectedProps)}";
        if (navCount.HasValue) envelope["@odata.count"] = navCount;
        envelope["value"] = json;
        return (envelope, null);
    }

    // Gap 5: ODataEntityNode with optional @odata.id
    // Gap 2: optional @odata.etag in response body (§4.5.9)
    // OData-JSON §4.5: annotations SHOULD appear before the properties they describe.
    // Build a new JsonObject with annotations first, then copy entity properties.
    private static JsonObject ODataEntityNode(
        HttpContext ctx, string prefix, string contextSegment, object entity,
        JsonSerializerOptions? jsonOptions, IEdmModel? edmModel, string? odataId = null, string? etag = null,
        IEdmEntityType? omitNavsForType = null)
    {
        // #325/#326 (Option B): bounded by clause: null (no $expand is possible on this path — see
        // the deep-insert remarks below) rather than whole-graph. omitNavsForType null is the
        // CRITICAL deep-insert opt-out (§11.4.2.2): SerializeBounded falls back to the exact
        // pre-#325 whole-graph JsonSerializer.SerializeToNode call in that case, so a deep-insert
        // POST response body keeps its inline nested-create graph exactly as before this fix.
        var serialized = (JsonObject)ExpandEngine.SerializeBounded(entity, omitNavsForType, edmModel, clause: null, jsonOptions)!;
        string baseUrl = BuildBaseUrl(ctx, prefix);

        // #176: on single-entity read responses, omit navigation properties that were not
        // $expand'd (there is no $expand here, so every declared navigation is stripped). Callers
        // that must keep the graph inline — deep-insert POST (§11.4.2.2) — pass no type and are
        // unaffected. See OmitUnexpandedNavigations for the spec citation.
        // #184: the concrete entity's CLR type carries [JsonPropertyName] renames on its
        // navigations, so omission keys off the same names the serializer just wrote.
        // #325/#326: defence-in-depth (practical no-op now — SerializeBounded already omitted
        // every un-expanded navigation at the point of serialization).
        ExpandEngine.OmitUnexpandedNavigations(serialized, omitNavsForType, clause: null, entity.GetType(), jsonOptions);

        var node = new JsonObject
        {
            ["@odata.context"] = JsonValue.Create($"{baseUrl}/$metadata#{contextSegment}")
        };
        if (odataId is not null)
            node["@odata.id"] = JsonValue.Create(odataId);
        if (etag is not null)
            node["@odata.etag"] = JsonValue.Create($"\"{etag}\"");

        // Copy entity properties after annotations
        foreach (var prop in serialized.ToList())
        {
            serialized.Remove(prop.Key);
            node[prop.Key] = prop.Value;
        }

        return node;
    }

    internal static IResult ODataEntityResult(
        HttpContext ctx, string prefix, string name, object entity,
        JsonSerializerOptions? jsonOptions, IEdmModel? edmModel, string? odataId = null, string? etag = null,
        IReadOnlyList<string>? selectedProps = null,
        IEdmEntityType? omitNavsForType = null)
    {
        // M3: when $select projected the response, the context gains the projection suffix
        // ("#Set(prop1,prop2)/$entity", JSON §10.8) and unselected properties are stripped
        // from the body so the context and the payload agree on shape.
        //
        // #184 (decision: keep behavior, documented): when $select names a non-expanded
        // navigation property (e.g. GET Set(key)?$select=cast, no $expand), that item stays in
        // the projected context — the context URL MUST reflect the client's select list (OData
        // JSON §10.8) — while the body carries no member for it: selecting an un-expanded nav
        // selects its navigation *link*, and a convention-computable navigation link is omitted
        // under the default odata.metadata=minimal (JSON §4.5.9 / §11.2.4.1). The result is a
        // spec-defensible "content-less" entity (only @odata.* annotations) whose context still
        // lists the selected nav. We deliberately do NOT drop the projection suffix (the rejected
        // option (a)): doing so would emit "#Set/$entity", which claims the FULL entity was
        // returned — strictly more misleading than the current, standards-accurate context — and
        // would violate the §10.8 requirement that the context echo the select list verbatim.
        string contextSegment = $"{AppendSelectSuffix(name, selectedProps)}/$entity";
        JsonObject node = ODataEntityNode(ctx, prefix, contextSegment, entity, jsonOptions, edmModel, odataId: odataId, etag: etag, omitNavsForType: omitNavsForType);
        if (selectedProps is { Count: > 0 })
        {
            var toRemove = node.Select(p => p.Key)
                             .Where(k => !k.StartsWith("@", StringComparison.Ordinal) &&
                                         !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                             .ToList();
            foreach (string? key in toRemove) node.Remove(key);
        }
        return Results.Ok(node);
    }

    // I-6: formats a primitive property value as its raw (unquoted, unwrapped) OData /$value
    // representation (Part 2 §4.7), using invariant culture. bool is special-cased to lowercase
    // "true"/"false" (bool.ToString() is not culture-sensitive and returns "True"/"False"), and
    // date/time types use their ISO-8601 round-trip format ("O") rather than IFormattable's
    // culture-general format, matching how System.Text.Json serializes these types in the JSON
    // envelope so /Prop and /Prop/$value agree on representation.
    private static string FormatRawValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // Called via reflection with TKey/TModel resolved from the profile's runtime types.
    private static void MapEntitySet<TKey, TModel>(
        RouteGroupBuilder parentGroup,
        IEntitySetEndpointSource source,
        OhDataRegistration registration,
        ILoggerFactory? loggerFactory,
        JsonSerializerOptions? jsonOptions)
        where TModel : class
    {
        if (source.HasETag && !source.HasGetById)
        {
            throw new InvalidOperationException(
                $"Entity set '{source.EntitySetName}': UseETag requires GetById to also be configured. " +
                "ETag validation on PUT/PATCH/DELETE requires fetching the current entity.");
        }

        // #351: a selector whose type the hash cannot faithfully represent produces the SAME ETag
        // for every row (a type with no ToString() override formats to its own type name), which
        // silently turns If-Match into a no-op. That is the worst failure mode a concurrency
        // primitive has — invisible in every response, and only observable as a lost update — so
        // it fails loudly here rather than shipping.
        if (source.HasETag && source.ETagSelectors is { } etagSelectors)
        {
            foreach (ETagSelectorInfo selector in etagSelectors)
            {
                if (ETagValueFormatter.IsSupportedSelectorType(selector.Type))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Entity set '{source.EntitySetName}': UseETag selector '{selector.Description}' returns " +
                    $"'{selector.Type}', which cannot be hashed into a meaningful ETag — every entity in the " +
                    "set would share one ETag value and If-Match would never detect a conflict. Supported " +
                    "selector types are: a binary row-version buffer (byte[], ImmutableArray<byte>, " +
                    "ReadOnlyMemory<byte>, Memory<byte>, ArraySegment<byte>), string, bool, an enum, any type " +
                    "implementing IFormattable (all the numeric, date/time, TimeSpan and Guid types), or a " +
                    "Nullable of any of those. Select a scalar projection instead, e.g. " +
                    "'x => x.Something.Id' or 'x => x.Something.RowVersion'.");
            }
        }

        // Profiles are registered as scoped. At request time, resolve a fresh instance
        // so handler delegates capture per-request scoped dependencies (e.g. DbContext).
        // The startup 'source' is used only for structural queries (HasGetById, MaxTop, etc.).
        Type profileType = source.GetType();
        IEntitySetEndpointSource ResolveHandlers(HttpContext ctx) =>
            (IEntitySetEndpointSource)ctx.RequestServices.GetRequiredService(profileType);

        string name = source.EntitySetName;
        string prefix = registration.Prefix;

        // Resolve this entity set's EDM type once at startup. It drives the #176 strip that omits
        // un-expanded navigation properties from read responses (never per-request EDM lookups).
        IEdmEntityType? rootEdmType =
            registration.EdmModel.EntityContainer?.FindEntitySet(name)?.EntityType;

        var logger = loggerFactory?.CreateLogger("OhData");

        if (source.IsAdvancedConfigureOverridden)
        {
            logger?.LogDebug(
                "OhData: {EntitySet} uses AdvancedConfigure override — automatic EDM configuration (HasKey, Filter, Select, etc.) was ejected.",
                name);
        }

        // Create an auth group for this entity set with an empty prefix so that auth is
        // applied once and propagates to all routes (both collection and key-based).
        // Key-based routes use templates like "/{name}({key})" which embed the entity set name
        // and must be mapped directly here rather than in a sub-group, because MapGroup inserts
        // a separator that would produce /name/({key}) instead of /name({key}).
        AuthorizationConfig? authConfig = source.Authorization;
        var entityAuthGroup = parentGroup.MapGroup("");

        if (authConfig is not null)
        {
            if (authConfig.Policy is not null)
                entityAuthGroup.RequireAuthorization(authConfig.Policy);
            if (authConfig.Roles is { Count: > 0 })
                entityAuthGroup.RequireAuthorization(policy => policy.RequireRole(authConfig.Roles.ToArray()));
            if (authConfig.Policy is null && authConfig.Roles is null or { Count: 0 })
                entityAuthGroup.RequireAuthorization();
        }

        // #203: attach this entity set's resolved write-body-size limit as endpoint metadata,
        // enforced by the group-level filter in MapAll for write methods only. Attached to the
        // auth group so it propagates to every route under this entity set (collection and
        // key-based). Absent metadata means "no OhData-level limit" (Kestrel's global still applies).
        if (source.MaxRequestBodyBytes is long maxBodyBytes)
        {
            entityAuthGroup.WithMetadata(new OhDataBodyLimitMetadata(maxBodyBytes));
        }

        // Collection-level routes use a sub-group so they can use the short "" template.
        var entityGroup = entityAuthGroup.MapGroup($"/{name}");

        // Cache ODataQuerySettings once at startup so each request does not allocate new instances.
        // Safe to share BECAUSE OF HOW IT IS USED, not because the type is immutable — it is a
        // mutable POCO. Every consumer these instances reach (FilterQueryOption/OrderByQueryOption/
        // SkipQueryOption/TopQueryOption.ApplyTo, and QueryBinderContext, which holds it behind a
        // get-only property) only reads it; the settings-mutating paths in Microsoft.AspNetCore
        // .OData all mutate an instance they created themselves — ODataQueryOptions.ApplyTo starts
        // with `querySettings = Context.UpdateQuerySettings(querySettings, query)`, which CopyFroms
        // into a new ODataQuerySettings, and the IgnoredQueryOptions/MaxFunctionCallDepth writes sit
        // on objects from GetODataQuerySettings() or `new ODataQuerySettings()`. Verified against
        // the Microsoft.AspNetCore.OData source, not assumed (#426).
        //
        // NOT the case for ODataQueryContext, which used to be cached on this line under the same
        // comment: it is written by ODataQueryOptions' constructor on every use and cannot be
        // shared. It is now built per request inside TryBuildQueryOptions — see the note there.
        var cachedCountSettings = new ODataQuerySettings();
        var cachedQuerySettings = new ODataQuerySettings { PageSize = source.MaxTop };
        // #206 phase 2 (optioned expand): settings for the FilterBinder/OrderByBinder that translate a
        // pushed expand's nested $filter/$orderby into the filtered-Include lambda. HandleNullPropagation
        // is False because the target is always an EF Core IQueryable (the pushdown gate requires it),
        // so the provider — not client-side null guards — evaluates the predicate in SQL.
        var cachedBinderSettings = new ODataQuerySettings { HandleNullPropagation = HandleNullPropagationOption.False };
        // #202: per-entity-set complexity-guard settings (expansion depth + node counts).
        var cachedValidationSettings = QueryOptionGate.BuildValidationSettings(source);

        // #206: $select projection pushdown — startup-computed eligibility inputs. Member-init
        // needs a public parameterless constructor (positional records have none), and the
        // per-request projection-set assembly matches selected names against the structural
        // properties by name. Names are matched case-insensitively (EDM identifiers); a model
        // whose structural properties differ only by case makes that lookup ambiguous, so such
        // a profile is pushdown-ineligible outright rather than crashing the dictionary build.
        bool pushdownCtorOk = typeof(TModel).GetConstructor(Type.EmptyTypes) is not null;
        // #322: the projection's structural member set is EDM-AWARE, and this is the ONLY place the
        // two navigation name spaces are reconciled. StructuralProperties subtracts only
        // PROFILE-DECLARED navigations, so a convention-discovered one survives carrying
        // IsComplex = true -- and TryBuildProjectionInit's complex-member bail then abandoned $select
        // pruning AND $expand pushdown for the whole entity set, on every bare $expand.
        //
        // Scope is deliberately THIS dictionary. NavigationPropertyNames is NOT re-sourced from the
        // EDM: it feeds Model B's DB/DL partitioning, whose frozen "a candidate that neither routes
        // nor declares the nav has no opinion" category would empty under convention sourcing,
        // collapsing the honored-sole-route case from RunDelegate to Blank -- a delegate that no
        // longer runs, data silently replaced by null. Issue322ModelBClassificationTests pins it.
        //
        // Both name spaces are EDM names, so the match is exact: #253 gives every EDM navigation the
        // same [JsonPropertyName]-resolved name StructuralPropertyInfo.Name carries.
        // OrdinalIgnoreCase, as OData resolves identifiers. NavigationProperties(), not
        // DeclaredNavigationProperties(), so an inherited navigation is subtracted too.
        var edmNavigationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootEdmType is not null)
        {
            foreach (IEdmNavigationProperty edmNav in rootEdmType.NavigationProperties())
                edmNavigationNames.Add(edmNav.Name);
        }
        var pushdownNameGroups = source.StructuralProperties
            .Where(p => !edmNavigationNames.Contains(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool pushdownNamesUnambiguous = pushdownNameGroups.All(g => g.Count() == 1);
        var pushdownStructuralByName = pushdownNameGroups
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // #440 symptom 2: the same subtraction, applied to the set driving STRUCTURAL-PROPERTY route
        // registration. source.StructuralProperties is "public readable CLR properties MINUS
        // PROFILE-DECLARED navigations", so a convention-discovered navigation survived it and the
        // block below registered GET /{Set}({key})/{Nav}, its /$value, and PUT/PATCH/DELETE over a
        // NAVIGATION -- building a one-property Delta over a navigation member. Two profiles over the
        // same CLR member exposed different route tables purely by declaration provenance.
        //
        // Applied HERE, not in BuildStructuralProperties, for two reasons. (a) It cannot go there:
        // that runs from VisitModelBuilder, while the EDM is being built, so registration.EdmModel
        // does not exist yet -- and EntitySetProfile deliberately carries no dependency on a built
        // model. (b) It should not: narrowing at the source moves every consumer for one route-table
        // defect, so the two production consumers are handled explicitly instead.
        //
        // NavigationPropertyNames is still NOT touched -- it feeds Model B's DB/DL partitioning, whose
        // "declares nothing" category empties under convention sourcing (Issue322ModelBClassificationTests).
        //
        // The bound-function collision check below iterates this SAME narrowed set deliberately: with
        // no property route registered there is no (template, GET) pair left to collide with.
        IReadOnlyList<StructuralPropertyInfo> structuralRouteProperties =
            edmNavigationNames.Count == 0
                ? source.StructuralProperties
                : source.StructuralProperties.Where(p => !edmNavigationNames.Contains(p.Name)).ToArray();

        // #206 phase 2 (Option A1): $expand Include pushdown — startup-computed per-navigation
        // bindings, keyed by CLR navigation property name. THE ELIGIBILITY RULE IS PROVENANCE:
        // a navigation is pushed down ONLY when it was declared WITHOUT a custom expand delegate.
        // A delegate-backed navigation always owns a NavigationRouteDefinition (routes are created
        // only when a handler is supplied), so "declared as a navigation but has no route" IS the
        // no-delegate test. NavigationPropertyNames holds every declared navigation (bare and
        // delegate-backed alike); NavigationRoutes holds only the delegate-backed ones — so the
        // set difference is exactly the delegate-less navigations. Each survivor of the structural
        // safety checks (settable property, non-cyclic related type, List-assignable collection)
        // becomes SQL-JOIN-expandable; delegate-backed navigations expand through their delegate
        // (Stage 3 / ExpandLevelAsync) and never appear here. Empty when the model exposes no
        // eligible delegate-less navigation, which short-circuits the request-time gate.
        //
        // No threading into the JSON pipeline is needed: a delegate-less navigation has no route,
        // so ExpandLevelAsync already skips it and leaves the Stage-1 serialization (the pushed,
        // JOIN-materialized related rows) in place; OmitUnexpandedNavigations then keeps it because
        // it was $expand'd. Delegate-backed navigations are the only ones ExpandLevelAsync loads.
        var routeBackedNavNames = new HashSet<string>(
            source.NavigationRoutes.Select(r => r.PropertyName), StringComparer.OrdinalIgnoreCase);
        var pushdownExpandNavs = source.NavigationPropertyNames
            .Where(navName => !routeBackedNavNames.Contains(navName)) // delegate-backed → delegate path only
            .Select(navName => (navName, binding: ExpandEngine.BuildExpandNavBinding<TModel>(navName, registration.EdmModel)))
            .Where(pair => pair.binding is not null)
            .ToDictionary(pair => pair.navName, pair => pair.binding!.Value, StringComparer.OrdinalIgnoreCase);

        // #313 stage 5: the navigations of THIS entity set whose bare $expand may page, resolved once
        // at startup by the shared predicate. Empty on the shipping default (ExpandPagingEnabled is
        // false), which is what makes the route table, $metadata and the three OpenAPI documents
        // byte-identical to a registration that never heard of #313.
        IReadOnlyList<ExpandEngine.ExpandPagingNav> expandPagingNavs =
            ExpandEngine.ResolveExpandPagingNavigations(source, typeof(TModel), registration);
        // Same set, keyed for the emission site's per-expand lookup. Ordinal-ignore-case to match how
        // every other EDM-name lookup in this file compares identifiers.
        IReadOnlyDictionary<string, ExpandEngine.ExpandPagingNav> expandPagingNavsByEdmName =
            expandPagingNavs.ToDictionary(n => n.EdmName, StringComparer.OrdinalIgnoreCase);

        // #418/#463/#464: the ceiling on a raw-served expansion USED TO BE precomputed here, as a
        // startup-resolved dictionary of this profile's own delegate-less collection navigations,
        // consulted by a depth-1 pass on the GetById route alone. Both of those were holes (#463
        // depth, #464 path) and the whole mechanism now lives in ApplyCollectionPipelineAsync's
        // Stage 3.6, resolved PER LEVEL through the shared ResolveNavTreatment. Nothing is
        // precomputed because nothing can be: the candidate set below depth 1 is a property of the
        // request's own $expand tree, not of this entity set. See EnforceRawExpandCeiling.

        // #199 Layer C: per-operation authorization. When the profile declared
        // ConfigureAuthorization(...), resolve the effective rule per route category and apply it to
        // that route's own handler builder — not a shared group, because the MapGroup slash rule
        // forbids per-category sub-groups for key-based routes. When null, the legacy single-group
        // auth applied above (entityAuthGroup) governs instead and these helpers are no-ops.
        IReadOnlyList<OperationAuthRule>? operationAuthRules = source.OperationAuthorization;

        OperationAuthRule? ResolveOperationRule(OhDataOperation category, string? boundOperationName)
        {
            if (operationAuthRules is null) return null;
            OperationAuthRule? generic = null;
            OperationAuthRule? named = null;
            foreach (var rule in operationAuthRules.Where(rule => (rule.Operations & category) != 0))
            {
                if (rule.BoundOperationName is null)
                {
                    generic = rule; // last generic rule for this category wins
                }
                // #525: OrdinalIgnoreCase, not Ordinal. Everything this rule governs -- the route
                // template, the operation segment, every other EDM-identifier lookup in this file --
                // matches case-insensitively, so an Ordinal comparison here made Invoke("stamp", ...)
                // against an operation declared `Stamp` resolve to NOTHING. The rule was discarded in
                // silence and the route fell back to the generic Invoke rule, or -- with no generic
                // rule -- to no requirement at all. That is a fail-OPEN on an authorization rule,
                // which is why the comparer alone is not the whole fix: the startup validation below
                // refuses any named rule that resolves to no declared operation, so a MISSPELLED name
                // (which no comparer can rescue) cannot evaporate either.
                else if (boundOperationName is not null &&
                         string.Equals(rule.BoundOperationName, boundOperationName, StringComparison.OrdinalIgnoreCase))
                {
                    named = rule; // a name-specific rule (Invoke("Name", …)) wins over a generic one
                }
            }
            return named ?? generic;
        }

        // #487 seam 2: does this profile impose a requirement on ANY category? Only then is a
        // rule-less category a hole rather than a service that is simply public. Same question
        // RegistrationRequiresAuthorizationSomewhere asks, one scope in.
        bool profileRequiresAuthSomewhere =
            operationAuthRules is not null
            && operationAuthRules.Any(r => !r.AllowAnonymous && r.Requirements.Count > 0);

        // #487 seam 2: one audit record per category, built lazily and cached, so the dozen routes
        // that share a category attach the same instance and the Finally convention's dedupe key
        // does the rest.
        var categoryAudits = new Dictionary<OhDataOperation, OhDataAnonymousRouteAudit>();
        OhDataAnonymousRouteAudit? CategoryAudit(OhDataOperation category)
        {
            if (categoryAudits.TryGetValue(category, out var cached)) return cached;

            // The selector is spelled out with its own lambda parameter so both halves of the
            // remedy are copy-pasteable as written -- the AllowAnonymous form is derived from the
            // same (method, parameter) pair rather than by string-substituting the ellipsis, which
            // produced `.Invoke(i => x.AllowAnonymous())`.
            (string label, string selector, string param) = category switch
            {
                OhDataOperation.Read => ("read", "Read", "r"),
                OhDataOperation.Create => ("create", "Create", "c"),
                OhDataOperation.Update => ("update", "Update", "u"),
                OhDataOperation.Delete => ("delete", "Delete", "d"),
                OhDataOperation.Invoke => ("bound function/action invocation", "Invoke", "i"),
                _ => (category.ToString(), category.ToString(), "x"),
            };

            var audit = new OhDataAnonymousRouteAudit(
                Key: $"set:{name}|{category}",
                Subject: $"the {label} routes of entity set '{name}' are ANONYMOUS.",
                Detail: $"Its ConfigureAuthorization(…) block names no rule for the " +
                        $"{category} category, and a category with no rule emits no requirement.",
                Remedy: $"Add .{selector}({param} => …) with the requirement you intended. If these " +
                        $"routes are meant to be public, say so with " +
                        $".{selector}({param} => {param}.AllowAnonymous()) and this warning stops -- " +
                        $"but on a category that emits AllowAnonymousAttribute, which overrides a " +
                        $"host-applied app.MapOhData().RequireAuthorization(). If you only mean " +
                        $"'no requirement of its own', name the requirement you intended instead. " +
                        $"See #572.");
            categoryAudits[category] = audit;
            return audit;
        }

        // Layer C applies coarse per-route auth. `keyBased` marks routes carrying a {key} segment, to
        // which Layer B (resource-based) auth attaches a load-by-key filter (see AttachResourceFilter);
        // collection-level routes (no {key}) pass keyBased: false.
        void ApplyOperationAuth(IEndpointConventionBuilder rb, OhDataOperation category, string? boundOperationName = null, bool keyBased = true)
        {
            if (operationAuthRules is null) return; // legacy group-auth path governs instead
            OperationAuthRule? rule = ResolveOperationRule(category, boundOperationName);
            if (rule is null)
            {
                // No rule -> inherit any group/global auth, and be anonymous when there is none.
                //
                // #487 seam 2: that "when there is none" is the fail-open, and it is silent. A
                // profile migrated from RequireAuthorization() -- which covers ALL operations --
                // to ConfigureAuthorization(a => a.Read(...).Writes(...)) reads as a refinement and
                // is a WIDENING: nothing names the Invoke category, so every bound function and
                // action on the set drops to anonymous. Measured on the pre-fix tree, that profile
                // answered 401 on its collection GET and 204-with-the-handler-executed on both
                // POST /Set/Action and POST /Set(key)/Action.
                //
                // Only a category the profile said NOTHING about is audited. An explicit
                // AllowAnonymous() produces a non-null rule and never reaches here, which is what
                // makes the diagnostic silenceable by stating the intent it is asking about.
                if (profileRequiresAuthSomewhere && CategoryAudit(category) is { } audit)
                {
                    rb.WithMetadata(audit);
                }
                return;
            }
            if (rule.AllowAnonymous)
            {
                rb.AllowAnonymous();
                return;
            }

            // #220: expose the resolved structured requirements as endpoint metadata so the opt-in
            // OpenAPI/NSwag "auth requirements" filters can render them (kinds/values) into the
            // operation description. Attached only on secured routes; anonymous routes returned above.
            rb.WithMetadata(new OhDataOperationAuthMetadata(rule.Requirements));

            // #199 Layer B: resource-based (instance-level) requirements are not an endpoint gate —
            // they are evaluated inside a per-request filter that loads the {key} entity. Attaching it
            // here (only when the category opts in) keeps property/nav/$ref routes gap-free.
            if (keyBased)
            {
                AttachResourceFilter(rb, category, boundOperationName);
            }

            // #487: the endpoint-gate half is shared with MapUnboundOperations rather than
            // transcribed there. An unbound operation's rule carries the same AuthRequirement list
            // this one does, and two replays of one list would be two things that must agree,
            // derived independently.
            ApplyAuthRequirements(rb, rule.Requirements);
        }

        // #199 Layer B helpers ─────────────────────────────────────────────────
        bool CategoryHasResource(OhDataOperation category, string? boundOperationName)
        {
            OperationAuthRule? rule = ResolveOperationRule(category, boundOperationName);
            return rule is { AllowAnonymous: false }
                && rule.Requirements.Any(r => r.Kind == AuthRequirementKind.Resource);
        }

        static OperationAuthorizationRequirement BuiltInResourceRequirement(OhDataOperation category) => category switch
        {
            OhDataOperation.Read => OhDataOperations.Read,
            OhDataOperation.Create => OhDataOperations.Create,
            OhDataOperation.Update => OhDataOperations.Update,
            OhDataOperation.Delete => OhDataOperations.Delete,
            _ => OhDataOperations.Invoke,
        };

        // Evaluate the category's resource-based requirements against `entity` via
        // IAuthorizationService. Returns a 403 result on failure (fail-closed — a requirement no
        // registered handler satisfies denies), or null to proceed. No-op without a Resource requirement.
        async Task<IResult?> CheckResourceAuthAsync(HttpContext ctx, object entity, OhDataOperation category, string? boundOperationName)
        {
            OperationAuthRule? rule = ResolveOperationRule(category, boundOperationName);
            if (rule is null || rule.AllowAnonymous) return null;
            var resourceReqs = rule.Requirements.Where(r => r.Kind == AuthRequirementKind.Resource).ToList();
            if (resourceReqs.Count == 0) return null;

            var authService = ctx.RequestServices.GetRequiredService<IAuthorizationService>();
            foreach (var req in resourceReqs)
            {
                AuthorizationResult result = req.Name is not null
                    ? await authService.AuthorizeAsync(ctx.User, entity, req.Name)
                    : await authService.AuthorizeAsync(ctx.User, entity, BuiltInResourceRequirement(category));
                if (!result.Succeeded)
                {
                    return ODataError(403, "Forbidden",
                        "You are not authorized to perform this operation on the requested resource.");
                }
            }
            return null;
        }

        // Attach a per-request filter to a key-based route that loads the {key} entity and runs the
        // category's resource requirement against it. Only attaches when the category opts in, so
        // non-resource routes carry zero request-time overhead.
        void AttachResourceFilter(IEndpointConventionBuilder rb, OhDataOperation category, string? boundOperationName)
        {
            if (!CategoryHasResource(category, boundOperationName)) return;
            rb.AddEndpointFilter(async (efc, next) =>
            {
                HttpContext ctx = efc.HttpContext;
                if (ctx.Request.RouteValues.TryGetValue("key", out object? keyObj) && keyObj is string keyStr)
                {
                    var s = ResolveHandlers(ctx);
                    object? parsedKey;
                    try
                    {
                        parsedKey = ODataKeyParser.Parse(keyStr, typeof(TKey));
                    }
                    catch (ODataKeyFormatException)
                    {
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{keyStr}'", target: "key");
                    }

                    object? entity = await s.InvokeGetByIdAsync(parsedKey!, ctx.RequestAborted);
                    if (entity is null)
                    {
                        return ODataError(404, "NotFound", $"{name} with key '{keyStr}' was not found.");
                    }

                    IResult? authFail = await CheckResourceAuthAsync(ctx, entity, category, boundOperationName);
                    if (authFail is not null) return authFail;
                }
                return await next(efc);
            });
        }

        // #525: a named Invoke rule must name a bound operation this profile really declares. The
        // comparer fix closes the MISCASED spelling; it cannot close the class, because a misspelled
        // name resolves to nothing under any comparer with the same silent consequence -- the rule is
        // discarded and the route falls back to the generic rule or to no requirement at all.
        // Refused rather than warned: an authorization rule that does not apply is not a diagnostic.
        //
        // Matched with the SAME comparer ResolveOperationRule uses -- a stricter check here would
        // reject exactly the miscased rules the fix just made work. Placed BEFORE the #486 GetById
        // guard so a typo is reported as a typo.
        //
        // #546: and no two named rules may resolve to the SAME operation. That hazard was INTRODUCED
        // by the comparer, and #525's check cannot see it -- it asks only whether a name resolves, and
        // both members of a colliding pair do. ResolveOperationRule is last-write-wins, so declaration
        // ORDER decides authorization: measured, `.Invoke("Stamp", RequireRole).Invoke("stamp",
        // AllowAnonymous)` served an anonymous invocation 200, where the pre-#525 Ordinal comparer had
        // made that same configuration deterministically PROTECTED.
        //
        // Applies to identically-spelled duplicates too. GENERIC Invoke rules are deliberately out of
        // scope: last-write-wins is BY DESIGN there, as for every category selector.
        if (operationAuthRules is not null)
        {
            string[] declaredOperationNames = source.BoundFunctions
                .Concat(source.BoundActions)
                .Select(o => o.Name)
                .ToArray();

            // Resolved declared name -> the first rule that claimed it, so the message can name the
            // pair rather than only the survivor.
            var claimedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (OperationAuthRule namedRule in operationAuthRules)
            {
                if (namedRule.BoundOperationName is null) continue;

                string? resolved = declaredOperationNames.FirstOrDefault(declared => string.Equals(
                    declared, namedRule.BoundOperationName, StringComparison.OrdinalIgnoreCase));

                if (resolved is null)
                {
                    string candidates = declaredOperationNames.Length == 0
                        ? "This profile declares no bound operation at all."
                        : "Declared bound operations (matched case-insensitively): " +
                          string.Join(", ", declaredOperationNames.Select(n => $"'{n}'")) + ".";
                    throw new InvalidOperationException(
                        $"Entity set '{name}': the authorization rule Invoke(\"{namedRule.BoundOperationName}\", …) " +
                        "names an operation this profile does not declare — there is no bound function or " +
                        "action called that. A named Invoke rule that resolves to nothing is silently " +
                        "discarded: the route would fall back to the generic Invoke rule, or, when there is " +
                        $"none, to no authorization requirement at all. {candidates} Correct the name, declare " +
                        "the operation with BindFunction/BindAction/BindEntityFunction/BindEntityAction, or " +
                        "remove the rule.");
                }

                if (claimedBy.TryGetValue(resolved, out string? firstSpelling))
                {
                    throw new InvalidOperationException(
                        $"Entity set '{name}': two authorization rules — Invoke(\"{firstSpelling}\", …) and " +
                        $"Invoke(\"{namedRule.BoundOperationName}\", …) — both target the bound operation " +
                        $"'{resolved}'. Named Invoke rules are matched case-insensitively and the LAST one " +
                        "declared wins, so the earlier rule is silently discarded and the order the two were " +
                        "written in decides whether the operation is protected. Keep exactly one rule per " +
                        "operation.");
                }

                claimedBy[resolved] = namedRule.BoundOperationName;
            }
        }

        // #199 Layer B: resource checks on a KEY-BASED route load the entity by key, so a Resource
        // requirement on such a route requires a GetById handler. Fail fast at startup.
        //
        // #486: this used to name Read/Update/Delete only -- three of the five categories that can
        // reach AttachResourceFilter. The filter also attaches on Create (the key-based
        // navigation-POST route) and on Invoke (entity-bound functions and actions), and it calls
        // InvokeGetByIdAsync, i.e. `GetById!.Invoke(...)`. So `.Create(c => c.RequireResource())`
        // beside a nav-POST handler, or `.Invoke(i => i.RequireResource())` beside an entity-bound
        // operation, passed startup and then NullReferenced on 100% of requests -- the generic 500
        // envelope. It fails closed (nothing is exposed), but it is exactly the configuration this
        // guard exists to make unreachable.
        //
        // The condition below asks the question the filter asks: does this profile register a
        // key-based route in a category whose rule carries a Resource requirement? The
        // COLLECTION-level members of those two categories are deliberately excluded: the collection
        // POST evaluates its Create requirement inline against the deserialized model (never through
        // GetById), so it needs none, and a collection-bound operation's route has no {key} segment
        // for the filter to read at all -- which the #690 check below answers on its own terms.
        if (operationAuthRules is not null && !source.HasGetById)
        {
            if (CategoryHasResource(OhDataOperation.Read, null)
                || CategoryHasResource(OhDataOperation.Update, null)
                || CategoryHasResource(OhDataOperation.Delete, null))
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': resource-based authorization (.RequireResource()) on Read/Update/Delete " +
                    "requires a GetById handler to load the entity for the check.");
            }

            if (CategoryHasResource(OhDataOperation.Create, null)
                && source.NavigationRoutes.FirstOrDefault(n => n.PostChild is not null) is { } resourceNavPost)
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': resource-based authorization (.RequireResource()) on Create " +
                    $"requires a GetById handler. The POST route of navigation property " +
                    $"'{resourceNavPost.PropertyName}' (POST /{name}({{key}})/{resourceNavPost.PropertyName}) " +
                    "is key-based, so the check loads the parent entity by key before running. Add a " +
                    "GetById handler, drop the navigation's post handler, or scope the requirement to " +
                    "the categories that do not need one.");
            }

            // Invoke rules can be scoped to a single operation name (Invoke("Name", ...)), so the
            // question is asked per entity-level operation rather than once for the category.
            BoundOperationDefinition? resourceEntityOp = source.BoundFunctions.Concat(source.BoundActions)
                .FirstOrDefault(o => o.IsEntityLevel && CategoryHasResource(OhDataOperation.Invoke, o.Name));
            if (resourceEntityOp is not null)
            {
                string opKind = resourceEntityOp.IsAction ? "action" : "function";
                string opMethod = resourceEntityOp.IsAction ? "POST" : "GET";
                throw new InvalidOperationException(
                    $"Entity set '{name}': resource-based authorization (.RequireResource()) on Invoke " +
                    $"requires a GetById handler. Entity-bound {opKind} '{resourceEntityOp.Name}' " +
                    $"({opMethod} /{name}({{key}})/{resourceEntityOp.Name}) is key-based, so the check " +
                    "loads the entity by key before the operation runs. Add a GetById handler, or use " +
                    "the coarse requirements (RequireAuthenticatedUser/RequireRole/RequireClaim/" +
                    "RequirePolicy) for this operation.");
            }
        }

        // #690/#696: a Resource requirement is evaluated against the entity loaded from the route's
        // {key} segment, and a collection-bound operation is mapped as /{Set}/{Name} with no key, so
        // on that route the requirement is dropped.
        //
        // The question is what the ROUTE is left enforcing, asked per collection-bound operation
        // against the rule that governs it: a route left enforcing nothing is refused, and coarse
        // requirements that still gate it are warned about.
        foreach (BoundOperationDefinition op in source.BoundFunctions.Concat(source.BoundActions))
        {
            if (op.IsEntityLevel) continue;

            // The route resolves its own rule exactly this way, so a rule that lost last-write-wins
            // or was displaced by a named one is never the one asked about.
            OperationAuthRule? rule = ResolveOperationRule(OhDataOperation.Invoke, op.Name);
            if (rule is null || rule.AllowAnonymous) continue;
            if (!rule.Requirements.Any(r => r.Kind == AuthRequirementKind.Resource)) continue;

            string spelling = rule.BoundOperationName is null
                ? "Invoke(...)"
                : $"Invoke(\"{rule.BoundOperationName}\", ...)";
            string opKind = op.IsAction ? "action" : "function";
            string route = $"{(op.IsAction ? "POST" : "GET")} /{name}/{op.Name}";

            if (!rule.Requirements.Any(r => r.Kind != AuthRequirementKind.Resource))
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': the authorization rule {spelling} declares " +
                    ".RequireResource() and nothing else, and it governs the collection-bound " +
                    $"{opKind} '{op.Name}' ({route}), whose route carries no key. Resource-based " +
                    "authorization evaluates the requirement against the entity loaded from the " +
                    "route's key segment, so on this route the requirement is dropped and NO " +
                    "requirement is enforced at all. Add a coarse requirement to the rule " +
                    "(RequireAuthenticatedUser/RequireRole/RequireClaim/RequirePolicy), declare the " +
                    "operation with BindEntityFunction/BindEntityAction so it carries a key to " +
                    "authorize against, or scope the rule with Invoke(\"Name\", ...) to the " +
                    "operations that have one.");
            }

            // Each placeholder appears EXACTLY once -- Microsoft.Extensions.Logging binds a template
            // positionally. Repeated VALUES are passed again under a distinct name.
            logger?.LogWarning(
                "OhData: '{EntitySet}' authorizes the collection-bound {OperationKind} '{Operation}' " +
                "with {Rule}, which declares .RequireResource() -- and that half is NOT applied on " +
                "'{Route}'. Resource-based authorization evaluates the requirement against the entity " +
                "loaded from the route's key segment, and a collection-bound operation is mapped " +
                "without one, so there is no instance to evaluate against. The rule's coarse " +
                "requirements DO gate this route, so what is silently dropped is the narrowing, not " +
                "the gate: a caller who satisfies them reaches '{Operation2}' whatever a resource " +
                "handler would have said about any row. If that instance check matters here, declare " +
                "the operation with BindEntityFunction/BindEntityAction so it carries a key, or scope " +
                "the rule with Invoke(\"Name\", ...) to the operations that have one. If the coarse " +
                "requirements are the whole intent for '{Operation3}', drop .RequireResource() from " +
                "the rule that governs it and this warning stops. See docs/authorization.md.",
                name, opKind, op.Name, spelling, route, op.Name, op.Name);
        }

        // #465: a Search handler on a Priority-1 profile is DEAD CODE, and used to be advertised
        // in the route's OpenAPI description while never being invoked. Refused at startup rather
        // than silently ignored, for the same reason every other dead-configuration check in this
        // file throws: a handler the framework will never call is a bug in the profile, and the
        // only moment it is cheap to find is startup.
        //
        // Why the framework cannot invoke it here instead. On the GetQueryable and GetAll paths
        // Search REPLACES the source collection and the framework then applies $filter/$orderby/
        // $top/$skip on top of the result. The Priority-1 contract is the opposite: the profile
        // receives ODataQueryOptions and owns the whole pipeline (see InvokeGetODataQueryableAsync).
        // There is no seam to feed a search-derived source INTO that -- honouring $search here
        // would mean bypassing the profile entirely, which (a) drops $filter/$orderby on the floor
        // for exactly the requests that carry $search, reproducing this defect one option over,
        // and (b) routes around whatever row-level scoping the profile's handler applies, which is
        // one of the main reasons to reach for Priority-1 in the first place. $search is therefore
        // the profile's own business on this path, reachable as options.Search inside
        // GetODataQueryable, exactly like every other option it is handed.
        if (source is IODataEntitySetEndpointSource searchCheckSource
            && searchCheckSource.HasGetODataQueryable && source.HasSearch)
        {
            throw new InvalidOperationException(
                $"Entity set '{name}': a Search handler is configured alongside GetODataQueryable, " +
                "but the Priority-1 read path never invokes it -- that path hands the full " +
                "ODataQueryOptions to the profile and the profile applies them itself. Remove the " +
                "Search handler and honour options.Search inside GetODataQueryable, or move the " +
                "entity set to the GetQueryable read path, where Search is invoked by the framework.");
        }

        // Priority 1: ODataEntitySetProfile with direct ODataQueryOptions handler
        if (source is IODataEntitySetEndpointSource odataSource && odataSource.HasGetODataQueryable)
        {
            // #475: this route's implemented set is the PROFILE's declaration, not a framework
            // constant -- it is the one shape where the handler, not the framework, decides which
            // options are honoured. Built once here, not per request.
            string[] p1ImplementedOptions =
                QueryOptionGate.BuildPriority1ImplementedOptions(odataSource.HonouredQueryOptions);

            var collReadP1Rb = entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    IResult? capabilityError = QueryOptionGate.CheckCollectionQueryOptionCapabilities(ctx, source, p1ImplementedOptions);
                    if (capabilityError is not null) return capabilityError;

                    var s = ResolveHandlers(ctx);
                    var odataSrc = (IODataEntitySetEndpointSource)s;
                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }

                    // #385: refuse a literal zero divisor BEFORE execution, so every provider gives
                    // the same answer instead of three (400 / 200-empty / 500).
                    if (QueryOptionGate.FindLiteralZeroDivisor(options) is { } zeroDivisorOption)
                    {
                        return ODataError(400, "InvalidQueryOption",
                            $"The {zeroDivisorOption} expression divides by the literal 0, which cannot " +
                            "be evaluated.");
                    }
                    // B1 fix: enforce FilterProperties/OrderByProperties/SelectProperties/
                    // ExpandProperties allowlists before handing options to the profile — the
                    // profile's own ApplyTo call has no opportunity to reject a disallowed
                    // property since it never calls Validate() itself.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
                    // #254: reject a nested $top above MaxExpandTop at any depth.
                    IResult? nestedTopError = QueryOptionGate.ValidateNestedTopCeiling(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                    if (nestedTopError is not null) return nestedTopError;
                    // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                    // level. Depth alone does not bound translation cost; breadth multiplies on top.
                    IResult? breadthError = QueryOptionGate.ValidateExpandBreadth(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                    if (breadthError is not null) return breadthError;
                    // #195: reject $top > MaxTop before invoking the profile. The Priority-1 path
                    // delegates query application to the profile, so without this guard a client
                    // could request an arbitrarily large page. Mirrors the Priority-2 path.
                    if (options.Top is not null && source.MaxTop.HasValue &&
                        options.Top.Value > source.MaxTop.Value)
                    {
                        return ODataError(400, "InvalidQueryOption",
                            $"The value of '$top' ({options.Top.Value}) exceeds the maximum allowed value ({source.MaxTop.Value}).");
                    }

                    var odataResult = await AsHandlerFault(WithExceptionMapping(odataSrc.InvokeGetODataQueryableAsync(options, ct), source, ctx, OhDataOperation.Read));
                    var queryable = odataResult.Items is IQueryable<TModel> typedQ
                        ? typedQ
                        : odataResult.Items.Cast<TModel>().AsQueryable();

                    // #195: framework-side safety cap. A Priority-1 profile owns query application,
                    // but if it does not page itself (no NextLink) and the client sent no $top, bound
                    // the materialized set to MaxTop and emit a continuation -- so it can never be
                    // coerced into an unbounded result. Neither case caps twice.
                    //
                    // #360: the continuation offset rides the framework-private FrameworkSkipOption and
                    // is applied HERE, on top of the profile's ApplyTo. Not $skip (which the framework
                    // emitted but never applied, so a profile ignoring the options served the same page
                    // forever) and not $skiptoken (which ApplyTo throws on).
                    //
                    // #244: no stabilizing order is injected before the cap Take, unlike Priority-2
                    // where the framework owns skip/take. Here the profile owns the pipeline including
                    // any $skip, so ordering after it would sort a sliced subset and ordering only the
                    // first page would misalign the continuation offset. Deterministic nextLink paging
                    // is the profile's responsibility; EF Core's warning 10102 already surfaces the
                    // omission.
                    string? frameworkNextLink = null;
                    int? appliedPageSize = null;
                    int frameworkSkip = 0;
                    if (odataResult.NextLink is null)
                    {
                        // The offset is read and applied whether or not the client also sent $top —
                        // it is gated only on the profile not paging itself. It used to sit inside
                        // the "$top is null" guard below, so a request carrying both the framework
                        // token and a $top dropped the offset silently and rewound to the first
                        // page. $top only decides whether the framework CAPS and emits a further
                        // continuation; it never means "forget where this walk had got to".
                        //
                        // The framework is the only thing that reads this option (it is invisible to
                        // ODataQueryOptions.ApplyTo), so applying it here cannot double up with the
                        // profile's own $skip, and with $top absent this is byte-identical to
                        // applying it inside the guard: same Skip, same position relative to Take.
                        //
                        // Caveat, and the reason a $top on a continuation is still out of contract:
                        // this Skip composes AFTER whatever the profile already applied, so when the
                        // profile honours $top the offset lands on the profile's already-taken
                        // window rather than ahead of it. That is unavoidable on a path where the
                        // profile owns ApplyTo, and @odata.nextLink is opaque by spec (§11.2.5.7) —
                        // a client is not entitled to graft query options onto one. What matters is
                        // that the offset is never silently discarded.
                        if (!TryReadFrameworkSkip(ctx, out frameworkSkip))
                        {
                            return ODataError(400, "InvalidSkipToken",
                                "The continuation token is invalid or has been corrupted.");
                        }
                        if (frameworkSkip > 0)
                            queryable = queryable.Skip(frameworkSkip);
                    }

                    if (odataResult.NextLink is null && options.Top is null)
                    {
                        int? preferredPageSize = ParseMaxPageSize(ctx);
                        appliedPageSize = preferredPageSize.HasValue
                            ? (source.MaxTop.HasValue
                                ? Math.Min(preferredPageSize.Value, source.MaxTop.Value)
                                : preferredPageSize.Value)
                            : source.MaxTop;

                        // #360: fetch ONE row past the page so a full final page is distinguishable
                        // from a full page with more behind it, WITHOUT a second round-trip to count
                        // the total (the whole point of this path is that the profile's provider
                        // executes exactly one query). The probe row is trimmed off below.
                        if (appliedPageSize.HasValue)
                        {
                            queryable = queryable.Take(appliedPageSize.Value == int.MaxValue
                                ? int.MaxValue
                                : appliedPageSize.Value + 1);
                        }
                        if (preferredPageSize.HasValue)
                            ctx.Response.Headers["Preference-Applied"] = $"{MaxPageSizePreference}={appliedPageSize!.Value}";
                    }

                    // #662 does not reclassify here: the profile applied the options itself, so
                    // the framework composed nothing it could attribute a failure to.
                    object[] items = QueryOptionGate.EvaluateQueryWithArithmeticFaultGuard(
                        () => queryable.ToArray(), options, logger, source.EntitySetName);

                    // #360: a continuation only when the probe row proves more rows exist — an
                    // exactly-full FINAL page (rows % pageSize == 0) no longer gets a nextLink that
                    // walks a client into an empty trailing page. The next offset is the
                    // framework-applied offset on this request plus the page just returned; the
                    // client's own $skip rides along unchanged in the link and is re-applied by the
                    // profile (or not) identically on every hop.
                    if (appliedPageSize is int ps && ps > 0 && items.Length > ps)
                    {
                        items = items[..ps];
                        frameworkNextLink = BuildFrameworkSkipLink(ctx, frameworkSkip + ps);
                    }

                    var (finalItems, selectedProps) = await ExpandEngine.ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix(name, selectedProps)}";
                    // #379: $count=true with no TotalCount fell back to items.Length -- the PAGE
                    // length, measured AFTER the framework's own Take cap above. On MaxTop = 50 over
                    // 10,000 rows that reported "@odata.count": 50 under a 200, so a paging UI showed
                    // one page instead of 200. §11.2.6.5 wants the count of items matching the
                    // request, explicitly unaffected by $top/$skip.
                    //
                    // The fallback is not wrong in general -- it is wrong exactly when paging moved
                    // the page away from the full set, which is why it went unnoticed: with no $top,
                    // no $skip and no cap, items IS the filtered set and the number is right. So the
                    // condition is measured rather than refused wholesale. Refusing wholesale would
                    // fail the canonical Priority-1 shape (ApplyTo + Items, no TotalCount) on every
                    // $count request, which is a worse trade than the defect for the many profiles
                    // whose sets never page.
                    //
                    // When paging COULD have truncated, the total is unknowable here -- the profile
                    // applied the paging and the framework never saw the pre-paging source -- so it
                    // throws. 500, not 501: since #475 a Priority-1 profile DECLARES whether it
                    // honours $count, and reaching this line means it declared that it does, so the
                    // condition is decided entirely by server-side state. Blaming the client would be
                    // the defect #496 removed one route over, and the shape matches #496's ruling on
                    // a Post handler returning null -- after the framework's own checks this can only
                    // be the handler breaking its contract.
                    //
                    // A profile that cannot count has a clean way out that #475 created: drop Count
                    // from HonouredQueryOptions and $count is refused with 501 before reaching here.
                    if (options.Count?.Value == true)
                    {
                        bool pagingMayHaveTruncated =
                            frameworkNextLink is not null ||
                            odataResult.NextLink is not null ||
                            options.Top is not null ||
                            options.Skip is not null;

                        if (odataResult.TotalCount is null && pagingMayHaveTruncated)
                        {
                            throw new InvalidOperationException(
                                $"The profile for '{name}' answered a $count=true request without setting " +
                                "ODataQueryResult.TotalCount, and this request was paged -- so the " +
                                $"returned page ({items.Length} item(s)) is not the total and the " +
                                "framework cannot recover it: the profile applied the paging and the " +
                                "pre-paging source never reached the framework. Set TotalCount to the " +
                                "pre-paging total, or remove OhDataSystemQueryOption.Count from " +
                                "HonouredQueryOptions so $count is refused with 501 instead.");
                        }

                        envelope["@odata.count"] = odataResult.TotalCount ?? (long)items.Length;
                    }

                    // nextLink: prefer the profile's own link; otherwise the framework continuation.
                    string? effectiveNextLink = odataResult.NextLink ?? frameworkNextLink;
                    if (effectiveNextLink is not null)
                    {
                        envelope["@odata.nextLink"] = effectiveNextLink;
                    }
                    envelope["value"] = finalItems;
                    return ODataEnvelopeResult(envelope, jsonOptions);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
                // #358: thrown only by EvaluateQueryWithArithmeticFaultGuard's narrow, guarded
                // materialize-site try above (queryable.ToArray()) — see that method's doc comment
                // for the full scope/guard rationale. This route does not control the Priority-1
                // profile's own ApplyTo call, only the enumeration of whatever IQueryable it hands
                // back.
                catch (QueryOptionGate.FilterArithmeticFaultException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            })
              .WithSummary($"List {name} (queryable)")
              .WithDescription(
                  "Returns entities via a profile-supplied IQueryable that the framework applies " +
                  "OData system query options to directly (Priority-1 read path). Live query " +
                  "options: $top, $skip" +
                  (source.FilterEnabled ? ", $filter" : "") +
                  (source.OrderByEnabled ? ", $orderby" : "") +
                  (source.SelectEnabled ? ", $select" : "") +
                  (source.ExpandEnabled ? ", $expand" : "") +
                  (source.CountEnabled ? ", $count" : "") + ".")
              .WithTags(name).Produces<ODataCollectionResponse<TModel>>(200).Produces(400).Produces(501)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: source.FilterEnabled,
                  OrderByEnabled: source.OrderByEnabled,
                  SelectEnabled: source.SelectEnabled,
                  ExpandEnabled: source.ExpandEnabled,
                  CountEnabled: source.CountEnabled,
                  // #465: the Priority-1 route has no $search leg -- there is nowhere to put one
                  // (see the startup guard above MapGet("") for the full argument), so it must not
                  // be advertised here. A Search handler on a Priority-1 profile is refused at
                  // startup, so source.HasSearch is provably false on this branch anyway; the
                  // literal states the route's contract rather than restating that.
                  SearchEnabled: false,
                  MaxTop: source.MaxTop,
                  TopSkipSupported: true));
            ApplyOperationAuth(collReadP1Rb, OhDataOperation.Read, keyBased: false);
        }
        // Priority 2: base GetQueryable (IQueryable without ODataQueryOptions)
        else if (source.HasGetQueryable)
        {
            var collReadP2Rb = entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    IResult? capabilityError = QueryOptionGate.CheckCollectionQueryOptionCapabilities(ctx, source, QueryOptionGate.s_collectionImplementedOptions);
                    if (capabilityError is not null) return capabilityError;

                    var s = ResolveHandlers(ctx);
                    var queryable = (IQueryable<TModel>)(await AsHandlerFault(WithExceptionMapping(s.InvokeGetQueryableAsync(ct), source, ctx, OhDataOperation.Read)))
                                    .Cast<TModel>();

                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }

                    // #385: refuse a literal zero divisor BEFORE execution, so every provider gives
                    // the same answer instead of three (400 / 200-empty / 500).
                    if (QueryOptionGate.FindLiteralZeroDivisor(options) is { } zeroDivisorOption)
                    {
                        return ODataError(400, "InvalidQueryOption",
                            $"The {zeroDivisorOption} expression divides by the literal 0, which cannot " +
                            "be evaluated.");
                    }
                    // B1 fix: enforce FilterProperties/OrderByProperties/SelectProperties/
                    // ExpandProperties allowlists before any ApplyTo call below.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
                    // #254: reject a nested $top above MaxExpandTop at any depth.
                    IResult? nestedTopError = QueryOptionGate.ValidateNestedTopCeiling(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                    if (nestedTopError is not null) return nestedTopError;
                    // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                    // level. Depth alone does not bound translation cost; breadth multiplies on top.
                    IResult? breadthError = QueryOptionGate.ValidateExpandBreadth(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                    if (breadthError is not null) return breadthError;

                    // Gap 4: $search on GetQueryable path — delegate to the Search handler, then
                    // apply remaining OData query options on top of the in-memory result set.
                    if (ctx.Request.Query.TryGetValue("$search", out var searchTermQ))
                    {
                        if (!source.HasSearch)
                        {
                            // 400, not 501: this route HAS a $search leg (the two lines below
                            // invoke it), so the functionality is implemented and merely
                            // unconfigured on this profile — the 400 side of the taxonomy above,
                            // the same shape as a false capability flag. $search on a route with
                            // no $search leg at all (/$count, GetById) is 501 by the sigil rule.
                            return ODataError(400, "UnsupportedQueryOption",
                                "This resource does not support $search. Configure the Search handler to enable it.");
                        }

                        var searchResults = await AsHandlerFault(WithExceptionMapping(s.InvokeSearchAsync(searchTermQ.ToString(), ct), source, ctx, OhDataOperation.Read));
                        var searchItems = searchResults.Cast<TModel>().AsQueryable();
                        // Continue with filter/orderby/top/skip on searchItems
                        queryable = searchItems;
                    }

                    long? odataCount = null;
                    if (options.Count?.Value == true)
                    {
                        var countQ = options.Filter is not null
                            ? (IQueryable<TModel>)options.Filter.ApplyTo(queryable, cachedCountSettings)
                            : queryable;
                        countQ = ApplyRoundingMode(countQ, source.RoundingMode);
                        odataCount = QueryOptionGate.CountRootQuery(
                            queryable, countQ, options, logger, source.EntitySetName);
                    }

                    // Apply filter/orderby/skip/top without $select so TModel shape is preserved.
                    // $select is handled via JsonNode post-processing to avoid ISelectExpandWrapper casing issues.
                    IQueryable<TModel> filtered = queryable;
                    bool sourceAlreadyOrdered = ResultOrderIsEstablished(queryable.Expression);
                    if (options.Filter is not null)
                        filtered = (IQueryable<TModel>)options.Filter.ApplyTo(filtered, cachedQuerySettings);
                    if (options.OrderBy is not null)
                        filtered = (IQueryable<TModel>)options.OrderBy.ApplyTo(filtered, cachedQuerySettings);
                    // #241: a deterministic total order is only needed when a row-limiting operator
                    // (Skip/Take/server-paging) will actually run — otherwise the full result set is
                    // returned and page order is moot, so an unbounded set (MaxTop=null, no $top/$skip/
                    // maxpagesize) is not burdened with a whole-table sort. When paging does engage,
                    // give it a stable order before any Skip/Take so the emitted LIMIT never rides an
                    // unordered scan (EF warning 10102) and @odata.nextLink boundaries are stable:
                    // append the entity key as a tiebreaker to a client $orderby; order by the key when
                    // neither the client nor the profile's own queryable established an order.
                    bool willRowLimit = options.Top is not null
                        || options.Skip is not null
                        || ctx.Request.Query.ContainsKey("$skiptoken")
                        || (options.Top is null && (source.MaxTop.HasValue || ParseMaxPageSize(ctx).HasValue));
                    if (willRowLimit)
                    {
                        filtered = EnsureStableOrder<TModel, TKey>(
                            filtered, options.OrderBy is not null, sourceAlreadyOrdered, source.KeyPropertyName);
                    }
                    // round() spec compliance (Part 2 §5.1.1.9): rewrite the Math.Round call nodes
                    // ApplyTo just emitted into the away-from-zero overload, unless the profile
                    // opted back into banker's rounding.
                    filtered = ApplyRoundingMode(filtered, source.RoundingMode);

                    // Gap 3: $skiptoken → treat as $skip when no $skip is present
                    int? tokenSkip = null;
                    if (options.Skip is null && ctx.Request.Query.TryGetValue("$skiptoken", out var tokenVal))
                    {
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(Uri.UnescapeDataString(tokenVal.ToString()));
                            tokenSkip = BitConverter.ToInt32(bytes, 0);
                        }
                        // Same throw set as TryReadFrameworkSkip — see the note there.
                        catch (Exception ex) when (ex is FormatException or ArgumentException)
                        {
                            return ODataError(400, "InvalidSkipToken",
                                "The skiptoken value is invalid or has been corrupted.");
                        }
                    }

                    // #360: an EXPLICIT client $skip counts toward the continuation offset too. It used
                    // to be applied to the query but left out of effectiveSkip, so the nextLink was
                    // computed as though the request had started at offset 0 and
                    // "GET /Set?$skip=10" with pageSize 10 linked straight back to row 10 — an
                    // infinite rewind. $skiptoken and $skip are mutually exclusive here by
                    // construction (tokenSkip is only read when options.Skip is null), and both
                    // express the SAME absolute offset, which is what BuildNextPageLink then
                    // re-encodes as the next $skiptoken.
                    int effectiveSkip = options.Skip?.Value ?? tokenSkip ?? 0;
                    if (options.Skip is not null)
                        filtered = (IQueryable<TModel>)options.Skip.ApplyTo(filtered, cachedQuerySettings);
                    else if (effectiveSkip > 0)
                        filtered = filtered.Skip(effectiveSkip);

                    // Batch 4 / M-4: Prefer: maxpagesize=N — client-requested page limit (§8.2.8.3).
                    // $top takes precedence over maxpagesize. When $top is absent, maxpagesize is
                    // capped at source.MaxTop rather than overriding it outright: MaxTop is a hard
                    // server-side ceiling (DoS protection), and a client preference must not be able
                    // to lift it. Per §8.2.8.7, Preference-Applied echoes the value the server actually
                    // honored, not the value the client asked for, so a clamped response still reports
                    // the true (smaller) page size rather than restating the client's request.
                    int? preferredPageSize = ParseMaxPageSize(ctx);
                    int? appliedPageSize = null; // only meaningful when $top is absent
                    if (options.Top is not null)
                    {
                        if (source.MaxTop.HasValue && options.Top.Value > source.MaxTop.Value)
                        {
                            return ODataError(400, "InvalidQueryOption",
                                $"The value of '$top' ({options.Top.Value}) exceeds the maximum allowed value ({source.MaxTop.Value}).");
                        }

                        filtered = (IQueryable<TModel>)options.Top.ApplyTo(filtered, cachedQuerySettings);
                    }
                    else
                    {
                        appliedPageSize = preferredPageSize.HasValue
                            ? (source.MaxTop.HasValue
                                ? Math.Min(preferredPageSize.Value, source.MaxTop.Value)
                                : preferredPageSize.Value)
                            : source.MaxTop;

                        // #360: fetch ONE row past the page. Whether the page is the last one is
                        // otherwise indistinguishable from a full page with more behind it when the
                        // row count is an exact multiple of the page size, and the only alternatives
                        // are a spurious trailing empty page (the old behaviour) or a second
                        // round-trip to LongCount the pre-paging total — which this path exists
                        // specifically to avoid (it never materializes; $count=true is the only thing
                        // that buys a count query, and that one is computed independently above and
                        // is unaffected). The probe row is trimmed off before serialization.
                        if (appliedPageSize.HasValue)
                        {
                            filtered = filtered.Take(appliedPageSize.Value == int.MaxValue
                                ? int.MaxValue
                                : appliedPageSize.Value + 1);
                        }
                        if (preferredPageSize.HasValue)
                            ctx.Response.Headers["Preference-Applied"] = $"{MaxPageSizePreference}={appliedPageSize!.Value}";
                    }

                    // #206: $select projection pushdown. When eligible, compose a member-init
                    // projection so the LINQ provider emits a column-pruned SELECT. The wire is
                    // unchanged either way: materialized objects are plain TModels and the
                    // existing JSON pipeline ($select trim, nav omission, ETag, expansion
                    // correlated by the always-projected key) runs identically. Ineligibility
                    // falls back silently to the full fetch (Debug-logged inside the helper).
                    // Extracted to a local so every $expand-pushdown fallback below reuses the
                    // exact same $select-only projection.
                    IQueryable<TModel> ApplySelectPushdown(IQueryable<TModel> q) =>
                        source.SelectPushdownEnabled &&
                        pushdownNamesUnambiguous &&
                        options.SelectExpand?.SelectExpandClause is { } selClause &&
                        ExpandEngine.ExtractSelectedProperties(selClause) is { } selNames
                            // #628: the EDM model is passed HERE too, not only on the $expand call
                            // sites below. Without it the polymorphic-root check inside cannot
                            // fire, and a member-init over the declared type erases the runtime
                            // identity @odata.type has to report -- measured: $select=Id on a
                            // polymorphic root emitted no annotation for the derived row.
                            ? ExpandEngine.TryApplySelectProjection(q, selNames, source, pushdownCtorOk,
                                pushdownStructuralByName, logger, edmModel: registration.EdmModel)
                            : q;

                    // #206 phase 2: multi-level $expand Include pushdown. Folds the delegate-less
                    // ($expand) navigations into the same member-init projection so one EF query loads
                    // them by JOIN, recursing per level while the deeper navigations are also
                    // delegate-less. Nested options are honoured at every level -- filter/orderby/paging
                    // to SQL, count/select on the serialized JSON.
                    //
                    // The delegate-safety invariant holds at EVERY depth: a branch is pushed only when
                    // it is delegate-less end-to-end, otherwise TryBuildEngagedExpand defers the whole
                    // branch. Gated to EF Core sources. Anything deferred or failing falls back to
                    // EDM-only for that request, exactly as before pushdown existed.
                    //
                    // #305: deliberately NOT gated on pushdownCtorOk, unlike ApplySelectPushdown -- this
                    // now feeds the Path A Include fallback when TryApplySelectProjection is ineligible,
                    // so it must be computed regardless of ctor eligibility.
                    List<ExpandEngine.EngagedExpand>? engagedExpandNavs = null;
                    // #305 fold-in: resolve the EF Core assembly ONCE here (short-circuited exactly like
                    // the old bool-returning IsEfCoreBacked gate it replaces) and reuse it below at the
                    // Path A Include-fallback call site instead of re-walking query.Provider a second time.
                    Assembly? efAssembly = null;
                    if (source.ExpandPushdownEnabled &&
                        pushdownNamesUnambiguous &&
                        options.SelectExpand?.SelectExpandClause is { } expandPlanClause &&
                        (efAssembly = ExpandEngine.ResolveEfCoreAssembly(filtered)) is not null)
                    {
                        foreach (ExpandedNavigationSelectItem expandItem in
                                 expandPlanClause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
                        {
                            string navName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;

                            // #206 ($levels): a $levels self-referential nav is excluded from
                            // pushdownExpandNavs (it is inherently cyclic), but a BOUNDED $levels
                            // projection is cycle-free, so resolve its binding on the fly here — skipping
                            // any delegate-backed nav (routeBackedNavNames) so its delegate is never bypassed.
                            ExpandEngine.ExpandNavBinding binding;
                            if (expandItem.LevelsOption is not null)
                            {
                                if (routeBackedNavNames.Contains(navName)) continue; // delegate-backed → delegate path
                                if (ExpandEngine.BuildLevelsNavBinding(typeof(TModel), navName) is not { } lb) continue;
                                binding = lb;
                            }
                            else if (!pushdownExpandNavs.TryGetValue(navName, out binding))
                            {
                                continue; // delegate-backed or non-pushable top-level nav → delegate/EDM path
                            }

                            if (ExpandEngine.TryBuildEngagedExpand(expandItem, binding, registration.EdmModel, registration,
                                    source.MaxExpansionDepth, out ExpandEngine.EngagedExpand engaged))
                            {
                                (engagedExpandNavs ??= new List<ExpandEngine.EngagedExpand>()).Add(engaged);
                            }
                            else
                            {
                                // #323: makes docs/query-options.md's existing "the reason is
                                // Debug-logged" claim true for $expand pushdown (previously no log was
                                // emitted at all here, unlike the analogous $select-pushdown skips
                                // above). TryBuildEngagedExpand defers for a structural reason — an
                                // unsupported nested option ($search/$compute/$apply), a nested level
                                // that is delegate-backed/cyclic/non-projectable, or the expansion depth
                                // budget — so the navigation stays EDM-only for this request rather than
                                // surfacing a 500.
                                logger?.LogDebug(
                                    "OhData: $expand pushdown deferred for {EntitySet}/{Nav}: navigation is not eligible for full pushdown at the requested depth/options; it stays EDM-only for this request.",
                                    source.EntitySetName, navName);
                            }
                        }
                    }

                    // #334: which top-level engaged expands carry their Nav@odata.count as an
                    // independent correlated scalar subquery, so the nested $skip/$top can bound the SQL
                    // fetch instead of deferring to the JSON pass:
                    //  - a COLLECTION nav (a reference has no count),
                    //  - carrying $count,
                    //  - a projection LEAF -- a level with children is projected element-wise further
                    //    down, and windowing it while also projecting a collection out of each element
                    //    is the APPLY/LATERAL shape SQLite cannot translate (#298/#304),
                    //  - not a $levels recursion (same reason, #300),
                    List<ExpandEngine.EngagedExpand>? carrierCounted = null;
                    if (engagedExpandNavs is { Count: > 0 })
                    {
                        foreach (ExpandEngine.EngagedExpand ce in engagedExpandNavs)
                        {
                            if (ce.Levels == 0 && ce.Binding.IsCollection && ce.Count
                                && ce.Children is not { Count: > 0 }
                                && (ce.Top is int || (ce.Skip is int cskip && cskip > 0)))
                            {
                                (carrierCounted ??= new List<ExpandEngine.EngagedExpand>()).Add(ce);
                            }
                        }
                        // More counted+windowed navs than the carrier has slots: fall back wholesale
                        // rather than carrying some counts and deferring others, so one request
                        // never mixes the two count sources.
                        if (carrierCounted is { Count: > ExpandEngine.ExpandCountCarrierSlots }) carrierCounted = null;
                    }
                    // Index-aligned with `items` below; re-indexed onto the serialized parents at the
                    // ShapePushedExpandsInJson call site.
                    Dictionary<PropertyInfo, int[]>? carrierCounts = null;

                    TModel[] items;
                    if (engagedExpandNavs is { Count: > 0 })
                    {
                        // Structural part of the projection: the $select set ONLY when $select
                        // pushdown is enabled AND a $select is present and eligible; else EVERY
                        // structural property. Expand pushdown must not column-prune on its own —
                        // that is $select-pushdown behavior the profile may have disabled
                        // (SelectPushdownEnabled=false), so the two capabilities stay independent (a
                        // pure $expand, or $expand under disabled select-pushdown, keeps all columns).
                        // Navigations are appended by TryApplySelectProjection; expanded nav
                        // identifiers ExtractSelectedProperties keeps are not structural and are
                        // skipped there, so they are never double-bound.
                        List<string> structuralNames =
                            source.SelectPushdownEnabled &&
                            options.SelectExpand!.SelectExpandClause is { } combClause &&
                            ExpandEngine.ExtractSelectedProperties(combClause) is { } combSelected
                                ? combSelected
                                : pushdownStructuralByName.Keys.ToList();

                        // #334: try the count-carrier projection FIRST. A null means either that no
                        // nav qualified, or that the root projection is ineligible for a member-init
                        // Select at all — in which case the request falls through to the unchanged
                        // path below (including, ultimately, the #305 Include fallback).
                        IQueryable<ExpandEngine.ExpandCountCarrier<TModel>>? carrierQuery =
                            carrierCounted is { Count: > 0 }
                                ? ExpandEngine.TryApplyCarrierProjection(
                                    filtered, structuralNames, source, pushdownCtorOk,
                                    pushdownStructuralByName, logger, engagedExpandNavs,
                                    registration.EdmModel, cachedBinderSettings, carrierCounted)
                                : null;

                        // #322: why the projection was ineligible, reported BY the eligibility checks
                        // rather than re-derived at the 400 below. Stays null on the success path.
                        string? projectionIneligibleReason = null;
                        IQueryable<TModel> pushedQuery = carrierQuery is not null
                            ? filtered // unused on the carrier path; keeps the reference check below false
                            : ExpandEngine.TryApplySelectProjection(
                                filtered, structuralNames, source, pushdownCtorOk, pushdownStructuralByName,
                                logger, engagedExpandNavs, registration.EdmModel, cachedBinderSettings,
                                r => projectionIneligibleReason = r);

                        if (carrierQuery is not null)
                        {
                            try
                            {
                                // #494: only the TRANSLATION of this query is a client-error
                                // candidate; a fault raised once rows start arriving is the
                                // server's. See TranslateThenMaterialize.
                                ExpandEngine.ExpandCountCarrier<TModel>[] carriers = QueryOptionGate.EvaluateQueryWithArithmeticFaultGuard(
                                    () => QueryOptionGate.TranslateThenMaterialize(() => carrierQuery), options, logger, source.EntitySetName);

                                // Unwrap IMMEDIATELY: `items` is a plain TModel[] from here on, so
                                // nothing downstream of materialization — the whole JSON shaping
                                // pipeline included — ever sees the carrier type.
                                items = new TModel[carriers.Length];
                                carrierCounts = new Dictionary<PropertyInfo, int[]>(carrierCounted!.Count);
                                for (int ci = 0; ci < carrierCounted.Count; ci++)
                                    carrierCounts[carrierCounted[ci].Binding.Property] = new int[carriers.Length];
                                for (int i = 0; i < carriers.Length; i++)
                                {
                                    items[i] = carriers[i].Entity;
                                    for (int ci = 0; ci < carrierCounted.Count; ci++)
                                        carrierCounts[carrierCounted[ci].Binding.Property][i] = carriers[i].Slot(ci);
                                }
                            }
                            catch (QueryOptionGate.QueryTranslationFailedException ex)
                            {
                                // Same fail-loud discipline (and the same message) as the ordinary
                                // pushdown execution site below — see its comment for why
                                // provider/infrastructure faults must NOT be relabelled 400 here.
                                logger?.LogWarning(ex.InnerException,
                                    "OhData: $expand count-carrier pushdown query failed to translate for {EntitySet}.",
                                    source.EntitySetName);
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{source.EntitySetName}' could not be processed: " +
                                    "the query shape produced by the requested nested options could not " +
                                    "be translated by the underlying data provider. Simplify the nested " +
                                    "$filter/$orderby/$top/$skip/$count combination, or write an expand " +
                                    "delegate for this navigation to take full control of its query shape.");
                            }
                        }
                        else if (ReferenceEquals(pushedQuery, filtered))
                        {
                            // #305 Path A ("serve, not silently drop"): the root projection is
                            // ineligible (e.g. no parameterless ctor / unknowable ETag / a complex-or-
                            // unsettable structural member), so TryApplySelectProjection could not fold
                            // engagedExpandNavs into a member-init Select. Before #305 this dropped to
                            // EDM-only (engagedExpandNavs = null) — the navigations then serialized
                            // whatever the CLR property's default value already was (typically an empty
                            // collection) under a lying 200. Now: serve the SAME engaged navigations via
                            // EF Core's own Include (bounded by MaxExpandTop exactly like the projection
                            // path — see ApplyIncludeFallback), or fail loud (400) for the one shape it
                            // still cannot fold through Include: $levels.
                            // #305 fold-in (review): validated here, OUTSIDE the try/catch around the
                            // actual Include construction+execution below, so this SPECIFIC actionable
                            // message reaches the client via the route's outer ODataException handler
                            // instead of being caught and overwritten by the generic provider-failure
                            // catch that wraps the real Include call.
                            //
                            // #616 narrowed this: a nested $expand is chained with ThenInclude now, so
                            // only $levels reaches the refusal.
                            if (ExpandEngine.FindLevelsExpand(engagedExpandNavs) is { } nestedNav)
                            {
                                // #322: same correction as the message above — name the check that
                                // actually failed, not the whole rule.
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{nestedNav.Binding.Property.Name}' could not be " +
                                    "served without a projection-eligible model: $levels under an " +
                                    "Include fallback is not supported, and " +
                                    $"'{typeof(TModel).Name}' is not projection-eligible because " +
                                    $"{projectionIneligibleReason ?? "its member-init projection could not be built"}. " +
                                    "Fix that to enable full pushdown, or write an expand delegate for " +
                                    "this navigation.");
                            }

                            // #323 (Change C) formerly rejected (400) a leaf expand whose element type
                            // navigates back to TModel here — the Include fallback populates TRACKED
                            // entities, so EF Core's own relationship fixup can wire up the back-reference
                            // and close a parent<->child object cycle System.Text.Json used to throw on.
                            // #325/#326 (OWNER DECISIONS, FROZEN spec — Option B) REMOVED that guard: the
                            // engaged navigations below now serialize through SerializeBounded
                            // (ApplyCollectionPipelineAsync Stage 1), which never hands an un-expanded
                            // navigation to System.Text.Json at all, so a reference cycle among these
                            // tracked entities — whether it closes back to the root (what this guard used
                            // to catch), between two sibling leaves, or inside a self-referential leaf
                            // element type (#326's two previously-still-500 classes) — is structurally
                            // unreachable. See IncludeFallbackSqliteTests.cs's IncludeFallbackCyclicLeafTests.

                            MethodInfo? efInclude = efAssembly is not null ? ExpandEngine.ResolveEfIncludeMethod(efAssembly) : null;
                            if (efInclude is null)
                            {
                                // The outer gate above already resolved efAssembly as non-null to reach
                                // this branch, so this should not happen against a genuine EF Core
                                // provider — fail loud rather than silently drop the navigations if it
                                // ever does.
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{source.EntitySetName}' could not be processed: " +
                                    "the underlying provider does not expose a usable Include API. " +
                                    "Write an expand delegate for this navigation instead.");
                            }

                            try
                            {
                                // #494: BUILDING the Include chain is part of the translation
                                // window, not a separate step — ApplyIncludeFallback constructs its
                                // query by reflection and deliberately unwraps its own
                                // TargetInvocationException so the provider's real exception type
                                // reaches the classifier. It therefore goes INSIDE the factory
                                // TranslateThenMaterialize treats as translation.
                                items = QueryOptionGate.EvaluateQueryWithArithmeticFaultGuard(
                                    () => QueryOptionGate.TranslateThenMaterialize(() => ApplySelectPushdown(
                                        ExpandEngine.ApplyIncludeFallback(
                                            filtered, engagedExpandNavs, efInclude,
                                            ExpandEngine.ResolveEfThenIncludeMethods(efAssembly!), registration.EdmModel,
                                            source.MaxExpandTop, cachedBinderSettings))),
                                    options, logger, source.EntitySetName);
                                // engagedExpandNavs stays SET (not nulled): the existing
                                // ShapePushedExpandsInJson pass below shapes nested
                                // $count/$select/$top/$skip exactly as it does for the projection path.
                            }
                            catch (QueryOptionGate.QueryTranslationFailedException ex)
                            {
                                // Same discipline as the translation-failure site below (S7: never
                                // leak ex.Message/provider details to the client): Include
                                // construction or translation failing — e.g. TModel is not an EF
                                // entity in this model, or this is not a tracking query — is a
                                // genuine capability gap, not something to paper over with missing
                                // data. A fault raised while the ROWS come back is not: it falls
                                // through to the group filter's 500.
                                logger?.LogWarning(ex.InnerException,
                                    "OhData: $expand Include fallback failed for {EntitySet}.",
                                    source.EntitySetName);
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{source.EntitySetName}' could not be processed: " +
                                    "the navigations could not be loaded via the underlying provider's " +
                                    "Include API. Write an expand delegate for this navigation to take " +
                                    "full control of its query shape.");
                            }
                        }
                        else
                        {
                            try
                            {
                                items = QueryOptionGate.EvaluateQueryWithArithmeticFaultGuard(
                                    () => QueryOptionGate.TranslateThenMaterialize(() => pushedQuery), options, logger, source.EntitySetName);
                            }
                            catch (QueryOptionGate.QueryTranslationFailedException ex)
                            {
                                // FAIL LOUD (owner directive): a folded $expand projection that fails
                                // to translate must not degrade to 200 with the affected navigations
                                // quietly empty -- that was #298's and #300's root cause, both now
                                // fixed at the source. Any other untranslatable combination is a real
                                // capability gap. Message stays generic per S7.
                                //
                                // #494: what reaches this catch is decided by WHEN the provider threw,
                                // not what it threw. The earlier type allowlist rested on the premise
                                // that a real infrastructure fault could only be a DbException or
                                // TimeoutException -- false: SqlClient reports pool exhaustion as a
                                // plain InvalidOperationException at enumeration, ObjectDisposedException
                                // derives from it, and so does EF's "a second operation was started".
                                // Each came back as 400 "simplify your query", telling client retry
                                // logic not to retry a retryable fault, while the same request without
                                // $expand correctly 500'd. TranslateThenMaterialize splits the phases
                                // at GetEnumerator/MoveNext.
                                //
                                // Warning, not Debug (which was invisible at production log levels).
                                logger?.LogWarning(ex.InnerException,
                                    "OhData: $expand pushdown query failed to translate for {EntitySet}.",
                                    source.EntitySetName);
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{source.EntitySetName}' could not be processed: " +
                                    "the query shape produced by the requested nested options could not " +
                                    "be translated by the underlying data provider. Simplify the nested " +
                                    "$filter/$orderby/$top/$skip/$count combination, or write an expand " +
                                    "delegate for this navigation to take full control of its query shape.");
                            }
                        }
                    }
                    else
                    {
                        // No expand pushdown — $select-only path. #662: the translation window is
                        // split out so an untranslatable client $filter/$orderby is the 400 the
                        // $expand path has answered since #494, not a 500.
                        items = QueryOptionGate.MaterializeRootQuery(
                            queryable, () => ApplySelectPushdown(filtered),
                            options, logger, source.EntitySetName);
                    }

                    // Gap 3: compute nextLink when MaxTop (or preferred page size) is set and page is full.
                    // #360: "full" now means the probe row fetched above actually came back — the page
                    // being exactly pageSize long proves nothing (rows % pageSize == 0 used to emit a
                    // link into an empty trailing page). Trim the probe row off before anything
                    // downstream sees it; the pipeline, ETags, expansion shaping and @odata.count (which
                    // is computed independently, pre-paging) are all unchanged by its existence.
                    string? nextLink = null;
                    int effectivePageSize = appliedPageSize ?? 0;
                    if (effectivePageSize > 0 && items.Length > effectivePageSize && options.Top is null)
                    {
                        items = items[..effectivePageSize];
                        int nextSkip = effectiveSkip + effectivePageSize;
                        string token = Convert.ToBase64String(BitConverter.GetBytes(nextSkip));
                        nextLink = BuildNextPageLink(ctx, token);
                    }

                    // #206 ($levels): the names of navigations this request actually PUSHED with $levels,
                    // so OmitUnexpandedNavigations keeps their bounded recursion (and ONLY theirs — a
                    // delegate-backed $levels nav is not pushed and must still be stripped beyond depth 1).
                    HashSet<string>? pushedLevelsNavNames = ExpandEngine.CollectPushedLevelsNavNames(engagedExpandNavs);

                    JsonArray finalItems;
                    List<string>? selectedProps;
                    try
                    {
                        // #464: engagedExpandNavs is threaded in so Stage 3.6's ceiling skips exactly
                        // the navigations ShapePushedExpandsInJson bounds (and, where #313 allows,
                        // pages) below — and bounds every OTHER expanded collection in the response,
                        // which on a non-EF source is all of them.
                        (finalItems, selectedProps) = await ExpandEngine.ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct, pushedLevelsNavNames, engagedExpandNavs);
                    }
                    catch (JsonException ex) when (engagedExpandNavs is { Count: > 0 })
                    {
                        // #305 Path B (FAIL LOUD, owner directive — supersedes the #206 fallback this
                        // replaces): a true object-graph cycle cannot be served at all, so rethrow: the
                        // group-level exception filter turns this into a generic 500 InternalServerError,
                        // never leaking the exception detail (or which navigation/shape tripped it) to
                        // the client. Belt-and-suspenders as of #325/#326 (Option B): SerializeBounded
                        // (ApplyCollectionPipelineAsync Stage 1) never hands an un-expanded EDM navigation
                        // to System.Text.Json, so a serialization cycle among EDM-declared navigations —
                        // whatever static back-reference shape it takes, including the sibling-
                        // cross-reference and self-referential-leaf classes #326 tracked — is structurally
                        // unreachable through this path now, on BOTH the member-init projection path
                        // (already true before #325/#326, via Change A) and the #305 Include fallback
                        // (newly true — see the FindCyclicLeafExpand removal note below
                        // FindLevelsExpand, immediately preceding ApplyIncludeFallback). This
                        // catch stays reachable for the one class #325's OWNER DECISIONS explicitly left
                        // as a loud 500 rather than fix: a cycle closed by an entity-typed CLR property
                        // that is NOT an EDM navigation (e.g. [NotMapped]) — SerializeBounded only
                        // suppresses/bounds EDM-declared navigations, so such a property still reaches
                        // System.Text.Json un-bounded on whichever branch of the walker serializes that
                        // level's structural/complex members. See T35 in SerializeBoundedWalkerTests.cs.
                        logger?.LogDebug(ex,
                            "OhData: $expand pushdown produced a serialization cycle for {EntitySet}.",
                            source.EntitySetName);
                        throw;
                    }

                    // #206 phase 2 (optioned expand): apply the JSON-side portion of each pushed
                    // expand's nested options — Nav@odata.count and count-deferred paging, plus nested
                    // $select projection — to the serialized parents. No-op unless a pushed expand
                    // actually carried $count or $select; the fallbacks above set engagedExpandNavs to
                    // null, so a request that abandoned pushdown does no shaping here.
                    string baseUrl = BuildBaseUrl(ctx, prefix);

                    if (engagedExpandNavs is { Count: > 0 })
                    {
                        // #313 stage 5: build the index-parallel (JsonObject, CLR entity) pair the link
                        // emission needs, and ONLY when this profile actually has a pageable navigation
                        // (the shipping default is an empty set, so this whole block is skipped and the
                        // call below is byte-identical to stage 3's). Built explicitly rather than
                        // relying on finalItems.OfType<JsonObject>() lining up with items positionally:
                        // the filter and the index would silently desynchronise if a page element ever
                        // failed to serialize to an object, and a link on the WRONG parent is worse than
                        // no link. See ExpandLevelAsync's items/jsonItems pair for the same idiom.
                        // #334 shares that index-parallel construction for exactly the same reason:
                        // the carrier's counts are positional against `items`, and a count attached
                        // to the WRONG parent is worse than no fix at all.
                        ExpandEngine.ExpandPagingContext? pagingCtx = null;
                        IEnumerable<JsonObject> shapeParents;
                        IReadOnlyDictionary<PropertyInfo, int[]>? shapeCounts = null;
                        PropertyInfo? parentKeyProp = expandPagingNavs.Count > 0
                            ? typeof(TModel).GetProperty(
                                source.KeyPropertyName,
                                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                            : null;
                        if (parentKeyProp is not null || carrierCounts is not null)
                        {
                            var pagingParents = new List<JsonObject>(finalItems.Count);
                            var pagingItems = new List<object>(finalItems.Count);
                            // The source index in `items` of each kept parent, so the carrier's
                            // counts can be re-indexed onto the (possibly shorter) parents list.
                            var sourceIndexes = new List<int>(finalItems.Count);
                            for (int i = 0; i < finalItems.Count && i < items.Length; i++)
                            {
                                if (finalItems[i] is not JsonObject parentObj) continue;
                                pagingParents.Add(parentObj);
                                pagingItems.Add(items[i]!);
                                sourceIndexes.Add(i);
                            }
                            shapeParents = pagingParents;
                            if (parentKeyProp is not null)
                            {
                                // #412: `preferredPageSize` is this request's Prefer: maxpagesize,
                                // already parsed above for the ROOT page. The same number governs the
                                // nested page — §8.2.8.5 scopes the preference to "each collection
                                // within the response", not to the top-level one — clamped down to
                                // MaxExpandTop at the emission site, never up.
                                pagingCtx = new ExpandEngine.ExpandPagingContext(
                                    baseUrl, name, parentKeyProp, pagingItems, expandPagingNavsByEdmName,
                                    preferredPageSize);
                            }
                            if (carrierCounts is not null)
                            {
                                var aligned = new Dictionary<PropertyInfo, int[]>(carrierCounts.Count);
                                foreach (KeyValuePair<PropertyInfo, int[]> nc in carrierCounts)
                                {
                                    int[] re = new int[sourceIndexes.Count];
                                    for (int j = 0; j < sourceIndexes.Count; j++) re[j] = nc.Value[sourceIndexes[j]];
                                    aligned[nc.Key] = re;
                                }
                                shapeCounts = aligned;
                            }
                        }
                        else
                        {
                            shapeParents = finalItems.OfType<JsonObject>();
                        }

                        ExpandEngine.ShapePushedExpandsInJson(
                            shapeParents, engagedExpandNavs, jsonOptions ?? _pascalCaseSerializerOptions,
                            source.MaxExpandTop, pagingCtx, shapeCounts);
                    }

                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix(name, selectedProps)}";
                    if (odataCount.HasValue) envelope["@odata.count"] = odataCount;
                    if (nextLink is not null) envelope["@odata.nextLink"] = nextLink;
                    envelope["value"] = finalItems;
                    return ODataEnvelopeResult(envelope, jsonOptions);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
                // #358: thrown only by EvaluateQueryWithArithmeticFaultGuard's narrow, guarded
                // materialize-site tries above (the odataCount LongCount(), and the three
                // ApplySelectPushdown/pushdown-expand ToArray() call sites) — see that method's
                // doc comment for the full scope/guard rationale, including why the AST-free CLR
                // fault detection here doesn't help on a real relational provider (tracked as a
                // separate follow-up issue).
                catch (QueryOptionGate.FilterArithmeticFaultException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            })
              .WithSummary($"List {name} (queryable)")
              .WithDescription(
                  "Returns entities via a profile-supplied IQueryable that the framework applies " +
                  "OData system query options to via ApplyTo (SQL pushdown for EF Core sources). " +
                  "Live query options: $top, $skip" +
                  (source.FilterEnabled ? ", $filter" : "") +
                  (source.OrderByEnabled ? ", $orderby" : "") +
                  (source.SelectEnabled ? ", $select" : "") +
                  (source.ExpandEnabled ? ", $expand" : "") +
                  (source.CountEnabled ? ", $count" : "") +
                  (source.HasSearch ? ", $search" : "") + ".")
              .WithTags(name).Produces<ODataCollectionResponse<TModel>>(200).Produces(400).Produces(501)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: source.FilterEnabled,
                  OrderByEnabled: source.OrderByEnabled,
                  SelectEnabled: source.SelectEnabled,
                  ExpandEnabled: source.ExpandEnabled,
                  CountEnabled: source.CountEnabled,
                  SearchEnabled: source.HasSearch,
                  MaxTop: source.MaxTop,
                  TopSkipSupported: true));
            ApplyOperationAuth(collReadP2Rb, OhDataOperation.Read, keyBased: false);
        }
        else if (source.HasGetAll)
        {
            var collReadAllRb = entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var s = ResolveHandlers(ctx);
                    logger?.LogDebug("GET {Prefix}/{Name}", prefix, name);

                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }

                    // Leg 1: $filter/$orderby are structurally unsupported on this path -- GetAll has
                    // no ApplyTo/IQueryable pipeline to push them to. $top/$skip are pure
                    // post-materialization Skip()/Take(), the same class as $select/$expand/$count
                    // below, so they are implemented rather than rejected.
                    //
                    // 501, not 400 -- CAN'T, not WON'T. With no IQueryable the framework cannot apply
                    // $filter under ANY configuration of this handler, so the refusal is
                    // flag-INDEPENDENT, which is why CheckCollectionQueryOptionCapabilities is called
                    // with checkFilterOrderBy:
                    if (options.Filter is not null || options.OrderBy is not null)
                    {
                        return ODataError(501, "UnsupportedQueryOption",
                            "This resource does not support $filter or $orderby. " +
                            "Configure GetQueryable to enable server-side query processing.");
                    }

                    // MaxTop caps an *explicit* $top exactly like the GetQueryable path (400
                    // InvalidQueryOption when exceeded).
                    if (options.Top is not null && source.MaxTop.HasValue && options.Top.Value > source.MaxTop.Value)
                    {
                        return ODataError(400, "InvalidQueryOption",
                            $"The value of '$top' ({options.Top.Value}) exceeds the maximum allowed value ({source.MaxTop.Value}).");
                    }

                    // B1 fix: the GetAll path routes $select/$expand/$count through the same
                    // ApplyCollectionPipelineAsync used by GetQueryable (see below), so those
                    // three options are functionally live here too and must respect their
                    // capability flags exactly like the other collection paths. $filter/
                    // $orderby are excluded from this check — they are rejected wholesale
                    // above regardless of flag state, since GetAll has no ApplyTo pipeline
                    // to push them down to. $top/$skip need no flag: they are always live,
                    // exactly like on the GetQueryable path.
                    IResult? capabilityError = QueryOptionGate.CheckCollectionQueryOptionCapabilities(
                        ctx, source, QueryOptionGate.s_getAllCollectionImplementedOptions, checkFilterOrderBy: false);
                    if (capabilityError is not null) return capabilityError;
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
                    // #254: reject a nested $top above MaxExpandTop at any depth. GetAll expands
                    // through delegates only (nested $top is not applied there), but the ceiling is a
                    // statement about what the client may ask for — same as the root MaxTop above.
                    IResult? nestedTopError = QueryOptionGate.ValidateNestedTopCeiling(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                    if (nestedTopError is not null) return nestedTopError;
                    // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                    // level. Depth alone does not bound translation cost; breadth multiplies on top.
                    IResult? breadthError = QueryOptionGate.ValidateExpandBreadth(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                    if (breadthError is not null) return breadthError;

                    // Post-materialization paging for GetAll, applied AFTER the handler call (GetAll
                    // or Search) fills the array and BEFORE $select/$expand serialization.
                    // @odata.count reflects the PRE-paging total (§11.2.5.5 — unaffected by
                    // $top/$skip), captured from the array length before paging.
                    //
                    // #201: an OMITTED $top is now capped to MaxTop (or a smaller Prefer:
                    // maxpagesize), with a $skip @odata.nextLink for the remainder — GetAll
                    // re-enumerates its source on each request, so offset paging is a valid
                    // continuation story (the same $skip scheme the Priority-1 path uses). This
                    // makes GetAll safe-by-default: it can no longer be coerced into returning an
                    // unbounded result set. Opt out by setting MaxTop = null (returns the full set,
                    // no nextLink). An EXPLICIT $top is taken as-is (already validated <= MaxTop
                    // above) and suppresses the default cap and its nextLink.
                    (object[] Paged, long PreTotal, string? NextLink) ApplyGetAllPaging(object[] items)
                    {
                        long preTotal = items.Length;
                        int effectiveSkip = options.Skip is { Value: > 0 } ? options.Skip.Value : 0;

                        IEnumerable<object> seq = items;
                        if (effectiveSkip > 0)
                            seq = seq.Skip(effectiveSkip);

                        int? appliedPageSize = null;
                        if (options.Top is not null)
                        {
                            seq = seq.Take(options.Top.Value);
                        }
                        else
                        {
                            int? preferredPageSize = ParseMaxPageSize(ctx);
                            appliedPageSize = preferredPageSize.HasValue
                                ? (source.MaxTop.HasValue
                                    ? Math.Min(preferredPageSize.Value, source.MaxTop.Value)
                                    : preferredPageSize.Value)
                                : source.MaxTop;
                            if (appliedPageSize.HasValue)
                                seq = seq.Take(appliedPageSize.Value);
                            if (preferredPageSize.HasValue)
                                ctx.Response.Headers["Preference-Applied"] = $"{MaxPageSizePreference}={appliedPageSize!.Value}";
                        }

                        object[] paged = ReferenceEquals(seq, items) ? items : seq.ToArray();

                        // nextLink only when the default cap was applied (omitted $top) and more
                        // items remain beyond this page. The pre-paging total lets us decide exactly.
                        string? nextLink = null;
                        if (appliedPageSize is int ps && ps > 0 && effectiveSkip + paged.Length < preTotal)
                            nextLink = BuildNextPageLinkWithSkip(ctx, effectiveSkip + paged.Length);

                        return (paged, preTotal, nextLink);
                    }

                    // Gap 4: $search on GetAll path
                    if (ctx.Request.Query.TryGetValue("$search", out var searchTerm))
                    {
                        if (!source.HasSearch)
                        {
                            // 400, not 501 — see the identical check on the GetQueryable path.
                            return ODataError(400, "UnsupportedQueryOption",
                                "This resource does not support $search. Configure the Search handler to enable it.");
                        }

                        var searchResults = await AsHandlerFault(WithExceptionMapping(s.InvokeSearchAsync(searchTerm.ToString(), ct), source, ctx, OhDataOperation.Read));
                        object[] searchItems = searchResults.ToArray();
                        var (pagedSearchItems, searchPreTotal, searchNextLink) = ApplyGetAllPaging(searchItems);

                        var (searchFinal, searchSelectedProps) = await ExpandEngine.ApplyCollectionPipelineAsync(pagedSearchItems, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);
                        string searchBaseUrl = BuildBaseUrl(ctx, prefix);
                        var searchEnvelope = new Dictionary<string, object?>();
                        searchEnvelope["@odata.context"] = $"{searchBaseUrl}/$metadata#{AppendSelectSuffix(name, searchSelectedProps)}";
                        // Batch 5: include @odata.count for search results when $count=true is
                        // requested. Leg 1: reflects the pre-paging total, per §11.2.5.5.
                        if (options.Count?.Value == true)
                            searchEnvelope["@odata.count"] = searchPreTotal;
                        if (searchNextLink is not null)
                            searchEnvelope["@odata.nextLink"] = searchNextLink;
                        searchEnvelope["value"] = searchFinal;
                        return ODataEnvelopeResult(searchEnvelope, jsonOptions);
                    }

                    object? result = await AsHandlerFault(WithExceptionMapping(s.InvokeGetAllAsync(ct), source, ctx, OhDataOperation.Read));
                    var enumerable = result as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    var rawItems = enumerable.ToArray();
                    var (pagedItems, preTotal, nextLink) = ApplyGetAllPaging(rawItems);

                    var (finalItems, selectedProps) = await ExpandEngine.ApplyCollectionPipelineAsync(pagedItems, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix(name, selectedProps)}";
                    // Batch 5 / Leg 1: §11.2.5.5 — include @odata.count when $count=true is
                    // requested on the GetAll path, reflecting the pre-paging total.
                    if (options.Count?.Value == true)
                        envelope["@odata.count"] = preTotal;
                    // #201: $skip continuation link when an omitted $top was capped to MaxTop.
                    if (nextLink is not null)
                        envelope["@odata.nextLink"] = nextLink;
                    envelope["value"] = finalItems;
                    return ODataEnvelopeResult(envelope, jsonOptions);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            })
              .WithSummary($"List {name} (simple read path)")
              .WithDescription(
                  "Returns the result of the GetAll handler. $top, $skip, $select, $expand, and " +
                  "$count are applied server-side, after materialization; $filter and $orderby are " +
                  "not supported on this path — configure GetQueryable to enable them. An omitted " +
                  "$top is capped to MaxTop (or a smaller Prefer: maxpagesize) with an " +
                  "@odata.nextLink for the remainder; set MaxTop=null to return the full set.")
              .WithTags(name).Produces<ODataCollectionResponse<TModel>>(200).Produces(400).Produces(501)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: false,
                  OrderByEnabled: false,
                  // B1 fix: $select/$expand/$count are functionally live on the GetAll path
                  // (routed through ApplyCollectionPipelineAsync above) and now enforced by
                  // CheckCollectionQueryOptionCapabilities, so the metadata should reflect the
                  // profile's actual flags instead of hardcoding "unsupported".
                  SelectEnabled: source.SelectEnabled,
                  ExpandEnabled: source.ExpandEnabled,
                  CountEnabled: source.CountEnabled,
                  SearchEnabled: source.HasSearch,
                  // Leg 1: $top is now live on this path and capped by MaxTop exactly like
                  // GetQueryable, so the doc metadata should advertise the same cap.
                  MaxTop: source.MaxTop,
                  TopSkipSupported: true));
            ApplyOperationAuth(collReadAllRb, OhDataOperation.Read, keyBased: false);
        }

        bool countSourceAppliesFilter = (source is IODataEntitySetEndpointSource odsCheck && odsCheck.HasGetODataQueryable)
            || source.HasGetQueryable;
        bool hasCountSource = countSourceAppliesFilter || source.HasGetAll;
        if (hasCountSource)
        {
            var countCollRb = entityGroup.MapGet("/$count", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    // #353: this route applies $filter and counts; it does not search or
                    // aggregate. It used to accept and DISCARD everything it does not apply, so
                    // `/$count?$search=alpha` answered 200 with the unfiltered total while the
                    // sibling collection route honoured (or rejected) the same option. The options
                    // §11.2.9 says MUST NOT affect a count are accepted and ignored -- that
                    // clause's specified behaviour, not a shortfall. See
                    // s_countRouteImplementedOptions for the partition and the $select/$format
                    // rulings.
                    IResult? countUnsupported =
                        QueryOptionGate.CheckUnsupportedSystemQueryOptions(ctx, QueryOptionGate.s_countRouteImplementedOptions);
                    if (countUnsupported is not null) return countUnsupported;

                    // B1 fix: $/count's own metadata advertises FilterEnabled: source.FilterEnabled
                    // (the only query option this route actually applies), so enforce it — a
                    // disabled $filter was previously applied unconditionally below.
                    IResult? countCapabilityError = QueryOptionGate.CheckDisabledQueryOption(
                        ctx, "$filter", source.FilterEnabled, nameof(IEntitySetEndpointSource.FilterEnabled));
                    if (countCapabilityError is not null) return countCapabilityError;

                    var s = ResolveHandlers(ctx);
                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }

                    // #385: refuse a literal zero divisor BEFORE execution, so every provider gives
                    // the same answer instead of three (400 / 200-empty / 500).
                    if (QueryOptionGate.FindLiteralZeroDivisor(options) is { } zeroDivisorOption)
                    {
                        return ODataError(400, "InvalidQueryOption",
                            $"The {zeroDivisorOption} expression divides by the literal 0, which cannot " +
                            "be evaluated.");
                    }
                    // B1 fix: enforce the FilterProperties allowlist here too.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);

                    if (s is IODataEntitySetEndpointSource odataCountSrc && odataCountSrc.HasGetODataQueryable)
                    {
                        // Priority 1 profiles apply query options themselves; don't re-apply $filter.
                        var countResult = await AsHandlerFault(WithExceptionMapping(odataCountSrc.InvokeGetODataQueryableAsync(options, ct), source, ctx, OhDataOperation.Read));
                        var queryable = countResult.Items is IQueryable<TModel> tq
                            ? tq
                            : countResult.Items.Cast<TModel>().AsQueryable();
                        // #662 does not reclassify here, as on the P1 collection read: the
                        // profile composed this queryable, so the framework has nothing to blame.
                        long odataQueryableCount = QueryOptionGate.EvaluateQueryWithArithmeticFaultGuard(
                            () => queryable.LongCount(), options, logger, source.EntitySetName);
                        return Results.Content(odataQueryableCount.ToString(), "text/plain");
                    }
                    if (source.HasGetQueryable)
                    {
                        var q = (IQueryable<TModel>)(await AsHandlerFault(WithExceptionMapping(s.InvokeGetQueryableAsync(ct), source, ctx, OhDataOperation.Read))).Cast<TModel>();
                        var filtered = options.Filter is not null
                            ? (IQueryable<TModel>)options.Filter.ApplyTo(q, cachedCountSettings)
                            : q;
                        filtered = ApplyRoundingMode(filtered, source.RoundingMode);
                        long queryableCount = QueryOptionGate.CountRootQuery(
                            q, filtered, options, logger, source.EntitySetName);
                        return Results.Content(queryableCount.ToString(), "text/plain");
                    }
                    if (options.Filter is not null)
                    {
                        // 501 for the same reason the sibling collection route's $filter/$orderby
                        // refusal is: this branch is reached only when the profile has no
                        // IQueryable source, so there is no filter code on the path and no flag
                        // that turns one on. Flag-independent => unimplemented => §9.3.1.
                        return ODataError(501, "UnsupportedQueryOption",
                            "$filter is not supported on this resource. Configure GetQueryable to enable server-side filtering.");
                    }

                    var items = await AsHandlerFault(WithExceptionMapping(s.InvokeGetAllAsync(ct), source, ctx, OhDataOperation.Read)) as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    // Fast path for ICollection (List, Array, etc.) — no enumeration needed.
                    long count = items is ICollection<TModel> coll
                        ? (long)coll.Count
                        : items.LongCount();
                    return Results.Content(count.ToString(), "text/plain");
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
                // #358: thrown only by EvaluateQueryWithArithmeticFaultGuard's narrow, guarded
                // LongCount() call sites above — see that method's doc comment for the full
                // scope/guard rationale.
                catch (QueryOptionGate.FilterArithmeticFaultException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces<long>(200, "text/plain").Produces(400).Produces(501)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  // #467 (F3): the GetAll fallback branch above returns 501 UnsupportedQueryOption
                  // for any $filter regardless of the flag -- there is no IQueryable to apply one
                  // to. Only the Priority-1 and GetQueryable branches actually honour it, so the
                  // advertisement is gated on the source, not on the flag alone. This is the same
                  // fix the sibling collection route already carries ("B1 fix", FilterEnabled:
                  // false on the GetAll route's metadata); /$count was missed.
                  FilterEnabled: source.FilterEnabled && countSourceAppliesFilter,
                  OrderByEnabled: false,
                  SelectEnabled: false,
                  ExpandEnabled: false,
                  // #467 (F2): CountEnabled means "the $count OPTION is honoured here", not "this
                  // route is a count". /$count returns a bare text/plain number: there is no
                  // envelope to carry an inline @odata.count. It used to be set true to say "this
                  // route IS the count", which the OpenAPI transformers -- the metadata's only
                  // consumers -- read as the other meaning and documented a $count query parameter
                  // that does nothing. $count is now REFUSED here rather than merely undocumented,
                  // so false is doubly right.
                  CountEnabled: false,
                  SearchEnabled: false,
                  MaxTop: null,
                  // #467 (F2): /$count applies neither $top nor $skip. It counts the whole set.
                  // The field means "this route HONOURS this option", so false is right even
                  // though both are ACCEPTED: §11.2.9 requires them to be present and IGNORED, and
                  // documenting a parameter that provably cannot change the response is the
                  // advertise-vs-serve mismatch #467 exists to remove. See
                  // s_countRouteImplementedOptions.
                  TopSkipSupported: false));
            ApplyOperationAuth(countCollRb, OhDataOperation.Read, keyBased: false);
        }

        if (source.HasGetById)
        {
            var rb = entityAuthGroup.MapGet($"/{name}({{key}})", async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("GET {Prefix}/{Name}({Key})", prefix, name, SanitizeLogValue(key));
                try
                {
                    // B1/S2 fix: $expand was previously advertised in this route's metadata
                    // (ExpandEnabled: source.ExpandEnabled) but silently ignored — 200 with no
                    // expansion, even for a nonexistent nav property. Enforce the flag like the
                    // collection routes, then actually expand below via the same pipeline the
                    // collection GET uses (batch-handler included), for context/serialization
                    // parity between GET /{Set} and GET /{Set}({key}).
                    // #380: this route implements $select and $expand and NOTHING else, and used
                    // to reject nothing at all — $filter/$orderby/$count/$top/$apply were each
                    // accepted and silently discarded under a 200, so a client that filtered a
                    // single entity reasonably concluded the filter had been applied. The check
                    // reads query-string KEYS only (no parsing, no ODataQueryOptions) and
                    // short-circuits on an empty query string, so the zero-cost no-option path
                    // this route is built around is preserved.
                    IResult? unsupportedOption =
                        QueryOptionGate.CheckUnsupportedSystemQueryOptions(ctx, QueryOptionGate.s_getByIdImplementedOptions);
                    if (unsupportedOption is not null) return unsupportedOption;

                    bool hasSelect = ctx.Request.Query.ContainsKey("$select");
                    bool hasExpand = ctx.Request.Query.ContainsKey("$expand");
                    IResult? selectCapabilityError = QueryOptionGate.CheckDisabledQueryOption(
                        ctx, "$select", source.SelectEnabled, nameof(IEntitySetEndpointSource.SelectEnabled));
                    if (selectCapabilityError is not null) return selectCapabilityError;
                    IResult? expandCapabilityError = QueryOptionGate.CheckDisabledQueryOption(
                        ctx, "$expand", source.ExpandEnabled, nameof(IEntitySetEndpointSource.ExpandEnabled));
                    if (expandCapabilityError is not null) return expandCapabilityError;

                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                    // M3: parse $select so the projected context ("#Set(prop1,prop2)/$entity")
                    // and the body it describes agree on shape. Constructing ODataQueryOptions
                    // costs a per-request parse, so skip it entirely unless $select or $expand is
                    // present — GetById is the hottest route and the no-option case must stay
                    // zero-cost.
                    ODataQueryOptions<TModel>? options = null;
                    List<string>? selectedProps = null;
                    if (hasSelect || hasExpand)
                    {
                        // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                        if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                                out options, out IResult? optionsError))
                        {
                            return optionsError;
                        }
                        // B1 fix: enforce SelectProperties/ExpandProperties allowlists.
                        ValidatePropertyAllowlists(options, cachedValidationSettings);
                        // #301: reject a nested $top above MaxExpandTop at any depth. GetById shares
                        // the same $expand inlining pipeline as the collection routes (batch handlers
                        // included, per the docs) but was missing this ceiling — mirrors the three
                        // collection-route call sites (Priority-1, GetQueryable, GetAll).
                        IResult? nestedTopError = QueryOptionGate.ValidateNestedTopCeiling(
                            options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                        if (nestedTopError is not null) return nestedTopError;
                        // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                        // level. Depth alone does not bound translation cost; breadth multiplies on top.
                        IResult? breadthError = QueryOptionGate.ValidateExpandBreadth(
                            options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                        if (breadthError is not null) return breadthError;
                        selectedProps = options.SelectExpand?.SelectExpandClause is not null
                            ? ExpandEngine.ExtractSelectedProperties(options.SelectExpand.SelectExpandClause)
                            : null;
                    }

                    object? result = await AsHandlerFault(WithExceptionMapping(s.InvokeGetByIdAsync(parsedKey!, ct), source, ctx, OhDataOperation.Read, key: parsedKey));
                    string? etagValue = null;
                    if (result is not null && source.HasETag)
                    {
                        // #496 finding 4: the profile's ETag selector is user code inside this
                        // route's `catch (ODataException)`. Inline rather than through
                        // AsHandlerFault's Task overload -- this one is synchronous, and a lambda
                        // would allocate a closure per request on the hottest read route.
                        try
                        {
                            etagValue = s.InvokeGetETag(result);
                        }
                        catch (Exception ex) when (HandlerFaultException.IsMisclassifiable(ex))
                        {
                            throw new HandlerFaultException(ex);
                        }
                        ctx.Response.Headers.ETag = $"\"{etagValue}\"";

                        // Gap 2: If-None-Match for conditional GET (§8.2.5)
                        if (ctx.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
                        {
                            var noneMatchList = ParseETagList(ifNoneMatch.ToString());
                            if (noneMatchList.Contains("*") || noneMatchList.Contains(etagValue))
                                return Results.StatusCode(304); // 304 Not Modified — no body
                        }
                    }
                    if (result is null)
                        return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    // Gap 5: include @odata.id in single-entity response
                    // Gap 2: include @odata.etag in body
                    // S4 fix: rebuild the key literal from the parsed CLR key (canonical, quoted
                    // + percent-encoded for string keys) rather than echoing the raw route
                    // segment -- the latter may carry decoded-but-unescaped characters (routing
                    // URL-decodes path segments before the handler sees them).
                    string odataId = BuildEntityId(ctx, prefix, name, parsedKey!);

                    if (hasExpand && options is not null)
                    {
                        // Reuse the collection pipeline (Serialize → ETag → Expand → Select) on a
                        // single-element array so GetById gets the same expand/batch-handler/
                        // select behavior as GET /{Set}, instead of a bespoke reimplementation.
                        var (expandedItems, expandSelectedProps) =
                            // #418/#463: `singleEntityRead: true` selects the single-entity
                            // remediation message. The ceiling itself now runs INSIDE the pipeline
                            // (Stage 3.6), at every level of the $expand tree — #418's own depth-1
                            // pass on this route is what #463 found the hole in.
                            await ExpandEngine.ApplyCollectionPipelineAsync(
                                new[] { result }, options, source, s, jsonOptions, rootEdmType,
                                registration, ctx.RequestServices, ct, singleEntityRead: true);
                        var entityBody = (JsonObject)expandedItems[0]!;

                        // Rebuild with @odata.context/@odata.id first (JSON §4.5: annotations
                        // precede the properties they describe). The pipeline's own ETag stage
                        // already put @odata.etag ahead of the entity's properties, so this
                        // preserves that ordering underneath context/id.
                        var node = new JsonObject
                        {
                            ["@odata.context"] = JsonValue.Create(
                                $"{BuildBaseUrl(ctx, prefix)}/$metadata#{AppendSelectSuffix(name, expandSelectedProps)}/$entity"),
                            ["@odata.id"] = JsonValue.Create(odataId),
                        };
                        foreach (var prop in entityBody.ToList())
                        {
                            entityBody.Remove(prop.Key);
                            node[prop.Key] = prop.Value;
                        }
                        return Results.Ok(node);
                    }

                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, registration.EdmModel, odataId: odataId, etag: etagValue, selectedProps: selectedProps, omitNavsForType: rootEdmType);
                }
                catch (ODataKeyFormatException ex)
                {
                    return BadKeyError(logger, ex, key, name);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404).Produces(501)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: false,
                  OrderByEnabled: false,
                  SelectEnabled: source.SelectEnabled,
                  ExpandEnabled: source.ExpandEnabled,
                  CountEnabled: false,
                  SearchEnabled: false,
                  MaxTop: null,
                  // #467 (F2): a single-entity read has nothing to page, so this route does not
                  // honour $top/$skip and must not document them. Since #380 it does not ignore
                  // them either — they are not in s_getByIdImplementedOptions and are refused with
                  // 501 UnsupportedQueryOption. The metadata value is the same; what changed is
                  // that "not documented" and "not accepted" now agree, which is the whole point of
                  // #467's rule that a field means "this route honours this option".
                  TopSkipSupported: false));
            ApplyOperationAuth(rb, OhDataOperation.Read);
        }

        // The TModel navigations withheld from a write handler when AllowDeepWrites is off (the
        // default). Computed once at startup. §11.4.2.2 deep insert / §11.4.3.1 deep update.
        //
        // #506: `SetMethod is not null` does NOT mean "public setter" -- it admits
        // `{ get; private set; }`, the standard EF shape. Keep it wide anyway: narrowing to a public
        // setter would exempt `[JsonInclude] { get; private set; }`, which STJ binds, and that OPENS
        // a hole. What makes the over-reach harmless is that the strip is GATED on the navigation
        // being named in the body, so it never fires for a member the client did not send.
        //
        // #457 hoisted this out of `if (source.HasPost)` -- it applied on the collection POST alone,
        // so deep UPDATE was documented out of scope but never enforced. One set, three routes:
        // re-deriving it at the PUT/PATCH sites is how the two would drift.
        //
        // #461: UNION with the EDM's navigation names. NavigationPropertyNames is the profile-
        // DECLARED set, so a convention-discovered navigation was not in the strip set at all and
        // reached the Post handler intact with AllowDeepWrites false. Union, not replacement --
        // NavigationPropertyNames also feeds Model B's DB/DL partitioning, whose "declares nothing"
        // category empties under convention sourcing.
        PropertyInfo[] deepWriteNavPropsToStrip = typeof(TModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null)
            .Where(p =>
            {
                string edmName = ODataPropertyNaming.ResolveEdmName(p);
                return source.NavigationPropertyNames.Contains(edmName)
                    || edmNavigationNames.Contains(edmName);
            })
            .ToArray();

        // PATCH's projection of the SAME set, by CLR property name — the delta loop has already
        // resolved each body key to a PropertyInfo, so it tests membership by name rather than
        // re-running the EDM-name resolution. Derived from deepWriteNavPropsToStrip rather than
        // recomputed, so there is only ever one answer to "is this a navigation" on the write path.
        // Ordinal because both sides are CLR member names produced by the same reflection walk.
        var deepWriteNavClrNames = new HashSet<string>(
            deepWriteNavPropsToStrip.Select(p => p.Name), StringComparer.Ordinal);

        // #506: the body-presence gate's lookup table — every JSON name that can NAME one of the
        // navigations above, mapped to the property it names. BuildBinderBodyNameTable owns the
        // derivation (and the reasoning); see it for why the primary key is the binder's own
        // contract name rather than the EDM name (#511).
        //
        // This is a lookup TABLE and not a call to FindClrPropertyByEdmName per body key on purpose.
        // That helper memoizes on (Type, string) in a process-wide ConcurrentDictionary keyed by the
        // exact string handed in, so calling it with client-supplied names would let a caller grow
        // that cache without bound. PATCH already does (pre-existing, and out of scope here); POST
        // and PUT must not join it.
        Dictionary<string, PropertyInfo> deepWriteNavByBodyName = BuildBinderBodyNameTable(
            Array.ConvertAll(
                deepWriteNavPropsToStrip,
                p => (ODataPropertyNaming.ResolveEdmName(p), p)),
            typeof(TModel),
            jsonOptions);

        // The gate only has work to do when there is something to strip AND the profile has not
        // opted in. Hoisted so neither write route pays a scan it would discard — a model with no
        // navigations at all (the common shape) never walks a body for this.
        bool deepWriteNavGateApplies = !source.AllowDeepWrites && deepWriteNavPropsToStrip.Length > 0;

        // #514: derived once per entity set at startup, because JsonDocumentOptions is an immutable
        // struct of three values and nothing about it varies per request.
        JsonDocumentOptions binderParityDocumentOptions =
            CreateBinderParityDocumentOptions(jsonOptions);

        // PATCH's body-name table. #510: bounded by the MODEL, because the memoizing
        // FindClrPropertyByEdmName caches on the caller's exact string in a process-wide dictionary,
        // so calling it per body key let a caller grow that dictionary without bound.
        //
        // #536: the primary key is JsonTypeInfo.Properties[].Name -- the string STJ itself matches a
        // body key against -- so the table cannot drift from the binder under a naming policy. Pair
        // JsonPropertyInfo back to PropertyInfo with HasSameMetadataDefinitionAs, never ==, which
        // compares ReflectedType and fails for inherited members (#462).
        //
        // EDM and CLR names stay as non-overwriting aliases, for FindClrPropertyByEdmName parity.
        // The comparer is OrdinalIgnoreCase unconditionally and deliberately does NOT follow
        // PropertyNameCaseInsensitive: this table IS the matcher for the root object, unlike
        // deepWriteNavByBodyName, which shadows a separate reader.
        var patchPropByBodyName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        PropertyInfo[] patchCandidateProps = typeof(TModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();

        JsonTypeInfo? patchWriteContract = TryResolveWriteContract(typeof(TModel), jsonOptions);
        if (patchWriteContract is not null)
        {
            foreach (JsonPropertyInfo contractProp in patchWriteContract.Properties)
            {
                if (contractProp.AttributeProvider is not PropertyInfo clrMember) continue;
                PropertyInfo? match = patchCandidateProps.FirstOrDefault(
                    candidate => clrMember.HasSameMetadataDefinitionAs(candidate));
                if (match is not null) patchPropByBodyName[contractProp.Name] = match;
            }
        }

        foreach (PropertyInfo p in patchCandidateProps)
            patchPropByBodyName.TryAdd(ODataPropertyNaming.ResolveEdmName(p), p);
        foreach (PropertyInfo p in patchCandidateProps)
            patchPropByBodyName.TryAdd(p.Name, p);

        // Local: the table's own accessor, so every PATCH-side resolution goes through one line.
        PropertyInfo? ResolvePatchBodyProperty(string bodyName) =>
            patchPropByBodyName.TryGetValue(bodyName, out PropertyInfo? found) ? found : null;

        // #355: the EDM's own Nullable="false" structural properties for this entity set, resolved
        // once at startup. Empty (so every check below is a length-0 loop) when the profile opts
        // out, which is the shape that keeps an opted-out registration paying nothing.
        EdmRequiredProperty[] edmRequiredProps = source.RequestBodyNullabilityValidationEnabled
            ? BuildEdmRequiredProperties(rootEdmType, typeof(TModel))
            : Array.Empty<EdmRequiredProperty>();

        // #544: the spellings a body could name those properties by — the same builder the
        // deep-write strip's table comes from, because the question is the same one. Empty (and
        // never consulted) when there is nothing to check, so the gate below costs an opted-out or
        // unconstrained entity set exactly one bool test per write.
        Dictionary<string, PropertyInfo> edmRequiredPropByBodyName = BuildBinderBodyNameTable(
            Array.ConvertAll(edmRequiredProps, p => (p.EdmName, p.Clr)),
            typeof(TModel),
            jsonOptions);
        bool edmRequiredGateApplies = edmRequiredProps.Length > 0;

        // #355: the same answer as a name set, for the structural-property write/delete routes,
        // which ask about ONE named property rather than validating a whole instance. Includes the
        // key (which edmRequiredProps deliberately excludes) because those routes do not reach the
        // key — it has its own KeyImmutableError stubs — and leaving it out here would be a claim
        // about the key that this set is not making.
        var edmNonNullablePropertyNames = new HashSet<string>(StringComparer.Ordinal);
        if (source.RequestBodyNullabilityValidationEnabled && rootEdmType is not null)
        {
            foreach (IEdmStructuralProperty edmProp in rootEdmType.StructuralProperties())
            {
                if (!edmProp.Type.IsNullable) edmNonNullablePropertyNames.Add(edmProp.Name);
            }
        }

        if (source.HasPost)
        {
            // If-None-Match on POST is not supported: the framework cannot extract the key from
            // the body without knowing the key property. Developers should handle this themselves.
            var rb = entityGroup.MapPost("", async (HttpContext ctx, CancellationToken ct) =>
            {
                if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);

                JsonDocument document;
                try
                {
                    // #514: read the body the way the binder reads it — see
                    // CreateBinderParityDocumentOptions.
                    document = await JsonDocument.ParseAsync(
                        ctx.Request.Body, binderParityDocumentOptions, ct);
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }

                using (document)
                {
                    // Deep insert (§32): `@odata.bind` (JSON §8.5 — link an existing entity) is
                    // documented non-support for 1.0.0. Detect and reject explicitly rather than
                    // silently ignoring it (which would look successful but not do what the
                    // client asked for). Use the $ref endpoints to link existing entities.
                    if (ContainsODataBindAnnotation(document.RootElement))
                    {
                        return ODataError(501, "NotImplemented",
                            "'@odata.bind' is not supported for POST " + $"/{name}. Use the $ref " +
                            "endpoints to link an existing entity, or enable AllowDeepWrites to " +
                            "create nested related entities inline (OData §11.4.2.2).");
                    }

                    // #389: a dynamic key that is not an OData simple identifier would otherwise be
                    // persisted verbatim and echoed on every later read -- a STORED fault for any
                    // other consumer, since '@odata.type' inside a complex value is what a
                    // conforming reader uses to resolve that value's type.
                    using PreparedWriteBody postPrepared =
                        PrepareWriteBody(registration, document.RootElement, typeof(TModel), jsonOptions);
                    if (postPrepared.Error is not null) return postPrepared.Error;

                    TModel? model;
                    try
                    {
                        model = postPrepared.Body.Deserialize<TModel>(jsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        return ODataError(400, "InvalidBody", ex.Message);
                    }

                    if (model is null)
                        return ODataError(400, "InvalidBody", "Request body is empty or could not be deserialized.");

                    // Deep insert (§32): strip nested navigation values unless the profile opted
                    // in via AllowDeepWrites. Nested values for non-navigation (plain) collection
                    // properties are untouched — only CLR properties the EDM or the profile calls
                    // navigations are stripped.
                    //
                    // #506 (BREAKING, and separate from the PUT regression the same issue fixes):
                    // only navigations the BODY NAMED are stripped. This loop was unconditional from
                    // 1.0.0, so a POST that mentioned no navigation still nulled every one of them —
                    // including a `{ get; private set; }` collection STJ never touched, which the
                    // model's constructor had initialized. That was always wrong for the same reason
                    // it is wrong on PUT (a body that sent no graph gives the strip nothing to
                    // prevent), and leaving POST unconditional while PUT is gated would put a
                    // per-verb divergence back into the exact surface this milestone spent ten PRs
                    // removing. Read against the strip's purpose it is a narrowing, not a widening:
                    // a client-sent nested graph is stripped exactly as before.
                    //
                    // Read off postPrepared.Body, not document.RootElement: the prepared element is
                    // what the deserializer below binds, and on the #398 control-information path
                    // the two differ. The gate must see the body the binder saw.
                    if (deepWriteNavGateApplies)
                    {
                        HashSet<string> bodyNavClrNames = CollectPresentBodyMemberClrNames(
                            postPrepared.Body, deepWriteNavByBodyName);
                        foreach (var navProp in deepWriteNavPropsToStrip)
                        {
                            if (bodyNavClrNames.Contains(navProp.Name)) navProp.SetValue(model, null);
                        }
                    }

                    // #355: the body must not contradict the type the framework's own $metadata
                    // publishes. Below the strip and above everything that reads the model, so the
                    // check sees exactly what the handler would have received — and above
                    // CheckResourceAuthAsync deliberately: a malformed body is a client error
                    // regardless of who sent it, and evaluating a resource policy against an
                    // instance the service already knows is invalid tells the policy nothing.
                    //
                    // #544: scoped to the properties this body NAMED. postPrepared.Body for the same
                    // reason the strip above reads it — it is the element the deserializer bound.
                    HashSet<string>? postRequiredNamed = edmRequiredGateApplies
                        ? CollectPresentBodyMemberClrNames(
                            postPrepared.Body, edmRequiredPropByBodyName)
                        : null;
                    IResult? postNullabilityFail =
                        ValidateEdmRequiredProperties(edmRequiredProps, model, postRequiredNamed);
                    if (postNullabilityFail is not null) return postNullabilityFail;

                    // #199 Layer B: resource-based Create auth runs against the incoming (pre-persist)
                    // entity — there is no stored row yet, so the collection POST cannot use the
                    // load-by-key filter (nav-POST, which has a {key}, checks against the parent instead).
                    IResult? createAuthFail = await CheckResourceAuthAsync(ctx, model, OhDataOperation.Create, boundOperationName: null);
                    if (createAuthFail is not null) return createAuthFail;

                    var s = ResolveHandlers(ctx);
                    logger?.LogDebug("POST {Prefix}/{Name}", prefix, name);
                    object? result = await WithExceptionMapping(s.InvokePostAsync(model, ct), source, ctx, OhDataOperation.Create, model: model);
                    if (result is null)
                    {
                        // #496 finding 1: a null return is a SERVER-side contract violation, not a
                        // statement about the request. This used to answer 400 "Post handler returned
                        // null." -- the client blamed for the server's bug, with the server's handler
                        // named back to it -- and it was the only 4xx null policy in the framework
                        // (GetAll -> 200 empty, GetById/PUT/PATCH -> 404, a bound operation -> 204).
                        //
                        // Throwing hands it to the group filter: real exception logged at Error, 500
                        // with the OData envelope and a generic message. Same invariant-assertion shape
                        // as DeltaFactory's TrySetPropertyValue.
                        //
                        // A profile that means to REJECT a create must do it BEFORE the handler: Post
                        // is typed Task<TModel?>, as is every entity-set handler delegate, so it cannot
                        // return an IResult and there is no "return an ODataError" idiom here. Use a
                        // Create authorization rule, RequestBodyNullabilityValidationEnabled, or throw.
                        throw new InvalidOperationException(
                            $"The Post handler for entity set '{name}' returned null. Post must " +
                            "return the created entity.");
                    }
                    string? postEtag = null;
                    if (source.HasETag)
                    {
                        postEtag = s.InvokeGetETag(result);
                        ctx.Response.Headers.ETag = $"\"{postEtag}\"";
                    }
                    // S4 fix: canonical, URL-safe key literal (quoted + percent-encoded for
                    // string keys) -- not InvokeGetKeyString, which returns the raw/unquoted
                    // form used elsewhere for body-vs-URL key equality comparisons.
                    string keyForUrl = s.InvokeGetKeyForUrl(result);
                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    string odataId = $"{baseUrl}/{name}({keyForUrl})";

                    // Gap 4: Prefer: return=minimal → 204 with Location header
                    if (PrefersMinimal(ctx))
                    {
                        ctx.Response.Headers.Location = odataId;
                        // §8.3.3: Content-Location on 204 mirrors the Location of the created entity.
                        ctx.Response.Headers["Content-Location"] = odataId;
                        // V1/§8.3.4: OData-EntityId is REQUIRED on any 204 response that creates an
                        // entity, since the client cannot recover the new entity's id from an empty body.
                        ctx.Response.Headers["OData-EntityId"] = odataId;
                        ctx.Response.Headers["Preference-Applied"] = "return=minimal";
                        return Results.NoContent();
                    }
                    else
                    {
                        // §8.3.3: Content-Location points to the canonical URL of the created resource.
                        ctx.Response.Headers["Content-Location"] = odataId;

                        EchoReturnRepresentationPreference(ctx);

                        // @odata.id and @odata.etag in the POST echo. Deep insert (§11.4.2.2): with
                        // AllowDeepWrites the handler's return value may carry nested navigation
                        // values, which SerializeToNode emits inline.
                        //
                        // #240: un-expanded navigations are omitted from the echo so it matches a read
                        // of the same type -- EXCEPT under deep writes, where the 201 deliberately
                        // echoes the created graph. The gate is entity-level because
                        // OmitUnexpandedNavigations strips ALL declared navigations unconditionally and
                        // never inspects whether one is populated.
                        var createdNode = ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, registration.EdmModel, odataId: odataId, etag: postEtag,
                            omitNavsForType: source.AllowDeepWrites ? null : rootEdmType);
                        return Results.Created(odataId, createdNode);
                    }
                }
            });
            rb.WithTags(name).Produces<TModel>(201).Produces(400).Produces(415).Produces(501)
              .WithMetadata(new OhDataRequestBodyMetadata
              {
                  BodyType = typeof(TModel),
                  Description = $"The {name} entity to create."
              });
            // #526: this is the COLLECTION POST ("/{name}") -- it carries no {key} segment, so
            // keyBased: true would attach a Layer B resource filter that reads
            // RouteValues["key"], finds nothing, and always calls next -- a per-request no-op.
            // The Create resource check for THIS route is already performed inline above, against
            // the deserialized model (CheckResourceAuthAsync), which is the only way to evaluate a
            // resource-based rule before the entity exists. Contrast the nav-POST create route
            // below (POST /{name}({key})/{nav}), which IS key-based and keeps keyBased: true.
            ApplyOperationAuth(rb, OhDataOperation.Create, keyBased: false);
        }

        if (source.HasPut)
        {
            var rb = entityAuthGroup.MapPut($"/{name}({{key}})", async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PUT {Prefix}/{Name}({Key})", prefix, name, SanitizeLogValue(key));
                if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);
                try
                {
                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    TModel? model;

                    // #506: which navigations the body NAMED, captured on whichever branch below
                    // holds the bytes — the JsonElement one has a prepared body, the streaming one
                    // has only #456's buffer, and both are scoped to their branch. Null when the
                    // gate does not apply (opted in, or nothing to strip), which is also the shape
                    // that keeps the branches from paying for a scan they would discard.
                    HashSet<string>? bodyNavClrNames = null;

                    // #544: and which required properties it named, captured on the same two
                    // branches and for the same reason. Null is "the body named nothing", which is
                    // the answer that reports nothing, so a branch that never assigns cannot
                    // accidentally report.
                    HashSet<string>? bodyRequiredNamed = null;

                    if (registration.OpenTypesActive)
                    {
                        // #389: dynamic-property names are policed BEFORE binding, and that check
                        // reads the raw JSON, so the body is buffered into a JsonDocument first.
                        // Only when the model actually HAS an open complex type -- otherwise PUT
                        // keeps streaming straight into the deserializer exactly as before, so
                        // nothing about the default path moves. Gating this on OpenTypesEnabled
                        // instead was the one measurable way an opted-in registration with no open
                        // types stopped being byte-identical to an opted-out one (#389 L1): the two
                        // reads report a malformed body differently, JsonDocument.ParseAsync
                        // omitting the "Path: $" that JsonSerializer.DeserializeAsync includes.
                        // #514: read the body the way the binder reads it — see
                        // CreateBinderParityDocumentOptions.
                        using JsonDocument putDocument = await JsonDocument.ParseAsync(
                            ctx.Request.Body, binderParityDocumentOptions, ct);
                        using PreparedWriteBody putPrepared = PrepareWriteBody(
                            registration, putDocument.RootElement, typeof(TModel), jsonOptions);
                        if (putPrepared.Error is not null) return putPrepared.Error;
                        // #506: the PREPARED element, for the same reason the collection POST reads
                        // it — it is what Deserialize binds on the next line.
                        if (deepWriteNavGateApplies)
                        {
                            bodyNavClrNames = CollectPresentBodyMemberClrNames(
                                putPrepared.Body, deepWriteNavByBodyName);
                        }
                        if (edmRequiredGateApplies)
                        {
                            bodyRequiredNamed = CollectPresentBodyMemberClrNames(
                                putPrepared.Body, edmRequiredPropByBodyName);
                        }
                        model = putPrepared.Body.Deserialize<TModel>(jsonOptions);
                    }
                    else
                    {
                        // #456: PUT is one of the two routes that never materialise the body here, so
                        // the '@odata.bind' check hoisted into PrepareWriteBody above cannot reach it
                        // -- PrepareWriteBody is not called on this branch at all. The body is copied
                        // once, scanned, and then handed to the SAME DeserializeAsync(Stream)
                        // overload as before, which is what keeps every malformed-body message
                        // identical to the streaming path (#389 L1: JsonDocument words it
                        // differently, and OpenTypeDefaultOnIsByteIdenticalTests pins the
                        // difference).
                        using MemoryStream putBuffered = await BufferRequestBodyAsync(ctx, ct);
                        if (ContainsODataBindAnnotation(
                                putBuffered.GetBuffer().AsSpan(0, (int)putBuffered.Length), jsonOptions))
                        {
                            return ODataBindNotImplementedError();
                        }

                        // #506: the second reader over the same buffer, and it must be as
                        // non-authoritative about malformed input as the first one — see
                        // CollectPresentBodyMemberClrNames(ReadOnlySpan<byte>). Reads GetBuffer()
                        // rather than the stream, so putBuffered.Position stays at 0 for the
                        // below and the DeserializeAsync(Stream) overload #389 L1 pins is
                        // untouched.
                        if (deepWriteNavGateApplies)
                        {
                            bodyNavClrNames = CollectPresentBodyMemberClrNames(
                                putBuffered.GetBuffer().AsSpan(0, (int)putBuffered.Length),
                                deepWriteNavByBodyName,
                                jsonOptions);
                        }

                        // #544: the same buffer, the same reader, the other table.
                        if (edmRequiredGateApplies)
                        {
                            bodyRequiredNamed = CollectPresentBodyMemberClrNames(
                                putBuffered.GetBuffer().AsSpan(0, (int)putBuffered.Length),
                                edmRequiredPropByBodyName,
                                jsonOptions);
                        }

                        model = await JsonSerializer.DeserializeAsync<TModel>(putBuffered, jsonOptions, ct);
                    }
                    if (model is null)
                        return ODataError(400, "InvalidBody", "Request body is empty or could not be deserialized.");

                    // #457 — deep update (§11.4.3.1): the same strip the collection POST applies,
                    // on the same set, for the same reason. Placed AFTER deserialization and
                    // BEFORE anything reads the model, so InvokeGetKeyString, the resource-auth
                    // gate, the Put handler and the AllowUpsert Post fallback all see one model.
                    // Deliberately below the two body scans above and not merged into either: this
                    // is a post-bind mutation of a CLR graph, while `@odata.bind` detection is a
                    // pre-bind read of the raw bytes whose ordering #456 pins.
                    // The key is never a navigation, so the key-mismatch check below is unaffected.
                    //
                    // #506 — THE REGRESSION HALF. This loop shipped unconditional, so PUT nulled
                    // navigations the body never mentioned, including ones System.Text.Json could
                    // not have bound (`{ get; private set; }` — PropertyInfo.SetMethod does not
                    // exclude a non-public accessor; see the strip set's own comment). Measured:
                    // `PUT {"id":1,"title":"t"}` handed the handler `Kids == null` where the
                    // constructor had put an empty list. #504 shipped to stop a CLIENT-SENT nested
                    // graph reaching the handler, and that is untouched — what is gated away is the
                    // case where the client sent no graph at all, which the strip was never for.
                    if (deepWriteNavGateApplies && bodyNavClrNames is not null)
                    {
                        foreach (var navProp in deepWriteNavPropsToStrip)
                        {
                            if (bodyNavClrNames.Contains(navProp.Name)) navProp.SetValue(model, null);
                        }
                    }

                    // #355/#544: an explicit null for a Nullable="false" property is refused here
                    // too. PUT is the ONE verb with a clause about omitted properties at all
                    // (§11.4.3), and that clause does not ask for a 400: it says a missing non-key,
                    // updatable structural property "MUST be set to [its] default value", which is
                    // the handler's business, not the boundary's. See ValidateEdmRequiredProperties.
                    // Grouped with the key-mismatch check below
                    // rather than after the precondition gate, following this route's existing
                    // ordering (body shape first, then If-Match).
                    IResult? putNullabilityFail =
                        ValidateEdmRequiredProperties(edmRequiredProps, model, bodyRequiredNamed);
                    if (putNullabilityFail is not null) return putNullabilityFail;

                    string bodyKeyStr = s.InvokeGetKeyString(model);
                    string parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                    if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                        return ODataError(400, "BadRequest", "Key in URL does not match key in request body.", target: "key");
                    var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                    if (etagCheck is not null) return etagCheck;

                    // m7: If-None-Match: * is a create-guard (§11.4.4 / RFC 7232) — "only if no
                    // current representation exists". Only meaningful when the profile supports
                    // upsert (otherwise PUT already 404s on a missing key with no ambiguity) and
                    // requires GetById to check existence before the write is attempted.
                    if (source.AllowUpsert && source.HasGetById
                        && ctx.Request.Headers.TryGetValue("If-None-Match", out var putIfNoneMatch)
                        && ParseETagList(putIfNoneMatch.ToString()).Contains("*"))
                    {
                        object? existingForGuard = await s.InvokeGetByIdAsync(parsedKey!, ct);
                        if (existingForGuard is not null)
                        {
                            return ODataError(412, "PreconditionFailed",
                                "If-None-Match: * precondition failed: a resource already exists at this key.");
                        }
                    }

                    object? result = await WithExceptionMapping(s.InvokePutAsync(parsedKey!, model, ct), source, ctx, OhDataOperation.Update, key: parsedKey, model: model);

                    // Gap 3: Upsert via PUT (§11.4.4) — create entity when result is null and AllowUpsert enabled
                    bool wasCreated = false;
                    if (result is null && source.AllowUpsert && source.HasPost)
                    {
                        result = await WithExceptionMapping(s.InvokePostAsync(model, ct), source, ctx, OhDataOperation.Create, model: model);
                        wasCreated = true;
                    }

                    if (result is null) return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    string? putEtag = null;
                    if (source.HasETag)
                    {
                        putEtag = s.InvokeGetETag(result);
                        ctx.Response.Headers.ETag = $"\"{putEtag}\"";
                    }

                    // Gap 4: Prefer: return=minimal → 204
                    if (PrefersMinimal(ctx))
                    {
                        ctx.Response.Headers["Preference-Applied"] = "return=minimal";
                        if (wasCreated)
                        {
                            // S4 fix: canonical, URL-safe key literal built from parsedKey (see GetById above).
                            string upsertOdataId = BuildEntityId(ctx, prefix, name, parsedKey!);
                            ctx.Response.Headers.Location = upsertOdataId;
                            // V1/§8.3.4: OData-EntityId is REQUIRED on the 204 response of an
                            // upsert-PUT that created the entity. A plain update-PUT must NOT
                            // carry this header — it only applies when a new entity was created.
                            ctx.Response.Headers["OData-EntityId"] = upsertOdataId;
                        }
                        return Results.NoContent();
                    }

                    EchoReturnRepresentationPreference(ctx);

                    // Gap 5: include @odata.id in PUT response
                    // Gap 2: include @odata.etag in body
                    // S4 fix: canonical, URL-safe key literal built from parsedKey (see GetById above).
                    string odataId = BuildEntityId(ctx, prefix, name, parsedKey!);
                    if (wasCreated)
                        return Results.Created(odataId, ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, registration.EdmModel, odataId: odataId, etag: putEtag, omitNavsForType: rootEdmType));
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, registration.EdmModel, odataId: odataId, etag: putEtag, omitNavsForType: rootEdmType);
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }
                catch (ODataKeyFormatException ex)
                {
                    return BadKeyError(logger, ex, key, name);
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404).Produces(415)
              .WithMetadata(new OhDataRequestBodyMetadata
              {
                  BodyType = typeof(TModel),
                  Description = $"The full {name} entity representation to replace the existing resource with."
              });
            ApplyOperationAuth(rb, OhDataOperation.Update);
        }

        if (source.HasPatch)
        {
            var rb = entityAuthGroup.MapMethods($"/{name}({{key}})", PatchMethod, async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PATCH {Prefix}/{Name}({Key})", prefix, name, SanitizeLogValue(key));
                if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);
                try
                {
                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                        ctx.Request.Body, jsonOptions, ct);

                    // BUG 2 fix: a syntactically valid JSON payload that isn't a JSON object (array,
                    // string, number, bool, null) would previously reach body.EnumerateObject() below,
                    // which throws InvalidOperationException for any non-Object JsonValueKind. That
                    // exception type isn't caught by this block's catch clauses, so it propagated as
                    // an unhandled 500. Reject it here as a normal 400 OData error instead.
                    if (body.ValueKind != JsonValueKind.Object)
                    {
                        return ODataError(400, "InvalidBody", "Request body must be a JSON object.");
                    }

                    // #389: see the POST route -- an unacceptable dynamic key is rejected before it
                    // can be bound and persisted.
                    using PreparedWriteBody patchPrepared =
                        PrepareWriteBody(registration, body, typeof(TModel), jsonOptions);
                    if (patchPrepared.Error is not null) return patchPrepared.Error;
                    // Every read of the body below this line goes through the PREPARED element, not
                    // `body`: on the control-information path the two differ, and binding the
                    // unstripped one would put an annotation in a bag that the read path then throws
                    // on forever. The key-mismatch check reads it too, so PATCH cannot disagree with
                    // itself about what the body contained.
                    JsonElement patchBody = patchPrepared.Body;

                    // #454: the key is immutable (§11.4.9), and this guard must validate EXACTLY the
                    // set the delta loop applies, or a body passes validation on one occurrence and is
                    // applied through another.
                    //
                    // It did not. TryGetJsonProperty returned the FIRST case-insensitive match on the
                    // CLR name and stopped, while the loop resolved EVERY body property through
                    // FindClrPropertyByEdmName ([JsonPropertyName]-aware) into a last-writer-wins Delta.
                    // Three bodies moved the key and returned 200: {"Id":1,"Id":999},
                    // {"id":1,"Id":999}, and -- with a renamed key -- a single {"code":"ZZ"}.
                    //
                    // Both halves now resolve through the same table against the same CLR property, and
                    // the loop never writes the key into the delta at all. A mismatch is REJECTED, not
                    // silently dropped. An omitted key is still valid -- PATCH is partial and the URL
                    // key is authoritative.
                    //
                    // #510: both halves go through the startup table, so a client-supplied name never
                    // reaches FindClrPropertyByEdmName's process-wide cache.
                    PropertyInfo? patchKeyClrProp = ResolvePatchBodyProperty(source.KeyPropertyName);
                    string patchParsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                    foreach (var prop in patchBody.EnumerateObject())
                    {
                        if (!IsPatchKeyOccurrence(prop.Name, patchKeyClrProp, source.KeyPropertyName)) continue;
                        if (!string.Equals(patchParsedKeyStr, prop.Value.ToString(), StringComparison.Ordinal))
                            return ODataError(400, "BadRequest", "Key in URL does not match key in request body.", target: "key");
                    }

                    // Local: does this body property name resolve to the entity's key property?
                    // Resolution goes through the same table the delta loop uses, so the two cannot
                    // disagree. The name-only fallback covers the (unexpected) case where the key
                    // property does not resolve to a CLR property at all -- it preserves the old
                    // behaviour rather than silently validating nothing.
                    bool IsPatchKeyOccurrence(string jsonName, PropertyInfo? keyClrProp, string keyPropertyName)
                    {
                        if (keyClrProp is null)
                            return string.Equals(jsonName, keyPropertyName, StringComparison.OrdinalIgnoreCase);
                        PropertyInfo? resolved = ResolvePatchBodyProperty(jsonName);
                        return resolved is not null
                            && string.Equals(resolved.Name, keyClrProp.Name, StringComparison.Ordinal);
                    }

                    // ETag check via If-Match header -- handler owns fetch-for-merge.
                    var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                    if (etagCheck is not null) return etagCheck;

                    // Build Delta<TModel>: only properties present in the request body are set.
                    // The handler is responsible for fetching the existing entity and applying
                    // the delta -- call delta.Patch(existing) to apply changed fields in-place.
                    var patchDelta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                    foreach (var prop in patchBody.EnumerateObject())
                    {
                        // #253: request body keys are JSON names — a [JsonPropertyName]-renamed property
                        // arrives under its JSON name, so resolve by EDM name (which honors the rename)
                        // rather than a plain CLR-name lookup that would silently drop the renamed member.
                        // #510: through the startup table, so prop.Name (client-supplied) never keys a
                        // process-wide cache.
                        PropertyInfo? clrProp = ResolvePatchBodyProperty(prop.Name);
                        // #454: the key never enters the delta. Every occurrence was validated
                        // against the URL key above (and a mismatch already returned 400), so what
                        // reaches here can only be a restatement of the key the URL already carries
                        // -- applying it would be a no-op at best, and leaving it out is what makes
                        // "the key cannot move" a structural property of this loop rather than a
                        // consequence of the guard having seen every occurrence.
                        if (patchKeyClrProp is not null && clrProp is not null
                            && string.Equals(clrProp.Name, patchKeyClrProp.Name, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        // #457 — deep update (§11.4.3.1): a navigation never ENTERS the delta when
                        // AllowDeepWrites is off. Not "enters and is nulled afterwards": Delta<T>
                        // is a change SET, so a nulled navigation would still be named by
                        // GetChangedPropertyNames() and still written by delta.Patch(existing) —
                        // turning a graph the client sent into an unrequested relationship CLEAR.
                        // It is also the shape the delta-mapping subsystem contradicts: Delta<TEntity>
                        // tracks structural properties only and DeltaMappingCompiler validates
                        // scalars/structural only, so a navigation in the Delta<TModel> that feeds it
                        // has nowhere to go. Same set as POST and PUT (deepWriteNavPropsToStrip),
                        // projected to CLR names because this loop already holds a PropertyInfo.
                        if (clrProp is not null && deepWriteNavClrNames.Contains(clrProp.Name)
                            && !source.AllowDeepWrites)
                        {
                            continue;
                        }
                        // #226: ignored properties get the same silent-skip as unknown members.
                        // This loop resolves members via CLR reflection (not the EDM), so EDM
                        // removal alone would not stop an ignored member from binding here.
                        if (clrProp is not null && !source.IgnoredPropertyNames.Contains(clrProp.Name))
                        {
                            object? value = prop.Value.Deserialize(clrProp.PropertyType, jsonOptions);
                            patchDelta.TrySetPropertyValue(clrProp.Name, value);
                        }
                    }

                    // #355: only the properties this body NAMED are in the delta, so this is exactly
                    // the "client explicitly sent null for a Nullable='false' property" case — a
                    // partial update that omits the property is untouched.
                    IResult? patchNullabilityFail =
                        ValidateEdmRequiredDelta(edmRequiredProps, patchDelta);
                    if (patchNullabilityFail is not null) return patchNullabilityFail;

                    object? result = await WithExceptionMapping(s.InvokePatchAsync(parsedKey!, patchDelta, ct), source, ctx, OhDataOperation.Update, key: parsedKey, delta: patchDelta);

                    string? patchEtag = null;
                    if (result is not null && source.HasETag)
                    {
                        patchEtag = s.InvokeGetETag(result);
                        ctx.Response.Headers.ETag = $"\"{patchEtag}\"";
                    }

                    if (result is null)
                        return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                    // Gap 4: Prefer: return=minimal → 204
                    if (PrefersMinimal(ctx))
                    {
                        ctx.Response.Headers["Preference-Applied"] = "return=minimal";
                        return Results.NoContent();
                    }

                    EchoReturnRepresentationPreference(ctx);

                    // Gap 5: include @odata.id in PATCH response
                    // Gap 2: include @odata.etag in body
                    // S4 fix: canonical, URL-safe key literal built from parsedKey (see GetById above).
                    string odataId = BuildEntityId(ctx, prefix, name, parsedKey!);
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, registration.EdmModel, odataId: odataId, etag: patchEtag, omitNavsForType: rootEdmType);
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }
                catch (ODataKeyFormatException ex)
                {
                    return BadKeyError(logger, ex, key, name);
                }
            });
            // Note: no .Accepts<TModel>("application/json") here -- that metadata caused ASP.NET
            // Core to reject non-JSON Content-Type requests with an empty 415 body before this
            // handler's manual IsJsonContentType() check (and its OData error formatting) ran.
            // Leg 2: OhDataRequestBodyMetadata documents the body instead, without triggering
            // that short-circuit -- see its XML doc for why.
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404).Produces(415)
              .WithMetadata(new OhDataRequestBodyMetadata
              {
                  BodyType = typeof(TModel),
                  Description = $"A partial {name} representation. Only properties present in the JSON body are applied (partial-update semantics) -- omitted properties are left unchanged."
              });
            ApplyOperationAuth(rb, OhDataOperation.Update);
        }

        if (source.HasDelete)
        {
            var rb = entityAuthGroup.MapDelete($"/{name}({{key}})", async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("DELETE {Prefix}/{Name}({Key})", prefix, name, SanitizeLogValue(key));
                try
                {
                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                    if (etagCheck is not null) return etagCheck;
                    bool deleted = await WithExceptionMapping(s.InvokeDeleteAsync(parsedKey!, ct), source, ctx, OhDataOperation.Delete, key: parsedKey);
                    if (!deleted && !source.IdempotentDelete)
                        return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    return Results.NoContent();
                }
                catch (ODataKeyFormatException ex)
                {
                    return BadKeyError(logger, ex, key, name);
                }
            });
            rb.WithTags(name).Produces(204).Produces(400).Produces(404);
            ApplyOperationAuth(rb, OhDataOperation.Delete);
        }

        // NOTE (#492 §4): duplicate bound-operation names within one profile are refused at BIND
        // time, in EntitySetProfile.Bind*/ValidateBoundOperationNameIsUnique -- not here. They have
        // to be: Microsoft.OData.ModelBuilder rejects a repeated ACTION name itself, from inside
        // VisitModelBuilder, with "Found more than one action with name 'X'" and no mention of the
        // profile or the entity set, so a check placed here would never run for half the cases.

        // Startup route-collision validation: POST /{name}({key})/{segment}.
        // A navigation property registered with a `post` handler (PostChild) claims
        // POST /{name}({key})/{nav.PropertyName} (creating a related entity, §11.4.2.1). An
        // entity-level bound action claims POST /{name}({key})/{action.Name} for the same
        // template shape. Unlike the structural-property-vs-bound-function check above (GET vs.
        // GET), these are both POST, so a shared name is a genuine route collision that ASP.NET
        // Core would only surface as an ambiguous-match failure at request time. Catch it at
        // startup instead, matching the existing idiom.
        //
        // #492 §2: OrdinalIgnoreCase, not Ordinal. ASP.NET Core literal-segment matching is
        // case-insensitive -- which OhDataBuilder.Register()'s own sibling checks already knew and
        // said so in a comment. This one and the two beside it did not, so a bound action named
        // `kids` beside a navigation `Kids` passed startup and made BOTH spellings of the URL an
        // AmbiguousMatchException.
        foreach (var navWithPost in source.NavigationRoutes.Where(n => n.PostChild is not null))
        {
            foreach (var collidingAction in source.BoundActions.Where(a =>
                a.IsEntityLevel && string.Equals(navWithPost.PropertyName, a.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': bound action '{collidingAction.Name}' conflicts with the " +
                    $"POST handler of navigation property '{navWithPost.PropertyName}' on " +
                    $"POST /{name}({{key}})/{collidingAction.Name} (route templates are case-insensitive). " +
                    "Rename the bound action or the navigation property.");
            }
        }

        // #416 / #492 §3: an entity-level bound FUNCTION vs a navigation ROUTE. Every NavigationRoutes
        // entry gets GET /{name}({key})/{nav} mapped -- including a post/addRef-only one, whose GET
        // 404s -- and an entity-level function claims the same template and method. No check existed:
        // the structural-property check cannot see it, because BuildStructuralProperties SUBTRACTS
        // every declared navigation, which is exactly why this pair fell through.
        //
        // Throws rather than warns: AmbiguousMatchException means NEITHER endpoint runs, so there is
        // no working configuration to preserve.
        foreach (var navRoute in source.NavigationRoutes)
        {
            foreach (var collidingFn in source.BoundFunctions.Where(f =>
                f.IsEntityLevel && string.Equals(navRoute.PropertyName, f.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': bound function '{collidingFn.Name}' conflicts with " +
                    $"navigation property '{navRoute.PropertyName}' on " +
                    $"GET /{name}({{key}})/{collidingFn.Name} (route templates are case-insensitive). " +
                    "That navigation registers a GET route because it was declared with a handler " +
                    "(getAll/get, post, addRef, removeRef or refTargetEntitySet). Rename the bound " +
                    "function or the navigation property.");
            }
        }

        // ── #313 stage 5: the bare-$expand continuation route ────────────────────────────────────
        //
        // GET /{Set}({key})/{Nav}?$skip=N — the target of the `Nav@odata.nextLink` the shaping pass
        // writes. Registered only for the navigations the SHARED predicate returned, so a route can
        // never exist without a link in front of it, nor a link without a route behind it, and a
        // delegate-backed or Blank navigation can never be served raw through here.
        //
        // The whole design turns on this endpoint being SMALL. It accepts $skip and nothing else, so
        // it constructs no ODataQueryOptions, runs no capability gate, applies no allowlist
        // validation, and calls no EnsureStableOrder. There is nothing to carry: the link it serves
        // was emitted for an expand that had no nested options at all, so the continuation of it
        // cannot need any either. Extracting the ~520-line collection-route body here would be the
        // mistake that killed the previous design.
        foreach (ExpandEngine.ExpandPagingNav pagingNav in expandPagingNavs)
        {
            // Startup route-collision validation, in the shared GET /{name}({key})/{segment} space.
            //
            // The existing check a few hundred lines below compares entity-level bound functions
            // against StructuralProperties ONLY — and BuildStructuralProperties subtracts every
            // declared navigation, so a bound function named identically to a delegate-less
            // collection navigation is perfectly legal TODAY (nothing registers that template for it)
            // and becomes a duplicate (template, GET) the moment this route appears. ASP.NET Core
            // would surface that as an ambiguous-match failure at REQUEST time, on a route that only
            // exists because someone opted in. Fail at MapOhData() instead, matching the idiom of the
            // two collision checks already in this file.
            // #492 §2: OrdinalIgnoreCase, not Ordinal -- ASP.NET Core literal-segment matching is
            // case-insensitive, so `books` and `Books` claim the same template.
            BoundOperationDefinition? collidingFn = source.BoundFunctions.FirstOrDefault(f =>
                f.IsEntityLevel && string.Equals(f.Name, pagingNav.EdmName, StringComparison.OrdinalIgnoreCase));
            if (collidingFn is not null)
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': bound function '{collidingFn.Name}' conflicts with the " +
                    $"$expand continuation route of navigation property '{pagingNav.EdmName}' on " +
                    $"GET /{name}({{key}})/{pagingNav.EdmName} (route templates are case-insensitive). " +
                    "That route is registered because " +
                    "ExpandPagingEnabled is on for this entity set; rename the bound function or the " +
                    "navigation property, or turn ExpandPagingEnabled off.");
            }

            string contNavName = pagingNav.EdmName;
            // THE PAGE SIZE IS MaxExpandTop, NEVER MaxTop. They are independent knobs: MaxTop still
            // defaults to 1000 while MaxExpandTop now defaults to null, so paging the continuation at
            // MaxTop would serve MaxExpandTop rows on page 1 and 1000 on page 2+ — and with MaxTop
            // unset, page 2 would be UNBOUNDED and #313's DoS would come straight back on the
            // continuation link. Non-null by the shared predicate.
            int contCap = source.MaxExpandTop!.Value;

            // The nav element type's EDM entity type, for the same §4.5.1/§11.2.4.2 nav-omission the
            // existing nav-collection route applies: this route takes no $expand, so every declared
            // navigation on the element type is omitted.
            IEdmEntityType? contElementEdmType = rootEdmType?
                .NavigationProperties()
                .FirstOrDefault(np => string.Equals(np.Name, contNavName, StringComparison.OrdinalIgnoreCase))?
                .ToEntityType();

            // Compose the continuation query ONCE at startup into a compiled delegate, following this
            // file's existing "Expression.Compile() runs at most once per type" convention. The shape:
            //
            //     parents.Where(p => p.Key == k)          <- request-scoped, built per request below
            //            .SelectMany(p => p.Nav)          <- an INNER JOIN with LIMIT/OFFSET, not a
            //            .OrderBy(c => c.ChildKey)           partitioned ROW_NUMBER() window
            //            .Skip(skip).Take(cap + 1)
            //
            // DETERMINISM IS BY CONSTRUCTION, NOT BY EnsureStableOrder. That helper short-circuits
            // when the source is already ordered — and the parent's own GetQueryable may well be
            // pre-ordered — which would leave page 2+ with only the parent's (possibly non-unique)
            // order and no total order over the children. So the OrderBy here is UNCONDITIONAL, and
            // its key comes from the same TryGetKeyClrProperty call ApplyNavShape uses to compose
            // page 1's tiebreaker (threaded through ExpandPagingNav.ChildKeyProperty). The two sides
            // therefore agree on the ordering column by construction rather than by coincidence.
            // The parent's own order never reaches the child collection: on page 1 it appears only in
            // the outer ORDER BY over parents, and here the parent is pinned to a single key.
            ParameterExpression contParentParam = Expression.Parameter(typeof(TModel), "p");
            Type contNavSelectorType = typeof(Func<,>).MakeGenericType(
                typeof(TModel), typeof(IEnumerable<>).MakeGenericType(pagingNav.ElementType));
            LambdaExpression contNavSelector = Expression.Lambda(
                contNavSelectorType,
                Expression.Property(contParentParam, pagingNav.NavProperty),
                contParentParam);

            ParameterExpression contChildParam = Expression.Parameter(pagingNav.ElementType, "c");
            Type contChildKeyType = pagingNav.ChildKeyProperty.PropertyType;
            LambdaExpression contChildKeySelector = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(pagingNav.ElementType, contChildKeyType),
                Expression.Property(contChildParam, pagingNav.ChildKeyProperty),
                contChildParam);

            MethodInfo contPageMethod = ExpandEngine._continuationPageMethod
                .MakeGenericMethod(typeof(TModel), pagingNav.ElementType, contChildKeyType);
            ParameterExpression contQParam = Expression.Parameter(typeof(IQueryable<TModel>), "q");
            ParameterExpression contSkipParam = Expression.Parameter(typeof(int), "skip");
            ParameterExpression contTakeParam = Expression.Parameter(typeof(int), "take");
            Func<IQueryable<TModel>, int, int, object[]> contPage =
                Expression.Lambda<Func<IQueryable<TModel>, int, int, object[]>>(
                    Expression.Call(
                        contPageMethod,
                        contQParam,
                        Expression.Constant(contNavSelector, typeof(Expression<>).MakeGenericType(contNavSelectorType)),
                        Expression.Constant(
                            contChildKeySelector,
                            typeof(Expression<>).MakeGenericType(
                                typeof(Func<,>).MakeGenericType(pagingNav.ElementType, contChildKeyType))),
                        contSkipParam,
                        contTakeParam),
                    contQParam, contSkipParam, contTakeParam)
                .Compile();

            // The parent-key CLR property, resolved exactly as ExpandLevelAsync resolves it.
            PropertyInfo? contParentKeyProp = typeof(TModel).GetProperty(
                source.KeyPropertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            FieldInfo contKeyBoxField = typeof(ExpandEngine.ContinuationKeyBox<TKey>)
                .GetField(nameof(ExpandEngine.ContinuationKeyBox<TKey>.Value))!;

            var contRb = entityAuthGroup.MapGet($"/{name}({{key}})/{contNavName}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        // 1. The $skip-ONLY surface. Every other system query option is rejected —
                        // conformant (Minimal item 7: parse the option or reject it), and the thing
                        // that keeps this endpoint from quietly becoming a second general-purpose
                        // collection route. This route's inline '$'-sigil loop was the precedent
                        // #359 generalised into FindUnsupportedSystemQueryOption; it now shares that
                        // matcher (and its $format exemption) so the two cannot drift, keeping only
                        // its own more specific message. See the block above s_collectionImplemented-
                        // Options for the full rule.
                        string? contUnsupported = QueryOptionGate.FindUnsupportedSystemQueryOption(
                            ctx, QueryOptionGate.s_expandContinuationImplementedOptions);
                        if (contUnsupported is not null)
                        {
                            return ODataError(501, "UnsupportedQueryOption",
                                $"The query option '{contUnsupported}' is not supported on an $expand " +
                                "continuation. This route serves the continuation of a BARE $expand " +
                                "and accepts '$skip' only.");
                        }

                        // 2. $skip, validated with the same idiom (and the same message shape) the
                        // navigation-collection route already uses.
                        int contSkip = 0;
                        if (ctx.Request.Query.TryGetValue("$skip", out var contSkipStr) &&
                            (!int.TryParse(contSkipStr, out contSkip) || contSkip < 0))
                        {
                            return ODataError(400, "InvalidQueryOption",
                                $"The value of '$skip' ('{contSkipStr}') is invalid. It must be a non-negative integer.");
                        }

                        // 3. The key. FormatException -> the shared BadKeyError, as everywhere else.
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                        // 4. The PARENT PROFILE'S OWN GetQueryable, which is what preserves row-level
                        // security for free: a tenant filter or soft-delete predicate baked into that
                        // queryable scopes the continuation exactly as it scoped the first page. It
                        // also means no foreign-key knowledge is required, which the convention EDM
                        // does not have.
                        var s = ResolveHandlers(ctx);
                        IQueryable<TModel> contParents =
                            (await s.InvokeGetQueryableAsync(ct)).Cast<TModel>();

                        // 5. Pin the parent. The key is referenced through a one-field box rather than
                        // an Expression.Constant of the value itself: that is the exact shape a C#
                        // closure produces, so EF Core's parameter extraction turns it into a query
                        // PARAMETER instead of baking the literal into the SQL (which would defeat the
                        // provider's plan cache on a route designed to be called repeatedly).
                        var contKeyBox = new ExpandEngine.ContinuationKeyBox<TKey> { Value = (TKey)parsedKey! };
                        var contKeyPredicate = Expression.Lambda<Func<TModel, bool>>(
                            Expression.Equal(
                                Expression.Property(contParentParam, contParentKeyProp!),
                                Expression.Convert(
                                    Expression.Field(Expression.Constant(contKeyBox), contKeyBoxField),
                                    contParentKeyProp!.PropertyType)),
                            contParentParam);

                        // 5b. #412: `Prefer: [odata.]maxpagesize=N` narrows THIS hop's page, clamped
                        // down to MaxExpandTop and never up — the ceiling is the server's bound and a
                        // client preference must not lift it, exactly as the root route clamps to
                        // MaxTop. This is what makes honouring the preference on the first hop sound:
                        // §8.2.8.5 states in terms that "the client MAY specify a different value for
                        // this preference with every request following a next link", so the page size
                        // is expected to travel on the request rather than inside the link, and the
                        // $skip-only continuation surface #313 chose does not have to widen to carry
                        // it. A client that stops sending the header simply gets MaxExpandTop-sized
                        // pages from there on; nothing is skipped or repeated either way, because
                        // $skip is an ABSOLUTE offset and the next one is computed from the rows this
                        // hop actually served.
                        int contPageSize = ParseMaxPageSize(ctx) is int contPreferred && contPreferred < contCap
                            ? contPreferred
                            : contCap;

                        // 6. Materialize pageSize + 1 rows: the probe row is what distinguishes "this
                        // page is exactly full and is the last one" from "there is more behind it",
                        // the rows % pageSize == 0 trap #360 fixed at the root. Synchronous, as the
                        // collection route's own materialization is.
                        object[] contRows = contPage(
                            contParents.Where(contKeyPredicate), contSkip, contPageSize + 1);

                        // 7/8. A MISSING PARENT KEY IS 200 + EMPTY value + NO LINK, not 404 (O3 on
                        // #313). SelectMany cannot distinguish "no such parent" from "a parent with no
                        // children", and an existence probe would cost a second round trip on EVERY
                        // continuation. Microsoft returns 404 here; this is a documented divergence.
                        bool contMore = contRows.Length > contPageSize;
                        if (contMore) contRows = contRows[..contPageSize];

                        string contBaseUrl = BuildBaseUrl(ctx, prefix);
                        var contJson = new JsonArray();
                        foreach (object contItem in contRows)
                        {
                            contJson.Add(ExpandEngine.SerializeBounded(
                                contItem, contElementEdmType, registration.EdmModel, clause: null,
                                jsonOptions ?? _pascalCaseSerializerOptions));
                        }

                        var contEnvelope = new Dictionary<string, object?>();
                        // The same path-shaped context segment BuildNavEnvelope emits for the
                        // delegate-backed nav route (m10, declared-not-fixed in
                        // docs/spec-compliance.md) — the two nav-collection surfaces must not
                        // disagree about their own context URL.
                        contEnvelope["@odata.context"] =
                            $"{contBaseUrl}/$metadata#{name}({key})/{contNavName}";
                        contEnvelope["value"] = contJson;
                        if (contMore)
                        {
                            // Absolute offset, formatted from the canonical key formatter so a string
                            // key round-trips back through ODataKeyParser on the next hop.
                            // #412: the offset advances by the rows THIS hop served, not by the
                            // ceiling — otherwise a narrowed page would skip everything between the
                            // page size and the ceiling.
                            contEnvelope["@odata.nextLink"] =
                                $"{contBaseUrl}/{name}({ODataEntityKeyUrlFormatter.Format(parsedKey!)})" +
                                $"/{contNavName}?$skip={(contSkip + contPageSize).ToString(CultureInfo.InvariantCulture)}";
                        }
                        return ODataEnvelopeResult(contEnvelope, jsonOptions);
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name, withTarget: false);
                    }
                })
                .WithSummary($"Continue a bare $expand of {name}/{contNavName}")
                .WithDescription(
                    "Serves the next page of a bare '$expand=" + contNavName + "' whose related " +
                    "collection exceeded MaxExpandTop. Accepts '$skip' only; every other system " +
                    "query option is rejected with 400.")
                .WithTags(name)
                .Produces(200,
                    typeof(ODataCollectionResponse<>).MakeGenericType(pagingNav.ElementType),
                    "application/json")
                .Produces(400).Produces(501);
            ApplyOperationAuth(contRb, OhDataOperation.Read);
        }

        // Navigation property routes
        foreach (var nav in source.NavigationRoutes)
        {
            string navPropertyName = nav.PropertyName;
            bool navIsCollection = nav.IsCollection;
            Type? navItemType = nav.NavItemType;
            // #179: the nav target/element EDM entity type, resolved once at startup. It drives the
            // #176 strip on nav-route reads (single-valued and collection) so a related entity's
            // shape matches a top-level read of that type — un-expanded navigations are omitted
            // (OData JSON §4.5.1 / §11.2.4.2) rather than leaking inline. For a collection nav
            // ToEntityType() yields the element type; for a single-valued nav the target type.
            IEdmEntityType? navTargetEdmType = rootEdmType?
                .NavigationProperties()
                .FirstOrDefault(p => string.Equals(p.Name, navPropertyName, StringComparison.OrdinalIgnoreCase))?
                .ToEntityType();
            var rb = entityAuthGroup.MapGet($"/{name}({{key}})/{navPropertyName}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        // S1/B1 fix: the COLLECTION branch parses $orderby/$skip/$top/$count/
                        // $select (below) but previously ignored anything else — most notably
                        // $filter — silently, returning 200 with the full unfiltered collection.
                        // That violates Minimal item 7 ("parse the option or reject it"): reject up
                        // front instead of quietly under-applying what the client asked for.
                        //
                        // The set is chosen by navIsCollection because the SINGLE-VALUED branch
                        // (below, past `if (navIsCollection)`) reads no query option at all; gating
                        // it with the collection set accepted and dropped $select/$orderby/$top/
                        // $count there under a 200.
                        IResult? navCapabilityError = QueryOptionGate.CheckNavUnsupportedQueryOptions(ctx, navIsCollection);
                        if (navCapabilityError is not null) return navCapabilityError;

                        var s = ResolveHandlers(ctx);
                        var requestNav = s.NavigationRoutes.First(n => n.PropertyName == navPropertyName);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        object? result = await requestNav.Handler(parsedKey!, ct);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name}({key})/{navPropertyName} not found.");
                        if (navIsCollection)
                        {
                            string baseUrl = BuildBaseUrl(ctx, prefix);
                            // Gap 5: apply $orderby/$top/$skip/$count on navigation collection results
                            var rawColl = result as System.Collections.IEnumerable;
                            IEnumerable<object> items = rawColl is not null
                                ? rawColl.Cast<object>()
                                : new[] { result };

                            // M-3: apply $orderby before $skip/$top, matching standard OData
                            // system-query-option ordering (filter, orderby, skip, top).
                            if (ctx.Request.Query.TryGetValue("$orderby", out var orderByStr)
                                && !string.IsNullOrEmpty(orderByStr))
                            {
                                var (orderedItems, orderByError) = ApplyNavOrderBy(items, navItemType, orderByStr.ToString());
                                if (orderByError is not null) return orderByError;
                                items = orderedItems!;
                            }

                            // m8: an invalid (non-numeric or negative) $skip/$top must 400, not be
                            // silently ignored (which would return the full, un-paged collection).
                            // Consistent with the collection GET route's $top/$skip validation.
                            if (ctx.Request.Query.TryGetValue("$skip", out var skipStr))
                            {
                                if (!int.TryParse(skipStr, out int skipVal) || skipVal < 0)
                                {
                                    return ODataError(400, "InvalidQueryOption",
                                        $"The value of '$skip' ('{skipStr}') is invalid. It must be a non-negative integer.");
                                }
                                if (skipVal > 0) items = items.Skip(skipVal);
                            }

                            long? navCount = null;
                            if (ctx.Request.Query.TryGetValue("$count", out var countVal)
                                && countVal == "true")
                            {
                                // Count before $top is applied (per OData spec)
                                navCount = items.LongCount();
                            }

                            if (ctx.Request.Query.TryGetValue("$top", out var topStr))
                            {
                                if (!int.TryParse(topStr, out int topVal) || topVal < 0)
                                {
                                    return ODataError(400, "InvalidQueryOption",
                                        $"The value of '$top' ('{topStr}') is invalid. It must be a non-negative integer.");
                                }
                                items = items.Take(topVal);
                            }

                            object[] itemArray = items.ToArray();
                            // Batch 3: apply $select post-processing to navigation collection results
                            var (navEnv, navEnvError) = BuildNavEnvelope(baseUrl, name, key, navPropertyName, navCount, itemArray, ctx, navItemType, jsonOptions, navTargetEdmType, registration.EdmModel);
                            if (navEnvError is not null) return navEnvError;
                            return ODataEnvelopeResult(navEnv!, jsonOptions);
                        }
                        // M1: single-valued navigation results must carry @odata.context too
                        // (JSON §4.5), mirroring what the collection branch above already does.
                        // #179: pass the nav target's EDM type so the related entity's own
                        // un-expanded navigations are omitted (§4.5.1 / §11.2.4.2), matching a
                        // top-level read of that type instead of leaking the full CLR graph.
                        return Results.Ok(ODataEntityNode(ctx, prefix, $"{name}({key})/{navPropertyName}/$entity", result, jsonOptions, registration.EdmModel, omitNavsForType: navTargetEdmType));
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name, withTarget: false);
                    }
                })
                .WithTags(name)
                // Leg 3 (docs-fidelity): a collection-valued nav route returns the same
                // @odata.context/value envelope shape as a top-level collection GET; a
                // single-valued nav route returns the entity itself (mirrors GetById's
                // TModel-only precedent above).
                .Produces(200,
                    navIsCollection
                        ? typeof(ODataCollectionResponse<>).MakeGenericType(navItemType ?? typeof(object))
                        : navItemType ?? typeof(object),
                    "application/json")
                .Produces(404).Produces(501);
            ApplyOperationAuth(rb, OhDataOperation.Read);

            // Batch 3: GET /{name}({key})/{nav}/$count — standalone count for navigation collections (§11.2.3)
            if (navIsCollection)
            {
                string navCountPropertyName = navPropertyName;
                var countRb = entityAuthGroup.MapGet($"/{name}({{key}})/{navCountPropertyName}/$count",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        try
                        {
                            // #359: this route counts the whole related collection and applies
                            // no data option at all -- it used to accept and discard every one of
                            // them, including the $filter its own sibling nav route rejects.
                            // $filter and $search are refused because §11.2.9 requires the count
                            // to be taken AFTER applying them and this route cannot; the four the
                            // same clause says MUST NOT affect a count are accepted and ignored.
                            // See s_navCountImplementedOptions.
                            IResult? navCountUnsupported =
                                QueryOptionGate.CheckUnsupportedSystemQueryOptions(ctx, QueryOptionGate.s_navCountImplementedOptions);
                            if (navCountUnsupported is not null) return navCountUnsupported;

                            var s = ResolveHandlers(ctx);
                            var requestNav = s.NavigationRoutes.First(n => n.PropertyName == navCountPropertyName);
                            object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                            object? result = await requestNav.Handler(parsedKey!, ct);
                            // M4: every 4xx/5xx must carry the OData error envelope (§9.4) — this
                            // was the sole bare Results.NotFound() in the file.
                            if (result is null)
                                return ODataError(404, "NotFound", $"{name}({key})/{navCountPropertyName} not found.");
                            var rawColl = result as System.Collections.IEnumerable;
                            long count;
                            if (rawColl is ICollection<object> objColl) count = objColl.Count;
                            else if (rawColl is System.Collections.ICollection nonGenColl) count = nonGenColl.Count;
                            else count = rawColl is not null ? rawColl.Cast<object>().LongCount() : 1L;
                            return Results.Content(count.ToString(CultureInfo.InvariantCulture), "text/plain");
                        }
                        catch (ODataKeyFormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name, withTarget: false);
                        }
                    })
                    .WithTags(name)
                    .Produces<long>(200, "text/plain")
                    .Produces(404).Produces(501);
                ApplyOperationAuth(countRb, OhDataOperation.Read);
            }

            // Gap 6: $ref endpoints for navigation (§11.4.6)
            string navRefPropertyName = nav.PropertyName;
            bool navRefIsCollection = nav.IsCollection;

            // GET /{name}({key})/{nav}/$ref — returns reference envelope
            var refNavCapture = nav;
            var refGetRb = entityAuthGroup.MapGet($"/{name}({{key}})/{navRefPropertyName}/$ref",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        var s = ResolveHandlers(ctx);
                        var requestNav = s.NavigationRoutes.First(n => n.PropertyName == navRefPropertyName);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        string baseUrl = BuildBaseUrl(ctx, prefix);
                        // M2: JSON Format §14 / Protocol §10.12 — an entity-reference response's
                        // context is "#$ref" (single-valued) or "#Collection($ref)" (collection),
                        // not a path shape.
                        string context = navRefIsCollection
                            ? $"{baseUrl}/$metadata#Collection($ref)"
                            : $"{baseUrl}/$metadata#$ref";

                        if (navRefIsCollection)
                        {
                            // When ChildEntitySetName and ChildKeyPropertyName are configured,
                            // build populated @odata.id references (OData §11.4.6.1).
                            if (refNavCapture.ChildEntitySetName is not null && refNavCapture.ChildKeyPropertyName is not null)
                            {
                                object? children = await requestNav.Handler(parsedKey!, ct);
                                var refs = new List<Dictionary<string, string>>();
                                if (children is System.Collections.IEnumerable childEnum)
                                {
                                    // Cache the compiled accessor outside the loop — all children
                                    // share the same concrete type in the common case.
                                    Func<object, object?>? cachedAccessor = null;
                                    Type? cachedChildType = null;
                                    foreach (object child in childEnum)
                                    {
                                        Type childType = child.GetType();
                                        if (cachedAccessor is null || childType != cachedChildType)
                                        {
                                            cachedAccessor = GetOrCompileNavRefKeyAccessor(childType, refNavCapture.ChildKeyPropertyName);
                                            cachedChildType = childType;
                                        }
                                        if (cachedAccessor(child) is { } k)
                                        {
                                            refs.Add(new Dictionary<string, string>
                                            {
                                                ["@odata.id"] = BuildEntityId(baseUrl, refNavCapture.ChildEntitySetName, k)
                                            });
                                        }
                                    }
                                }
                                return ODataEnvelopeResult(new Dictionary<string, object?>
                                {
                                    ["@odata.context"] = context,
                                    ["value"] = refs
                                }, jsonOptions);
                            }

                            // No ChildEntitySetName/ChildKeyPropertyName configured — return minimal
                            // envelope. Use HasMany(..., refTargetEntitySet: "...") to enable
                            // populated @odata.id references.
                            return ODataEnvelopeResult(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = context,
                                ["value"] = System.Array.Empty<object>()
                            }, jsonOptions);
                        }
                        else
                        {
                            // Single-entity $ref: when ChildEntitySetName and ChildKeyPropertyName
                            // are configured, call the handler to get the related entity and build
                            // the @odata.id link (OData §11.4.6.1).
                            if (refNavCapture.ChildEntitySetName is not null && refNavCapture.ChildKeyPropertyName is not null)
                            {
                                object? child = await requestNav.Handler(parsedKey!, ct);
                                if (child is not null)
                                {
                                    var accessor = GetOrCompileNavRefKeyAccessor(child.GetType(), refNavCapture.ChildKeyPropertyName);
                                    if (accessor(child) is { } k)
                                    {
                                        return ODataEnvelopeResult(new Dictionary<string, object?>
                                        {
                                            ["@odata.context"] = context,
                                            ["@odata.id"] = BuildEntityId(baseUrl, refNavCapture.ChildEntitySetName, k)
                                        }, jsonOptions);
                                    }
                                }
                            }

                            return ODataEnvelopeResult(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = context
                            }, jsonOptions);
                        }
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name, withTarget: false);
                    }
                })
                .WithTags(name)
                .Produces(200,
                    navRefIsCollection ? typeof(ODataRefCollectionResponse) : typeof(ODataRefResponse),
                    "application/json");
            ApplyOperationAuth(refGetRb, OhDataOperation.Read);

            // POST /{name}({key})/{nav}/$ref   — collection nav: add a link (§11.4.6.2)
            // PUT  /{name}({key})/{nav}/$ref   — single-value nav: set the link (§11.4.6.3)
            if (nav.AddRef is not null)
            {
                string addRefNavPropertyName = navRefPropertyName;
                async Task<IResult> handleAddOrSetRef(string key, HttpContext ctx, CancellationToken ct)
                {
                    // B2 fix: mirrors the PATCH/property-write pattern -- reject a non-JSON
                    // Content-Type with a proper 415 envelope before touching the body at all.
                    if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);

                    try
                    {
                        var s = ResolveHandlers(ctx);
                        var requestNav = s.NavigationRoutes.First(n => n.PropertyName == addRefNavPropertyName);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                        // #478: adding/setting a link mutates the addressed entity's relationship
                        // state, so a received If-Match must be honoured (RFC 9110 §13.1.1 -- the
                        // method MUST NOT be performed when the precondition evaluates false).
                        // The addRef/setRef delegate signature is (TKey, string, CancellationToken),
                        // so the handler author cannot implement this themselves; before this the
                        // header was silently discarded and the link written with a 204.
                        // Positioned after the key parse and BEFORE the body is read, matching the
                        // structural-property write route: a refused precondition outranks a
                        // malformed body, and nothing has been mutated when it fires.
                        var refEtagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                        if (refEtagCheck is not null) return refEtagCheck;

                        JsonElement body;
                        try
                        {
                            body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
                        }
                        catch (JsonException ex)
                        {
                            // B2 fix: malformed and empty JSON bodies previously had no catch
                            // clause here at all -- JsonException (including the "no JSON tokens"
                            // case for an empty body) propagated as an uncaught 500.
                            return ODataError(400, "InvalidBody", ex.Message);
                        }

                        // B2 fix: a syntactically valid non-object JSON payload (array, string,
                        // number, bool, null) would previously reach TryGetJsonProperty ->
                        // JsonElement.EnumerateObject(), which throws InvalidOperationException
                        // for any non-Object ValueKind -- another uncaught 500. Reject it here.
                        if (body.ValueKind != JsonValueKind.Object)
                        {
                            return ODataError(400, "InvalidBody", "Request body must be a JSON object.");
                        }

                        if (!TryGetJsonProperty(body, "@odata.id", out var odataIdEl))
                            return ODataError(400, "BadRequest", "Request body must contain '@odata.id'.");

                        // #455: JsonElement.GetString() throws InvalidOperationException for every
                        // ValueKind except String and Null, and the only catch clauses around this
                        // block are JsonException and FormatException -- so '{"@odata.id": 123}',
                        // a body that is perfectly well-formed JSON and merely semantically wrong,
                        // escaped to the group filter and became a generic 500. Every other
                        // hand-deserialized write path answers 400 for that (see the POST/PUT/PATCH
                        // design note in CLAUDE.md); this route was the one exception.
                        //
                        // JsonValueKind.Null is REJECTED here too, and that is a deliberate
                        // behaviour change rather than a side effect of the guard. Null never threw
                        // -- GetString() returns null and the '?? ""' turned it into an EMPTY
                        // entity-id, which was then handed to the profile's addRef/setRef delegate
                        // as a link target and answered 204. An explicit '"@odata.id": null' is not
                        // a reference to anything: §11.4.6.2 wants the entity-id of the entity to
                        // link, and "the member is present but names no entity" is the same client
                        // error as omitting the member, which already answers 400 one line above.
                        // Answering 204 while passing "" to a handler is precisely the
                        // silent-success failure mode the rest of the write surface is built to
                        // avoid.
                        if (odataIdEl.ValueKind != JsonValueKind.String)
                        {
                            return ODataError(400, "BadRequest",
                                "The '@odata.id' member must be a string containing the entity-id " +
                                "of the entity to link.");
                        }

                        string relatedId = odataIdEl.GetString()!;
                        await requestNav.AddRef!(parsedKey!, (object)relatedId, ct);
                        return Results.NoContent();
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name, withTarget: false);
                    }
                }

                var refBodyMetadata = new OhDataRequestBodyMetadata
                {
                    BodyType = typeof(ODataRefWriteRequest),
                    Description = $"A reference to the entity to link as {navRefPropertyName}."
                };

                if (navRefIsCollection)
                {
                    var refAddRb = entityAuthGroup.MapPost($"/{name}({{key}})/{navRefPropertyName}/$ref", handleAddOrSetRef)
                        .WithTags(name)
                        .Produces(204)
                        .Produces(400)
                        .Produces(415)
                        .WithMetadata(refBodyMetadata);
                    ApplyOperationAuth(refAddRb, OhDataOperation.Update);
                }
                else
                {
                    var refSetRb = entityAuthGroup.MapPut($"/{name}({{key}})/{navRefPropertyName}/$ref", handleAddOrSetRef)
                        .WithTags(name)
                        .Produces(204)
                        .Produces(400)
                        .Produces(415)
                        .WithMetadata(refBodyMetadata);
                    ApplyOperationAuth(refSetRb, OhDataOperation.Update);
                }
            }

            // DELETE /{name}({key})/{nav}/$ref (remove relationship)
            if (nav.RemoveRef is not null)
            {
                string removeRefNavPropertyName = navRefPropertyName;
                var refDeleteRb = entityAuthGroup.MapDelete($"/{name}({{key}})/{navRefPropertyName}/$ref",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        try
                        {
                            var s = ResolveHandlers(ctx);
                            var requestNav = s.NavigationRoutes.First(n => n.PropertyName == removeRefNavPropertyName);
                            object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                            // #478: unlinking mutates the addressed entity's relationship state.
                            // Same reasoning as the add/set route above -- the removeRef delegate
                            // gets (TKey, string, CancellationToken) and cannot check the header.
                            var refEtagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                            if (refEtagCheck is not null) return refEtagCheck;

                            // For DELETE $ref on collection nav, the related id may come from query param $id
                            string relatedId = ctx.Request.Query.TryGetValue("$id", out var idVal)
                                ? idVal.ToString()
                                : "";
                            await requestNav.RemoveRef!(parsedKey!, (object)relatedId, ct);
                            return Results.NoContent();
                        }
                        catch (ODataKeyFormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name, withTarget: false);
                        }
                    })
                    .WithTags(name)
                    .Produces(204)
                    .Produces(400);
                ApplyOperationAuth(refDeleteRb, OhDataOperation.Update);
            }

            // POST /{name}({key})/{nav} — create a new related entity (§11.4.2.1).
            // Registered only when PostChild is present (handler-presence-drives-routes).
            // Shares the /{name}({key})/{nav} template with the GET nav route above, but a
            // distinct HTTP method, so the two coexist without collision.
            if (nav.PostChild is not null)
            {
                string postNavPropertyName = navPropertyName;
                Type postNavItemType = navItemType ?? typeof(object);
                var postNavCapture = nav;

                // #355: the child type's own required properties. This is a documented CREATE route
                // (#389 H2 wired the dynamic-key policing into it for exactly that reason), so a
                // body that violates the published contract answers the same way here as it does on
                // the collection POST — leaving it out would be a per-route divergence in the one
                // place the framework creates an entity of another type. The EDM type is resolved
                // through EdmClrTypeMap rather than by name convention (#508); a child type the EDM
                // does not declare yields an empty set and the route behaves exactly as before.
                EdmRequiredProperty[] navPostRequiredProps =
                    source.RequestBodyNullabilityValidationEnabled
                    ? BuildEdmRequiredProperties(
                        EdmClrTypeMap.FindStructuredType(registration.EdmModel, postNavItemType),
                        postNavItemType)
                    : Array.Empty<EdmRequiredProperty>();

                // #544: the child type's own body-name table — the entity set's table is keyed to
                // TModel and names nothing on this route.
                Dictionary<string, PropertyInfo> navPostRequiredPropByBodyName =
                    BuildBinderBodyNameTable(
                        Array.ConvertAll(navPostRequiredProps, p => (p.EdmName, p.Clr)),
                        postNavItemType,
                        jsonOptions);
                bool navPostRequiredGateApplies = navPostRequiredProps.Length > 0;
                var navPostRb = entityAuthGroup.MapPost($"/{name}({{key}})/{postNavPropertyName}",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);

                        object? parsedKey;
                        try
                        {
                            parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        }
                        catch (ODataKeyFormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name);
                        }

                        var s = ResolveHandlers(ctx);

                        // #478: creating a related entity through the parent's navigation mutates
                        // the parent's relationship state, so a received If-Match is honoured here
                        // too (RFC 9110 §13.1.1). The `post` delegate gets
                        // (TKey, TNavigation, CancellationToken) and cannot check the header
                        // itself. ResolveHandlers is hoisted above the body read for this -- the
                        // precondition must be evaluated before anything is deserialized, so a
                        // refused write never runs user code.
                        var navPostEtagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                        if (navPostEtagCheck is not null) return navPostEtagCheck;

                        object? child;

                        // #544: which required properties the body named, captured on whichever
                        // branch below holds the bytes — same shape as PUT, null meaning "named
                        // nothing".
                        HashSet<string>? navBodyRequiredNamed = null;
                        try
                        {
                            // #389 H2: this is a documented CREATE route, so it polices dynamic
                            // property names exactly as POST /{EntitySet} does -- it was the one
                            // entity-creating route the check had not been wired into, and a body
                            // rejected with 400 on the collection POST was accepted with 201 here
                            // and persisted. Same buffer-then-bind shape as PUT, and gated the same
                            // way, so a registration with no open complex type keeps streaming
                            // straight into the deserializer.
                            if (registration.OpenTypesActive)
                            {
                                // #514: read the body the way the binder reads it — see
                                // CreateBinderParityDocumentOptions.
                                using JsonDocument navDocument = await JsonDocument.ParseAsync(
                                    ctx.Request.Body, binderParityDocumentOptions, ct);
                                using PreparedWriteBody navPrepared = PrepareWriteBody(
                                    registration, navDocument.RootElement, postNavItemType, jsonOptions);
                                if (navPrepared.Error is not null) return navPrepared.Error;
                                if (navPostRequiredGateApplies)
                                {
                                    navBodyRequiredNamed = CollectPresentBodyMemberClrNames(
                                        navPrepared.Body, navPostRequiredPropByBodyName);
                                }
                                child = navPrepared.Body.Deserialize(postNavItemType, jsonOptions);
                            }
                            else
                            {
                                // #456: the nav-POST create route is the second of the two streaming
                                // write routes -- same reasoning and same shape as PUT above.
                                using MemoryStream navBuffered = await BufferRequestBodyAsync(ctx, ct);
                                if (ContainsODataBindAnnotation(
                                        navBuffered.GetBuffer().AsSpan(0, (int)navBuffered.Length), jsonOptions))
                                {
                                    return ODataBindNotImplementedError();
                                }

                                if (navPostRequiredGateApplies)
                                {
                                    navBodyRequiredNamed = CollectPresentBodyMemberClrNames(
                                        navBuffered.GetBuffer().AsSpan(0, (int)navBuffered.Length),
                                        navPostRequiredPropByBodyName,
                                        jsonOptions);
                                }

                                child = await JsonSerializer.DeserializeAsync(navBuffered, postNavItemType, jsonOptions, ct);
                            }
                        }
                        catch (JsonException ex)
                        {
                            return ODataError(400, "InvalidBody", ex.Message);
                        }

                        if (child is null)
                            return ODataError(400, "InvalidBody", "Request body is empty or could not be deserialized.");

                        // #355: same check, same message, same authority as the collection POST.
                        IResult? navPostNullabilityFail = ValidateEdmRequiredProperties(
                            navPostRequiredProps, child, navBodyRequiredNamed);
                        if (navPostNullabilityFail is not null) return navPostNullabilityFail;

                        var requestNav = s.NavigationRoutes.First(n => n.PropertyName == postNavPropertyName);
                        logger?.LogDebug("POST {Prefix}/{Name}({Key})/{Nav}", prefix, name, SanitizeLogValue(key), postNavPropertyName);
                        object? created = await requestNav.PostChild!(parsedKey!, child, ct);
                        if (created is null)
                            return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                        // Build the Location/@odata.id from the created child's key when the
                        // navigation was configured with refTargetEntitySet (reuses the same
                        // ChildEntitySetName/ChildKeyPropertyName detection $ref relies on).
                        string baseUrl = BuildBaseUrl(ctx, prefix);
                        string? childOdataId = null;
                        if (postNavCapture.ChildEntitySetName is not null && postNavCapture.ChildKeyPropertyName is not null)
                        {
                            var accessor = GetOrCompileNavRefKeyAccessor(created.GetType(), postNavCapture.ChildKeyPropertyName);
                            if (accessor(created) is { } childKeyVal)
                            {
                                childOdataId = BuildEntityId(baseUrl, postNavCapture.ChildEntitySetName, childKeyVal);
                            }
                        }

                        // Prefer: return=minimal → 204 (mirrors the entity-level POST behaviour).
                        // Location/OData-EntityId can only be set when childOdataId is computable.
                        if (PrefersMinimal(ctx))
                        {
                            if (childOdataId is not null)
                            {
                                ctx.Response.Headers.Location = childOdataId;
                                ctx.Response.Headers["Content-Location"] = childOdataId;
                                ctx.Response.Headers["OData-EntityId"] = childOdataId;
                            }
                            ctx.Response.Headers["Preference-Applied"] = "return=minimal";
                            return Results.NoContent();
                        }

                        if (childOdataId is not null)
                            ctx.Response.Headers["Content-Location"] = childOdataId;

                        EchoReturnRepresentationPreference(ctx);

                        // When the target entity set is known, the context matches the child's
                        // own entity set (as if fetched via GET /{ChildEntitySet}({key})); otherwise
                        // fall back to a context scoped to the navigation path.
                        string contextSegment = postNavCapture.ChildEntitySetName is not null
                            ? $"{postNavCapture.ChildEntitySetName}/$entity"
                            : $"{name}({key})/{postNavPropertyName}/$entity";
                        var createdNode = ODataEntityNode(ctx, prefix, contextSegment, created, jsonOptions, registration.EdmModel, odataId: childOdataId, omitNavsForType: navTargetEdmType);
                        return childOdataId is not null
                            ? Results.Created(childOdataId, createdNode)
                            : Results.Json(createdNode, statusCode: 201);
                    })
                    .WithTags(name)
                    .Produces(201)
                    .Produces(400)
                    .Produces(404)
                    .Produces(415)
                    .WithMetadata(new OhDataRequestBodyMetadata
                    {
                        BodyType = postNavItemType,
                        Description = $"The related {postNavPropertyName} entity to create."
                    });
                ApplyOperationAuth(navPostRb, OhDataOperation.Create);
            }
        }

        // #221: property routes are numerous (four per structural property, per entity set) and,
        // by default, omitted from the generated API docs via ExcludeFromDescription — leaving the
        // primary CRUD/nav/bound-operation surface legible. They stay fully live at runtime
        // regardless; DocProp only affects ApiExplorer enumeration (the shared upstream for
        // Microsoft.AspNetCore.OpenApi, Swashbuckle, and NSwag). Opt back in via
        // PropertyRouteDocsEnabled (server-wide default or per-profile). DocProp is the identity
        // when docs are enabled, so it composes cleanly onto each route's fluent chain.
        RouteHandlerBuilder DocProp(RouteHandlerBuilder b) =>
            source.PropertyRouteDocsEnabled ? b : b.ExcludeFromDescription();

        // Individual structural property access (I-6, OData §11.2.6 / Part 2 §4.6-4.7).
        // This block registers property READ (GET /{Set}({key})/{Property} and its /$value),
        // which rides the existing GetById handler — no new handler delegate. Property WRITE
        // (PUT/PATCH/DELETE on /{Set}({key})/{Property}) is implemented further below, riding
        // Patch as a one-property Delta; only raw /{Property}/$value *writes* remain unsupported
        // (read-only). Registered only when PropertyAccessEnabled resolves true AND GetById is
        // configured.
        if (source.PropertyAccessEnabled && source.HasGetById)
        {
            // Startup route-collision validation (shared /{Set}({key})/{segment} space).
            // Structural vs navigation is disjoint by construction: BuildStructuralProperties
            // excludes every name recorded via HasOptional/HasRequired/HasMany, so a structural
            // property and a navigation route can never claim the same GET template. The one
            // real collision risk is an entity-level bound function (also GET, also scoped to
            // /{name}({key})/{segment}) sharing a name with a structural property. $ref/$count/
            // $value carry a reserved '$' sigil and can never collide with a bare property name.
            // Entity-level bound actions are POST, so method disjointness rules them out here.
            // #492 §2: OrdinalIgnoreCase, not Ordinal. Measured: structural property `Price` plus a
            // BindEntityFunction handler named `price` passed startup, and then BOTH
            // GET /{Set}(1)/price and GET /{Set}(1)/Price were AmbiguousMatchException.
            foreach (var collidingFn in source.BoundFunctions.Where(f => f.IsEntityLevel))
            {
                StructuralPropertyInfo? collidingProperty = structuralRouteProperties
                    .FirstOrDefault(p => string.Equals(p.Name, collidingFn.Name, StringComparison.OrdinalIgnoreCase));
                if (collidingProperty is not null)
                {
                    throw new InvalidOperationException(
                        $"Entity set '{name}': bound function '{collidingFn.Name}' conflicts with " +
                        $"structural property '{collidingProperty.Name}' on GET /{name}({{key}})/{collidingFn.Name} " +
                        "(route templates are case-insensitive). Rename the bound function or the property.");
                }
            }

            foreach (var propCapture in structuralRouteProperties)
            {
                // GET /{name}({key})/{Property} — property-value envelope (§11.2.6).
                var propGetRb = DocProp(entityAuthGroup.MapGet($"/{name}({{key}})/{propCapture.Name}",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        IResult? unsupportedOption =
                            QueryOptionGate.CheckUnsupportedSystemQueryOptions(ctx, QueryOptionGate.s_propertyRouteImplementedOptions);
                        if (unsupportedOption is not null) return unsupportedOption;

                        try
                        {
                            var s = ResolveHandlers(ctx);
                            object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                            object? entity = await s.InvokeGetByIdAsync(parsedKey!, ct);
                            if (entity is null)
                                return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                            string? etagValue = null;
                            if (source.HasETag)
                            {
                                etagValue = s.InvokeGetETag(entity);
                                ctx.Response.Headers.ETag = $"\"{etagValue}\"";

                                if (ctx.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
                                {
                                    var noneMatchList = ParseETagList(ifNoneMatch.ToString());
                                    if (noneMatchList.Contains("*") || noneMatchList.Contains(etagValue))
                                        return Results.StatusCode(304); // 304 Not Modified — no body
                                }
                            }

                            var requestProp = s.StructuralProperties.First(p => p.Name == propCapture.Name);
                            object? value = requestProp.Accessor(entity);

                            // §11.2.3: "If the property is single-valued and has the null value, the
                            // service responds with 204 No Content." (Cited §11.2.6 until #369 --
                            // that is "Requesting Related Entities", which governs navigation
                            // properties, not this route. The behaviour was right, the reference was
                            // not.)
                            if (value is null) return Results.NoContent();

                            string baseUrl = BuildBaseUrl(ctx, prefix);
                            var envelope = new Dictionary<string, object?>
                            {
                                ["@odata.context"] = $"{baseUrl}/$metadata#{name}({key})/{propCapture.Name}",
                                ["value"] = value,
                            };
                            // #252: serialize through the owned options so a complex-typed property's
                            // nested member names follow OhData's casing (PascalCase by default) instead
                            // of leaking the host's HttpJsonOptions policy via the Results.Ok pipeline.
                            // (Envelope keys are Dictionary keys — unaffected by PropertyNamingPolicy —
                            // and primitive values have no member names, so both are unchanged.)
                            // #396: `value` is a raw CLR property value (a complex type's whole
                            // sub-graph, for a complex property), so this envelope is serialized
                            // inside the filter's scope rather than deferred. See PreRenderedJson.
                            return PreRenderedJson(envelope, jsonOptions ?? _pascalCaseSerializerOptions);
                        }
                        catch (ODataKeyFormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name);
                        }
                    })
                    .WithTags(name)
                    .Produces(200, typeof(ODataPropertyResponse<>).MakeGenericType(propCapture.ClrType), "application/json")
                    .Produces(204)
                    .Produces(404).Produces(501));
                ApplyOperationAuth(propGetRb, OhDataOperation.Read);

                // GET /{name}({key})/{Property}/$value — raw value (Part 2 §4.7).
                bool propIsComplex = propCapture.IsComplex;
                var propValueRb = DocProp(entityAuthGroup.MapGet($"/{name}({{key}})/{propCapture.Name}/$value",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        IResult? unsupportedOption =
                            QueryOptionGate.CheckUnsupportedSystemQueryOptions(ctx, QueryOptionGate.s_propertyRouteImplementedOptions);
                        if (unsupportedOption is not null) return unsupportedOption;

                        // Complex-typed properties have no raw representation — a static
                        // attribute of the property, checked before touching the data source.
                        if (propIsComplex)
                        {
                            return ODataError(400, "BadRequest",
                                $"Property '{propCapture.Name}' is a complex type and has no raw $value representation.",
                                target: propCapture.Name);
                        }

                        try
                        {
                            var s = ResolveHandlers(ctx);
                            object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                            object? entity = await s.InvokeGetByIdAsync(parsedKey!, ct);
                            if (entity is null)
                                return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                            var requestProp = s.StructuralProperties.First(p => p.Name == propCapture.Name);
                            object? value = requestProp.Accessor(entity);

                            // #369 -- §11.2.3.1, verbatim: "A $value request for a property that is
                            // null results in a 204 No Content response." 404 is reserved by the very
                            // next sentence for a DIFFERENT condition: "If the property is not
                            // available, for example due to permissions". A null value is available
                            // and is null.
                            //
                            // This answered 404, citing Part 2 §4.7 ("the raw value of a null property
                            // does not exist") -- which says nothing about a status code, while the
                            // sibling /{Prop} route two routes up has always returned 204 for the same
                            // entity in the same state. One property, two segments, two answers.
                            if (value is null) return Results.NoContent();

                            if (value is byte[] bytes)
                                return Results.Bytes(bytes, "application/octet-stream");

                            return Results.Text(FormatRawValue(value), "text/plain");
                        }
                        catch (ODataKeyFormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name);
                        }
                    })
                    .WithTags(name)
                    // Leg 3 (docs-fidelity): the raw $value body is either text/plain (every
                    // scalar type, via FormatRawValue) or application/octet-stream (byte[]
                    // properties only) — never JSON.
                    .Produces<string>(200, "text/plain", "application/octet-stream")
                    .Produces(400)
                    .Produces(404).Produces(501));
                ApplyOperationAuth(propValueRb, OhDataOperation.Read);
            }
        }

        // Individual structural property WRITE (#30 PUT/PATCH, #31 DELETE-to-null;
        // OData §11.4.9.1/.2/.3). Rides the existing Patch handler — no new handler delegate.
        // A single-property write is built as a one-property Delta<TModel> and handed to the
        // profile's existing Patch handler, which already owns fetch-existing → apply → persist.
        // Registered only when PropertyAccessEnabled resolves true AND Patch is configured
        // (property writes are a read-modify-write over Patch's own fetch-for-merge story;
        // unlike property READ, GetById is not required here — Patch does its own fetching).
        if (source.PropertyAccessEnabled && source.HasPatch)
        {
            foreach (var propCapture in structuralRouteProperties)
            {
                string propName = propCapture.Name;
                // #253: propName is the OData/EDM name (route segment, error targets); the underlying
                // Delta<TModel> keys by the CLR property name, which differs under [JsonPropertyName].
                string clrPropName = propCapture.Property.Name;
                // #355: ASK THE EDM, not the CLR type. StructuralPropertyInfo.IsNullable answers
                // "can this CLR type hold null", for which EVERY reference type qualifies — so a
                // property the framework's own $metadata declares Nullable="false" (an ordinary
                // non-nullable `string`) passed this gate, the null reached the handler through a
                // one-property Delta, and the persistence layer's rejection came back as a 500.
                // That is #355's defect on the property routes, and it is the same defect the entity
                // write routes have: two independently derived answers to one question. The CLR
                // answer is kept only as the fallback for a property the EDM does not declare (an
                // AdvancedConfigure model may omit one) and for a profile that opted out.
                bool propIsNullable =
                    !edmNonNullablePropertyNames.Contains(propCapture.Name) && propCapture.IsNullable;
                bool propIsComplex = propCapture.IsComplex;
                Type propClrType = propCapture.ClrType;

                if (propCapture.IsKey)
                {
                    // §11.4.9: the key property is immutable. Register explicit 400-returning
                    // stubs for PUT/PATCH/DELETE so clients get a clean OData error instead of
                    // an unmatched-route 404 (no other route claims these key-scoped templates).
                    IResult KeyImmutableError() => ODataError(400, "BadRequest",
                        $"Property '{propName}' is the entity's key and cannot be modified.",
                        target: propName);

                    // #184: the stub lambdas take (string key) — otherwise the generated operation
                    // omits the {key} path-parameter declaration its sibling GET carries, producing
                    // an OpenAPI document with an undeclared template variable (technically invalid).
                    // The key is unused: the response is a fixed 400 regardless of its value.
                    var propKeyPutRb = DocProp(entityAuthGroup.MapPut($"/{name}({{key}})/{propName}", (string key) => KeyImmutableError())
                        .WithTags(name).Produces(400));
                    ApplyOperationAuth(propKeyPutRb, OhDataOperation.Update);
                    var propKeyPatchRb = DocProp(entityAuthGroup.MapMethods($"/{name}({{key}})/{propName}", PatchMethod, (string key) => KeyImmutableError())
                        .WithTags(name).Produces(400));
                    ApplyOperationAuth(propKeyPatchRb, OhDataOperation.Update);
                    var propKeyDeleteRb = DocProp(entityAuthGroup.MapDelete($"/{name}({{key}})/{propName}", (string key) => KeyImmutableError())
                        .WithTags(name).Produces(400));
                    ApplyOperationAuth(propKeyDeleteRb, OhDataOperation.Update);
                    continue;
                }

                // Shared PUT/PATCH handler for a primitive property (PATCH on a primitive is
                // semantically identical to PUT — there is no partial state to merge). For a
                // complex property, PUT still performs a full replacement; PATCH (partial merge
                // into an existing complex value) is not built for 1.0.0 — documented non-support,
                // returns 400 rather than silently no-oping or guessing at a merge strategy.
                async Task<IResult> HandleSetPropertyAsync(string key, HttpContext ctx, CancellationToken ct, bool isPatchVerb)
                {
                    if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);

                    // #645: 501, not 400, by the framework's own mechanical test -- could any setting on
                    // the profile make this same request succeed on this same route? Nothing on
                    // EntitySetProfile or EntitySetDefaults enables a complex-property merge; it is a
                    // documented non-goal, so no configuration makes it succeed and §9.3.1's MUST
                    // ("functionality not implemented ... MUST respond with 501") applies, as does
                    // Minimal item 7 which puts that 501 in the conformance MUST list.
                    //
                    // The sibling non-goal, @odata.bind, already answers 501 NotImplemented at its two
                    // sites; this was the one place a permanent non-goal answered 400, under a code
                    // ("NotSupported") used nowhere else in the framework. Answered 400 NotSupported
                    // from 1.0.0 through 1.7.0.
                    //
                    // GET .../{ComplexProperty}/$value keeps its 400 and is NOT this case: §11.2.3.1
                    // defines /$value for PRIMITIVE properties only, so a complex property has no raw
                    // value by definition and the request is meaningless rather than unimplemented.
                    if (isPatchVerb && propIsComplex)
                    {
                        return ODataError(501, "NotImplemented",
                            $"PATCH (partial merge) on complex property '{propName}' is not supported. " +
                            "Use PUT to replace the entire complex value.", target: propName);
                    }

                    try
                    {
                        var s = ResolveHandlers(ctx);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                        var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                        if (etagCheck is not null) return etagCheck;

                        JsonElement body;
                        try
                        {
                            body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
                        }
                        catch (JsonException ex)
                        {
                            return ODataError(400, "InvalidBody", ex.Message);
                        }

                        if (body.ValueKind != JsonValueKind.Object)
                        {
                            return ODataError(400, "InvalidBody",
                                "Request body must be a JSON object with a 'value' member.", target: propName);
                        }

                        if (!TryGetJsonProperty(body, "value", out JsonElement valueEl))
                        {
                            return ODataError(400, "InvalidBody",
                                "Request body must contain a 'value' member.", target: propName);
                        }

                        // #389: a property-route write replaces a whole complex value, so the same
                        // dynamic-key policing the entity routes do applies to what lands inside it.
                        using PreparedWriteBody propPrepared =
                            PrepareWriteBody(registration, valueEl, propClrType, jsonOptions);
                        if (propPrepared.Error is not null) return propPrepared.Error;

                        object? newValue;
                        try
                        {
                            newValue = propPrepared.Body.ValueKind == JsonValueKind.Null
                                ? null
                                : propPrepared.Body.Deserialize(propClrType, jsonOptions);
                        }
                        catch (JsonException ex)
                        {
                            return ODataError(400, "InvalidBody",
                                $"The 'value' member could not be converted to the property's type: {ex.Message}",
                                target: propName);
                        }

                        if (newValue is null && !propIsNullable)
                        {
                            return NonNullablePropertyError(propName);
                        }

                        var delta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                        if (!delta.TrySetPropertyValue(clrPropName, newValue))
                        {
                            return ODataError(400, "InvalidBody",
                                $"Could not set property '{propName}' to the supplied value.", target: propName);
                        }

                        object? result = await WithExceptionMapping(s.InvokePatchAsync(parsedKey!, delta, ct), source, ctx, OhDataOperation.Update, key: parsedKey, delta: delta);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                        if (source.HasETag)
                        {
                            string writeEtag = s.InvokeGetETag(result);
                            ctx.Response.Headers.ETag = $"\"{writeEtag}\"";
                        }

                        return Results.NoContent();
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name);
                    }
                }

                var propertyWriteBodyMetadata = new OhDataRequestBodyMetadata
                {
                    BodyType = typeof(ODataPropertyWriteRequest<>).MakeGenericType(propClrType),
                    Description = $"The new value for '{propName}', wrapped in a 'value' member."
                };

                var propPutRb = DocProp(entityAuthGroup.MapPut($"/{name}({{key}})/{propName}",
                    (string key, HttpContext ctx, CancellationToken ct) => HandleSetPropertyAsync(key, ctx, ct, isPatchVerb: false))
                    .WithTags(name).Produces(204).Produces(400).Produces(404).Produces(412).Produces(415)
                    .WithMetadata(propertyWriteBodyMetadata));
                ApplyOperationAuth(propPutRb, OhDataOperation.Update);

                var propPatchRb = DocProp(entityAuthGroup.MapMethods($"/{name}({{key}})/{propName}", PatchMethod,
                    (string key, HttpContext ctx, CancellationToken ct) => HandleSetPropertyAsync(key, ctx, ct, isPatchVerb: true))
                    .WithTags(name).Produces(204).Produces(400).Produces(404).Produces(412).Produces(415)
                    .WithMetadata(propertyWriteBodyMetadata));
                ApplyOperationAuth(propPatchRb, OhDataOperation.Update);

                // DELETE — set the property to null (§11.4.9.3). Non-nullable is a structural
                // (static, per-type) validation, checked before touching the data source at all —
                // the same "cheap check first" pattern used for the key-immutable stub above.
                var propDeleteRb = DocProp(entityAuthGroup.MapDelete($"/{name}({{key}})/{propName}", async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    if (!propIsNullable)
                    {
                        return NonNullablePropertyError(propName);
                    }

                    try
                    {
                        var s = ResolveHandlers(ctx);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                        var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                        if (etagCheck is not null) return etagCheck;

                        var delta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                        delta.TrySetPropertyValue(clrPropName, null);
                        object? result = await WithExceptionMapping(s.InvokePatchAsync(parsedKey!, delta, ct), source, ctx, OhDataOperation.Update, key: parsedKey, delta: delta);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                        if (source.HasETag)
                        {
                            string deleteEtag = s.InvokeGetETag(result);
                            ctx.Response.Headers.ETag = $"\"{deleteEtag}\"";
                        }

                        return Results.NoContent();
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name);
                    }
                }).WithTags(name).Produces(204).Produces(400).Produces(404).Produces(412));
                ApplyOperationAuth(propDeleteRb, OhDataOperation.Update);
            }
        }

        // Bound functions — GET /{EntitySet}/{FunctionName}?param=value
        foreach (var fn in source.BoundFunctions.Where(f => !f.IsEntityLevel))
        {
            var fnCapture = fn;
            var rb = entityGroup.MapGet($"/{fn.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
                // #359: the sigil gate, before parameter binding and before the handler
                // delegate runs — so a refused invocation of an ACTION provably mutates
                // nothing, the same placement CheckETagAsync uses. See
                // s_boundOperationImplementedOptions for why $top/$skip are listed
                // unconditionally.
                IResult? opUnsupported = QueryOptionGate.CheckUnsupportedSystemQueryOptions(
                    ctx, QueryOptionGate.s_boundOperationImplementedOptions);
                if (opUnsupported is not null) return opUnsupported;

                var s = ResolveHandlers(ctx);
                var requestFn = s.BoundFunctions.First(f => f.Name == fnCapture.Name && !f.IsEntityLevel);
                object?[] args = new object?[fnCapture.Parameters.Length];
                for (int i = 0; i < fnCapture.Parameters.Length; i++)
                {
                    var param = fnCapture.Parameters[i];
                    if (ctx.Request.Query.TryGetValue(param.Name!, out var val))
                    {
                        try
                        {
                            var targetType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
                            var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
                            args[i] = converter.ConvertFromInvariantString(val.ToString() ?? "");
                        }
                        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or OverflowException or ArgumentException)
                        {
                            return ODataError(400, "InvalidParameter",
                                $"Cannot convert parameter '{param.Name}' value to {param.ParameterType.Name}.",
                                target: param.Name);
                        }
                    }
                    else if (param.HasDefaultValue)
                    {
                        args[i] = param.DefaultValue;
                    }
                    else
                    {
                        return ODataError(400, "MissingParameter",
                            $"Required parameter '{param.Name}' is missing.",
                            target: param.Name);
                    }
                }
                object? result = await requestFn.Invoke(args, ct);
                if (result is null) return Results.NoContent();
                // Gap 1: @odata.context on function results when return type matches TModel
                return BoundOperationResults.WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s, startupSource: source, fnCapture.Name, continuable: true);
            }).WithTags(name).Produces(400).Produces(501);
            BoundOperationResults.AddBoundOperationProduces<TModel>(rb, fnCapture);
            BoundOperationResults.AddBoundOperationPagingMetadata<TModel>(rb, fnCapture, source);
            // Issue #181: document the function's query-string parameters.
            var boundFnQueryParams = BoundOperationResults.BuildFunctionQueryParametersMetadata(fnCapture.Parameters, skipKey: false);
            if (boundFnQueryParams is not null) rb.WithMetadata(boundFnQueryParams);
            // #526: COLLECTION-bound -- mapped on entityGroup as "/{FunctionName}", no {key}
            // segment. The entity-bound twin further down keeps keyBased: true.
            ApplyOperationAuth(rb, OhDataOperation.Invoke, fnCapture.Name, keyBased: false);
        }

        // Bound actions — POST /{EntitySet}/{ActionName} with JSON body params
        // Note: TryGetJsonProperty (below) provides case-insensitive JSON property lookup,
        // matching the case-insensitive query string lookup used for bound functions.
        foreach (var action in source.BoundActions.Where(a => !a.IsEntityLevel))
        {
            var actionCapture = action;
            var rb = entityGroup.MapPost($"/{action.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
                // #359: the sigil gate, before parameter binding and before the handler
                // delegate runs — so a refused invocation of an ACTION provably mutates
                // nothing, the same placement CheckETagAsync uses. See
                // s_boundOperationImplementedOptions for why $top/$skip are listed
                // unconditionally.
                IResult? opUnsupported = QueryOptionGate.CheckUnsupportedSystemQueryOptions(
                    ctx, QueryOptionGate.s_boundOperationImplementedOptions);
                if (opUnsupported is not null) return opUnsupported;

                var s = ResolveHandlers(ctx);
                var requestAction = s.BoundActions.First(a => a.Name == actionCapture.Name && !a.IsEntityLevel);
                object?[] args = new object?[actionCapture.Parameters.Length];
                if (actionCapture.Parameters.Length > 0)
                {
                    // B2 fix: mirrors the PATCH/property-write pattern -- reject a non-JSON
                    // Content-Type with a proper 415 envelope before touching the body at all.
                    if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);
                    try
                    {
                        var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                            ctx.Request.Body, cancellationToken: ct);

                        // B2 fix: a syntactically valid JSON payload that isn't a JSON object
                        // (array, string, number, bool, null) would previously reach
                        // TryGetJsonProperty -> JsonElement.EnumerateObject(), which throws
                        // InvalidOperationException for any non-Object ValueKind -- an uncaught
                        // 500. Reject it here as a normal 400 instead.
                        if (body.ValueKind != JsonValueKind.Object)
                        {
                            return ODataError(400, "InvalidBody", "Request body must be a JSON object.");
                        }

                        for (int i = 0; i < actionCapture.Parameters.Length; i++)
                        {
                            var param = actionCapture.Parameters[i];
                            if (TryGetJsonProperty(body, param.Name!, out var val))
                            {
                                // #389 H2: an action parameter whose type is (or contains) an open
                                // complex type binds dynamic keys just like an entity body does, and
                                // a handler that persists it stores them verbatim -- the same vector
                                // POST/PUT/PATCH are policed for. Checked per PARAMETER against the
                                // parameter's declared type, so the {"paramName": value} envelope is
                                // never itself treated as a bag.
                                using PreparedWriteBody actionPrepared = PrepareWriteBody(
                                    registration, val, param.ParameterType, jsonOptions);
                                if (actionPrepared.Error is not null) return actionPrepared.Error;
                                args[i] = actionPrepared.Body.Deserialize(param.ParameterType, jsonOptions);
                            }
                            else if (param.HasDefaultValue)
                            {
                                args[i] = param.DefaultValue;
                            }
                            else
                            {
                                return ODataError(400, "MissingParameter",
                                    $"Required parameter '{param.Name}' is missing.",
                                    target: param.Name);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        return ODataError(400, "InvalidBody", ex.Message);
                    }
                }
                object? result = await requestAction.Invoke(args, ct);
                if (result is null) return Results.NoContent();
                // Gap 1: @odata.context on action results when return type matches TModel
                return BoundOperationResults.WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s, startupSource: source, actionCapture.Name, continuable: false);
            }).WithTags(name).Produces(400).Produces(415).Produces(501);
            BoundOperationResults.AddBoundOperationProduces<TModel>(rb, actionCapture);
            BoundOperationResults.AddBoundOperationPagingMetadata<TModel>(rb, actionCapture, source);
            // Leg 2 / #184: synthesize a POCO body schema from the action's parameters (see the
            // matching comment on the unbound-action branch of MapUnboundOperations).
            if (actionCapture.Parameters.Length > 0)
            {
                rb.WithMetadata(new OhDataRequestBodyMetadata
                {
                    // #499/#547: scope the memoization to this REGISTRATION INSTANCE so two
                    // registrations declaring the same entity set + action name (e.g. v1/v2 of the
                    // same versioned action, or two hosts in one process) get distinct memoized
                    // schema types instead of silently sharing whichever one mapped first.
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        registration, $"{name}.{actionCapture.Name}", actionCapture.Parameters),
                    Description = "JSON object with the action's parameters: " +
                        string.Join(", ", actionCapture.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                });
            }
            // #526: COLLECTION-bound -- mapped on entityGroup as "/{ActionName}", no {key}
            // segment. The entity-bound twin further down keeps keyBased: true.
            ApplyOperationAuth(rb, OhDataOperation.Invoke, actionCapture.Name, keyBased: false);
        }

        // Gap 7: Entity-level bound functions — GET /{name}({key})/{fn.Name}
        foreach (var fn in source.BoundFunctions.Where(f => f.IsEntityLevel))
        {
            var fnCapture = fn;
            var rb = entityAuthGroup.MapGet($"/{name}({{key}})/{fn.Name}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        // #359: the sigil gate, before parameter binding and before the handler
                        // delegate runs — so a refused invocation of an ACTION provably mutates
                        // nothing, the same placement CheckETagAsync uses. See
                        // s_boundOperationImplementedOptions for why $top/$skip are listed
                        // unconditionally.
                        IResult? opUnsupported = QueryOptionGate.CheckUnsupportedSystemQueryOptions(
                            ctx, QueryOptionGate.s_boundOperationImplementedOptions);
                        if (opUnsupported is not null) return opUnsupported;

                        var s = ResolveHandlers(ctx);
                        var requestFn = s.BoundFunctions.First(f => f.Name == fnCapture.Name && f.IsEntityLevel);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        // First arg is the key; remaining come from query string
                        object?[] args = new object?[fnCapture.Parameters.Length];
                        args[0] = parsedKey;
                        for (int i = 1; i < fnCapture.Parameters.Length; i++)
                        {
                            var param = fnCapture.Parameters[i];
                            if (ctx.Request.Query.TryGetValue(param.Name!, out var val))
                            {
                                try
                                {
                                    var targetType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
                                    var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
                                    args[i] = converter.ConvertFromInvariantString(val.ToString() ?? "");
                                }
                                catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or OverflowException or ArgumentException)
                                {
                                    return ODataError(400, "InvalidParameter",
                                        $"Cannot convert parameter '{param.Name}' to {param.ParameterType.Name}.",
                                        target: param.Name);
                                }
                            }
                            else if (param.HasDefaultValue)
                            {
                                args[i] = param.DefaultValue;
                            }
                            else
                            {
                                return ODataError(400, "MissingParameter",
                                    $"Required parameter '{param.Name}' is missing.", target: param.Name);
                            }
                        }
                        object? result = await requestFn.Invoke(args, ct);
                        if (result is null) return Results.NoContent();
                        // Gap 1: @odata.context on entity-level function results
                        return BoundOperationResults.WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s, startupSource: source, fnCapture.Name, continuable: true);
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name);
                    }
                })
                .WithTags(name).Produces(400).Produces(501);
            BoundOperationResults.AddBoundOperationProduces<TModel>(rb, fnCapture);
            BoundOperationResults.AddBoundOperationPagingMetadata<TModel>(rb, fnCapture, source);
            // Issue #181: document the function's query-string parameters (skip the leading key,
            // which is a route parameter already documented via BindingSource.Path).
            var entityFnQueryParams = BoundOperationResults.BuildFunctionQueryParametersMetadata(fnCapture.Parameters, skipKey: true);
            if (entityFnQueryParams is not null) rb.WithMetadata(entityFnQueryParams);
            ApplyOperationAuth(rb, OhDataOperation.Invoke, fnCapture.Name);
        }

        // Gap 7: Entity-level bound actions — POST /{name}({key})/{action.Name}
        //
        // Gap 7a: entity-bound actions. Under the precondition gate since #566 -- §11.4.1.1's MUST
        // covers "a Data Modification Request OR ACTION REQUEST". Collection-bound and unbound
        // actions have no key to load an entity by, so they stay out.
        foreach (var action in source.BoundActions.Where(a => a.IsEntityLevel))
        {
            var actionCapture = action;
            var rb = entityAuthGroup.MapMethods($"/{name}({{key}})/{action.Name}", new[] { "POST" },
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        // #359: the sigil gate, before parameter binding and before the handler
                        // delegate runs — so a refused invocation of an ACTION provably mutates
                        // nothing, the same placement CheckETagAsync uses. See
                        // s_boundOperationImplementedOptions for why $top/$skip are listed
                        // unconditionally.
                        IResult? opUnsupported = QueryOptionGate.CheckUnsupportedSystemQueryOptions(
                            ctx, QueryOptionGate.s_boundOperationImplementedOptions);
                        if (opUnsupported is not null) return opUnsupported;

                        var s = ResolveHandlers(ctx);
                        var requestAction = s.BoundActions.First(a => a.Name == actionCapture.Name && a.IsEntityLevel);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                        // Must precede the body read: a refused invocation runs no user code.
                        var actionEtagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                        if (actionEtagCheck is not null) return actionEtagCheck;

                        object?[] args = new object?[actionCapture.Parameters.Length];
                        args[0] = parsedKey;
                        if (actionCapture.Parameters.Length > 1)
                        {
                            // B2 fix: mirrors the PATCH/property-write pattern -- reject a
                            // non-JSON Content-Type with a proper 415 envelope before touching
                            // the body at all.
                            if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);
                            try
                            {
                                var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                                    ctx.Request.Body, cancellationToken: ct);

                                // B2 fix: a syntactically valid JSON payload that isn't a JSON
                                // object (array, string, number, bool, null) would previously
                                // reach TryGetJsonProperty -> JsonElement.EnumerateObject(), which
                                // throws InvalidOperationException for any non-Object ValueKind --
                                // an uncaught 500. Reject it here as a normal 400 instead.
                                if (body.ValueKind != JsonValueKind.Object)
                                {
                                    return ODataError(400, "InvalidBody", "Request body must be a JSON object.");
                                }

                                for (int i = 1; i < actionCapture.Parameters.Length; i++)
                                {
                                    var param = actionCapture.Parameters[i];
                                    if (TryGetJsonProperty(body, param.Name!, out var val))
                                    {
                                        // #389 H2: same per-parameter dynamic-key check as the
                                        // collection-level bound action above. The loop starts at 1
                                        // because parameter 0 of an entity-level action is the key.
                                        using PreparedWriteBody actionPrepared = PrepareWriteBody(
                                            registration, val, param.ParameterType, jsonOptions);
                                        if (actionPrepared.Error is not null) return actionPrepared.Error;
                                        args[i] = actionPrepared.Body.Deserialize(
                                            param.ParameterType, jsonOptions);
                                    }
                                    else if (param.HasDefaultValue)
                                    {
                                        args[i] = param.DefaultValue;
                                    }
                                    else
                                    {
                                        return ODataError(400, "MissingParameter",
                                            $"Required parameter '{param.Name}' is missing.", target: param.Name);
                                    }
                                }
                            }
                            catch (JsonException ex)
                            {
                                return ODataError(400, "InvalidBody", ex.Message);
                            }
                        }
                        object? result = await requestAction.Invoke(args, ct);
                        if (result is null) return Results.NoContent();
                        // Gap 1: @odata.context on entity-level action results
                        return BoundOperationResults.WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s, startupSource: source, actionCapture.Name, continuable: false);
                    }
                    catch (ODataKeyFormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name);
                    }
                })
                .WithTags(name).Produces(400).Produces(415).Produces(501);
            BoundOperationResults.AddBoundOperationProduces<TModel>(rb, actionCapture);
            BoundOperationResults.AddBoundOperationPagingMetadata<TModel>(rb, actionCapture, source);
            // Leg 2 / #184: entity-level Parameters[0] is the route key (see BoundOperationDefinition's
            // XML doc), so only Parameters[1..] are body parameters — synthesize the POCO body schema
            // from those, excluding the leading key.
            if (actionCapture.Parameters.Length > 1)
            {
                ParameterInfo[] bodyParams = actionCapture.Parameters.Skip(1).ToArray();
                rb.WithMetadata(new OhDataRequestBodyMetadata
                {
                    // #499/#547: same registration-identity scoping as the collection-level bound
                    // action above.
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        registration, $"{name}.{actionCapture.Name}.Entity", bodyParams),
                    Description = "JSON object with the action's parameters: " +
                        string.Join(", ", bodyParams.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                });
            }
            ApplyOperationAuth(rb, OhDataOperation.Invoke, actionCapture.Name);
        }

    }

    private static bool TryGetJsonProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

