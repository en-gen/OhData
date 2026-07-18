using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OData.Query;
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
using OhData.Abstractions;
using OhData.Abstractions.AspNetCore.OData;

namespace OhData.AspNetCore;

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

    private static string BuildBaseUrl(HttpContext ctx, string prefix) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";

    private static string BuildNextPageLink(HttpContext ctx, string skiptoken)
    {
        var req = ctx.Request;
        var query = HttpUtility.ParseQueryString(req.QueryString.ToString());
        query.Remove("$skip");
        query["$skiptoken"] = skiptoken;
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}?{query}";
    }

    // #195: continuation link for the Priority-1 path, expressed as $skip rather than the opaque
    // $skiptoken BuildNextPageLink emits. The Priority-1 profile re-applies the incoming
    // ODataQueryOptions via ApplyTo, which honors $skip natively but has no handler for $skiptoken.
    private static string BuildNextPageLinkWithSkip(HttpContext ctx, int skip)
    {
        var req = ctx.Request;
        var query = HttpUtility.ParseQueryString(req.QueryString.ToString());
        query.Remove("$skiptoken");
        query["$skip"] = skip.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}?{query}";
    }

    private static bool PrefersMinimal(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("Prefer", out var prefer) &&
        prefer.ToString().Contains("return=minimal", StringComparison.OrdinalIgnoreCase);

    // BUG 1 fix: POST/PUT/PATCH bodies are read and deserialized manually (see below) rather
    // than via a `TModel model` minimal-API parameter, so content-type negotiation must be done
    // by hand too -- otherwise a mismatched Content-Type would either be silently ignored (we'd
    // try to parse non-JSON as JSON) or, if left to ASP.NET Core's implicit binder/`.Accepts<T>()`
    // metadata, would short-circuit with an empty 415 body before this OData error-formatting
    // code ever runs. Media-type parameters (e.g. ";odata.metadata=full", ";charset=utf-8") are
    // stripped before comparison since they don't affect whether the payload is JSON.
    // #203: the write methods that carry a request body OhData deserializes. DELETE is excluded
    // (its $ref variant reads only a small link body and no body-size limit is meaningful there).
    private static bool IsBodyBearingWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

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

    private static IEnumerable<string> ParseETagList(string raw)
    {
        // Split comma-separated ETags per RFC 7232 §3.1.
        // Each entry may optionally carry a W/ weak-validator prefix; strip it before comparison.
        return raw.Split(',').Select(s =>
        {
            string t = s.Trim();
            if (t.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(2);
            return t.Trim('"');
        });
    }

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

    public static RouteGroupBuilder MapAll(IEndpointRouteBuilder routes, OhDataRegistration registration)
    {
        string prefix = registration.Prefix;
        var group = routes.MapGroup(prefix);
        // Resolve JsonOptions once at startup so handlers don't pay DI lookup per request.
        var startupJsonOptions = routes.ServiceProvider
            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            ?.Value?.SerializerOptions;

        // Resolved once here (rather than down at the per-profile loop) so the group-level
        // exception filter below can log through the same "OhData" category every other
        // handler uses.
        var loggerFactory = routes.ServiceProvider.GetService<ILoggerFactory>();
        var groupLogger = loggerFactory?.CreateLogger("OhData");

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
        // OData-MaxVersion filters below, not just route handlers. Deliberately does not catch
        // OperationCanceledException: a client-aborted request has no response to write and
        // should be left to ASP.NET Core's own cancellation handling rather than have this filter
        // try to produce a 500 for it.
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (IsBodyBearingWriteMethod(http.Request.Method) &&
                http.GetEndpoint()?.Metadata.GetMetadata<OhDataBodyLimitMetadata>() is { } limit)
            {
                IHttpMaxRequestBodySizeFeature? sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (sizeFeature is { IsReadOnly: false })
                {
                    sizeFeature.MaxRequestBodySize = limit.MaxBytes;
                }

                if (http.Request.ContentLength is long len && len > limit.MaxBytes)
                {
                    return ODataError(413, "RequestEntityTooLarge",
                        $"The request body ({len} bytes) exceeds the maximum allowed size ({limit.MaxBytes} bytes).");
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

        // Pre-compute static responses that are determined at startup.
        string metadataXml = BuildMetadataXml(registration.EdmModel);
        var serviceDocEntitySets = registration.Profiles
            .Select(p => new { name = p.EntitySetName, kind = "EntitySet", url = p.EntitySetName })
            .ToArray();

        // Service document -- lists available entity sets
        group.MapGet("", (HttpContext ctx) =>
        {
            string baseUrl = BuildBaseUrl(ctx, prefix);
            return Results.Ok(new Dictionary<string, object>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata",
                ["value"] = serviceDocEntitySets
            });
        }).ExcludeFromDescription();

        // $metadata -- CSDL XML describing the EDM model
        group.MapGet("/$metadata", () => Results.Content(metadataXml, "application/xml; charset=utf-8"))
            .ExcludeFromDescription();

        // loggerFactory resolved once at the top of this method (see groupLogger above).

        // One set of CRUD routes per registered profile
        foreach (var profile in registration.Profiles)
        {
            try
            {
                _mapEntitySetMethod
                    .MakeGenericMethod(profile.KeyType, profile.ModelType)
                    .Invoke(null, new object?[] { group, profile, registration, loggerFactory, startupJsonOptions });
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
        MapUnboundOperations(group, registration.UnboundOperations, startupJsonOptions);

        return group;
    }

    // Leg 3 (docs-fidelity): an unbound function/action's success response is the bare
    // Invoke() result (no @odata.context envelope — see MapUnboundOperations below), so the
    // most honest static schema available is the operation's own declared return type
    // (UnboundOperationDefinition.ReturnType/ReturnsCollection, already unwrapped from
    // Task&lt;T&gt;/ValueTask&lt;T&gt; and, for a collection return, down to its element type, at
    // registration time). A void/Task-returning operation has no 200 response at all — every
    // call to it produces 204 — so ReturnType is null there and only 204 is registered.
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
            else if (returnType != typeof(string) &&
                     typeof(System.Collections.IEnumerable).IsAssignableFrom(returnType) &&
                     returnType.GetInterfaces().Concat(new[] { returnType })
                         .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                                   && i.GetGenericArguments()[0] == typeof(TModel)))
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
        JsonSerializerOptions? jsonOptions)
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
                    return result is not null ? Results.Ok(result) : Results.NoContent();
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
                                    args[i] = val.Deserialize(param.ParameterType, jsonOptions);
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
                    return result is not null ? Results.Ok(result) : Results.NoContent();
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
                        BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                            $"Unbound.{opCapture.Name}", opCapture.Parameters),
                        Description = "JSON object with the action's parameters: " +
                            string.Join(", ", opCapture.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                    });
                }
            }
        }
    }

    internal static IResult ODataError(
        int status, string code, string message,
        string? target = null)
    {
        var errorObj = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (target is not null) errorObj["target"] = target;

        var body = new Dictionary<string, object> { ["error"] = errorObj };
        return status switch
        {
            400 => Results.BadRequest(body),
            404 => Results.NotFound(body),
            _ => Results.Json(body, statusCode: status)
        };
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

    // B1 fix (property allowlists): FilterProperties/OrderByProperties/SelectProperties/
    // ExpandProperties are wired into the EDM at startup via EntityTypeConfiguration.Filter/
    // .OrderBy/.Select/.Expand (EntitySetProfile.cs), which mark the non-allowlisted properties
    // NotFilterable/NotSortable/NotSelectable/NotExpandable in the model. Those restrictions are
    // only enforced when something calls ODataQueryOptions.Validate(...) — ApplyTo alone ignores
    // them. This settings object is deliberately permissive on every OTHER axis
    // (AllowedQueryOptions = All, no arithmetic/function/logical-operator restrictions, no node-
    // count/expansion-depth ceiling) so Validate's only effect here is the per-property
    // allowlist check the profile already declared. The coarse per-category enable/disable is
    // handled separately by CheckCollectionQueryOptionCapabilities with its own
    // "UnsupportedQueryOption" code and message; this only needs to surface *a* 400 (via the
    // existing ODataException catch clauses), so Microsoft's default validator wording is fine.
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
    // complexity limits (MaxExpansionDepth default 12, node counts 10000/1000/1000 as before) so an
    // implementor can tighten them per profile or globally via WithDefaults. AllowedQueryOptions=All
    // etc. is retained so the only checks these run are the per-property allowlist annotations and
    // the complexity ceilings — $top/$skip/$count keep their own dedicated enforcement (see the
    // ValidatePropertyAllowlists remark). MaxExpansionDepth is now enforced (was hardcoded 0/disabled):
    // a $expand nesting deeper than the limit is rejected with 400 rather than silently truncated.
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

    /// <remarks>
    /// This check is advisory, not atomic. Between the ETag read and the caller's write,
    /// another request may modify the resource. For true atomic concurrency, use
    /// data-store-level concurrency tokens (e.g., EF Core [Timestamp] / SQL WHERE RowVersion = @expected).
    /// The HTTP ETag mechanism provides a best-effort conflict signal, not a transaction guarantee.
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
        if (!ctx.Request.Headers.TryGetValue("If-Match", out var ifMatch)) return null;

        // RFC 7232 §3.1: If-Match may carry a comma-separated list of ETags.
        // The precondition is satisfied if the current ETag matches any one of them.
        var etagList = ParseETagList(ifMatch.ToString()).ToList();

        // m6: the existence check must happen before the wildcard short-circuit. Per
        // RFC 7232 §3.1 / Protocol §11.4.1.1, If-Match -- including "*" -- fails with 412 when
        // no current representation exists; it must NOT fall through to whatever 404 the
        // caller's own "not found" handling would otherwise produce.
        object? current = await requestSource.InvokeGetByIdAsync(parsedKey!, ct);
        if (current is null)
        {
            return ODataError(412, "PreconditionFailed",
                "If-Match precondition failed: the resource does not exist.");
        }

        if (etagList.Contains("*")) return null; // wildcard -- matches any existing representation

        string currentETag = requestSource.InvokeGetETag(current);
        if (!etagList.Contains(currentETag))
            return ODataError(412, "PreconditionFailed", "The ETag does not match the current resource version.");
        return null; // OK to proceed
    }

    // -- JsonNode $select post-processing helpers ---------------------------------

    // Fallback serializer used when no JsonOptions are configured: camelCase to match
    // ASP.NET Core's default HttpJsonOptions (PropertyNamingPolicy = CamelCase).
    // JsonArray nodes pre-serialised here are written as-is by Results.Ok, bypassing
    // the ASP.NET Core pipeline, so casing must be baked in at this stage.
    private static readonly JsonSerializerOptions _camelCaseSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Unified collection pipeline: Serialize → ETag → Expand → Select.
    // Serialises exactly once using the configured jsonOptions (falls back to camelCase
    // when jsonOptions is null, matching ASP.NET Core's default naming policy).
    private static async Task<(JsonArray Items, List<string>? SelectedProps)> ApplyCollectionPipelineAsync(
        object[] originalItems,
        ODataQueryOptions options,
        IEntitySetEndpointSource source,
        IEntitySetEndpointSource requestSource,
        JsonSerializerOptions? jsonOptions,
        IEdmEntityType? rootEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        CancellationToken ct)
    {
        // Stage 1: Serialize once using the configured naming policy.
        var serializerOptions = jsonOptions ?? _camelCaseSerializerOptions;
        JsonArray json = JsonSerializer.SerializeToNode(originalItems, serializerOptions)!.AsArray();

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

            await ExpandLevelAsync(
                rootItems, rootObjects, rootClause, requestSource, rootEdmType,
                registration, requestServices, serializerOptions, depth: 1, ct);
        }

        // Stage 3.5: Omit navigation properties that were not $expand'd (issue #176).
        // System.Text.Json serialises the entire CLR graph, so every declared navigation
        // leaks into the payload — as [] (collection) or null (single) when unloaded, or with
        // data when a sibling $expand pulled it in. OData JSON Format v4.01 §4.5.1 / §11.2.4.2
        // require a non-expanded navigation to be OMITTED entirely, never emitted inline. This
        // pass removes each un-expanded navigation and recurses into the expanded ones so their
        // own un-expanded navigations are stripped too (face 3). Runs after Stage 3 so freshly
        // injected expansions are present, and before Stage 4 so $select still has final say.
        OmitUnexpandedNavigations(json, rootEdmType, options.SelectExpand?.SelectExpandClause, source.ModelType, serializerOptions);

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
    private static void StripToSelectedProperties(IEnumerable<JsonObject> objects, List<string> selectedProps)
    {
        foreach (JsonObject obj in objects)
        {
            var toRemove = obj.Select(p => p.Key)
                             .Where(k => !k.StartsWith("@", StringComparison.Ordinal) &&
                                         !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                             .ToList();
            foreach (string? key in toRemove) obj.Remove(key);
        }
    }

    // Deepest nesting level ExpandLevelAsync will follow. The clause tree the OData parser builds
    // is already finite (bounded by the depth the client actually wrote in $expand), so this is
    // not needed for correctness on well-formed requests — it is a guard against a pathological /
    // adversarial request that nests $expand extremely deep (§11.2.4.2 places no hard cap, and
    // this framework disables Microsoft's MaxExpansionDepth validator). Beyond this depth the
    // deeper related entities are simply not loaded.
    internal const int MaxNestedExpandDepth = 12;

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
    private static async Task ExpandLevelAsync(
        IReadOnlyList<object> items,
        IReadOnlyList<JsonObject> jsonItems,
        SelectExpandClause clause,
        IEntitySetEndpointSource levelSource,
        IEdmEntityType? levelEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        JsonSerializerOptions serializerOptions,
        int depth,
        CancellationToken ct)
    {
        if (items.Count == 0 || depth > MaxNestedExpandDepth) return;

        // Cache the key PropertyInfo once per level (M-3 perf parity with the old inline loop).
        PropertyInfo? keyProp = items[0].GetType()
            .GetProperty(levelSource.KeyPropertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        foreach (ExpandedNavigationSelectItem expandItem in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string propName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;
            NavigationRouteDefinition? navRoute = levelSource.NavigationRoutes.FirstOrDefault(n =>
                string.Equals(n.PropertyName, propName, StringComparison.OrdinalIgnoreCase));
            if (navRoute is null) continue; // no handler registered for this navigation — cannot load it

            // Derive the expand key the way the serializer named the parent's property: honor a
            // per-property [JsonPropertyName] rename first (#184), then fall back to the naming
            // policy ("children" for camelCase, "Children" for PascalCase). Resolved off the actual
            // runtime entity type at this level. Must agree with OmitUnexpandedNavigations' key so
            // Stage 3.5 keeps (not strips) the expansion this injects.
            PropertyInfo? expandClrProp = items[0].GetType().GetProperty(
                propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            string expandKey = ResolveNavigationJsonKey(propName, expandClrProp, serializerOptions);

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

            // Inject the serialised related value onto each parent JsonObject.
            for (int i = 0; i < items.Count; i++)
            {
                jsonItems[i][expandKey] = relatedByIndex[i] is null
                    ? null
                    : JsonSerializer.SerializeToNode(relatedByIndex[i], serializerOptions);
            }

            SelectExpandClause? nestedClause = expandItem.SelectAndExpand;
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
                // Resolve the navigation target's entity set (its own NavigationRoutes drive the
                // next level) and its request-scoped source (nav handlers may capture scoped
                // dependencies such as a DbContext). Resolution is by EDM entity type rather than
                // NavigationSource so it works whether or not an explicit nav-source binding exists.
                IEdmEntityType? targetEdmType =
                    (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?.NavigationProperty?.ToEntityType();
                IEntitySetEndpointSource? targetSource = ResolveRequestSourceForEdmType(targetEdmType, registration, requestServices);

                if (targetSource is not null)
                {
                    await ExpandLevelAsync(
                        childItems, childObjects, nestedClause, targetSource, targetEdmType,
                        registration, requestServices, serializerOptions, depth + 1, ct);
                }
                // If the target set is not registered (no source), the deeper expansion cannot be
                // loaded here; Stage 3.5's OmitUnexpandedNavigations still keeps the (empty) nav per
                // the clause, mirroring the pre-#183 limitation for unregistered navigation targets.
            }

            // Apply this navigation's nested $select to the just-injected children (reuses the
            // root-level strip so casing / annotation handling are identical). Runs after the
            // deeper recursion so nested $expand keeps final say over what data is present, and
            // ExtractSelectedProperties preserves expanded nav names so they survive projection.
            if (hasNestedSelect)
            {
                List<string>? nestedSelected = ExtractSelectedProperties(nestedClause);
                if (nestedSelected is not null) StripToSelectedProperties(childObjects, nestedSelected);
            }
        }
    }

    // Finds the request-scoped endpoint source for a navigation target EDM entity type by matching
    // it to a registered profile's entity set, then resolving that profile from the request scope
    // (profiles are registered AddScoped). Returns null when no profile owns an entity set of that
    // type — e.g. a navigation whose target type is present in the model but not exposed as its own
    // entity set — in which case nested expansion of that navigation is not possible.
    private static IEntitySetEndpointSource? ResolveRequestSourceForEdmType(
        IEdmEntityType? targetEdmType, OhDataRegistration registration, IServiceProvider requestServices)
    {
        if (targetEdmType is null) return null;
        string targetName = targetEdmType.FullTypeName();
        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            IEdmEntityType? setType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (setType is not null && setType.FullTypeName() == targetName)
            {
                return requestServices.GetService(profile.GetType()) as IEntitySetEndpointSource;
            }
        }
        return null;
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
    private static void OmitUnexpandedNavigations(
        JsonNode? node,
        IEdmEntityType? edmType,
        SelectExpandClause? clause,
        Type? clrType,
        JsonSerializerOptions? serializerOptions)
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
                OmitUnexpandedNavigations(element, edmType, clause, clrType, serializerOptions);
            }
            return;
        }
        if (node is not JsonObject obj) return;

        // Navigation name → its nested $expand clause, for the navigations expanded at THIS level.
        // Presence means "keep and recurse"; absence means "remove".
        Dictionary<string, SelectExpandClause?>? expanded = null;
        if (clause is not null)
        {
            foreach (ExpandedNavigationSelectItem expandItem in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
            {
                string navName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;
                (expanded ??= new Dictionary<string, SelectExpandClause?>(StringComparer.OrdinalIgnoreCase))
                    [navName] = expandItem.SelectAndExpand;
            }
        }

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
            PropertyInfo? clrNavProp = clrType?.GetProperty(
                navProp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            string serializedKey = ResolveNavigationJsonKey(navProp.Name, clrNavProp, serializerOptions);

            if (expanded is not null && expanded.TryGetValue(navProp.Name, out SelectExpandClause? nested))
            {
                // Recurse into the expanded value to strip ITS un-expanded navigations. obj[key]
                // is null when the expanded single-valued nav had no related entity — the recursive
                // call no-ops on a null node, so no separate presence check is needed. The nested
                // CLR type (element type for a collection nav) carries [JsonPropertyName] resolution
                // one level deeper.
                OmitUnexpandedNavigations(obj[serializedKey], navProp.ToEntityType(), nested,
                    NavElementClrType(clrNavProp), serializerOptions);
            }
            else
            {
                obj.Remove(serializedKey);
            }
        }
    }

    // #184: resolve the JSON key a navigation property serializes to. A per-property
    // [System.Text.Json.Serialization.JsonPropertyName] rename wins (STJ emits it verbatim);
    // otherwise the naming policy converts the CLR name (and a null policy leaves it unchanged).
    private static string ResolveNavigationJsonKey(
        string navClrName, PropertyInfo? clrNavProp, JsonSerializerOptions? serializerOptions)
    {
        JsonPropertyNameAttribute? rename = clrNavProp?.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (rename is not null) return rename.Name;
        return serializerOptions?.PropertyNamingPolicy?.ConvertName(navClrName) ?? navClrName;
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

    // M3: appends the OData JSON §10.7/§10.8 projection suffix to a context segment when a
    // $select projection narrowed the response, e.g. "Widgets" -> "Widgets(Id,Name)". A no-op
    // (segment returned unchanged) when no projection is in effect.
    private static string AppendSelectSuffix(string segment, IReadOnlyList<string>? selectedProps) =>
        selectedProps is { Count: > 0 } ? $"{segment}({string.Join(",", selectedProps)})" : segment;

    // M-3: apply $orderby to a navigation collection's in-memory results. Consistent with how
    // $top/$skip are already applied on this path (property-name based, not pushed down to the
    // handler or to SQL). Supports multiple sort keys ("Prop1 asc,Prop2 desc") and is
    // case-insensitive on the property name so it works the same whether the client sends the
    // CLR (PascalCase) name or the camelCase name the response serializer emits. An unknown
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

            PropertyInfo? prop = navItemType?.GetProperty(
                propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (navItemType is not null && prop is null)
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
        JsonSerializerOptions? jsonOptions, IEdmEntityType? navElementEdmType)
    {
        var navSerializerOptions = jsonOptions ?? _camelCaseSerializerOptions;

        // #179: serialize the items up front (previously the no-$select path returned the raw CLR
        // objects) so un-expanded navigations on the nav element type can be stripped. Nav-collection
        // routes take no $expand, so every declared navigation on the element type is omitted per
        // OData JSON §4.5.1 / §11.2.4.2 — matching a top-level collection GET of that type instead
        // of leaking each item's whole CLR graph. Runs before $select so projection has final say.
        var json = JsonSerializer.SerializeToNode(itemArray, navSerializerOptions)!.AsArray();
        // #184: navItemType is the CLR element type, so [JsonPropertyName] renames on its
        // navigations are honored when computing which keys to omit.
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
                    if (navItemType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is null)
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
        JsonSerializerOptions? jsonOptions, string? odataId = null, string? etag = null,
        IEdmEntityType? omitNavsForType = null)
    {
        var serialized = JsonSerializer.SerializeToNode(entity, jsonOptions)!.AsObject();
        string baseUrl = BuildBaseUrl(ctx, prefix);

        // #176: on single-entity read responses, omit navigation properties that were not
        // $expand'd (there is no $expand here, so every declared navigation is stripped). Callers
        // that must keep the graph inline — deep-insert POST (§11.4.2.2) — pass no type and are
        // unaffected. See OmitUnexpandedNavigations for the spec citation.
        // #184: the concrete entity's CLR type carries [JsonPropertyName] renames on its
        // navigations, so omission keys off the same names the serializer just wrote.
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
        JsonSerializerOptions? jsonOptions, string? odataId = null, string? etag = null,
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
        JsonObject node = ODataEntityNode(ctx, prefix, contextSegment, entity, jsonOptions, odataId: odataId, etag: etag, omitNavsForType: omitNavsForType);
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

        // Cache ODataQueryContext and ODataQuerySettings once at startup so each request
        // does not allocate new instances. Both are read-only after construction.
        var cachedODataQueryContext = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
        var cachedCountSettings = new ODataQuerySettings();
        var cachedQuerySettings = new ODataQuerySettings { PageSize = source.MaxTop };
        // #202: per-entity-set complexity-guard settings (expansion depth + node counts).
        var cachedValidationSettings = BuildValidationSettings(source);

        // Priority 1: ODataEntitySetProfile with direct ODataQueryOptions handler
        if (source is IODataEntitySetEndpointSource odataSource && odataSource.HasGetODataQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    IResult? capabilityError = CheckCollectionQueryOptionCapabilities(ctx, source);
                    if (capabilityError is not null) return capabilityError;

                    var s = ResolveHandlers(ctx);
                    var odataSrc = (IODataEntitySetEndpointSource)s;
                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);
                    // B1 fix: enforce FilterProperties/OrderByProperties/SelectProperties/
                    // ExpandProperties allowlists before handing options to the profile — the
                    // profile's own ApplyTo call has no opportunity to reject a disallowed
                    // property since it never calls Validate() itself.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);
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
                    // The continuation link uses $skip (not the opaque $skiptoken the Priority-2 path
                    // emits): a Priority-1 profile applies the incoming ODataQueryOptions via ApplyTo,
                    // which natively honors $skip but throws on a $skiptoken it has no handler for.
                    // The profile applies $skip itself on the follow-up request; the framework then
                    // only re-applies the Take cap on top.
                    string? frameworkNextLink = null;
                    int? appliedPageSize = null;
                    if (odataResult.NextLink is null && options.Top is null)
                    {
                        int? preferredPageSize = ParseMaxPageSize(ctx);
                        appliedPageSize = preferredPageSize.HasValue
                            ? (source.MaxTop.HasValue
                                ? Math.Min(preferredPageSize.Value, source.MaxTop.Value)
                                : preferredPageSize.Value)
                            : source.MaxTop;

                        if (appliedPageSize.HasValue)
                            queryable = queryable.Take(appliedPageSize.Value);
                        if (preferredPageSize.HasValue)
                            ctx.Response.Headers["Preference-Applied"] = $"maxpagesize={appliedPageSize!.Value}";
                    }

                    object[] items = queryable.ToArray();

                    // Emit a $skip continuation link when a full page was returned under the cap
                    // (there may be more rows). The next offset is the profile-applied $skip on this
                    // request plus the page just returned.
                    if (appliedPageSize is int ps && ps > 0 && items.Length == ps)
                    {
                        int nextSkip = (options.Skip?.Value ?? 0) + items.Length;
                        frameworkNextLink = BuildNextPageLinkWithSkip(ctx, nextSkip);
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
                    return Results.Ok(envelope);
                }
                catch (Microsoft.OData.ODataException ex)
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
                  MaxTop: source.MaxTop));
        }
        // Priority 2: base GetQueryable (IQueryable without ODataQueryOptions)
        else if (source.HasGetQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    IResult? capabilityError = CheckCollectionQueryOptionCapabilities(ctx, source);
                    if (capabilityError is not null) return capabilityError;

                    var s = ResolveHandlers(ctx);
                    var queryable = (IQueryable<TModel>)(await s.InvokeGetQueryableAsync(ct))
                                    .Cast<TModel>();

                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);
                    // B1 fix: enforce FilterProperties/OrderByProperties/SelectProperties/
                    // ExpandProperties allowlists before any ApplyTo call below.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);

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
                        odataCount = countQ.LongCount();
                    }

                    // Apply filter/orderby/skip/top without $select so TModel shape is preserved.
                    // $select is handled via JsonNode post-processing to avoid ISelectExpandWrapper casing issues.
                    IQueryable<TModel> filtered = queryable;
                    if (options.Filter is not null)
                        filtered = (IQueryable<TModel>)options.Filter.ApplyTo(filtered, cachedQuerySettings);
                    if (options.OrderBy is not null)
                        filtered = (IQueryable<TModel>)options.OrderBy.ApplyTo(filtered, cachedQuerySettings);
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
                        catch
                        {
                            return ODataError(400, "InvalidSkipToken",
                                "The skiptoken value is invalid or has been corrupted.");
                        }
                    }

                    int effectiveSkip = tokenSkip ?? 0;
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

                        if (appliedPageSize.HasValue)
                            filtered = filtered.Take(appliedPageSize.Value);
                        if (preferredPageSize.HasValue)
                            ctx.Response.Headers["Preference-Applied"] = $"maxpagesize={appliedPageSize!.Value}";
                    }

                    var items = filtered.ToArray();

                    // Gap 3: compute nextLink when MaxTop (or preferred page size) is set and page is full
                    string? nextLink = null;
                    int effectivePageSize = appliedPageSize ?? 0;
                    if (effectivePageSize > 0 && items.Length == effectivePageSize && options.Top is null)
                    {
                        int nextSkip = effectiveSkip + items.Length;
                        string token = Convert.ToBase64String(BitConverter.GetBytes(nextSkip));
                        nextLink = BuildNextPageLink(ctx, token);
                    }

                    var (finalItems, selectedProps) = await ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{AppendSelectSuffix(name, selectedProps)}";
                    if (odataCount.HasValue) envelope["@odata.count"] = odataCount;
                    // Gap 3: add nextLink to envelope
                    if (nextLink is not null) envelope["@odata.nextLink"] = nextLink;
                    envelope["value"] = finalItems;
                    return Results.Ok(envelope);
                }
                catch (Microsoft.OData.ODataException ex)
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
                  MaxTop: source.MaxTop));
        }
        else if (source.HasGetAll)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var s = ResolveHandlers(ctx);
                    logger?.LogDebug("GET {Prefix}/{Name}", prefix, name);

                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);

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
                        return Results.Ok(searchEnvelope);
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
                    return Results.Ok(envelope);
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
                  MaxTop: source.MaxTop));
        }

        bool hasCountSource = (source is IODataEntitySetEndpointSource odsCheck && odsCheck.HasGetODataQueryable)
            || source.HasGetQueryable || source.HasGetAll;
        if (hasCountSource)
        {
            entityGroup.MapGet("/$count", async (HttpContext ctx, CancellationToken ct) =>
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
                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);
                    // B1 fix: enforce the FilterProperties allowlist here too.
                    ValidatePropertyAllowlists(options, cachedValidationSettings);

                    if (s is IODataEntitySetEndpointSource odataCountSrc && odataCountSrc.HasGetODataQueryable)
                    {
                        // Priority 1 profiles apply query options themselves; don't re-apply $filter.
                        var countResult = await odataCountSrc.InvokeGetODataQueryableAsync(options, ct);
                        var queryable = countResult.Items is IQueryable<TModel> tq
                            ? tq
                            : countResult.Items.Cast<TModel>().AsQueryable();
                        return Results.Content(queryable.LongCount().ToString(), "text/plain");
                    }
                    if (source.HasGetQueryable)
                    {
                        var q = (IQueryable<TModel>)(await s.InvokeGetQueryableAsync(ct)).Cast<TModel>();
                        var filtered = options.Filter is not null
                            ? (IQueryable<TModel>)options.Filter.ApplyTo(q, cachedCountSettings)
                            : q;
                        filtered = ApplyRoundingMode(filtered, source.RoundingMode);
                        return Results.Content(filtered.LongCount().ToString(), "text/plain");
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
            }).WithTags(name).Produces<long>(200, "text/plain").Produces(400)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: source.FilterEnabled,
                  OrderByEnabled: false,
                  SelectEnabled: false,
                  ExpandEnabled: false,
                  CountEnabled: true,
                  SearchEnabled: false,
                  MaxTop: null));
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
                        options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);
                        // B1 fix: enforce SelectProperties/ExpandProperties allowlists.
                        ValidatePropertyAllowlists(options, cachedValidationSettings);
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
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({ODataEntityKeyUrlFormatter.Format(parsedKey!)})";

                    if (hasExpand && options is not null)
                    {
                        // Reuse the collection pipeline (Serialize → ETag → Expand → Select) on a
                        // single-element array so GetById gets the same expand/batch-handler/
                        // select behavior as GET /{Set}, instead of a bespoke reimplementation.
                        var (expandedItems, expandSelectedProps) =
                            await ApplyCollectionPipelineAsync(new[] { result }, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct);
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

                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: etagValue, selectedProps: selectedProps, omitNavsForType: rootEdmType);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
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
                  MaxTop: null));
        }

        if (source.HasPost)
        {
            // Deep insert (§32/§11.4.2.2): precomputed once at startup (not per-request) — the
            // set of TModel navigation properties (declared via HasOptional/HasRequired/HasMany)
            // that must be nulled out before invoking Post when AllowDeepInsert is disabled
            // (the default). System.Text.Json already binds nested navigation values into these
            // properties during deserialization; stripping them here is what keeps a Post
            // handler that doesn't expect a graph from silently persisting only part of one.
            // Properties without a public setter can't be deserialized into by STJ in the first
            // place, so they're excluded — nothing to strip.
            PropertyInfo[] deepInsertNavPropsToStrip = typeof(TModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => source.NavigationPropertyNames.Contains(p.Name) && p.SetMethod is not null)
                .ToArray();

            // If-None-Match on POST is not supported: the framework cannot extract the key from
            // the body without knowing the key property. Developers should handle this themselves.
            var rb = entityGroup.MapPost("", async (HttpContext ctx, CancellationToken ct) =>
            {
                if (!IsJsonContentType(ctx)) return UnsupportedMediaTypeError(ctx);

                JsonDocument document;
                try
                {
                    document = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
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
                            "endpoints to link an existing entity, or enable AllowDeepInsert to " +
                            "create nested related entities inline (OData §11.4.2.2).");
                    }

                    TModel? model;
                    try
                    {
                        model = document.RootElement.Deserialize<TModel>(jsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        return ODataError(400, "InvalidBody", ex.Message);
                    }

                    if (model is null)
                        return ODataError(400, "InvalidBody", "Request body is empty or could not be deserialized.");

                    // Deep insert (§32): strip nested navigation values unless the profile opted
                    // in via AllowDeepInsert. Nested values for non-navigation (plain) collection
                    // properties are untouched — only CLR properties declared as navigations via
                    // HasOptional/HasRequired/HasMany are stripped.
                    if (!source.AllowDeepInsert)
                    {
                        foreach (var navProp in deepInsertNavPropsToStrip)
                        {
                            navProp.SetValue(model, null);
                        }
                    }

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

                        // §8.2.8.7: Prefer: return=representation — explicit opt-in; already the default behaviour.
                        // Acknowledge the preference in the response header when present.
                        if (ctx.Request.Headers.TryGetValue("Prefer", out var postPrefer)
                            && postPrefer.ToString().Contains("return=representation", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.Headers["Preference-Applied"] = "return=representation";
                        }

                        // Gap 5: include @odata.id in POST response body
                        // Gap 2: include @odata.etag in body
                        // Deep insert (§32): when AllowDeepInsert is true, `result` (the handler's
                        // return value) may carry nested navigation values populated by the
                        // handler — SerializeToNode below serializes them inline automatically,
                        // satisfying §11.4.2.2's "return the created entity with related entities."
                        var createdNode = ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, odataId: odataId, etag: postEtag);
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
                    TModel? model = await JsonSerializer.DeserializeAsync<TModel>(ctx.Request.Body, jsonOptions, ct);
                    if (model is null)
                        return ODataError(400, "InvalidBody", "Request body is empty or could not be deserialized.");
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
                            string upsertOdataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({ODataEntityKeyUrlFormatter.Format(parsedKey!)})";
                            ctx.Response.Headers.Location = upsertOdataId;
                            // V1/§8.3.4: OData-EntityId is REQUIRED on the 204 response of an
                            // upsert-PUT that created the entity. A plain update-PUT must NOT
                            // carry this header — it only applies when a new entity was created.
                            ctx.Response.Headers["OData-EntityId"] = upsertOdataId;
                        }
                        return Results.NoContent();
                    }

                    // §8.2.8.7: Prefer: return=representation — explicit opt-in; already the default behaviour.
                    if (ctx.Request.Headers.TryGetValue("Prefer", out var putPrefer)
                        && putPrefer.ToString().Contains("return=representation", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Response.Headers["Preference-Applied"] = "return=representation";
                    }

                    // Gap 5: include @odata.id in PUT response
                    // Gap 2: include @odata.etag in body
                    // S4 fix: canonical, URL-safe key literal built from parsedKey (see GetById above).
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({ODataEntityKeyUrlFormatter.Format(parsedKey!)})";
                    if (wasCreated)
                        return Results.Created(odataId, ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, odataId: odataId, etag: putEtag));
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: putEtag);
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404).Produces(415)
              .WithMetadata(new OhDataRequestBodyMetadata
              {
                  BodyType = typeof(TModel),
                  Description = $"The full {name} entity representation to replace the existing resource with."
              });
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

                    // Only validate key mismatch if the key property was explicitly present in the body.
                    // PATCH is a partial update -- the key may be omitted. URL key is authoritative.
                    if (TryGetJsonProperty(body, source.KeyPropertyName, out JsonElement keyEl))
                    {
                        string bodyKeyStr = keyEl.ToString();
                        string parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                        if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                            return ODataError(400, "BadRequest", "Key in URL does not match key in request body.", target: "key");
                    }

                    // ETag check via If-Match header -- handler owns fetch-for-merge.
                    var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                    if (etagCheck is not null) return etagCheck;

                    // Build Delta<TModel>: only properties present in the request body are set.
                    // The handler is responsible for fetching the existing entity and applying
                    // the delta -- call delta.Patch(existing) to apply changed fields in-place.
                    var patchDelta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                    foreach (var prop in body.EnumerateObject())
                    {
                        var clrProp = typeof(TModel).GetProperty(prop.Name,
                            BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                        if (clrProp is not null)
                        {
                            object? value = prop.Value.Deserialize(clrProp.PropertyType, jsonOptions);
                            patchDelta.TrySetPropertyValue(clrProp.Name, value);
                        }
                    }

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

                    // §8.2.8.7: Prefer: return=representation -- explicit opt-in; already the default behaviour.
                    if (ctx.Request.Headers.TryGetValue("Prefer", out var patchPrefer)
                        && patchPrefer.ToString().Contains("return=representation", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Response.Headers["Preference-Applied"] = "return=representation";
                    }

                    // Gap 5: include @odata.id in PATCH response
                    // Gap 2: include @odata.etag in body
                    // S4 fix: canonical, URL-safe key literal built from parsedKey (see GetById above).
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({ODataEntityKeyUrlFormatter.Format(parsedKey!)})";
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: patchEtag);
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
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
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces(204).Produces(400).Produces(404);
        }

        // Startup route-collision validation: POST /{name}({key})/{segment}.
        // A navigation property registered with a `post` handler (PostChild) claims
        // POST /{name}({key})/{nav.PropertyName} (creating a related entity, §11.4.2.1). An
        // entity-level bound action claims POST /{name}({key})/{action.Name} for the same
        // template shape. Unlike the structural-property-vs-bound-function check above (GET vs.
        // GET), these are both POST, so a shared name is a genuine route collision that ASP.NET
        // Core would only surface as an ambiguous-match failure at request time. Catch it at
        // startup instead, matching the existing idiom.
        foreach (var navWithPost in source.NavigationRoutes.Where(n => n.PostChild is not null))
        {
            foreach (var collidingAction in source.BoundActions.Where(a =>
                a.IsEntityLevel && string.Equals(navWithPost.PropertyName, a.Name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Entity set '{name}': bound action '{collidingAction.Name}' conflicts with the " +
                    $"POST handler of navigation property '{navWithPost.PropertyName}' on " +
                    $"POST /{name}({{key}})/{collidingAction.Name}. Rename the bound action or the navigation property.");
            }
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
                            var (navEnv, navEnvError) = BuildNavEnvelope(baseUrl, name, key, navPropertyName, navCount, itemArray, ctx, navItemType, jsonOptions, navTargetEdmType);
                            if (navEnvError is not null) return navEnvError;
                            return Results.Ok(navEnv);
                        }
                        // M1: single-valued navigation results must carry @odata.context too
                        // (JSON §4.5), mirroring what the collection branch above already does.
                        // #179: pass the nav target's EDM type so the related entity's own
                        // un-expanded navigations are omitted (§4.5.1 / §11.2.4.2), matching a
                        // top-level read of that type instead of leaking the full CLR graph.
                        return Results.Ok(ODataEntityNode(ctx, prefix, $"{name}({key})/{navPropertyName}/$entity", result, jsonOptions, omitNavsForType: navTargetEdmType));
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
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
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                        }
                    })
                    .WithTags(name)
                    .Produces<long>(200, "text/plain")
                    .Produces(404);
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
                                            // S4 fix: canonical, URL-safe key literal (quoted + percent-encoded for string keys).
                                            string formattedKey = ODataEntityKeyUrlFormatter.Format(k);
                                            refs.Add(new Dictionary<string, string>
                                            {
                                                ["@odata.id"] = $"{baseUrl}/{refNavCapture.ChildEntitySetName}({formattedKey})"
                                            });
                                        }
                                    }
                                }
                                return Results.Ok(new Dictionary<string, object?>
                                {
                                    ["@odata.context"] = context,
                                    ["value"] = refs
                                });
                            }

                            // No ChildEntitySetName/ChildKeyPropertyName configured — return minimal
                            // envelope. Use HasMany(..., refTargetEntitySet: "...") to enable
                            // populated @odata.id references.
                            return Results.Ok(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = context,
                                ["value"] = System.Array.Empty<object>()
                            });
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
                                        // S4 fix: canonical, URL-safe key literal (quoted + percent-encoded for string keys).
                                        string childKey = ODataEntityKeyUrlFormatter.Format(k);
                                        return Results.Ok(new Dictionary<string, object?>
                                        {
                                            ["@odata.context"] = context,
                                            ["@odata.id"] = $"{baseUrl}/{refNavCapture.ChildEntitySetName}({childKey})"
                                        });
                                    }
                                }
                            }

                            return Results.Ok(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = context
                            });
                        }
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                    }
                })
                .WithTags(name)
                .Produces(200,
                    navRefIsCollection ? typeof(ODataRefCollectionResponse) : typeof(ODataRefResponse),
                    "application/json");

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
                        string relatedId = odataIdEl.GetString() ?? "";
                        await requestNav.AddRef!(parsedKey!, (object)relatedId, ct);
                        return Results.NoContent();
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                    }
                }

                var refBodyMetadata = new OhDataRequestBodyMetadata
                {
                    BodyType = typeof(ODataRefWriteRequest),
                    Description = $"A reference to the entity to link as {navRefPropertyName}."
                };

                if (navRefIsCollection)
                {
                    entityAuthGroup.MapPost($"/{name}({{key}})/{navRefPropertyName}/$ref", handleAddOrSetRef)
                        .WithTags(name)
                        .Produces(204)
                        .Produces(400)
                        .Produces(415)
                        .WithMetadata(refBodyMetadata);
                }
                else
                {
                    entityAuthGroup.MapPut($"/{name}({{key}})/{navRefPropertyName}/$ref", handleAddOrSetRef)
                        .WithTags(name)
                        .Produces(204)
                        .Produces(400)
                        .Produces(415)
                        .WithMetadata(refBodyMetadata);
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
                            // For DELETE $ref on collection nav, the related id may come from query param $id
                            string relatedId = ctx.Request.Query.TryGetValue("$id", out var idVal)
                                ? idVal.ToString()
                                : "";
                            await requestNav.RemoveRef!(parsedKey!, (object)relatedId, ct);
                            return Results.NoContent();
                        }
                        catch (FormatException ex)
                        {
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                        }
                    })
                    .WithTags(name)
                    .Produces(204)
                    .Produces(400);
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
                entityAuthGroup.MapPost($"/{name}({{key}})/{postNavPropertyName}",
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
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                        }

                        object? child;
                        try
                        {
                            child = await JsonSerializer.DeserializeAsync(ctx.Request.Body, postNavItemType, jsonOptions, ct);
                        }
                        catch (JsonException ex)
                        {
                            return ODataError(400, "InvalidBody", ex.Message);
                        }

                        if (child is null)
                            return ODataError(400, "InvalidBody", "Request body is empty or could not be deserialized.");

                        var s = ResolveHandlers(ctx);
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
                                // S4 fix: canonical, URL-safe key literal (quoted + percent-encoded for string keys).
                                string formattedChildKey = ODataEntityKeyUrlFormatter.Format(childKeyVal);
                                childOdataId = $"{baseUrl}/{postNavCapture.ChildEntitySetName}({formattedChildKey})";
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

                        if (ctx.Request.Headers.TryGetValue("Prefer", out var postNavPrefer)
                            && postNavPrefer.ToString().Contains("return=representation", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.Headers["Preference-Applied"] = "return=representation";
                        }

                        // When the target entity set is known, the context matches the child's
                        // own entity set (as if fetched via GET /{ChildEntitySet}({key})); otherwise
                        // fall back to a context scoped to the navigation path.
                        string contextSegment = postNavCapture.ChildEntitySetName is not null
                            ? $"{postNavCapture.ChildEntitySetName}/$entity"
                            : $"{name}({key})/{postNavPropertyName}/$entity";
                        var createdNode = ODataEntityNode(ctx, prefix, contextSegment, created, jsonOptions, odataId: childOdataId);
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
            }
        }

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
            foreach (var collidingFn in source.BoundFunctions.Where(f => f.IsEntityLevel))
            {
                bool propertyNameCollision = source.StructuralProperties
                    .Any(p => string.Equals(p.Name, collidingFn.Name, StringComparison.Ordinal));
                if (propertyNameCollision)
                {
                    throw new InvalidOperationException(
                        $"Entity set '{name}': bound function '{collidingFn.Name}' conflicts with " +
                        $"structural property '{collidingFn.Name}' on GET /{name}({{key}})/{collidingFn.Name}. " +
                        "Rename the bound function or the property.");
                }
            }

            foreach (var propCapture in source.StructuralProperties)
            {
                // GET /{name}({key})/{Property} — property-value envelope (§11.2.6).
                entityAuthGroup.MapGet($"/{name}({{key}})/{propCapture.Name}",
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
                            return Results.Ok(envelope);
                        }
                        catch (FormatException ex)
                        {
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                        }
                    })
                    .WithTags(name)
                    .Produces(200, typeof(ODataPropertyResponse<>).MakeGenericType(propCapture.ClrType), "application/json")
                    .Produces(204)
                    .Produces(404);

                // GET /{name}({key})/{Property}/$value — raw value (Part 2 §4.7).
                bool propIsComplex = propCapture.IsComplex;
                entityAuthGroup.MapGet($"/{name}({{key}})/{propCapture.Name}/$value",
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
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                        }
                    })
                    .WithTags(name)
                    // Leg 3 (docs-fidelity): the raw $value body is either text/plain (every
                    // scalar type, via FormatRawValue) or application/octet-stream (byte[]
                    // properties only) — never JSON.
                    .Produces<string>(200, "text/plain", "application/octet-stream")
                    .Produces(400)
                    .Produces(404);
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
            foreach (var propCapture in source.StructuralProperties)
            {
                string propName = propCapture.Name;
                bool propIsNullable = propCapture.IsNullable;
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
                    entityAuthGroup.MapPut($"/{name}({{key}})/{propName}", (string key) => KeyImmutableError())
                        .WithTags(name).Produces(400);
                    entityAuthGroup.MapMethods($"/{name}({{key}})/{propName}", PatchMethod, (string key) => KeyImmutableError())
                        .WithTags(name).Produces(400);
                    entityAuthGroup.MapDelete($"/{name}({{key}})/{propName}", (string key) => KeyImmutableError())
                        .WithTags(name).Produces(400);
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

                        object? newValue;
                        try
                        {
                            newValue = valueEl.ValueKind == JsonValueKind.Null
                                ? null
                                : valueEl.Deserialize(propClrType, jsonOptions);
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
                        if (!delta.TrySetPropertyValue(propName, newValue))
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
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                }

                var propertyWriteBodyMetadata = new OhDataRequestBodyMetadata
                {
                    BodyType = typeof(ODataPropertyWriteRequest<>).MakeGenericType(propClrType),
                    Description = $"The new value for '{propName}', wrapped in a 'value' member."
                };

                entityAuthGroup.MapPut($"/{name}({{key}})/{propName}",
                    (string key, HttpContext ctx, CancellationToken ct) => HandleSetPropertyAsync(key, ctx, ct, isPatchVerb: false))
                    .WithTags(name).Produces(204).Produces(400).Produces(404).Produces(412).Produces(415)
                    .WithMetadata(propertyWriteBodyMetadata);

                entityAuthGroup.MapMethods($"/{name}({{key}})/{propName}", PatchMethod,
                    (string key, HttpContext ctx, CancellationToken ct) => HandleSetPropertyAsync(key, ctx, ct, isPatchVerb: true))
                    .WithTags(name).Produces(204).Produces(400).Produces(404).Produces(412).Produces(415)
                    .WithMetadata(propertyWriteBodyMetadata);

                // DELETE — set the property to null (§11.4.9.3). Non-nullable is a structural
                // (static, per-type) validation, checked before touching the data source at all —
                // the same "cheap check first" pattern used for the key-immutable stub above.
                entityAuthGroup.MapDelete($"/{name}({{key}})/{propName}", async (string key, HttpContext ctx, CancellationToken ct) =>
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
                        delta.TrySetPropertyValue(propName, null);
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
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                }).WithTags(name).Produces(204).Produces(400).Produces(404).Produces(412);
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
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, s);
            }).WithTags(name).Produces(400);
            AddBoundOperationProduces<TModel>(rb, fnCapture);
            // Issue #181: document the function's query-string parameters.
            var boundFnQueryParams = BuildFunctionQueryParametersMetadata(fnCapture.Parameters, skipKey: false);
            if (boundFnQueryParams is not null) rb.WithMetadata(boundFnQueryParams);
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
                                args[i] = val.Deserialize(param.ParameterType, jsonOptions);
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
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, s);
            }).WithTags(name).Produces(400).Produces(415);
            AddBoundOperationProduces<TModel>(rb, actionCapture);
            // Leg 2 / #184: synthesize a POCO body schema from the action's parameters (see the
            // matching comment on the unbound-action branch of MapUnboundOperations).
            if (actionCapture.Parameters.Length > 0)
            {
                rb.WithMetadata(new OhDataRequestBodyMetadata
                {
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        $"{name}.{actionCapture.Name}", actionCapture.Parameters),
                    Description = "JSON object with the action's parameters: " +
                        string.Join(", ", actionCapture.Parameters.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                });
            }
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
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, s);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                })
                .WithTags(name).Produces(400);
            AddBoundOperationProduces<TModel>(rb, fnCapture);
            // Issue #181: document the function's query-string parameters (skip the leading key,
            // which is a route parameter already documented via BindingSource.Path).
            var entityFnQueryParams = BuildFunctionQueryParametersMetadata(fnCapture.Parameters, skipKey: true);
            if (entityFnQueryParams is not null) rb.WithMetadata(entityFnQueryParams);
        }

        // Gap 7: Entity-level bound actions — POST /{name}({key})/{action.Name}
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
                                        args[i] = val.Deserialize(param.ParameterType, jsonOptions);
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
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, s);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
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
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        $"{name}.{actionCapture.Name}.Entity", bodyParams),
                    Description = "JSON object with the action's parameters: " +
                        string.Join(", ", bodyParams.Select(p => $"{p.Name} ({p.ParameterType.Name})")) + "."
                });
            }
        }

    }

    // Gap 1: Wrap bound operation result with @odata.context when return type matches TModel (§11.5.3).
    // For collection results (IEnumerable<TModel>): context = {root}/$metadata#{EntitySet}
    // For single results (TModel): context = {root}/$metadata#{EntitySet}/$entity
    // For primitives/other types: return Results.Ok directly (no wrapping needed).
    private static IResult WrapBoundOpResult(
        HttpContext ctx, string prefix, string entitySetName, object result, Type modelType,
        JsonSerializerOptions? jsonOptions, IEdmEntityType? rootEdmType, IEntitySetEndpointSource source)
    {
        var resultType = result.GetType();

        // Check for collection of TModel
        bool isCollectionOfModel = false;
        if (resultType != typeof(string))
        {
            foreach (var iface in new[] { resultType }.Concat(resultType.GetInterfaces()))
            {
                if (iface.IsGenericType
                    && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    && iface.GetGenericArguments()[0] == modelType)
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
            var serializerOptions = jsonOptions ?? _camelCaseSerializerOptions;
            var json = JsonSerializer.SerializeToNode(coll, serializerOptions)!.AsArray();
            if (source.HasETag)
            {
                InjectETagsIntoJsonArray(json, coll, source);
            }
            OmitUnexpandedNavigations(json, rootEdmType, clause: null, modelType, serializerOptions);

            return Results.Ok(new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata#{entitySetName}",
                ["value"] = json
            });
        }

        // Check for single TModel
        if (resultType == modelType || modelType.IsAssignableFrom(resultType))
        {
            // #179: a single-TModel bound-op result rides the same omission + ETag path as GetById
            // so its shape matches a top-level read — un-expanded navigations stripped (§4.5.1 /
            // §11.2.4.2) and @odata.etag injected when UseETag is set.
            string? boundOpEtag = source.HasETag ? source.InvokeGetETag(result) : null;
            return ODataEntityResult(ctx, prefix, entitySetName, result, jsonOptions,
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
            return Results.Ok(new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{primitiveBaseUrl}/$metadata#{edmTypeName}",
                ["value"] = result
            });
        }

        // Primitive/other — no context wrapping
        return Results.Ok(result);
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

