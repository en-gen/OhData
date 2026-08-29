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

    private static string BuildBaseUrl(HttpContext ctx, string prefix) =>
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
    private static string BuildNextPageLinkWithSkip(HttpContext ctx, int skip)
    {
        var req = ctx.Request;
        var query = HttpUtility.ParseQueryString(req.QueryString.ToString());
        query.Remove("$skiptoken");
        query.Remove(FrameworkSkipOption);
        query["$skip"] = skip.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}?{query}";
    }

    // #360: the Priority-1 continuation offset, carried as a CUSTOM (non-"$"-prefixed) query option.
    //
    // On the Priority-1 path the profile owns query application: it calls ODataQueryOptions.ApplyTo
    // itself and the framework only layers its MaxTop cap on top. That makes a "$skip=N" continuation
    // unhonourable by the framework — it emits the link but never applies the skip, so a profile that
    // does NOT re-apply the incoming options serves the identical first page forever and a client
    // walking @odata.nextLink never terminates.
    //
    // Whether the profile applied $skip is not reliably observable (ODataQueryOptions.ApplyTo is
    // virtual, but options.Skip.ApplyTo(q) bypasses any override, and expression-tree probing for a
    // Queryable.Skip node false-negatives on the very common "materialize, then .AsQueryable()"
    // profile shape). So instead of detecting, the framework carries its OWN offset and applies it
    // itself: correct whether or not the profile honours the standard options, and no detection
    // required. The incoming $skip is left verbatim on every hop, so a profile that DOES apply it
    // re-establishes the same base page each time and the framework offset accumulates on top —
    // no double-skip either way.
    //
    // It cannot be $skiptoken: ODataQueryOptions.ApplyTo THROWS on a $skiptoken it has no handler
    // for ("Unable to parse the skiptoken value '...'. Skiptoken value should always be server
    // generated."), which would break every profile that calls ApplyTo. A custom query option is
    // ignored by ApplyTo (verified) and by OhData's own option gating, and @odata.nextLink is
    // opaque to clients by spec (§11.2.5.7), so the name is a framework-private detail.
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

    // #456: the same question as the JsonElement overload above, asked of RAW UTF-8 -- for the two
    // write routes that do NOT otherwise materialise the body. PUT and the navigation-POST create
    // route stream ctx.Request.Body straight into JsonSerializer.DeserializeAsync unless the
    // registration has an open complex type, so there is no JsonElement to walk and no
    // PrepareWriteBody call to piggy-back on. (Every other write route -- PATCH, the
    // structural-property writes, and each bound/unbound action parameter -- already deserializes
    // into a JsonElement unconditionally, so those get the fix for free from PrepareWriteBody.)
    //
    // A Utf8JsonReader rather than a second JsonDocument.Parse, for two reasons. It allocates only
    // for an escaped property name, and -- the load-bearing one -- a malformed body must NOT be
    // reported from here. JsonSerializer.DeserializeAsync words a malformed body differently from
    // JsonDocument (it appends "Path: $"), and OpenTypeDefaultOnIsByteIdenticalTests exists because
    // that difference is observable; so the reader's own JsonException is swallowed, the scan simply
    // stops where the reader does, and the request proceeds to the deserializer, which stays the
    // sole author of that message. Anything the reader could not reach is by definition inside a
    // fragment the deserializer is about to reject anyway -- WHICH IS TRUE ONLY BECAUSE THE TWO
    // READERS ACCEPT THE SAME BODIES. #511: that premise was false for a UTF-8 BOM and for any
    // host-relaxed JSON option, and each falsehood turned this scan into a silent "no annotation
    // here" on a body the binder went on to bind. CreateBinderParityReader is what restores it; the
    // swallow itself is unchanged and must stay.
    //
    // Semantics match the JsonElement walk exactly: any property name, at any depth, in an object or
    // inside an array, whose name ENDS WITH "@odata.bind". The unescaped comparison is a raw span
    // suffix test; a name carrying JSON escapes ('category@odata.bind' is the same member name)
    // falls back to the unescaped string, because JsonElement's own prop.Name is unescaped and the
    // two overloads must not disagree about what a body contains.
    // #511: THE ONE PLACE EITHER SPAN SCANNER GETS A READER, and the reason it exists is that a
    // scanner which reads the body differently from the binder is a FAIL-OPEN, not a mismatch.
    //
    // Both span scanners swallow their JsonException — deliberately and correctly, so that
    // JsonSerializer.DeserializeAsync stays the sole author of the malformed-body message (#389 L1;
    // see the notes on each scanner). The swallow's safety argument was "a body this reader cannot
    // finish is a body the deserializer is about to reject", and that argument holds only while the
    // two readers accept the SAME bodies. Where they diverge the scanner throws on a body the binder
    // then binds perfectly well, the throw is swallowed, and the scan reports "nothing found":
    // the deep-write strip does not fire and '@odata.bind' is discarded under a 200/201.
    //
    // Three divergences were measured on the pre-fix tree, one of them needing no configuration:
    //
    //   1. A LEADING UTF-8 BOM. Utf8JsonReader throws at its first byte; DeserializeAsync skips it
    //      (as does JsonDocument.ParseAsync, which is why the collection POST was unaffected and PUT
    //      answered 200 to bytes POST answered 501 to). Skipping it here CLOSES a divergence rather
    //      than widening acceptance — every reader on the write path already accepted it except
    //      these two. BOMs are routine in bodies sourced from Windows tooling and from files.
    //
    //   2. RELAXED HOST JSON OPTIONS. startupJsonOptions copies the host's Http.Json
    //      SerializerOptions, so the binder honours ReadCommentHandling.Skip, AllowTrailingCommas and
    //      a raised MaxDepth. A default reader throws at the first such token, and — because the
    //      throw is swallowed — the scan STOPS THERE, so every navigation and every annotation named
    //      after that point is invisible while the binder reads on.
    //
    // The three members below are exactly what JsonSerializerOptions.GetReaderOptions() derives
    // internally for DeserializeAsync, which is what makes this parity rather than a second guess.
    // MaxDepth needs no translation: 0 means "the 64 default" on both types.
    //
    // Deliberately NOT derived: .NET 10's AllowDuplicateProperties. This assembly multi-targets
    // net8.0, where neither JsonSerializerOptions nor JsonReaderOptions has the member, and the
    // divergence it would leave runs the SAFE way — a host that turns duplicate-rejection on makes
    // the binder stricter than the scanner, so the scanner sees MORE and the request the binder
    // would have rejected is rejected anyway.
    // #511: the contract the WRITE path's binder resolves for a model type, which is where the
    // deep-write gate's body-name table takes its keys from.
    //
    // A probe COPY of the registration's options, not the instance itself, mirroring
    // OpenTypeJsonOptions.ValidateOrThrow: resolving a JsonTypeInfo calls MakeReadOnly() on the
    // options, and startup must stay free to keep configuring the real instance (PrimeNavSuppression
    // states as an invariant that it resolves no contract, and OpenTypeModifierOrderingTests exists
    // because the modifier chain's ordering is load-bearing). A copy carries the TypeInfoResolver,
    // every modifier on it and the PropertyNamingPolicy, so it answers the property-name question
    // identically; it costs one options object and one contract resolution per entity set at startup.
    //
    // Null on failure rather than throwing, and the caller then falls back to the EDM/CLR aliases —
    // i.e. degrades to exactly the pre-#511 table rather than to no table. A model whose contract
    // cannot be built cannot be deserialized either, so the write routes are already dead in that
    // case and a startup exception thrown from HERE would blame the wrong thing.
    //
    // ONE probe per options instance, not one per entity set: MapEntitySet runs per entity set and a
    // fresh copy each time would resolve every model's contract into its own throwaway cache. Keyed
    // weakly on the source instance so the probe dies with the registration, matching
    // s_navSuppressedOptionsCache's shape. GetValue may run the factory more than once under a race
    // and still returns one shared instance, which is all this needs.
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

    // #355: "which properties does the framework's OWN $metadata say cannot be null?" — asked of the
    // EDM, once per type at startup.
    //
    // WHY THE EDM AND NOTHING ELSE. The framework publishes the nullability of every structural
    // property in the CSDL it generates, and before #355 nothing enforced it: a null for a property
    // declared Nullable="false" reached the handler, and the persistence layer's rejection surfaced
    // as a generic 500 (measured on the shipped TestBench: POST /Movies {"Title":null} ->
    // 500 InternalServerError, from EF's "Required properties '{'Title'}' are missing"). A violation
    // the framework could see at its own boundary, reported as a server fault.
    //
    // The alternative — deriving "required" from the CLR type, from [Required], or from nullable
    // reference-type annotations — is the second-independently-derived-model hazard this codebase
    // records over and over (#454, #458, #511). It is not hypothetical here: the structural-property
    // write route already had a nullability check built on IsNullableClrType, for which EVERY
    // reference type is nullable, so a Nullable="false" string sailed straight through it. That
    // route now asks the same question of the same authority (see propIsNullable), so there is one
    // answer rather than two.
    //
    // FOUR DELIBERATE EXCLUSIONS, each of which would otherwise reject a legal request:
    //   - the KEY. Every EDM key is Nullable="false", and a server-generated key is routinely
    //     omitted on create (§11.4.2 permits it). Taken from edmType.Key() rather than from the
    //     profile's selector so entity and navigation-child types are answered the same way.
    //   - a non-nullable VALUE type. `int Year` cannot hold null, so a JSON null for it is already
    //     a JsonException -> 400 from the binder, worded by the deserializer. Checking it would cost
    //     a boxing read per request to answer a question with one possible answer.
    //   - a member the EDM declares but no readable CLR property backs. Nothing to read.
    //   - anything the EDM does not declare at all. Ignore()d properties are removed from the EDM,
    //     and a property withheld from the published contract cannot be required by it.
    //
    // TOP LEVEL ONLY, and stated rather than assumed: a null inside a nested complex value is not
    // checked. Widening to the complex graph is a separate decision with its own recursion and
    // cycle questions; #355 is about the root body the handler is handed.
    private static EdmRequiredProperty[] BuildEdmRequiredProperties(
        IEdmStructuredType? edmType, Type clrType)
    {
        if (edmType is null) return Array.Empty<EdmRequiredProperty>();

        var keyNames = new HashSet<string>(StringComparer.Ordinal);
        if (edmType is IEdmEntityType entityType && entityType.Key() is { } keys)
        {
            foreach (IEdmStructuralProperty k in keys) keyNames.Add(k.Name);
        }

        var required = new List<EdmRequiredProperty>();
        foreach (IEdmStructuralProperty edmProp in edmType.StructuralProperties())
        {
            if (edmProp.Type.IsNullable) continue;
            if (keyNames.Contains(edmProp.Name)) continue;

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
    /// #355: the whole-instance check, for the routes that produce a complete entity — the
    /// collection <c>POST</c>, <c>PUT</c>, and the navigation-<c>POST</c> create route. An OMITTED
    /// property is a violation there as well as an explicit <c>null</c>: both leave the handler with
    /// an entity that is not a valid instance of the declared type (§11.4.2), which is the state
    /// that produced the 500.
    /// </summary>
    /// <remarks>
    /// <b>It reads the BOUND INSTANCE, not the raw body, and that bounds what "omitted" can mean
    /// here.</b> A required property whose CLR declaration carries a non-null initializer
    /// (<c>public string Name { get; set; } = "";</c>) is not null after binding whether or not the
    /// body named it, so an omission is invisible — correctly, because nothing invalid reaches the
    /// handler and nothing downstream would have rejected it. One declared <c>= null!</c>, the other
    /// ordinary EF shape, is null and is reported. Reading the raw body instead would mean a fourth
    /// scanner shadowing the binder on the two routes that stream (<c>PUT</c>, nav-<c>POST</c>), and
    /// #511 is the record of what that class of thing costs.
    /// </remarks>
    private static IResult? ValidateEdmRequiredProperties(
        EdmRequiredProperty[] required, object instance)
    {
        foreach (EdmRequiredProperty p in required)
        {
            if (p.Clr.GetValue(instance) is null)
            {
                return ODataError(400, "InvalidBody",
                    $"Property '{p.EdmName}' is declared non-nullable by the service metadata and " +
                    "cannot be null or omitted.", target: p.EdmName);
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
                return ODataError(400, "InvalidBody",
                    $"Property '{p.EdmName}' is declared non-nullable by the service metadata and " +
                    "cannot be set to null.", target: p.EdmName);
            }
        }
        return null;
    }

    private static ReadOnlySpan<byte> Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF };

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

    // #506: "which navigations did this body actually NAME?" — the question the deep-write strip
    // has to answer before it nulls anything.
    //
    // WHY IT HAS TO BE ASKED AT ALL. The strip exists to stop a handler that does not expect a graph
    // from silently persisting part of one. If the body sent no graph there is nothing to prevent,
    // and nulling anyway DESTROYS state: a `List<Child> Kids { get; private set; } = new()` — plain
    // EF encapsulation, and a navigation the convention model builder discovers — went to the
    // handler as `null` rather than as the empty list the constructor put there, on a PUT whose body
    // was `{"id":1,"title":"t"}`. A handler diff-syncing that collection against the loaded entity
    // then sees null: an NRE in `.Count`, or a "null means clear the relationship" misread.
    //
    // TOP LEVEL ONLY, and that is not a shortcut. deepWriteNavPropsToStrip holds properties of
    // TModel; a navigation named inside a nested object belongs to some other type and is stripped
    // (or not) by whatever handles that type — here it is already inside a subtree that the root
    // navigation's own presence accounts for.
    //
    // The returned set holds CLR property names (ordinal, like deepWriteNavClrNames) rather than the
    // body's spelling, so the strip loop can test `navProp.Name` directly and two spellings of one
    // navigation collapse to one entry.
    private static HashSet<string> CollectPresentNavClrNames(
        JsonElement body, Dictionary<string, PropertyInfo> navByBodyName)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        // A non-object body names nothing. It cannot reach the strip in practice — the deserializer
        // rejects it first — but EnumerateObject() throws InvalidOperationException for any other
        // ValueKind, and an unhandled one of those is a 500 (BUG 2 on the PATCH route, same shape).
        if (body.ValueKind != JsonValueKind.Object) return present;

        foreach (JsonProperty prop in body.EnumerateObject())
        {
            if (navByBodyName.TryGetValue(prop.Name, out PropertyInfo? navProp))
                present.Add(navProp.Name);
        }

        return present;
    }

    // #506: the same question asked of RAW UTF-8, for PUT's non-open-type branch — the one write
    // path that has neither a JsonDocument nor a JsonElement, only the buffer #456 already made for
    // the '@odata.bind' scan.
    //
    // Same discipline as ContainsODataBindAnnotation(ReadOnlySpan<byte>) and for the same reason:
    // the reader's own JsonException is SWALLOWED, because JsonSerializer.DeserializeAsync must stay
    // the sole author of the malformed-body message (it appends "Path: $"; JsonDocument does not,
    // and #389 L1 measured that difference as observable — DeepInsertTests
    // .WritesWithoutTheAnnotation_StillSucceed_AndPutStillWordsAMalformedBodyItself and
    // OpenTypeDefaultOnIsByteIdenticalTests pin both halves). A body this reader cannot finish is a
    // body the deserializer is about to reject, so a partial answer here is never acted on.
    //
    // CurrentDepth == 1 is exactly "a member of the root object": Utf8JsonReader reports the root
    // StartObject at depth 0 and its property names at depth 1, so a nested object's members (depth
    // 2+) and the members of objects inside a root-level ARRAY are skipped without a state machine.
    //
    // GetString() rather than ValueTextEquals: matching is case-INSENSITIVE whenever the binder's
    // PropertyNameCaseInsensitive is set (always, in practice) and ValueTextEquals is ordinal, so the
    // no-allocation comparison cannot answer this question. The allocation is one string per
    // top-level member, which is what JsonDocument would have cost on the other branch anyway.
    //
    // #511: the reader comes from CreateBinderParityReader, so it accepts exactly what the binder
    // accepts. Constructing a DEFAULT one here made every reader-configuration divergence a silent
    // "this body names no navigation" — see that method for the three measured ones.
    private static HashSet<string> CollectPresentNavClrNames(
        ReadOnlySpan<byte> utf8Json, Dictionary<string, PropertyInfo> navByBodyName,
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
                if (navByBodyName.TryGetValue(reader.GetString()!, out PropertyInfo? navProp))
                    present.Add(navProp.Name);
            }
        }
        catch (JsonException)
        {
            // Deliberately swallowed -- see the note above.
        }

        return present;
    }

    // #456: a rewindable copy of the request body, so the '@odata.bind' scan and the deserializer can
    // both read it. Only PUT and the navigation-POST create route need this, and only on the path
    // where they were streaming.
    //
    // The deserializer is handed a Stream, through the SAME JsonSerializer.DeserializeAsync overload
    // it used before, rather than a JsonElement or a ReadOnlySpan<byte>: that is what keeps every
    // malformed-body, wrong-type and depth-limit message byte-identical to the streaming path. Going
    // through JsonDocument here instead is exactly the regression #389 L1 measured.
    //
    // GetBuffer() (not ToArray()) at the call sites, so the scan reads the copy already made rather
    // than making a second one; MemoryStream's public constructors leave the buffer publicly visible.
    //
    // WHY A MemoryStream AND NOT PipeReader/ReadOnlySequence<byte>. Considered, and it does not win:
    //   - It would avoid a COPY, not the MATERIALISATION. Reading the request PipeReader to
    //     completion holds every byte in the pipe's pooled segments, so the resident-bytes exposure
    //     the clamp above is about is identical. The accumulate-everything shape is
    //     ReadAsync + AdvanceTo(buffer.Start, buffer.End) in a loop — the documented UNBOUNDED
    //     buffering pattern, with the writer-pause flow control to reason about on top. It improves
    //     no bound.
    //   - Feeding those bytes to the deserializer would cost the invariant that forced the buffer in
    //     the first place. Utf8JsonReader takes a ReadOnlySequence<byte> happily, but
    //     JsonSerializer.Deserialize<T>(ref Utf8JsonReader) is a DIFFERENT overload from the
    //     DeserializeAsync(Stream) these routes used before, and "the deserializer words a malformed
    //     body exactly as it did" (the 'Path: $' pin) is the thing that must not move. Keeping the
    //     overload would mean hand-writing a Stream over a ReadOnlySequence — more code, same bytes
    //     resident, for no gain.
    // A pass-through Stream that scans as the deserializer pulls WOULD preserve streaming, and is
    // rejected for a correctness reason rather than a cost one: it makes the answer depend on where
    // in the body the annotation sits. '{"Id":"notanint","x@odata.bind":1}' would 400 on PUT while
    // the collection POST — which scans the whole body before binding anything — answers 501 for the
    // same bytes. Per-verb divergence on @odata.bind is precisely what #456 exists to remove.
    //
    // THE CAPACITY HINT IS CLAMPED, AND THAT IS THE WHOLE POINT OF THE LINE. Content-Length is a
    // CLIENT CLAIM, not a measurement: it arrives in the request head, before a single body byte
    // has. Pre-sizing the buffer from it hands any unauthenticated caller a remote allocation
    // primitive — declare 30 MB, send one byte, never finish, repeat across N connections, and the
    // server has committed N x 30 MB against N bytes of actual traffic. The streaming path this
    // replaces allocated in proportion to bytes RECEIVED, so honouring the header here would have
    // been a real regression rather than a wash.
    //
    // 81,920 is two facts at once: it is Stream.CopyTo's own default buffer size (so an honest small
    // body still lands in a single right-sized allocation and the first copied chunk fits exactly),
    // and it is under the 85,000-byte large-object-heap threshold (so a bogus hint costs a
    // collectable gen0 array rather than LOH pressure that survives until a gen2 compaction). Beyond
    // it the stream just doubles, which costs a few reallocations on genuinely large bodies — the
    // right trade against removing the primitive.
    //
    // WHAT ACTUALLY BOUNDS THE COPY — and since #474 the answer is finally "OhData does". It used
    // not to be, and the #203 commentary above read as more protective than the code was: #203's
    // filter does BOTH of its jobs — the Content-Length fast-reject AND setting Kestrel's
    // per-request MaxRequestBodySize — only when OhDataBodyLimitMetadata is attached, and that
    // metadata existed only when the profile or EntitySetDefaults set MaxRequestBodyBytes, which
    // DEFAULTED TO NULL at both levels. So on a default configuration neither half ran and the only
    // ceiling was the HOST's Kestrel MaxRequestBodySize — which an app that also accepts uploads
    // routinely raises or disables, leaving nothing at all. EntitySetDefaults.MaxRequestBodyBytes
    // now defaults to EntitySetDefaults.DefaultMaxRequestBodyBytes (30,000,000 — Kestrel's own
    // number, chosen so a DEFAULT host sees no behaviour change), and the group filter falls back to
    // the registration's copy of it for the routes that belong to no entity set. Setting it to null
    // server-wide restores the old "the host's limit is the only limit" behaviour.
    //
    // (Note the collection POST is NOT a precedent for the capacity hint, only for the
    // materialisation: JsonDocument.ParseAsync grows incrementally from pooled buffers and never
    // reads Content-Length. Hence the clamp — this must not be the one place that trusts it.)
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

    // #389: policing dynamic-property names on the way in. Only a registration whose EDM actually
    // declares an open complex type pays anything here (OpenTypesActive, not OpenTypesEnabled --
    // #389 L1; and now that the flag defaults to true, that EDM half is effectively the whole gate)
    // -- for everyone else this is one bool test and the body is never walked. See
    // OpenTypeJsonOptions.FindInvalidDynamicKey for why the check rides the raw JSON
    // against JsonTypeInfo rather than the bound CLR graph.
    //
    // Called from every route that binds a body which can reach a dynamic bag: POST/PUT/PATCH on the
    // entity, the structural-property write route, the navigation-POST create route, and each
    // parameter of a bound or unbound ACTION. The action routes pass the individual PARAMETER value
    // and its declared parameter type, never the {"paramName": value} envelope -- the envelope's own
    // keys are parameter names matched by the operation's signature, so they are not dynamic keys
    // and must not be policed as such.
    //
    // #398 stages 1-2 widened this from "reject a bad key" to "prepare the body". It answers two
    // things from ONE walk, and the caller has to honour both:
    //   - a key the odataIdentifier grammar rejects  -> 400, exactly as before;
    //   - a key that must not become a dynamic property but is NOT a client error -- control
    //     information ('@'), or a name the profile withholds with Ignore(...) -> the body is
    //     re-emitted without it and the request proceeds. Silently, in both cases: an annotation is
    //     not a property value, and a withheld name gets the same treatment an unknown member on a
    //     closed type already gets. Dropping is not enough on its own -- System.Text.Json would bag
    //     either one -- so the drop has to be a real edit to the body the binder sees.
    //
    // The returned JsonDocument, when non-null, MUST be disposed by the caller; PreparedWriteBody
    // exists so `using` at the call site covers it without an extra block. On the common path it is
    // null and Body is the caller's own element, so nothing is copied.
    //
    // (Disposal is right; the reason once stated for it was not. JsonDocument.Parse over a
    // ReadOnlyMemory<byte> — which is what RewriteWithoutUnbindableKeys hands it — does NOT pool the
    // payload: it wraps the caller's memory and only the metadata database is rented from the shared
    // array pool. Disposing still returns that database, so the `using` earns its keep either way.)
    private readonly record struct PreparedWriteBody(
        IResult? Error, JsonElement Body, JsonDocument? Rewritten) : IDisposable
    {
        public void Dispose() => Rewritten?.Dispose();
    }

    private static PreparedWriteBody PrepareWriteBody(
        OhDataRegistration registration, JsonElement body, Type declaredType,
        JsonSerializerOptions? jsonOptions)
    {
        // #456: ABOVE the OpenTypesActive gate, and that placement is the whole fix. This check was
        // added by #398 review MEDIUM-1 and put BELOW the gate, so on any registration without an
        // open complex type -- the majority -- PrepareWriteBody returned before reaching it and
        // 'prop@odata.bind' on PUT, PATCH, the nav-POST create route or a structural-property write
        // was accepted with 200/201 and the annotation silently discarded. The client asked to bind
        // a relationship, got a success, and nothing happened.
        //
        // It is safe above the gate because it needs nothing the gate protects: it is a pure
        // JsonElement walk over a body the caller has ALREADY materialised, whereas everything below
        // the gate needs `jsonOptions` and the open-type contract. The one thing the gate really was
        // buying -- PUT and nav-POST streaming straight into the deserializer instead of buffering --
        // is not obtained by skipping the check, because those two routes never call PrepareWriteBody
        // at all on the non-open path; they now do their own buffered scan (see
        // ContainsODataBindAnnotation(ReadOnlySpan<byte>) and its two call sites), which keeps
        // JsonSerializer.DeserializeAsync as the component that words a malformed body.
        //
        // #398 stage 2's ordering constraint still holds and is why this cannot move DOWN either:
        // stage 2 classifies any key containing '@' as control information and STRIPS it, and
        // 'Thing@odata.bind' contains one, so a bind annotation reaching ScanWriteBody would be
        // silently dropped rather than reported.
        //
        // 501, not 400, and the same 501 the collection POST gives: deep insert by reference is
        // UNIMPLEMENTED, not malformed, and it is unimplemented on every verb. The old 400 some of
        // these routes gave was incidental anyway -- it came from '@' failing the odataIdentifier
        // grammar, not from anything that knew what @odata.bind meant.
        //
        // The collection POST keeps its own earlier check: it has always been unconditional, and its
        // message is richer (it names the entity set, and it mentions AllowDeepWrites for the
        // adjacent case — a client that meant to CREATE the related entity inline rather than link
        // an existing one, which on POST is deep insert).
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

    private static int? ParseMaxPageSize(HttpContext ctx)
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

    // #358 (adversarial review R2, HIGH): signals that evaluating (enumerating/counting) a
    // $filter- or $orderby-ApplyTo'd query raised a client-triggerable arithmetic fault (div/mod
    // by zero, decimal overflow) -- thrown only by EvaluateQueryWithArithmeticFaultGuard below,
    // and caught by a dedicated clause on each collection-read route's OUTER try. A dedicated
    // type rather than reusing Microsoft.OData.ODataException: several nearby try/catch blocks
    // (the $expand-pushdown Include-fallback and pushed-query materialize sites) already catch
    // ODataException for an unrelated reason (provider translation failures) and rewrite it into
    // a different, $expand-specific message -- reusing ODataException here would let those catches
    // intercept and mask this fault instead of it reaching the route's own 400 InvalidQueryOption
    // handling.
    private sealed class FilterArithmeticFaultException(string message) : Exception(message);

    /// <summary>
    /// Evaluates <paramref name="materialize"/> — an enumeration (<c>ToArray</c>) or
    /// <c>LongCount</c> of the $filter/$orderby-ApplyTo'd query for the current request — and
    /// converts a <see cref="DivideByZeroException"/>/<see cref="OverflowException"/> raised
    /// DURING that specific call into a <see cref="FilterArithmeticFaultException"/> the calling
    /// route's own catch clause turns into 400 InvalidQueryOption.
    /// <para>
    /// Scope (adversarial review R2, HIGH — "narrow the try"): callers must wrap ONLY the
    /// materialization call itself, never handler invocation, <c>ApplyCollectionPipelineAsync</c>
    /// (nav delegates, batch handlers, ETag computation), or JSON serialization. An arithmetic
    /// fault raised from any of those is a genuine server bug, not a client input problem, and
    /// must reach the group-level exception filter (logged, 500) like any other unexpected
    /// exception — never be relabeled 400 just because it happens to share an exception type with
    /// a bad $filter.
    /// </para>
    /// <para>
    /// Guard (same review, same finding): only engages when <paramref name="options"/> actually
    /// carries a $filter or $orderby. Without either, no client-supplied expression could be the
    /// cause of a fault raised while enumerating this query — e.g. a profile's own
    /// <c>GetQueryable</c> Select projection dividing by zero is enumerated at this exact call
    /// site with NO $filter in the request, and must 500 (a genuine handler bug), not 400. When
    /// the guard doesn't match, the exception is left alone and propagates normally.
    /// </para>
    /// <para>
    /// Provider note: this only ever engages when the .NET runtime itself raises the exception —
    /// LINQ-to-Objects and EF Core's InMemory provider evaluate arithmetic client-side. A real
    /// relational provider (SQL Server, PostgreSQL, SQLite) may instead defer the fault into the
    /// database (raising a <c>DbException</c> subclass, or in SQLite's case treating division by
    /// zero as NULL and returning zero matching rows) — neither is caught here. That gap is
    /// tracked separately; see #358's follow-up issue for a provider-independent fix.
    /// </para>
    /// </summary>
    private static T EvaluateQueryWithArithmeticFaultGuard<TModel, T>(
        Func<T> materialize, ODataQueryOptions<TModel> options, ILogger? logger, string entitySetName)
    {
        bool hasFilter = options.Filter is not null;
        bool hasOrderBy = options.OrderBy is not null;
        try
        {
            return materialize();
        }
        catch (Exception ex) when ((ex is DivideByZeroException or OverflowException) && (hasFilter || hasOrderBy))
        {
            logger?.LogDebug(ex,
                "OhData: arithmetic fault evaluating $filter/$orderby for {EntitySet}.", entitySetName);
            string option = (hasFilter, hasOrderBy) switch
            {
                (true, true) => "$filter or $orderby expression",
                (true, false) => "$filter expression",
                _ => "$orderby expression",
            };
            throw new FilterArithmeticFaultException($"The {option} could not be evaluated: {ex.Message}");
        }
    }

    // #494: signals that the underlying LINQ provider could not TRANSLATE the query shape the
    // request asked for -- thrown only by TranslateThenMaterialize below, and caught by the three
    // $expand-pushdown execution sites, which rewrite it into their own 400 message. A dedicated
    // type for the same reason FilterArithmeticFaultException is one: the surrounding code already
    // catches ODataException for other reasons.
    private sealed class QueryTranslationFailedException(Exception inner)
        : Exception(inner.Message, inner);

    /// <summary>
    /// Enumerates <paramref name="build"/>'s query, separating the provider's TRANSLATION phase
    /// from its MATERIALIZATION phase so the two can be classified differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #494. The three $expand-pushdown execution sites used to wrap a whole
    /// <c>query.ToArray()</c> in <c>catch (ex is InvalidOperationException or
    /// NotSupportedException or ODataException)</c> and answer <c>400</c> "could not be translated
    /// by the underlying data provider". The premise -- recorded in those comments and in
    /// <c>ExpandPushdownExceptionClassificationTests</c> -- was that a real infrastructure fault
    /// arrives as a <c>DbException</c> subclass or a <c>TimeoutException</c>, so an
    /// <c>InvalidOperationException</c> could only be EF's translation failure. That premise is
    /// false, and the counterexamples are the ones that matter under load: SqlClient reports
    /// connection-pool exhaustion as a plain <c>InvalidOperationException</c> ("Timeout expired ...
    /// max pool size was reached") from <c>SqlConnection.Open</c>; <c>ObjectDisposedException</c>
    /// DERIVES from <c>InvalidOperationException</c>, so a disposed <c>DbContext</c> matched too;
    /// and EF's own "a second operation was started on this context instance" is an
    /// <c>InvalidOperationException</c>. Under any of those, an <c>$expand</c> request answered
    /// <c>400</c> -- telling client retry logic NOT to retry -- while the same request without
    /// <c>$expand</c> correctly answered <c>500</c>.
    /// </para>
    /// <para>
    /// The populations are separated POSITIVELY rather than by widening or narrowing the type
    /// list, because no type list can separate them: EF raises a translation failure BEFORE any
    /// command executes. <c>IQueryable&lt;T&gt;.GetEnumerator()</c> is what compiles the query
    /// (<c>EntityQueryProvider.Execute</c>), and the connection is not opened until the first
    /// <c>MoveNext()</c>. Verified on EF Core 10 / .NET 10.0.11 against SQLite: an untranslatable
    /// <c>Where</c> throws <c>InvalidOperationException</c> ("The LINQ expression ... could not be
    /// translated") out of <c>GetEnumerator()</c>, with the enumerator never created. So the
    /// <c>build</c> delegate and <c>GetEnumerator</c> are the translation window -- expression
    /// construction included, which is why this takes a factory rather than a query (the Include
    /// fallback builds its query by reflection and deliberately unwraps its own
    /// <c>TargetInvocationException</c> so the real type reaches this filter) -- and everything
    /// from the first <c>MoveNext</c> onward propagates untouched to the group-level exception
    /// filter, i.e. a logged <c>500</c>.
    /// </para>
    /// <para>
    /// <c>ObjectDisposedException</c> is excluded from the translation window as well: a disposed
    /// context can fail at compile time too, and "the object is gone" is never a statement about
    /// the client's query.
    /// </para>
    /// <para>
    /// A provider that translated lazily -- inside <c>MoveNext</c> rather than
    /// <c>GetEnumerator</c> -- would surface its translation failures as <c>500</c> here instead of
    /// <c>400</c>. That is the safe direction (loud either way, and never a false "retry is
    /// pointless"), and EF Core, the only provider this path is reachable with, does not do it.
    /// </para>
    /// </remarks>
    private static T[] TranslateThenMaterialize<T>(Func<IQueryable<T>> build)
    {
        IEnumerator<T> enumerator;
        try
        {
            enumerator = build().GetEnumerator();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException
                                   && ex is InvalidOperationException or NotSupportedException
                                       or Microsoft.OData.ODataException)
        {
            throw new QueryTranslationFailedException(ex);
        }

        // Materialization window. Nothing is caught here on purpose -- see the remarks above.
        using (enumerator)
        {
            var buffer = new List<T>();
            while (enumerator.MoveNext()) buffer.Add(enumerator.Current);
            return buffer.ToArray();
        }
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

    // #241: guarantees the deterministic total order server paging requires (OData §11.2.6.2).
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

        // #389: OData open COMPLEX types, ON BY DEFAULT; AddOhData(o => o.WithOpenTypes(false)) is
        // the escape hatch. Every complex type the EDM marks OpenType="true" carries a
        // DynamicPropertyDictionaryAnnotation naming the CLR member that backs its dynamic
        // properties; this layers one more resolver modifier that marks that member as
        // System.Text.Json extension data, so the bag serialises and binds FLAT (dynamic keys as
        // siblings of the declared properties) with no attribute on the consumer's model.
        //
        // Default-ON because a complex type with a dictionary member IS an open type: this same
        // builder has always emitted OpenType="true" for it and always omitted the member from the
        // declared properties, so leaving the wire nested made $metadata and the payload disagree
        // and made conformance something the developer had to know the spec to ask for. It is also
        // what Microsoft.AspNetCore.OData does -- its ODataResourceSerializer.AppendDynamicProperties
        // reads the SAME annotation and appends dynamic properties flat, with no opt-in flag
        // anywhere in that path.
        //
        // Flattening still RE-BINDS a body an existing adopter already sends -- the container stops
        // being a declared property, so {"Meta":{"Bag":{...}}} becomes a dynamic key named "Bag" --
        // and the echo of that mis-bound value is byte-identical to the correct one, so it is not
        // detectable by diffing responses. WarnWireShapeIsFlat below names every affected type once
        // at startup, which is the only signal available before the stored data is already wrong.
        // With the escape hatch taken, the map is not even built and effectiveJsonOptions is threaded
        // through reference-unchanged.
        //
        // Ordering: added AFTER the ignored-property modifier and BEFORE the per-request
        // nav-suppression modifier, which derives from these options. The three never contend for a
        // member -- nav suppression only removes EDM navigations (an open complex type has none),
        // and the ignored-property map is keyed by profile.ModelType (an ENTITY type), which a
        // container's declaring complex type can never be, so those two modifiers never see the
        // same JsonTypeInfo.
        //
        // THAT ORDERING IS AN INVARIANT, NOT AN INCIDENT, and OpenTypeModifierOrderingTests asserts
        // it. Two properties depend on it. (1) The open-type modifier snapshots its declared-name
        // collision set from typeInfo.Properties, so it must run while every EDM NAVIGATION is still
        // on the contract -- deriving nav suppression from startupJsonOptions instead of from these
        // options would put the removal first and silently convert a bag key that shadows a
        // navigation from a hard 500 into a navigation-shadowing leak. (2) Conversely it must run
        // AFTER the ignored-property modifier, whose removals are what make Ignore()d names invisible
        // to it -- which is why those names are threaded in separately below rather than read off the
        // contract.
        //
        // Note what the ignored-name argument does NOT do: it does not make the two modifiers meet.
        // They still never touch the same JsonTypeInfo. The withheld names cross as DATA, so the
        // open-type modifier can refuse a bag key spelled like a member the profile withholds.
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
        PrimeNavSuppression(effectiveJsonOptions, registration.EdmModel);

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

        // S7: a handler that throws (as opposed to returning an ODataError IResult, which every
        // deliberate error path in this file does) previously escaped as an empty, envelope-less
        // 500 -- no body, no logging, and the most common production failure mode (e.g. the
        // database is down) shipped with unspecified, §9.4-violating behavior. This is the
        // last-resort safety net: convert any exception that reaches here into the same OData
        // error envelope every other error response uses, with a generic message -- never
        // ex.Message or the stack trace, which could leak internal details (connection strings,
        // type names, file paths) to the client -- and log the real exception so operators can
        // actually diagnose the failure. Registered as the outermost group filter (added first)
        // so it also covers exceptions thrown by the OData-Version/$format/Accept and
        // OData-MaxVersion filters below, not just route handlers.
        //
        // #493: the one exception it declines to catch is a cancellation raised BECAUSE THE CLIENT
        // WENT AWAY -- there is no response left to write, so it is left to ASP.NET Core's own
        // cancellation handling. That is a statement about the REQUEST, not about the exception
        // type, and the filter used to test the type alone (`ex is not OperationCanceledException`).
        // The whole OCE family escaped, aborted or not -- and TaskCanceledException is what
        // HttpClient throws on ITS OWN timeout, i.e. a server-side dependency fault wearing
        // cancellation's clothes. Measured on the pre-fix tree with a handler that threw
        // TaskCanceledException on a request that was never aborted: HTTP 500 with an EMPTY body,
        // no envelope, and nothing logged by OhData at all -- precisely the failure mode this
        // filter exists to eliminate, on what is arguably the most common outbound-dependency
        // failure there is. The condition therefore now asks RequestAborted as well: an OCE on a
        // live request is an ordinary unhandled exception and is logged and enveloped like one.
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
                return ODataError(413, "RequestEntityTooLarge",
                    "The request body exceeds the maximum allowed size.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !ctx.HttpContext.RequestAborted.IsCancellationRequested)
            {
                groupLogger?.LogError(ex, "OhData: unhandled exception processing {Method} {Path}",
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
                    return ODataError(413, "RequestEntityTooLarge",
                        $"The request body ({len} bytes) exceeds the maximum allowed size ({limit} bytes).");
                }
            }
            return await next(ctx);
        });

        // Gap 1: Add OData-Version: 4.0 header to all responses (§8.2.6).
        // Batch 4: Return 406 Not Acceptable when the client cannot accept application/json (§8.2.3).
        // Batch 5: Validate $format query option (§11.2.12); it overrides the Accept header.
        // $metadata returns application/xml, so it is exempted from the JSON-only checks.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers["OData-Version"] = "4.0";

            string path = ctx.HttpContext.Request.Path.Value ?? "";
            bool isMetadata = path.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase);
            if (!isMetadata)
            {
                // §11.2.12: $format overrides Accept. Only application/json (and the shorthand
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
                    // §8.2.3 / RFC 7231 §5.3.2 (issue #182): reject Accept headers that don't include
                    // a media range this route can satisfy. Most routes produce application/json, but
                    // the raw-value routes are exceptions (like $metadata's application/xml above):
                    // /$count returns the count as text/plain (§11.2.6.5), and /{property}/$value
                    // returns the raw value as text/plain for scalars or application/octet-stream for
                    // byte[] (§11.2.4.3), so those segments can satisfy the corresponding types too.
                    // A client (e.g. Swagger UI, reading the content types those routes advertise in
                    // the OpenAPI document) that asks for text/plain on /$count is making a valid
                    // request and must not get a 406. Negotiation goes through AcceptHeaderPermits,
                    // which parses real media ranges and honors q-values rather than substring-scanning
                    // the header — so "application/*" and "text/*" match the way RFC 7231 requires, and
                    // "application/json;q=0" (meaning "not acceptable") correctly 406s.
                    string accept = ctx.HttpContext.Request.Headers.Accept.ToString();
                    if (!string.IsNullOrEmpty(accept))
                    {
                        bool isCount = path.EndsWith("/$count", StringComparison.OrdinalIgnoreCase);
                        bool isValue = path.EndsWith("/$value", StringComparison.OrdinalIgnoreCase);

                        // Producible sets are unchanged from the substring version — only the matching
                        // rule changed. $value produces JSON, text/plain, or octet-stream; $count
                        // produces JSON or text/plain; every other route produces JSON.
                        string[] producible = isValue
                            ? new[] { "application/json", "text/plain", "application/octet-stream" }
                            : isCount
                                ? new[] { "application/json", "text/plain" }
                                : new[] { "application/json" };

                        if (!AcceptHeaderPermits(accept, producible))
                        {
                            string producibleList = isValue
                                ? "application/json, text/plain, or application/octet-stream"
                                : isCount
                                    ? "application/json or text/plain"
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

        // Gap 7: Unbound functions/actions — registered once at service root level (§11.5.1)
        MapUnboundOperations(
            group, registration.UnboundOperations, effectiveJsonOptions, registration);

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

        return group;
    }

    // #489: Ignore() loses its EDM half under AdvancedConfigure, and the consequence is a value
    // oracle no reader derives from either half on its own.
    //
    // Ignore() withholds a property on TWO levels. The EDM removal rides the _configurators pipeline
    // in EntitySetProfile.VisitModelBuilder; runtime suppression (routes, wire, PATCH binding) is
    // applied separately from IgnoredPropertyNames. Overriding AdvancedConfigure returns from
    // VisitModelBuilder BEFORE the configurator pipeline runs, so the EDM half is ejected while the
    // runtime half still applies. That is the stated contract of the eject hatch and it is CORRECT --
    // the developer has taken full EDM ownership, and this is deliberately not "fixed":
    //
    //   * re-imposing OhData's Ignore() on top of the override would defeat the hatch outright, and
    //   * it would be arbitrary. HasOptional/HasRequired/HasMany ride the SAME pipeline and stay
    //     ejected; nothing distinguishes Ignore()'s configurator from theirs except that its
    //     consequence is a disclosure rather than a missing navigation. A half-ejected pipeline whose
    //     membership is decided by severity is a worse contract than the one it replaces.
    //
    // What is missing is the SIGNAL, so this is the WarnWireShapeIsFlat shape: a legitimate
    // configuration whose consequence is not predictable from either half gets one startup warning
    // naming it. With both features in play the property is back in $metadata and query-addressable
    // while the wire still omits it, so $filter over it answers truthfully one predicate at a time --
    // the value is never served and is still discoverable. In the ordinary case the EDM removal makes
    // the property indistinguishable from one that never existed (ODL fails the same "could not find
    // a property named…" it produces for a genuinely nonexistent name), so the 400 cannot confirm
    // existence.
    //
    // GATED ON THE EDM AS BUILT, not on the mere presence of the override. Re-applying
    // `configuration.EntityType.Ignore(...)` by hand inside the override is exactly what the
    // documentation prescribes, and it really does remove the property -- warning on that
    // configuration would fire on the correct one and teach developers to tune the warning out.
    // Silent, therefore, whenever the property is genuinely gone from the EDM.
    //
    // The query-capability half is deliberately NOT part of the gate. Whether $filter is live depends
    // on what the override re-enabled (taking the hatch also drops OhData's automatic
    // Filter()/OrderBy()/Select() calls), but $metadata advertises the property's name and type
    // regardless, and a capability the override adds later must not silently un-warn the profile.
    //
    // Emitted once per affected property per registration at startup, never per request.
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

    // #313: the startup diagnostic that stands in for the ceiling that MaxExpandTop no longer defaults to.
    //
    // Stage 1 removed an invented 1000 because the framework cannot know how large a child collection
    // is. That leaves a real exposure — a bare ?$expand=Children materializes the WHOLE child
    // collection — and nothing to point at it, since with no ceiling the shape answers 200 and looks
    // healthy. This names each affected navigation once at startup so the decision is made by someone
    // who knows the data, and it deliberately stops at informing: it prescribes no number, because
    // picking one is exactly the mistake stage 1 undid.
    //
    // The conditions are ALL of the conditions under which the exposure is live, which is what
    // keeps this from being noise:
    //   - ExpandEnabled  — false by DEFAULT, and by itself enough to silence the whole diagnostic for
    //                      a registration that never opts into $expand. This is the load-bearing one.
    //   - HasGetQueryable— the pushdown path is the only one that materializes a raw child collection
    //                      from the database; GetAll/Priority-1/GetById are out of scope (G11).
    //   - ExpandPushdownEnabled — NOT one of the five conditions #313's design lists, and it belongs.
    //                      MEASURED: with it false, /BeAuthors?$expand=Books over a seeded 5-book author
    //                      returns "Books":[] and issues no child query at all — no EngagedExpand is
    //                      built (see the gate at the ApplyIncludeFallback site), so the delegate path's
    //                      ServeRaw case no-ops over a graph nothing ever loaded. There is no
    //                      materialization to bound, so warning about it would name a knob that changes
    //                      nothing for that registration. It defaults to TRUE, so this narrows almost
    //                      nothing in practice — it just keeps the rule "all of the conditions under
    //                      which the exposure is live" honest rather than approximately true.
    //   - collection-valued — a single-valued navigation is one row and cannot be the DoS.
    //   - ServeRaw       — a delegate-backed navigation is never in the engaged tree. Resolved through
    //                      the SAME ResolveNavTreatment stage 5's route registration uses
    //                      (ResolveExpandPagingNavigations), over the SAME candidate set, so the
    //                      warning and the routes cannot drift — rather than through a per-profile
    //                      "owns no NavigationRouteDefinition" test. #415: this clause used to add
    //                      "and a BLANKED one (a sibling profile over the same EDM type disagrees) is
    //                      not served at all" — MEASURED FALSE at the ROOT, which is the only level
    //                      this diagnostic describes. Model B gives the URL-named set authority over
    //                      its own navigations, so a nav the old union-based check called Blank is
    //                      still served RAW and unbounded by /{Set}?$expand={Nav}, and the warning
    //                      went SILENT for exactly the profile that needed it whenever ANY sibling
    //                      over the same EDM type delegated the nav. #421 fixed that by resolving the
    //                      candidate set the way the root read path does — `new[] { profile }`. A
    //                      navigation THIS profile delegates still yields RunDelegate and is still
    //                      silent, which is the clause's actual point.
    //   - MaxExpandTop is null — with a ceiling set there is a bound, and #313 stage 2 already turns
    //                      the over-ceiling shape into a 400. Nothing to warn about.
    //
    // Emitted once per registration at startup, never per request.
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
                if (ResolveNavTreatment(nav.Name, candidates).Treatment != NavTreatment.ServeRaw) continue;

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

    // #440: the startup diagnostic for the OTHER half of #322's root cause — the profile's
    // navigation set and the EDM's disagree, and #322's projection fix reconciled them for the
    // QUERY PLAN only.
    //
    // A WARNING, not a throw. The shape that triggers this is `public Publisher? Publisher { get; set; }`
    // beside an `int? PublisherId` — the ordinary EF Core reference navigation on a profiled entity,
    // with no attributes and no fluent declaration. Throwing would break startup for every adopter
    // who has one, with no migration but editing every profile.
    //
    // WHY IT STILL EARNS ITS PLACE NOW THAT BOTH #440 SYMPTOMS ARE FIXED. It no longer reports a
    // DEFECT — it reports the disagreement itself, which the fixes do not remove and cannot: the
    // framework must not decide whether an undeclared navigation was meant to be exposed. $metadata
    // still advertises a navigation that this entity set will never serve by any means — $expand
    // omits it, and no navigation route stands behind it — so a client generated from $metadata
    // asks for related data it can never receive, with nothing in any response saying why. That is
    // a true, actionable, startup-time statement about a configuration gap, and it is the only
    // channel that reaches the person who can close it.
    //
    // #461 WIDENED IT TO THE WRITE PATH, and that is a correction, not an addition. The message said
    // "will never serve it", which speaks only of reads — and it was incomplete in BOTH directions at
    // once. Before #461 the write path did not merely fail to serve the navigation, it QUIETLY
    // ACCEPTED it: the deep-insert strip set was built from the profile-declared navigation names, so
    // a nested value for an undeclared one was bound and handed to the Post handler with
    // AllowDeepWrites at its default of false. The strip set now subtracts the same EDM navigation
    // set #446 established as the authority, so the write path agrees with the read path — and the
    // sentence naming that is added here, in the commit that made it true, rather than left for a
    // reader to infer from "serve".
    //
    // #457 WIDENED THE SAME SENTENCE FROM "a POST body" TO "a write body". Deep update (§11.4.3.1)
    // was documented out of scope but not enforced, so PUT forwarded the nested value and PATCH
    // bound it into the Delta<TModel>; naming POST alone would now understate the (correct)
    // behaviour on exactly the two verbs that used to get it wrong.
    //
    // WHAT IS NOT LISTED, deliberately, AND WHY THE LIST KEEPS SHRINKING. This message states only
    // what is still true at the moment it is emitted, and every time the framework closes one of the
    // consequences the sentence naming it comes out in the SAME commit. Already removed:
    //   - pushdown disqualification (#322): an undeclared navigation used to abandon $select/$expand
    //     pushdown for the whole entity set. Fixed; never named here. A test asserts the message
    //     never contains "pushdown", "$filter" or "Include".
    //   - structural-property routes (#440 symptom 2): GET /{Set}({key})/{Nav}, its /$value, and
    //     PUT/PATCH/DELETE alongside a Patch handler used to register over the navigation. Route
    //     registration now subtracts the EDM's navigation names, so those templates do not exist.
    //   - "$expand answers 200 with null" (#440 symptom 1): it answers 200 with the navigation
    //     OMITTED now, which is a different and much smaller claim — the payload no longer asserts
    //     that the relationship is empty. The message says "omitted", and must keep saying whatever
    //     is actually emitted.
    // #313 stage 3 shipped a diagnostic that outlived the behaviour it described and needed a
    // follow-up to correct; this comment exists so the next person to fix a consequence edits the
    // sentence rather than leaving it.
    //
    // The gate is "the remaining consequence is REACHABLE on this profile": ExpandEnabled. With
    // $expand off, the entity set expands nothing at all, so an undeclared navigation is no more
    // unreachable than a declared delegate-less one and there is nothing to distinguish. It is also
    // the load-bearing half of the targeting, exactly as in WarnUnboundedBareExpand: ExpandEnabled
    // is false by default. PropertyAccessEnabled/HasGetById/HasPatch are no longer part of the gate
    // — they gated the property-route consequence, which no longer exists.
    //
    // Emitted once per registration at startup, never per request.
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

    // Leg 3 (docs-fidelity): a bound function/action's success response goes through
    // WrapBoundOpResult (see below), which chooses one of three shapes at runtime based on the
    // operation's actual return value: an IEnumerable<TModel> result gets the collection
    // envelope, a TModel result gets the single-entity envelope (documented as bare TModel,
    // mirroring the GetById precedent), and anything else is returned largely as-is. Mirror
    // that same dispatch here, using BoundOperationDefinition.ReturnType (the delegate's
    // declared, Task/ValueTask-unwrapped return type, computed once at bind time) so the
    // documented schema matches what WrapBoundOpResult will actually produce.
    private static void AddBoundOperationProduces<TModel>(RouteHandlerBuilder rb, BoundOperationDefinition op)
        where TModel : class
    {
        Type? returnType = op.ReturnType;
        if (returnType is not null)
        {
            if (returnType == typeof(TModel))
            {
                rb.Produces<TModel>(200);
            }
            // #497: the SAME element predicate WrapBoundOpResult applies — assignability, not
            // equality — so a delegate declared `Task<List<TDerived>>` is documented as the
            // collection envelope it will actually be served in, rather than as a bare
            // List<TDerived>. One predicate, two sites; they must move together.
            else if (returnType != typeof(string) &&
                     typeof(System.Collections.IEnumerable).IsAssignableFrom(returnType) &&
                     returnType.GetInterfaces().Concat(new[] { returnType })
                         .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                                   && typeof(TModel).IsAssignableFrom(i.GetGenericArguments()[0])))
            {
                rb.Produces<ODataCollectionResponse<TModel>>(200);
            }
            else
            {
                rb.Produces(200, returnType, "application/json");
            }
        }
        rb.Produces(204);
    }

    // Issue #181: build the query-parameter documentation marker for a bound/unbound *function*.
    // Each of these parameters is read from the query string at request time (see the function
    // registration loops), but the handler binds no minimal-API parameters, so ApiExplorer would
    // otherwise see none of them and the OpenAPI document would list "parameters: []". A trailing
    // CancellationToken is already excluded from Parameters by BoundOperationDefinition.From /
    // UnboundOperationDefinition.From. For entity-level functions the leading key parameter
    // (Parameters[0]) is a route parameter already documented via BindingSource.Path, so it is
    // skipped here. Returns null when there is nothing to document.
    private static OhDataQueryParametersMetadata? BuildFunctionQueryParametersMetadata(
        ParameterInfo[] parameters, bool skipKey)
    {
        int start = skipKey ? 1 : 0;
        if (parameters.Length <= start) return null;

        var list = new List<OhDataQueryParameter>(parameters.Length - start);
        for (int i = start; i < parameters.Length; i++)
        {
            var p = parameters[i];
            list.Add(new OhDataQueryParameter
            {
                Name = p.Name!,
                Type = p.ParameterType,
                IsRequired = !p.HasDefaultValue,
            });
        }

        return new OhDataQueryParametersMetadata { Parameters = list };
    }

    private static void MapUnboundOperations(
        RouteGroupBuilder group,
        IReadOnlyList<UnboundOperationDefinition> unboundOps,
        JsonSerializerOptions? jsonOptions,
        OhDataRegistration registration)
    {
        foreach (var op in unboundOps)
        {
            var opCapture = op;
            if (!op.IsAction)
            {
                // Unbound function: GET /{prefix}/{FunctionName}?params
                var rb = group.MapGet($"/{op.Name}", async (HttpContext ctx, CancellationToken ct) =>
                {
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
                }).Produces(400);
                AddUnboundOperationProduces(rb, opCapture);
                // Issue #181: document the function's query-string parameters.
                var unboundFnQueryParams = BuildFunctionQueryParametersMetadata(opCapture.Parameters, skipKey: false);
                if (unboundFnQueryParams is not null) rb.WithMetadata(unboundFnQueryParams);
            }
            else
            {
                // Unbound action: POST /{prefix}/{ActionName} with JSON body
                var rb = group.MapPost($"/{op.Name}", async (HttpContext ctx, CancellationToken ct) =>
                {
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
                }).Produces(400).Produces(415);
                AddUnboundOperationProduces(rb, opCapture);
                // Leg 2: an action's parameters are deserialized by name out of a JSON body object
                // (see the loop above), not a single bound CLR type. #184: synthesize a POCO whose
                // properties are exactly those parameters so the OpenAPI body schema shows the real
                // shape instead of an empty {}. The prose description is retained alongside it.
                if (opCapture.Parameters.Length > 0)
                {
                    rb.WithMetadata(new OhDataRequestBodyMetadata
                    {
                        // #499: the key must carry registration identity -- "Unbound.{Name}" alone
                        // is scoped to nothing but the operation name, so two registrations
                        // declaring an unbound operation of the same name collided even when
                        // nothing else about them overlapped (the worst of the three sites, per
                        // #499/#425, since it doesn't even carry an entity set name).
                        BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                            $"{registration.Name}.Unbound.{opCapture.Name}", opCapture.Parameters),
                        Description = "JSON object with the action's parameters: " +
                            string.Join(", ", opCapture.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                    });
                }
            }
        }
    }

    // #495: the options for envelopes whose ENTIRE content is framework-generated -- every OData
    // error envelope, and the service document. Deliberately a standalone, host-free instance
    // rather than the registration's own `jsonOptions` (which derives from the host's and so
    // carries its converters, encoder and key policy by design, #252).
    //
    // An error envelope's members are `error`/`code`/`message`/`target`, the service document's are
    // `@odata.context`/`value`: contractual identifiers from the OData formats, not names any
    // policy may rewrite. Their values are strings this framework generated -- no user model data
    // anywhere -- so there is nothing here for a host converter or naming policy to have an opinion
    // about, and two things for it to break:
    //
    //  * SHAPE. These envelopes are Dictionary<string, ...>, so a host DictionaryKeyPolicy applies
    //    to the keys. Measured on the pre-fix tree with the host's HttpJsonOptions set to
    //    SnakeCaseUpper: {"ERROR":{"CODE":"NotFound","MESSAGE":...}} -- every error response the
    //    framework produces, on every route, loses the shape a client switches on.
    //  * FAULT. A host converter that THROWS took the envelope with it: the dictionary was handed
    //    to Results.BadRequest/NotFound/Json and serialized at IResult-execute time, i.e. after the
    //    endpoint-filter chain unwound (the #396 hazard), so the group filter could neither catch
    //    it nor log it. Measured: an empty 500 with no OhData log -- including for the group
    //    filter's OWN 500 envelope, which is the one response that must never fail.
    //
    // Both are closed by rendering here, inside the pipeline, with options this framework owns.
    //
    // The Encoder is set EXPLICITLY and is not decoration. ASP.NET Core's own
    // Microsoft.AspNetCore.Http.Json.JsonOptions overrides the Web default with
    // UnsafeRelaxedJsonEscaping, on the stated grounds that its output goes straight to a response
    // body rather than into an HTML page -- which is equally true here. A bare
    // `new JsonSerializerOptions()` leaves Encoder null, i.e. JavaScriptEncoder.Default, and that
    // is NOT the same bytes: measured on a default host, a 404 whose message quotes the requested
    // key emitted each apostrophe as a six-character unicode escape rather than literally
    // (78 -> 88 bytes), and every angle bracket, ampersand and non-ASCII character would go the
    // same way. Everything else about
    // ASP.NET Core's defaults that could reach these envelopes already matches -- DictionaryKeyPolicy
    // and WriteIndented are unset there too, and PropertyNamingPolicy cannot apply (dictionary keys
    // are not property names, and ServiceDocumentEntry pins its own with [JsonPropertyName]).
    // ErrorEnvelopeFidelityTests pins the exact bytes rather than the reasoning.
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

    // #396: serialize NOW, inside the endpoint-filter pipeline, and return the bytes.
    //
    // An IResult returned from a minimal-API handler is executed by RequestDelegateFactory AFTER
    // the whole endpoint-filter chain has unwound, so anything that throws while the result
    // executes is outside the group-level exception filter's try -- and by that point the status
    // line and headers are already on the wire. Measured on the pre-fix tree: a bound function
    // whose return value faults during serialization logs "Request finished ... - 200" and the
    // client gets HTTP 200 with a truncated body. That is strictly worse than the envelope-less
    // 500 the S7 filter was written to eliminate: a success status with a malformed body defeats
    // client-side error handling completely, where a 500 at least fails loudly.
    //
    // Most routes are already immune, and for a reason worth stating: they build their response as
    // a JsonNode inside the handler (SerializeBounded / ODataEntityNode /
    // ApplyCollectionPipelineAsync), so every user-supplied converter, getter and ToString runs
    // where the filter can still see it, and executing a materialized JsonNode afterwards cannot
    // re-enter user code. The routes that handed a RAW CLR object to Results.Json -- the bound and
    // unbound function/action results, and the structural-property read envelope -- had no such
    // stage. This gives them one.
    //
    // Byte-identical to Results.Json by construction: same declared TValue (so JsonSerializer
    // resolves the same JsonTypeInfo -- do NOT "simplify" the call sites to pass object), same
    // options instance, same "application/json; charset=utf-8". The one header difference is that
    // Content-Length is now written explicitly instead of being inferred by the server from a
    // fully-buffered response body.
    //
    // Cancellation is unaffected: SerializeToUtf8Bytes is synchronous and takes no token, so it
    // cannot manufacture an OperationCanceledException, and the S7 filter still declines to catch
    // one raised by a user converter.
    //
    // Cost, measured (OperationResultBufferingBenchmarks, BenchmarkDotNet, against a Stream.Null
    // write so the baseline is as cheap as it can possibly be): a small DTO -- the shape a bound
    // function actually returns -- gets FASTER, 0.74x, because SerializeAsync's async state machine
    // and flush cost dominate a payload of tens of bytes. A 189 KB result costs +18% and a 9.4 MB
    // result +74%, both entirely the extra full-payload byte[] (and, past 85 KB, its LOH traffic).
    // That is accepted rather than optimised away: driving a pooled Utf8JsonWriter directly is
    // faster still at every size (measured, kept as an arm in that class) but requires transcribing
    // the JsonSerializerOptions -> JsonWriterOptions mapping by hand, and getting one member of it
    // wrong or missing one a future runtime adds would change the response bytes -- against a fix
    // whose whole non-faulting requirement is byte-identity. Worth revisiting only behind
    // byte-for-byte differential tests. For scale: the entity and collection routes already
    // materialise a whole JsonNode tree for payloads of this size, which allocates far more than a
    // byte[] does, so this is nowhere near the framework's buffering ceiling.
    private static IResult PreRenderedJson<TValue>(
        TValue value, JsonSerializerOptions options, int statusCode = StatusCodes.Status200OK)
        => new Utf8JsonHttpResult(JsonSerializer.SerializeToUtf8Bytes(value, options), statusCode);

    // #495: the owned options, with the OData envelope's contractual dictionary keys protected.
    //
    // Every OData success envelope this framework emits is a Dictionary<string, ...> whose keys --
    // `@odata.context`, `value`, `@odata.count`, `@odata.nextLink` -- are identifiers the format
    // defines, not names a policy may rewrite. System.Text.Json applies DictionaryKeyPolicy to
    // them, so a host that set one reshaped every such response: measured with the host's
    // HttpJsonOptions DictionaryKeyPolicy at SnakeCaseUpper, a plain collection GET came back as
    // {"@ODATA.CONTEXT":...,"VALUE":[...]} -- parseable JSON that no OData client can read. (The
    // entity routes were never affected: they emit a JsonObject, whose member names STJ writes
    // verbatim.) The registration's own options inherit the policy from the host's by construction
    // (#252 copies the host instance and overrides only PropertyNamingPolicy), so clearing it has
    // to happen here.
    //
    // Everything else is left exactly as the host configured it -- converters, encoder,
    // PropertyNamingPolicy -- because the values in these envelopes ARE payload, and #252's
    // division is that OhData owns the names and the host owns value formatting.
    //
    // Costs nothing on a host that set no policy, which is every default configuration: the source
    // instance is returned by reference, so no options are copied and no second JsonTypeInfo cache
    // is created. Only a host that really set one pays for the derived instance, once per source
    // instance (the sources here are per-registration, built at startup).
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions>
        _envelopeOptionsCache = new();

    private static JsonSerializerOptions EnvelopeOptions(JsonSerializerOptions source)
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

    // Every keyed route peels off the FormatException that ODataKeyParser.Parse throws on an
    // unparseable key and maps it to the same 400 envelope. `withTarget` preserves the existing
    // split: entity-addressed routes point `target` at "key"; navigation routes omit it.
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

    // B1 fix: capability-flag enforcement (Minimal item 7 — "parse the option or reject it").
    // FilterEnabled/OrderByEnabled/SelectEnabled/ExpandEnabled/CountEnabled were previously
    // decorative on the GetQueryable and Priority-1 collection paths: the flags only drove EDM
    // model-bound capability annotations (Swagger/$metadata advertisement), never a runtime
    // gate. This helper is the runtime gate: a disabled option present in the query string is
    // rejected with a specific "UnsupportedQueryOption" error naming the option, mirroring the
    // wording the GetAll path already uses for its own wholesale $filter/$orderby/$top/$skip
    // rejection (it structurally cannot support those regardless of any flag).
    private static IResult? CheckDisabledQueryOption(HttpContext ctx, string queryOptionName, bool enabled, string flagName)
    {
        if (enabled) return null;
        if (!ctx.Request.Query.ContainsKey(queryOptionName)) return null;
        return ODataError(400, "UnsupportedQueryOption",
            $"This resource does not support {queryOptionName}. Set {flagName} = true on the " +
            "profile (or the corresponding EntitySetDefaults property) to enable it.");
    }

    // Applies CheckDisabledQueryOption across the full $filter/$orderby/$select/$expand/$count
    // set — the gate used by the GetQueryable and Priority-1 collection GET routes. $filter and
    // $orderby are optionally skipped (checkFilterOrderBy: false) on paths that already reject
    // them structurally regardless of the flag (the GetAll path, which has no ApplyTo pipeline).
    private static IResult? CheckCollectionQueryOptionCapabilities(
        HttpContext ctx, IEntitySetEndpointSource source, bool checkFilterOrderBy = true)
    {
        // #196: reject system options this framework does not implement at all, rather than
        // ignoring them silently (Minimal-conformance item 7 — "parse the option or reject it").
        IResult? unimplemented = CheckUnimplementedCollectionQueryOptions(ctx);
        if (unimplemented is not null) return unimplemented;

        if (checkFilterOrderBy)
        {
            IResult? r = CheckDisabledQueryOption(ctx, "$filter", source.FilterEnabled, nameof(IEntitySetEndpointSource.FilterEnabled));
            if (r is not null) return r;
            r = CheckDisabledQueryOption(ctx, "$orderby", source.OrderByEnabled, nameof(IEntitySetEndpointSource.OrderByEnabled));
            if (r is not null) return r;
        }

        IResult? sr = CheckDisabledQueryOption(ctx, "$select", source.SelectEnabled, nameof(IEntitySetEndpointSource.SelectEnabled));
        if (sr is not null) return sr;
        sr = CheckDisabledQueryOption(ctx, "$expand", source.ExpandEnabled, nameof(IEntitySetEndpointSource.ExpandEnabled));
        if (sr is not null) return sr;
        sr = CheckDisabledQueryOption(ctx, "$count", source.CountEnabled, nameof(IEntitySetEndpointSource.CountEnabled));
        return sr;
    }

    // S1/B1 fix: system query options the navigation collection GET route does not implement.
    // $select, $orderby, $skip, $top, and $count ARE implemented (parsed directly off the query
    // string in the nav-route handler); everything else — most notably $filter — was previously
    // ignored outright rather than rejected, so a client asking to filter a navigation collection
    // silently got back the whole, unfiltered set (S1).
    private static readonly string[] s_navUnsupportedSystemOptions =
    {
        "$filter", "$expand", "$search", "$apply", "$compute", "$skiptoken", "$deltatoken",
    };

    // #196: system query options the *main* collection GET routes do not implement at all — as
    // opposed to the capability-gated $filter/$orderby/$select/$expand/$count (handled by
    // CheckCollectionQueryOptionCapabilities) or the implemented $top/$skip/$search/$skiptoken.
    // These were previously ignored silently on the main route even though the navigation route
    // already rejected them. $apply/$compute are unimplemented aggregation options ($compute is
    // 4.01-only and blocked by the pinned OData package range); $index is a 4.01 ordered-insert
    // option; $deltatoken belongs to delta/change-tracking. Ignoring a known option violates
    // Minimal-conformance item 7 ("parse the option or reject the request").
    private static readonly string[] s_collectionUnimplementedSystemOptions =
    {
        "$apply", "$compute", "$index", "$deltatoken",
    };

    private static IResult? CheckUnimplementedCollectionQueryOptions(HttpContext ctx)
    {
        string? option = s_collectionUnimplementedSystemOptions
            .FirstOrDefault(o => ctx.Request.Query.ContainsKey(o));
        if (option is not null)
        {
            return ODataError(400, "UnsupportedQueryOption",
                $"The query option '{option}' is not supported.");
        }
        return null;
    }

    private static IResult? CheckNavUnsupportedQueryOptions(HttpContext ctx)
    {
        string? option = s_navUnsupportedSystemOptions
            .FirstOrDefault(o => ctx.Request.Query.ContainsKey(o));
        if (option is not null)
        {
            return ODataError(400, "UnsupportedQueryOption",
                $"This navigation route does not support {option}. Supported query options " +
                "are $select, $orderby, $skip, $top, and $count.");
        }
        return null;
    }

    // #202: per-entity-set validation settings, built once per set from the source's resolved
    // complexity limits (MaxExpansionDepth default 3, node counts 10000/1000/1000 as before) so an
    // implementor can tighten them per profile or globally via WithDefaults. AllowedQueryOptions=All
    // etc. is retained so the only checks these run are the per-property allowlist annotations and
    // the complexity ceilings — $top/$skip/$count keep their own dedicated enforcement (see the
    // ValidatePropertyAllowlists remark). MaxExpansionDepth is now enforced (was hardcoded 0/disabled):
    // a $expand nesting deeper than the limit — including a $levels that resolves deeper (#206) — is
    // rejected with 400 by Microsoft's SelectExpandQueryValidator rather than silently truncated.
    private static ODataValidationSettings BuildValidationSettings(IEntitySetEndpointSource source) => new()
    {
        AllowedQueryOptions = AllowedQueryOptions.All,
        AllowedArithmeticOperators = AllowedArithmeticOperators.All,
        AllowedFunctions = AllowedFunctions.AllFunctions,
        AllowedLogicalOperators = AllowedLogicalOperators.All,
        MaxExpansionDepth = source.MaxExpansionDepth,
        MaxAnyAllExpressionDepth = source.MaxAnyAllExpressionDepth,
        MaxNodeCount = source.MaxFilterNodeCount,
        MaxOrderByNodeCount = source.MaxOrderByNodeCount,
    };

    // B1 fix (property allowlists): FilterProperties/OrderByProperties/SelectProperties/
    // ExpandProperties are wired into the EDM at startup via EntityTypeConfiguration.Filter/
    // .OrderBy/.Select/.Expand (EntitySetProfile.cs), which mark the non-allowlisted properties
    // NotFilterable/NotSortable/NotSelectable/NotExpandable in the model. Those restrictions are
    // only enforced when something calls ODataQueryOptions.Validate(...) — ApplyTo alone ignores
    // them. The coarse per-category enable/disable is handled separately by
    // CheckCollectionQueryOptionCapabilities with its own "UnsupportedQueryOption" code and
    // message; this only needs to surface *a* 400 (via the existing ODataException catch
    // clauses), so Microsoft's default validator wording is fine.
    //
    // Runs only the per-option validators that enforce the property allowlists
    // (NotFilterable/NotSortable/NotSelectable/NotExpandable model-bound annotations written by
    // FilterProperties/OrderByProperties/SelectProperties/ExpandProperties at EDM-build time).
    // Deliberately NOT ODataQueryOptions.Validate(settings): the whole-options validator also
    // runs the Top validator, and the mere presence of model-bound settings on the entity type
    // (created as a side effect of entityType.Filter(...)/.Select(...) etc.) makes the
    // model-bound MaxTop default to 0, which would reject every $top outright. $top/$skip/$count
    // have their own dedicated enforcement in this file (source.MaxTop clamp, m8 negative-value
    // 400s, CountEnabled gate), so only the three property-scoped validators run here. Throws
    // Microsoft.OData.ODataException on violation, which each route's existing catch clause maps
    // to a 400 OData error.
    private static void ValidatePropertyAllowlists<TModel>(ODataQueryOptions<TModel> options, ODataValidationSettings settings)
    {
        options.Filter?.Validate(settings);
        options.OrderBy?.Validate(settings);
        options.SelectExpand?.Validate(settings);
    }

    // #402: construct ODataQueryOptions inside a try whose scope is EXACTLY the construction, and
    // whose catch is deliberately broad.
    //
    // Every route that reads query options used to build them inline inside the handler's
    // whole-body `try`, whose only mapped catch is `catch (ODataException)`. That is wrong on both
    // axes. Measured (see QueryOptionConstructionFaultTests, which probes every system query option
    // this framework accepts with empty, malformed and hostile values):
    //
    //   * Microsoft.OData.ODataException  — empty value for $filter/$orderby/$top/$skip/$count/
    //                                       $search/$apply/$compute, thrown from the constructor.
    //   * System.ArgumentException        — `$skiptoken=` (or a bare `$skiptoken`), thrown by
    //                                       SkipTokenQueryOption's ctor via BuildQueryOptions.
    //                                       NOT an ODataException, so it escaped to the group
    //                                       filter as a 500 on a request any client can send.
    //
    // The tempting fix — add ArgumentException to the catch list — repeats a mistake this file has
    // already made twice (ValidateOrThrow, the continuation-token readers): the throw set of
    // somebody else's constructor is not ours to enumerate. ODataQueryOptions.BuildQueryOptions
    // news up one option object per recognized $-option, each with its own argument validation, and
    // nothing in Microsoft.AspNetCore.OData's contract says those are ODataException-only — the
    // $skiptoken case proves they are not, and a package bump can add another tomorrow.
    //
    // So: catch everything. That is defensible HERE and only here, because the try contains nothing
    // but option parsing — no handler, no data source, no serialization. Any failure inside it is by
    // definition a statement about the request URL, which makes 400 the right answer for the whole
    // set, known and unknown. That is precisely why the scope had to be tightened first: the old
    // whole-handler try also contains InvokeGetQueryableAsync, so broadening the catch THERE would
    // have turned a genuine handler fault (database down) into a 400 and destroyed the S7 guarantee
    // from the other direction.
    //
    // ODataException keeps its existing message pass-through, so the eight empty-value cases above
    // stay byte-identical. Anything else gets a generic message (an unrecognized exception type's
    // Message is not vetted for client exposure — same reasoning as the S7 filter) and the real
    // exception is logged at Warning so an operator can see what actually happened.
    //
    // True on success (options set); false on failure (error set to the 400 result to return).
    //
    // #426: the ODataQueryContext is built HERE, per request, and this method takes the IEdmModel
    // rather than a context so that no caller is able to hand it a shared one. It used to be built
    // once per entity set at startup and captured by all five read-route closures, which is a
    // documented contract violation, not merely a risky optimisation: ODataQueryOptions' own
    // constructor WRITES to the context it is given —
    //
    //     Contract.Assert(context.RequestContainer == null);      // ODataQueryOptions.cs:76
    //     context.RequestContainer = request.GetRouteServices();
    //     context.Request = request;
    //
    // — and Initialize then reads `context.Request` back off that shared field (:1165,
    // IsNoDollarQueryEnable) rather than using the constructor's own `request` parameter. Two
    // requests in flight against one context therefore race on Request: the second write lands
    // between the first request's write and its read, so the first request dereferences a DIFFERENT
    // request's HttpContext — concurrently with that request's own owner, and in production
    // possibly after it has completed and been recycled (DefaultHttpContext.Uninitialize nulls
    // _features). Either way the FeatureReferences read tears and throws NullReferenceException out
    // of DefaultHttpContext.get_RequestServices — swallowed by the broad catch below and relabelled
    // 400, which is why #384 was first reported as a 500 and presents as a 400 today. Measured on
    // the pre-fix tree: 16-89 failures (four runs: 43/31/16/89) in 32,000 constructions over 16
    // threads sharing one context, every one of them a NullReferenceException thrown from
    // DefaultHttpContext.get_RequestServices; 0 in 32,000 with a fresh context each, and 0 across
    // 55 repeats of the full solution suite (QueryContextPerRequestTests). MS's Contract.Assert is
    // the library stating outright that the type is per-request, and every construction site in
    // Microsoft.AspNetCore.OData itself builds one per request.
    //
    // The (IEdmModel, IEdmType, ODataPath) overload does NOT work as the cheap path here, despite
    // being the obvious candidate: it leaves ElementClrType null, and ODataQueryOptions<TEntity>'s
    // constructor throws ArgumentException ("ElementClrTypeNull") unless
    // Context.ElementClrType == typeof(TEntity). FilterQueryOption.ApplyTo throws on a null
    // ElementClrType too. The CLR-type overload is the only usable one, and it is what MS uses.
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

    // #254 (E1): enforce the per-navigation MaxExpandTop ceiling on an EXPLICIT nested $top at every
    // depth of the $expand tree, before any handler or query runs. Runs IN ADDITION TO (never instead
    // of) ValidatePropertyAllowlists on each collection read path.
    //
    // Deliberately depth- AND pushdown-independent: a nested $top is rejected on a delegate-backed
    // navigation too. That mirrors the root MaxTop, which 400s an over-large $top on every read path
    // regardless of how the collection is ultimately loaded — the ceiling is a statement about what
    // the client may ask for, not about how the server would have served it.
    //
    // Returns the 400 OData error to return, or null when every nested $top is within the ceiling
    // (including the no-ceiling case, MaxExpandTop = null).
    private static IResult? ValidateNestedTopCeiling(SelectExpandClause? clause, int? cap)
    {
        if (clause is null || cap is not int max) return null;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            if (item.TopOption is long top && top > max)
            {
                string nav = item.PathToNavigationProperty.FirstSegment.Identifier;
                return ODataError(400, "InvalidQueryOption",
                    $"The value of '$top' ({top}) on the expanded navigation '{nav}' exceeds the maximum allowed value ({max}).");
            }

            IResult? deeper = ValidateNestedTopCeiling(item.SelectAndExpand, cap);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    // #429 (#202's unshipped breadth guard): reject a $expand tree containing more navigation
    // expansions than the resolved MaxExpandBreadth, before any handler or query runs.
    //
    // WHY THIS EXISTS ALONGSIDE MaxExpansionDepth. Depth is one axis. Translation cost for a pushed
    // nested projection multiplies by ~3 per level AND by the number of navigations expanded at each
    // level, so capping depth alone leaves the other factor free. Measured at the DEFAULT depth of 3
    // on a six-navigation model, with no breadth guard: 4.1 s of single-core CPU for a 1,952-byte
    // response. The EF compiled-query cache is no defence — each distinct navigation SUBSET is a
    // distinct cache key, so cycling subsets never warms it.
    //
    // WHY THE COUNT SPANS THE WHOLE TREE. A per-level cap of B under a depth ceiling of D still
    // admits B^D expansions (6^6 = 55,986 at the shipped ceiling). Counting every node bounds the
    // two axes together. Counting DISTINCT NAMES would be weaker still — the most expensive shapes
    // measured reuse six names over six levels.
    //
    // Deliberately pushdown-independent, like ValidateNestedTopCeiling: a delegate-backed expansion
    // at this breadth is N+1-per-level expensive rather than 3^n expensive, but it is still a
    // statement about what the client may ASK for, not about how the server would have served it.
    //
    // Returns the 400 OData error to return, or null when the request is within the limit.
    private static IResult? ValidateExpandBreadth(SelectExpandClause? clause, int cap, int maxExpansionDepth)
    {
        if (clause is null) return null;
        int count = CountExpandNodes((SelectExpandClause)clause, maxExpansionDepth, cap);
        if (count <= cap) return null;

        // The message states the LIMIT, not the request's actual count: CountExpandNodes stops as
        // soon as the limit is passed, because an adversarial tree is exactly the input we must not
        // walk in full in order to reject it. The limit is the actionable half anyway.
        return ODataError(400, "InvalidQueryOption",
            $"The request expands more than {cap} navigations. '$expand' is limited to {cap} " +
            "navigation expansions counted across every level of the expansion tree (a " +
            "'$levels=N' expansion counts as N). Request fewer navigations, or raise " +
            "MaxExpandBreadth on the entity set profile or in WithDefaults.");
    }

    // Counts navigation expansions in the whole $expand tree, stopping as soon as <paramref
    // name="cap"/> is exceeded. A $levels=N item counts as N — its resolved level count, through the
    // SAME ResolveLevelsBudget the loaders use (#428), so the guard cannot disagree with them about
    // what a $levels resolves to — because that is what it costs: one nested projection level each,
    // exactly like the equivalent explicit chain.
    private static int CountExpandNodes(SelectExpandClause clause, int maxExpansionDepth, int cap)
    {
        int count = 0;
        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            count += item.LevelsOption is { } lv
                ? Math.Max(1, ResolveLevelsBudget(lv.IsMaxLevel, lv.Level, maxExpansionDepth, MaxNestedExpandDepth))
                : 1;
            if (count > cap) return count;

            if (item.SelectAndExpand is { } nested)
            {
                count += CountExpandNodes(nested, maxExpansionDepth, cap - count);
                if (count > cap) return count;
            }
        }
        return count;
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
    private static readonly JsonSerializerOptions _pascalCaseSerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
    };

    // Unified collection pipeline: Serialize → ETag → Expand → Select.
    // Serialises exactly once using the owned jsonOptions (defensively falls back to the
    // PascalCase _pascalCaseSerializerOptions if ever null — in practice it is always supplied).
    // #294: a nested $top/$skip rejected against a delegate-backed navigation somewhere in the
    // expand tree throws Microsoft.OData.ODataException out of ExpandLevelAsync — every caller of
    // this method already catches that exception and converts it to 400 InvalidQueryOption.
    private static async Task<(JsonArray Items, List<string>? SelectedProps)> ApplyCollectionPipelineAsync(
        object[] originalItems,
        ODataQueryOptions options,
        IEntitySetEndpointSource source,
        IEntitySetEndpointSource requestSource,
        JsonSerializerOptions? jsonOptions,
        IEdmEntityType? rootEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        CancellationToken ct,
        HashSet<string>? pushedLevelsNavNames = null,
        IReadOnlyList<EngagedExpand>? engagedExpandNavs = null,
        bool singleEntityRead = false)
    {
        // Stage 1: Serialize once using the configured naming policy.
        // #325/#326 (Option B): bounded by the root $expand clause (and any pushed $levels
        // budget), never by the object graph — see SerializeBounded's remarks. This is what makes
        // a plain GET over a self-referential/bidirectional model with tracked-entity relationship
        // fixup safe: navigations outside the clause are never handed to System.Text.Json at all,
        // so a cycle among them is structurally unreachable.
        var serializerOptions = jsonOptions ?? _pascalCaseSerializerOptions;
        SelectExpandClause? rootClauseForSerialize = options.SelectExpand?.SelectExpandClause;

        // #466: the RAW substrate's own $levels budget, unioned onto the PUSHED one.
        //
        // Before this, `levelsNavNames` was exactly CollectPushedLevelsNavNames' answer — the
        // navigations the EF projection recursed with BuildLevelsNavAccess — and it is null on
        // GetAll, GetById, Priority-1 and a non-EF GetQueryable. BuildExpandLookup seeds a levels
        // budget ONLY for a name in this set, so on those four paths TryKeepNav dropped the
        // self-navigation below level 1 and `$levels=N` served ONE level, silently, while the
        // explicit nested spelling of the same request served all N. Two spellings of one request
        // must not give different answers.
        //
        // Nothing needs to LOAD anything for the raw substrate: the related rows are already in the
        // CLR graph the handler returned (an EF fixup-populated tree, an in-memory object graph),
        // and SerializeBounded reads them by reflection. Seeding the budget is therefore the whole
        // fix — the same walker/keep-rule the explicit spelling already rides.
        //
        // MEMBERSHIP IS `ServeRaw AND some candidate has an opinion`, resolved through the SAME
        // ResolveNavTreatment every other site uses, PER LEVEL: RunDelegate/Blank are excluded
        // because the raw graph is NOT their answer (a delegate-backed $levels takes
        // ExpandLevelAsync's own path, which rejects a multi-level one outright rather than
        // truncating it — see LevelsOnDelegateRejection), and a navigation no candidate has an
        // opinion on is excluded because nothing ever loaded it. Depth >= 2 uses the exact-EDM-type
        // union and the walk descends only through ServeRaw parents — the raw substrate is exactly
        // what a ServeRaw parent leaves behind (#293 Model B).
        //
        // THE UNION FEEDS THE TWO SERIALIZATION STAGES ONLY, NEVER ExpandLevelAsync — and that
        // restriction is load-bearing, not tidiness. An earlier revision of this branch passed the
        // union to all three stages, reasoning that "AnyCandidateHasOpinion keeps it inert for #440,
        // because #440's omission arm fires only when NO candidate has an opinion". That reasoning
        // is correct PER NAME PER LEVEL and false for a FLAT set: membership is decided at the level
        // the name was found, while the omission arm tests the same set against a name at a
        // DIFFERENT level. A navigation called `Children` that is ServeRaw-with-opinion at depth 2
        // (so it enters the set) and UNDECLARED at the root therefore bypassed the omission arm
        // there, and `?$expand=Children,Other($expand=Children($levels=2))` emitted `"Children": []`
        // on the root entity — the exact "expanded, and empty" statement about a relationship the
        // server never evaluated that #440 exists to prevent, under a 200, on a DEFAULT
        // configuration (this union is built whenever the clause carries a $levels anywhere,
        // independently of MaxExpandTop). MEASURED on that revision; base e3a7bd3 omits the member.
        //
        // Stage 3 therefore keeps the PUSHED set, which is the set #440's exclusion was written
        // against: BuildLevelsNavBinding does not consult NavigationPropertyNames, so an undeclared
        // self-referential navigation genuinely can be pushed and loaded, and only that case needs
        // the bypass. The raw set never needs it — a raw name enters only where some candidate has
        // an opinion at its OWN level, and a navigation with an opinion never reaches the
        // no-opinion arm at all. Issue466NavOmissionRegressionTests is the tripwire.
        HashSet<string>? levelsNavNames = pushedLevelsNavNames;
        if (rootClauseForSerialize is not null && ClauseHasLevels((SelectExpandClause)rootClauseForSerialize) &&
            CollectRawServedLevelsNavNames(
                (SelectExpandClause)rootClauseForSerialize, new[] { requestSource }, registration, requestServices,
                null, depth: 1) is { } rawLevelsNavNames)
        {
            if (levelsNavNames is null)
            {
                levelsNavNames = rawLevelsNavNames;
            }
            else
            {
                // Copy rather than mutate: the pushed set belongs to the caller.
                levelsNavNames = new HashSet<string>(levelsNavNames, StringComparer.OrdinalIgnoreCase);
                levelsNavNames.UnionWith(rawLevelsNavNames);
            }
        }

        // Perf fix (measured regression vs. develop, see SerializeBoundedCollection's remarks):
        // ONE batched call for the whole page instead of one SerializeBounded call per entity.
        // Fold-in #5 (#325/#326 review — maxLevels asymmetry) still applies: pass the SAME
        // source.MaxExpansionDepth ceiling Stage 3.5's OmitUnexpandedNavigations call below already
        // uses, instead of silently defaulting to the file-wide MaxNestedExpandDepth (12).
        JsonArray json = SerializeBoundedCollection(originalItems, rootEdmType, registration.EdmModel,
            rootClauseForSerialize,
            serializerOptions, maxLevels: source.MaxExpansionDepth, levelsNavNames: levelsNavNames);

        // Stage 2: Inject @odata.etag using the original (pre-expand) items for ETag computation.
        if (source.HasETag)
        {
            InjectETagsIntoJsonArray(json, originalItems, requestSource);
        }

        // Stage 3: Inject expanded nav properties (if $expand requested), including NESTED
        // $expand/$select clauses (issue #183, OData §11.2.4.2). Delegated to the recursive
        // ExpandLevelAsync so a single, uniform routine handles the root level and every deeper
        // level: $expand=Studio($expand=Movies) loads Movies on each expanded Studio, and nested
        // $select inside an $expand projects the related entities. Root-level $select is still
        // applied by Stage 4 below (it also needs to return the selected-property list for the
        // projected context URL); ExpandLevelAsync applies each deeper level's own $select.
        if (options.SelectExpand?.SelectExpandClause is { } rootClause &&
            rootClause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any())
        {
            // Pair each root CLR entity with its serialised JsonObject (same index/order). The
            // ETag reorder above replaces json[i] in place, so the parallelism still holds.
            var rootItems = new List<object>(originalItems.Length);
            var rootObjects = new List<JsonObject>(originalItems.Length);
            for (int i = 0; i < originalItems.Length; i++)
            {
                if (json[i] is JsonObject o)
                {
                    rootItems.Add(originalItems[i]);
                    rootObjects.Add(o);
                }
            }

            // #440: pushedLevelsNavNames is threaded in so the ServeRaw branch can tell a navigation
            // that was pushed through BuildLevelsNavBinding (loaded, keep it) from one nothing ever
            // loaded (omit it). The candidate set here is the SINGLE requesting profile, so
            // AnyCandidateHasOpinion at this level is exactly "this profile declares or routes the
            // navigation" — the same per-profile question WarnUndeclaredConventionNavigations asks,
            // and deliberately not a sibling-union question (a sibling entity set declaring the same
            // CLR member does not make THIS entity set able to serve it).
            //
            // #466: `pushedLevelsNavNames`, NOT the `levelsNavNames` union built above. The union is
            // flat and its membership is decided per level, so feeding it to a per-level omission
            // test lets a deep name suppress the omission of a same-named root navigation. See the
            // union site for the measurement.
            await ExpandLevelAsync(
                rootItems, rootObjects, rootClause, new[] { requestSource }, rootEdmType,
                registration, requestServices, serializerOptions, depth: 1, ct,
                source.MaxExpansionDepth, pushedLevelsNavNames);
        }

        // Stage 3.5: Omit navigation properties that were not $expand'd (issue #176).
        // System.Text.Json serialises the entire CLR graph, so every declared navigation
        // leaks into the payload — as [] (collection) or null (single) when unloaded, or with
        // data when a sibling $expand pulled it in. OData JSON Format v4.01 §4.5.1 / §11.2.4.2
        // require a non-expanded navigation to be OMITTED entirely, never emitted inline. This
        // pass removes each un-expanded navigation and recurses into the expanded ones so their
        // own un-expanded navigations are stripped too (face 3). Runs after Stage 3 so freshly
        // injected expansions are present, and before Stage 4 so $select still has final say.
        OmitUnexpandedNavigations(json, rootEdmType, options.SelectExpand?.SelectExpandClause, source.ModelType, serializerOptions,
            activeLevels: null, maxLevels: source.MaxExpansionDepth, levelsNavNames: levelsNavNames);

        // Stage 3.6 (#418/#463/#464): hold every RAW-SERVED collection expansion in this response to
        // MaxExpandTop, at every level. See EnforceRawExpandCeiling for the whole argument — what
        // counts as raw-served, why it is a 400 rather than a trim-and-link, and why an engaged
        // (pushed) navigation is skipped here and bounded by ShapePushedExpandsInJson instead.
        //
        // Sited HERE, in the shared pipeline, rather than at a route: all five read routes converge
        // on this method, and #418's original per-route siting on GetById alone is precisely how #464
        // (three unbounded collection paths) went unnoticed. Runs after Stage 3.5 so #440's omissions
        // and the un-expanded strip have already happened — there is no point measuring a collection
        // that is about to be removed — and before Stage 4 so the root $select cannot hide a breach.
        //
        // Inert, and byte-identically so, on the shipping default: MaxExpandTop is null.
        if (source.MaxExpandTop is int expandCeiling &&
            options.SelectExpand?.SelectExpandClause is { } ceilingClause)
        {
            EnforceRawExpandCeiling(
                json.OfType<JsonObject>().ToList(), ceilingClause, new[] { requestSource },
                source.ModelType, engagedExpandNavs, serializerOptions,
                expandCeiling, source.MaxExpansionDepth, source.EntitySetName, singleEntityRead,
                pathPrefix: string.Empty, pathSuffix: string.Empty, depth: 1);
        }

        // Stage 4: Strip unselected properties at the ROOT level (if $select requested). Deeper
        // levels have already had their own $select applied by ExpandLevelAsync in Stage 3.
        List<string>? selectedProps = null;
        if (options.SelectExpand?.SelectExpandClause is not null)
        {
            selectedProps = ExtractSelectedProperties(options.SelectExpand.SelectExpandClause);
            if (selectedProps is not null)
            {
                StripToSelectedProperties(json.OfType<JsonObject>(), selectedProps);
            }
        }

        return (json, selectedProps);
    }

    // Removes every property not in <paramref name="selectedProps"/> from each object, leaving
    // OData annotations (keys starting with '@', e.g. @odata.etag) untouched — they are metadata
    // and must survive $select. Shared by the root-level Stage-4 strip and the per-level nested
    // $select strip in ExpandLevelAsync so casing and annotation handling stay identical.
    //
    // OData §11.2.4.2: an INLINE control-information key is "name@odata.xxx" (e.g. a nested
    // expand's "Chapters@odata.count"). Its base name (the substring before '@') names the
    // property the annotation belongs to, so it must survive $select exactly when that property
    // does — otherwise stripping the enclosing level's non-selected keys would delete a nested
    // expand's count/annotations. We therefore keep a key when: '@' is at index 0 (top-level
    // annotation such as @odata.etag / @odata.id — existing behavior); or '@' appears later and
    // the base name is a selected/expanded property; or the whole key is itself selected.
    private static void StripToSelectedProperties(IEnumerable<JsonObject> objects, List<string> selectedProps)
    {
        foreach (JsonObject obj in objects)
        {
            var toRemove = obj.Select(p => p.Key)
                             .Where(k => !KeepUnderSelect(k, selectedProps))
                             .ToList();
            foreach (string? key in toRemove) obj.Remove(key);
        }
    }

    // Decides whether a single JSON key survives a $select strip. See StripToSelectedProperties
    // for the §11.2.4.2 rationale behind the inline-control-information (name@odata.xxx) case.
    private static bool KeepUnderSelect(string key, List<string> selectedProps)
    {
        int at = key.IndexOf('@');
        if (at == 0) return true;                       // top-level annotation (@odata.etag, @odata.id, ...)
        if (at > 0 &&                                    // inline control info: keep iff its property is selected
            selectedProps.Contains(key.Substring(0, at), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return selectedProps.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    // Deepest nesting level ExpandLevelAsync will follow, AND the model-bound `entityType.Expand(N)`
    // cap written into the EDM at startup (EntitySetProfile.VisitModelBuilder) — the cap Microsoft's
    // SelectExpandQueryValidator validates a NUMERIC $levels=N against. The clause tree the OData
    // parser builds is already finite (bounded by the depth the client actually wrote in $expand),
    // so this is not needed for correctness on well-formed requests — it is a guard against a
    // pathological / adversarial request that nests $expand extremely deep (§11.2.4.2 places no hard
    // cap). Beyond this depth the deeper related entities are simply not loaded.
    //
    // #328/#428: TIED to the MaxExpansionDepth ceiling, deliberately, and it must stay tied.
    // MaxExpansionDepth is what $levels=max resolves to; MaxNestedExpandDepth is what a numeric
    // $levels=N is validated against. While they could diverge (this was 12 while MaxExpansionDepth
    // was unbounded above), a profile at MaxExpansionDepth = 15 rejected $levels=13/14/15 with 400
    // and served $levels=max at depth 15 — the more expensive spelling was the one that got through
    // (#428). Deriving one from the other makes that divergence unrepresentable.
    // ExpandDepthCeilingTieTests is the tripwire.
    internal const int MaxNestedExpandDepth = EntitySetDefaults.MaxExpansionDepthCeiling;

    // #428: the ONE place `$levels` is turned into a number of levels to load. Both substrates call
    // it — the pushdown projection builder (TryBuildEngagedExpand) and the JSON keep/strip pass
    // (BuildExpandLookup) — because they used to be independent transcriptions of the same rule and
    // they disagreed about `$levels=max`.
    //
    // The bug: `$levels=max` was resolved against <paramref name="remainingDepth"/> ALONE (the
    // profile's resolved MaxExpansionDepth), while a NUMERIC `$levels=N` is validated by Microsoft's
    // SelectExpandQueryValidator against min(MaxExpansionDepth, modelBoundMaxDepth) — and for the
    // IsMaxLevel case that validator only requires the minimum to be non-zero, it does not clamp. So
    // a profile at MaxExpansionDepth = 15 with a model-bound cap of 12 rejected `$levels=13/14/15`
    // with 400 and served `$levels=max` at depth 15. The more expensive spelling was the one that got
    // through, and at ~3x per level (#328) that is a cost multiplier, not a cosmetic inconsistency:
    // 3^16 translation units, extrapolated at ~2.2 hours of single-core CPU for one request.
    //
    // <paramref name="modelBoundCap"/> is that model-bound cap. Since #328 it is DERIVED from the
    // MaxExpansionDepth ceiling, so on a shipped build it can no longer be lower than
    // remainingDepth and this clamp cannot fire — which is the point: the divergence is now
    // unrepresentable rather than merely fixed. The parameter stays explicit so the rule is a
    // testable function of its inputs (ExpandLevelsResolutionTests drives it with a cap BELOW
    // remainingDepth, the configuration that used to be reachable) and so re-widening the ceiling
    // cannot silently re-open #428.
    internal static int ResolveLevelsBudget(bool isMaxLevel, long requestedLevel, int remainingDepth, int modelBoundCap)
    {
        int cap = Math.Min(remainingDepth, modelBoundCap);
        long levels = isMaxLevel ? cap : requestedLevel;
        return (int)Math.Min(levels, (long)cap);
    }

    // Issue #183 / OData §11.2.4.2: recursively inject $expand'd navigation properties for one
    // level of a page of entities, then descend into each expanded navigation's own nested
    // $expand/$select clause. <paramref name="items"/> are the CLR entities at this level and
    // <paramref name="jsonItems"/> their already-serialised JsonObjects (parallel, same order);
    // mutations to jsonItems are what end up in the response. <paramref name="levelSource"/> is the
    // request-scoped endpoint source whose NavigationRoutes cover this level's entity type, and
    // <paramref name="levelEdmType"/> is that type in the EDM (used to resolve nested targets).
    //
    // Batching mirrors the top-level strategy per level: when a navigation exposes a BatchHandler
    // it is invoked once for the whole flattened set of entities at this level; otherwise the
    // per-entity Handler is called once per entity (N+1 within that one property). Nested levels
    // flatten every related entity across the page into a single set before recursing, so a
    // batch-capable navigation is still batched once per level rather than once per parent.
    // #294: a nested $top/$skip against a delegate-backed navigation anywhere in the expand tree
    // (see the RunDelegate branch below) throws Microsoft.OData.ODataException rather than
    // returning/threading an IResult — every caller of ApplyCollectionPipelineAsync already catches
    // that exception and converts it to 400 InvalidQueryOption.
    private static async Task ExpandLevelAsync(
        IReadOnlyList<object> items,
        IReadOnlyList<JsonObject> jsonItems,
        SelectExpandClause clause,
        IReadOnlyList<IEntitySetEndpointSource> levelSources,
        IEdmEntityType? levelEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        JsonSerializerOptions serializerOptions,
        int depth,
        CancellationToken ct,
        int maxExpansionDepth,
        HashSet<string>? pushedLevelsNavNames = null)
    {
        if (items.Count == 0 || depth > MaxNestedExpandDepth || levelSources.Count == 0) return;

        // Cache the key PropertyInfo once per level (M-3 perf parity with the old inline loop).
        // #292: levelSources is a union of 2+ profiles only when the EDM couldn't disambiguate
        // which entity set a navigation targets (see ResolveRequestSourcesForEdmType); those
        // profiles all share the same CLR model type by construction, so the key property — a
        // structural convention on that type, not part of any delegate/authorization boundary —
        // is read off the first candidate.
        PropertyInfo? keyProp = items[0].GetType()
            .GetProperty(levelSources[0].KeyPropertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        foreach (ExpandedNavigationSelectItem expandItem in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string propName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;

            // Derive the expand key the way the serializer named the parent's property: honor a
            // per-property [JsonPropertyName] rename first (#184), then fall back to the naming
            // policy ("children" for camelCase, "Children" for PascalCase). Resolved off the actual
            // runtime entity type at this level. Must agree with OmitUnexpandedNavigations' key so
            // Stage 3.5 keeps (not strips) the expansion this injects.
            // #253 completion: propName is the EDM (JSON) nav name, so map JSON→CLR (a plain
            // GetProperty(jsonName) would miss the renamed CLR member and mis-derive the key under a
            // non-camelCase policy) before resolving the payload key off the CLR property.
            PropertyInfo? expandClrProp = ODataPropertyNaming.FindClrPropertyByEdmName(items[0].GetType(), propName);
            string expandKey = ResolveNavigationJsonKey(expandClrProp?.Name ?? propName, expandClrProp, serializerOptions);

            // Model B — declaring-set authority (OWNER DECISION 2026-07-26, FROZEN spec on issue
            // #293): resolve this navigation's treatment from levelSources, the exact-EDM-type
            // candidate set for this level (see ResolveRequestSourcesForEdmType). ResolveNavTreatment
            // is the SAME dispatch the pushdown gate uses (TryBuildEngagedExpand, over the equivalent
            // startup-profile candidate set for the child element type) — reusing it here means the
            // gate and this delegate path can never disagree: the gate only ever pushes down a
            // ServeRaw navigation (so RunDelegate/Blank always arrive here needing action), and this
            // path only ever runs the sole RunDelegate route or blanks a Blank one — plus, since
            // #440, omits a ServeRaw navigation that NO candidate at this level declares or routes
            // (see the ServeRaw branch: that sub-case is not a delegate decision at all, it is
            // "nothing ever loaded this, so there is no raw value to serve").
            NavTreatmentResult treatment = ResolveNavTreatment(propName, levelSources);
            bool isCollectionNav = (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                .NavigationProperty?.Type.IsCollection() ?? false;

            if (treatment.Treatment == NavTreatment.ServeRaw)
            {
                // #440 symptom 1: ServeRaw has TWO populations and only one of them has an answer.
                //
                // The frozen reading — "DB(X) = ∅, so whatever is already sitting at
                // jsonItems[i][expandKey] IS the raw, authoritative answer" — holds when some
                // candidate DECLARED the navigation: the declaration is what puts it in
                // pushdownExpandNavs, so an EF-backed read really did JOIN it in and the value is
                // loaded data. It does NOT hold for a navigation the OData convention builder
                // discovered and NO candidate at this level declares or routes (#293's "has no
                // opinion" category, reported by ResolveNavTreatment as AnyCandidateHasOpinion =
                // false). Nothing ever chose to load that one — it is absent from
                // pushdownExpandNavs, has no delegate, and is not in the engaged tree — so what
                // survives serialization is an unpopulated CLR member, and emitting it means
                // emitting `"Customer": null` (or `[]`) under a 200.
                //
                // That is the one answer that is definitely wrong. OData JSON Format v4.01 §8.3
                // defines the inline representation of a navigation property as the representation
                // of an EXPANDED one, and a single-valued expanded navigation whose value is null
                // is the positive statement that the relationship is empty. The server never
                // determined that. §8.1 covers the other case: a navigation that was not expanded
                // is represented by its (computed, and in metadata=minimal omitted) navigation
                // link, not inline — which is exactly what OmitUnexpandedNavigations already does
                // for every navigation this request did not expand. So the member is REMOVED: the
                // payload then asserts only "not expanded", which is true, instead of "expanded,
                // and empty", which is not.
                //
                // NOT a 400, deliberately. The framework's fail-loud convention (#294, #402, #405)
                // rejects an option the CLIENT got wrong or that the server parsed and could not
                // honour. This is neither: the request is valid against the $metadata the server
                // published, and the gap is the SERVER's configuration — a navigation the profile
                // never declared. A 400 would charge the client for the developer's omission, on
                // the ordinary `public Customer? Customer { get; set; }` shape, turning a currently
                // succeeding request into an error across every adopter who has one. The loud
                // channel for a configuration defect is startup, and that is where it is:
                // WarnUndeclaredConventionNavigations names this exact condition once per
                // (entity set, navigation) at MapOhData().
                //
                // THE ONE EXCLUSION: a $levels expand. The root pushdown loop resolves a $levels
                // navigation through BuildLevelsNavBinding, which does NOT consult
                // NavigationPropertyNames — so `?$expand=Self($levels=2)` over an undeclared
                // self-referential navigation IS pushed and IS loaded. pushedLevelsNavNames is the
                // set that was actually pushed that way, so those keep their loaded value. Nested
                // levels reached from here are never pushed (a ServeRaw parent returns below
                // without recursing, so this method only ever descends through a delegate), which
                // is why the recursive call passes none.
                if (!treatment.AnyCandidateHasOpinion &&
                    !(pushedLevelsNavNames?.Contains(propName) ?? false))
                {
                    for (int i = 0; i < items.Count; i++) jsonItems[i].Remove(expandKey);
                    continue;
                }

                // DB(X) = ∅ over every candidate at this level and at least one DECLARED it: nobody
                // delegates this navigation, so whatever is already sitting at
                // jsonItems[i][expandKey] — an EF Include pushed down by the query, or the plain
                // serialized CLR graph — IS the raw, authoritative answer. Nothing to inject or
                // blank; leave it exactly as serialized.
                //
                // #320: but this branch does NOT recurse, so a DEEPER navigation reached only through
                // this ServeRaw parent's already-materialized graph never reaches the nested-$top/$skip
                // rejection below — its option was accepted, never applied, and answered 200. Scan the
                // subtree for that case before leaving. The scan is skipped entirely (no candidate
                // resolution, no profile instantiation) unless the subtree actually carries a nested
                // $top/$skip, so the common ServeRaw expand stays a bare `continue`.
                if (expandItem.SelectAndExpand is { } rawNested && ClauseHasNestedTopOrSkip(rawNested))
                {
                    IEdmEntityType? rawTargetEdmType =
                        (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                        .NavigationProperty?.ToEntityType();
                    EnsureNestedWindowIsApplicable(
                        rawNested, rawTargetEdmType, registration, requestServices, depth + 1);
                }
                continue;
            }

            // #294 + #320 (uniform rule): a nested $top/$skip inside $expand cannot be applied to a
            // navigation whose treatment is not ServeRaw — a RunDelegate nav's Handler/BatchHandler
            // returns the delegate's FULL answer for the given parent key(s) and nothing downstream
            // windows it, and a Blank nav is emptied outright. Silently ignoring the option returned
            // every related row (or none) under an unsuspicious 200 — the #294 bug. Reject instead of
            // guessing, consistent with the framework's "parse the option or reject it" contract, and
            // mirroring ValidateNestedTopCeiling's over-ceiling 400 above it in the request pipeline.
            //
            // Checked BEFORE the Blank branch and before either delegate branch below, so no handler
            // runs for a rejected request and the answer does not depend on which non-ServeRaw
            // treatment the navigation resolved to. Does not apply to ServeRaw: EF pushdown honors and
            // windows a nested $top there (and where it does NOT — a ServeRaw nav whose branch was
            // never pushed down at all — the option is still silently ignored; see the class note on
            // ClauseHasNestedTopOrSkip).
            if (expandItem.TopOption is not null || expandItem.SkipOption is not null)
            {
                // Thrown (not returned) for the same reason EnsureWithinExpandCeiling throws below:
                // it avoids IResult threading through this void recursive walk. All 5 collection-GET
                // call sites of ApplyCollectionPipelineAsync already catch Microsoft.OData.ODataException
                // and surface it as 400 InvalidQueryOption.
                throw NestedWindowRejection(propName, treatment.Treatment);
            }

            if (treatment.Treatment == NavTreatment.Blank)
            {
                // DB(X) and DL(X) disagree at this level (some candidate delegates, another declares
                // the nav with no route — or 2+ candidates delegate via distinct routes): the framework
                // cannot tell which authoritative declaration governs, so it fails closed rather than
                // guessing. Overwrite explicitly — this key IS in the $expand clause, so Stage 3.5's
                // OmitUnexpandedNavigations (which only ever REMOVES un-$expand'd keys) would otherwise
                // keep whatever the parent handler/EF fixup happened to leave here.
                for (int i = 0; i < items.Count; i++)
                {
                    jsonItems[i][expandKey] = isCollectionNav ? new JsonArray() : null;
                }
                continue;
            }

            // #466: a MULTI-LEVEL $levels on a delegate-backed navigation is REJECTED, not truncated.
            //
            // The delegate loads ONE level: this branch calls Handler/BatchHandler once for this
            // level's parents, and the $levels item's nested clause carries no expand item of its
            // own, so `hasNestedExpand` is false and nothing recurses. The deeper self-references are
            // then stripped by OmitUnexpandedNavigations (a delegate-backed nav is never in
            // levelsNavNames, deliberately — see ApplyCollectionPipelineAsync's union). So
            // `Nav($levels=3)` answered 200 with ONE level while `Nav($expand=Nav($expand=Nav))` —
            // the same request, spelled out — answered 200 with three. Silent truncation of the
            // requested shape, and the M1 rule ("no bound without either a continuation or a 400")
            // rules that out.
            //
            // WHY 400 AND NOT AN IMPLEMENTATION. Loading level 2 means running a delegate at depth 2,
            // and WHICH delegate is not settled for this substrate: Model B resolves depth >= 2 from
            // the exact-EDM-type union (#293, FROZEN), which for a self-referential navigation over a
            // type exposed by a disagreeing sibling set is Blank — while the PUSHDOWN path's $levels
            // deliberately never re-resolves and stays on the URL-named set all the way down (#318,
            // owner-settled). Implementing here would have to pick one of those, i.e. make an owner
            // decision about gate resolution on a substrate that has never had one. The 400 needs no
            // such decision, and it follows this file's own precedent exactly: #294 rejects a nested
            // $top/$skip on a delegate-backed navigation for the very same reason (the option cannot
            // be applied to a delegate's answer), a few lines above.
            //
            // $levels=1 is NOT rejected: it is a spec-equivalent restatement of a bare $expand, which
            // this path serves correctly. The budget is resolved through the SAME ResolveLevelsBudget
            // both loaders use, so this guard cannot disagree with them about what a $levels means.
            if (expandItem.LevelsOption is { } delegateLevels &&
                ResolveLevelsBudget(
                    delegateLevels.IsMaxLevel, delegateLevels.Level, maxExpansionDepth, MaxNestedExpandDepth) > 1)
            {
                throw LevelsOnDelegateRejection(propName);
            }

            // NavTreatment.RunDelegate: exactly one candidate at this level routes this navigation
            // back, and no candidate disagrees (declares it delegate-less) — that route is the sole,
            // unambiguous authority for it. Run it.
            NavigationRouteDefinition navRoute = treatment.Route!;

            // Load the related entity/collection for every entity at this level, keeping the CLR
            // results (relatedByIndex[i]) so deeper levels can read their keys.
            object?[] relatedByIndex = new object?[items.Count];
            if (navRoute.BatchHandler is not null)
            {
                var keys = new List<object>(items.Count);
                object?[] keyByIndex = new object?[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    object? keyVal = keyProp?.GetValue(items[i]);
                    keyByIndex[i] = keyVal;
                    if (keyVal is not null) keys.Add(keyVal);
                }

                IReadOnlyDictionary<object, object?> map = await navRoute.BatchHandler(keys, ct);
                for (int i = 0; i < items.Count; i++)
                {
                    // A missing key means "no children" (collection → []) or "no related entity"
                    // (single → null), matching the per-entity fallback's empty/null defaults.
                    relatedByIndex[i] = keyByIndex[i] is { } k && map.TryGetValue(k, out object? v)
                        ? v
                        : (navRoute.IsCollection ? Array.Empty<object>() : null);
                }
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    relatedByIndex[i] = keyProp?.GetValue(items[i]) is { } keyVal
                        ? await navRoute.Handler(keyVal, ct)
                        : (navRoute.IsCollection ? Array.Empty<object>() : null);
                }
            }

            // Resolve the navigation target's EDM entity type and nested $expand/$select clause up
            // front — needed both by the #325/#326 bounded splice immediately below AND by the
            // hasNestedExpand recursion further down (single computation, reused).
            IEdmNavigationProperty? navProp =
                (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?.NavigationProperty;
            IEdmEntityType? targetEdmType = navProp?.ToEntityType();
            SelectExpandClause? nestedClause = expandItem.SelectAndExpand;

            // Inject the serialised related value onto each parent JsonObject.
            // #325/#326 (Option B): the delegate's own returned graph gets the SAME bounded
            // treatment as Stage 1 — a delegate can return tracked/cyclic entities (T30/T31), and
            // this splice is a second, independent serialization event Stage 1's walker never
            // reaches (relatedByIndex[i] is the delegate's freshly returned object, not something
            // read off the root item via reflection). Bounded by nestedClause exactly as Stage 1 is
            // bounded by the root clause: any navigation nestedClause itself keeps gets a
            // reflection-read splice here, and — for one that resolves to RunDelegate/Blank at the
            // NEXT level — the hasNestedExpand recursion below still unconditionally overwrites it
            // (same ordering guarantee as Stage 1: walker first, delegate-safety overwrite after).
            for (int i = 0; i < items.Count; i++)
            {
                // Fold-in #2: cardinality comes from the EDM (isCollectionNav, already resolved
                // above), never sniffed from relatedByIndex[i]'s own CLR shape.
                jsonItems[i][expandKey] = SerializeBounded(
                    relatedByIndex[i], targetEdmType, registration.EdmModel, nestedClause, serializerOptions,
                    isCollectionValue: isCollectionNav);
            }

            if (nestedClause is null) continue;

            bool hasNestedExpand = nestedClause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any();
            bool hasNestedSelect = !nestedClause.AllSelected;
            if (!hasNestedExpand && !hasNestedSelect) continue;

            // Flatten every related entity across the whole page into one (CLR, JsonObject) set so
            // a deeper batch navigation is invoked once per level, and nested $select is applied to
            // all of them in one pass.
            var childItems = new List<object>();
            var childObjects = new List<JsonObject>();
            for (int i = 0; i < items.Count; i++)
            {
                object? related = relatedByIndex[i];
                JsonNode? node = jsonItems[i][expandKey];
                if (navRoute.IsCollection)
                {
                    if (related is System.Collections.IEnumerable seq && node is JsonArray arr)
                    {
                        int j = 0;
                        foreach (object? elem in seq)
                        {
                            if (elem is not null && j < arr.Count && arr[j] is JsonObject childObj)
                            {
                                childItems.Add(elem);
                                childObjects.Add(childObj);
                            }
                            j++;
                        }
                    }
                }
                else if (related is not null && node is JsonObject childObj)
                {
                    childItems.Add(related);
                    childObjects.Add(childObj);
                }
            }

            if (childItems.Count == 0) continue;

            if (hasNestedExpand)
            {
                // The request-scoped source(s) that legitimately serve the NEXT level's own
                // NavigationRoutes (nav handlers may capture scoped dependencies such as a
                // DbContext) — navProp/targetEdmType were already resolved above for the bounded
                // splice. #292: unions every profile exposing the same CLR/EDM type so the per-nav
                // lookup above (routeMatches) can fail closed on conflicts instead of a single
                // arbitrary FirstOrDefault picking whichever profile happens to be first in
                // registration/iteration order.
                IReadOnlyList<IEntitySetEndpointSource> targetSources = ResolveRequestSourcesForEdmType(
                    targetEdmType, registration, requestServices);

                if (targetSources.Count > 0)
                {
                    await ExpandLevelAsync(
                        childItems, childObjects, (SelectExpandClause)nestedClause, targetSources, targetEdmType,
                        registration, requestServices, serializerOptions, depth + 1, ct,
                        maxExpansionDepth);
                }
                // If no candidate set is registered/resolvable at all, the deeper expansion
                // cannot be loaded here; Stage 3.5's OmitUnexpandedNavigations still keeps the
                // (empty) nav per the clause, mirroring the pre-#183 limitation for unregistered
                // navigation targets. This is safe precisely because zero candidates means no
                // profile anywhere exposes the type, so no delegate-safety union applies to it.
            }

            // Apply this navigation's nested $select to the just-injected children (reuses the
            // root-level strip so casing / annotation handling are identical). Runs after the
            // deeper recursion so nested $expand keeps final say over what data is present, and
            // ExtractSelectedProperties preserves expanded nav names so they survive projection.
            if (hasNestedSelect)
            {
                List<string>? nestedSelected = ExtractSelectedProperties((SelectExpandClause)nestedClause);
                if (nestedSelected is not null) StripToSelectedProperties(childObjects, nestedSelected);
            }
        }
    }

    // #294/#320: the single place the nested-$top/$skip rejection message is built, so the two throw
    // sites (the navigation reached directly by ExpandLevelAsync, and one reached only through a
    // ServeRaw parent's materialized graph) can never drift apart. The RunDelegate wording is
    // byte-identical to the message #294 shipped — it is quoted in docs and asserted in tests.
    private static Microsoft.OData.ODataException NestedWindowRejection(string navName, NavTreatment treatment) =>
        treatment == NavTreatment.RunDelegate
            ? new Microsoft.OData.ODataException(
                $"A nested $top/$skip is not supported on the delegate-backed navigation '{navName}'; " +
                "declare it delegate-less (no Handler/BatchHandler) to enable server-side windowing, " +
                "or remove the option.")
            : new Microsoft.OData.ODataException(
                $"A nested $top/$skip is not supported on the navigation '{navName}': the entity sets " +
                "exposing this type disagree about whether it is delegate-backed, so it is served " +
                "empty and no window can be applied. Remove the option.");

    // #466: the message for a multi-level $levels on a delegate-backed navigation. Deliberately
    // shaped like NestedWindowRejection's RunDelegate arm — same substrate, same reason (the option
    // cannot be applied to a delegate's answer), same two remedies — and it names the spelling that
    // DOES have an answer, because that is the whole point of the issue: the explicit nested chain
    // recurses through this method, so every deeper level is RESOLVED through Model B — run through
    // the level's own delegate where the candidate set agrees, Blanked where it does not — instead of
    // being dropped without a word.
    private static Microsoft.OData.ODataException LevelsOnDelegateRejection(string navName) =>
        new(
            $"A '$levels' expansion of more than one level is not supported on the delegate-backed " +
            $"navigation '{navName}'; the delegate loads a single level, so the deeper levels would " +
            "be silently dropped. Spell the depth out with nested $expand, or declare the navigation " +
            "delegate-less (no Handler/BatchHandler) to enable the server-side $levels recursion.");

    // #320: true when <paramref name="clause"/> carries a $top or $skip on ANY navigation at any depth
    // below it. A pure clause walk — no EDM lookup, no candidate resolution, no profile instantiation —
    // so ExpandLevelAsync's ServeRaw branch pays nothing for the overwhelmingly common expand that
    // carries no nested window at all. Unbounded recursion, like its siblings ValidateNestedTopCeiling
    // and CountExpandNodes: the depth and breadth ceilings (#328/#429) have already rejected an
    // oversized tree before any of the three runs.
    //
    // SCOPE NOTE (deliberate, measured, NOT fixed here). "Not applicable" is resolved from the Model B
    // treatment (RunDelegate/Blank), not from whether the option was in fact applied. A ServeRaw
    // navigation whose branch was never SQL-pushdown-windowed — an in-memory GetAll source, a
    // non-EF IQueryable, or a branch TryBuildEngagedExpand deferred for a structural reason — still
    // ignores its nested $top/$skip silently. Rejecting THAT would make the answer depend on whether
    // pushdown happened to engage, which is an internal optimization decision invisible to the client,
    // and would turn requests that are honored today into 400s. It needs its own owner decision
    // (reject vs. apply in memory) alongside #352's retirement of this rejection.
    //
    // #464 AMENDMENT TO THAT NOTE. It described only the nested-$top/$skip half of the off-pushdown
    // gap. The MaxExpandTop CEILING had the same reach hole and it is no longer open: a ServeRaw
    // collection expansion that pushdown did not engage — a GetAll source, a Priority-1 source, a
    // non-EF IQueryable ($search's in-memory swap included), a branch TryBuildEngagedExpand deferred,
    // or any level of a single-entity read — is now bounded by EnforceRawExpandCeiling, which 400s
    // rather than serving an unbounded collection. What is still true of the paragraph above is
    // narrower than it reads: the nested $top/$skip WINDOW is still silently ignored on those paths
    // (the client's option is not applied; the response is simply the whole, now ceiling-bounded,
    // collection). That residue is what #352 owns.
    private static bool ClauseHasNestedTopOrSkip(SelectExpandClause clause)
    {
        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            if (item.TopOption is not null || item.SkipOption is not null) return true;
            if (item.SelectAndExpand is { } deeper && ClauseHasNestedTopOrSkip(deeper)) return true;
        }
        return false;
    }

    // #466: does this $expand tree carry a $levels anywhere? A pure clause walk, and the gate that
    // keeps CollectRawServedLevelsNavNames (which DOES resolve candidate sets per level) off the
    // overwhelmingly common expand that carries no $levels at all. Same shape and same bounding
    // argument as ClauseHasNestedTopOrSkip above.
    private static bool ClauseHasLevels(SelectExpandClause clause)
    {
        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            if (item.LevelsOption is not null) return true;
            if (item.SelectAndExpand is { } deeper && ClauseHasLevels(deeper)) return true;
        }
        return false;
    }

    // #466: the navigations carrying $levels whose recursion the RAW substrate serves — i.e. the ones
    // BuildExpandLookup must seed a levels budget for so SerializeBounded/OmitUnexpandedNavigations
    // keep the self-reference down to the depth requested, exactly as they already do for a PUSHED
    // $levels (CollectPushedLevelsNavNames) and for the explicit nested spelling.
    //
    // Candidates are resolved through the SAME ResolveRequestSourcesForEdmType and the treatment
    // through the SAME ResolveNavTreatment the real descent uses, so this cannot disagree with it.
    // The walk descends only through a ServeRaw navigation, because that is exactly the boundary of
    // the raw substrate: ExpandLevelAsync's ServeRaw branch leaves the materialized graph in place
    // and does not recurse, so everything below it is read straight off that graph, while a
    // RunDelegate/Blank navigation replaces the value and owns its own subtree.
    //
    // AnyCandidateHasOpinion is required — see the union at the call site for why (it is what keeps
    // #440's omission arm untouched).
    private static HashSet<string>? CollectRawServedLevelsNavNames(
        SelectExpandClause clause,
        IReadOnlyList<IEntitySetEndpointSource> levelSources,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        HashSet<string>? names,
        int depth)
    {
        if (depth > MaxNestedExpandDepth) return names;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string navName = item.PathToNavigationProperty.FirstSegment.Identifier;
            NavTreatmentResult treatment = ResolveNavTreatment(navName, levelSources);
            if (treatment.Treatment != NavTreatment.ServeRaw || !treatment.AnyCandidateHasOpinion) continue;

            if (item.LevelsOption is not null)
            {
                (names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(navName);
            }

            if (item.SelectAndExpand is { } nested &&
                nested.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any())
            {
                IEdmEntityType? childEdmType =
                    (item.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                    .NavigationProperty?.ToEntityType();
                names = CollectRawServedLevelsNavNames(
                    nested,
                    ResolveRequestSourcesForEdmType(childEdmType, registration, requestServices),
                    registration, requestServices, names, depth + 1);
            }
        }

        return names;
    }

    // #320: walks the $expand subtree hanging off a ServeRaw navigation and throws the same 400 the
    // direct path throws for a nested $top/$skip on any navigation whose treatment is not ServeRaw.
    //
    // WHY THIS IS NEEDED AT ALL. ExpandLevelAsync's ServeRaw branch `continue`s without recursing —
    // correctly, since the raw materialized value IS the answer — so a delegate-backed grandchild
    // reached ONLY through a delegate-less parent's graph was never resolved, and its nested
    // $top/$skip was accepted, never applied, and answered 200 with every related row.
    //
    // WHY IT CANNOT TURN A HONORED REQUEST INTO A 400. A nested $top/$skip is honored only when the
    // whole branch was pushed down, and TryBuildEngagedExpand pushes a branch only when EVERY level of
    // it is ServeRaw (a RunDelegate or Blank child defers the whole parent). So whenever this scan
    // finds a non-ServeRaw navigation, that branch was certainly not pushed and the option was
    // certainly not applied.
    //
    // Candidates are resolved through the SAME ResolveRequestSourcesForEdmType the real descent uses,
    // and the treatment through the SAME ResolveNavTreatment, so the scan cannot disagree with the
    // descent it stands in for. Mirrors ExpandLevelAsync's own guards: bounded by MaxNestedExpandDepth,
    // and silent when no profile exposes the level's type (nothing there could have applied the option
    // either, and nothing else in the pipeline treats that as an error).
    //
    // #440 interaction: an UNDECLARED convention-discovered navigation has no opinion from any
    // candidate, so ResolveNavTreatment reports it ServeRaw and this scan does not reject it. That is
    // the intended pairing — #440 REMOVES such a navigation from the payload rather than emitting an
    // unpopulated value, so its subtree is not served at all and there is nothing for a window to
    // apply to. Rejecting there would also charge the client a 400 for the developer's missing
    // declaration, which #440 deliberately declined to do; the loud channel for that is the startup
    // warning. The call site likewise runs this scan only AFTER #440's omission branch.
    private static void EnsureNestedWindowIsApplicable(
        SelectExpandClause clause,
        IEdmEntityType? levelEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        int depth)
    {
        if (levelEdmType is null || depth > MaxNestedExpandDepth) return;

        IReadOnlyList<IEntitySetEndpointSource> candidates =
            ResolveRequestSourcesForEdmType(levelEdmType, registration, requestServices);
        if (candidates.Count == 0) return;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string navName = item.PathToNavigationProperty.FirstSegment.Identifier;
            NavTreatment navTreatment = ResolveNavTreatment(navName, candidates).Treatment;

            if (navTreatment != NavTreatment.ServeRaw &&
                (item.TopOption is not null || item.SkipOption is not null))
            {
                throw NestedWindowRejection(navName, navTreatment);
            }

            if (item.SelectAndExpand is { } deeper)
            {
                IEdmEntityType? deeperEdmType =
                    (item.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                    .NavigationProperty?.ToEntityType();
                EnsureNestedWindowIsApplicable(
                    deeper, deeperEdmType, registration, requestServices, depth + 1);
            }
        }
    }

    // #292: finds the request-scoped endpoint source(s) that legitimately serve a navigation
    // target EDM entity type, replacing a plain FirstOrDefault-by-CLR-type that was registration-
    // order dependent whenever the SAME type was exposed by 2+ entity sets (structurally the same
    // shape of bug the #293 delegate-backed-nav union fixes on the pushdown path, but here on the
    // Stage-3 delegate expansion path).
    //
    // Always the union: every profile whose entity set's EDM type is <paramref name="targetEdmType"/>.
    // This CAN legitimately return 2+ candidates — the caller (ExpandLevelAsync) resolves
    // per-navigation-name ambiguity from the full candidate list rather than this method picking
    // one, so a genuine conflict (two candidates routing the same nav name differently) fails
    // closed instead of being silently decided here.
    //
    // No branch preferring the EDM's own navigation-source binding (originally proposed in #292
    // item 1, i.e. <c>expandItem.NavigationSource</c> at the call site). The DECISION stands; the
    // reason originally written here did not, and was corrected by #313 (design finding B5).
    //
    // WHAT THIS COMMENT USED TO CLAIM, AND WHY IT WAS WRONG. It said the convention builder "never
    // produces a real <see cref="IEdmEntitySet"/> binding for a navigation, only either no binding
    // or an <see cref="IEdmUnknownEntitySet"/> placeholder". MEASURED FALSE on this tree, both
    // arms, by walking <c>IEdmEntitySet.NavigationPropertyBindings</c> and
    // <c>FindNavigationTarget</c> over a registration's own <c>OhDataRegistration.EdmModel</c>:
    //
    //   (A) EXACTLY ONE entity set exposes the navigation's target type → a REAL binding exists.
    //       The parent set's NavigationPropertyBindings contains `<navName> -> <thatSet>`, and
    //       FindNavigationTarget(nav) returns that same EdmEntitySet.
    //   (B) TWO OR MORE entity sets expose it → NavigationPropertyBindings is EMPTY, and
    //       FindNavigationTarget(nav) returns an EdmUnknownEntitySet whose Name is the navigation
    //       PROPERTY's own name, never an entity set's.
    //   (C) NO entity set exposes it → the same EdmUnknownEntitySet placeholder as (B).
    //
    // So the placeholder half of the old claim was right and the "never a real binding" half was
    // not. The binding is produced by the convention builder itself, not by any profile-facing API —
    // that part is unchanged: there is still no reachable way for a profile to DECLARE a
    // cross-entity-set binding.
    //
    // WHY THE DECISION SURVIVES THE CORRECTION. The binding branch is redundant rather than
    // unreachable, and it is redundant in exactly the cases above: in (A) a real binding exists but
    // there is by construction exactly ONE candidate, so the union below resolves identically; in
    // (B) and (C) — the only cases where the union could be ambiguous — there is no binding to
    // prefer. Intentional, reviewed deviation from #292's written step 1, now for a reason that is
    // true.
    //
    // DO NOT "RESTORE" THE BINDING AS LOAD-BEARING. #313 O5 restricts nested-$expand continuation
    // links to depth 1, where the URL already names the parent set and there is no child set to
    // disambiguate — which is precisely what took this finding OFF the critical path. A real
    // binding is only the correct source of a child SET NAME at depth >= 2, and the hazard there is
    // that adding a second, unrelated entity set over the child type moves the model from
    // (A) to (B) and DELETES the binding — silently changing behaviour with no change to the
    // navigation itself. Anything built on it must test
    // `set.FindNavigationTarget(nav) is IEdmEntitySet and not IEdmUnknownEntitySet` and must treat
    // the absent case as a first-class outcome, not an error.
    //
    // Returns an empty list when targetEdmType is null or no profile exposes it at all — e.g. a
    // navigation whose target type is present in the model but never registered as its own entity
    // set — in which case nested expansion of that navigation is not possible from any source.
    private static IReadOnlyList<IEntitySetEndpointSource> ResolveRequestSourcesForEdmType(
        IEdmEntityType? targetEdmType, OhDataRegistration registration, IServiceProvider requestServices)
    {
        List<IEntitySetEndpointSource> candidates = new();
        foreach (IEntitySetEndpointSource profile in ResolveProfilesForEdmType(targetEdmType, registration))
        {
            if (requestServices.GetService(profile.GetType()) is IEntitySetEndpointSource instance)
                candidates.Add(instance);
        }
        return candidates;
    }

    // Model B candidate resolution (FROZEN spec, issue #293): the startup profiles whose entity
    // set's EDM entity type is EXACTLY <paramref name="targetEdmType"/> (matched by
    // <see cref="IEdmEntityType.FullTypeName"/> — never CLR-type assignability, and never registration
    // order). This is "the candidate set S" the decision table in ResolveNavTreatment partitions into
    // DB/DL. Shared by both call sites that need a level's candidate set:
    //   - the pushdown gate (TryBuildEngagedExpand → ResolveProfilesForClrType below), which only has
    //     structural facts available at query-plan time, so the startup singletons here suffice;
    //   - the delegate expansion path (ExpandLevelAsync → ResolveRequestSourcesForEdmType above), which
    //     re-resolves each of these same candidates through the request scope so their handlers may
    //     capture scoped dependencies (e.g. a DbContext).
    // Because both paths start from this exact same set, the gate and the delegate path can never
    // compute a different candidate set for the same navigation — only ResolveNavTreatment's decision
    // over that set matters, and it is likewise shared.
    private static IReadOnlyList<IEntitySetEndpointSource> ResolveProfilesForEdmType(
        IEdmEntityType? targetEdmType, OhDataRegistration registration)
    {
        if (targetEdmType is null) return Array.Empty<IEntitySetEndpointSource>();

        string targetName = targetEdmType.FullTypeName();
        List<IEntitySetEndpointSource> candidates = new();
        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            IEdmEntityType? setType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (setType is not null && setType.FullTypeName() == targetName) candidates.Add(profile);
        }
        return candidates;
    }

    // Gate-side convenience over ResolveProfilesForEdmType: resolves a CLR element type (as seen at
    // query-plan time, e.g. binding.ElementType) to its declared EDM entity type via the same
    // EdmClrTypeMap lookup IsMemberInitProjectable already relies on, then defers to
    // ResolveProfilesForEdmType so the gate's candidate set is computed by the exact same EDM-type
    // match the delegate path uses — never CLR-type equality/assignability on its own, which is what
    // made #293's original fix over-broad (matching a base/derived CLR type rather than the exact EDM
    // entity type the level is actually reached through).
    //
    // #508: the lookup used to be model.FindDeclaredType(clrType.FullName), which matches on the EDM
    // type's FULL NAME and so returns null for EVERY type on a renamed schema. The candidate set was
    // then empty, ResolveNavTreatment saw no candidates, and the gate deferred — silently, model-wide.
    // See EdmClrTypeMap for why the annotation route has no such failure mode, and why the lookup
    // stays EXACT here.
    private static IReadOnlyList<IEntitySetEndpointSource> ResolveProfilesForClrType(
        Type clrType, IEdmModel model, OhDataRegistration registration)
    {
        IEdmEntityType? edmType = EdmClrTypeMap.FindEntityType(model, clrType);
        return ResolveProfilesForEdmType(edmType, registration);
    }

    // Model B navigation treatment (declaring-set authority — OWNER DECISION 2026-07-26, FROZEN spec
    // on issue #293). Each candidate set's OWN declaration is authoritative for its OWN navigations; a
    // delegate on a sibling/derived set never retroactively poisons a nav that ANOTHER set legitimately
    // serves raw. Fail-closed BLANKING happens only on genuine disagreement between candidates.
    //
    // Partitions <paramref name="candidates"/> — the exact-EDM-type candidate set for one level, from
    // ResolveProfilesForEdmType / ResolveProfilesForClrType / ResolveRequestSourcesForEdmType — into:
    //   DB(navName) = candidates that route this nav back (NavigationRoutes has a matching entry)
    //   DL(navName) = candidates that DECLARE this nav (NavigationPropertyNames) but have NO route
    // A candidate that neither routes nor declares the nav has no opinion on it and is ignored.
    //
    //   DB empty                          -> ServeRaw     (nobody delegates; raw survives as-is)
    //   DB has exactly one route, DL empty -> RunDelegate  (sole, unambiguous authority — run it)
    //   DB non-empty AND DL non-empty      -> Blank        (delegate-backed vs delegate-less disagree)
    //   DB has 2+ candidates                -> Blank        (2+ distinct delegate routes disagree)
    // Deterministic: only set membership over `candidates` is read, never registration/iteration order.
    //
    // Used by BOTH the pushdown gate (candidates = startup profiles for the child element type) and the
    // delegate expansion path (candidates = levelSources, the same profiles re-resolved through the
    // request scope) — see the two call sites — so they can never diverge: the gate only ever pushes
    // down a ServeRaw navigation (RunDelegate/Blank always defer to the delegate path), and the delegate
    // path's ServeRaw case is a no-op (the pushed/serialized raw value already present is correct)
    // WHENEVER some candidate declared the navigation. #440 split off the other half of ServeRaw —
    // nobody declares OR routes it, so nothing ever loaded it — which the delegate path OMITS rather
    // than emitting as null; see AnyCandidateHasOpinion below and the ServeRaw branch that reads it.
    private enum NavTreatment { ServeRaw, RunDelegate, Blank }

    // #440: AnyCandidateHasOpinion is "DB(navName) ∪ DL(navName) is non-empty" — i.e. at least one
    // candidate at this level either routes or declares the navigation. It is the COMPLEMENT of the
    // decision table's own "a candidate that neither routes nor declares the nav has no opinion on it
    // and is ignored" clause: when every candidate is in that category, DB and DL are both empty and
    // this is false. Reported here rather than recomputed by the caller so the two can never disagree
    // about what "has an opinion" means.
    //
    // It changes NO Treatment. The four rows above are byte-identical, the pushdown gate reads only
    // .Treatment, and Issue322ModelBClassificationTests pins the whole table through the Treatment
    // property alone. What it lets ExpandLevelAsync do is distinguish ServeRaw's two populations,
    // which are NOT the same claim: "a candidate declared this nav delegate-less, so the raw value is
    // authoritative" versus "nobody at this level has any opinion, so nothing ever chose to load it
    // and there is no authoritative value to serve". Emitting the second as null asserts that no
    // related entity exists, which the framework never determined.
    private readonly record struct NavTreatmentResult(
        NavTreatment Treatment, NavigationRouteDefinition? Route, bool AnyCandidateHasOpinion);

    private static NavTreatmentResult ResolveNavTreatment(string navName, IReadOnlyList<IEntitySetEndpointSource> candidates)
    {
        List<NavigationRouteDefinition>? delegateBacked = null; // DB(navName)
        bool anyDelegateLess = false; // DL(navName) non-empty?

        foreach (IEntitySetEndpointSource candidate in candidates)
        {
            NavigationRouteDefinition? route = candidate.NavigationRoutes.FirstOrDefault(n =>
                string.Equals(n.PropertyName, navName, StringComparison.OrdinalIgnoreCase));
            if (route is not null)
            {
                (delegateBacked ??= new List<NavigationRouteDefinition>()).Add(route);
            }
            else if (candidate.NavigationPropertyNames.Any(n => string.Equals(n, navName, StringComparison.OrdinalIgnoreCase)))
            {
                anyDelegateLess = true;
            }
        }

        bool anyOpinion = delegateBacked is not null || anyDelegateLess;
        if (delegateBacked is null) return new NavTreatmentResult(NavTreatment.ServeRaw, null, anyOpinion);
        if (delegateBacked.Count == 1 && !anyDelegateLess)
            return new NavTreatmentResult(NavTreatment.RunDelegate, delegateBacked[0], anyOpinion);
        return new NavTreatmentResult(NavTreatment.Blank, null, anyOpinion); // disagreement, or 2+ distinct routes
    }

    // OData JSON Format v4.01 §4.5.1 / §11.2.4.2: a navigation property that was not requested
    // via $expand MUST NOT appear in the payload — it is never serialised inline as an empty
    // array or null. System.Text.Json has no notion of $expand and serialises the whole CLR
    // graph, so this pass walks the serialised JSON against the EDM model and removes every
    // navigation member that was not expanded at its own level, recursing into the expanded ones
    // (following their nested $expand context) so a related entity never carries its own
    // un-expanded navigations. It only OMITS; the actual data for each expanded navigation —
    // including nested ones — is injected beforehand by ExpandLevelAsync (Stage 3, issue #183),
    // so by the time this runs an expanded navigation already holds its loaded related entities.
    // Only members that the EDM declares as navigation properties are touched, so structural
    // properties and @odata.* annotations are left untouched by construction.
    //
    // Post-#325/#326 role (Option B): SerializeBounded (below) now makes this pass a PRACTICAL
    // no-op at every one of its five call sites — it never writes an un-expanded navigation in the
    // first place, so there is nothing left here to strip. It stays wired in at every call site as
    // defence-in-depth against a CALLER-level mistake (a future call site that forgets to invoke
    // SerializeBounded, or invokes it with the wrong clause), never against a decision-table bug:
    // BuildExpandLookup/TryKeepNav below are the single shared source of the keep/recurse rules, so
    // the two methods cannot independently drift on what "kept" means the way they did before this
    // fold-in (see #325/#326 fold-in review — the maxLevels asymmetry that motivated the extraction).
    private static void OmitUnexpandedNavigations(
        JsonNode? node,
        IEdmEntityType? edmType,
        SelectExpandClause? clause,
        Type? clrType,
        JsonSerializerOptions? serializerOptions,
        (string Nav, int Remaining)? activeLevels = null,
        int maxLevels = MaxNestedExpandDepth,
        HashSet<string>? levelsNavNames = null)
    {
        if (edmType is null) return;

        // A JsonArray is a top-level collection or an expanded collection navigation — every
        // element is an entity of the same type sharing the same $expand context. A JsonObject is
        // a single entity. Anything else (null, i.e. an expanded single-valued navigation with no
        // related entity, or a primitive) has no navigations to strip and is left as-is.
        if (node is JsonArray array)
        {
            foreach (JsonNode? element in array)
            {
                OmitUnexpandedNavigations(element, edmType, clause, clrType, serializerOptions, activeLevels, maxLevels, levelsNavNames);
            }
            return;
        }
        if (node is not JsonObject obj) return;

        // Navigation name → its nested $expand clause, for the navigations expanded at THIS level.
        // Presence means "keep and recurse"; absence means "remove". #206: a nav carrying $levels=N is
        // ALSO recorded in levelsRemaining as its resolved recursion budget, so its self-reference is
        // kept (not stripped) at every level down to the depth actually loaded — Microsoft keeps
        // $levels implicit (a single top-level item), so without this the recursive levels below the
        // first would be stripped as "unexpanded". The keep is gated to navs that were actually PUSHED
        // (levelsNavNames): a delegate-backed $levels nav takes the delegate path (which loads only the
        // first level), so its deeper self-references must still be stripped as before — otherwise the
        // delegate's raw serialized graph would leak beyond depth 1.
        (Dictionary<string, SelectExpandClause?>? expanded, Dictionary<string, int>? levelsRemaining) =
            BuildExpandLookup(clause, levelsNavNames, maxLevels);

        // NavigationProperties() (not DeclaredNavigationProperties()) so inherited navigations on a
        // derived entity type are covered too. edmType is always an entity type here — the root is
        // the entity set's type and recursion passes navProp.ToEntityType() — so no complex-type
        // branch is needed.
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            // Match on the serialised key. #184: resolve the CLR property so a per-property
            // [JsonPropertyName] rename is honored ahead of the naming policy — System.Text.Json
            // writes a renamed nav under the attribute's exact name (it is NOT run through
            // PropertyNamingPolicy), so keying off the policy-converted name alone would miss a
            // renamed nav (leaking it inline) and a sibling $expand would write a second,
            // differently-cased key. Falls back to the naming-policy name when unrenamed, so a
            // symmetric JsonNamingPolicy (snake_case, etc.) still round-trips exactly.
            // #253 completion: navProp.Name is the EDM (JSON) navigation name, so map JSON→CLR to
            // reach the renamed CLR member and derive the payload key off it (a plain
            // GetProperty(jsonName) would miss it and mis-case the key under a non-camelCase policy).
            PropertyInfo? clrNavProp = clrType is null
                ? null
                : ODataPropertyNaming.FindClrPropertyByEdmName(clrType, navProp.Name);
            string serializedKey = ResolveNavigationJsonKey(clrNavProp?.Name ?? navProp.Name, clrNavProp, serializerOptions);

            // Fold-in #6 (de-duplication): TryKeepNav is the SAME keep/recurse rule SerializeBounded
            // uses (see its remarks below) — the two can no longer independently drift on what
            // "kept" means.
            NavKeepDecision decision = TryKeepNav(navProp.Name, expanded, levelsRemaining, activeLevels);
            if (!decision.Keep)
            {
                obj.Remove(serializedKey);
                continue;
            }

            // Recurse into the expanded value to strip ITS un-expanded navigations. obj[key]
            // is null when the expanded single-valued nav had no related entity — the recursive
            // call no-ops on a null node, so no separate presence check is needed. The nested
            // CLR type (element type for a collection nav) carries [JsonPropertyName] resolution
            // one level deeper.
            OmitUnexpandedNavigations(obj[serializedKey], navProp.ToEntityType(), decision.NestedClause,
                NavElementClrType(clrNavProp), serializerOptions, decision.ChildActive, maxLevels, levelsNavNames);
        }
    }

    // Fold-in #6 (#325/#326 review, de-duplication): the keep/recurse decision table shared by
    // OmitUnexpandedNavigations (JSON-strip substrate, above) and SerializeBounded (CLR-read+splice
    // substrate, below) — previously reimplemented byte-for-byte identically in both methods, which
    // had ALREADY drifted once (one passed maxLevels: source.MaxExpansionDepth at its Stage 1 call
    // site, the other silently defaulted to the file-wide MaxNestedExpandDepth ceiling — see fold-in
    // #5). Extracting a single shared implementation makes that class of drift structurally
    // impossible: the two methods now call the SAME code, so they can no longer disagree on what
    // "kept" means — only on what each one DOES with a kept/dropped decision (strip a JSON key vs.
    // never write a CLR value to it in the first place). This is orthogonal to (and does not weaken)
    // the defence-in-depth OmitUnexpandedNavigations itself provides — that comes from requiring the
    // CALL at every site, not from forking the decision logic (see its own remarks above).
    //
    // Returns navigation name -> its nested $expand clause for every navigation expanded at THIS
    // level, and (for a $levels-carrying self-referential nav that was actually PUSHED —
    // levelsNavNames) the resolved recursion budget. See OmitUnexpandedNavigations' original #206
    // remarks for the full $levels rationale.
    private static (Dictionary<string, SelectExpandClause?>? Expanded, Dictionary<string, int>? LevelsRemaining)
        BuildExpandLookup(SelectExpandClause? clause, HashSet<string>? levelsNavNames, int maxLevels)
    {
        Dictionary<string, SelectExpandClause?>? expanded = null;
        Dictionary<string, int>? levelsRemaining = null;
        if (clause is not null)
        {
            foreach (ExpandedNavigationSelectItem expandItem in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
            {
                string navName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;
                (expanded ??= new Dictionary<string, SelectExpandClause?>(StringComparer.OrdinalIgnoreCase))
                    [navName] = expandItem.SelectAndExpand;
                if (expandItem.LevelsOption is { } lv && levelsNavNames is not null && levelsNavNames.Contains(navName))
                {
                    // #428: the SAME resolution rule TryBuildEngagedExpand uses, called rather than
                    // re-spelled — these two used to be independent transcriptions of "what does
                    // $levels resolve to", and they disagreed. Math.Max(_, 1) stays HERE and not in
                    // the shared helper: this side wants a floor (a kept nav needs at least one
                    // level of budget), while TryBuildEngagedExpand wants < 1 to mean "not pushable".
                    int resolved = ResolveLevelsBudget(lv.IsMaxLevel, lv.Level, maxLevels, MaxNestedExpandDepth);
                    (levelsRemaining ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))[navName] =
                        Math.Max(resolved, 1);
                }
            }
        }
        return (expanded, levelsRemaining);
    }

    // Fold-in #6: whether navPropName is kept (explicitly $expand'd at this level, or still within
    // an inherited $levels budget) and, when kept, the nested clause / child recursion budget to
    // carry into the next level. Keep-vs-drop mirrors OmitUnexpandedNavigations' original #206
    // remarks exactly — this record IS that rule, not a re-derivation of it.
    private readonly record struct NavKeepDecision(
        bool Keep, SelectExpandClause? NestedClause, (string Nav, int Remaining)? ChildActive);

    private static NavKeepDecision TryKeepNav(
        string navPropName,
        Dictionary<string, SelectExpandClause?>? expanded,
        Dictionary<string, int>? levelsRemaining,
        (string Nav, int Remaining)? activeLevels)
    {
        SelectExpandClause? nested = null;
        bool explicitlyExpanded = expanded is not null && expanded.TryGetValue(navPropName, out nested);
        // #206 ($levels): keep the self-referential nav even without an explicit nested item when a
        // parent $levels expansion still has recursion budget for it.
        bool keptByLevels = !explicitlyExpanded && activeLevels is { } al &&
            string.Equals(al.Nav, navPropName, StringComparison.OrdinalIgnoreCase) && al.Remaining > 0;

        if (!explicitlyExpanded && !keptByLevels) return new NavKeepDecision(false, null, null);

        SelectExpandClause? nestedClause = explicitlyExpanded ? nested : null;
        // Resolve the levels budget carried into the next recursion: a fresh $levels=N at this
        // level seeds N-1; otherwise the inherited budget decrements. When it reaches 0 the nav
        // is kept at this level but its own self-reference below is stripped (depth honored).
        int? nextLevels =
            levelsRemaining is not null && levelsRemaining.TryGetValue(navPropName, out int freshLevels)
                ? freshLevels - 1
                : (keptByLevels ? activeLevels!.Value.Remaining - 1 : (int?)null);
        (string, int)? childActive = nextLevels is int nl && nl > 0 ? (navPropName, nl) : null;

        return new NavKeepDecision(true, nestedClause, childActive);
    }

    // #325/#326 (OWNER DECISIONS, FROZEN spec — Option B, "clause-bounded, level-wise
    // serialization"): the walker that replaces "serialize the whole CLR graph, then strip"
    // (OmitUnexpandedNavigations' job) with "serialize only what was asked for" at the point of
    // serialization itself. Root cause of #325/#326: whole-graph serialization is bounded by the
    // OBJECT GRAPH, but omission is bounded by the $expand CLAUSE, and omission runs strictly
    // AFTER serialization (Stage 3.5) — so any graph deeper/wider than the clause gets walked by
    // System.Text.Json first, and a bidirectional/self-referential EF model's relationship fixup
    // makes that graph cyclic, which SerializeToNode throws on.
    //
    // Mechanism: serialize <paramref name="value"/> with EVERY one of its EDM type's navigation
    // properties suppressed at the JsonTypeInfo level (GetNavSuppressedOptions — the same
    // TypeInfoResolver-modifier mechanism IgnoredPropertyJsonOptions.Build uses for Ignore()'d
    // properties), then for each navigation the $expand <paramref name="clause"/> keeps (or an
    // active $levels budget keeps — mirrors OmitUnexpandedNavigations' own keep/recurse decision
    // table exactly, including $levels bookkeeping, because that logic IS the spec), read the CLR
    // value via reflection and recurse into THIS method, splicing the result back onto the
    // suppressed JsonObject. Recursion is therefore bounded by the clause tree (finite: it is
    // exactly as deep as the client's own $expand/$levels request), never by the object graph, so
    // a reference cycle in the underlying CLR graph is structurally unreachable — the walker never
    // asks System.Text.Json to serialize a navigation property at all.
    //
    // Correctness for EVERY caller, including ones that later overwrite what this method wrote:
    // for a ServeRaw navigation (see ResolveNavTreatment/NavTreatment) nothing downstream touches
    // this key again, so the CLR value read here — whatever a member-init projection, an EF
    // Include, or the plain tracked graph already populated — is the answer this method commits to.
    // (Adjacent, pre-existing gap, #320: a ServeRaw nav's own CHILDREN may themselves be
    // delegate-backed — this method has no way to run a delegate, so a nested delegate-backed
    // navigation reached only through a ServeRaw parent is not resolved here. Not new to #325/#326;
    // tracked under #320, not fixed by this change.)
    // For a RunDelegate/Blank navigation this method may splice a stale or empty guess (the CLR
    // property is typically unpopulated before the delegate runs), but ExpandLevelAsync's
    // RunDelegate/Blank branches unconditionally overwrite jsonItems[i][expandKey] AFTER this
    // walker runs (see ApplyCollectionPipelineAsync Stage 1 and the Stage 3 delegate-injection
    // splice below) — a hard ordering requirement carried over from Model B (#292/#293) delegate
    // safety. This method never inspects NavTreatment/ResolveNavTreatment itself; it only decides
    // WHAT the $expand clause asked to see, never WHO is authoritative for the answer.
    //
    // <paramref name="edmType"/> null is the deliberate whole-graph fallback for ODataEntityNode's
    // deep-insert opt-out (§11.4.2.2): that caller passes no EDM type specifically so the freshly
    // deserialized POST/PUT/PATCH body graph stays inline, unbounded, exactly as before this fix —
    // do not "fix" that case here.
    //
    // Keys off value.GetType() (the RUNTIME type) rather than a statically-threaded CLR type for
    // both the suppression-options lookup and FindClrPropertyByEdmName, so a derived/polymorphic
    // entity instance's own (possibly inherited) navigation properties are found and suppressed
    // correctly even when reached through a base-typed navigation property.
    //
    // #343 CORRECTION TO THE PARAGRAPH ABOVE. Keying the CLR side off the runtime type was only
    // ever HALF of it: the navigation NAMES were still enumerated from the DECLARED
    // <paramref name="edmType"/>, so a navigation declared only on a DERIVED EDM type was never in
    // the suppression set and went straight to System.Text.Json — emitting unrequested data, and
    // 500ing outright when two derived instances referenced each other through one (measured on a
    // plain GET with NO query string). <paramref name="model"/> is what closes that: it lets
    // GetNavSuppressedOptions resolve the RUNTIME type's own EDM type and union in its navigations.
    // It is threaded rather than looked up ambiently because Microsoft.OData.Edm gives an
    // IEdmEntityType no back-reference to the model that owns it.
    //
    // <paramref name="isCollectionValue"/> (fold-in #2, 200→500 regression guard): whether
    // <paramref name="value"/> represents a COLLECTION of entities of <paramref name="edmType"/>
    // rather than a single entity. This is decided by the CALLER from EDM cardinality
    // (navRoute.IsCollection / navProp.Type.IsCollection()) — NEVER sniffed from value's own CLR
    // shape (the pre-fold-in code tested `value is IEnumerable`). Shape-sniffing misfires for an
    // entity model whose CLR class itself happens to implement IEnumerable (e.g. it wraps or
    // derives from a collection type for unrelated domain reasons): such an entity would be walked
    // element-by-element as if it WERE a collection of its own EDM type, instead of being serialized
    // as the single object it is — corrupting the response shape (or throwing, depending on what
    // its elements are) instead of a correct 200.
    private static JsonNode? SerializeBounded(
        object? value,
        IEdmEntityType? edmType,
        IEdmModel? model,
        SelectExpandClause? clause,
        JsonSerializerOptions? serializerOptions,
        (string Nav, int Remaining)? activeLevels = null,
        int maxLevels = MaxNestedExpandDepth,
        HashSet<string>? levelsNavNames = null,
        bool isCollectionValue = false)
    {
        if (value is null) return null;

        JsonSerializerOptions opts = serializerOptions ?? _pascalCaseSerializerOptions;

        if (edmType is null)
        {
            // Deliberate whole-graph fallback — see remarks above.
            return JsonSerializer.SerializeToNode(value, opts);
        }

        if (isCollectionValue)
        {
            // Perf (#337): the NESTED collection level is batched exactly like the root level —
            // ONE SerializeToNode call for the whole homogeneous sibling set instead of one per
            // element. Before this, SerializeBoundedCollection's batching only ever fired for the
            // root page, and its fast path ("the clause keeps NO navigation") was unreachable for
            // any $expand by construction, so ~99% of an $expand payload's bytes were still
            // serialized one entity at a time — the exact shape batching was introduced to remove.
            //
            // Materialized into a List<object?> by ENUMERATION (never handed to System.Text.Json as
            // the concrete collection) for two reasons, both load-bearing:
            //   1. Member dispatch. The element declared type is `object`, so STJ resolves each
            //      element's RUNTIME type and a DERIVED entity sitting in a base-typed collection
            //      still emits its own members (in STJ's derived-first property order). Handing STJ
            //      the concrete List<TBase> instead would serialize every element by its DECLARED
            //      type and silently drop those members.
            //   2. Index alignment. SerializeBoundedCollection pairs batched[i] with values[i], and
            //      is contractually index-preserving (see its remarks). Building the list by the
            //      same enumeration STJ will then walk keeps that true even for a collection whose
            //      indexer order disagrees with its enumeration order.
            //
            // `object`-declared dispatch is NOT, however, equivalent to runtime-TYPED dispatch:
            // supplying `object` as the declared type is exactly what triggers STJ's POLYMORPHIC
            // RE-ENTRY, which emits the type discriminator of the nearest [JsonPolymorphic] ancestor
            // ("$kind": "a", ...). The per-element SerializeToNode(element, element.GetType(), ...)
            // call this replaces passes declared type == runtime type, so STJ resolves that type's
            // own JsonTypeInfo and writes no discriminator at all. suppressPolymorphicMetadata: true
            // therefore makes SerializeBoundedCollection fall back to that exact per-element call for
            // the collections where the difference is observable — see its remarks.
            var elements = ((IEnumerable)value).Cast<object?>().ToList();
            return SerializeBoundedCollection(elements, edmType, model, clause, opts, maxLevels, levelsNavNames,
                activeLevels, suppressPolymorphicMetadata: true);
        }

        Type clrType = value.GetType();
        JsonSerializerOptions navSuppressed = GetNavSuppressedOptions(opts, model, (IEdmEntityType)edmType, clrType);
        JsonNode? node = JsonSerializer.SerializeToNode(value, clrType, navSuppressed);

        // Fold-in #2 (200→500 regression): a custom JsonConverter on the ENTITY type itself may
        // write a non-object shape (e.g. a bare string, as develop's whole-graph serializer would
        // also produce) — there are no navigations to splice into that, so return it as-is instead
        // of forcing .AsObject() and throwing InvalidOperationException.
        if (node is not JsonObject obj) return node;

        // Navigation name -> its nested $expand clause, for navigations expanded at THIS level —
        // and, for a $levels-carrying self-referential nav that was actually PUSHED (levelsNavNames),
        // the resolved recursion budget. Fold-in #6: shared with OmitUnexpandedNavigations via
        // BuildExpandLookup/TryKeepNav so the two can never disagree on what "kept" means.
        (Dictionary<string, SelectExpandClause?>? expanded, Dictionary<string, int>? levelsRemaining) =
            BuildExpandLookup(clause, levelsNavNames, maxLevels);

        SpliceKeptNavigations(obj, value, clrType, (IEdmEntityType)edmType, model, expanded, levelsRemaining, activeLevels, opts,
            maxLevels, levelsNavNames);

        return obj;
    }

    // Perf fix (measured regression vs. develop — GetAllPage/Filter/OrderBy/CountTrue/Select/
    // TopSkip allocation up 26-40%): the per-entity splice loop SerializeBounded ran inline above,
    // extracted so SerializeBoundedCollection (below) can reuse it WITHOUT re-deriving the
    // keep/recurse decision (expanded/levelsRemaining) per entity — every entity at the SAME level
    // shares the SAME clause, so BuildExpandLookup only needs to run ONCE per batch, not once per
    // entity. This is the single source both SerializeBounded and SerializeBoundedCollection call
    // to splice kept navigations onto an already nav-suppressed JsonObject; the two can no longer
    // independently drift on what gets spliced or how.
    private static void SpliceKeptNavigations(
        JsonObject obj,
        object value,
        Type clrType,
        IEdmEntityType edmType,
        IEdmModel? model,
        Dictionary<string, SelectExpandClause?>? expanded,
        Dictionary<string, int>? levelsRemaining,
        (string Nav, int Remaining)? activeLevels,
        JsonSerializerOptions opts,
        int maxLevels,
        HashSet<string>? levelsNavNames)
    {
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            // Perf (fold-in #7): decide keep/drop BEFORE any reflection — FindClrPropertyByEdmName/
            // ResolveNavigationJsonKey cost nothing for the (usually much more numerous) navigations
            // this level did NOT ask to see; they were already removed from `obj` by
            // GetNavSuppressedOptions above, so a dropped nav needs no further work at all.
            NavKeepDecision decision = TryKeepNav(navProp.Name, expanded, levelsRemaining, activeLevels);
            if (!decision.Keep) continue; // not requested — already suppressed above

            PropertyInfo? clrNavProp = ODataPropertyNaming.FindClrPropertyByEdmName(clrType, navProp.Name);
            object? navValue = clrNavProp?.GetValue(value);

            // Fold-in #1 (#325/#326 regression, DATA EXPOSURE): GetNavSuppressedOptions strips every
            // EDM navigation from clrType's JsonTypeInfo so System.Text.Json never walks into the
            // (potentially cyclic) graph at all — that suppression exists PURELY to keep the graph
            // walk bounded, never to decide member visibility. Splicing obj[serializedKey]
            // unconditionally below would therefore bypass whatever the BASE (un-suppressed) options
            // would themselves have decided about this member: a [JsonIgnore]'d navigation, one
            // hidden by JsonIgnoreCondition.WhenWritingNull/WhenWritingDefault, or one carrying a
            // custom [JsonConverter] (whose shape this recursive splice cannot honor) must all stay
            // absent/unmodified exactly as they would on develop — a clause-kept nav is never a
            // license to resurrect what the base serializer options would themselves have hidden.
            if (clrNavProp is not null && !IsNavVisibleInBaseOptions(opts, clrType, clrNavProp, value, navValue))
            {
                continue;
            }

            string serializedKey = ResolveNavigationJsonKey(clrNavProp?.Name ?? navProp.Name, clrNavProp, opts);
            if (navValue is null)
            {
                obj[serializedKey] = navProp.Type.IsCollection() ? new JsonArray() : null;
                continue;
            }

            obj[serializedKey] = SerializeBounded(
                navValue, navProp.ToEntityType(), model, decision.NestedClause, opts, decision.ChildActive, maxLevels,
                levelsNavNames, isCollectionValue: navProp.Type.IsCollection());
        }
    }

    // Perf fix (measured regression vs. develop, BenchmarkDotNet: GetAllPage +40%, Filter +38%,
    // OrderBy +35%, CountTrue +34%, Select +28%, TopSkip +26% allocated bytes): SerializeBounded
    // above is invoked once PER ENTITY on the collection GET path, so its
    // JsonSerializer.SerializeToNode call — one call per entity — replaced develop's ONE call for
    // the whole page. That is pure overhead whenever nothing is actually expanded (no $expand, or
    // — like the benchmark's BenchWidget model — an entity type with zero EDM navigations at all):
    // the per-entity walker buys cycle-safety (#325/#326) that never pays for itself because there
    // is nothing to walk.
    //
    // This is the collection-aware entry point used by ApplyCollectionPipelineAsync's Stage 1: ONE
    // JsonSerializer.SerializeToNode call over the WHOLE page (using nav-suppressed options, so the
    // cycle-safety guarantee is unchanged — no navigation is ever handed to System.Text.Json unless
    // the clause asks for it), producing a JsonArray in one shot exactly like develop's original
    // single Stage-1 call. Kept navigations are then spliced in per element via
    // SpliceKeptNavigations — but ONLY for navigations the clause actually keeps, and the whole
    // per-element splice pass is skipped entirely (FAST PATH) when the clause keeps none at all.
    //
    // #337: this is ALSO SerializeBounded's nested-collection branch. The fast path above is gated
    // on "the clause keeps NO navigation", which an $expand request never satisfies by definition —
    // so before #337 batching only ever paid off for a request that expanded nothing (or a model
    // with zero EDM navigations), and every entity BELOW the root was still serialized one
    // SerializeToNode call at a time. Routing the nested level here batches each homogeneous
    // sibling set into a single call as well, which is where the overwhelming majority of an
    // $expand payload's bytes actually live (measured: 1 batched call over ~1 KB vs. 1,000
    // individual calls over ~82 KB on the ExpandCollection benchmark shape). <paramref
    // name="activeLevels"/> is the $levels budget inherited from the parent level — always null at
    // the root call site (mirroring Stage 1's original per-entity call, which never passed one),
    // threaded through by the nested caller so the batched path's keep/recurse decision is the
    // SAME one the per-entity path made.
    //
    // Correctness: uses the SAME BuildExpandLookup/TryKeepNav decision table and the SAME
    // SpliceKeptNavigations splice SerializeBounded itself uses (single source, see its remarks),
    // so the two can never disagree on what "kept" means or how a kept nav gets spliced. The
    // keep/recurse decision (expanded/levelsRemaining) is computed ONCE for the whole batch — valid
    // because every element here is a SIBLING at the same level, serialized under the SAME clause
    // and the SAME inherited activeLevels budget, exactly as the per-entity loop this replaces
    // passed the identical (clause, activeLevels) pair to every element in turn.
    //
    // Array element order MUST match <paramref name="values"/>' source order exactly: STJ's
    // SerializeToNode over an IEnumerable preserves enumeration order (the same guarantee develop's
    // original single-call Stage 1 already relied on), and the splice loop below pairs
    // batched[i]/values[i] by that same index — see SerializeBoundedWalkerTests for an explicit
    // index-pairing assertion (a heterogeneous per-entity nav value pattern, e.g. entity N's nav
    // populated and its neighbours' not, would surface a misaligned splice as wrong data on the
    // wrong entity rather than passing by coincidence).
    private static JsonArray SerializeBoundedCollection(
        IReadOnlyList<object?> values,
        IEdmEntityType? edmType,
        IEdmModel? model,
        SelectExpandClause? clause,
        JsonSerializerOptions? serializerOptions,
        int maxLevels = MaxNestedExpandDepth,
        HashSet<string>? levelsNavNames = null,
        (string Nav, int Remaining)? activeLevels = null,
        bool suppressPolymorphicMetadata = false)
    {
        JsonSerializerOptions opts = serializerOptions ?? _pascalCaseSerializerOptions;
        if (values.Count == 0) return new JsonArray();

        if (edmType is null)
        {
            // Deliberate whole-graph fallback — mirrors SerializeBounded's own edmType:null branch
            // (see its remarks). One call for the whole collection, exactly as develop's original
            // single Stage-1 SerializeToNode(object[], ...) call: values is typed as
            // IReadOnlyList<object>, so System.Text.Json resolves each element by its own runtime
            // type (the same "boxed object" polymorphism develop's array call already relied on),
            // not by a single shared declared type.
            return JsonSerializer.SerializeToNode(values, opts) as JsonArray ?? new JsonArray();
        }

        (Dictionary<string, SelectExpandClause?>? expanded, Dictionary<string, int>? levelsRemaining) =
            BuildExpandLookup(clause, levelsNavNames, maxLevels);

        // Fast-path probe: does the clause keep ANY navigation of edmType at this level? Exactly
        // the same TryKeepNav rule SpliceKeptNavigations applies per entity below, evaluated ONCE
        // for the whole batch. No $expand (or an entity type with zero EDM navigations, like the
        // benchmark model) always lands here with anyNavKept == false.
        bool anyNavKept = false;
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            if (TryKeepNav(navProp.Name, expanded, levelsRemaining, activeLevels).Keep)
            {
                anyNavKept = true;
                break;
            }
        }

        // Walk the DISTINCT runtime types present, once, before the single batched serialize call
        // below. GetNavSuppressedOptions returns the SAME derived options instance regardless of
        // clrType (see CreateNavSuppressionState), so any successful call captures it.
        //
        // #482: this loop is NO LONGER load-bearing for suppression correctness, and must not be
        // read as if it were. It used to be the pre-population that "guaranteed" every element type
        // had a suppression set before its JsonTypeInfo was resolved — a guarantee that covered only
        // the types in THIS collection and left every transitively reached type frozen
        // un-suppressed. The modifier now computes each type's set itself from the seeded schema, so
        // a type this loop never sees is suppressed exactly as one it does. What the loop still does
        // is seed the model (once) and pair each runtime type with the caller's declared EDM type for
        // the no-ClrTypeAnnotation residue — plus the polymorphism probe piggy-backed below.
        JsonSerializerOptions? navSuppressed = null;
        HashSet<Type>? seenTypes = null;
        bool polymorphic = false;
        foreach (object? value in values)
        {
            if (value is null) continue;
            Type t = value.GetType();
            if ((seenTypes ??= new HashSet<Type>()).Add(t))
            {
                navSuppressed = GetNavSuppressedOptions(opts, model, (IEdmEntityType)edmType, t);
                // Piggy-backed on the distinct-type pass that already exists, so the polymorphism
                // test costs one cached lookup per DISTINCT runtime type per collection — never a
                // per-element check on the hot path.
                if (suppressPolymorphicMetadata && !polymorphic && EmitsPolymorphicMetadata(opts, t))
                {
                    polymorphic = true;
                }
            }
        }
        if (navSuppressed is null)
        {
            // Every element was null, so no runtime type was available to pre-populate suppression
            // for and there is nothing to serialize. #337: a JSON null per element, NOT an empty
            // array — a nested collection navigation may legitimately hold nulls, and the
            // per-element path this replaces emitted exactly one null per null element
            // (SerializeBounded returns null for a null value), so this is what keeps the nested
            // level byte-identical.
            //
            // The root call site's behaviour is unchanged either way, but NOT because "a page never
            // contains a null entity" — that would be an assumption about handler behaviour, not an
            // invariant. It is because a null entity in the page fails EARLIER in the pipeline: a
            // GetQueryable returning [null, null] 500s upstream of this method on develop and on
            // this branch alike, so the two shapes are never distinguishable at the root.
            // Independently, this shape is the safer one: develop's `return new JsonArray()` would
            // leave the returned array SHORTER than originalItems, which Stage 2 (ETag injection)
            // and Stage 3 (expansion) index by originalItems.Length — a latent desync this removes.
            var allNull = new JsonArray();
            for (int i = 0; i < values.Count; i++) allNull.Add((JsonNode?)null);
            return allNull;
        }

        if (polymorphic)
        {
            // #337 correctness fallback: at least one element's type hierarchy has polymorphism
            // CONFIGURED (a [JsonPolymorphic] ancestor, or the equivalent set up by a custom
            // TypeInfoResolver — see EmitsPolymorphicMetadata, which asks STJ rather than reading
            // attributes). Batching hands STJ an `object`-declared element, which makes it take the
            // polymorphic re-entry path and write a type discriminator ("$kind", "$type", ...) that
            // the per-element call never wrote. That is not a cosmetic difference: the discriminator
            // is an arbitrary STJ key in an OData payload (not @odata.type), StripToSelectedProperties
            // /KeepUnderSelect only preserve keys containing '@' so it silently vanishes under
            // $select, and the standalone navigation route (which still serializes per element)
            // would disagree with $expand on the same navigation.
            //
            // So for these collections only, serialize exactly as before batching: one
            // SerializeToNode per element with declared type == runtime type. Everything else keeps
            // the batched path — this costs the optimization only for genuinely polymorphic models.
            //
            // Deliberately NOT applied at the root call site (suppressPolymorphicMetadata defaults to
            // false): the root page was ALREADY batched before #337, so develop already emits the
            // discriminator there. Suppressing it at the root would be a second output change, not a
            // fix — byte-identity with develop is the acceptance criterion, not internal symmetry.
            var perElement = new JsonArray();
            foreach (object? value in values)
            {
                perElement.Add(SerializeBounded(
                    value, edmType, model, clause, opts, activeLevels, maxLevels, levelsNavNames));
            }
            return perElement;
        }

        JsonArray batched = JsonSerializer.SerializeToNode(values, navSuppressed) as JsonArray ?? new JsonArray();

        if (!anyNavKept) return batched; // FAST PATH: nothing to splice.

        for (int i = 0; i < values.Count && i < batched.Count; i++)
        {
            // node-is-not-JsonObject guard (fold-in #2, 200→500 regression fix): a custom
            // JsonConverter on the entity type may write a non-object shape — nothing to splice
            // into. A null values[i] (defensive; the collection GET path never hands this method a
            // null entity) is likewise left as whatever the batched call already produced for it.
            if (values[i] is not { } value || batched[i] is not JsonObject obj) continue;

            SpliceKeptNavigations(obj, value, value.GetType(), (IEdmEntityType)edmType, model, expanded, levelsRemaining,
                activeLevels, opts, maxLevels, levelsNavNames);
        }

        return batched;
    }

    // Fold-in #1 (#325/#326 regression, data exposure): true when the BASE (pre-nav-suppression)
    // JsonTypeInfo for clrType — resolved through the SAME resolver GetNavSuppressedOptions itself
    // falls back to (never `opts.GetTypeInfo` directly: an `opts` instance that has never actually
    // been handed to `JsonSerializer.Serialize`/`SerializeToNode` and carries no explicit
    // `TypeInfoResolver` throws `NotSupportedException` from `GetTypeInfo`, even though
    // `SerializeToNode` itself tolerates that exact options shape via its own implicit reflection
    // fallback — `_pascalCaseSerializerOptions`, this file's own defensive fallback, is exactly such
    // an instance) — would itself emit clrNavProp for this entity instance.
    //
    // BOTH [JsonIgnore] SPELLINGS ARE DECIDED BY ShouldSerialize, NOT BY ABSENCE. This comment used
    // to say that a plain [JsonIgnore] "removes the member from typeInfo.Properties entirely", with
    // the presence check below as the mechanism. That is FALSE on .NET 10 — measured against
    // DefaultJsonTypeInfoResolver on 10.0.11:
    //
    //   Hidden   get=null set=null shouldSer=fn   <- [JsonIgnore], STILL IN Properties
    //
    // i.e. an unconditionally ignored member stays in JsonTypeInfo.Properties with Get and Set
    // nulled out and a ShouldSerialize delegate that returns false (verified by invoking it). So
    // the loop below DOES find it, and the `return` inside the loop — not the `return false` after
    // it — is what answers "not visible" for it. The method's outcome was always right; only the
    // stated reason was wrong. JsonIgnoreCondition.WhenWritingNull/WhenWritingDefault land in the
    // same place by the same route: present, gated at serialize time by ShouldSerialize (invoked
    // here with the SAME (owner, value) pair System.Text.Json itself would use).
    //
    // Getting this right is load-bearing rather than cosmetic, because the open-type modifier
    // (OpenTypeJsonOptions.Build) snapshots its declared-name set from exactly this collection: a
    // navigation carrying [JsonIgnore] is still in Properties, so a dynamic bag key spelled like it
    // still collides and still hard-fails, instead of quietly shadowing the navigation. A reader who
    // believed the old comment would conclude the opposite.
    //
    // The trailing `return false` therefore covers only members genuinely absent from the base
    // contract — most importantly one an earlier TypeInfoResolver modifier REMOVED, which is how
    // Ignore() (#226) works and is a real difference from [JsonIgnore]: a removed member is not in
    // Properties at all, which is precisely why extension data can capture it (see
    // OpenTypeJsonOptions' remarks on Ignore() containment).
    //
    // A custom [JsonConverter] on the property changes its wire shape in a way SerializeBounded's own
    // recursive splice cannot reproduce, so that case is treated as not natively-visible too —
    // omitted rather than corrupted with the wrong shape.
    private static bool IsNavVisibleInBaseOptions(
        JsonSerializerOptions opts, Type clrType, PropertyInfo clrNavProp, object entityValue, object? navValue)
    {
        if (clrNavProp.GetCustomAttribute<JsonConverterAttribute>() is not null) return false;

        JsonTypeInfo? typeInfo = GetBaseTypeInfo(opts, clrType);
        if (typeInfo is null) return false;
        foreach (JsonPropertyInfo p in typeInfo.Properties)
        {
            // #462/#343 FOLLOW-ON, A FIFTH INSTANCE OF THE SAME DEFECT CLASS — found by the shared
            // fixture, not by the issues. `pi != clrNavProp` is PropertyInfo equality, which also
            // compares ReflectedType, and for an INHERITED navigation on a DERIVED runtime instance
            // the two sides come from reflection walks that disagree about it. MEASURED on .NET
            // 10.0.11 for `RtDerived : RtBase` with `Items` declared on the base:
            //     clrNavProp.ReflectedType = RtDerived   (FindClrPropertyByEdmName does
            //                                             RtDerived.GetProperties())
            //     pi.ReflectedType         = RtBase      (System.Text.Json's AttributeProvider)
            //     pi == clrNavProp -> false ; pi.HasSameMetadataDefinitionAs(clrNavProp) -> true
            // So the loop never matched, the trailing `return false` answered "not visible", and
            // SpliceKeptNavigations SKIPPED the navigation — a declared navigation the client
            // explicitly $expand'ed was silently DROPPED from every derived instance, while the base
            // instances in the same page kept theirs. That is the exact mirror of #343 (which emits
            // what nobody asked for); fixing only one of the two would leave a derived entity unable
            // to serve any expanded navigation at all.
            //
            // HasSameMetadataDefinitionAs is the same comparison, for the same reason, that
            // OpenTypeJsonOptions.Build already uses against its own AttributeProvider — see the long
            // comment there. It is NOT whole-member identity (it matches across generic
            // instantiations); what makes it safe here is the same thing that makes it safe there:
            // both sides are members of `clrType` or of one of its base types, and a single type's
            // member list cannot contain two instantiations of one generic definition. Non-derived
            // instances are unaffected — their two PropertyInfos already compared equal.
            if (p.AttributeProvider is not PropertyInfo pi || !pi.HasSameMetadataDefinitionAs(clrNavProp)) continue;
            return p.ShouldSerialize is null || p.ShouldSerialize(entityValue, navValue);
        }
        // Absent from the base contract entirely. NOT the [JsonIgnore] case — that member is still in
        // Properties and was answered by the ShouldSerialize return above (see the note on this
        // method). This is the modifier-REMOVED case (Ignore(), #226) and the
        // no-metadata-for-the-type case.
        return false;
    }

    // Fold-in #7 (perf hygiene): ONE derived JsonSerializerOptions per baseOptions (not one per
    // (baseOptions, ClrType) pair as before) — mirrors IgnoredPropertyJsonOptions.Build's own
    // pattern (see its remarks): a single TypeInfoResolver modifier consults a type-keyed lookup
    // built INSIDE this cache entry, so N entity types sharing a registration's baseOptions share
    // ONE derived JsonSerializerOptions and therefore one JsonTypeInfo cache, instead of each type
    // paying for (and duplicating work across) its own independent derived-options instance.
    //
    // #482 — THE PRE-POPULATION "GUARANTEE" THAT USED TO BE ASSERTED HERE WAS FALSE, AND ITS
    // FALSENESS WAS A 500-FOREVER. This comment used to read: "The per-type nav-name set is
    // populated via GetOrAdd BEFORE returning Derived, which guarantees it is present the first time
    // `clrType` is ever handed to Derived for serialization." That held only for types handed to
    // GetNavSuppressedOptions *directly*. It said nothing about a type System.Text.Json reaches on
    // its OWN, transitively, while walking a graph — and STJ resolves and permanently caches a
    // type's JsonTypeInfo on FIRST use per options instance, so such a type froze UN-SUPPRESSED for
    // the process lifetime. Measured at e3a7bd3 through the shipped code: serialize one entity whose
    // open-type dynamic bag holds a live instance of ANOTHER entity type (bag values are stored
    // verbatim and serialized by runtime type), and the NEXT read of that other entity set throws
    // `JsonException: A possible object cycle was detected` on an ordinary parent/child fixup graph —
    // rendered by the group filter as 500 on a plain GET with no query string, on every request,
    // forever. #343 resurrected, permanently, by nothing but serialization ORDER.
    //
    // THE ORDER DEPENDENCE IS NOW GONE BY CONSTRUCTION, not patched edge by edge. The modifier below
    // computes the suppression set ITSELF, as a pure function of (typeInfo.Type, the EDM), at the
    // moment STJ resolves the contract — so it cannot matter which route reached the type, or
    // whether any route reached it at all. `EdmTypeByClrType` is what makes that function
    // total: one walk of the schema (SeedNavSuppressionModel) maps EVERY EDM-declared entity type to
    // its CLR type up front, before any contract can be resolved on Derived. Nothing about the
    // caller, the call order, or the reachability path is consulted.
    //
    // The map is keyed CLR->EDM via ODataConventionModelBuilder's own ClrTypeAnnotation, NOT via
    // model.FindDeclaredType(clrType.FullName) as the #343 union did. That was not a style choice:
    // FindDeclaredType matches on the EDM type's FULL NAME, so a registration that sets
    // `ODataConventionModelBuilder.Namespace` (or otherwise renames the schema) makes every lookup
    // miss — measured: with `mb.Namespace = "Custom.Ns"`, `FindDeclaredType(typeof(Beta).FullName)`
    // returns null while the annotation resolves Beta correctly. #343's runtime-type union was
    // therefore a silent no-op on any custom-namespace model; it is not any more.
    //
    // Coverage this buys, beyond the measured dynamic-bag edge: an `object`-declared CLR member
    // holding an entity, a COMPLEX type carrying an entity-typed member, an EF Core lazy-loading
    // proxy or any other CLR subclass the EDM does not declare (the base-chain walk in
    // BuildNavClrNames resolves it to its nearest EDM-known ancestor), and any future edge nobody
    // has thought of — because none of them is an edge any more.
    //
    // #507 — THE "COMPLEX TYPE CARRYING AN ENTITY-TYPED MEMBER" CLAIM ABOVE WAS FALSE WHEN #491
    // MADE IT, AND IS TRUE ONLY NOW. The seed walked model.SchemaElements.OfType<IEdmEntityType>()
    // alone, and AddNavClrNames read navigations off entity types alone. But
    // ODataConventionModelBuilder models an entity-typed member of a COMPLEX type as a navigation
    // ON THE COMPLEX TYPE (measured: `class PxMeta { string Note; PxEntity Owner; }` yields
    // `<ComplexType Name="PxMeta"><NavigationProperty Name="Owner" .../>`), so BuildNavClrNames'
    // base-chain lookup into an entity-only map computed an EMPTY set for every complex CLR type and
    // the modifier removed nothing. What #491's own measurement covered was the entity reached
    // THROUGH the member — `Owner.Children` really was suppressed — which is exactly why the gap
    // looked closed. Measured on the pre-fix tree, both consequences on a PLAIN GET with no query
    // string: `"Meta":{"Note":"y","Owner":{...}}` — navigation data inline with no $expand naming it
    // (§4.5.1) — and `E.Meta.Owner = E` throwing `JsonException: A possible object cycle was
    // detected`, i.e. the group filter's 500, on every request. Neither is order-dependent and
    // neither needs open types, unlike #482's poisoning step.
    //
    // The map is therefore keyed by IEdmStructuredType now, and the invariant test that could not
    // see this (RuntimeTypeConfigResolutionTests.EveryEdmEntityType_ResolvesSuppressed_*, a universal
    // statement that quantified over ENTITY types only) has a complex-type twin.
    //
    // SUPPRESSED, NOT SERVED, for the same reason a derived-declared navigation is (see
    // GetNavSuppressedOptions' remarks): §4.5.1/§11.2.4.2 require a non-expanded navigation to be
    // omitted, and SpliceKeptNavigations iterates the ENTITY type's navigations, so a complex type's
    // navigation has no route into `expanded` and no splice would ever put it back. "Serve it" would
    // mean serving it unconditionally, which is the defect. `$expand=Meta/Owner` — a complex-type
    // path segment the OData parser does accept — is consequently omitted rather than expanded; that
    // is a pre-existing feature gap (the splice never handled it), not a regression, and it now fails
    // the same way an unexpanded navigation does instead of leaking the whole graph.
    //
    // The review's third reasoned edge — a navigation FindClrPropertyByEdmName cannot resolve — is
    // REAL and is closed at AddNavClrNames instead, by reading the model builder's own
    // ClrPropertyInfoAnnotation alongside the name lookup. See that method: an EDM-level rename
    // (reachable through AdvancedConfigure) produced a navigation the name lookup could not see at
    // all, so it was never suppressed on ANY route — order-independently broken rather than
    // order-dependently broken.
    //
    // NOT closed, and deliberately so: a CLR type that is not an EDM entity type in ANY seeded model
    // and holds a reference to itself. Suppression is defined by the EDM; a member the EDM does not
    // call a navigation is data, and removing it would be a silent data loss rather than a fix.
    // That residue is #440's territory (a CLR type absent from the EDM), not this one's.
    //
    // Also caches the BASE (un-suppressed) JsonTypeInfo per clrType — fold-in #1's
    // IsNavVisibleInBaseOptions needs it, resolved through the SAME captured BaseResolver fallback
    // (never `opts.GetTypeInfo`, which requires the caller's options to already carry an explicit
    // resolver — see IsNavVisibleInBaseOptions' remarks) — so that lookup is O(1) after first use
    // too, instead of re-resolving/re-walking clrType's properties on every kept navigation.
    //
    // Keyed via ConditionalWeakTable (not the previous ConcurrentDictionary<(Options,Type),...>) so
    // an options instance's cache entry is collected along with the options itself — the previous
    // strong-keyed dictionary leaked one entry per distinct JsonSerializerOptions for the life of
    // the process, which matters for test suites (e.g. WebApplicationFactory) that construct a fresh
    // host — and therefore fresh options — per test class.
    //
    // EdmTypeByClrType (#482, widened to complex types by #507): every EDM-declared STRUCTURED type
    // — entity AND complex — of every model seeded onto this options instance, keyed by its CLR type.
    // Filled by SeedNavSuppressionModel BEFORE any contract can be resolved on Derived; read — never
    // written — by the resolver modifier. Together with the type it is handed, it is the WHOLE of the
    // modifier's input.
    //
    // SeededModels/SeedGate (#482): which IEdmModel instances have already been walked into
    // EdmTypeByClrType, and the gate that publishes that flag LAST so a second thread which
    // observes "seeded" also observes a COMPLETE map. See SeedNavSuppressionModel for why a bare
    // ConcurrentDictionary flag would not be enough.
    private sealed record NavSuppressionState(
        JsonSerializerOptions Derived,
        IJsonTypeInfoResolver BaseResolver,
        ConcurrentDictionary<Type, HashSet<string>> NavClrNamesByType,
        ConcurrentDictionary<Type, JsonTypeInfo?> BaseTypeInfoByType,
        ConcurrentDictionary<Type, bool> PolymorphicByType,
        ConcurrentDictionary<Type, NavSourceBinding> EdmTypeByClrType,
        ConcurrentDictionary<IEdmModel, bool> SeededModels,
        object SeedGate);

    // #482: what the seeded map holds. The MODEL travels with the EDM type because the
    // navigation -> CLR member mapping the builder recorded lives as an annotation ON THE MODEL, and
    // Microsoft.OData.Edm gives an IEdmNavigationProperty no back-reference to the model that owns
    // it - the same reason IEdmModel is threaded through the SerializeBounded family (#343).
    // #507: IEdmStructuredType, not IEdmEntityType — a complex type carrying an entity-typed member
    // declares navigations too, and they were never in the map (see CreateNavSuppressionState).
    private readonly record struct NavSourceBinding(IEdmModel? Model, IEdmStructuredType EdmType);

    private static readonly ConditionalWeakTable<JsonSerializerOptions, NavSuppressionState>
        s_navSuppressedOptionsCache = new();

    private static NavSuppressionState CreateNavSuppressionState(JsonSerializerOptions baseOptions)
    {
        var navClrNamesByType = new ConcurrentDictionary<Type, HashSet<string>>();
        var edmTypeByClrType = new ConcurrentDictionary<Type, NavSourceBinding>();
        IJsonTypeInfoResolver baseResolver = baseOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        var derived = new JsonSerializerOptions(baseOptions);
        derived.TypeInfoResolver = baseResolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            // #482: COMPUTE, never look up and give up. The old code did TryGetValue and returned
            // when the type had no entry — which is precisely how a transitively reached type froze
            // un-suppressed forever. GetOrAdd means the answer for a type is decided HERE, once, from
            // the EDM, no matter who reached it or in what order.
            HashSet<string> navClrNames =
                navClrNamesByType.GetOrAdd(typeInfo.Type, t => BuildNavClrNames(t, edmTypeByClrType));
            if (navClrNames.Count == 0) return;
            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo prop && navClrNames.Contains(prop.Name))
                    typeInfo.Properties.RemoveAt(i);
            }
        });
        return new NavSuppressionState(
            derived, baseResolver, navClrNamesByType,
            new ConcurrentDictionary<Type, JsonTypeInfo?>(), new ConcurrentDictionary<Type, bool>(),
            edmTypeByClrType, new ConcurrentDictionary<IEdmModel, bool>(), new object());
    }

    // #482: the modifier's pure function. The CLR property names on <paramref name="clrType"/> that
    // back a navigation of ANY EDM entity type on <paramref name="clrType"/>'s own CLR base chain.
    //
    // The base walk (rather than an exact-type lookup) is what covers a runtime type the EDM does not
    // declare at all: an EF Core lazy-loading proxy, a Castle/DynamicProxy subclass, or any derived
    // CLR type a handler returns through a base-typed entity set. Its nearest EDM-known ancestor's
    // navigations are resolved AGAINST THE RUNTIME TYPE — FindClrPropertyByEdmName is given
    // <paramref name="clrType"/>, never the ancestor — so a shadowed or overridden member resolves to
    // the member System.Text.Json will actually put on the contract.
    //
    // UNION over the chain, not nearest-wins, and that is not interchangeable: EDM navigation sets
    // are a suppression boundary (#325/#326's premise is that NO navigation reaches STJ unspoken
    // for), so a derived type's set must never shadow its base's. Same policy, same reason, as
    // InheritedNameSets.Resolve — interfaces deliberately not walked, since an EDM entity type is
    // always a class.
    //
    // Cost: one HashSet, plus one FindClrPropertyByEdmName and one annotation lookup per navigation,
    // per DISTINCT runtime type per options instance — memoized in NavClrNamesByType, and
    // FindClrPropertyByEdmName is itself process-wide memoized. Nothing per request, nothing per
    // entity. The schema walk that feeds it is measured at ~0.9 us per EDM entity type (0.44 ms for a
    // 400-entity-type model), paid once at MapOhData().
    private static HashSet<string> BuildNavClrNames(
        Type clrType, ConcurrentDictionary<Type, NavSourceBinding> edmTypeByClrType)
    {
        var navClrNames = new HashSet<string>(StringComparer.Ordinal);
        for (Type? cur = clrType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            if (edmTypeByClrType.TryGetValue(cur, out NavSourceBinding binding))
                AddNavClrNames(navClrNames, binding.Model, binding.EdmType, clrType);
        }
        return navClrNames;
    }

    // #482: one walk of <paramref name="model"/>'s schema, mapping every EDM entity type to its CLR
    // type, so BuildNavClrNames above is TOTAL rather than dependent on who called first.
    //
    // Called from MapAll (PrimeNavSuppression — before a single request is served) and, defensively,
    // from GetNavSuppressedOptions on every call, since the SerializeBounded family is reachable in
    // tests and from the `_pascalCaseSerializerOptions` fallback without going through MapAll.
    //
    // CONCURRENCY. The whole point of #482 is that whichever thread resolves a JsonTypeInfo first
    // decides that type's behaviour for the process lifetime, so "seeded" must never be observable
    // before the map it promises is complete. A bare `SeededModels.TryAdd(model, true)` guard would
    // do exactly that: thread A wins the TryAdd and starts filling the map, thread B loses it,
    // concludes "already seeded", and serializes against a half-filled map — re-creating the defect
    // under a different trigger. Hence the gate: the flag is written INSIDE the lock and LAST, so a
    // lock-free reader that sees `true` is guaranteed (by ConcurrentDictionary's volatile publish and
    // the lock's release) to see every map entry written before it. B's fast-path miss costs it the
    // lock, where it waits for A and then returns.
    //
    // Idempotent per model, and additive across models: two registrations that share one
    // JsonSerializerOptions instance (only reachable via the `_pascalCaseSerializerOptions` fallback —
    // MapAll threads a per-registration `effectiveJsonOptions`) union their schemas rather than
    // first-one-wins. Union is the safe direction here; the residue is a CLR type declared by BOTH
    // models with DIFFERENT navigations, whose first-resolved contract still wins, and which #458
    // already refuses within a registration.
    private static void SeedNavSuppressionModel(NavSuppressionState state, IEdmModel? model)
    {
        if (model is null || state.SeededModels.ContainsKey((IEdmModel)model)) return;
        lock (state.SeedGate)
        {
            if (state.SeededModels.ContainsKey((IEdmModel)model)) return;
            // EdmClrTypeMap reads ODataConventionModelBuilder's own ClrTypeAnnotation for every
            // STRUCTURED type the schema declares — the same "read the builder's own annotation"
            // route OpenTypeJsonOptions takes for a complex type's dynamic-property container, and
            // for the same reason: it involves no name convention, so a renamed schema namespace
            // cannot make it miss. Absent only for a hand-built IEdmModel, which OhData never
            // produces; GetNavSuppressedOptions' caller pairing below covers that residue for
            // directly served types.
            //
            // #507: entity AND complex. The walk used to be OfType<IEdmEntityType>(), which is why a
            // complex type's own entity-typed navigation was never in any suppression set.
            foreach (KeyValuePair<Type, IEdmStructuredType> pair in EdmClrTypeMap.ForModel((IEdmModel)model))
            {
                state.EdmTypeByClrType.TryAdd(pair.Key, new NavSourceBinding(model, pair.Value));
            }
            state.SeededModels[model] = true;
        }
    }

    // #482: called once per registration from MapAll, with the SAME options instance every route
    // closure is handed, so the schema walk happens before any request rather than on whichever
    // request happens to arrive first. Purely a map fill — it resolves no JsonTypeInfo and installs
    // no modifier, so it is inert with respect to the ignore -> open-type -> nav-suppression modifier
    // ordering invariant (OpenTypeModifierOrderingTests).
    private static void PrimeNavSuppression(JsonSerializerOptions baseOptions, IEdmModel? model)
    {
        SeedNavSuppressionModel(
            s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState), model);
    }

    // #343: THE SUPPRESSION SET IS BUILT FROM THE RUNTIME TYPE, NOT THE DECLARED EDM TYPE ALONE.
    //
    // #325/#326 ("Option B", clause-bounded serialization) rest on one structural premise: NO
    // navigation ever reaches System.Text.Json unless the $expand clause asked for it. That is what
    // makes a reference cycle structurally unreachable rather than merely unlikely. Enumerating only
    // `edmType.NavigationProperties()` broke the premise for a navigation declared on a DERIVED
    // entity type: it was never in the set, so it was never removed from the runtime type's
    // JsonTypeInfo, so STJ walked into it. Measured on the pre-fix tree, both consequences the issue
    // reports, on a PLAIN GET with no query string at all: a derived-declared collection navigation
    // emitted inline (`"Notes":[...]` with no $expand=Notes anywhere), and two derived instances
    // referencing each other through a derived-declared single navigation 500ing on the collection
    // route and on GetById alike.
    //
    // SUPPRESSED, NOT SERVED — and the alternative is not a close call. (a) OData JSON Format 4.01
    // §4.5.1 / §11.2.4.2 require a non-expanded navigation to be OMITTED, never emitted inline; a
    // derived-declared navigation is a navigation. (b) It cannot be asked for: the $expand clause is
    // bound against the entity set's DECLARED type, and SpliceKeptNavigations likewise iterates the
    // declared type's navigations, so a derived-declared nav has no route into `expanded` and no
    // splice would ever put it back. "Serve it" would therefore mean serving it UNCONDITIONALLY,
    // which is the defect. (c) It is consistent with #293's frozen Model B spec rather than in
    // tension with it: Model B decides WHO is authoritative for a navigation the clause kept at a
    // level whose EDM type is known (ResolveProfilesForEdmType matches the EXACT EDM type, never CLR
    // assignability), and it never has an opinion about a navigation no clause can name. #440's
    // ServeRaw split points the same way — a navigation no candidate declares or routes is OMITTED,
    // not emitted as null.
    //
    // #482 SUPERSEDES THE MECHANISM, NOT THE DECISION. Everything above still holds; what changed is
    // WHERE the runtime type's EDM type comes from and WHEN it is consulted. This method no longer
    // computes any suppression set — the resolver modifier does, from the seeded CLR->EDM map, at
    // contract-resolution time (see CreateNavSuppressionState). All this method contributes is the
    // seeding, which must happen before the SerializeToNode call the caller is about to make.
    //
    // model.FindDeclaredType(clrType.FullName) is gone from the lookup path: it matches on the EDM
    // type's full name, so it silently returned null — and #343's union silently did nothing — for
    // any model whose schema namespace was renamed. The ClrTypeAnnotation route SeedNavSuppressionModel
    // uses has no such failure mode.
    //
    // The two TryAdds are the residue guard for a model carrying no ClrTypeAnnotation (a hand-built
    // IEdmModel; OhData itself always builds through ODataConventionModelBuilder, which writes the
    // annotation). Precedence is deliberate and matches what the seed would have produced: the
    // runtime type's OWN declared EDM type first, the caller's DECLARED base type second, and both
    // lose to whatever the seed already put there — TryAdd never overwrites.
    //
    // Guarded by ContainsKey because this method runs ONCE PER ENTITY on the single-entity path. In
    // steady state the whole method is two dictionary probes — the same order of cost as the single
    // GetOrAdd it replaced.
    //
    // #508: the residue guard's first TryAdd used model.FindDeclaredType(clrType.FullName), the last
    // read-path survivor of that convention. It is EdmClrTypeMap now — which, on a model built by
    // ODataConventionModelBuilder, is exactly what the seed above already put in the map, so this
    // branch is reached only for a hand-built IEdmModel with no ClrTypeAnnotation at all. There the
    // second TryAdd (the CALLER's declared EDM type) is the whole of the residue guard, unchanged.
    private static JsonSerializerOptions GetNavSuppressedOptions(
        JsonSerializerOptions baseOptions, IEdmModel? model, IEdmEntityType edmType, Type clrType)
    {
        NavSuppressionState state = s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState);
        SeedNavSuppressionModel(state, model);
        if (!state.EdmTypeByClrType.ContainsKey(clrType))
        {
            if (EdmClrTypeMap.FindStructuredType(model, clrType) is { } runtimeEdmType)
                state.EdmTypeByClrType.TryAdd(clrType, new NavSourceBinding(model, runtimeEdmType));
            state.EdmTypeByClrType.TryAdd(clrType, new NavSourceBinding(model, edmType));
        }
        return state.Derived;
    }

    // The CLR property names on <paramref name="clrType"/> that back <paramref name="edmType"/>'s
    // navigations (NavigationProperties() is inherited-inclusive, so a base type's navigations come
    // along with a derived one's).
    //
    // #507: edmType is an IEdmStructuredType, not an IEdmEntityType. NavigationProperties() is
    // declared on IEdmStructuredType precisely because a COMPLEX type can carry them, and
    // ODataConventionModelBuilder puts one there for every entity-typed member of a complex type.
    //
    // TWO routes, UNIONED, because either one alone has a blind spot (#482, the third edge the review
    // reasoned about).
    //
    // (1) FindClrPropertyByEdmName - the same lookup the splice uses, so for every navigation it can
    //     resolve, suppression and splice cannot disagree about which member is meant.
    //
    // (2) The model builder's own ClrPropertyInfoAnnotation on the navigation - the AUTHORITATIVE
    //     record of the backing member, written when the navigation was built. Route (1) matches on
    //     the EDM NAME (via [JsonPropertyName] or the CLR name, case-insensitively), so an
    //     EDM-LEVEL rename defeats it outright: measured against the referenced package,
    //     `mb.EntityType<Beta>().HasMany(b => b.Children).Name = "Kids"` yields an EDM navigation
    //     named `Kids` whose annotation still reports `Children`, and `FindClrPropertyByEdmName(
    //     typeof(Beta), "Kids")` returns null. Pre-#482 that navigation was therefore NEVER
    //     suppressed on any route - the #343 leak and the cycle-500 both, reachable through
    //     AdvancedConfigure's full EDM control. The annotation closes it.
    //
    // Union rather than either/or: (2) is absent for a hand-built IEdmModel, and (1) is what covers a
    // member (2) never recorded. Over-suppression is not a risk from adding (2) - it names the very
    // member the builder mapped to this navigation, and a name that is not on the contract removes
    // nothing.
    //
    // KNOWN, OUT OF SCOPE, and NOT made worse here: SpliceKeptNavigations still reads the value
    // through route (1) alone, so an EDM-renamed navigation that the clause DOES $expand is spliced
    // as an empty array rather than its data. That is a data-plumbing defect of its own; before this
    // change the same request ALSO emitted the whole un-suppressed CLR graph under the wrong key, so
    // suppression strictly improves it.
    private static void AddNavClrNames(
        HashSet<string> into, IEdmModel? model, IEdmStructuredType edmType, Type clrType)
    {
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            PropertyInfo? clrProp = ODataPropertyNaming.FindClrPropertyByEdmName(clrType, navProp.Name);
            if (clrProp is not null) into.Add(clrProp.Name);

            string? declaredMember = model?
                .GetAnnotationValue<Microsoft.OData.ModelBuilder.ClrPropertyInfoAnnotation>(navProp)?
                .ClrPropertyInfo?.Name;
            if (declaredMember is not null) into.Add(declaredMember);
        }
    }

    // Fold-in #1 support: the BASE (un-suppressed) JsonTypeInfo for clrType under baseOptions,
    // resolved via the captured BaseResolver fallback (see CreateNavSuppressionState) and cached
    // alongside the nav-suppression state for that baseOptions instance. Returns null when the
    // resolver has no metadata for clrType at all (e.g. a non-object/primitive runtime type reached
    // through a base-typed navigation) — treated as "not visible" by the caller, which is always
    // safe (omission, never corruption).
    private static JsonTypeInfo? GetBaseTypeInfo(JsonSerializerOptions baseOptions, Type clrType)
    {
        NavSuppressionState state = s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState);
        return state.BaseTypeInfoByType.GetOrAdd(clrType, type => state.BaseResolver.GetTypeInfo(type, baseOptions));
    }

    // #337 correctness support: would System.Text.Json write polymorphic metadata (a type
    // discriminator) for an instance of <paramref name="clrType"/> when it is reached through an
    // `object`-declared slot — i.e. through the batched SerializeBoundedCollection call rather than
    // a per-element SerializeToNode(value, value.GetType(), ...)?
    //
    // Asks STJ, never the attributes. Polymorphism is only USUALLY declared with
    // [JsonPolymorphic]/[JsonDerivedType]; it can equally be configured by a custom
    // TypeInfoResolver or a JsonTypeInfo modifier, which attribute reflection would miss entirely
    // (and would then silently re-introduce the discriminator leak this guards against). The
    // resolved JsonTypeInfo.PolymorphismOptions is the single authority for both spellings, and it
    // is resolved through the SAME captured BaseResolver every other lookup in this file uses (see
    // IsNavVisibleInBaseOptions' remarks on why `opts.GetTypeInfo` is not safe here).
    //
    // Walks base classes AND interfaces because the discriminator comes from the nearest configured
    // ANCESTOR, not from clrType itself: for `A : Base` where Base carries the configuration, A's
    // own type info has no PolymorphismOptions at all.
    //
    // Memoized per (clrType, options) alongside the rest of the per-options state, so a collection
    // pays one dictionary lookup per distinct runtime type and the hierarchy walk happens once.
    private static bool EmitsPolymorphicMetadata(JsonSerializerOptions baseOptions, Type clrType)
    {
        NavSuppressionState state = s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState);
        if (state.PolymorphicByType.TryGetValue(clrType, out bool cached)) return cached;

        bool result = false;
        for (Type? t = clrType; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (GetBaseTypeInfo(baseOptions, t)?.PolymorphismOptions is not null) { result = true; break; }
        }
        // Interfaces are unordered and have no single "nearest" ancestor to walk, so unlike the base
        // chain above there is nothing to step through - the question is purely "does ANY of them
        // configure polymorphism", which Any states directly and short-circuits identically.
        result = result || clrType.GetInterfaces()
            .Any(iface => GetBaseTypeInfo(baseOptions, iface)?.PolymorphismOptions is not null);

        // Racing writers compute the same answer, so last-write-wins is safe here.
        state.PolymorphicByType[clrType] = result;
        return result;
    }

    // #338 (perf): the resolved key, memoized per (PropertyInfo, JsonSerializerOptions). Those two
    // ARE the full dependency set of the computation below — the [JsonPropertyName] rename is a
    // function of the property alone, and the fallback is a function of the options'
    // PropertyNamingPolicy alone — so the key is exactly as wide as the answer and no wider.
    // Keying on PropertyInfo alone would be WRONG: two registrations may carry different naming
    // policies (OhDataBuilder.WithJsonPropertyNamingPolicy is per-registration), and they would
    // collide on the un-renamed branch.
    //
    // Shaped as ConditionalWeakTable<options, ConcurrentDictionary<PropertyInfo, string>> rather
    // than one strong-keyed ConcurrentDictionary<(options, prop), string> for the same reason
    // s_navSuppressedOptionsCache is (see fold-in #7 there): a strong options key leaks an entry per
    // distinct JsonSerializerOptions for the life of the process, which a test suite that builds a
    // fresh WebApplicationFactory host per class hits hard. The inner PropertyInfo keys are
    // collected with the options entry that roots them.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<PropertyInfo, string>>
        s_navJsonKeyCache = new();

    // #184: resolve the JSON key a navigation property serializes to. A per-property
    // [System.Text.Json.Serialization.JsonPropertyName] rename wins (STJ emits it verbatim);
    // otherwise the naming policy converts the CLR name (and a null policy leaves it unchanged).
    //
    // #338 (perf): GetCustomAttribute is not cheap and this is a hot-path call — OmitUnexpandedNavigations
    // reaches it once per EDM navigation per JSON object (~3,000 times on a 1,000-row, 3-navigation
    // $expand), inside a pass its own header documents as a PRACTICAL no-op. Memoized here rather
    // than reordered around the keep/drop test, because BOTH branches of that test need the key
    // (the drop branch to obj.Remove it, the keep branch to index into it), so a reorder saves
    // nothing — see OmitUnexpandedNavigations.
    private static string ResolveNavigationJsonKey(
        string navClrName, PropertyInfo? clrNavProp, JsonSerializerOptions? serializerOptions)
    {
        // No CLR property (AdvancedConfigure EDM with no matching member): nothing stable to key a
        // cache entry on, and no attribute lookup to save — the naming-policy call is all there is.
        //
        // CALLER INVARIANT: when clrNavProp is non-null, navClrName IS clrNavProp.Name — every call
        // site passes `clrNavProp?.Name ?? <edm fallback>` or `prop.Name` directly. The cache below
        // therefore keys on the property alone and recomputes from prop.Name. A defensive
        // navClrName != clrNavProp.Name branch was tried here and removed: it is unreachable at all
        // five call sites, and an unexecutable branch on a hot path can never be validated.
        if (clrNavProp is null)
        {
            return serializerOptions?.PropertyNamingPolicy?.ConvertName(navClrName) ?? navClrName;
        }

        // A null options argument and _pascalCaseSerializerOptions produce identical answers (the
        // latter's PropertyNamingPolicy is null), so they can safely share one cache entry — the
        // same substitution every other method in this file makes for a null options argument.
        JsonSerializerOptions optionsKey = serializerOptions ?? _pascalCaseSerializerOptions;
        return s_navJsonKeyCache.GetOrCreateValue(optionsKey).GetOrAdd(
            clrNavProp,
            static (prop, opts) =>
            {
                JsonPropertyNameAttribute? rename = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (rename is not null) return rename.Name;
                return opts.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
            },
            optionsKey);
    }

    // #184: the CLR type carrying a navigation target's own properties — the element type for a
    // collection navigation (List<T>/T[]/IEnumerable<T>), or the property type itself for a
    // single-valued navigation — so nested [JsonPropertyName] resolution can recurse. Returns null
    // when the CLR property is unknown (e.g. AdvancedConfigure EDM with no matching CLR member).
    private static Type? NavElementClrType(PropertyInfo? clrNavProp)
    {
        if (clrNavProp is null) return null;
        Type navType = clrNavProp.PropertyType;
        if (navType == typeof(string)) return navType;
        if (navType.IsArray) return navType.GetElementType();
        foreach (Type iface in new[] { navType }
            .Concat(navType.GetInterfaces())
            .Where(iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            return iface.GetGenericArguments()[0];
        }
        return navType;
    }

    // Batch 4: Inject @odata.etag into a JsonArray using the original (pre-expand) items array
    // to compute each ETag. Per OData-JSON §4.5, annotations precede the properties they describe,
    // so rebuild each JsonObject with @odata.etag first.
    private static JsonArray InjectETagsIntoJsonArray(JsonArray json, object[] originalItems, IEntitySetEndpointSource source)
    {
        for (int i = 0; i < Math.Min(json.Count, originalItems.Length); i++)
        {
            if (json[i] is JsonObject obj)
            {
                string etag = source.InvokeGetETag(originalItems[i]);
                var reordered = new JsonObject { ["@odata.etag"] = JsonValue.Create($"\"{etag}\"") };
                foreach (var prop in obj.ToList())
                {
                    obj.Remove(prop.Key);
                    reordered[prop.Key] = prop.Value;
                }
                json[i] = reordered;
            }
        }
        return json;
    }

    // M3: returns the client's $select (+ $expand) property list, in request order and
    // de-duplicated, so both the Stage-4 body filter and the projected context URL
    // ("#Set(prop1,prop2)", JSON §10.7/§10.8) agree on exactly which properties were selected
    // and in what order. Ordinal-case as normalized by the Microsoft.OData parser (which
    // resolves $select identifiers to the EDM property name regardless of the casing the
    // client sent).
    private static List<string>? ExtractSelectedProperties(SelectExpandClause clause)
    {
        if (clause.AllSelected) return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var props = new List<string>();
        foreach (var item in clause.SelectedItems)
        {
            if (item is PathSelectItem psi)
            {
                string id = psi.SelectedPath.FirstSegment.Identifier;
                if (seen.Add(id)) props.Add(id);
            }
        }

        // When only $expand (no $select) is used, AllSelected is false but SelectedItems
        // has no PathSelectItems — only ExpandedNavigationSelectItems. An empty set would
        // strip every property in Stage 4, so treat this as "keep all".
        if (props.Count == 0) return null;

        // Preserve expanded nav properties so they survive Stage 4 when $select and
        // $expand are combined (e.g. $select=Name&$expand=Children keeps both).
        foreach (var ensi in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string id = ensi.PathToNavigationProperty.FirstSegment.Identifier;
            if (seen.Add(id)) props.Add(id);
        }

        return props;
    }

    /// <summary>
    /// #206: composes the <c>$select</c> member-init projection
    /// (<c>x =&gt; new TModel { A = x.A, ... }</c>) onto <paramref name="query"/> when the
    /// request is eligible, returning <paramref name="query"/> unchanged (full fetch — today's
    /// behavior) otherwise. The projection set is selected structural properties ∪ the entity
    /// key (always: <c>@odata.id</c>, expansion correlation, <c>$skiptoken</c>) ∪ the
    /// <c>UseETag</c> properties (so <c>@odata.etag</c> is identical with and without
    /// pushdown). The lambda is built per request and deliberately UNCACHED: <c>$select</c>
    /// combinations are client-controlled and unbounded, so a lambda cache keyed by select-set
    /// would be an unbounded-growth vector (#202 hardening ethos); LINQ providers' own query
    /// caches key structurally and absorb repeated shapes.
    /// <para>
    /// #206 phase 2: when <paramref name="expandNavs"/> is supplied (the $expand pushdown path),
    /// each pushed navigation is folded into the same member-init — a collection as
    /// <c>Nav = x.Nav[.Where(f)][.OrderBy(o)][.Skip(s)][.Take(t)].ToList()</c> (the nested
    /// $filter/$orderby/$top/$skip of the expand, bound by Microsoft's FilterBinder/OrderByBinder;
    /// see BuildShapedNavAccess), a single-valued reference as <c>Ref = x.Ref</c> — so one EF Core
    /// query loads the related rows via a JOIN. When <c>null</c> (the $select-only path) the
    /// projection is byte-for-byte what it was before. Ineligibility (no ctor / unknowable ETag
    /// names / complex or unsettable structural member, or a nested clause the binder cannot bind)
    /// returns <paramref name="query"/> unchanged; the caller detects that by reference and abandons
    /// expand pushdown for the request, so the folded navigations are never partially applied.
    /// </para>
    /// </summary>
    private static IQueryable<TModel> TryApplySelectProjection<TModel>(
        IQueryable<TModel> query,
        IReadOnlyList<string> selectedNames,
        IEntitySetEndpointSource source,
        bool hasParameterlessCtor,
        IReadOnlyDictionary<string, StructuralPropertyInfo> structuralByName,
        ILogger? logger,
        IReadOnlyList<EngagedExpand>? expandNavs = null,
        IEdmModel? edmModel = null,
        ODataQuerySettings? binderSettings = null,
        Action<string>? onIneligible = null)
    {
        if (!TryBuildProjectionInit<TModel>(
                selectedNames, source, hasParameterlessCtor, structuralByName, logger, expandNavs,
                edmModel, binderSettings, carrierCounted: null,
                out ParameterExpression px, out Expression pinit, out _, out string? reason))
        {
            // #322: the ONE consumer of the reason is the #305 400 below, which used to recite the
            // whole eligibility RULE ("a public parameterless constructor, settable non-complex
            // properties, ...") at a developer whose model satisfied all of it. Reported, never
            // reconstructed at the failure site — a re-derivation there would be a second copy of
            // these checks, free to drift from the ones that actually decided.
            if (reason is not null) onIneligible?.Invoke(reason);
            return query;
        }

        return query.Select(Expression.Lambda<Func<TModel, TModel>>(pinit, px));
    }

    /// <summary>
    /// #334: the shared core of the root member-init projection — the eligibility checks, the
    /// structural member binds and the folded <c>$expand</c> navigation binds — extracted verbatim
    /// from <see cref="TryApplySelectProjection"/> so the count-carrier projection
    /// (<see cref="TryApplyCarrierProjection"/>) reuses every one of them rather than duplicating
    /// the logic. Returns <c>false</c> for exactly the ineligibility cases that used to
    /// <c>return query</c> unchanged.
    /// <para>
    /// <paramref name="carrierCounted"/> names the top-level engaged expands whose
    /// <c>Nav@odata.count</c> is to be carried as an independent scalar subquery. Those navigations
    /// get their nested <c>$skip</c>/<c>$top</c> pushed to SQL (<c>countViaCarrier</c>) instead of
    /// the count bound, and one count expression each is emitted into <paramref name="countExprs"/>,
    /// index-aligned with that list. <c>null</c> (the ordinary path) changes nothing.
    /// </para>
    /// </summary>
    private static bool TryBuildProjectionInit<TModel>(
        IReadOnlyList<string> selectedNames,
        IEntitySetEndpointSource source,
        bool hasParameterlessCtor,
        IReadOnlyDictionary<string, StructuralPropertyInfo> structuralByName,
        ILogger? logger,
        IReadOnlyList<EngagedExpand>? expandNavs,
        IEdmModel? edmModel,
        ODataQuerySettings? binderSettings,
        IReadOnlyList<EngagedExpand>? carrierCounted,
        out ParameterExpression parameter,
        out Expression entityInit,
        out List<Expression?>? countExprs,
        out string? ineligibilityReason)
    {
        parameter = null!;
        entityInit = null!;
        countExprs = null;
        ineligibilityReason = null;

        if (!hasParameterlessCtor)
        {
            ineligibilityReason =
                $"'{typeof(TModel).Name}' has no public parameterless constructor (a positional record has none)";
            logger?.LogDebug(
                "OhData: $select pushdown skipped for {EntitySet}: {Model} has no public parameterless constructor.",
                source.EntitySetName, typeof(TModel).Name);
            return false;
        }

        // Selected names can include expanded-navigation identifiers (ExtractSelectedProperties
        // keeps them for the JSON trim); those are not structural and are skipped here —
        // expansion loads via delegates correlated by the always-projected key. Nested $select
        // paths ($select=address/city) arrive as their top-level identifier and project the
        // whole member; the JSON trim shapes the nested object.
        var members = new Dictionary<string, StructuralPropertyInfo>(StringComparer.Ordinal);
        foreach (StructuralPropertyInfo selectedProp in selectedNames
            .Where(structuralByName.ContainsKey)
            .Select(name => structuralByName[name]))
        {
            members[selectedProp.Name] = selectedProp;
        }

        foreach (StructuralPropertyInfo structural in structuralByName.Values
            .Where(p => p.IsKey))
        {
            members[structural.Name] = structural;
        }

        if (source.HasETag)
        {
            if (source.ETagPropertyNames is null)
            {
                ineligibilityReason =
                    "its UseETag selector is not a direct property selector, so the properties the " +
                    "ETag is computed from cannot be identified and projected";
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: UseETag selector property names are unknowable (non-direct selector).",
                    source.EntitySetName);
                return false;
            }

            foreach (string name in source.ETagPropertyNames)
            {
                // #253: structuralByName is keyed by the EDM name (which may be a [JsonPropertyName]
                // rename), but UseETag selector names are CLR property names — match on the CLR name.
                StructuralPropertyInfo? etagProp = structuralByName.Values
                    .FirstOrDefault(p => string.Equals(p.Property.Name, name, StringComparison.Ordinal));
                if (etagProp is null)
                {
                    ineligibilityReason =
                        $"the UseETag property '{name}' is not a structural property of " +
                        $"'{typeof(TModel).Name}'";
                    logger?.LogDebug(
                        "OhData: $select pushdown skipped for {EntitySet}: UseETag property '{Property}' is not a structural property.",
                        source.EntitySetName, name);
                    return false;
                }

                members[etagProp.Name] = etagProp;
            }
        }

        foreach (StructuralPropertyInfo member in members.Values)
        {
            // Complex-typed members are a phase-1 boundary: projecting an EF-owned complex
            // property under a TRACKING queryable throws inside EF ("owned entity without a
            // corresponding owner"), turning a working request into a 500. byte[] is classified
            // primitive (s_primitiveClrTypes), so rowversion ETag inputs keep pushdown.
            if (member.IsComplex)
            {
                ineligibilityReason =
                    $"its structural property '{member.Name}' is complex-typed " +
                    $"({member.Property.PropertyType.Name}), and projecting an EF-owned complex " +
                    "property under a tracking query is not supported";
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: '{Property}' is complex-typed (owned-entity projection is a phase-1 boundary).",
                    source.EntitySetName, member.Name);
                return false;
            }

            if (member.Property.SetMethod is not { IsPublic: true })
            {
                ineligibilityReason =
                    $"its structural property '{member.Name}' has no public setter";
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: '{Property}' has no public setter.",
                    source.EntitySetName, member.Name);
                return false;
            }
        }

        ParameterExpression x = Expression.Parameter(typeof(TModel), "x");
        var bindings = members.Values
            .Select(m => (MemberBinding)Expression.Bind(m.Property, Expression.Property(x, m.Property)))
            .ToList();

        // #206 phase 2: fold each pushed $expand navigation into the same member-init so the LINQ
        // provider loads the related rows as part of this one query (EF Core translates a collection
        // navigation projected with .ToList() into a JOIN, and a single-valued navigation into an
        // outer join). Nested $filter/$orderby/$top/$skip become a filtered/ordered/paged Include via
        // BuildShapedNavAccess. Eligibility of each binding — settable property, non-cyclic related
        // type, List-assignable collection — was decided at startup in BuildExpandNavBinding.
        if (expandNavs is { Count: > 0 })
        {
            try
            {
                foreach (EngagedExpand nav in expandNavs)
                {
                    // #334: is this one of the navs whose count the carrier supplies as an
                    // independent scalar subquery? Matched on the CLR PropertyInfo, which is the
                    // identity BuildExpandNavBinding assigns once at startup.
                    int carrierIndex = -1;
                    if (carrierCounted is not null)
                    {
                        for (int ci = 0; ci < carrierCounted.Count; ci++)
                        {
                            if (ReferenceEquals(carrierCounted[ci].Binding.Property, nav.Binding.Property))
                            {
                                carrierIndex = ci;
                                break;
                            }
                        }
                    }

                    // #254: the ROOT entity set's resolved MaxExpandTop governs at every depth (the
                    // same rule MaxExpansionDepth follows), so it is read from `source` once here.
                    Expression access = BuildShapedNavAccess(
                        x, nav, (IEdmModel)edmModel!, (ODataQuerySettings)binderSettings!, source.MaxExpandTop,
                        countViaCarrier: carrierIndex >= 0);
                    bindings.Add(Expression.Bind(nav.Binding.Property, access));

                    if (carrierIndex >= 0)
                    {
                        // #334: the count is a SECOND, INDEPENDENT expression rooted at the same
                        // navigation-access node — filtered but never ordered or windowed — mirroring
                        // the CreateTotalCountExpression / ProjectAsWrapper split
                        // Microsoft.AspNetCore.OData's SelectExpandBinder.BuildExpandedProperty makes.
                        // Because neither chain reads the other, $count=true no longer perturbs the
                        // $top translation.
                        (countExprs ??= new List<Expression?>(new Expression?[carrierCounted!.Count]))
                            [carrierIndex] = BuildNavCountExpression(
                                x, nav, (IEdmModel)edmModel!, (ODataQuerySettings)binderSettings!);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or Microsoft.OData.ODataException)
            {
                // FAIL LOUD (owner directive, post-#298/#300 review): a nested expand option
                // Microsoft's own binder cannot translate into an expression must not silently
                // degrade the response to EDM-only under a 200 — that is exactly the class of bug
                // #298/#300 reported (the parent collection itself came back wrong/empty because a
                // downstream shape could not be translated). The client asked for a $expand nested
                // option this server cannot honor against this model; that is a 400, not a
                // silently-wrong 200. Logged at Debug (same diagnostic detail as before) so the
                // operator can still see which clause tripped it; the client-facing message stays
                // generic (never ex.Message/stack trace, which could leak internal details) per this
                // file's existing InternalServerError convention (S7).
                //
                // Narrowed (adversarial review, post-branch): this step is pure in-memory expression
                // construction via Microsoft's FilterBinder/OrderByBinder (see BindNavShape) — no
                // database I/O happens here, so there is no transient/provider fault class to worry
                // about. The filter is narrowed so a genuine framework bug (e.g. a
                // NullReferenceException from a binder defect) surfaces as a 500 for investigation
                // rather than being mislabeled a 400 "your query is bad" response.
                //
                // #494 deliberately left this one alone. The execution-time catches it used to be
                // "consistent with" were classifying by exception TYPE around a call that really
                // does touch the database, which is what made their type list wrong; this call
                // touches nothing, so the same list is sound here. In TranslateThenMaterialize's
                // terms, everything below is translation and there is no materialization phase to
                // separate it from.
                logger?.LogDebug(ex,
                    "OhData: $expand pushdown failed for {EntitySet}: a nested expand option could not be bound.",
                    source.EntitySetName);
                throw new Microsoft.OData.ODataException(
                    $"The '$expand' on '{source.EntitySetName}' could not be processed: a nested expand " +
                    "option could not be translated. Simplify the nested $filter/$orderby, or write an " +
                    "expand delegate for this navigation to take full control of its query shape.");
            }
        }

        parameter = x;
        entityInit = Expression.MemberInit(Expression.New(typeof(TModel)), bindings);
        return true;
    }

    /// <summary>
    /// #334: the count leg — <c>owner.Nav[.Where(f)].Count()</c>. Rooted at the SAME
    /// navigation-access node the windowed page is built from, but with NO OrderBy/Skip/Take, so it
    /// translates as a plain correlated scalar aggregate rather than a collection projected out of a
    /// windowed set (the APPLY/LATERAL shape #298/#300/#304 established SQLite cannot translate).
    /// <para>
    /// The nested <c>$filter</c> rides into the count — and only the <c>$filter</c> — because OData
    /// §11.2.4.2 defines <c>Nav@odata.count</c> as the size of the FULL filtered collection, not of
    /// the returned page.
    /// </para>
    /// </summary>
    private static Expression BuildNavCountExpression(
        Expression owner, EngagedExpand engaged, IEdmModel model, ODataQuerySettings binderSettings)
    {
        Type elem = engaged.Binding.ElementType;
        Expression access = Expression.Property(owner, engaged.Binding.Property);
        NavShapeBindings bound = BindNavShape(engaged, elem, model, binderSettings);
        if (bound.Predicate is not null)
            access = Expression.Call(_enumerableWhere.MakeGenericMethod(elem), access, bound.Predicate);
        return Expression.Call(_enumerableCount.MakeGenericMethod(elem), access);
    }

    /// <summary>
    /// #334: the count-carrier projection —
    /// <c>new ExpandCountCarrier&lt;TModel&gt; { Entity = new TModel { … }, C0 = x.Nav.Where(f).Count(), … }</c>
    /// — so ONE query returns both the SQL-windowed page and an exact per-parent
    /// <c>Nav@odata.count</c>. Returns <c>null</c> when the root projection is ineligible (the same
    /// conditions <see cref="TryApplySelectProjection"/> bails on) or when a count expression could
    /// not be produced for every requested nav, in which case the caller falls back to the ordinary
    /// projection and the count-defers-paging behaviour of #254/#298/#304.
    /// </summary>
    private static IQueryable<ExpandCountCarrier<TModel>>? TryApplyCarrierProjection<TModel>(
        IQueryable<TModel> query,
        IReadOnlyList<string> selectedNames,
        IEntitySetEndpointSource source,
        bool hasParameterlessCtor,
        IReadOnlyDictionary<string, StructuralPropertyInfo> structuralByName,
        ILogger? logger,
        IReadOnlyList<EngagedExpand> expandNavs,
        IEdmModel edmModel,
        ODataQuerySettings binderSettings,
        IReadOnlyList<EngagedExpand> carrierCounted)
    {
        if (!TryBuildProjectionInit<TModel>(
                selectedNames, source, hasParameterlessCtor, structuralByName, logger, expandNavs,
                edmModel, binderSettings, carrierCounted,
                out ParameterExpression x, out Expression entityInit, out List<Expression?>? countExprs,
                out _))
        {
            // No reason is surfaced here: a null return falls through to the ordinary projection
            // below, which re-runs the same checks and reports the reason to ITS caller.
            return null;
        }

        if (countExprs is null || countExprs.Count != carrierCounted.Count ||
            countExprs.Any(static c => c is null))
        {
            return null;
        }

        Type carrierType = typeof(ExpandCountCarrier<TModel>);
        var carrierBindings = new List<MemberBinding>(countExprs.Count + 1)
        {
            Expression.Bind(carrierType.GetProperty(nameof(ExpandCountCarrier<TModel>.Entity))!, entityInit),
        };
        for (int i = 0; i < countExprs.Count; i++)
            carrierBindings.Add(Expression.Bind(carrierType.GetProperty($"C{i}")!, countExprs[i]!));

        return query.Select(Expression.Lambda<Func<TModel, ExpandCountCarrier<TModel>>>(
            Expression.MemberInit(Expression.New(carrierType), carrierBindings), x));
    }

    /// <summary>
    /// #334: the projection slot for a nested <c>$count</c>. The root projection normally emits
    /// <c>new TModel { … }</c>, which has nowhere to put a count scalar — that absence is precisely
    /// why <c>$count=true</c> used to have to suppress the <c>$top</c> SQL bound and count the
    /// materialized array instead. (Microsoft.AspNetCore.OData has the slot already: its
    /// <c>SelectExpandWrapper</c>'s <c>PropertyContainer</c> carries <c>Collection</c> and
    /// <c>TotalCount</c> side by side.)
    /// <para>
    /// FIXED SLOTS, not an array or a List: a member-init of settable scalar members is what every
    /// LINQ provider can translate. <see cref="ExpandCountCarrierSlots"/> covers any realistic
    /// number of counted, windowed, top-level navigations in one request; a request that exceeds it
    /// simply falls back to the pre-#334 path rather than failing.
    /// </para>
    /// <para>
    /// The carrier NEVER reaches the serializer: the collection route unwraps it to
    /// <c>TModel[]</c> immediately after <c>ToArray()</c>, so nothing in the JSON shaping pipeline
    /// (SerializeBounded / SerializeBoundedCollection / SpliceKeptNavigations /
    /// OmitUnexpandedNavigations / StripToSelectedProperties) ever sees a wrapper type.
    /// </para>
    /// </summary>
    internal sealed class ExpandCountCarrier<T>
    {
        public T Entity { get; set; } = default!;
        public int C0 { get; set; }
        public int C1 { get; set; }
        public int C2 { get; set; }
        public int C3 { get; set; }
        public int C4 { get; set; }
        public int C5 { get; set; }
        public int C6 { get; set; }
        public int C7 { get; set; }

        public int Slot(int i) => i switch
        {
            0 => C0,
            1 => C1,
            2 => C2,
            3 => C3,
            4 => C4,
            5 => C5,
            6 => C6,
            7 => C7,
            _ => throw new ArgumentOutOfRangeException(nameof(i)),
        };
    }

    /// <summary>#334: how many counted navs one carrier projection can hold. See <see cref="ExpandCountCarrier{T}"/>.</summary>
    internal const int ExpandCountCarrierSlots = 8;

    // #206 phase 2 (Option A1): a navigation the $expand pushdown folds into the collection
    // projection — the CLR property to bind, whether it is a collection (materialized with
    // .ToList() so EF Core emits the JOIN) or a single-valued reference, and the related element
    // type. Built once at startup for each DELEGATE-LESS navigation that survives the safety
    // checks (see BuildExpandNavBinding); delegate-backed navigations never appear here.
    private readonly record struct ExpandNavBinding(PropertyInfo Property, bool IsCollection, Type ElementType);

    // #206 phase 2: cached open generic Enumerable.ToList<T>, closed per collection-navigation binding.
    private static readonly MethodInfo _enumerableToList =
        typeof(Enumerable).GetMethod(nameof(Enumerable.ToList), BindingFlags.Public | BindingFlags.Static)!;

    // #206 phase 2 (optioned expand): cached open-generic Enumerable operators used to fold a
    // filtered / ordered / paged Include into the collection projection. The nested $filter/$orderby/
    // $top/$skip of a $expand are pushed to SQL by composing these onto the navigation access
    // (x.Nav.Where(f).OrderBy(o).Skip(s).Take(t).ToList()); EF Core translates the result to a single
    // JOIN with a ROW_NUMBER window for paging. The Where/OrderBy predicates are produced by
    // Microsoft's own OData binders (FilterBinder/OrderByBinder), never a hand-rolled translator.
    private static readonly MethodInfo _enumerableWhere = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Where) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);
    private static readonly MethodInfo _enumerableOrderBy = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.OrderBy) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableOrderByDescending = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.OrderByDescending) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableThenBy = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.ThenBy) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableThenByDescending = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.ThenByDescending) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableSkip = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Skip) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(int));
    private static readonly MethodInfo _enumerableTake = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Take) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(int));
    // #206 phase 2 (multi-level expand): Enumerable.Select<TSource,TResult>(source, selector) — the
    // element-wise projection folded into a JOIN'd collection when a nested $expand (or $levels)
    // recurses one level deeper. EF Core translates a collection navigation projected element-wise
    // with .ToList() into a ThenInclude-style JOIN, so the whole delegate-less chain loads in one query.
    private static readonly MethodInfo _enumerableSelect = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Select) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

    // #334: Enumerable.Count<T>(IEnumerable<T>) — the PARAMETERLESS overload. Composed on the
    // filtered but UN-WINDOWED navigation access to obtain an exact Nav@odata.count as a correlated
    // scalar aggregate, independent of the Skip/Take window composed on the same nav for the page.
    private static readonly MethodInfo _enumerableCount = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1);

    // #206 phase 2 (optioned expand): the OData filter/orderby binders are stateless — all per-bind
    // state flows through the QueryBinderContext argument — so a single shared instance is reused
    // across requests (matching this file's cache-the-reflection-machinery ethos).
    private static readonly FilterBinder _filterBinder = new();
    private static readonly OrderByBinder _orderByBinder = new();

    // #206 phase 2 (optioned + multi-level expand): one delegate-less navigation the request
    // $expand'd, resolved for pushdown. Carries the startup binding plus the request's parsed nested
    // clauses. Filter/OrderBy/Skip/Top are pushed to SQL via BuildShapedNavAccess; Count and
    // NestedSelect are applied afterward on the serialized JSON (ShapePushedExpandsInJson) so the
    // wire stays a plain POCO in the configured naming policy (PascalCase by default) — no
    // SelectExpandWrapper ever reaches the serializer. When
    // Count is requested, Skip/Top are DEFERRED to the JSON pass instead of SQL so the emitted
    // Nav@odata.count reflects the full filtered collection (OData §11.2.4.2), not the page.
    // <para>#206 (recursion): <c>Children</c> holds each pushed nested $expand one level deeper —
    // folded into the same JOIN'd query as an element-wise projection (EF ThenInclude). A branch is
    // only recorded here when it is delegate-less AND pushable AT EVERY level; a delegate-backed (or
    // otherwise non-pushable) nested nav defers the whole parent off pushdown (see
    // TryBuildEngagedExpand), so a pushed branch can never EF-include a delegate navigation — the
    // delegate-safety invariant holds at any depth by construction. <c>Levels</c> (&gt; 0) marks a
    // <c>$levels=N</c> self-referential expand recursed N deep against the same <c>Binding</c>;
    // <c>Children</c> is then null (the recursion re-uses this binding), while
    // Filter/OrderBy/Skip/Top/Count/NestedSelect — when present (#254) — apply at EVERY level.</para>
    private readonly record struct EngagedExpand(
        ExpandNavBinding Binding,
        FilterClause? Filter,
        OrderByClause? OrderBy,
        int? Skip,
        int? Top,
        bool Count,
        List<string>? NestedSelect,
        IReadOnlyList<EngagedExpand>? Children,
        int Levels);

    // #206 phase 2 (optioned + multi-level expand): resolve a $expand item that targets a
    // delegate-less, pushdown-eligible navigation into an EngagedExpand (recursing into its own
    // nested $expand), or return false to DEFER the whole branch off the pushdown path (it then stays
    // EDM-only for the request, exactly as before). Deferred cases: $search/$compute/$apply
    // (unsupported inside a pushed expand); a nested $expand whose child is delegate-backed, cyclic,
    // or a non-member-init-projectable type (the parent is deferred wholesale so a pushed branch is
    // delegate-less AND projectable end-to-end); an intermediate level whose element type cannot be
    // member-init-projected. The nested options $filter/$orderby/$top/$skip/$count/$select are honored
    // at every level. $levels is handled by the caller via BuildLevelsNavBinding; #254 lets a $levels
    // item carry $filter/$orderby/$skip/$top/$count/$select (applied at every level of the recursion),
    // while a $levels item carrying its own nested $expand is still deferred.
    // <paramref name="remainingDepth"/> is the resolved MaxExpansionDepth budget for the whole chain
    // (Microsoft's SelectExpandQueryValidator already 400s a request nesting deeper, so this is a
    // belt-and-suspenders cap that never partially applies a too-deep graph).
    private static bool TryBuildEngagedExpand(
        ExpandedNavigationSelectItem item, ExpandNavBinding binding, IEdmModel model,
        OhDataRegistration registration, int remainingDepth, out EngagedExpand engaged)
    {
        engaged = default;
        if (remainingDepth < 1) return false;
        if (item.SearchOption is not null || item.ComputeOption is not null || item.ApplyOption is not null)
        {
            return false; // $search/$compute/$apply inside an expand — not implemented on the pushdown path
        }

        // $levels: bounded self-referential recursion (the caller resolved the self-referential binding
        // via BuildLevelsNavBinding).
        //
        // #254 (item 2): the recursion now carries the item's other nested options —
        // $filter/$orderby/$skip/$top/$count/$select — applied at EVERY level, matching what ODL itself
        // does (SelectExpandQueryOption.ProcessLevels rewrites $levels=N into N nested expand items each
        // holding the SAME Filter/OrderBy/Top/Skip/Count and the same nested $select clause).
        //
        // STILL DEFERRED: a $levels item that also carries its own nested $expand. Depth accounting
        // between the $levels budget and the nested branch's own remainingDepth is ambiguous against
        // MaxExpansionDepth, so the whole branch stays EDM-only (graceful, never a 500).
        //
        // Micro-decision (A) (owner-settled, FROZEN spec on issue #293): this $levels branch NEVER
        // calls ResolveNavTreatment / ResolveProfilesForClrType — it recurses the SAME already-resolved
        // `binding` (the URL-named root set's own self-referential nav) at every level, exactly as
        // BuildLevelsNavBinding produced it. So `GET /Base?$expand=Children($levels=2)` through a
        // self-referential nav resolves ENTIRELY from the URL-named set and serves raw at every level,
        // even when the same CLR/EDM type is ALSO exposed (with disagreeing nav config) by another
        // entity set. By contrast, the explicit nested form below — `$expand=Children($expand=Children)`
        // — descends one real ExpandedNavigationSelectItem per level, so EACH level re-resolves its own
        // candidate set via ResolveProfilesForClrType/ResolveNavTreatment; if that type is exposed by
        // MULTIPLE disagreeing sets, the grandchild's treatment is Blank (candidate disagreement) even
        // though $levels would have served it raw.
        //
        // #318, CORRECTED: this comment used to stop at "the grandchild BLANKS", which UNDERSTATES the
        // outcome by a whole level and is measurably wrong. A non-ServeRaw child makes the childItems
        // loop below `return false`, which defers the WHOLE PARENT BRANCH off pushdown; the parent
        // level is then never loaded, and ExpandLevelAsync's ServeRaw branch is a no-op over it, so the
        // PARENT navigation comes back empty too. MEASURED against the LvNodes/LvSecureNodes fixture
        // (one LvNode type, two entity sets disagreeing on Children):
        //
        //   ?$expand=Children             -> 200  Children:[A, B]                 (root serves)
        //   ?$expand=Children($levels=2)  -> 200  Children:[A[A1,A2,A3], B[B1]]   (both levels serve)
        //   ?$expand=Children($expand=Children)
        //                                 -> 200  Children:[]                     (BOTH levels lost)
        //
        // Both halves of that asymmetry are individually owner-settled on #293 and neither is a bug:
        // micro-decision (A) ships the fail-closed Blank for the explicit nested form, and
        // micro-decision (B) ("delegate-less pushable parent empties whole branch vs delegate-backed
        // parent blanks only child: both leak-safe, DEFER PARITY") is exactly the extra level lost
        // here. #318 tracks the optional parent-set provenance threading that would unify the explicit
        // form with $levels under "serve raw"; it is a widening on a delegate-safety boundary, so it
        // must not be done as a drive-by. Do NOT "fix" the inconsistency in the other direction by
        // making $levels blank — the FROZEN spec lists the whole $levels suite under "tests that STAY
        // GREEN (confirm, don't gut)", and $levels resolving from the URL-named set alone is the
        // decision, not an oversight. Pinned end-to-end by Issue318LevelsVsExplicitNestedSelfExpandTests.
        if (item.LevelsOption is not null)
        {
            SelectExpandClause? lc = item.SelectAndExpand;
            if (lc is not null && lc.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any())
                return false; // $levels + nested $expand — deferred

            int levels = ResolveLevelsBudget(
                item.LevelsOption.IsMaxLevel, item.LevelsOption.Level, remainingDepth, MaxNestedExpandDepth);
            if (levels < 1) return false;
            if (!IsMemberInitProjectable(binding.ElementType, model)) return false;

            int? levelsSkip = item.SkipOption is long lsk ? (int)Math.Min(lsk, int.MaxValue) : null;
            int? levelsTop = item.TopOption is long ltp ? (int)Math.Min(ltp, int.MaxValue) : null;
            List<string>? levelsSelect = lc is not null ? ExtractSelectedProperties((SelectExpandClause)lc) : null;
            if (levelsSelect is not null)
            {
                // THE $levels + $select TRAP: the recursion is IMPLICIT in ODL — the nested clause of a
                // $levels item holds NO ExpandedNavigationSelectItem for the self-navigation — so
                // ExtractSelectedProperties never sees it and the strip would delete the self-nav key
                // (and its Nav@odata.count) at every level. Append the self-nav's EDM name so
                // StripToSelectedProperties/KeepUnderSelect keep both. Resolved through
                // ODataPropertyNaming.ResolveEdmName (never the raw CLR name) so a [JsonPropertyName]-
                // renamed self-navigation keeps working — the same call CollectPushedLevelsNavNames uses.
                string selfNavEdmName = ODataPropertyNaming.ResolveEdmName(binding.Property);
                if (!levelsSelect.Contains(selfNavEdmName, StringComparer.OrdinalIgnoreCase))
                    levelsSelect.Add(selfNavEdmName);
            }

            engaged = new EngagedExpand(
                binding, item.FilterOption, item.OrderByOption, levelsSkip, levelsTop,
                item.CountOption == true, levelsSelect, Children: null, Levels: levels);
            return true;
        }

        SelectExpandClause? nested = item.SelectAndExpand;
        List<EngagedExpand>? children = null;
        var childItems = nested?.SelectedItems.OfType<ExpandedNavigationSelectItem>().ToList();
        if (childItems is { Count: > 0 })
        {
            // An intermediate level (one with its own nested $expand) is projected element-wise into a
            // fresh member-init so the deeper navigations fold in. That requires the element type to be
            // member-init-projectable; otherwise defer the whole branch (stays EDM-only, never a 500).
            if (remainingDepth < 2) return false;
            if (!IsMemberInitProjectable(binding.ElementType, model)) return false;

            // Model B pushdown gate (FROZEN spec, issue #293): resolve the candidate set for this
            // element CLR type — every startup profile whose entity set's EDM type is EXACTLY this
            // type (ResolveProfilesForClrType; never CLR-type assignability, never registration
            // order) — and use the SAME ResolveNavTreatment dispatch the delegate expansion path
            // uses (ExpandLevelAsync) to decide each nested nav's treatment. A nav only stays
            // EF-includable (pushed down) when its treatment is ServeRaw (DB(nav) = ∅ over the
            // candidate set); RunDelegate and Blank both defer the WHOLE parent branch off pushdown
            // so it resolves via the delegate expansion path instead, which alone knows how to invoke
            // the sole delegate or write the blanked value. Computing the gate's candidate set with
            // the exact same helper the delegate path uses is what guarantees the two can never
            // diverge on the same navigation.
            IReadOnlyList<IEntitySetEndpointSource> childCandidates =
                ResolveProfilesForClrType(binding.ElementType, model, registration);

            foreach (ExpandedNavigationSelectItem childItem in childItems)
            {
                string childNavName = childItem.PathToNavigationProperty.FirstSegment.Identifier;
                if (ResolveNavTreatment(childNavName, childCandidates).Treatment != NavTreatment.ServeRaw)
                    return false; // RunDelegate or Blank — defer whole branch (never EF-included)

                if (BuildExpandNavBinding(binding.ElementType, childNavName, model) is not { } childBinding)
                    return false; // cyclic / non-projectable nested nav — defer whole branch

                if (!TryBuildEngagedExpand(childItem, childBinding, model, registration, remainingDepth - 1, out EngagedExpand childEngaged))
                    return false; // deeper level not pushable — defer whole branch

                (children ??= new List<EngagedExpand>()).Add(childEngaged);
            }
        }

        // Filter/OrderBy/Top/Skip/Count are only valid on a collection-valued expand; the OData parser
        // rejects them on a single-valued reference, so they arrive null there and this stays a bare
        // single-valued include (BuildShapedNavAccess returns x.Ref unchanged) carrying only $select.
        int? skip = item.SkipOption is long s ? (int)Math.Min(s, int.MaxValue) : null;
        int? top = item.TopOption is long t ? (int)Math.Min(t, int.MaxValue) : null;
        List<string>? nestedSelect = nested is not null ? ExtractSelectedProperties((SelectExpandClause)nested) : null;

        engaged = new EngagedExpand(
            binding, item.FilterOption, item.OrderByOption, skip, top, item.CountOption == true, nestedSelect,
            children, Levels: 0);
        return true;
    }

    // #206 phase 2 (multi-level expand): true when an element type can be projected into a fresh
    // member-init at an INTERMEDIATE expand level (i.e. one that folds deeper navigations). Requires a
    // public parameterless constructor and every scalar structural property (per the EDM) to be a
    // public-settable CLR property that is not complex-typed — projecting an EF-owned complex property
    // under a tracking queryable throws (the same phase-1 boundary TryApplySelectProjection guards). A
    // type that fails this defers its parent branch off pushdown (stays EDM-only), never a 500.
    //
    // #323 fold-in (review): memoized — as of #323 (Change A/B) this runs per request, per engaged
    // expand, per level, and does GetConstructor + FindDeclaredType + per-property reflection each time,
    // for a result that is invariant for a given (elementType, model) pair for the lifetime of that EDM
    // model. Cached per this file's existing static-cache convention (e.g. s_efIncludeMethodCache);
    // reference equality on IEdmModel is correct here since the same model instance is reused for every
    // request within one registration, and different registrations never share a model instance.
    private static readonly ConcurrentDictionary<(Type ElementType, IEdmModel Model), bool>
        s_memberInitProjectableCache = new();

    private static bool IsMemberInitProjectable(Type elementType, IEdmModel model) =>
        s_memberInitProjectableCache.GetOrAdd((elementType, model), static key =>
        {
            (Type elementType, IEdmModel model) = key;
            if (elementType.GetConstructor(Type.EmptyTypes) is null) return false;
            // #508: EdmClrTypeMap, not model.FindDeclaredType(elementType.FullName) — the latter
            // matches on the EDM type's full name, so on a renamed schema it answered null for every
            // element type, this method answered false for every element type, and TryBuildEngagedExpand
            // deferred EVERY $expand branch off pushdown. See EdmClrTypeMap.
            if (EdmClrTypeMap.FindEntityType(model, elementType) is not { } edmType)
            {
                return false;
            }

            foreach (IEdmStructuralProperty sp in edmType.StructuralProperties())
            {
                if (sp.Type.Definition is IEdmComplexType) return false; // owned-entity projection boundary
                // #253: sp.Name is the EDM name, which may be a [JsonPropertyName] rename — resolve back
                // to the CLR property by EDM name (falls back to a plain CLR-name match for un-renamed).
                PropertyInfo? clrProp = ODataPropertyNaming.FindClrPropertyByEdmName(elementType, sp.Name);
                if (clrProp is null || clrProp.SetMethod is not { IsPublic: true }) return false;
            }
            return true;
        });

    // #206 phase 2 (multi-level expand): the public-settable, non-complex scalar structural CLR
    // properties of <paramref name="elementType"/> (per the EDM), bound as <c>n.Prop</c> into an
    // intermediate level's fresh member-init. Callers gate on IsMemberInitProjectable first, so every
    // returned property is guaranteed settable and present.
    private static IEnumerable<PropertyInfo> ScalarStructuralClrProps(Type elementType, IEdmModel model)
    {
        // #508: EdmClrTypeMap, not model.FindDeclaredType(elementType.FullName). Callers gate on
        // IsMemberInitProjectable, which resolves through the same lookup — so the two must never be
        // able to disagree about which EDM type backs the element type.
        if (EdmClrTypeMap.FindEntityType(model, elementType) is not { } edmType)
            yield break;
        foreach (IEdmStructuralProperty sp in edmType.StructuralProperties()
            .Where(sp => sp.Type.Definition is not IEdmComplexType))
        {
            // #253: sp.Name is the EDM name (possibly a [JsonPropertyName] rename) — resolve to CLR.
            PropertyInfo? clrProp = ODataPropertyNaming.FindClrPropertyByEdmName(elementType, sp.Name);
            if (clrProp is { SetMethod.IsPublic: true }) yield return clrProp;
        }
    }

    // #206 phase 2 (optioned + multi-level expand): build the navigation access expression folded into
    // the collection projection for one engaged expand, relative to <paramref name="owner"/> (the
    // top-level query parameter, or a deeper element parameter when recursing). For a collection nav
    // this is owner.Nav.Where(filter).OrderBy/ThenBy(key…).Skip(s).Take(t)[.Select(memberInit)].ToList()
    // — each stage present only when the request carried it, and the .Select present only when a nested
    // $expand folds deeper navigations (EF ThenInclude). The Where/OrderBy lambdas come from Microsoft's
    // FilterBinder/OrderByBinder (bound against the nav element type), so nested $filter/$orderby
    // translate with the exact OData semantics the top-level collection path uses — no bespoke
    // OData→LINQ translator. Skip/Take are omitted here when $count is requested (the JSON pass pages
    // after counting). A single-valued reference has no collection operators; it is projected into a
    // null-guarded member-init whenever its element type is member-init-projectable (#323) — LEAF
    // expands included, not only ones with deeper nested navigations — and returned unchanged (bare)
    // only when the element type is not projectable. A $levels expand delegates to BuildLevelsNavAccess
    // (bounded self-referential recursion). Runs inside the caller's try/catch: a binder that cannot
    // bind a clause throws, and the caller then abandons pushdown for the request (the nav stays
    // EDM-only) rather than surfacing a 500.
    // #334: <paramref name="countViaCarrier"/> is set by TryBuildProjectionInit for the top-level
    // counted navs whose Nav@odata.count the carrier projection supplies as an independent scalar
    // subquery. It only ever reaches this level's ApplyNavShape — never a nested BuildMemberInit
    // call below — so a counted nav at depth >= 2 keeps the pre-#334 path by construction.
    private static Expression BuildShapedNavAccess(
        Expression owner, EngagedExpand engaged, IEdmModel model, ODataQuerySettings binderSettings,
        int? maxExpandTop, bool countViaCarrier = false)
    {
        ExpandNavBinding nav = engaged.Binding;

        if (engaged.Levels > 0)
        {
            // #254 (item 2): a $levels expand may now carry $filter/$orderby/$skip/$top/$count. Bind
            // the Where/OrderBy lambdas ONCE here and reuse them at every level of the recursion — the
            // nav element type is invariant under $levels (BuildLevelsNavBinding requires
            // elementType == ownerType) and expression trees are immutable, so re-binding per level
            // would allocate identical nodes for no benefit.
            NavShapeBindings levelsBound = BindNavShape(engaged, nav.ElementType, model, binderSettings);
            return BuildLevelsNavAccess(owner, engaged, engaged.Levels, model, levelsBound);
        }

        Expression access = Expression.Property(owner, nav.Property);
        Type elem = nav.ElementType;

        if (!nav.IsCollection)
        {
            // Single-valued reference. #323 (Change A): project it into a fresh member-init
            // (null-guarded so a missing reference stays null) whenever the element type is
            // member-init-projectable — a LEAF expand (no nested children) included, not only one
            // that carries deeper pushed navigations. BuildMemberInit handles an empty child list.
            // Materializing every leaf through a fresh POCO rather than the bare EF-tracked entity is
            // what makes a serialization cycle structurally impossible on this path (no entity with a
            // live back-reference navigation is ever handed to the serializer). When the element type
            // is NOT projectable, fall back to the bare reference (EF outer join loads the full
            // related entity) exactly as before.
            if (IsMemberInitProjectable(elem, model))
            {
                Expression init = BuildMemberInit(
                    access, elem, engaged.Children ?? Array.Empty<EngagedExpand>(), model, binderSettings,
                    maxExpandTop);
                return Expression.Condition(
                    Expression.Equal(access, Expression.Constant(null, elem)),
                    Expression.Constant(null, elem), init);
            }
            return access;
        }

        access = ApplyNavShape(
            access, engaged, elem, model, BindNavShape(engaged, elem, model, binderSettings), maxExpandTop,
            countViaCarrier: countViaCarrier);

        // #323 (Change A): fold EVERY element-wise projection — leaf or intermediate — into the query
        // whenever the element type is member-init-projectable, not only when a nested $expand folds
        // deeper delegate-less navigations in (EF ThenInclude). Same structural-cycle-impossibility
        // rationale as the single-valued branch above: a leaf collection is now a List<T> of fresh
        // POCOs, never the bare EF-tracked related entities, so a bidirectional back-reference can no
        // longer close a parent<->child object cycle for System.Text.Json. BuildMemberInit handles an
        // empty child list. When the element type is NOT projectable, this stays the bare
        // .ToList() of full related entities (all columns materialized) exactly as before.
        if (IsMemberInitProjectable(elem, model))
        {
            ParameterExpression n = Expression.Parameter(elem, "n");
            LambdaExpression proj = Expression.Lambda(
                BuildMemberInit(
                    n, elem, engaged.Children ?? Array.Empty<EngagedExpand>(), model, binderSettings,
                    maxExpandTop),
                n);
            access = Expression.Call(_enumerableSelect.MakeGenericMethod(elem, elem), access, proj);
        }

        return Expression.Call(_enumerableToList.MakeGenericMethod(elem), access);
    }

    // #254 (item 2): the OData-bound lambdas for one engaged expand's nested $filter/$orderby, split
    // out of the shaping step (BindNavShape → ApplyNavShape) so the $levels recursion can bind ONCE
    // and apply at every level. Null members mean "the request carried no such clause".
    private readonly record struct NavShapeBindings(
        LambdaExpression? Predicate,
        IReadOnlyList<(LambdaExpression Key, bool Descending)>? OrderBy);

    // #206 phase 2 (optioned expand) / #254: bind a collection expand's nested $filter/$orderby with
    // Microsoft's own FilterBinder/OrderByBinder. A fresh QueryBinderContext per bind: it holds the
    // binder's `$it` lambda parameter and other per-clause state, so filter and orderby each get their
    // own rather than sharing one. Throws (via the binders) on a clause that cannot be bound — the
    // caller's try/catch then abandons pushdown for the request.
    private static NavShapeBindings BindNavShape(
        EngagedExpand engaged, Type elem, IEdmModel model, ODataQuerySettings binderSettings)
    {
        LambdaExpression? predicate = null;
        if (engaged.Filter is not null)
        {
            var ctx = new QueryBinderContext(model, binderSettings, elem);
            predicate = (LambdaExpression)_filterBinder.BindFilter(engaged.Filter, ctx);
        }

        List<(LambdaExpression, bool)>? orderBy = null;
        if (engaged.OrderBy is not null)
        {
            var ctx = new QueryBinderContext(model, binderSettings, elem);
            OrderByBinderResult? result = _orderByBinder.BindOrderBy(engaged.OrderBy, ctx);
            for (OrderByBinderResult? cur = result; cur is not null; cur = cur.ThenBy)
            {
                orderBy ??= new List<(LambdaExpression, bool)>();
                orderBy.Add(((LambdaExpression)cur.OrderByExpression, cur.Direction == OrderByDirection.Descending));
            }
        }

        return new NavShapeBindings(predicate, orderBy);
    }

    // #206 phase 2 (optioned expand) / #254: compose the already-bound nested $filter/$orderby plus the
    // nested $skip/$top (and the #254 MaxExpandTop count bound) onto the navigation-access expression,
    // returning the shaped (un-materialized) IEnumerable. Pure expression assembly — no binding — so
    // the $levels recursion can call it once per level with the same NavShapeBindings.
    //
    // <paramref name="deferPagingToJson"/> (#300): true ONLY for the $levels recursion (set by its sole
    // caller, BuildLevelsNavAccess). Inside that recursion every level BOTH windows a collection AND
    // projects a further (self-referential) collection out of it — the same "double collection on one
    // level" shape #298 hit for $count — which requires SQL APPLY/LATERAL that not every provider (SQLite
    // among them) translates. When true, no SQL Skip/Take (nor the #298 count-bound Take below) is
    // composed at all; the caller windows in the JSON pass instead (ShapeLevelsInJson), exactly like the
    // count bound already deferred there via <paramref name="maxExpandTop"/> being null on that call.
    //
    // <paramref name="countViaCarrier"/> (#334): true for a top-level, projection-LEAF, non-$levels
    // collection expand that carries BOTH $count and a nested $skip/$top window, and whose exact
    // count the caller is obtaining as an independent correlated scalar subquery (see
    // BuildNavCountExpression / ExpandCountCarrier). Because the count no longer rides on the
    // materialized array's length, this level no longer has to fetch the whole filtered collection
    // to count it: $skip/$top compose to SQL exactly as they do without $count.
    private static Expression ApplyNavShape(
        Expression access, EngagedExpand engaged, Type elem, IEdmModel model,
        in NavShapeBindings bound, int? maxExpandTop, bool deferPagingToJson = false,
        bool countViaCarrier = false)
    {
        if (bound.Predicate is not null)
            access = Expression.Call(_enumerableWhere.MakeGenericMethod(elem), access, bound.Predicate);

        if (bound.OrderBy is { Count: > 0 })
        {
            bool first = true;
            foreach ((LambdaExpression keySelector, bool descending) in bound.OrderBy)
            {
                MethodInfo op = (first, descending) switch
                {
                    (true, false) => _enumerableOrderBy,
                    (true, true) => _enumerableOrderByDescending,
                    (false, false) => _enumerableThenBy,
                    (false, true) => _enumerableThenByDescending,
                };
                access = Expression.Call(op.MakeGenericMethod(elem, keySelector.ReturnType), access, keySelector);
                first = false;
            }
        }

        // #298 fix: the SQL Take(cap+1) count bound is only safe to compose when this level is a
        // projection LEAF (no nested $expand children of its own). A level WITH children is further
        // projected element-wise a few lines below in BuildShapedNavAccess (the .Select(...) that folds
        // the deeper navigation) — windowing THIS level's collection and ALSO projecting a further
        // collection out of each of its elements in the same query is the "double collection" shape that
        // requires SQL APPLY/LATERAL, which not every provider translates (SQLite among them); composing
        // the bound there risked exactly the untranslatable shape #298 reported. For a level with
        // children the ceiling is still enforced — just in the JSON pass (WriteNestedCountAndWindow)
        // instead of as a SQL LIMIT, the same trade the $levels path already takes below.
        bool isProjectionLeaf = engaged.Children is not { Count: > 0 };
        int? countBound = !deferPagingToJson && isProjectionLeaf && engaged.Count && maxExpandTop is int cap
            ? (int)Math.Min((long)cap + 1, int.MaxValue)
            : null;

        // #334: this level's count comes from an independent scalar subquery, so the count bound is
        // NOT composed and the requested window goes to SQL instead (see the composition block
        // below). `countBound` above is deliberately still COMPUTED — `paging` reads it, and keeping
        // that decision byte-identical is what makes the deterministic child-key ORDER BY tiebreaker
        // (and therefore WHICH rows land in the page, and therefore the response body) unchanged.
        bool carrierCount = countViaCarrier && !deferPagingToJson && isProjectionLeaf && engaged.Count;

        // #313: a BARE pushed leaf (no $count, no explicit $top of its own — $skip alone included) used
        // to compose NO SQL Take at all, leaving the single most common $expand shape unbounded by
        // MaxExpandTop. Same trade as countBound above (SQL Take(cap+1), gated to a projection leaf for
        // the same APPLY/LATERAL reason), just for the no-$count case. Mutually exclusive with
        // countBound (that one requires engaged.Count; this one requires !engaged.Count), and with an
        // explicit $top (Top is null here) since $top must win over the default ceiling bound.
        int? defaultLeafBound = !deferPagingToJson && isProjectionLeaf && !engaged.Count && engaged.Top is null
            && maxExpandTop is int defaultCap
            ? (int)Math.Min((long)defaultCap + 1, int.MaxValue)
            : null;

        // Whenever paging is in play — pushed to SQL now (no $count) OR deferred to the JSON window
        // (with $count, or under #300's deferPagingToJson) — stabilize the order so WHICH rows land in
        // the page is deterministic. Mirrors the root path's EnsureStableOrder (#241): append the nav
        // element's single key as a FINAL tiebreaker (a ThenBy after an explicit nested $orderby so a
        // non-unique sort column still pages stably, or the sole OrderBy when none was given). Applied
        // even when Skip/Take/countBound end up NOT composed to SQL (deferPagingToJson, or a
        // countBound-suppressing children level) so the deferred JSON window (ShapePushedExpandsInJson /
        // ShapeLevelsInJson) still pages over a deterministic SQL order. A composite/unresolvable key is
        // left to the provider (best-effort, never throws).
        //
        // #313 note on that best-effort miss: when TryGetKeyClrProperty returns null the Skip/Take below
        // is STILL composed, so EF sees a row-limiting operator with no OrderBy and logs
        // RowLimitingOperationWithoutOrderByWarning — which an app that opted into
        // ConfigureWarnings(w => w.Throw(...)) turns into an exception. That is pre-existing for the
        // explicit-$top/$skip and $count arms; defaultLeafBound only widens WHICH requests reach it.
        // Deliberately left as-is rather than substituting some arbitrary non-key OrderBy: the shape is
        // not reachable through ODataConventionModelBuilder at all (it rejects a composite-key
        // navigation target outright), only through an AdvancedConfigure-built EDM, and inventing an
        // order for a type whose key we could not resolve would change the wire order of exactly the
        // cases we cannot reason about.
        bool paging = (engaged.Skip is int s && s > 0) || engaged.Top is int || countBound is not null
            || defaultLeafBound is not null;
        if (paging && TryGetKeyClrProperty(model, elem) is { } keyProp)
        {
            ParameterExpression e = Expression.Parameter(elem, "e");
            LambdaExpression keySelector = Expression.Lambda(Expression.Property(e, keyProp), e);
            MethodInfo tiebreak = bound.OrderBy is { Count: > 0 } ? _enumerableThenBy : _enumerableOrderBy;
            access = Expression.Call(tiebreak.MakeGenericMethod(elem, keyProp.PropertyType), access, keySelector);
        }

        // #300 fix: inside the $levels recursion (deferPagingToJson), no SQL Skip/Take is composed at
        // all — every level both windows a collection and projects a further (self-referential)
        // collection out of it, the same untranslatable APPLY/LATERAL shape as the #298 count case.
        // $skip/$top are applied instead in the JSON pass (ShapeLevelsInJson), exactly as the count
        // bound already deferred (maxExpandTop: null) for this path. Outside $levels, Skip/Take push to
        // SQL only when $count is absent; with $count the full (ordered) filtered set is materialized
        // so the JSON pass can count it before paging (see EngagedExpand remarks) — bounded by
        // countBound (#254/#298) so an unbounded child collection can no longer be materialized.
        //
        // #304 fix: the raw (no-$count) Skip/Take below is ALSO only safe at a projection LEAF, for
        // exactly the same reason the #298 count bound is leaf-gated above — a level with children is
        // further projected element-wise a few lines below in BuildShapedNavAccess, so windowing THIS
        // level's collection and projecting a further collection out of each of its elements in the
        // SAME query is the same untranslatable "double collection" SQL APPLY/LATERAL shape, just
        // without $count in the mix. For a level with children the window is instead applied in the
        // JSON pass (ShapePushedExpandsInJson → ApplyNestedWindow), bounded by the same MaxExpandTop
        // ceiling WriteNestedCountAndWindow already enforces for the $count case.
        if (!deferPagingToJson)
        {
            if (carrierCount)
            {
                // #334: bound the FETCH by the requested window — Skip/Take compose to SQL exactly
                // as they do without $count. The count no longer rides on the array length, so
                // bounding the fetch cannot under-report Nav@odata.count (OData §11.2.4.2); the
                // JSON pass reads the carrier's exact value instead (ShapePushedExpandsInJson).
                //
                // The residual Take(cap + 1) when no $top was given preserves the pre-#334 DoS
                // bound: with a true count <= cap it never truncates the window, and with a true
                // count > cap the re-sited ceiling check 400s on the carrier's exact value before
                // the page is ever used — so MaxExpandTop breach behaviour is unchanged. With no
                // ceiling configured (the shipping default, #313) there is no residual bound, which
                // is also exactly what the pre-#334 path composed for this shape (`countBound` is
                // null when `maxExpandTop` is null): the $skip/$top the client asked for is now the
                // only bound, where before there was none at all.
                if (engaged.Skip is int csk && csk > 0)
                    access = Expression.Call(_enumerableSkip.MakeGenericMethod(elem), access, Expression.Constant(csk));
                long limit = engaged.Top is int ctp ? ctp : long.MaxValue;
                if (maxExpandTop is int ccap) limit = Math.Min(limit, (long)ccap + 1);
                if (limit < int.MaxValue)
                    access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant((int)limit));
            }
            else if (!engaged.Count)
            {
                if (isProjectionLeaf)
                {
                    if (engaged.Skip is int sk && sk > 0)
                        access = Expression.Call(_enumerableSkip.MakeGenericMethod(elem), access, Expression.Constant(sk));
                    if (engaged.Top is int tp)
                        access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(tp));
                    // #313: no explicit $top → fall back to the default ceiling bound (composed AFTER
                    // any $skip, same as the explicit-$top Take above) so a bare (or $skip-only) leaf is
                    // no longer an unbounded materialization.
                    else if (defaultLeafBound is int leafBound)
                        access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(leafBound));
                }
                // else: a level WITH children — defer the $skip/$top window to the JSON pass (#304).
            }
            else if (countBound is int rowBound)
            {
                access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(rowBound));
            }
        }

        return access;
    }

    // #206 phase 2 (multi-level expand): the fresh member-init projected for one element of an
    // intermediate expand level — <c>new Elem { scalar1 = source.scalar1, …, ChildNav = &lt;folded&gt; }</c>
    // — binding every scalar structural property (so nothing is column-pruned) and folding each nested
    // pushed navigation via BuildShapedNavAccess. <paramref name="source"/> is the element expression
    // (a Select lambda parameter for a collection, or the reference access for a single-valued nav).
    // Callers gate on IsMemberInitProjectable so every scalar bind is settable and non-complex.
    private static Expression BuildMemberInit(
        Expression source, Type elemType, IReadOnlyList<EngagedExpand> children,
        IEdmModel model, ODataQuerySettings binderSettings, int? maxExpandTop)
    {
        var bindings = new List<MemberBinding>();
        AddScalarBindings(bindings, source, elemType, model);
        foreach (EngagedExpand child in children)
        {
            bindings.Add(Expression.Bind(child.Binding.Property,
                BuildShapedNavAccess(source, child, model, binderSettings, maxExpandTop)));
        }
        return Expression.MemberInit(Expression.New(elemType), bindings);
    }

    // #206 phase 2 (multi-level expand): bind every scalar structural property of <paramref name="elemType"/>
    // as <c>member = source.member</c> into <paramref name="bindings"/> — the "don't column-prune an
    // intermediate level" rule shared by BuildMemberInit and BuildLevelsNavAccess. Callers gate on
    // IsMemberInitProjectable first, so every returned property is settable and non-complex.
    private static void AddScalarBindings(List<MemberBinding> bindings, Expression source, Type elemType, IEdmModel model)
    {
        foreach (PropertyInfo p in ScalarStructuralClrProps(elemType, model))
            bindings.Add(Expression.Bind(p, Expression.Property(source, p)));
    }

    // #206 phase 2 ($levels): build the bounded self-referential recursion for a $levels=N expand.
    // Returns the value assigned to <paramref name="owner"/>.Nav: each level is projected into a FRESH
    // member-init recursing the SAME navigation one level shallower, and the deepest level empties the
    // self-navigation (an empty collection / a null reference) so the graph is finite — no parent<->child
    // object cycle can form for System.Text.Json.
    //
    // #254 (item 2): the expand's nested $filter/$orderby/$skip/$top are applied at EVERY level of the
    // recursion, matching the semantics ODL itself implements — Microsoft's
    // SelectExpandQueryOption.ProcessLevels rewrites $levels=N into N nested
    // ExpandedNavigationSelectItems each carrying the SAME Filter/OrderBy/Top/Skip/Count options. The
    // <paramref name="bound"/> lambdas were bound ONCE by the caller: the nav element type is invariant
    // under $levels, so the same LambdaExpression is valid (and immutable) at every level. A nested
    // $expand under $levels is still deferred off pushdown by TryBuildEngagedExpand.
    private static Expression BuildLevelsNavAccess(
        Expression owner, EngagedExpand engaged, int remaining, IEdmModel model,
        in NavShapeBindings bound)
    {
        ExpandNavBinding nav = engaged.Binding;
        Type elem = nav.ElementType; // == owner's type (a true self-reference; see BuildLevelsNavBinding)
        Expression access = Expression.Property(owner, nav.Property);

        if (nav.IsCollection)
        {
            // #254/#300: NEITHER the MaxExpandTop count bound NOR $skip/$top are pushed into SQL here
            // (hence maxExpandTop: null AND deferPagingToJson: true). Inside a $levels projection every
            // level both WINDOWS a collection and projects a further (self-referential) collection out
            // of it, which EF Core can only translate with SQL APPLY/LATERAL — SQLite and other
            // providers without APPLY fail to translate, and the request would then silently degrade to
            // EDM-only (no data AND no count/window — #300 was exactly this for $skip/$top). The
            // ceiling and the $skip/$top window are both enforced/applied instead at EVERY level, in the
            // JSON pass (ShapeLevelsInJson → WriteNestedCountAndWindow / WriteNestedWindowOnly): a count
            // breach OR a $skip/$top-only breach is a 400 (#316), never a truncated count nor an
            // unbounded materialization, and $skip/$top window the already-materialized array. What is
            // given up is only the SQL-side cost bound — consistent with E3, which already leaves an
            // omitted nested $top unbounded.
            access = ApplyNavShape(access, engaged, elem, model, bound, maxExpandTop: null, deferPagingToJson: true);

            ParameterExpression n = Expression.Parameter(elem, "n");
            var bindings = new List<MemberBinding>();
            AddScalarBindings(bindings, n, elem, model);
            Expression deeper = remaining > 1
                ? BuildLevelsNavAccess(n, engaged, remaining - 1, model, bound)
                // Leaf (#335): a NEW empty list, so the self-navigation serializes as [] rather than
                // null and the recursion terminates without loading a further level.
                //
                // This used to be `n.Nav.Take(0).ToList()`, which reads the same [] but is not free:
                // it still NAMES the navigation, so EF Core composes a real N+1'th join level for it —
                // a full-table ROW_NUMBER() window whose every row is then discarded by
                // `WHERE "row" <= 0` (#335). Worse, translation cost for a pushed nested projection is
                // ~3x per collection level (#328), so the dead level is a full FACTOR OF 3 on the whole
                // request, not a constant. Measured on the #328 harness: depth 9, 3,830 ms -> 1,544 ms;
                // depth 10, 11,453 ms -> 4,582 ms. Expression.New(List<elem>) names no navigation, so
                // EF evaluates it client-side per row and emits exactly N joins for $levels=N.
                //
                // Byte-identical by construction: both shapes produce an empty List<elem> assigned to
                // the same member, and the deepest level is where the recursion stops, so no level that
                // carries data is affected. LevelsJoinCountSqliteTests pins both halves — the join
                // count AND the exact response bytes.
                : Expression.New(typeof(List<>).MakeGenericType(elem));
            bindings.Add(Expression.Bind(nav.Property, deeper));
            LambdaExpression proj = Expression.Lambda(Expression.MemberInit(Expression.New(elem), bindings), n);
            Expression projected = Expression.Call(_enumerableSelect.MakeGenericMethod(elem, elem), access, proj);
            return Expression.Call(_enumerableToList.MakeGenericMethod(elem), projected);
        }

        // Single-valued self-reference (e.g. a Manager chain): a null-guarded fresh member-init. The
        // OData parser rejects $filter/$orderby/$skip/$top/$count on a single-valued navigation, so
        // there is nothing to shape here — only a nested $select can reach this shape, and that is
        // applied on the serialized JSON (ShapeLevelsInJson).
        var refBindings = new List<MemberBinding>();
        AddScalarBindings(refBindings, access, elem, model);
        Expression refDeeper = remaining > 1
            ? BuildLevelsNavAccess(access, engaged, remaining - 1, model, bound)
            : Expression.Constant(null, elem);
        refBindings.Add(Expression.Bind(nav.Property, refDeeper));
        Expression refInit = Expression.MemberInit(Expression.New(elem), refBindings);
        return Expression.Condition(
            Expression.Equal(access, Expression.Constant(null, elem)),
            Expression.Constant(null, elem), refInit);
    }

    // #206 phase 2 (optioned expand): the CLR property for a navigation element type's single EDM key,
    // used to stabilize nested paging (see BuildShapedNavAccess). Returns null for a composite key, a
    // keyless type, or a CLR name that does not resolve — the caller then simply skips stabilization.
    private static PropertyInfo? TryGetKeyClrProperty(IEdmModel model, Type elem)
    {
        // #508: EdmClrTypeMap, not model.FindDeclaredType(elem.FullName). On a renamed schema this
        // answered null for every element type, which made every navigation non-pageable (#313) and
        // dropped the nested-paging tiebreaker.
        if (EdmClrTypeMap.FindEntityType(model, elem) is not { } entityType) return null;
        var keys = entityType.Key().ToList();
        if (keys.Count != 1) return null; // composite / keyless → leave order to the provider
        // #253: the EDM key name may be a [JsonPropertyName] rename — resolve back to the CLR property.
        return ODataPropertyNaming.FindClrPropertyByEdmName(elem, keys[0].Name);
    }

    // ── #313 stage 5: bare-$expand continuation paging ───────────────────────────────────────────
    //
    // One navigation of one entity set that a bare `?$expand=Nav` may page: over the ceiling it is
    // trimmed to MaxExpandTop and annotated with `Nav@odata.nextLink`, and a continuation route
    // `GET /{Set}({key})/{Nav}?$skip=N` is registered to serve the rest.
    //
    // Everything here is resolved ONCE at startup, off structural facts only.
    private readonly record struct ExpandPagingNav(
        // The EDM (JSON) navigation name — the URL segment, and the key the emission site matches on.
        // Honors [JsonPropertyName] exactly as $expand and $metadata do.
        string EdmName,
        PropertyInfo NavProperty,
        Type ElementType,
        // The child element type's single-key CLR property, resolved through the SAME
        // TryGetKeyClrProperty call ApplyNavShape uses to compose page 1's tiebreaker. Sharing the
        // call — not re-deriving the key — is what makes page 1's ORDER BY and the continuation's
        // ORDER BY provably the same column (§4.5 of the design). A composite/unresolvable key makes
        // the navigation non-pageable outright rather than silently unordered.
        PropertyInfo ChildKeyProperty);

    // THE SHARED PAGEABILITY PREDICATE. Two call sites — continuation-route registration
    // (MapEntitySet) and link emission (ShapePushedExpandsInJson) — and they must be the same set or
    // the feature is broken in one of two directions: a link with no route behind it is a 404 on the
    // continuation, and a route with no link in front of it is a delegate-safety hole.
    //
    // The condition that most needs to be shared: <b>ServeRaw is resolved through ResolveNavTreatment</b>,
    // not through "this profile owns no NavigationRouteDefinition for it". That is what keeps a
    // navigation THIS profile routes through a delegate from ever getting a raw continuation route —
    // its own candidate set puts it in DB, so the treatment is RunDelegate, never ServeRaw. Same rule,
    // same helper, and now the same CANDIDATE SET as the stage 3 startup diagnostic
    // (WarnUnboundedBareExpand).
    //
    // #421: THE CANDIDATE SET IS `new[] { profile }` — the URL-named set ALONE. It used to be
    // ResolveProfilesForEdmType, the sibling union, which is what the root read path beside it does
    // NOT do; the two disagreed about which profile's declaration governs a navigation at depth 1 —
    // the only depth this predicate governs, since #313 O5 restricts continuation links to the root.
    //
    // WHAT THE UNION WAS JUSTIFIED WITH, AND WHY BOTH HALVES ARE FALSE (#415, #421). It claimed "a
    // Blank navigation is emptied by ExpandLevelAsync before ShapePushedExpandsInJson ever sees it,
    // so no link is emitted for it", and that a per-profile predicate would therefore "serve those
    // rows RAW, bypassing the delegate".
    //
    //   (1) ExpandLevelAsync does NOT empty it at the root. ApplyCollectionPipelineAsync passes
    //       `new[] { requestSource }` — the URL-named profile ALONE — so ResolveNavTreatment never
    //       sees the sibling and answers ServeRaw. MEASURED:
    //       `/BeAuthors?$filter=Id eq 1&$expand=Books` with the delegate-backed sibling registered
    //       serves the five raw books (BareExpandContinuationDelegateSafetyTests
    //       .RootExpand_WithASiblingDelegate_StillServesTheDeclaringSetsOwnRawRows), and over a
    //       ceiling it returns the stage-2 400 — a status only reachable once the rows have been
    //       materialized and counted.
    //   (2) That is not a delegate bypass, so serving those rows raw is not one either. Under Model B
    //       declaring-set authority (FROZEN, owner decision 2026-07-26 on #293, which says in terms
    //       "Root (depth 1): KEEP as-is — already reads only the URL-named set"), BeAuthors declares
    //       Books delegate-less and is authoritative for its OWN navigation; the sibling's delegate
    //       governs the sibling's set. The union at depth >= 2 is ambiguity resolution — the EDM has
    //       no binding to say which set a nested path resolves to — and the root has nothing to
    //       disambiguate because the URL names the set.
    //
    // WHY THE ALIGNED PREDICATE IS NOT A DELEGATE-SAFETY HOLE, which is the whole question #421 turns
    // on. The continuation route reads the PARENT PROFILE'S OWN GetQueryable (step 4 of the handler),
    // pins one parent by key and SelectManys the navigation — the same queryable, the same entity
    // set, the same `entityAuthGroup` authorization as the root read. And the aligned candidate set is
    // BYTE-FOR-BYTE the array the root read path uses, so the navigations this predicate now returns
    // are exactly {navigations `GET /{Set}?$expand={Nav}` already serves RAW} ∩ {pageable}. The route
    // can therefore expose no row the read path beside it does not already expose to the same caller;
    // it exposes a strictly narrower window on one of them. Nothing crosses an entity-set boundary:
    // the sibling's delegate still governs the sibling's own routes, which are untouched.
    //
    // The direction of the old error was also not the harmless one it looks like. Withholding the
    // route withheld the LINK too (both call sites read this one predicate), so an over-ceiling bare
    // $expand on a set that declares the navigation delegate-less 400'd forever with no paging escape
    // hatch — ExpandPagingEnabled inert for that entity set, silently, on a navigation the profile
    // itself declared delegate-less. Fail-closed against a hazard that does not exist is not free.
    //
    // The remaining conditions are the ones under which a link could be emitted at all:
    //   - ExpandPagingEnabled   the opt-in knob (default false)
    //   - MaxExpandTop is int   the page size, for page 1 and every continuation alike. NEVER MaxTop:
    //                           they are independent knobs, so paging the continuation at MaxTop would
    //                           serve N rows on page 1 and MaxTop on page 2+ — and with MaxTop null,
    //                           page 2 would be unbounded and #313's DoS would return on the link.
    //   - ExpandEnabled         no $expand, no bare expand to page
    //   - HasGetQueryable       the pushdown path is the only one that composes the SQL bound (G11:
    //                           GetById/$expand of a delegate-less nav serves [] and is out of scope)
    //   - ExpandPushdownEnabled with pushdown off no EngagedExpand is built, so nothing materializes
    //   - collection-valued     a single-valued nav is one row; there is nothing to continue
    //   - resolvable single key see ChildKeyProperty above
    //
    // Returns an empty list — the shipping default — whenever the knob is off, which is what keeps a
    // default registration's route table byte-identical.
    private static IReadOnlyList<ExpandPagingNav> ResolveExpandPagingNavigations(
        IEntitySetEndpointSource profile, Type modelType, OhDataRegistration registration)
    {
        if (!profile.ExpandPagingEnabled || profile.MaxExpandTop is not int ||
            !profile.ExpandEnabled || !profile.HasGetQueryable || !profile.ExpandPushdownEnabled)
        {
            return Array.Empty<ExpandPagingNav>();
        }

        // The entity set must exist in the container for the continuation URL this predicate implies
        // to be addressable at all. Kept as a guard only — #421 removed the sibling union that used
        // to be the reason this lookup was here.
        if (registration.EdmModel.EntityContainer?.FindEntitySet(profile.EntitySetName) is null)
            return Array.Empty<ExpandPagingNav>();

        // #421: THE CANDIDATE SET IS `new[] { profile }` — the URL-named set ALONE, byte-for-byte the
        // array ApplyCollectionPipelineAsync passes as the root level's `levelSources`. See this
        // method's remarks above for why the sibling union that used to be here protected nothing.
        IReadOnlyList<IEntitySetEndpointSource> candidates = new[] { profile };

        List<ExpandPagingNav> result = new();
        // NavigationPropertyNames (not the EDM's navigations) deliberately: it is the set the pushdown
        // gate itself builds pushdownExpandNavs from, so a nav absent from it can never be in the
        // engaged tree and could never receive a link. It is also the set BuildStructuralProperties
        // subtracts, so a continuation route can never collide with a structural-property route.
        foreach (string navName in profile.NavigationPropertyNames)
        {
            if (BuildExpandNavBinding(modelType, navName, registration.EdmModel) is not { } binding) continue;
            if (!binding.IsCollection) continue;
            if (ResolveNavTreatment(navName, candidates).Treatment != NavTreatment.ServeRaw) continue;
            if (TryGetKeyClrProperty(registration.EdmModel, binding.ElementType) is not { } childKey) continue;

            result.Add(new ExpandPagingNav(
                ODataPropertyNaming.ResolveEdmName(binding.Property), binding.Property,
                binding.ElementType, childKey));
        }
        return result;
    }

    // #313 stage 5: a one-field holder for the parent key value referenced from the continuation's
    // Where predicate. Expression.Constant(box) + Expression.Field is the exact shape the C# compiler
    // emits for a captured local, which is what EF Core's parameter extraction recognises — so the
    // key becomes a SQL PARAMETER rather than a literal baked into the command text. An
    // Expression.Constant of the value itself would produce a distinct SQL string per key and defeat
    // the provider's plan cache on a route whose whole purpose is to be called repeatedly.
    private sealed class ContinuationKeyBox<T>
    {
        public T Value = default!;
    }

    // #313 stage 5: the continuation page, written as plain LINQ so the composed shape is readable
    // rather than assembled from MethodInfos. The only reason it is generic-and-reflected at all is
    // that the navigation element type is not a type parameter of MapEntitySet; its sole caller
    // closes it once at startup and compiles a delegate over it, so no reflection runs per request
    // and no TargetInvocationException can wrap a provider fault.
    //
    // SelectMany over a key-pinned parent is what makes the provider emit an INNER JOIN with
    // LIMIT/OFFSET rather than the partitioned ROW_NUMBER() window page 1 uses — an index seek, not
    // a window over the whole child table. The OrderBy is unconditional (see the call site).
    private static object[] ContinuationPage<TParent, TElement, TChildKey>(
        IQueryable<TParent> parents,
        Expression<Func<TParent, IEnumerable<TElement>>> navSelector,
        Expression<Func<TElement, TChildKey>> childKeySelector,
        int skip, int take)
    {
        TElement[] page = parents
            .SelectMany(navSelector)
            .OrderBy(childKeySelector)
            .Skip(skip)
            .Take(take)
            .ToArray();
        object[] boxed = new object[page.Length];
        for (int i = 0; i < page.Length; i++) boxed[i] = page[i]!;
        return boxed;
    }

    private static readonly MethodInfo _continuationPageMethod =
        typeof(OhDataEndpointFactory).GetMethod(
            nameof(ContinuationPage), BindingFlags.NonPublic | BindingFlags.Static)!;

    // #313 stage 5: "truly bare", as a plan-time predicate over the already-parsed EngagedExpand.
    // The rule in one sentence: a nested option list that normalizes to the IDENTITY transform is
    // bare; anything else is not. Only two no-ops survive the parser — `$skip=0` (ApplyNavShape
    // already guards `sk > 0`, so it composed nothing) and `$count=false` (EngagedExpand.Count is
    // `item.CountOption == true`, so absent and false are already the same value) — and both are
    // therefore treated as bare, giving a faithful `?$skip={cap}` continuation.
    //
    // Everything else keeps the 400 stage 2 gave it (§5's fail-closed matrix): a nested
    // $filter/$orderby/$select cannot be carried by a $skip-only link; $levels and a level WITH
    // children are not SQL-bounded at all, so a link there would advertise a bound that does not
    // exist; an explicit $top means the client asked for exactly N rows and got them, so the response
    // is complete with respect to the request and a link would be wrong.
    private static bool IsBareContinuableLeaf(in EngagedExpand e) =>
        e.Binding.IsCollection
        && e.Levels == 0
        && e.Children is not { Count: > 0 }
        && e.Filter is null
        && e.OrderBy is null
        && e.Top is null
        && e.Skip is null or 0
        && !e.Count
        && e.NestedSelect is not { Count: > 0 };

    // #313 stage 5: what the JSON shaping pass needs to write a continuation link, threaded from the
    // GetQueryable collection route. Non-null ONLY at depth 1 — the recursive ShapePushedExpandsInJson
    // calls pass null, which is exactly what keeps depth >= 2 on its 400 (§5).
    //
    // ParentItems is the CLR page, index-parallel with the JsonObjects being shaped. It is threaded
    // because <b>the parent key is not in the JSON</b>: a root $select strips it and the shaping pass
    // runs after the strip (G6), so reading the key off the payload would produce a broken link for
    // exactly the requests that need one most. ExpandLevelAsync maintains the same parallel
    // items/jsonItems pair for the same reason and is the precedent followed here.
    // #412: <paramref name="RequestedPageSize"/> is this request's `Prefer: [odata.]maxpagesize=N`,
    // or null when the client asked for nothing. It NARROWS the nested page — never the ceiling — and
    // only on the one arm that emits a continuation link. See ShapePushedExpandsInJson's bare-leaf arm.
    private sealed record ExpandPagingContext(
        string BaseUrl,
        string EntitySetName,
        PropertyInfo ParentKeyProperty,
        IReadOnlyList<object> ParentItems,
        IReadOnlyDictionary<string, ExpandPagingNav> PageableByEdmName,
        int? RequestedPageSize);

    // #206 phase 2 (optioned + multi-level expand): apply the JSON-side portion of a pushed expand's
    // nested options to the already-serialized parent objects (in the configured naming policy —
    // PascalCase by default) — $count (emit
    // Nav@odata.count), the count-deferred $skip/$top paging, nested $select projection, and
    // (recursively) the same shaping for each deeper pushed level. Filter/OrderBy (and paging when
    // $count is absent) were already applied in SQL by BuildShapedNavAccess, so this touches only the
    // navs that actually need post-serialization shaping. Reuses StripToSelectedProperties so nested
    // $select casing/annotation handling is identical to the root-level strip.
    // #206 ($levels): the CLR property names of every navigation this request pushed with $levels,
    // walked recursively through the engaged tree. OmitUnexpandedNavigations uses this to keep the
    // bounded recursion of ONLY these (delegate-less, pushed) navs — a delegate-backed $levels nav is
    // never in the engaged tree, so its deeper self-references stay stripped as before. Returns null
    // (the common no-$levels case) so the keep is a strict no-op unless a $levels expand was pushed.
    private static HashSet<string>? CollectPushedLevelsNavNames(IReadOnlyList<EngagedExpand>? engaged)
    {
        if (engaged is null) return null;
        HashSet<string>? names = null;
        void Walk(IReadOnlyList<EngagedExpand> level)
        {
            foreach (EngagedExpand e in level)
            {
                // #253 completion: OmitUnexpandedNavigations matches against EDM (JSON) nav names, so
                // record the binding's EDM name (a [JsonPropertyName]-renamed self-referential nav
                // resolves to its JSON name, an un-renamed one to its CLR name).
                if (e.Levels > 0) (names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(ODataPropertyNaming.ResolveEdmName(e.Binding.Property));
                if (e.Children is { Count: > 0 }) Walk(e.Children);
            }
        }
        Walk(engaged);
        return names;
    }

    // #313 stage 5: <paramref name="paging"/> is non-null ONLY on the depth-1 call from the
    // GetQueryable collection route; every recursive call below passes null, which is what keeps
    // depth >= 2 on the 400 stage 2 gave it (§5, and O5 on the issue).
    // #334: <paramref name="carrierCounts"/> maps a counted navigation's CLR property to that nav's
    // exact per-parent count, index-parallel with <paramref name="parents"/>. Non-null ONLY on the
    // depth-1 call from the GetQueryable collection route (like <paramref name="paging"/>), and only
    // for navs the carrier projection actually carried; every other counted nav still counts its
    // materialized array through WriteNestedCountAndWindow.
    private static void ShapePushedExpandsInJson(
        IEnumerable<JsonObject> parents, IReadOnlyList<EngagedExpand> engaged,
        JsonSerializerOptions serializerOptions, int? maxExpandTop,
        ExpandPagingContext? paging = null,
        IReadOnlyDictionary<PropertyInfo, int[]>? carrierCounts = null)
    {
        foreach (EngagedExpand e in engaged)
        {
            // A level with a nested $count/$select OR deeper pushed children needs JSON work; a pure
            // leaf whose options were fully handled in SQL is skipped. (A purely structural $levels
            // recursion carries no Count/NestedSelect/Children either, so it is skipped here too —
            // UNLESS it carries a nested $skip/$top: #300 fixed BuildLevelsNavAccess to no longer push
            // those to SQL for the $levels path, so a $levels item with $skip/$top and nothing else
            // now needs the JSON pass too, or the window would silently never be applied.)
            bool hasChildren = e.Children is { Count: > 0 };
            bool levelsNeedsJsonPaging = e.Levels > 0 && (e.Skip is int || e.Top is int);
            // #313: a bare collection leaf (no $count, no $top/$skip of its own) now carries a SQL
            // Take(MaxExpandTop+1) bound composed by ApplyNavShape (defaultLeafBound) — so it still
            // needs to be visited here to enforce the ceiling, even though there is no count/select/
            // children work to do otherwise. Also the entry point for a BARE $levels recursion (which
            // carries no children by construction — TryBuildEngagedExpand defers a nested $expand under
            // $levels off pushdown), whose per-level ceiling ShapeLevelsInJson enforces below. Gated on
            // `maxExpandTop is int` so an uncapped registration keeps skipping the walk outright.
            bool needsLeafCeilingCheck = e.Binding.IsCollection && !hasChildren && e.Top is null && maxExpandTop is int;
            if (!e.Count && e.NestedSelect is null && !hasChildren && !levelsNeedsJsonPaging && !needsLeafCeilingCheck)
                continue;

            // #254 (item 2): a $levels expand may now carry $count/$select. Its recursion is implicit
            // (there is no per-level EngagedExpand — the SAME binding repeats), so shape every level by
            // walking the serialized graph down the self-navigation.
            if (e.Levels > 0)
            {
                ShapeLevelsInJson(parents, e, e.Levels, serializerOptions, maxExpandTop);
                continue;
            }

            PropertyInfo prop = e.Binding.Property;
            string key = ResolveNavigationJsonKey(prop.Name, prop, serializerOptions);

            // #313 stage 5: is THIS engaged expand the one shape a $skip-only continuation can serve?
            // Resolved once per expand item rather than per parent — it is a plan-time fact.
            ExpandPagingNav? pageableNav = null;
            if (paging is not null && maxExpandTop is int && IsBareContinuableLeaf(e) &&
                paging.PageableByEdmName.TryGetValue(
                    ODataPropertyNaming.ResolveEdmName(prop), out ExpandPagingNav resolvedNav))
            {
                pageableNav = resolvedNav;
            }

            // #334: this nav's exact per-parent counts, when the carrier projection supplied them.
            int[]? navCounts = null;
            carrierCounts?.TryGetValue(prop, out navCounts);

            // Index into paging.ParentItems. The two lists are built index-parallel by the caller
            // (see the ShapePushedExpandsInJson call site in the GetQueryable route), so this counter
            // and the foreach below stay in step by construction.
            int parentIndex = -1;
            foreach (JsonObject parent in parents)
            {
                parentIndex++;
                JsonNode? node = parent[key];
                if (e.Binding.IsCollection && node is JsonArray arr)
                {
                    if (e.Count && navCounts is not null)
                    {
                        // Defensive: navCounts is built index-parallel with `parents` in ONE loop at
                        // the single call site that supplies it, so this cannot fire. It throws
                        // rather than falling through to WriteNestedCountAndWindow, because that
                        // path counts the materialized array — which the carrier has already
                        // SQL-windowed — and would therefore report the PAGE size as the collection
                        // count. A 500 is strictly better than a silently wrong @odata.count.
                        if ((uint)parentIndex >= (uint)navCounts.Length)
                            throw new InvalidOperationException("OhData: nested $count carrier desynchronised from the serialized page.");

                        // #334: exact count from SQL, and the page was ALREADY windowed in SQL — so
                        // no JSON window is applied here (that is the whole point: the fetch was
                        // bounded to the requested window instead of to the ceiling). The
                        // MaxExpandTop ceiling is re-sited from "materialized array length" to "the
                        // exact count"; see the EnsureWithinExpandCeiling(int, …) overload for why
                        // that is the same predicate rather than merely a similar one.
                        int exact = navCounts[parentIndex];
                        EnsureWithinExpandCeiling(exact, key, maxExpandTop, "'$count'");
                        parent[$"{key}@odata.count"] = exact;
                    }
                    else if (e.Count)
                    {
                        WriteNestedCountAndWindow(parent, key, arr, e, maxExpandTop);
                    }
                    // #304: a children level with $skip/$top but no $count was never SQL-windowed
                    // (ApplyNavShape deferred it here) — apply that window now, BEFORE recursing into
                    // children, so only the surviving (windowed) parents are shaped further. $skip=0 is
                    // guarded out (mirrors ApplyNavShape's own `sk > 0` guard) so a no-op $skip=0 doesn't
                    // trip the MaxExpandTop ceiling below — but $top is NOT guarded on > 0: $top=0 must
                    // still window (to an empty array), never fall through to "no window at all".
                    else if (hasChildren && ((e.Skip is int sk && sk > 0) || e.Top is int))
                    {
                        WriteNestedWindowOnly(arr, key, e, maxExpandTop);
                    }
                    // #313: bare children (nested $expand, no $count/$skip/$top of its own) can't be
                    // SQL-windowed at all (the same APPLY/LATERAL constraint documented on
                    // ApplyNavShape's isProjectionLeaf gate) — so it was, and still is, fully
                    // materialized here. It now needs the ceiling check the windowed shapes above
                    // already get, applied BEFORE recursing into children so a breach 400s before
                    // descending any further.
                    else if (hasChildren && maxExpandTop is int)
                    {
                        EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$expand'");
                    }
                    // #313: bare leaf (no children, no $count, no explicit $top — a lone $skip=0 no-op
                    // included). ApplyNavShape now SQL-bounds this shape to MaxExpandTop+1 rows
                    // (defaultLeafBound), so arr.Count > cap here means the true collection exceeds the
                    // configured budget.
                    //
                    // #313 stage 5: for the truly-bare subset on an opted-in profile that breach becomes
                    // a continuation instead of a 400 — trim to the ceiling and annotate. Everything
                    // else still takes the EnsureWithinExpandCeiling 400 below, so the M1 rule ("no
                    // bound without either a link or a 400") holds at this commit as at every other.
                    else if (!hasChildren && e.Top is null && maxExpandTop is int leafCap)
                    {
                        // #412: the client may ask for a SMALLER nested page with
                        // `Prefer: [odata.]maxpagesize=N` (Protocol §8.2.8.5 — "each collection within
                        // the response"). It narrows the page, never the ceiling: clamping the ceiling
                        // to a client-supplied number would let a header turn a 200 into a 400, and
                        // MaxExpandTop is the server's own DoS bound. So the preference is read ONLY
                        // on this arm, and ONLY when a link is actually going out — trimming without a
                        // link is the silent truncation M1 forbids, which is why a non-pageable
                        // navigation below still measures itself against maxExpandTop and not against
                        // the request.
                        int leafPage = leafCap;
                        if (pageableNav is not null && paging!.RequestedPageSize is int requested && requested < leafCap)
                            leafPage = requested;

                        // WriteNestedNextLink reports false when it could not build a link (a null key
                        // value); it leaves the array untouched in that case, so the ceiling's 400
                        // applies exactly as it would for any other non-pageable over-ceiling shape.
                        // Never a trim without a link.
                        if (pageableNav is not { } pnav || arr.Count <= leafPage
                            || !WriteNestedNextLink(parent, key, arr, leafPage, pnav, paging!, parentIndex))
                        {
                            EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$expand'");
                        }
                    }
                    // Recurse into deeper pushed levels on the (paged) elements BEFORE this level's
                    // $select strip — the strip keeps expanded-nav names (ExtractSelectedProperties), so
                    // the children survive, and shaping deeper counts/selects sees the full child graph.
                    if (hasChildren)
                        ShapePushedExpandsInJson(arr.OfType<JsonObject>(), e.Children!, serializerOptions, maxExpandTop);
                    if (e.NestedSelect is not null)
                        StripToSelectedProperties(arr.OfType<JsonObject>(), e.NestedSelect);
                }
                else if (!e.Binding.IsCollection && node is JsonObject one)
                {
                    if (hasChildren)
                        ShapePushedExpandsInJson(new[] { one }, e.Children!, serializerOptions, maxExpandTop);
                    if (e.NestedSelect is not null)
                        StripToSelectedProperties(new[] { one }, e.NestedSelect);
                }
            }
        }
    }

    // #254/#304: the shared MaxExpandTop ceiling check for a collection level whose windowing was
    // deferred to the JSON pass — because it couldn't be SQL-bounded, the full (filtered) collection had
    // to be materialized first (see ApplyNavShape's isProjectionLeaf gate), so a collection larger than
    // the configured budget is a DoS exposure the same way an unbounded nested $count materialization
    // would be. <paramref name="verb"/> names what couldn't be computed/applied in the resulting message
    // (e.g. "'$count'" or "'$top'/'$skip'") so the two call sites (count vs. plain windowing) get a
    // distinct-but-same-family message. Throws Microsoft.OData.ODataException, caught by the collection
    // route's existing handler and returned as a 400 InvalidQueryOption — no IResult threading through
    // this void recursive walk.
    private static void EnsureWithinExpandCeiling(JsonArray arr, string key, int? maxExpandTop, string verb) =>
        EnsureWithinExpandCeiling(arr.Count, key, maxExpandTop, verb);

    // #334: the same check against a count that did NOT come from a materialized array — the
    // carrier's exact scalar. Split out (rather than duplicated at the call site) so the message the
    // client sees is byte-identical whichever way the count was obtained. The predicate is also
    // equivalent, not merely similar: the pre-#334 array was Take(cap + 1)-bounded, so
    // `arr.Count > cap` was already exactly `trueCount > cap` — with the carrier the breach signal
    // is simply exact rather than a saturated proxy for it.
    private static void EnsureWithinExpandCeiling(int count, string key, int? maxExpandTop, string verb)
    {
        if (maxExpandTop is int cap && count > cap)
        {
            throw new Microsoft.OData.ODataException(
                $"The nested {verb} on '{key}' cannot be computed: the related collection exceeds the " +
                $"maximum of {cap} entities. Narrow it with a nested $filter.");
        }
    }

    // #313 stage 5: trim one over-ceiling bare leaf to the ceiling and annotate it with
    // <c>Nav@odata.nextLink</c>. The counterpart of EnsureWithinExpandCeiling for the one shape a
    // $skip-only continuation can faithfully serve.
    //
    // TRIM AND LINK ARE ONE STEP, OR NEITHER HAPPENS. Both bail-outs below are checked BEFORE the
    // array is touched, and the method reports whether it linked so the caller can fall back to the
    // ceiling's 400. An earlier revision trimmed first and returned early on a bail-out, which left a
    // silently truncated array carrying neither a link nor an error — the one outcome #313's M1 rule
    // forbids outright. (The comment that justified it, "a page that is complete-as-far-as-it-goes",
    // was wrong on its own terms: the probe row and every row above the cap were already gone.)
    //
    // Trimming is by "while > cap" rather than removing a single assumed probe row: the caller only
    // reaches this for arr.Count > cap and ApplyNavShape bounded the SQL to cap + 1 rows, so today the
    // excess is exactly one row, but the loop stays correct if that bound ever widens.
    //
    // ANNOTATION NAME: `Nav@odata.nextLink`, the 4.0 long form. The 4.01 short form is a SHOULD and
    // this framework emits OData-Version: 4.0 with @odata.-prefixed control information everywhere.
    // The prefix is the payload key (naming policy + [JsonPropertyName]), so the annotation is a
    // sibling of the property it annotates under the same spelling; the URL segment is the EDM name,
    // which is what $metadata declares and what $expand accepts.
    //
    // PLACEMENT: after the array. JSON Format §20.2 exempts nextLink from the "immediately prior"
    // rule, and JsonObject insertion order gives this for free.
    //
    // THE KEY COMES FROM THE CLR ENTITY, NEVER THE PAYLOAD. A root $select strips the key property
    // and this pass runs after the strip, so `parent["Id"]` is absent for exactly the requests that
    // most need a working link. paging.ParentItems is the index-parallel CLR page threaded in for
    // this one purpose.
    // Returns true when the array was trimmed AND annotated; false when no link could be built, in
    // which case the array is left EXACTLY as it was and the caller applies the ceiling's 400 instead.
    private static bool WriteNestedNextLink(
        JsonObject parent, string key, JsonArray arr, int cap,
        in ExpandPagingNav nav, ExpandPagingContext paging, int parentIndex)
    {
        // Defensive: a desynchronised index would silently produce a link for the WRONG parent, which
        // is worse than no link. Unreachable through the single call site, which builds ParentItems
        // index-parallel with the JsonObject list it iterates — kept as an assertion, not as a case
        // the tests can reach.
        if ((uint)parentIndex >= (uint)paging.ParentItems.Count) return false;

        // Reachable: TKey is unconstrained, so a string or Nullable<T> key property can hold null on a
        // returned entity, and ODataEntityKeyUrlFormatter.Format throws on null. Without a key there is
        // no addressable continuation, so this collection is not pageable after all and takes the 400.
        object? parentKey = paging.ParentKeyProperty.GetValue(paging.ParentItems[parentIndex]);
        if (parentKey is null) return false;

        while (arr.Count > cap) arr.RemoveAt(arr.Count - 1);

        // Page 1's child offset is always 0 — a bare expand carries no $skip by definition — so the
        // first continuation hop is always ?$skip={cap}. The root page's own offset never appears
        // here: the root's continuation is a $skiptoken on a DIFFERENT path served by a DIFFERENT
        // route, and neither link builder reads the response body (§4.6).
        parent[$"{key}@odata.nextLink"] =
            $"{paging.BaseUrl}/{paging.EntitySetName}({ODataEntityKeyUrlFormatter.Format(parentKey)})" +
            $"/{nav.EdmName}?$skip={cap.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    // #418/#463/#464: the MaxExpandTop ceiling over every RAW-SERVED collection expansion in a
    // response — at EVERY level of the $expand tree, on EVERY read path.
    //
    // "RAW-SERVED" means: the rows were never loaded by anything the framework composed. They are
    // whatever the handler's own graph already held — an EF Include inside a GetById delegate, a
    // fixup-populated tracked graph behind GetAll, an in-memory object graph behind a non-EF
    // IQueryable or a Priority-1 source, or a branch the $expand pushdown declined to engage. The
    // framework applies NO nested $filter/$orderby/$top window to any of it (measured: all six
    // nested options are silently ignored there while the collection pushdown honours every one),
    // so the configured ceiling is the ONLY bound such a collection has.
    //
    // WHAT THIS REPLACED, AND WHY THE SHAPE CHANGED TWICE.
    //   #418 shipped this as a depth-1 pass over the single-entity read, driven by a nav set resolved
    //   ONCE at startup from the root profile. Both of those turned out to be holes:
    //   - #463 (depth): the walk never recursed into item.SelectAndExpand and the startup set held
    //     only the ROOT profile's navigations, so with cap = 2 `?$expand=Books($expand=Chapters)`
    //     served every chapter. That is #454's pattern again — a validation and its enforcement
    //     consulting different sets: ValidateNestedTopCeiling walks the WHOLE tree (an explicit
    //     $top=1000 at depth 2 is rejected) while the ceiling that bounds SERVED data checked depth 1.
    //     The option that would have bounded the fetch was rejected; the shape that fetched
    //     everything passed. It closed the option axis (#418's own note) and not the depth axis.
    //   - #464 (path): the collection route's ceiling and its continuation link both live behind
    //     ShapePushedExpandsInJson, which runs ONLY when ResolveEfCoreAssembly found EF Core. So on a
    //     non-EF GetQueryable ($search's in-memory swap included), on GetAll and on Priority-1 the
    //     configured DoS bound silently did not exist — `?$expand=Children` at cap 1 served all three
    //     children, and `?$expand=Children($top=1)` accepted the in-ceiling $top and served all three
    //     as well. MaxExpandTop's own XML doc claimed it bounded "every collection $expand level".
    //   Both are the same defect — a bound whose enforcement was sited on ONE substrate at ONE depth —
    //   so they get one mechanism, resolved PER LEVEL, called from ApplyCollectionPipelineAsync where
    //   all five read routes already converge.
    //
    // MODEL B GOVERNS WHICH ROOT BRANCHES ARE RAW — AT DEPTH 1, AND ONLY THERE. Through the shared
    // ResolveNavTreatment (#293, FROZEN), over the URL-named profile alone, which is exactly what
    // ApplyCollectionPipelineAsync passes ExpandLevelAsync as the root candidate set:
    //   - ServeRaw is bounded, and descended into. A ServeRaw nav with NO opinion from any candidate
    //     is bounded too — nothing can delegate it (no candidate routes it).
    //   - RunDelegate is NOT bounded and NOT descended into: ExpandLevelAsync really did run the
    //     delegate, so those rows are the developer's own answer and #313 O6 settled that the
    //     framework does not truncate them. A 400 would be the same weakening by another route.
    //   - Blank is NOT bounded — but it is also UNREACHABLE here, and that is structural rather than
    //     incidental: the root candidate set is the single requesting profile, and over ONE candidate
    //     ResolveNavTreatment can only answer ServeRaw or RunDelegate (a candidate either routes a
    //     navigation or declares it, never both, so DB and DL can never both be non-empty and DB can
    //     never hold two). The depth-1 gate is therefore "skip RunDelegate" in practice; it is
    //     written as `!= ServeRaw` so that it stays correct if the root set ever widens, not because
    //     a Blank root branch is a case the tests can reach.
    //
    // BELOW DEPTH 1 THERE IS NO CLASSIFICATION TEST AT ALL, and that is the correction #464-one-
    // level-down needed. Everything this walk reaches below depth 1 sits under a ServeRaw parent by
    // construction, and ExpandLevelAsync's ServeRaw branch DOES NOT RECURSE — so no delegate ran
    // down there, nothing was blanked, and every value present is the root handler's own raw graph
    // regardless of how the navigation naming it is classified. An earlier revision of this pass
    // applied the depth-1 test at every level and cited #313 O6 for the exemption; MEASURED, cap = 2,
    // GetAll, Author -Books(delegate-less)-> Book -Chapters(DELEGATE)->,
    // `?$expand=Books($expand=Chapters)` served five chapters with the Chapters delegate invoked ZERO
    // times. The exemption was citing a delegate that never ran, over rows the ceiling exists to
    // bound. So below depth 1: check and descend, classification-blind.
    //
    // THE DESCENT STILL STOPS AT A NON-ServeRaw PARENT AT DEPTH 1, and that is the collection route's
    // own rule, not a new one: TryBuildEngagedExpand pushes a branch only when it is delegate-less
    // end-to-end, so the pushed ceiling likewise never reaches into a delegate-backed parent's
    // subtree. Keeping the two the same is what stops this pass from bounding a delegate's answer —
    // and it is a strictly narrower exemption than "any navigation a profile declares with a
    // delegate", which is what made the earlier revision wrong.
    //
    // AN ENGAGED NAVIGATION IS SKIPPED WHOLESALE. ShapePushedExpandsInJson/ShapeLevelsInJson enforce
    // the ceiling for those (and, for the one shape #313 allows, trim-and-link instead of 400) AFTER
    // this pass runs. Checking them here as well would 400 exactly the requests that were about to be
    // served a continuation link.
    //
    // A 400, NEVER A TRIM-AND-LINK -- and that asymmetry with the collection route is the whole M1
    // analysis, so it is recorded here rather than in a commit message.
    //
    // M1 ("no bound without either a continuation link or a 400") permits both outcomes. The link is
    // the better one wherever it can be built, and two of the three things it needs ARE available
    // here: the parent key is in the URL (so no ExpandPagingContext threading is required at all),
    // and the continuation route GET /{Set}({key})/{Nav}?$skip=N is already registered whenever
    // ResolveExpandPagingNavigations returned that navigation. The third is not, and it is decisive:
    //
    //   PAGE 1 AND THE CONTINUATION CANNOT BE PROVEN TO AGREE ON AN ORDER. On the PUSHED collection
    //   expansion the framework composes BOTH sides -- ApplyNavShape appends OrderBy(child key) to
    //   page 1's SQL and the continuation composes the same OrderBy over the same column, so the two
    //   agree by construction (#313 s4.5). On a raw-served expansion the framework composes NEITHER:
    //   the child rows arrive already materialized inside whatever the developer's own handler
    //   returned, in whatever order that handler produced (measured on this tree: a plain
    //   `LEFT JOIN "Books"` with no ORDER BY over the child at all, and an in-memory GetAll graph has
    //   no defined order at all). Re-sorting the serialized JsonArray to compensate does not close the
    //   gap -- it would compare the child key as a JSON value, while the continuation compares it in
    //   the DATABASE, and those two orders genuinely differ for the ordinary key types (SQL Server
    //   orders `uniqueidentifier` by a byte permutation no JSON sort reproduces; string keys order by
    //   the column's collation, not by ordinal).
    //
    // A link over a disagreeing order silently SKIPS and DUPLICATES rows across the page boundary,
    // which is worse than the 400 and is undetectable by the client. So this site takes the 400,
    // exactly as #418's own note recommends for the case where the ceiling is straightforward and the
    // link is not. ExpandPagingEnabled therefore buys nothing on a raw-served expansion, and neither
    // message below pretends otherwise.
    //
    // Throws Microsoft.OData.ODataException, which all five read routes already catch and surface as
    // 400 InvalidQueryOption -- no IResult threading through this void recursive walk.
    private static void EnforceRawExpandCeiling(
        IReadOnlyList<JsonObject> levelObjects,
        SelectExpandClause? clause,
        IReadOnlyList<IEntitySetEndpointSource> rootSources,
        Type? levelClrType,
        IReadOnlyList<EngagedExpand>? engaged,
        JsonSerializerOptions serializerOptions,
        int cap,
        int maxExpansionDepth,
        string entitySetName,
        bool singleEntityRead,
        string pathPrefix,
        string pathSuffix,
        int depth)
    {
        if (clause is null || levelObjects.Count == 0 || depth > MaxNestedExpandDepth) return;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string edmName = item.PathToNavigationProperty.FirstSegment.Identifier;

            // Engaged: bounded (and, where #313 allows, trimmed-and-linked) by
            // ShapePushedExpandsInJson / ShapeLevelsInJson instead. Skip the whole branch — a level
            // under an engaged one is engaged too, so that pass covers all of it.
            if (IsEngagedNav(engaged, edmName)) continue;

            // THE MODEL B GATE IS A DEPTH-1 GATE, AND ONLY A DEPTH-1 GATE. At depth 1 a navigation's
            // treatment decides whether anything other than the root handler produced its rows: a
            // RunDelegate nav's rows came from the developer's own delegate (#313 O6 — not the
            // framework's to bound), and a Blank one was overwritten with []/null by ExpandLevelAsync
            // (nothing to bound). Both are skipped, and skipping them also stops the descent, so this
            // walk never enters a delegate's subtree.
            //
            // BELOW depth 1 the same test would be wrong, and applying it there was the #464 defect
            // reproduced one level down. Everything reached here is under a ServeRaw parent by
            // construction, and ExpandLevelAsync's ServeRaw branch DOES NOT RECURSE — so at depth >= 2
            // no delegate ran, nothing was blanked, and every value in the payload is the root
            // handler's own raw graph whatever the EDM/profile classification of the navigation that
            // names it. MEASURED, cap = 2, GetAll, Author -Books(delegate-less)-> Book
            // -Chapters(DELEGATE)-> : `?$expand=Books($expand=Chapters)` served five chapters with the
            // Chapters delegate invoked ZERO times. Classifying those rows as "the delegate's answer"
            // and exempting them cited a delegate that never ran. The same holds for a Blank-
            // classified navigation down here: nothing blanked it either, so its rows are served raw.
            // So: check and descend regardless of classification.
            if (depth == 1 && ResolveNavTreatment(edmName, rootSources).Treatment != NavTreatment.ServeRaw)
            {
                continue;
            }

            PropertyInfo? navClr = levelClrType is null
                ? null
                : ODataPropertyNaming.FindClrPropertyByEdmName(levelClrType, edmName);
            string jsonKey = ResolveNavigationJsonKey(navClr?.Name ?? edmName, navClr, serializerOptions);

            bool descend = item.SelectAndExpand is { } nestedClause &&
                           nestedClause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any();

            // #466 + #463: a $levels recursion serves the SAME navigation N levels down, and since
            // #466 it does so on the raw substrate too. Its deeper levels carry no clause item of
            // their own (the recursion is implicit — the same navigation repeats), so they would be
            // invisible to the clause walk and unbounded, which is #463's hole re-opened along the
            // $levels axis. Walk them explicitly, exactly as ShapeLevelsInJson does for the pushed
            // recursion, through the SAME ResolveLevelsBudget both loaders use (#428).
            int levelsBudget = item.LevelsOption is { } lv
                ? ResolveLevelsBudget(lv.IsMaxLevel, lv.Level, maxExpansionDepth, MaxNestedExpandDepth)
                : 0;
            bool needChildren = descend || levelsBudget > 1;
            List<JsonObject>? children = null;

            foreach (JsonObject obj in levelObjects)
            {
                JsonNode? node = obj[jsonKey];
                if (node is JsonArray arr)
                {
                    if (arr.Count > cap)
                    {
                        throw RawExpandCeilingBreach(
                            jsonKey, cap, entitySetName, singleEntityRead, pathPrefix + edmName + pathSuffix);
                    }
                    if (needChildren) (children ??= new List<JsonObject>()).AddRange(arr.OfType<JsonObject>());
                }
                else if (needChildren && node is JsonObject one)
                {
                    // A single-valued navigation holds at most one related entity, so there is
                    // nothing to bound here — but its own children may be collections.
                    (children ??= new List<JsonObject>()).Add(one);
                }
            }

            // Levels 2..N of a $levels recursion: same navigation, same key, same ceiling. Checked
            // per level and top-down, so a breach 400s before the walk descends any further.
            if (levelsBudget > 1 && children is not null)
            {
                IReadOnlyList<JsonObject> levelNodes = children;
                for (int remaining = levelsBudget - 1; remaining >= 1 && levelNodes.Count > 0; remaining--)
                {
                    var deeper = new List<JsonObject>();
                    foreach (JsonObject obj in levelNodes)
                    {
                        JsonNode? node = obj[jsonKey];
                        if (node is JsonArray arr)
                        {
                            if (arr.Count > cap)
                            {
                                throw RawExpandCeilingBreach(
                                    jsonKey, cap, entitySetName, singleEntityRead,
                                    pathPrefix + edmName + pathSuffix);
                            }
                            deeper.AddRange(arr.OfType<JsonObject>());
                        }
                        else if (node is JsonObject one)
                        {
                            deeper.Add(one);
                        }
                    }
                    levelNodes = deeper;
                }
            }

            if (!descend || children is null) continue;

            EnforceRawExpandCeiling(
                children, item.SelectAndExpand,
                // Never read again: the Model B gate above is depth-1-only, so no deeper level
                // resolves a candidate set at all. That is why ResolveRequestSourcesForEdmType is
                // NOT called here — a per-level candidate resolution whose answer is discarded would
                // be per-request cost buying a decision this walk deliberately does not make.
                rootSources,
                NavElementClrType(navClr),
                engaged: null, // unreachable otherwise: this level was not engaged, so no child of it is
                serializerOptions, cap, maxExpansionDepth,
                entitySetName, singleEntityRead,
                pathPrefix + edmName + "($expand=", ")" + pathSuffix, depth + 1);
        }
    }

    // #463/#464: is this navigation covered by the pushdown's own JSON shaping pass at this level?
    // Matched on the binding's EDM name, exactly as CollectPushedLevelsNavNames records one.
    private static bool IsEngagedNav(IReadOnlyList<EngagedExpand>? engaged, string edmName)
    {
        if (engaged is null) return false;
        foreach (EngagedExpand e in engaged)
        {
            if (string.Equals(
                    ODataPropertyNaming.ResolveEdmName(e.Binding.Property), edmName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // #418/#463/#464: the two remediation messages.
    //
    // The single-entity arm is #418's, byte-identical: it is asserted in tests and quoted in docs,
    // and at depth 1 <paramref name="expandPath"/> is exactly the navigation's EDM name — which is
    // what that message always interpolated. Deeper levels get the whole path back
    // ("Books($expand=Chapters)"), so the suggested collection-route request is the SAME request the
    // client actually made rather than a truncation of it.
    //
    // Neither arm is EnsureWithinExpandCeiling's. That one ends "Narrow it with a nested $filter",
    // which is actively false advice for a raw-served expansion: a nested $filter is one of the
    // options this substrate silently ignores, so following it returns the same 400.
    private static Microsoft.OData.ODataException RawExpandCeilingBreach(
        string jsonKey, int cap, string entitySetName, bool singleEntityRead, string expandPath) =>
        singleEntityRead
            ? new Microsoft.OData.ODataException(
                $"The nested '$expand' on '{jsonKey}' cannot be served from a single-entity read: the " +
                $"related collection exceeds the maximum of {cap} entities. A single-entity read " +
                "applies no nested $filter/$orderby/$top window and cannot page an expanded " +
                $"collection, so request it through the collection route instead — e.g. " +
                $"GET /{entitySetName}?$filter=<key eq …>&$expand={expandPath}.")
            : new Microsoft.OData.ODataException(
                $"The nested '$expand' on '{jsonKey}' cannot be served: the related collection " +
                $"exceeds the maximum of {cap} entities. This expansion was not pushed down to the " +
                "data source, so no nested $filter/$orderby/$top window is applied to it and it " +
                "cannot be paged. Narrow the related data where it is loaded, raise MaxExpandTop, or " +
                "make the navigation pushdown-eligible (declared without a delegate, over an EF Core " +
                "IQueryable).");

    // #206 phase 2 (optioned expand) / #254: emit <c>Nav@odata.count</c> for one pushed collection
    // expand and apply its count-deferred $skip/$top window.
    //
    // OData §11.2.4.2 requires the emitted count to be the FULL filtered collection, not the page —
    // which is exactly why the #254 ceiling breach is a 400 rather than a silent truncation: the
    // materialization was bounded to MaxExpandTop + 1 rows in SQL (ApplyNavShape), so seeing more than
    // MaxExpandTop rows here means the true count is unknowable within the configured budget.
    private static void WriteNestedCountAndWindow(
        JsonObject parent, string key, JsonArray arr, EngagedExpand e, int? maxExpandTop)
    {
        EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$count'");

        // Count reflects the full filtered collection (paging was deferred to here).
        parent[$"{key}@odata.count"] = arr.Count;
        ApplyNestedWindow(arr, e);
    }

    // #304: a collection level with children carrying $skip/$top but NO $count (ApplyNavShape composed
    // no SQL Skip/Take at all for this shape — see its isProjectionLeaf gate) — apply the deferred
    // window here, in the JSON pass, BEFORE the caller recurses into children, so only the surviving
    // (windowed) parents are shaped further. Enforces the same MaxExpandTop ceiling as
    // WriteNestedCountAndWindow (the collection had to be fully materialized to window it here at all),
    // but — unlike that method — never emits <c>@odata.count</c>: no $count was requested on this shape.
    private static void WriteNestedWindowOnly(JsonArray arr, string key, EngagedExpand e, int? maxExpandTop)
    {
        EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$top'/'$skip'");
        ApplyNestedWindow(arr, e);
    }

    // #298/#300: the $skip/$top window shared by the $count case above (WriteNestedCountAndWindow) and
    // the $levels no-$count case (ShapeLevelsInJson) below — split out so there is exactly one place
    // that windows a JsonArray in-place, rather than two copies of the same [skip, end) rebuild.
    private static void ApplyNestedWindow(JsonArray arr, EngagedExpand e)
    {
        int skip = e.Skip is int sk && sk > 0 ? Math.Min(sk, arr.Count) : 0;
        int end = e.Top is int tp ? Math.Min(arr.Count, skip + Math.Max(tp, 0)) : arr.Count;
        if (skip > 0 || end < arr.Count)
        {
            // Rebuild to the [skip, end) window in one O(n) pass (Clear detaches the captured nodes so
            // they can be re-added) rather than repeated RemoveAt(0).
            var window = new List<JsonNode?>(end - skip);
            for (int i = skip; i < end; i++) window.Add(arr[i]);
            arr.Clear();
            foreach (JsonNode? node in window) arr.Add(node);
        }
    }

    // #254 (item 2): apply a $levels expand's nested $count/$select at EVERY level of the recursion.
    // <paramref name="parents"/> are the serialized entities at the current level and
    // <paramref name="remaining"/> the levels still loaded beneath them. Descends the SAME navigation
    // key each time — the $levels recursion re-uses one EngagedExpand rather than a per-level tree.
    //
    // The nested $select strip is applied to a level only AFTER descending into it, so the deeper
    // level's own Nav@odata.count is already written and survives the strip: NestedSelect carries the
    // self-navigation's EDM name (appended at plan time in TryBuildEngagedExpand), which is exactly
    // what KeepUnderSelect keys the "Nav@odata.count" inline-control-information rule off.
    private static void ShapeLevelsInJson(
        IEnumerable<JsonObject> parents, EngagedExpand e, int remaining,
        JsonSerializerOptions serializerOptions, int? maxExpandTop)
    {
        if (remaining < 1) return;

        PropertyInfo prop = e.Binding.Property;
        string key = ResolveNavigationJsonKey(prop.Name, prop, serializerOptions);

        var next = new List<JsonObject>();
        foreach (JsonObject parent in parents)
        {
            JsonNode? node = parent[key];
            if (e.Binding.IsCollection && node is JsonArray arr)
            {
                // #300: $skip/$top on a $levels expand are never pushed to SQL (ApplyNavShape's
                // deferPagingToJson) — they must be windowed here regardless of whether $count also
                // rides along. WriteNestedCountAndWindow does count-emission + windowing when $count
                // is requested; otherwise #316: WriteNestedWindowOnly (not a bare ApplyNestedWindow) so
                // the same MaxExpandTop ceiling is enforced here too — without it, a $levels recursion
                // with $skip/$top and no $count materialized every level's full collection with no
                // bound at all. Same $skip=0 no-op guard as the #304 pushed-expand path above (mirrors
                // ApplyNavShape's `sk > 0`); $top is never guarded on > 0 (a $top=0 window must still
                // collapse to empty, not be skipped).
                if (e.Count) WriteNestedCountAndWindow(parent, key, arr, e, maxExpandTop);
                else if ((e.Skip is int sk && sk > 0) || e.Top is int) WriteNestedWindowOnly(arr, key, e, maxExpandTop);
                // #313: a BARE level (no $count, no $skip/$top of its own — a nested $select alone
                // included) fired NEITHER arm above, so the ceiling was enforced NOWHERE on this path:
                // BuildLevelsNavAccess passes deferPagingToJson: true AND maxExpandTop: null, so
                // ApplyNavShape composes no SQL bound either. That made `Nav($levels=1)` — a
                // spec-equivalent restatement of a bare `$expand=Nav`, byte-identical response and all —
                // a one-parameter bypass of the very ceiling the bare shape is now rejected by. Checked
                // per level, on every level, so a breach 400s before the walk descends any further.
                // Same verb as the bare pushed-expand arm in ShapePushedExpandsInJson, so the two
                // spellings of the same request produce the same message.
                else EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$expand'");
                next.AddRange(arr.OfType<JsonObject>());
            }
            else if (!e.Binding.IsCollection && node is JsonObject one)
            {
                next.Add(one);
            }
        }

        if (next.Count == 0) return;

        ShapeLevelsInJson(next, e, remaining - 1, serializerOptions, maxExpandTop);
        if (e.NestedSelect is not null) StripToSelectedProperties(next, e.NestedSelect);
    }

    // #206 phase 2 (Option A1): builds the startup-time $expand pushdown binding for one
    // DELEGATE-LESS navigation (by CLR property name), or returns null when it is not eligible to
    // be folded into the collection projection. Only navigations declared WITHOUT a custom expand
    // delegate reach this method (the caller filters out every navigation that owns a
    // NavigationRouteDefinition), so provenance — "no delegate exists" — is already established;
    // this method only adds the structural safety checks. A navigation qualifies when it maps to a
    // settable CLR property and, for a collection, whose member type can accept a List&lt;TElement&gt;
    // (the .ToList() the projection emits). #323 (Change B): a navigation back to TModel (a
    // bidirectional relationship) is EXCLUDED only when the element type is also NOT member-init-
    // projectable — a projectable element type is always materialized through BuildShapedNavAccess's
    // fresh-POCO member-init (Change A), which structurally cannot close a parent&lt;-&gt;child object
    // cycle regardless of what navigations it declares, so the guard is unnecessary (and wrongly
    // conservative) there. An un-projectable element type keeps today's conservative defer. Everything
    // else stays EDM-only.
    private static ExpandNavBinding? BuildExpandNavBinding<TModel>(string navPropertyName, IEdmModel model) =>
        BuildExpandNavBinding(typeof(TModel), navPropertyName, model);

    // #206 phase 2 (Option A1 / multi-level): non-generic core — build the pushdown binding for a
    // navigation <paramref name="navPropertyName"/> declared on <paramref name="ownerType"/> (the
    // root model at the top level, or a nested element type when recursing), or null when it is not
    // eligible. #323 (Change B): the back-reference guard is narrowed to the un-projectable residue —
    // checked against the OWNER at this level, so a nested nav that navigates back to its own parent
    // (a bidirectional relationship) is excluded exactly as at the root, but ONLY when its element
    // type cannot be member-init-projected (see the remarks above).
    private static ExpandNavBinding? BuildExpandNavBinding(Type ownerType, string navPropertyName, IEdmModel model)
    {
        // #253 completion: navPropertyName is the EDM (JSON) navigation name — a [JsonPropertyName]-
        // renamed nav arrives here as its JSON name (from NavigationPropertyNames or the parser's
        // resolved identifier), so map JSON→CLR to reach the actual CLR member EF Include needs.
        PropertyInfo? navProp = ODataPropertyNaming.FindClrPropertyByEdmName(ownerType, navPropertyName);
        if (navProp is null || navProp.SetMethod is not { IsPublic: true }) return null;

        Type? elementType = NavElementClrType(navProp);
        if (elementType is null) return null;

        // #323 (Change B): a member-init-projectable element type is always materialized through a
        // fresh POCO (Change A in BuildShapedNavAccess), never the bare EF-tracked entity, so a
        // navigation back to ownerType can no longer close a serialization cycle on this path — only
        // an UN-projectable element type still risks materializing the bare (potentially cyclic)
        // related entity, so only that residue keeps the conservative defer.
        if (!IsMemberInitProjectable(elementType, model) && TypeHasNavigationTo(elementType, ownerType))
        {
            return null; // cyclic AND un-projectable — stays EDM-only
        }

        // NavElementClrType returns the property type itself for a single-valued reference and the
        // element type for a collection, so "element differs from property" identifies a collection.
        bool isCollection = navProp.PropertyType != elementType;

        if (isCollection &&
            !navProp.PropertyType.IsAssignableFrom(typeof(List<>).MakeGenericType(elementType)))
        {
            return null; // e.g. an array-typed collection nav; a List<T> cannot be assigned to it
        }

        return new ExpandNavBinding(navProp, isCollection, elementType);
    }

    // #206 phase 2 ($levels): build the pushdown binding for a SELF-REFERENTIAL navigation targeted by
    // $levels=N — the only shape the OData parser accepts $levels on. Unlike BuildExpandNavBinding this
    // deliberately allows the (inherently cyclic) self-reference: the $levels projection recurses a
    // BOUNDED number of times into FRESH member-init POCOs (each level's deeper nav is loaded then
    // emptied at the leaf), so no parent<->child object cycle can form for System.Text.Json. Requires
    // the navigation's element type to equal the owner type (a true recursive hierarchy) and, for a
    // collection, a List-assignable member. Returns null (→ pushdown skipped, nav stays EDM-only) for a
    // route-backed nav (checked by the caller), a non-self-referential target, or an unsettable property.
    private static ExpandNavBinding? BuildLevelsNavBinding(Type ownerType, string navPropertyName)
    {
        // #253 completion: navPropertyName is the EDM (JSON) navigation name — map JSON→CLR so a
        // renamed self-referential nav still resolves to its CLR member for the $levels projection.
        PropertyInfo? navProp = ODataPropertyNaming.FindClrPropertyByEdmName(ownerType, navPropertyName);
        if (navProp is null || navProp.SetMethod is not { IsPublic: true }) return null;

        Type? elementType = NavElementClrType(navProp);
        if (elementType is null || elementType != ownerType) return null; // $levels needs a true self-reference

        bool isCollection = navProp.PropertyType != elementType;
        if (isCollection &&
            !navProp.PropertyType.IsAssignableFrom(typeof(List<>).MakeGenericType(elementType)))
        {
            return null;
        }

        return new ExpandNavBinding(navProp, isCollection, elementType);
    }

    // #206 phase 2: true when <paramref name="type"/> declares a public property that navigates
    // back to <paramref name="target"/> (or a base/interface in target's hierarchy) — i.e. a
    // navigation that would close a serialization cycle IF the related entity were ever handed to
    // the serializer bare (untransformed by a fresh-POCO projection). The assignability check is
    // intentionally broadened in BOTH directions on the property type AND the collection element
    // type (adversarial-review hardening): a back-reference need not be the exact TModel — a base
    // class or interface that TModel implements (or that is assignable from TModel) also closes a
    // cycle. Over-matching here only forces a safe fallback, never incorrect data, so the
    // conservative direction is correct. Implementation UNCHANGED by #323 — only its callers'
    // interpretation of a `true` result changed: BuildExpandNavBinding's guard (Change B) consults it
    // only for an element type that is NOT member-init-projectable (a projectable type is always
    // materialized through a fresh POCO — Change A — so a back-reference there can no longer close a
    // cycle) and is still load-bearing for THAT class post-#325/#326 (belt-and-suspenders alongside
    // SerializeBounded, and still the only thing that decides pushdown ELIGIBILITY in the first
    // place — SerializeBounded only makes the RESULT safe to serialize once a shape is pushed). The
    // #305 Include fallback's OWN former use of this method (FindCyclicLeafExpand, Change C) was
    // REMOVED by #325/#326 (Option B) — see the removal note below FindNestedExpandOrLevels,
    // immediately preceding ApplyIncludeFallback.
    private static bool TypeHasNavigationTo(Type type, Type target)
    {
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (target.IsAssignableFrom(p.PropertyType) || p.PropertyType.IsAssignableFrom(target)) return true;
            Type? elem = NavElementClrType(p);
            if (elem is not null && elem != p.PropertyType &&
                (target.IsAssignableFrom(elem) || elem.IsAssignableFrom(target)))
            {
                return true;
            }
        }
        return false;
    }

    // #206 phase 2: expand pushdown reads related rows through the LINQ provider (a projection with
    // .ToList()), which only actually loads navigation data for an EF Core provider. On a
    // LINQ-to-objects (or any non-EF) provider the same projection would read un-populated CLR
    // navigations and return empty/null data, so pushdown is gated to EF Core queryables and every
    // other provider takes the (delegate-less → EDM-only) fallback path.
    //
    // #305 fold-in: also doubles as the EF Core assembly resolver for the Path A Include fallback
    // below — the caller gates entry on the result being non-null (EF Core-backed) and reuses the
    // SAME resolved assembly to locate EntityFrameworkQueryableExtensions.Include by REFLECTION: this
    // package has no compile-time dependency on Microsoft.EntityFrameworkCore, so the Include-fallback
    // machinery must locate EF Core's own extension methods off whatever assembly the host app
    // actually loaded, never a `using Microsoft.EntityFrameworkCore;` reference. Previously this walk
    // ran twice per request (once as a bool-returning gate, once again to fetch the assembly); now it
    // runs once and the result is threaded through.
    private static Assembly? ResolveEfCoreAssembly(IQueryable query)
    {
        for (Type? t = query.Provider.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Namespace is { } ns &&
                ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                return t.Assembly;
            }
        }
        return null;
    }

    // #305 Path A: reflection handle for EF Core's
    // Include&lt;TEntity,TProperty&gt;(IQueryable&lt;TEntity&gt;, Expression&lt;Func&lt;TEntity,TProperty&gt;&gt;)
    // — the two-generic-parameter, lambda-based overload (EF Core also exposes a one-generic-parameter
    // STRING-path overload, excluded here by generic-arity). Cached per assembly: GetMethods()/LINQ
    // filtering is not free, and this resolves on the hot GetQueryable pushdown path whenever the root
    // projection is ineligible (see ApplyIncludeFallback).
    private static readonly ConcurrentDictionary<Assembly, MethodInfo?> s_efIncludeMethodCache = new();

    private static MethodInfo? ResolveEfIncludeMethod(Assembly efAssembly) =>
        s_efIncludeMethodCache.GetOrAdd(efAssembly, static asm =>
        {
            Type? ext = asm.GetType("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
            return ext?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Include" && m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.IsGenericType &&
                    m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IQueryable<>));
        });

    // #305 Path A: true when ANY engaged expand — at any depth, including deferred nested children —
    // carries a nested $filter/$orderby. Those are SQL-only (bound and composed by
    // BindNavShape/ApplyNavShape/BuildShapedNavAccess onto the member-init projection); once the root
    // projection is ineligible there is no member-init to compose them onto, and a plain EF Include
    // cannot carry a predicate/ordering at all — so the caller fails loud instead of silently serving
    // the navigation unfiltered/unsorted (which would be exactly the kind of wrong-data-under-200 #305
    // reports, just for a different reason than the original silent-drop).
    private static bool HasNestedFilterOrOrderBy(IReadOnlyList<EngagedExpand> engaged)
    {
        foreach (EngagedExpand e in engaged)
        {
            if (e.Filter is not null || e.OrderBy is not null) return true;
            if (e.Children is { Count: > 0 } && HasNestedFilterOrOrderBy(e.Children)) return true;
        }
        return false;
    }

    // #305 fold-in (review): the FIRST top-level engaged expand that carries a nested $expand or
    // $levels — the scope ApplyIncludeFallback below does not serve (see its remarks). Checked by the
    // caller BEFORE invoking ApplyIncludeFallback, and OUTSIDE the try/catch that wraps the actual
    // Include construction+execution, so this validation's specific/actionable ODataException message
    // reaches the client verbatim (via the route's outer ODataException handler) instead of being
    // caught and overwritten by the generic provider-failure catch around the real Include call.
    private static EngagedExpand? FindNestedExpandOrLevels(IReadOnlyList<EngagedExpand> engaged)
    {
        foreach (EngagedExpand e in engaged)
        {
            if (e.Children is { Count: > 0 } || e.Levels > 0) return e;
        }
        return null;
    }

    // #323 (Change C) formerly guarded ApplyIncludeFallback below with a FindCyclicLeafExpand check
    // that rejected (400) any leaf whose element type navigated back to the root model — Include
    // populates TRACKED entities, so EF's own relationship fixup can wire up a back-reference the
    // member-init projection path's Change A structurally forecloses. #325/#326 (OWNER DECISIONS,
    // FROZEN spec — Option B) REMOVED that guard: the same clause-bounded serialization walker
    // (SerializeBounded) that fixes #325's plain-GET tracked-entity cycle makes the Include
    // fallback's tracked-entity graph safe to serve too, regardless of which two instances the
    // cycle closes between — a back-reference to the root (what Change C caught), a sibling
    // cross-reference, or a self-referential leaf element type (#326's two previously-still-500
    // classes) are all now served correctly. Rejecting a request the framework can now answer
    // would be backwards relative to the #305/#323 "serve, don't silently drop or reject" direction.
    // See IncludeFallbackSqliteTests.cs's IncludeFallbackCyclicLeafTests for the coverage (flipped
    // from asserting 400 to asserting real served data).

    // #305 Path A: populate the request's engaged $expand navigations via EF Core's own Include when
    // the root TModel projection is ineligible for a member-init Select (TryApplySelectProjection
    // returned <paramref name="query"/> unchanged — no parameterless ctor / unknowable ETag / complex-or-
    // unsettable structural member). Before #305 this dropped the navigations to EDM-only under a 200
    // (the nav then serialized whatever the CLR property's default value was — typically an empty
    // collection — silently wrong data). Resolved by reflection off the SAME EF Core assembly
    // ResolveEfCoreAssembly already confirmed the query runs against.
    //
    // MaxExpandTop bounds materialization exactly like the member-init path: ApplyNavShape composes the
    // same Skip/Take/count-bound (Take(cap+1)) windowing with the same deterministic tiebreak ordering —
    // reused as-is here, never a "load all then trim".
    //
    // SCOPE (documented deviation from the fully general settled design — see the PR/report): only LEAF
    // engaged expands (no nested $expand children, no $levels) are served this way; the caller has
    // already rejected any nested $filter/$orderby (HasNestedFilterOrOrderBy) and any nested $expand/
    // $levels (FindNestedExpandOrLevels) before calling this method, so no such item reaches the loop
    // below. A nested $expand or $levels fails loud rather than risk an unverified reflection-built
    // ThenInclude/self-referential Include chain — untested by this fix's settled-design test list, and
    // materially riskier than the member-init path: EF's automatic navigation-fixup can wire up a
    // tracked self-referential navigation beyond the requested depth even when it was never explicitly
    // Include'd, which the member-init projection (fresh POCOs, not EF-tracked) never risks.
    // #325/#326 (Option B): a bidirectional/cyclic back-reference (what #323's Change C used to reject
    // here) is now served — SerializeBounded makes the Include fallback's tracked-entity graph safe to
    // serialize regardless of which two instances a cycle closes between (see the removal note just
    // above this method, where Change C's guard used to live).
    private static IQueryable<TModel> ApplyIncludeFallback<TModel>(
        IQueryable<TModel> query, IReadOnlyList<EngagedExpand> engaged, MethodInfo includeMethod,
        IEdmModel model, int? maxExpandTop)
        where TModel : class
    {
        foreach (EngagedExpand e in engaged)
        {
            ParameterExpression owner = Expression.Parameter(typeof(TModel), "x");
            Expression access = Expression.Property(owner, e.Binding.Property);
            if (e.Binding.IsCollection)
            {
                // Filter/OrderBy are verified absent by the caller (HasNestedFilterOrOrderBy), so this
                // reduces to exactly the Skip/Take/count-bound windowing ApplyNavShape composes on the
                // (eligible) member-init projection path.
                access = ApplyNavShape(access, e, e.Binding.ElementType, model, default, maxExpandTop);
            }

            LambdaExpression lambda = Expression.Lambda(access, owner);
            MethodInfo closedInclude = includeMethod.MakeGenericMethod(typeof(TModel), access.Type);
            try
            {
                query = (IQueryable<TModel>)closedInclude.Invoke(null, new object?[] { query, lambda })!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                // Unwrap: MethodInfo.Invoke always wraps the callee's own exception. The caller's catch
                // narrows on the callee's REAL exception type (InvalidOperationException/
                // NotSupportedException/ODataException), so it must see that type, not this wrapper.
                throw tie.InnerException;
            }
        }
        return query;
    }

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
            if (navItemType is not null && !ODataPropertyNaming.IsKnownEdmName(navItemType, propName))
            {
                return (null, ODataError(400, "InvalidQueryOption",
                    $"Property '{propName}' does not exist on type '{navItemType.Name}'."));
            }
            PropertyInfo? prop = navItemType is null
                ? null
                : ODataPropertyNaming.FindClrPropertyByEdmName(navItemType, propName);

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
            json.Add(SerializeBounded(item, navElementEdmType, edmModel, clause: null, navSerializerOptions));
        }
        // #184: navItemType is the CLR element type, so [JsonPropertyName] renames on its
        // navigations are honored when computing which keys to omit. Defence-in-depth (#325/#326):
        // a practical no-op now that SerializeBounded never wrote an un-expanded navigation, kept
        // in case a future caller ever hands this a clause again without checking.
        OmitUnexpandedNavigations(json, navElementEdmType, clause: null, navItemType, navSerializerOptions);

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
        var serialized = (JsonObject)SerializeBounded(entity, omitNavsForType, edmModel, clause: null, jsonOptions)!;
        string baseUrl = BuildBaseUrl(ctx, prefix);

        // #176: on single-entity read responses, omit navigation properties that were not
        // $expand'd (there is no $expand here, so every declared navigation is stripped). Callers
        // that must keep the graph inline — deep-insert POST (§11.4.2.2) — pass no type and are
        // unaffected. See OmitUnexpandedNavigations for the spec citation.
        // #184: the concrete entity's CLR type carries [JsonPropertyName] renames on its
        // navigations, so omission keys off the same names the serializer just wrote.
        // #325/#326: defence-in-depth (practical no-op now — SerializeBounded already omitted
        // every un-expanded navigation at the point of serialization).
        OmitUnexpandedNavigations(serialized, omitNavsForType, clause: null, entity.GetType(), jsonOptions);

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

    private static IResult ODataEntityResult(
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
        var cachedValidationSettings = BuildValidationSettings(source);

        // #206: $select projection pushdown — startup-computed eligibility inputs. Member-init
        // needs a public parameterless constructor (positional records have none), and the
        // per-request projection-set assembly matches selected names against the structural
        // properties by name. Names are matched case-insensitively (EDM identifiers); a model
        // whose structural properties differ only by case makes that lookup ambiguous, so such
        // a profile is pushdown-ineligible outright rather than crashing the dictionary build.
        bool pushdownCtorOk = typeof(TModel).GetConstructor(Type.EmptyTypes) is not null;
        // #322: the projection's structural member set is EDM-AWARE, and this is the ONLY place the
        // two navigation name spaces are reconciled.
        //
        // source.StructuralProperties is "every public readable CLR property MINUS every
        // PROFILE-DECLARED navigation" — BuildStructuralProperties subtracts only
        // _navigationPropertyNames (HasOptional/HasRequired/HasMany). A navigation the
        // ODataConventionModelBuilder discovered on its own but the profile never declared therefore
        // SURVIVES as a structural property, and carries IsComplex = true because its CLR type is not
        // an OData primitive. TryBuildProjectionInit's complex-member bail then fires for every
        // request whose projection member set contains it — which is every bare $expand (no narrowing
        // $select projects EVERY structural name) and every $select that names the navigation — so
        // $select column pruning AND $expand JOIN pushdown are abandoned for the whole entity set,
        // and a nested $filter/$orderby/$expand becomes the #305 400. The EDM is the authority on
        // what is a navigation; a navigation is not a projectable column, so it is subtracted here.
        //
        // Scope is deliberately THIS dictionary and nothing else. source.NavigationPropertyNames is
        // NOT re-sourced from the EDM: it feeds Model B's DB/DL partitioning (ResolveNavTreatment,
        // #292/#293), whose FROZEN spec has "a candidate that neither routes nor declares the nav has
        // no opinion on it and is ignored" as a load-bearing category. Convention-sourcing it would
        // make every candidate declare every EDM navigation of its type, emptying that category and
        // collapsing the honored-sole-route case (RunDelegate) to Blank — a delegate that no longer
        // runs and data silently replaced by null. The two sets are separable and stay separate;
        // Issue322ModelBClassificationTests pins the classification for the multi-candidate shapes.
        //
        // Both name spaces are EDM names, so the match is exact rather than approximate:
        // StructuralPropertyInfo.Name is ODataPropertyNaming.ResolveEdmName(prop), and
        // OhDataBuilder.ApplyJsonPropertyNameRenames gives every EDM navigation that same
        // [JsonPropertyName]-resolved name (#253). OrdinalIgnoreCase because that is how OData
        // resolves identifiers, matching the dictionary this feeds. NavigationProperties() (not
        // DeclaredNavigationProperties()) so an inherited navigation on a derived entity type is
        // subtracted too — the same enumeration WarnUnboundedBareExpand walks.
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

        // #440 symptom 2: THE SAME SUBTRACTION, applied to the set that drives STRUCTURAL-PROPERTY
        // ROUTE REGISTRATION. source.StructuralProperties is "every public readable CLR property
        // MINUS every PROFILE-DECLARED navigation", so a navigation the ODataConventionModelBuilder
        // discovered but the profile never declared survives in it — and the property-route block
        // far below reads it directly, registering GET /{Set}({key})/{Nav} and its /$value over a
        // NAVIGATION, plus PUT/PATCH/DELETE when a Patch handler is configured. Those writes build a
        // one-property Delta<TModel> over a navigation member; nobody opted into that, and the
        // declared control has no such routes at all (it 404s), so two profiles over the SAME CLR
        // member expose different route tables purely by declaration provenance. The EDM is the
        // authority on what is a navigation, and a navigation is not a structural property.
        //
        // WHY HERE AND NOT IN BuildStructuralProperties:
        //   (a) It cannot go there. BuildStructuralProperties runs from
        //       IVisitModelBuilder.VisitModelBuilder — i.e. WHILE the EDM is being built — so
        //       registration.EdmModel does not exist yet and the EDM's navigation set is not merely
        //       awkward to reach, it has not been computed. EntitySetProfile also deliberately
        //       carries no dependency on a built IEdmModel.
        //   (b) It should not go there even if it could. StructuralProperties is the profile-level
        //       answer to "which CLR members did this profile not declare as navigations", and
        //       narrowing it at the source moves every consumer at once for one route-table defect.
        //       Both remaining production consumers are handled explicitly instead: the projection
        //       member set (#322, subtracted above) and this route block. #313's continuation-route
        //       collision check reads NavigationPropertyNames, not this set, and the companion
        //       OpenAPI/NSwag/Swashbuckle packages do not read StructuralProperties at all — they
        //       read IgnoredPropertyNames through IgnoredPropertyDocsMap plus SchemaPropertyCasing
        //       (verified across all three packages, not assumed), so no generated schema moves.
        //   (c) NavigationPropertyNames is still NOT touched. It feeds Model B's DB/DL partitioning
        //       (ResolveNavTreatment, #292/#293), whose frozen "a candidate that neither routes nor
        //       declares the nav has no opinion" category empties under convention sourcing —
        //       measured collapsing the honored-sole-route case from RunDelegate to Blank.
        //       Issue322ModelBClassificationTests pins that and must stay green.
        //
        // The bound-function collision check below iterates this SAME narrowed set deliberately:
        // with no property route registered for an undeclared navigation there is no (template, GET)
        // pair left to collide with, so throwing at startup would be a validation error describing a
        // route that no longer exists. The declared case is unaffected — a declared navigation was
        // never in StructuralProperties in the first place.
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
            .Select(navName => (navName, binding: BuildExpandNavBinding<TModel>(navName, registration.EdmModel)))
            .Where(pair => pair.binding is not null)
            .ToDictionary(pair => pair.navName, pair => pair.binding!.Value, StringComparer.OrdinalIgnoreCase);

        // #313 stage 5: the navigations of THIS entity set whose bare $expand may page, resolved once
        // at startup by the shared predicate. Empty on the shipping default (ExpandPagingEnabled is
        // false), which is what makes the route table, $metadata and the three OpenAPI documents
        // byte-identical to a registration that never heard of #313.
        IReadOnlyList<ExpandPagingNav> expandPagingNavs =
            ResolveExpandPagingNavigations(source, typeof(TModel), registration);
        // Same set, keyed for the emission site's per-expand lookup. Ordinal-ignore-case to match how
        // every other EDM-name lookup in this file compares identifiers.
        IReadOnlyDictionary<string, ExpandPagingNav> expandPagingNavsByEdmName =
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

        // Layer C applies coarse per-route auth. `keyBased` marks routes carrying a {key} segment, to
        // which Layer B (resource-based) auth attaches a load-by-key filter (see AttachResourceFilter);
        // collection-level routes (no {key}) pass keyBased: false.
        void ApplyOperationAuth(IEndpointConventionBuilder rb, OhDataOperation category, string? boundOperationName = null, bool keyBased = true)
        {
            if (operationAuthRules is null) return; // legacy group-auth path governs instead
            OperationAuthRule? rule = ResolveOperationRule(category, boundOperationName);
            if (rule is null) return; // no rule → inherit any group/global auth (anonymous if none)
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

            // Named policies apply as separate RequireAuthorization(name) calls (they stack → AND).
            foreach (var req in rule.Requirements.Where(r => r.Kind == AuthRequirementKind.Policy))
            {
                rb.RequireAuthorization(req.Name!);
            }

            // Inline requirements (authenticated/role/claim) replay onto one AuthorizationPolicyBuilder.
            var inlineRequirements = rule.Requirements
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
                    catch (FormatException)
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

        // #525: a named Invoke rule must name a bound operation this profile really declares.
        //
        // The comparer fix above closes the MISCASED spelling, which is the shape that was reported.
        // It cannot close the class: a misspelled name -- the far likelier typo -- still resolves to
        // nothing under any comparer, and the consequence is identical and identically silent (the
        // rule is discarded, the route falls back to the generic Invoke rule or to no requirement at
        // all, and the developer believes the operation is protected). There is no legitimate
        // configuration in which a rule targets an operation that does not exist, so this is refused
        // rather than warned about: an authorization rule that does not apply is not a diagnostic
        // matter.
        //
        // Matched with the SAME comparer ResolveOperationRule uses, and it has to be -- a stricter
        // check here would reject exactly the miscased rules the fix above just made work, which
        // would be this very bug re-introduced one layer up and wearing an exception. Placed BEFORE
        // the #486 GetById guard below (which itself resolves rules by name) so a typo is reported
        // as a typo rather than as a missing GetById handler.
        if (operationAuthRules is not null)
        {
            string[] declaredOperationNames = source.BoundFunctions
                .Concat(source.BoundActions)
                .Select(o => o.Name)
                .ToArray();

            foreach (OperationAuthRule namedRule in operationAuthRules)
            {
                if (namedRule.BoundOperationName is null) continue;
                if (declaredOperationNames.Any(declared => string.Equals(
                        declared, namedRule.BoundOperationName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

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
        // COLLECTION-level members of those two categories are deliberately excluded and are not an
        // oversight -- the collection POST evaluates its Create requirement inline against the
        // deserialized model (never through GetById), and a collection-bound operation's route has
        // no {key} segment for the filter to read, so both are legal without GetById.
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
                    "loads the entity by key before the operation runs. Add a GetById handler, or scope " +
                    "the requirement to collection-bound operations only.");
            }
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
            var collReadP1Rb = entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    IResult? capabilityError = CheckCollectionQueryOptionCapabilities(ctx, source);
                    if (capabilityError is not null) return capabilityError;

                    var s = ResolveHandlers(ctx);
                    var odataSrc = (IODataEntitySetEndpointSource)s;
                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }
                    // B1 fix: enforce FilterProperties/OrderByProperties/SelectProperties/
                    // ExpandProperties allowlists before handing options to the profile — the
                    // profile's own ApplyTo call has no opportunity to reject a disallowed
                    // property since it never calls Validate() itself.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
                    // #254: reject a nested $top above MaxExpandTop at any depth.
                    IResult? nestedTopError = ValidateNestedTopCeiling(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                    if (nestedTopError is not null) return nestedTopError;
                    // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                    // level. Depth alone does not bound translation cost; breadth multiplies on top.
                    IResult? breadthError = ValidateExpandBreadth(
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

                    var odataResult = await odataSrc.InvokeGetODataQueryableAsync(options, ct);
                    var queryable = odataResult.Items is IQueryable<TModel> typedQ
                        ? typedQ
                        : odataResult.Items.Cast<TModel>().AsQueryable();

                    // #195: framework-side safety cap. The profile owns query application, but if it
                    // does not page the result itself (no NextLink) and the client did not cap with
                    // $top, bound the materialized set to MaxTop (or a smaller Prefer: maxpagesize)
                    // and emit a continuation nextLink — so a Priority-1 profile can never be coerced
                    // into returning an unbounded result set. When the profile supplies its own
                    // NextLink it is trusted to have paged; when $top is present the client has capped
                    // explicitly; neither case caps again.
                    //
                    // #360: the continuation offset is carried in the framework-private
                    // FrameworkSkipOption custom query option and applied HERE, by the framework, on
                    // top of whatever the profile's own ApplyTo did — it is neither $skip (which the
                    // framework emitted but never applied, so a profile that ignores the incoming
                    // options served the same page forever and a nextLink walk never terminated) nor
                    // $skiptoken (which ApplyTo throws on). See BuildFrameworkSkipLink.
                    //
                    // #244: the framework deliberately does NOT inject a stabilizing order before this
                    // cap Take — unlike the Priority-2 path, where the framework owns skip/take and can
                    // order every page consistently. Here the profile owns its whole pipeline via
                    // ApplyTo, including any $skip, so the framework can't order safely: ordering after
                    // the profile's own Skip would sort a sliced subset, and ordering only the first
                    // (unskipped) page would misalign the continuation offset. Deterministic
                    // @odata.nextLink paging on this path is therefore the profile's responsibility — it
                    // must establish a stable order (a terminal OrderBy, or applying the client's
                    // $orderby). EF Core already surfaces the omission: warning 10102 fires when a query
                    // is skip/take'd without an ORDER BY. See docs/query-options.md.
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
                            ctx.Response.Headers["Preference-Applied"] = $"maxpagesize={appliedPageSize!.Value}";
                    }

                    object[] items = EvaluateQueryWithArithmeticFaultGuard(
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

                    var (finalItems, selectedProps) = await ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix(name, selectedProps)}";
                    // $count=true: prefer TotalCount if profile provided it (pre-paging), otherwise
                    // fall back to items.Length (post-paging).
                    if (options.Count?.Value == true)
                    {
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
                catch (FilterArithmeticFaultException ex)
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
              .WithTags(name).Produces<ODataCollectionResponse<TModel>>(200).Produces(400)
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
                    IResult? capabilityError = CheckCollectionQueryOptionCapabilities(ctx, source);
                    if (capabilityError is not null) return capabilityError;

                    var s = ResolveHandlers(ctx);
                    var queryable = (IQueryable<TModel>)(await s.InvokeGetQueryableAsync(ct))
                                    .Cast<TModel>();

                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }
                    // B1 fix: enforce FilterProperties/OrderByProperties/SelectProperties/
                    // ExpandProperties allowlists before any ApplyTo call below.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
                    // #254: reject a nested $top above MaxExpandTop at any depth.
                    IResult? nestedTopError = ValidateNestedTopCeiling(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                    if (nestedTopError is not null) return nestedTopError;
                    // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                    // level. Depth alone does not bound translation cost; breadth multiplies on top.
                    IResult? breadthError = ValidateExpandBreadth(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                    if (breadthError is not null) return breadthError;

                    // Gap 4: $search on GetQueryable path — delegate to the Search handler, then
                    // apply remaining OData query options on top of the in-memory result set.
                    if (ctx.Request.Query.TryGetValue("$search", out var searchTermQ))
                    {
                        if (!source.HasSearch)
                        {
                            return ODataError(400, "UnsupportedQueryOption",
                                "This resource does not support $search. Configure the Search handler to enable it.");
                        }

                        var searchResults = await s.InvokeSearchAsync(searchTermQ.ToString(), ct);
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
                        odataCount = EvaluateQueryWithArithmeticFaultGuard(
                            () => countQ.LongCount(), options, logger, source.EntitySetName);
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
                            ctx.Response.Headers["Preference-Applied"] = $"maxpagesize={appliedPageSize!.Value}";
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
                        ExtractSelectedProperties(selClause) is { } selNames
                            ? TryApplySelectProjection(q, selNames, source, pushdownCtorOk, pushdownStructuralByName, logger)
                            : q;

                    // #206 phase 2: $expand Include pushdown, now MULTI-LEVEL. Fold the eligible top-level
                    // $expand navigations of this request — those declared WITHOUT a delegate
                    // (pushdownExpandNavs), so there is no delegate to bypass — into the SAME member-init
                    // projection so a single EF Core query loads the related rows via a JOIN, recursing
                    // into each nested $expand as an element-wise projection (EF ThenInclude) when the
                    // deeper navigations are ALSO delegate-less and pushable at that level. Nested options
                    // at every level ($filter/$orderby/$top/$skip/$count/$select) are honored:
                    // filter/orderby/paging push to SQL (BuildShapedNavAccess), count/select apply on the
                    // serialized JSON (ShapePushedExpandsInJson). $levels=N / $levels=max on a
                    // self-referential nav recurse a BOUNDED (cycle-free) projection N deep, capped at the
                    // resolved MaxExpansionDepth. A navigation declared WITH a delegate is never in
                    // pushdownExpandNavs and is skipped for $levels too (routeBackedNavNames), so it always
                    // takes the delegate expansion path (Stage 3) — the delegate-safety invariant holds at
                    // EVERY depth: a branch is pushed only when it is delegate-less end-to-end, otherwise
                    // TryBuildEngagedExpand defers the whole branch off pushdown. Gated to EF Core-backed
                    // sources (a projection reading un-populated navigations would be wrong elsewhere).
                    // Anything deferred (non-EF, a delegate-backed/cyclic level, $search/$compute/$apply,
                    // a $levels carrying extra options) or that fails (projection/translation/serialization
                    // cycle, unbindable clause) falls back: the navigation then stays EDM-only for this
                    // request, exactly as before pushdown existed.
                    //
                    // #305: deliberately NOT gated on pushdownCtorOk (unlike ApplySelectPushdown above).
                    // Before #305, a root TModel without a parameterless ctor never even reached this
                    // point — the projection would be ineligible anyway, so computing engagedExpandNavs
                    // was pure waste. Now it feeds the Path A Include fallback below (ApplyIncludeFallback)
                    // when TryApplySelectProjection turns out ineligible, so it must be computed
                    // regardless of ctor eligibility; TryApplySelectProjection itself still bails
                    // immediately on !hasParameterlessCtor (see its own hasParameterlessCtor check), so
                    // this costs nothing extra for the ctor-eligible case.
                    List<EngagedExpand>? engagedExpandNavs = null;
                    // #305 fold-in: resolve the EF Core assembly ONCE here (short-circuited exactly like
                    // the old bool-returning IsEfCoreBacked gate it replaces) and reuse it below at the
                    // Path A Include-fallback call site instead of re-walking query.Provider a second time.
                    Assembly? efAssembly = null;
                    if (source.ExpandPushdownEnabled &&
                        pushdownNamesUnambiguous &&
                        options.SelectExpand?.SelectExpandClause is { } expandPlanClause &&
                        (efAssembly = ResolveEfCoreAssembly(filtered)) is not null)
                    {
                        foreach (ExpandedNavigationSelectItem expandItem in
                                 expandPlanClause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
                        {
                            string navName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;

                            // #206 ($levels): a $levels self-referential nav is excluded from
                            // pushdownExpandNavs (it is inherently cyclic), but a BOUNDED $levels
                            // projection is cycle-free, so resolve its binding on the fly here — skipping
                            // any delegate-backed nav (routeBackedNavNames) so its delegate is never bypassed.
                            ExpandNavBinding binding;
                            if (expandItem.LevelsOption is not null)
                            {
                                if (routeBackedNavNames.Contains(navName)) continue; // delegate-backed → delegate path
                                if (BuildLevelsNavBinding(typeof(TModel), navName) is not { } lb) continue;
                                binding = lb;
                            }
                            else if (!pushdownExpandNavs.TryGetValue(navName, out binding))
                            {
                                continue; // delegate-backed or non-pushable top-level nav → delegate/EDM path
                            }

                            if (TryBuildEngagedExpand(expandItem, binding, registration.EdmModel, registration,
                                    source.MaxExpansionDepth, out EngagedExpand engaged))
                            {
                                (engagedExpandNavs ??= new List<EngagedExpand>()).Add(engaged);
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

                    // #334: which top-level engaged expands get their Nav@odata.count carried as an
                    // independent correlated scalar subquery, so the nested $skip/$top can bound the
                    // SQL fetch instead of being deferred to the JSON pass?
                    //
                    //  - a COLLECTION nav (a reference has no count),
                    //  - carrying $count,
                    //  - a projection LEAF (no nested $expand children of its own) — a level with
                    //    children is projected element-wise further down, and windowing it while
                    //    also projecting a collection out of each element is the APPLY/LATERAL shape
                    //    #298/#304 established SQLite cannot translate,
                    //  - not a $levels recursion (same reason, per #300),
                    //  - and carrying an actual nested $skip/$top WINDOW. Without one there is
                    //    nothing to bound: the pre-#334 path already fetched exactly what the
                    //    ceiling allowed, so engaging the carrier would add a count subquery for no
                    //    benefit. Gating on the window is what keeps every un-windowed counted
                    //    request byte-identical AND cost-identical to before.
                    //
                    // A counted nav at depth >= 2 is excluded by construction, not by a check here:
                    // countViaCarrier is only ever passed to the TOP-level BuildShapedNavAccess call
                    // (TryBuildProjectionInit), never into a nested BuildMemberInit.
                    List<EngagedExpand>? carrierCounted = null;
                    if (engagedExpandNavs is { Count: > 0 })
                    {
                        foreach (EngagedExpand ce in engagedExpandNavs)
                        {
                            if (ce.Levels == 0 && ce.Binding.IsCollection && ce.Count
                                && ce.Children is not { Count: > 0 }
                                && (ce.Top is int || (ce.Skip is int cskip && cskip > 0)))
                            {
                                (carrierCounted ??= new List<EngagedExpand>()).Add(ce);
                            }
                        }
                        // More counted+windowed navs than the carrier has slots: fall back wholesale
                        // rather than carrying some counts and deferring others, so one request
                        // never mixes the two count sources.
                        if (carrierCounted is { Count: > ExpandCountCarrierSlots }) carrierCounted = null;
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
                            ExtractSelectedProperties(combClause) is { } combSelected
                                ? combSelected
                                : pushdownStructuralByName.Keys.ToList();

                        // #334: try the count-carrier projection FIRST. A null means either that no
                        // nav qualified, or that the root projection is ineligible for a member-init
                        // Select at all — in which case the request falls through to the unchanged
                        // path below (including, ultimately, the #305 Include fallback).
                        IQueryable<ExpandCountCarrier<TModel>>? carrierQuery =
                            carrierCounted is { Count: > 0 }
                                ? TryApplyCarrierProjection(
                                    filtered, structuralNames, source, pushdownCtorOk,
                                    pushdownStructuralByName, logger, engagedExpandNavs,
                                    registration.EdmModel, cachedBinderSettings, carrierCounted)
                                : null;

                        // #322: why the projection was ineligible, reported BY the eligibility checks
                        // rather than re-derived at the 400 below. Stays null on the success path.
                        string? projectionIneligibleReason = null;
                        IQueryable<TModel> pushedQuery = carrierQuery is not null
                            ? filtered // unused on the carrier path; keeps the reference check below false
                            : TryApplySelectProjection(
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
                                ExpandCountCarrier<TModel>[] carriers = EvaluateQueryWithArithmeticFaultGuard(
                                    () => TranslateThenMaterialize(() => carrierQuery), options, logger, source.EntitySetName);

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
                            catch (QueryTranslationFailedException ex)
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
                            // path — see ApplyIncludeFallback), or fail loud (400) when the request needs
                            // something a plain Include cannot carry (a nested $filter/$orderby) or that
                            // this fix does not fold through Include (a nested $expand/$levels).
                            if (HasNestedFilterOrOrderBy(engagedExpandNavs))
                            {
                                // #322: this message used to recite the eligibility RULE — "a public
                                // parameterless constructor, settable non-complex properties, and ... a
                                // direct UseETag selector" — at a developer whose model had all three,
                                // naming nothing that was actually wrong. It now names the ONE check
                                // that failed, as reported by the check itself.
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{source.EntitySetName}' could not be processed: " +
                                    "a nested $filter/$orderby on $expand requires a projection-eligible " +
                                    $"model, and '{typeof(TModel).Name}' is not one because " +
                                    $"{projectionIneligibleReason ?? "its member-init projection could not be built"}. " +
                                    "Fix that, or write an expand delegate for this navigation to take " +
                                    "full control of its query shape.");
                            }

                            // #305 fold-in (review): validated here, OUTSIDE the try/catch around the
                            // actual Include construction+execution below, so this SPECIFIC actionable
                            // message reaches the client via the route's outer ODataException handler
                            // instead of being caught and overwritten by the generic provider-failure
                            // catch that wraps the real Include call.
                            if (FindNestedExpandOrLevels(engagedExpandNavs) is { } nestedNav)
                            {
                                // #322: same correction as the message above — name the check that
                                // actually failed, not the whole rule.
                                throw new Microsoft.OData.ODataException(
                                    $"The '$expand' on '{nestedNav.Binding.Property.Name}' could not be " +
                                    "served without a projection-eligible model: a nested $expand or " +
                                    "$levels under a plain Include fallback is not supported, and " +
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

                            MethodInfo? efInclude = efAssembly is not null ? ResolveEfIncludeMethod(efAssembly) : null;
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
                                items = EvaluateQueryWithArithmeticFaultGuard(
                                    () => TranslateThenMaterialize(() => ApplySelectPushdown(
                                        ApplyIncludeFallback(
                                            filtered, engagedExpandNavs, efInclude, registration.EdmModel,
                                            source.MaxExpandTop))),
                                    options, logger, source.EntitySetName);
                                // engagedExpandNavs stays SET (not nulled): the existing
                                // ShapePushedExpandsInJson pass below shapes nested
                                // $count/$select/$top/$skip exactly as it does for the projection path.
                            }
                            catch (QueryTranslationFailedException ex)
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
                                items = EvaluateQueryWithArithmeticFaultGuard(
                                    () => TranslateThenMaterialize(() => pushedQuery), options, logger, source.EntitySetName);
                            }
                            catch (QueryTranslationFailedException ex)
                            {
                                // FAIL LOUD (owner directive, post-#298/#300 review): a folded $expand
                                // projection that fails to translate at the provider must not silently
                                // degrade to 200 with the affected navigations quietly empty — that was
                                // the exact root cause of #298 ($count + nested $expand) and #300
                                // ($levels + $skip/$top), both now fixed at the source (ApplyNavShape no
                                // longer composes the untranslatable SQL shape for those two cases). Any
                                // OTHER combination this provider still cannot translate is a genuine
                                // capability gap, not something to paper over with missing data — 400,
                                // not a silently-wrong 200. The client-facing message stays generic
                                // (never ex.Message/stack trace, which could leak provider/schema
                                // details) per this file's existing InternalServerError convention (S7).
                                //
                                // #494: what reaches this catch is decided by WHEN the provider threw,
                                // not by what it threw. An earlier revision allowlisted
                                // InvalidOperationException / NotSupportedException / ODataException
                                // around the whole materialization, on the premise that a real
                                // infrastructure fault could only arrive as a DbException subclass or a
                                // TimeoutException. It cannot: SqlClient reports pool exhaustion as a
                                // plain InvalidOperationException from SqlConnection.Open, at
                                // enumeration, inside this exact try; ObjectDisposedException derives
                                // from InvalidOperationException; and EF's "a second operation was
                                // started on this context instance" is one as well. Every one of those
                                // came back as 400 "simplify your query", telling client retry logic not
                                // to retry a fault that is entirely retryable — while the SAME request
                                // without $expand correctly 500'd. TranslateThenMaterialize now splits
                                // the two phases at GetEnumerator/MoveNext, so only a genuine
                                // translation failure lands here and everything else propagates to the
                                // group-level exception filter as a logged 500.
                                //
                                // Logged at Warning (was Debug, invisible at production log levels): a
                                // request this server cannot translate is something the operator wants
                                // to see, and it is the same level TryBuildQueryOptions and BadKeyError
                                // already use for the 400s they raise.
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
                        // No expand pushdown — $select-only path, byte-for-byte unchanged.
                        items = EvaluateQueryWithArithmeticFaultGuard(
                            () => ApplySelectPushdown(filtered).ToArray(), options, logger, source.EntitySetName);
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
                    HashSet<string>? pushedLevelsNavNames = CollectPushedLevelsNavNames(engagedExpandNavs);

                    JsonArray finalItems;
                    List<string>? selectedProps;
                    try
                    {
                        // #464: engagedExpandNavs is threaded in so Stage 3.6's ceiling skips exactly
                        // the navigations ShapePushedExpandsInJson bounds (and, where #313 allows,
                        // pages) below — and bounds every OTHER expanded collection in the response,
                        // which on a non-EF source is all of them.
                        (finalItems, selectedProps) = await ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct, pushedLevelsNavNames, engagedExpandNavs);
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
                        // FindNestedExpandOrLevels, immediately preceding ApplyIncludeFallback). This
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
                        ExpandPagingContext? pagingCtx = null;
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
                                pagingCtx = new ExpandPagingContext(
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

                        ShapePushedExpandsInJson(
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
                catch (FilterArithmeticFaultException ex)
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
              .WithTags(name).Produces<ODataCollectionResponse<TModel>>(200).Produces(400)
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

                    // Leg 1 (docs-fidelity): $filter/$orderby remain structurally unsupported on
                    // this path — GetAll has no ApplyTo/IQueryable pipeline to push them down to.
                    // $top/$skip, by contrast, are pure post-materialization Skip()/Take() — the
                    // same class of operation as the already-live $select/$expand/$count below —
                    // so they are implemented rather than rejected. See docs/query-options.md.
                    if (options.Filter is not null || options.OrderBy is not null)
                    {
                        return ODataError(400, "UnsupportedQueryOption",
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
                    IResult? capabilityError = CheckCollectionQueryOptionCapabilities(ctx, source, checkFilterOrderBy: false);
                    if (capabilityError is not null) return capabilityError;
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
                    // #254: reject a nested $top above MaxExpandTop at any depth. GetAll expands
                    // through delegates only (nested $top is not applied there), but the ceiling is a
                    // statement about what the client may ask for — same as the root MaxTop above.
                    IResult? nestedTopError = ValidateNestedTopCeiling(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                    if (nestedTopError is not null) return nestedTopError;
                    // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                    // level. Depth alone does not bound translation cost; breadth multiplies on top.
                    IResult? breadthError = ValidateExpandBreadth(
                        options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                    if (breadthError is not null) return breadthError;

                    // Post-materialization paging for GetAll, applied AFTER the handler call (GetAll
                    // or Search) fills the array and BEFORE $select/$expand serialization.
                    // @odata.count reflects the PRE-paging total (§11.2.6.5 — unaffected by
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
                                ctx.Response.Headers["Preference-Applied"] = $"maxpagesize={appliedPageSize!.Value}";
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
                            return ODataError(400, "UnsupportedQueryOption",
                                "This resource does not support $search. Configure the Search handler to enable it.");
                        }

                        var searchResults = await s.InvokeSearchAsync(searchTerm.ToString(), ct);
                        object[] searchItems = searchResults.ToArray();
                        var (pagedSearchItems, searchPreTotal, searchNextLink) = ApplyGetAllPaging(searchItems);

                        var (searchFinal, searchSelectedProps) = await ApplyCollectionPipelineAsync(pagedSearchItems, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);
                        string searchBaseUrl = BuildBaseUrl(ctx, prefix);
                        var searchEnvelope = new Dictionary<string, object?>();
                        searchEnvelope["@odata.context"] = $"{searchBaseUrl}/$metadata#{AppendSelectSuffix(name, searchSelectedProps)}";
                        // Batch 5: include @odata.count for search results when $count=true is
                        // requested. Leg 1: reflects the pre-paging total, per §11.2.6.5.
                        if (options.Count?.Value == true)
                            searchEnvelope["@odata.count"] = searchPreTotal;
                        if (searchNextLink is not null)
                            searchEnvelope["@odata.nextLink"] = searchNextLink;
                        searchEnvelope["value"] = searchFinal;
                        return ODataEnvelopeResult(searchEnvelope, jsonOptions);
                    }

                    object? result = await s.InvokeGetAllAsync(ct);
                    var enumerable = result as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    var rawItems = enumerable.ToArray();
                    var (pagedItems, preTotal, nextLink) = ApplyGetAllPaging(rawItems);

                    var (finalItems, selectedProps) = await ApplyCollectionPipelineAsync(pagedItems, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix(name, selectedProps)}";
                    // Batch 5 / Leg 1: §11.2.6.5 — include @odata.count when $count=true is
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
              .WithTags(name).Produces<ODataCollectionResponse<TModel>>(200).Produces(400)
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
                    // B1 fix: $/count's own metadata advertises FilterEnabled: source.FilterEnabled
                    // (the only query option this route actually applies), so enforce it — a
                    // disabled $filter was previously applied unconditionally below.
                    IResult? countCapabilityError = CheckDisabledQueryOption(
                        ctx, "$filter", source.FilterEnabled, nameof(IEntitySetEndpointSource.FilterEnabled));
                    if (countCapabilityError is not null) return countCapabilityError;

                    var s = ResolveHandlers(ctx);
                    // #402: broad-catch-to-400 around exactly the construction. See TryBuildQueryOptions.
                    if (!TryBuildQueryOptions<TModel>(registration.EdmModel, ctx, logger,
                            out ODataQueryOptions<TModel>? options, out IResult? optionsError))
                    {
                        return optionsError;
                    }
                    // B1 fix: enforce the FilterProperties allowlist here too.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);

                    if (s is IODataEntitySetEndpointSource odataCountSrc && odataCountSrc.HasGetODataQueryable)
                    {
                        // Priority 1 profiles apply query options themselves; don't re-apply $filter.
                        var countResult = await odataCountSrc.InvokeGetODataQueryableAsync(options, ct);
                        var queryable = countResult.Items is IQueryable<TModel> tq
                            ? tq
                            : countResult.Items.Cast<TModel>().AsQueryable();
                        long odataQueryableCount = EvaluateQueryWithArithmeticFaultGuard(
                            () => queryable.LongCount(), options, logger, source.EntitySetName);
                        return Results.Content(odataQueryableCount.ToString(), "text/plain");
                    }
                    if (source.HasGetQueryable)
                    {
                        var q = (IQueryable<TModel>)(await s.InvokeGetQueryableAsync(ct)).Cast<TModel>();
                        var filtered = options.Filter is not null
                            ? (IQueryable<TModel>)options.Filter.ApplyTo(q, cachedCountSettings)
                            : q;
                        filtered = ApplyRoundingMode(filtered, source.RoundingMode);
                        long queryableCount = EvaluateQueryWithArithmeticFaultGuard(
                            () => filtered.LongCount(), options, logger, source.EntitySetName);
                        return Results.Content(queryableCount.ToString(), "text/plain");
                    }
                    if (options.Filter is not null)
                    {
                        return ODataError(400, "UnsupportedQueryOption",
                            "$filter is not supported on this resource. Configure GetQueryable to enable server-side filtering.");
                    }

                    var items = await s.InvokeGetAllAsync(ct) as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
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
                catch (FilterArithmeticFaultException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces<long>(200, "text/plain").Produces(400)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  // #467 (F3): the GetAll fallback branch above returns 400 UnsupportedQueryOption
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
                  // envelope to carry an inline @odata.count, and the option is ignored. It used
                  // to be set true to say "this route IS the count", which the OpenAPI
                  // transformers -- the metadata's only consumers -- read as the other meaning
                  // and documented a $count query parameter that does nothing.
                  CountEnabled: false,
                  SearchEnabled: false,
                  MaxTop: null,
                  // #467 (F2): /$count applies neither $top nor $skip. It counts the whole set.
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
                    bool hasSelect = ctx.Request.Query.ContainsKey("$select");
                    bool hasExpand = ctx.Request.Query.ContainsKey("$expand");
                    IResult? selectCapabilityError = CheckDisabledQueryOption(
                        ctx, "$select", source.SelectEnabled, nameof(IEntitySetEndpointSource.SelectEnabled));
                    if (selectCapabilityError is not null) return selectCapabilityError;
                    IResult? expandCapabilityError = CheckDisabledQueryOption(
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
                        IResult? nestedTopError = ValidateNestedTopCeiling(
                            options.SelectExpand?.SelectExpandClause, source.MaxExpandTop);
                        if (nestedTopError is not null) return nestedTopError;
                        // #429: reject a $expand tree wider than MaxExpandBreadth, counted across every
                        // level. Depth alone does not bound translation cost; breadth multiplies on top.
                        IResult? breadthError = ValidateExpandBreadth(
                            options.SelectExpand?.SelectExpandClause, source.MaxExpandBreadth, source.MaxExpansionDepth);
                        if (breadthError is not null) return breadthError;
                        selectedProps = options.SelectExpand?.SelectExpandClause is not null
                            ? ExtractSelectedProperties(options.SelectExpand.SelectExpandClause)
                            : null;
                    }

                    object? result = await s.InvokeGetByIdAsync(parsedKey!, ct);
                    string? etagValue = null;
                    if (result is not null && source.HasETag)
                    {
                        etagValue = s.InvokeGetETag(result);
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
                            await ApplyCollectionPipelineAsync(
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
                catch (FormatException ex)
                {
                    return BadKeyError(logger, ex, key, name);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: false,
                  OrderByEnabled: false,
                  SelectEnabled: source.SelectEnabled,
                  ExpandEnabled: source.ExpandEnabled,
                  CountEnabled: false,
                  SearchEnabled: false,
                  MaxTop: null,
                  // #467 (F2): a single-entity read has nothing to page. $top/$skip are ignored
                  // here (200 with the whole entity), so they must not be documented.
                  TopSkipSupported: false));
            ApplyOperationAuth(rb, OhDataOperation.Read);
        }

        // Nested-graph handling (§32/§11.4.2.2 deep insert, §11.4.3.1 deep update): precomputed
        // once at startup (not per-request) — the set of TModel navigation properties that must
        // not reach a write handler when AllowDeepWrites is disabled (the default). System.Text.Json
        // already binds nested navigation values into these properties during deserialization;
        // withholding them here is what keeps a handler that doesn't expect a graph from silently
        // persisting only part of one.
        //
        // WHAT THE `SetMethod is not null` FILTER ACTUALLY DOES (#506). It excludes a property with
        // NO setter of any kind. It does NOT exclude a non-public one — PropertyInfo.SetMethod
        // returns a private/protected/init accessor happily — so `{ get; private set; }`, the
        // standard EF-encapsulation shape, IS in this set even though System.Text.Json cannot bind
        // into it. The comment that stood here claimed the opposite ("properties without a public
        // setter can't be deserialized into by STJ in the first place, so they're excluded"), and
        // the strip below was written trusting it: it ran unconditionally, so a PUT that mentioned
        // no navigation at all still handed the handler `null` where the model's constructor had
        // put an empty list.
        //
        // Keeping the filter wide is deliberate. Narrowing it to a PUBLIC setter would exempt a
        // `[JsonInclude] { get; private set; }` navigation, which STJ binds perfectly well — that
        // OPENS a deep-write hole rather than closing one. What changed instead is that the strip is
        // GATED on the navigation being NAMED IN THE REQUEST BODY (deepWriteNavByBodyName and
        // CollectPresentNavClrNames, below), which makes the filter's over-reach harmless: the gate
        // never fires for a member the client did not send, whatever its accessors look like.
        //
        // The follow-on claim about PATCH is true on its own terms and is kept: Delta<T>'s
        // InitializeProperties requires a public getter AND setter, so a setter-less navigation
        // could not enter a delta either. It was simply stated as a consequence of a false premise.
        //
        // #253 completion: NavigationPropertyNames is the EDM (JSON) navigation name set, so resolve
        // each CLR property to its EDM name before testing membership (a renamed nav's CLR name is
        // not in the set — its JSON name is).
        //
        // #457 HOISTED THIS OUT OF `if (source.HasPost)`. It was declared inside that block and
        // applied on the collection POST alone, so deep UPDATE — a nested graph in PUT or PATCH,
        // OData 4.01 §11.4.3.1, a SEPARATE named feature from deep insert and one that
        // docs/deep-insert.md has declared out of scope since 1.0.0 — was documented-out but never
        // enforced: STJ bound the nested values and PUT forwarded them to the handler while PATCH
        // bound them into the Delta<TModel>. One set, three routes, deliberately: re-deriving a
        // second set at the PUT/PATCH sites is how the two would drift, and the #461 union below is
        // exactly the kind of correction a second copy would miss.
        //
        // #461: UNION with edmNavigationNames — the write-side twin of #446, and the same
        // subtraction argument pointed at a set the read side already fixed. NavigationPropertyNames
        // is the profile-DECLARED set, so a navigation the ODataConventionModelBuilder discovered
        // but the profile never declared with HasOptional/HasRequired/HasMany was not in the strip
        // set at all — System.Text.Json bound it during deserialization and it reached the Post
        // handler intact, WITH AllowDeepWrites at its default of false. A handler doing
        // `db.Add(model); SaveChanges();` then persists nested rows nobody opted into, which is the
        // silent-partial-graph hazard the strip exists to prevent. The shape that hits it is the
        // most ordinary one there is: a profile that declares no navigations at all, over
        // `public Customer? Customer { get; set; }`.
        //
        // The EDM is the authority on what is a navigation (#446), and a navigation is not
        // deep-write-exempt because the profile forgot to name it. As with #446 the subtraction is
        // applied HERE, at the consumer, not in BuildStructuralProperties or
        // NavigationPropertyNames: the former cannot see a built EDM (it runs from
        // VisitModelBuilder), and the latter feeds Model B's DB/DL partitioning, whose "a candidate
        // that neither routes nor declares the nav has no opinion" category empties under
        // convention sourcing (Issue322ModelBClassificationTests pins it).
        //
        // Union, not replacement: NavigationPropertyNames stays in the test because a declared
        // navigation is the case that already worked and must keep working byte-for-byte, and
        // because the two sets are separately sourced (declaration versus convention discovery) and
        // neither is a superset of the other by construction. edmNavigationNames is
        // OrdinalIgnoreCase and holds EDM names, which is exactly what ResolveEdmName produces.
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
        // navigations above, mapped to the property it names. Built once at startup from the same
        // array, so "is this body key a navigation" has exactly one answer on the write path.
        //
        // #511 REPLACED THE PRIMARY KEY WITH THE BINDER'S OWN ANSWER, and the reason is the defect
        // class rather than the one policy that exposed it. The table used to be keyed by
        // ODataPropertyNaming.ResolveEdmName (= [JsonPropertyName] ?? CLR name — deliberately
        // POLICY-FREE, because $metadata advertises the CLR identifier whatever casing payloads use,
        // OData §4.4) plus the CLR name. The binder matches the [JsonPropertyName] ?? POLICY-CONVERTED
        // name. camelCase differs from the CLR name only by case, so the OrdinalIgnoreCase comparer
        // hid the divergence for the only policy anyone had configured; SnakeCaseLower and
        // KebabCaseLower do not. Measured: with SnakeCaseLower and a `BackOrders` navigation, a body
        // naming `back_orders` was BOUND by the deserializer and MISSED by this table, so the strip
        // never fired — #504's hazard, reopened by a spelling.
        //
        // Adding PropertyNamingPolicy?.ConvertName(...) as a third key would have closed that one
        // policy. It would not have closed the CLASS, which is "two things that must agree, derived
        // independently" — #454's exact shape, in this same release. So the primary key is now read
        // OFF THE CONTRACT the binder resolves: JsonTypeInfo.Properties[].Name is, by construction,
        // the string System.Text.Json matches a body key against, whatever produced it — a naming
        // policy, a [JsonPropertyName], a custom TypeInfoResolver modifier, or a source-generated
        // contract. There is no second derivation left to drift.
        //
        // HasSameMetadataDefinitionAs, never == / ReferenceEquals, to pair a JsonPropertyInfo back to
        // its PropertyInfo. PropertyInfo equality also compares ReflectedType, and for an INHERITED
        // member the two reflection walks disagree about it — typeof(TModel).GetProperties() reports
        // TModel while STJ's AttributeProvider reports the declaring base. That exact comparison bug
        // is #462's third defect (IsNavVisibleInBaseOptions), measured on .NET 10.0.11; it would show
        // up here as an inherited navigation silently losing its contract key.
        //
        // The comparer follows the binder too, the way IgnoredPropertyJsonOptions.WithheldNameComparer
        // does: OrdinalIgnoreCase whenever PropertyNameCaseInsensitive is set — which is always in
        // practice (the fallback options set it explicitly and a host's come from
        // JsonSerializerDefaults.Web), but it is READ rather than assumed, because a table wider than
        // the binder strips a navigation the binder never bound, which is #506's destruction case.
        //
        // The EDM name and the CLR name STAY, demoted to non-overwriting aliases. They are what
        // FindClrPropertyByEdmName resolves through, which is what the PATCH loop uses, so dropping
        // them would make a [JsonPropertyName]-renamed navigation named by its CLR name behave
        // differently on PATCH than on POST/PUT — a per-verb divergence to fix a per-host one. On a
        // default host every alias collapses onto the contract key under the comparer, so nothing
        // about the shipped behaviour moves.
        //
        // This is a lookup TABLE and not a call to FindClrPropertyByEdmName per body key on purpose.
        // That helper memoizes on (Type, string) in a process-wide ConcurrentDictionary keyed by the
        // exact string handed in, so calling it with client-supplied names would let a caller grow
        // that cache without bound. PATCH already does (pre-existing, and out of scope here); POST
        // and PUT must not join it.
        var deepWriteNavByBodyName = new Dictionary<string, PropertyInfo>(
            (jsonOptions ?? _pascalCaseSerializerOptions).PropertyNameCaseInsensitive
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        if (deepWriteNavPropsToStrip.Length > 0)
        {
            JsonTypeInfo? writeContract = TryResolveWriteContract(typeof(TModel), jsonOptions);
            if (writeContract is not null)
            {
                foreach (JsonPropertyInfo contractProp in writeContract.Properties)
                {
                    if (contractProp.AttributeProvider is not PropertyInfo clrMember) continue;
                    foreach (PropertyInfo navProp in deepWriteNavPropsToStrip)
                    {
                        if (!clrMember.HasSameMetadataDefinitionAs(navProp)) continue;
                        deepWriteNavByBodyName[contractProp.Name] = navProp;
                        break;
                    }
                }
            }
        }

        foreach (PropertyInfo navProp in deepWriteNavPropsToStrip)
            deepWriteNavByBodyName.TryAdd(ODataPropertyNaming.ResolveEdmName(navProp), navProp);
        foreach (PropertyInfo navProp in deepWriteNavPropsToStrip)
            deepWriteNavByBodyName.TryAdd(navProp.Name, navProp);

        // The gate only has work to do when there is something to strip AND the profile has not
        // opted in. Hoisted so neither write route pays a scan it would discard — a model with no
        // navigations at all (the common shape) never walks a body for this.
        bool deepWriteNavGateApplies = !source.AllowDeepWrites && deepWriteNavPropsToStrip.Length > 0;

        // #514: derived once per entity set at startup, because JsonDocumentOptions is an immutable
        // struct of three values and nothing about it varies per request.
        JsonDocumentOptions binderParityDocumentOptions =
            CreateBinderParityDocumentOptions(jsonOptions);

        // #510: PATCH's own body-name lookup table — the same move #506 made for the deep-write gate
        // on POST/PUT, now applied to the one route that was left calling the memoizing helper with
        // client-supplied strings.
        //
        // ODataPropertyNaming.FindClrPropertyByEdmName memoizes on (Type, string) in a PROCESS-WIDE
        // ConcurrentDictionary keyed by the caller's exact string, and the PATCH delta loop called it
        // once per BODY PROPERTY NAME. The lookup is what caches, not the result, so a caller could
        // grow that dictionary without bound by sending bodies full of distinct unmatched keys —
        // each one a permanent entry for the life of the process. No single request costs anything
        // worth measuring; the growth is cumulative and never reclaimed. That is out of line with the
        // posture the rest of the framework takes (OpenTypeJsonOptions' ValidatedKeys cache is capped
        // at 1024 entries and memoises non-ASCII keys only, with the reasoning written out).
        //
        // Option 3 of the three the issue lists, and the one the write path was already heading
        // toward: the names PATCH can encounter are now bounded by the MODEL rather than by the
        // request. Nothing else changes — capping the shared cache or refusing to cache misses would
        // both have made a hot READ-path helper slower to close a WRITE-path hole.
        //
        // BEHAVIOURALLY IDENTICAL TO THE CALL IT REPLACES, deliberately and to the letter. The table
        // is built from the same reflection walk (public instance, non-indexer), keyed EDM name first
        // and CLR name second as non-overwriting aliases — which is exactly FindClrPropertyByEdmName's
        // two-stage FirstOrDefault, since insertion follows GetProperties() order and TryAdd keeps
        // the first writer. The comparer is OrdinalIgnoreCase UNCONDITIONALLY, matching that helper
        // rather than the binder: it has always matched case-insensitively regardless of
        // PropertyNameCaseInsensitive, and following the binder here would CHANGE what PATCH binds.
        //
        // Note this is deliberately NOT keyed off the binder's contract the way #511 keyed
        // deepWriteNavByBodyName. Adding JsonTypeInfo.Properties[].Name would make PATCH start
        // binding names it does not bind today (a snake_case body key against a PascalCase CLR
        // property, say) — a real per-host divergence, but a separate behaviour change from #510 and
        // one that belongs to its own issue rather than riding along inside a memory fix.
        var patchPropByBodyName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        PropertyInfo[] patchCandidateProps = typeof(TModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();
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
        EdmRequiredProperty[] edmRequiredProps = source.ValidateRequestBodyNullability
            ? BuildEdmRequiredProperties(rootEdmType, typeof(TModel))
            : Array.Empty<EdmRequiredProperty>();

        // #355: the same answer as a name set, for the structural-property write/delete routes,
        // which ask about ONE named property rather than validating a whole instance. Includes the
        // key (which edmRequiredProps deliberately excludes) because those routes do not reach the
        // key — it has its own KeyImmutableError stubs — and leaving it out here would be a claim
        // about the key that this set is not making.
        var edmNonNullablePropertyNames = new HashSet<string>(StringComparer.Ordinal);
        if (source.ValidateRequestBodyNullability && rootEdmType is not null)
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
                        HashSet<string> bodyNavClrNames =
                            CollectPresentNavClrNames(postPrepared.Body, deepWriteNavByBodyName);
                        foreach (var navProp in deepWriteNavPropsToStrip)
                        {
                            if (bodyNavClrNames.Contains(navProp.Name)) navProp.SetValue(model, null);
                        }
                    }

                    // #355: the body must be a valid instance of the type the framework's own
                    // $metadata publishes. Below the strip and above everything that reads the
                    // model, so the check sees exactly what the handler would have received — and
                    // above CheckResourceAuthAsync deliberately: a malformed body is a client error
                    // regardless of who sent it, and evaluating a resource policy against an
                    // instance the service already knows is invalid tells the policy nothing.
                    IResult? postNullabilityFail = ValidateEdmRequiredProperties(edmRequiredProps, model);
                    if (postNullabilityFail is not null) return postNullabilityFail;

                    // #199 Layer B: resource-based Create auth runs against the incoming (pre-persist)
                    // entity — there is no stored row yet, so the collection POST cannot use the
                    // load-by-key filter (nav-POST, which has a {key}, checks against the parent instead).
                    IResult? createAuthFail = await CheckResourceAuthAsync(ctx, model, OhDataOperation.Create, boundOperationName: null);
                    if (createAuthFail is not null) return createAuthFail;

                    var s = ResolveHandlers(ctx);
                    logger?.LogDebug("POST {Prefix}/{Name}", prefix, name);
                    object? result = await s.InvokePostAsync(model, ct);
                    if (result is null) return ODataError(400, "BadRequest", "Post handler returned null.");
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

                        // Gap 5: include @odata.id in POST response body
                        // Gap 2: include @odata.etag in body
                        // Deep insert (§32): when AllowDeepWrites is true, `result` (the handler's
                        // return value) may carry nested navigation values populated by the
                        // handler — SerializeToNode below serializes them inline automatically,
                        // satisfying §11.4.2.2's "return the created entity with related entities."
                        // #240: omit un-expanded navigations from the POST echo so it matches a read
                        // of the same type — EXCEPT when the profile opted into deep writes, where the
                        // 201 deliberately echoes the created graph inline (§11.4.2.2). The gate is
                        // entity-level because OmitUnexpandedNavigations strips ALL declared navs
                        // unconditionally (it never inspects whether a nav is populated); a per-request
                        // choice would require a value-aware strip. Accepted residual: a deep-write
                        // profile doing a *non-deep* POST still echoes its (null/empty) navs.
                        //
                        // #457: PUT/PATCH still omit UNCONDITIONALLY, and that is deliberately left
                        // alone. This issue is about what the HANDLER receives; §11.4.3.1 requires
                        // nothing of the update response beyond what §11.4.2 already says, and
                        // widening the echo is a separate wire-shape change with its own #240
                        // consequences. The asymmetry is therefore: a deep-write profile's 201 echoes
                        // the graph, its 200 on PUT/PATCH does not.
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
            ApplyOperationAuth(rb, OhDataOperation.Create);
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
                            bodyNavClrNames =
                                CollectPresentNavClrNames(putPrepared.Body, deepWriteNavByBodyName);
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
                        // CollectPresentNavClrNames(ReadOnlySpan<byte>). Reads GetBuffer() rather
                        // than the stream, so putBuffered.Position stays at 0 for the deserializer
                        // below and the DeserializeAsync(Stream) overload #389 L1 pins is untouched.
                        if (deepWriteNavGateApplies)
                        {
                            bodyNavClrNames = CollectPresentNavClrNames(
                                putBuffered.GetBuffer().AsSpan(0, (int)putBuffered.Length),
                                deepWriteNavByBodyName,
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

                    // #355: PUT REPLACES the entity, so an omitted non-nullable property is exactly
                    // as invalid as an explicit null — the resulting entity would not be a valid
                    // instance of the declared type. Grouped with the key-mismatch check below
                    // rather than after the precondition gate, following this route's existing
                    // ordering (body shape first, then If-Match).
                    IResult? putNullabilityFail = ValidateEdmRequiredProperties(edmRequiredProps, model);
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

                    object? result = await s.InvokePutAsync(parsedKey!, model, ct);

                    // Gap 3: Upsert via PUT (§11.4.4) — create entity when result is null and AllowUpsert enabled
                    bool wasCreated = false;
                    if (result is null && source.AllowUpsert && source.HasPost)
                    {
                        result = await s.InvokePostAsync(model, ct);
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
                catch (FormatException ex)
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

                    // #454: the key property is immutable (§11.4.9), and the guard below must
                    // validate EXACTLY the set the delta loop applies -- otherwise a body can pass
                    // validation on one occurrence and be applied through another.
                    //
                    // It previously did not. `TryGetJsonProperty` returns the FIRST case-insensitive
                    // match and stops, and it matched on the key's CLR name; the loop below resolves
                    // EVERY body property through `FindClrPropertyByEdmName` (case-insensitive AND
                    // [JsonPropertyName]-aware) into a last-writer-wins Delta<T>. Three bodies
                    // therefore moved the key and returned 200: {"Id":1,"Id":999},
                    // {"id":1,"Id":999}, and -- with a renamed key -- a single {"code":"ZZ"}, which
                    // the CLR-name lookup could not see at all.
                    //
                    // Both halves now resolve through the same function against the same CLR
                    // property: every occurrence is validated here, and the loop never writes the
                    // key into the delta at all. A mismatch is REJECTED rather than silently
                    // dropped, matching both the pre-existing single-occurrence 400 and the
                    // structural-property write route's KeyImmutableError.
                    //
                    // A body that omits the key is still valid -- PATCH is a partial update and the
                    // URL key is authoritative.
                    //
                    // #510: both halves resolve through the startup table now instead of through
                    // FindClrPropertyByEdmName, so a client-supplied name never reaches that
                    // helper's process-wide cache. The table answers identically (see its
                    // construction), so #454's "both halves consult the same set" property is
                    // preserved by construction rather than restated.
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

                    object? result = await s.InvokePatchAsync(parsedKey!, patchDelta, ct);

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
                catch (FormatException ex)
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
                    bool deleted = await s.InvokeDeleteAsync(parsedKey!, ct);
                    if (!deleted && !source.IdempotentDelete)
                        return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    return Results.NoContent();
                }
                catch (FormatException ex)
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

        // ── #416 / #492 §3: entity-level bound function vs a navigation ROUTE ────────────────
        //
        // Every entry in NavigationRoutes gets `GET /{name}({key})/{nav.PropertyName}` mapped for
        // it below -- including one declared with only `post`/`addRef`/`removeRef`, whose GET is
        // registered with a null-returning handler that 404s. An entity-level bound function claims
        // `GET /{name}({key})/{fn.Name}`. Same template, same method, and NO check existed: the
        // three pre-existing ones cover structural properties (a set from which
        // BuildStructuralProperties SUBTRACTS every declared navigation, which is exactly why this
        // pair fell through), the nav-POST-vs-action pair above, and the #313 continuation route.
        //
        // #416 raised warn-vs-throw on the grounds that an app might be "relying on whichever route
        // wins registration order". Measured, none does: ASP.NET Core raises AmbiguousMatchException
        // and NEITHER endpoint runs, so there is no working configuration to break -- only a raw 500
        // on every request to that URL, moved to a named startup failure.
        //
        // The check keys off NavigationRoutes rather than off which particular delegate was
        // supplied, because the GET is mapped for the whole list. A navigation declared with NO
        // handler is absent from that list, registers no route, and stays legal (the shape #313
        // stage 5 deliberately left alone, and which its own check governs once the continuation
        // route exists).
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
        foreach (ExpandPagingNav pagingNav in expandPagingNavs)
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

            MethodInfo contPageMethod = _continuationPageMethod
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
            FieldInfo contKeyBoxField = typeof(ContinuationKeyBox<TKey>)
                .GetField(nameof(ContinuationKeyBox<TKey>.Value))!;

            var contRb = entityAuthGroup.MapGet($"/{name}({{key}})/{contNavName}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        // 1. The $skip-ONLY surface. Every other system query option is rejected —
                        // conformant (Minimal item 7: parse the option or reject it), and the thing
                        // that keeps this endpoint from quietly becoming a second general-purpose
                        // collection route. Rejecting by the '$' sigil rather than an allowlist of
                        // known names is deliberately fail-closed: a future OData system option this
                        // build has never heard of is refused rather than silently ignored.
                        //
                        // $format is the ONE exemption, and it is not a data option at all: §11.2.12
                        // content negotiation is implemented once, on the group filter in MapAll, for
                        // every route on the whole OData surface — it never reaches this handler and
                        // cannot change a single row. Refusing it here would make this the only route
                        // in the surface that 400s a conformant, already-supported option, and would
                        // break the common client habit of appending it to a server-issued link. An
                        // unsupported VALUE is still rejected, by that same group filter, unchanged.
                        foreach (string queryKey in ctx.Request.Query.Keys)
                        {
                            if (!queryKey.StartsWith('$') ||
                                string.Equals(queryKey, "$skip", StringComparison.Ordinal) ||
                                string.Equals(queryKey, "$format", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            return ODataError(400, "UnsupportedQueryOption",
                                $"The query option '{queryKey}' is not supported on an $expand " +
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
                        var contKeyBox = new ContinuationKeyBox<TKey> { Value = (TKey)parsedKey! };
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
                            contJson.Add(SerializeBounded(
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
                    catch (FormatException ex)
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
                .Produces(400);
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
                        // S1/B1 fix: this route parses $orderby/$skip/$top/$count/$select (below)
                        // but previously ignored anything else — most notably $filter — silently,
                        // returning 200 with the full unfiltered collection. That violates
                        // Minimal item 7 ("parse the option or reject it"): reject up front
                        // instead of quietly under-applying what the client asked for.
                        IResult? navCapabilityError = CheckNavUnsupportedQueryOptions(ctx);
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
                    catch (FormatException ex)
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
                .Produces(404);
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
                        catch (FormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name, withTarget: false);
                        }
                    })
                    .WithTags(name)
                    .Produces<long>(200, "text/plain")
                    .Produces(404);
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
                    catch (FormatException ex)
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
                    catch (FormatException ex)
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
                        catch (FormatException ex)
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
                EdmRequiredProperty[] navPostRequiredProps = source.ValidateRequestBodyNullability
                    ? BuildEdmRequiredProperties(
                        EdmClrTypeMap.FindStructuredType(registration.EdmModel, postNavItemType),
                        postNavItemType)
                    : Array.Empty<EdmRequiredProperty>();
                var navPostRb = entityAuthGroup.MapPost($"/{name}({{key}})/{postNavPropertyName}",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);

                        object? parsedKey;
                        try
                        {
                            parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        }
                        catch (FormatException ex)
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
                        IResult? navPostNullabilityFail =
                            ValidateEdmRequiredProperties(navPostRequiredProps, child);
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

                            // §11.2.6: a single-valued null property returns 204 No Content.
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
                        catch (FormatException ex)
                        {
                            return BadKeyError(logger, ex, key, name);
                        }
                    })
                    .WithTags(name)
                    .Produces(200, typeof(ODataPropertyResponse<>).MakeGenericType(propCapture.ClrType), "application/json")
                    .Produces(204)
                    .Produces(404));
                ApplyOperationAuth(propGetRb, OhDataOperation.Read);

                // GET /{name}({key})/{Property}/$value — raw value (Part 2 §4.7).
                bool propIsComplex = propCapture.IsComplex;
                var propValueRb = DocProp(entityAuthGroup.MapGet($"/{name}({{key}})/{propCapture.Name}/$value",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
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

                            // Part 2 §4.7: the raw value of a null property does not exist.
                            if (value is null)
                            {
                                return ODataError(404, "NotFound",
                                    $"{name}({key})/{propCapture.Name} is null; its raw value does not exist.",
                                    target: propCapture.Name);
                            }

                            if (value is byte[] bytes)
                                return Results.Bytes(bytes, "application/octet-stream");

                            return Results.Text(FormatRawValue(value), "text/plain");
                        }
                        catch (FormatException ex)
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
                    .Produces(404));
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

                    if (isPatchVerb && propIsComplex)
                    {
                        return ODataError(400, "NotSupported",
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
                            return ODataError(400, "BadRequest",
                                $"Property '{propName}' is not nullable and cannot be set to null.", target: propName);
                        }

                        var delta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                        if (!delta.TrySetPropertyValue(clrPropName, newValue))
                        {
                            return ODataError(400, "InvalidBody",
                                $"Could not set property '{propName}' to the supplied value.", target: propName);
                        }

                        object? result = await s.InvokePatchAsync(parsedKey!, delta, ct);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                        if (source.HasETag)
                        {
                            string writeEtag = s.InvokeGetETag(result);
                            ctx.Response.Headers.ETag = $"\"{writeEtag}\"";
                        }

                        return Results.NoContent();
                    }
                    catch (FormatException ex)
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
                        return ODataError(400, "BadRequest",
                            $"Property '{propName}' is not nullable and cannot be set to null.", target: propName);
                    }

                    try
                    {
                        var s = ResolveHandlers(ctx);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));

                        var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                        if (etagCheck is not null) return etagCheck;

                        var delta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                        delta.TrySetPropertyValue(clrPropName, null);
                        object? result = await s.InvokePatchAsync(parsedKey!, delta, ct);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");

                        if (source.HasETag)
                        {
                            string deleteEtag = s.InvokeGetETag(result);
                            ctx.Response.Headers.ETag = $"\"{deleteEtag}\"";
                        }

                        return Results.NoContent();
                    }
                    catch (FormatException ex)
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
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s);
            }).WithTags(name).Produces(400);
            AddBoundOperationProduces<TModel>(rb, fnCapture);
            // Issue #181: document the function's query-string parameters.
            var boundFnQueryParams = BuildFunctionQueryParametersMetadata(fnCapture.Parameters, skipKey: false);
            if (boundFnQueryParams is not null) rb.WithMetadata(boundFnQueryParams);
            ApplyOperationAuth(rb, OhDataOperation.Invoke, fnCapture.Name);
        }

        // Bound actions — POST /{EntitySet}/{ActionName} with JSON body params
        // Note: TryGetJsonProperty (below) provides case-insensitive JSON property lookup,
        // matching the case-insensitive query string lookup used for bound functions.
        foreach (var action in source.BoundActions.Where(a => !a.IsEntityLevel))
        {
            var actionCapture = action;
            var rb = entityGroup.MapPost($"/{action.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
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
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s);
            }).WithTags(name).Produces(400).Produces(415);
            AddBoundOperationProduces<TModel>(rb, actionCapture);
            // Leg 2 / #184: synthesize a POCO body schema from the action's parameters (see the
            // matching comment on the unbound-action branch of MapUnboundOperations).
            if (actionCapture.Parameters.Length > 0)
            {
                rb.WithMetadata(new OhDataRequestBodyMetadata
                {
                    // #499: prefix with the registration name so two registrations declaring the
                    // same entity set + action name (e.g. v1/v2 of the same versioned action) get
                    // distinct memoized schema types instead of silently sharing whichever one
                    // mapped first.
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        $"{registration.Name}.{name}.{actionCapture.Name}", actionCapture.Parameters),
                    Description = "JSON object with the action's parameters: " +
                        string.Join(", ", actionCapture.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                });
            }
            ApplyOperationAuth(rb, OhDataOperation.Invoke, actionCapture.Name);
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
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s);
                    }
                    catch (FormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name);
                    }
                })
                .WithTags(name).Produces(400);
            AddBoundOperationProduces<TModel>(rb, fnCapture);
            // Issue #181: document the function's query-string parameters (skip the leading key,
            // which is a route parameter already documented via BindingSource.Path).
            var entityFnQueryParams = BuildFunctionQueryParametersMetadata(fnCapture.Parameters, skipKey: true);
            if (entityFnQueryParams is not null) rb.WithMetadata(entityFnQueryParams);
            ApplyOperationAuth(rb, OhDataOperation.Invoke, fnCapture.Name);
        }

        // Gap 7: Entity-level bound actions — POST /{name}({key})/{action.Name}
        //
        // #478 -- DELIBERATE If-Match EXCLUSION, and the one place in this file where a
        // state-changing keyed route does NOT call CheckETagAsync. The $ref and navigation-POST
        // routes above were brought under the precondition gate; actions were not, and the reason
        // is the identity of the *target resource*, not convenience:
        //
        //   RFC 9110 §13.1.1 evaluates If-Match against "the current representation of the target
        //   resource". The target resource of POST /Set(key)/Action is the ACTION-INVOCATION
        //   resource (OData Protocol §11.5.4), which has no representation and therefore no
        //   entity tag of its own. `Set(key)` is the action's binding parameter, not the request
        //   target. A $ref write is different in kind: OData §11.4.6 defines it as a modification
        //   of the addressed entity's own relationship state, so the entity IS the target.
        //
        // Consequences the exclusion accepts: an action that mutates its binding entity ignores a
        // received If-Match, and the only way to honour it is for the profile to inject
        // IHttpContextAccessor and hand-implement the comparison. That escape hatch exists for
        // EVERY handler on a scoped profile, $ref delegates included -- so "the author cannot do
        // it" is NOT the reason the $ref routes above are gated; the reason is that the addressed
        // entity is unambiguously their target and the server should do it once, correctly.
        // Collection-level bound actions and unbound actions have no key at all and no entity to
        // compare against. Revisiting this needs an explicit decision, not a mechanical extension;
        // docs/etags.md states the exclusion for API consumers.
        foreach (var action in source.BoundActions.Where(a => a.IsEntityLevel))
        {
            var actionCapture = action;
            var rb = entityAuthGroup.MapMethods($"/{name}({{key}})/{action.Name}", new[] { "POST" },
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        var s = ResolveHandlers(ctx);
                        var requestAction = s.BoundActions.First(a => a.Name == actionCapture.Name && a.IsEntityLevel);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
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
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, registration.EdmModel, s);
                    }
                    catch (FormatException ex)
                    {
                        return BadKeyError(logger, ex, key, name);
                    }
                })
                .WithTags(name).Produces(400).Produces(415);
            AddBoundOperationProduces<TModel>(rb, actionCapture);
            // Leg 2 / #184: entity-level Parameters[0] is the route key (see BoundOperationDefinition's
            // XML doc), so only Parameters[1..] are body parameters — synthesize the POCO body schema
            // from those, excluding the leading key.
            if (actionCapture.Parameters.Length > 1)
            {
                ParameterInfo[] bodyParams = actionCapture.Parameters.Skip(1).ToArray();
                rb.WithMetadata(new OhDataRequestBodyMetadata
                {
                    // #499: same registration-identity prefix as the collection-level bound action
                    // above.
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        $"{registration.Name}.{name}.{actionCapture.Name}.Entity", bodyParams),
                    Description = "JSON object with the action's parameters: " +
                        string.Join(", ", bodyParams.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                });
            }
            ApplyOperationAuth(rb, OhDataOperation.Invoke, actionCapture.Name);
        }

    }

    // Gap 1: Wrap bound operation result with @odata.context when return type matches TModel (§11.5.3).
    // For collection results (IEnumerable<TModel>): context = {root}/$metadata#{EntitySet}
    // For single results (TModel): context = {root}/$metadata#{EntitySet}/$entity
    // For primitives/other types: return Results.Ok directly (no wrapping needed).
    private static IResult WrapBoundOpResult(
        HttpContext ctx, string prefix, string entitySetName, object result, Type modelType,
        JsonSerializerOptions? jsonOptions, IEdmEntityType? rootEdmType, IEdmModel? edmModel,
        IEntitySetEndpointSource source)
    {
        var resultType = result.GetType();

        // #497 (the #462 defect class, fourth site — it predates InheritedTypeConfig's consolidation
        // and was not swept in): the element test used to be `== modelType`, i.e. EXACT CLR-type
        // equality, while the SINGLE-entity branch below already accepted a derived instance via
        // IsAssignableFrom. A handler declared `Task<IEnumerable<TModel>>` that returns a
        // `List<TDerived>` — the ordinary EF Core TPH shape — lists only IEnumerable<TDerived> in
        // GetInterfaces(), so it missed this branch, missed the single-entity branch (IsAssignableFrom
        // fails on a List), missed the Edm-primitive map, and fell into the final raw-graph
        // PreRenderedJson. MEASURED on the pre-fix tree:
        //     [{"Special":"x","Id":1,"Name":"derived","Parts":[{"Id":9,"Label":"PART-LEAK"}]}]
        // — no @odata.context, no `value` envelope, the declared navigation `Parts` served INLINE
        // (§4.5.1 / §11.2.4.2 and #179 both bypassed), and no @odata.etag injected — while the
        // identical handler returning List<TModel> got the full envelope with navigations stripped. A
        // cyclic derived graph turned the same request into a 500, since nothing suppressed the
        // navigations before serialization.
        //
        // Assignability, not equality. It cannot over-match: modelType is the entity set's own TModel
        // (never `object`), and every arm this could steal from — the single-entity branch and the Edm
        // primitive map — is tested for an element type assignable to TModel, which no primitive and
        // no non-TModel DTO is. The string guard stays because string is IEnumerable<char>.
        //
        // AddBoundOperationProduces' collection arm carries the SAME predicate over the DECLARED
        // return type, so the OpenAPI document and the wire cannot disagree about which shape a bound
        // operation produces; changing one without the other is what #497 calls the advertise-vs-serve
        // half of the defect.
        bool isCollectionOfModel = false;
        if (resultType != typeof(string))
        {
            foreach (var iface in new[] { resultType }.Concat(resultType.GetInterfaces()))
            {
                if (iface.IsGenericType
                    && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    && modelType.IsAssignableFrom(iface.GetGenericArguments()[0]))
                {
                    isCollectionOfModel = true;
                    break;
                }
            }
        }

        if (isCollectionOfModel)
        {
            // Materialize the enumerable to an array so JSON serialization works correctly.
            // Cast via non-generic IEnumerable since the concrete type is IEnumerable<TModel>
            // not IEnumerable<object>.
            object[] coll = ((IEnumerable)result).Cast<object>().ToArray();
            string baseUrl = BuildBaseUrl(ctx, prefix);

            // #179: route the collection through the same serialize → ETag → omit-navs stages the
            // normal collection GET uses (ApplyCollectionPipelineAsync). A bound op returns the
            // entity set's own type but takes no $expand, so every declared navigation is omitted
            // (§4.5.1 / §11.2.4.2) and @odata.etag is injected per item when UseETag is set —
            // previously the raw CLR graph was handed to Results.Ok, leaking navs and dropping ETags.
            // #325/#326 (Option B): bounded (clause: null — a bound op takes no $expand), never
            // whole-graph, so a bound function/action returning tracked/cyclic entities is safe too.
            var serializerOptions = jsonOptions ?? _pascalCaseSerializerOptions;
            var json = new JsonArray();
            foreach (object item in coll)
            {
                json.Add(SerializeBounded(item, rootEdmType, edmModel, clause: null, serializerOptions));
            }
            if (source.HasETag)
            {
                InjectETagsIntoJsonArray(json, coll, source);
            }
            // Defence-in-depth (#325/#326): practical no-op now.
            OmitUnexpandedNavigations(json, rootEdmType, clause: null, modelType, serializerOptions);

            // #495: rendered here rather than deferred to Results.Ok. The JsonArray above is
            // already materialized, but the envelope AROUND it was not: it is a
            // Dictionary<string, object?>, so the host's DictionaryKeyPolicy rewrote
            // `@odata.context`/`value` (measured: `@ODATA.CONTEXT`/`VALUE`), and the write happened
            // after the filter chain unwound. Rendering with the registration's owned options keeps
            // the envelope keys contractual while the payload inside the array still honours the
            // host's converters/encoder exactly as it did (#252) -- those already ran, above.
            return PreRenderedJson(new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata#{entitySetName}",
                ["value"] = json
            }, EnvelopeOptions(serializerOptions));
        }

        if (resultType == modelType || modelType.IsAssignableFrom(resultType))
        {
            // #179: a single-TModel bound-op result rides the same omission + ETag path as GetById
            // so its shape matches a top-level read — un-expanded navigations stripped (§4.5.1 /
            // §11.2.4.2) and @odata.etag injected when UseETag is set.
            string? boundOpEtag = source.HasETag ? source.InvokeGetETag(result) : null;
            return ODataEntityResult(ctx, prefix, entitySetName, result, jsonOptions, edmModel,
                etag: boundOpEtag, omitNavsForType: rootEdmType);
        }

        // m5: primitive results get the JSON §11 individual-value envelope
        // ({"@odata.context":"...#Edm.<Type>","value":<primitive>}). Only types this framework
        // can confidently name as an Edm primitive are wrapped; anything else (a non-TModel
        // complex/DTO type) falls through unwrapped rather than risk asserting a wrong Edm type.
        Type underlyingResultType = Nullable.GetUnderlyingType(resultType) ?? resultType;
        if (s_edmPrimitiveTypeNames.TryGetValue(underlyingResultType, out string? edmTypeName))
        {
            string primitiveBaseUrl = BuildBaseUrl(ctx, prefix);
            // #495: this branch is the one #396 listed as a knowing residual ("only a host-
            // registered converter for a primitive type could fault there"). It can: measured with
            // a host JsonConverter<decimal> that throws, the client got an empty, envelope-less 500
            // with nothing logged, because Results.Ok deferred the write past the filter chain.
            // Pre-rendering moves that converter call inside the filter's scope, so a throwing one
            // now produces the logged 500 envelope like any other handler fault -- and a merely
            // reformatting one still reformats, because `value` here IS payload and the host owns
            // value formatting (#252). Only the envelope's keys are OhData's.
            return PreRenderedJson(new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{primitiveBaseUrl}/$metadata#{edmTypeName}",
                ["value"] = result
            }, EnvelopeOptions(jsonOptions ?? _pascalCaseSerializerOptions));
        }

        // Primitive/other (e.g. a non-TModel DTO) — no context wrapping. Serialize through the
        // owned options so its property names follow OhData's casing (#252) rather than leaking the
        // host's HttpJsonOptions naming policy via the ASP.NET Core Results.Ok serialization path.
        // #396: this is the one branch of this method that hands an arbitrary CLR graph to the
        // serializer — the two above emit a materialized JsonArray or an Edm primitive — so it is
        // the branch that has to serialize inside the filter's scope. See PreRenderedJson.
        return PreRenderedJson(result, jsonOptions ?? _pascalCaseSerializerOptions);
    }

    // m5: CLR type -> Edm primitive type name, used to build the individual-value response
    // envelope for bound operations that return a bare primitive (JSON §11). Deliberately not
    // exhaustive of every Edm primitive kind — only the CLR types this framework's parameter/
    // return-type conversion already supports elsewhere (see the query-string/JSON-body
    // parameter converters above).
    private static readonly Dictionary<Type, string> s_edmPrimitiveTypeNames = new()
    {
        [typeof(string)] = "Edm.String",
        [typeof(bool)] = "Edm.Boolean",
        [typeof(byte)] = "Edm.Byte",
        [typeof(sbyte)] = "Edm.SByte",
        [typeof(short)] = "Edm.Int16",
        [typeof(int)] = "Edm.Int32",
        [typeof(long)] = "Edm.Int64",
        [typeof(float)] = "Edm.Single",
        [typeof(double)] = "Edm.Double",
        [typeof(decimal)] = "Edm.Decimal",
        [typeof(Guid)] = "Edm.Guid",
        // OData v4 has no "DateTime" primitive; both CLR DateTime and DateTimeOffset map to
        // Edm.DateTimeOffset, matching FormatRawValue's ("o") treatment of the two types above.
        [typeof(DateTime)] = "Edm.DateTimeOffset",
        [typeof(DateTimeOffset)] = "Edm.DateTimeOffset",
        [typeof(DateOnly)] = "Edm.Date",
        [typeof(TimeOnly)] = "Edm.TimeOfDay",
        [typeof(TimeSpan)] = "Edm.Duration",
        [typeof(byte[])] = "Edm.Binary",
    };

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

