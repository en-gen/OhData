using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        // Resolve JsonOptions once at startup so handlers don't pay DI lookup per request.
        var startupJsonOptions = routes.ServiceProvider
            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            ?.Value?.SerializerOptions;

        // #226: registration-wide ignored-property suppression. Validates same-model-type
        // conflicts, then — only when at least one profile declares ignores — derives a single
        // options instance whose resolver modifier removes the ignored members. When no profile
        // ignores anything the original options are threaded through unchanged (zero delta).
        var ignoredByType = IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap(registration.Profiles);
        JsonSerializerOptions? effectiveJsonOptions = ignoredByType.Count == 0
            ? startupJsonOptions
            : IgnoredPropertyJsonOptions.Build(startupJsonOptions ?? _camelCaseSerializerOptions, ignoredByType);

        // Resolved once here (rather than down at the per-profile loop) so the group-level
        // exception filter below can log through the same "OhData" category every other
        // handler uses.
        var loggerFactory = routes.ServiceProvider.GetService<ILoggerFactory>();
        var groupLogger = loggerFactory?.CreateLogger("OhData");

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
        MapUnboundOperations(group, registration.UnboundOperations, effectiveJsonOptions);

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
        CancellationToken ct,
        HashSet<string>? pushedLevelsNavNames = null)
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
        OmitUnexpandedNavigations(json, rootEdmType, options.SelectExpand?.SelectExpandClause, source.ModelType, serializerOptions,
            activeLevels: null, maxLevels: source.MaxExpansionDepth, levelsNavNames: pushedLevelsNavNames);

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
                    int resolved = lv.IsMaxLevel ? maxLevels : (int)Math.Min(lv.Level, maxLevels);
                    (levelsRemaining ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))[navName] =
                        Math.Min(Math.Max(resolved, 1), maxLevels);
                }
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

            SelectExpandClause? nested = null;
            bool explicitlyExpanded = expanded is not null &&
                expanded.TryGetValue(navProp.Name, out nested);
            // #206 ($levels): keep the self-referential nav even without an explicit nested item when a
            // parent $levels expansion still has recursion budget for it.
            bool keptByLevels = !explicitlyExpanded && activeLevels is { } al &&
                string.Equals(al.Nav, navProp.Name, StringComparison.OrdinalIgnoreCase) && al.Remaining > 0;

            if (explicitlyExpanded || keptByLevels)
            {
                // Recurse into the expanded value to strip ITS un-expanded navigations. obj[key]
                // is null when the expanded single-valued nav had no related entity — the recursive
                // call no-ops on a null node, so no separate presence check is needed. The nested
                // CLR type (element type for a collection nav) carries [JsonPropertyName] resolution
                // one level deeper.
                SelectExpandClause? nestedClause = explicitlyExpanded ? nested : null;
                // Resolve the levels budget carried into the next recursion: a fresh $levels=N at this
                // level seeds N-1; otherwise the inherited budget decrements. When it reaches 0 the nav
                // is kept at this level but its own self-reference below is stripped (depth honored).
                int? nextLevels =
                    levelsRemaining is not null && levelsRemaining.TryGetValue(navProp.Name, out int freshLevels)
                        ? freshLevels - 1
                        : (keptByLevels ? activeLevels!.Value.Remaining - 1 : (int?)null);
                (string, int)? childActive = nextLevels is int nl && nl > 0 ? (navProp.Name, nl) : null;

                OmitUnexpandedNavigations(obj[serializedKey], navProp.ToEntityType(), nestedClause,
                    NavElementClrType(clrNavProp), serializerOptions, childActive, maxLevels, levelsNavNames);
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
        ODataQuerySettings? binderSettings = null)
    {
        if (!hasParameterlessCtor)
        {
            logger?.LogDebug(
                "OhData: $select pushdown skipped for {EntitySet}: {Model} has no public parameterless constructor.",
                source.EntitySetName, typeof(TModel).Name);
            return query;
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
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: UseETag selector property names are unknowable (non-direct selector).",
                    source.EntitySetName);
                return query;
            }

            foreach (string name in source.ETagPropertyNames)
            {
                if (!structuralByName.TryGetValue(name, out StructuralPropertyInfo? etagProp))
                {
                    logger?.LogDebug(
                        "OhData: $select pushdown skipped for {EntitySet}: UseETag property '{Property}' is not a structural property.",
                        source.EntitySetName, name);
                    return query;
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
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: '{Property}' is complex-typed (owned-entity projection is a phase-1 boundary).",
                    source.EntitySetName, member.Name);
                return query;
            }

            if (member.Property.SetMethod is not { IsPublic: true })
            {
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: '{Property}' has no public setter.",
                    source.EntitySetName, member.Name);
                return query;
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
                    Expression access = BuildShapedNavAccess(x, nav, edmModel!, binderSettings!);
                    bindings.Add(Expression.Bind(nav.Binding.Property, access));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A nested expand clause Microsoft's binder cannot translate (an unusual $filter/
                // $orderby shape) must not become a 500: abandon pushdown by returning the query
                // unchanged. The caller detects this by reference equality and falls back, so the
                // delegate-less navigation stays EDM-only for the request.
                logger?.LogDebug(ex,
                    "OhData: $expand pushdown skipped for {EntitySet}: a nested expand option could not be bound; delegate-less navigations stay EDM-only for this request.",
                    source.EntitySetName);
                return query;
            }
        }

        Expression<Func<TModel, TModel>> projection = Expression.Lambda<Func<TModel, TModel>>(
            Expression.MemberInit(Expression.New(typeof(TModel)), bindings), x);
        return query.Select(projection);
    }

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

    // #206 phase 2 (optioned expand): the OData filter/orderby binders are stateless — all per-bind
    // state flows through the QueryBinderContext argument — so a single shared instance is reused
    // across requests (matching this file's cache-the-reflection-machinery ethos).
    private static readonly FilterBinder _filterBinder = new();
    private static readonly OrderByBinder _orderByBinder = new();

    // #206 phase 2 (optioned + multi-level expand): one delegate-less navigation the request
    // $expand'd, resolved for pushdown. Carries the startup binding plus the request's parsed nested
    // clauses. Filter/OrderBy/Skip/Top are pushed to SQL via BuildShapedNavAccess; Count and
    // NestedSelect are applied afterward on the serialized JSON (ShapePushedExpandsInJson) so the
    // wire stays camelCase plain-POCO — no SelectExpandWrapper ever reaches the serializer. When
    // Count is requested, Skip/Top are DEFERRED to the JSON pass instead of SQL so the emitted
    // Nav@odata.count reflects the full filtered collection (OData §11.2.4.2), not the page.
    // <para>#206 (recursion): <c>Children</c> holds each pushed nested $expand one level deeper —
    // folded into the same JOIN'd query as an element-wise projection (EF ThenInclude). A branch is
    // only recorded here when it is delegate-less AND pushable AT EVERY level; a delegate-backed (or
    // otherwise non-pushable) nested nav defers the whole parent off pushdown (see
    // TryBuildEngagedExpand), so a pushed branch can never EF-include a delegate navigation — the
    // delegate-safety invariant holds at any depth by construction. <c>Levels</c> (&gt; 0) marks a
    // <c>$levels=N</c> self-referential expand recursed N deep against the same <c>Binding</c>;
    // <c>Children</c> is then null (the recursion re-uses this binding).</para>
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
    // at every level. $levels is handled by the caller via BuildLevelsNavBinding; a $levels item that
    // also carries other nested options is deferred (only structural recursion is pushed for $levels).
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

        // $levels: pure structural recursion only (the caller resolved the self-referential binding
        // via BuildLevelsNavBinding). A $levels expand combined with other nested options is deferred
        // to keep the recursion simple and cycle-safe — a rare combination, and graceful (EDM-only).
        if (item.LevelsOption is not null)
        {
            SelectExpandClause? lc = item.SelectAndExpand;
            bool hasOtherOptions =
                item.FilterOption is not null || item.OrderByOption is not null ||
                item.CountOption == true || item.SkipOption is not null || item.TopOption is not null ||
                (lc is not null && (ExtractSelectedProperties(lc) is not null ||
                                    lc.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any()));
            if (hasOtherOptions) return false;

            int levels = item.LevelsOption.IsMaxLevel ? remainingDepth : (int)item.LevelsOption.Level;
            levels = Math.Min(levels, remainingDepth);
            if (levels < 1) return false;
            if (!IsMemberInitProjectable(binding.ElementType, model)) return false;

            engaged = new EngagedExpand(binding, null, null, null, null, false, null, Children: null, Levels: levels);
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

            // Delegate-safety at depth: resolve the element type's own profile (if any). A nested nav
            // that the element type's profile declares WITH a delegate must never be EF-included, so
            // the whole parent branch is deferred (it then resolves via the existing delegate/EDM path).
            // When the element type is not its own entity set (no profile), no delegate can exist for
            // its navigations, so they are inherently delegate-less and eligible if otherwise pushable.
            IEntitySetEndpointSource? childSource = FindStartupSourceForClrType(binding.ElementType, registration);
            var childRouteBacked = childSource is not null
                ? new HashSet<string>(childSource.NavigationRoutes.Select(r => r.PropertyName), StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (ExpandedNavigationSelectItem childItem in childItems)
            {
                string childNavName = childItem.PathToNavigationProperty.FirstSegment.Identifier;
                if (childRouteBacked is not null && childRouteBacked.Contains(childNavName))
                    return false; // delegate-backed nested nav — defer whole branch (never EF-included)

                if (BuildExpandNavBinding(binding.ElementType, childNavName) is not { } childBinding)
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
        List<string>? nestedSelect = nested is not null ? ExtractSelectedProperties(nested) : null;

        engaged = new EngagedExpand(
            binding, item.FilterOption, item.OrderByOption, skip, top, item.CountOption == true, nestedSelect,
            children, Levels: 0);
        return true;
    }

    // #206 phase 2 (multi-level expand): the request-scoped startup source (structural metadata only —
    // NavigationRoutes etc.) whose entity set's CLR model type is <paramref name="clrType"/>, or null
    // when that type is not exposed as its own entity set. Used at query-plan time to decide whether a
    // nested navigation is delegate-backed; only structural facts are read, so the startup singleton
    // source suffices (no request scope needed). Returns the FIRST profile whose model type matches —
    // OhData allows at most one entity set per CLR model type in practice (set names are de-duplicated
    // at startup), so a single match is expected; a null result means the type is a nav-target only
    // (no profile), hence no delegate can exist for its navigations.
    private static IEntitySetEndpointSource? FindStartupSourceForClrType(Type clrType, OhDataRegistration registration)
    {
        return registration.Profiles.FirstOrDefault(p => p.ModelType == clrType);
    }

    // #206 phase 2 (multi-level expand): true when an element type can be projected into a fresh
    // member-init at an INTERMEDIATE expand level (i.e. one that folds deeper navigations). Requires a
    // public parameterless constructor and every scalar structural property (per the EDM) to be a
    // public-settable CLR property that is not complex-typed — projecting an EF-owned complex property
    // under a tracking queryable throws (the same phase-1 boundary TryApplySelectProjection guards). A
    // type that fails this defers its parent branch off pushdown (stays EDM-only), never a 500.
    private static bool IsMemberInitProjectable(Type elementType, IEdmModel model)
    {
        if (elementType.GetConstructor(Type.EmptyTypes) is null) return false;
        if (model.FindDeclaredType(elementType.FullName ?? elementType.Name) is not IEdmEntityType edmType) return false;

        foreach (IEdmStructuralProperty sp in edmType.StructuralProperties())
        {
            if (sp.Type.Definition is IEdmComplexType) return false; // owned-entity projection boundary
            PropertyInfo? clrProp = elementType.GetProperty(
                sp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (clrProp is null || clrProp.SetMethod is not { IsPublic: true }) return false;
        }
        return true;
    }

    // #206 phase 2 (multi-level expand): the public-settable, non-complex scalar structural CLR
    // properties of <paramref name="elementType"/> (per the EDM), bound as <c>n.Prop</c> into an
    // intermediate level's fresh member-init. Callers gate on IsMemberInitProjectable first, so every
    // returned property is guaranteed settable and present.
    private static IEnumerable<PropertyInfo> ScalarStructuralClrProps(Type elementType, IEdmModel model)
    {
        if (model.FindDeclaredType(elementType.FullName ?? elementType.Name) is not IEdmEntityType edmType)
            yield break;
        foreach (IEdmStructuralProperty sp in edmType.StructuralProperties()
            .Where(sp => sp.Type.Definition is not IEdmComplexType))
        {
            PropertyInfo? clrProp = elementType.GetProperty(
                sp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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
    // after counting). A single-valued reference has no collection operators; it is returned unchanged
    // unless it carries deeper navigations, in which case it is projected into a null-guarded member-init.
    // A $levels expand delegates to BuildLevelsNavAccess (bounded self-referential recursion). Runs
    // inside the caller's try/catch: a binder that cannot bind a clause throws, and the caller then
    // abandons pushdown for the request (the nav stays EDM-only) rather than surfacing a 500.
    private static Expression BuildShapedNavAccess(
        Expression owner, EngagedExpand engaged, IEdmModel model, ODataQuerySettings binderSettings)
    {
        ExpandNavBinding nav = engaged.Binding;

        if (engaged.Levels > 0)
            return BuildLevelsNavAccess(owner, engaged, engaged.Levels, model);

        Expression access = Expression.Property(owner, nav.Property);
        Type elem = nav.ElementType;

        if (!nav.IsCollection)
        {
            // Single-valued reference. When it carries deeper pushed navigations, project it into a
            // fresh member-init (null-guarded so a missing reference stays null); otherwise the bare
            // reference (EF outer join loads the full related entity).
            if (engaged.Children is { Count: > 0 })
            {
                Expression init = BuildMemberInit(access, elem, engaged.Children, model, binderSettings);
                return Expression.Condition(
                    Expression.Equal(access, Expression.Constant(null, elem)),
                    Expression.Constant(null, elem), init);
            }
            return access;
        }

        access = ApplyNavFilterOrderPaging(access, engaged, elem, model, binderSettings);

        // Fold a nested $expand into an element-wise projection so the deeper delegate-less navigations
        // load in the same JOIN'd query (EF ThenInclude). Without children this is byte-identical to the
        // single-level path (a plain .ToList() of the full related entities — all columns materialized).
        if (engaged.Children is { Count: > 0 })
        {
            ParameterExpression n = Expression.Parameter(elem, "n");
            LambdaExpression proj = Expression.Lambda(
                BuildMemberInit(n, elem, engaged.Children, model, binderSettings), n);
            access = Expression.Call(_enumerableSelect.MakeGenericMethod(elem, elem), access, proj);
        }

        return Expression.Call(_enumerableToList.MakeGenericMethod(elem), access);
    }

    // #206 phase 2 (optioned expand): apply a collection expand's nested $filter/$orderby/$skip/$top to
    // the navigation-access expression, returning the shaped (un-materialized) IEnumerable. Extracted
    // from BuildShapedNavAccess so the single-level, nested, and $levels paths share one implementation.
    private static Expression ApplyNavFilterOrderPaging(
        Expression access, EngagedExpand engaged, Type elem, IEdmModel model, ODataQuerySettings binderSettings)
    {
        // A fresh QueryBinderContext per bind: it holds the binder's `$it` lambda parameter and other
        // per-clause state, so filter and orderby each get their own rather than sharing one.
        if (engaged.Filter is not null)
        {
            var ctx = new QueryBinderContext(model, binderSettings, elem);
            var predicate = (LambdaExpression)_filterBinder.BindFilter(engaged.Filter, ctx);
            access = Expression.Call(_enumerableWhere.MakeGenericMethod(elem), access, predicate);
        }

        if (engaged.OrderBy is not null)
        {
            var ctx = new QueryBinderContext(model, binderSettings, elem);
            OrderByBinderResult? result = _orderByBinder.BindOrderBy(engaged.OrderBy, ctx);
            bool first = true;
            for (OrderByBinderResult? cur = result; cur is not null; cur = cur.ThenBy)
            {
                var keySelector = (LambdaExpression)cur.OrderByExpression;
                bool descending = cur.Direction == OrderByDirection.Descending;
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

        // Whenever paging is in play — pushed to SQL now (no $count) OR deferred to the JSON window
        // (with $count) — stabilize the order so WHICH rows land in the page is deterministic. Mirrors
        // the root path's EnsureStableOrder (#241): append the nav element's single key as a FINAL
        // tiebreaker (a ThenBy after an explicit nested $orderby so a non-unique sort column still pages
        // stably, or the sole OrderBy when none was given). Applied even under $count so the deferred
        // JSON window (ShapePushedExpandsInJson) pages over a deterministic SQL order. A
        // composite/unresolvable key is left to the provider (best-effort, never throws).
        bool paging = (engaged.Skip is int s && s > 0) || engaged.Top is int;
        if (paging && TryGetKeyClrProperty(model, elem) is { } keyProp)
        {
            ParameterExpression e = Expression.Parameter(elem, "e");
            LambdaExpression keySelector = Expression.Lambda(Expression.Property(e, keyProp), e);
            MethodInfo tiebreak = engaged.OrderBy is null ? _enumerableOrderBy : _enumerableThenBy;
            access = Expression.Call(tiebreak.MakeGenericMethod(elem, keyProp.PropertyType), access, keySelector);
        }

        // Skip/Take push to SQL only when $count is absent; with $count the full (ordered) filtered set
        // is materialized so the JSON pass can count it before paging (see EngagedExpand remarks).
        if (!engaged.Count)
        {
            if (engaged.Skip is int sk && sk > 0)
                access = Expression.Call(_enumerableSkip.MakeGenericMethod(elem), access, Expression.Constant(sk));
            if (engaged.Top is int tp)
                access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(tp));
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
        IEdmModel model, ODataQuerySettings binderSettings)
    {
        var bindings = new List<MemberBinding>();
        AddScalarBindings(bindings, source, elemType, model);
        foreach (EngagedExpand child in children)
            bindings.Add(Expression.Bind(child.Binding.Property, BuildShapedNavAccess(source, child, model, binderSettings)));
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
    // object cycle can form for System.Text.Json. Any nested $filter/$orderby is intentionally NOT
    // applied under $levels (TryBuildEngagedExpand defers a $levels that carries other options), so this
    // stays a pure structural recursion — the common "load this hierarchy N deep" case.
    private static Expression BuildLevelsNavAccess(
        Expression owner, EngagedExpand engaged, int remaining, IEdmModel model)
    {
        ExpandNavBinding nav = engaged.Binding;
        Type elem = nav.ElementType; // == owner's type (a true self-reference; see BuildLevelsNavBinding)
        Expression access = Expression.Property(owner, nav.Property);

        if (nav.IsCollection)
        {
            ParameterExpression n = Expression.Parameter(elem, "n");
            var bindings = new List<MemberBinding>();
            AddScalarBindings(bindings, n, elem, model);
            Expression deeper = remaining > 1
                ? BuildLevelsNavAccess(n, engaged, remaining - 1, model)
                // Leaf: an empty page of the self-navigation (Take(0)) so it serializes as [] rather
                // than null, and the recursion terminates without loading a further level.
                : Expression.Call(
                    _enumerableToList.MakeGenericMethod(elem),
                    Expression.Call(_enumerableTake.MakeGenericMethod(elem),
                        Expression.Property(n, nav.Property), Expression.Constant(0)));
            bindings.Add(Expression.Bind(nav.Property, deeper));
            LambdaExpression proj = Expression.Lambda(Expression.MemberInit(Expression.New(elem), bindings), n);
            Expression projected = Expression.Call(_enumerableSelect.MakeGenericMethod(elem, elem), access, proj);
            return Expression.Call(_enumerableToList.MakeGenericMethod(elem), projected);
        }

        // Single-valued self-reference (e.g. a Manager chain): a null-guarded fresh member-init.
        var refBindings = new List<MemberBinding>();
        AddScalarBindings(refBindings, access, elem, model);
        Expression refDeeper = remaining > 1
            ? BuildLevelsNavAccess(access, engaged, remaining - 1, model)
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
        if (model.FindDeclaredType(elem.FullName ?? elem.Name) is not IEdmEntityType entityType) return null;
        var keys = entityType.Key().ToList();
        if (keys.Count != 1) return null; // composite / keyless → leave order to the provider
        return elem.GetProperty(keys[0].Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
    }

    // #206 phase 2 (optioned + multi-level expand): apply the JSON-side portion of a pushed expand's
    // nested options to the already-serialized (camelCase) parent objects — $count (emit
    // Nav@odata.count), the count-deferred $skip/$top paging, nested $select projection, and
    // (recursively) the same shaping for each deeper pushed level. Filter/OrderBy (and paging when
    // $count is absent) were already applied in SQL by BuildShapedNavAccess, so this touches only the
    // navs that actually need post-serialization shaping. Reuses StripToSelectedProperties so nested
    // $select casing/annotation handling is identical to the root-level strip.
    private static void ShapePushedExpandsInJson(
        JsonArray parents, IReadOnlyList<EngagedExpand> engaged, JsonSerializerOptions serializerOptions) =>
        ShapePushedExpandsInJson(parents.OfType<JsonObject>(), engaged, serializerOptions);

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
                if (e.Levels > 0) (names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(e.Binding.Property.Name);
                if (e.Children is { Count: > 0 }) Walk(e.Children);
            }
        }
        Walk(engaged);
        return names;
    }

    private static void ShapePushedExpandsInJson(
        IEnumerable<JsonObject> parents, IReadOnlyList<EngagedExpand> engaged, JsonSerializerOptions serializerOptions)
    {
        foreach (EngagedExpand e in engaged)
        {
            // A level with a nested $count/$select OR deeper pushed children needs JSON work; a pure
            // leaf whose options were fully handled in SQL is skipped. ($levels shaping is structural
            // only — it carries no Count/NestedSelect/Children, so it is a no-op here.)
            bool hasChildren = e.Children is { Count: > 0 };
            if (!e.Count && e.NestedSelect is null && !hasChildren) continue;

            PropertyInfo prop = e.Binding.Property;
            string key = ResolveNavigationJsonKey(prop.Name, prop, serializerOptions);

            foreach (JsonObject parent in parents)
            {
                JsonNode? node = parent[key];
                if (e.Binding.IsCollection && node is JsonArray arr)
                {
                    if (e.Count)
                    {
                        // Count reflects the full filtered collection (paging was deferred to here).
                        parent[$"{key}@odata.count"] = arr.Count;
                        int skip = e.Skip is int sk && sk > 0 ? Math.Min(sk, arr.Count) : 0;
                        int end = e.Top is int tp ? Math.Min(arr.Count, skip + Math.Max(tp, 0)) : arr.Count;
                        if (skip > 0 || end < arr.Count)
                        {
                            // Rebuild to the [skip, end) window in one O(n) pass (Clear detaches the
                            // captured nodes so they can be re-added) rather than repeated RemoveAt(0).
                            var window = new List<JsonNode?>(end - skip);
                            for (int i = skip; i < end; i++) window.Add(arr[i]);
                            arr.Clear();
                            foreach (JsonNode? node2 in window) arr.Add(node2);
                        }
                    }
                    // Recurse into deeper pushed levels on the (paged) elements BEFORE this level's
                    // $select strip — the strip keeps expanded-nav names (ExtractSelectedProperties), so
                    // the children survive, and shaping deeper counts/selects sees the full child graph.
                    if (hasChildren)
                        ShapePushedExpandsInJson(arr.OfType<JsonObject>(), e.Children!, serializerOptions);
                    if (e.NestedSelect is not null)
                        StripToSelectedProperties(arr.OfType<JsonObject>(), e.NestedSelect);
                }
                else if (!e.Binding.IsCollection && node is JsonObject one)
                {
                    if (hasChildren)
                        ShapePushedExpandsInJson(new[] { one }, e.Children!, serializerOptions);
                    if (e.NestedSelect is not null)
                        StripToSelectedProperties(new[] { one }, e.NestedSelect);
                }
            }
        }
    }

    // #206 phase 2 (Option A1): builds the startup-time $expand pushdown binding for one
    // DELEGATE-LESS navigation (by CLR property name), or returns null when it is not eligible to
    // be folded into the collection projection. Only navigations declared WITHOUT a custom expand
    // delegate reach this method (the caller filters out every navigation that owns a
    // NavigationRouteDefinition), so provenance — "no delegate exists" — is already established;
    // this method only adds the structural safety checks. A navigation qualifies when it maps to a
    // settable CLR property whose (element) type declares no navigation back to TModel — a
    // bidirectional relationship would materialize a parent<->child object cycle that
    // System.Text.Json throws on — and, for a collection, whose member type can accept a
    // List&lt;TElement&gt; (the .ToList() the projection emits). Everything else stays EDM-only.
    private static ExpandNavBinding? BuildExpandNavBinding<TModel>(string navPropertyName) =>
        BuildExpandNavBinding(typeof(TModel), navPropertyName);

    // #206 phase 2 (Option A1 / multi-level): non-generic core — build the pushdown binding for a
    // navigation <paramref name="navPropertyName"/> declared on <paramref name="ownerType"/> (the
    // root model at the top level, or a nested element type when recursing), or null when it is not
    // eligible. Acyclicity is checked against the OWNER at this level, so a nested nav that navigates
    // back to its own parent (a bidirectional relationship) is excluded exactly as at the root.
    private static ExpandNavBinding? BuildExpandNavBinding(Type ownerType, string navPropertyName)
    {
        PropertyInfo? navProp = ownerType.GetProperty(
            navPropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (navProp is null || navProp.SetMethod is not { IsPublic: true }) return null;

        Type? elementType = NavElementClrType(navProp);
        if (elementType is null) return null;
        if (TypeHasNavigationTo(elementType, ownerType)) return null; // cyclic — stays EDM-only

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
        PropertyInfo? navProp = ownerType.GetProperty(
            navPropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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
    // navigation that would close a serialization cycle back to the parent entity. The
    // assignability check is intentionally broadened in BOTH directions on the property type AND
    // the collection element type (adversarial-review hardening): a back-reference need not be the
    // exact TModel — a base class or interface that TModel implements (or that is assignable from
    // TModel) also closes a cycle. Over-matching here only forces a safe fallback to the EDM-only
    // path, never incorrect data, so the conservative direction is correct.
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
    private static bool IsEfCoreBacked(IQueryable query)
    {
        for (Type? t = query.Provider.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Namespace is { } ns &&
                ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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
        var pushdownNameGroups = source.StructuralProperties
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool pushdownNamesUnambiguous = pushdownNameGroups.All(g => g.Count() == 1);
        var pushdownStructuralByName = pushdownNameGroups
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

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
        var pushdownExpandNavs = new Dictionary<string, ExpandNavBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (string navName in source.NavigationPropertyNames
            .Where(navName => !routeBackedNavNames.Contains(navName))) // delegate-backed → delegate path only
        {
            if (BuildExpandNavBinding<TModel>(navName) is { } binding)
                pushdownExpandNavs[navName] = binding;
        }

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
                else if (boundOperationName is not null &&
                         string.Equals(rule.BoundOperationName, boundOperationName, StringComparison.Ordinal))
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

        // #199 Layer B: resource checks on Read/Update/Delete load the entity by key, so a Resource
        // requirement on any of those categories requires a GetById handler. Fail fast at startup.
        if (operationAuthRules is not null && !source.HasGetById
            && (CategoryHasResource(OhDataOperation.Read, null)
                || CategoryHasResource(OhDataOperation.Update, null)
                || CategoryHasResource(OhDataOperation.Delete, null)))
        {
            throw new InvalidOperationException(
                $"Entity set '{name}': resource-based authorization (.RequireResource()) on Read/Update/Delete " +
                "requires a GetById handler to load the entity for the check.");
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
                    //
                    // #244: the framework deliberately does NOT inject a stabilizing order before this
                    // cap Take — unlike the Priority-2 path, where the framework owns skip/take and can
                    // order every page consistently. Here the profile owns its whole pipeline via
                    // ApplyTo, including any $skip, so the framework can't order safely: ordering after
                    // the profile's own Skip would sort a sliced subset, and ordering only the first
                    // (unskipped) page would misalign the $skip continuation. Deterministic
                    // @odata.nextLink paging on this path is therefore the profile's responsibility — it
                    // must establish a stable order (a terminal OrderBy, or applying the client's
                    // $orderby). EF Core already surfaces the omission: warning 10102 fires when a query
                    // is skip/take'd without an ORDER BY. See docs/query-options.md.
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
                    List<EngagedExpand>? engagedExpandNavs = null;
                    if (source.ExpandPushdownEnabled &&
                        pushdownCtorOk && pushdownNamesUnambiguous &&
                        options.SelectExpand?.SelectExpandClause is { } expandPlanClause &&
                        IsEfCoreBacked(filtered))
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
                        }
                    }

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

                        IQueryable<TModel> pushedQuery = TryApplySelectProjection(
                            filtered, structuralNames, source, pushdownCtorOk, pushdownStructuralByName,
                            logger, engagedExpandNavs, registration.EdmModel, cachedBinderSettings);

                        if (ReferenceEquals(pushedQuery, filtered))
                        {
                            // Projection ineligible (e.g. a complex/unsettable structural member) →
                            // the navigations were NOT materialized. Abandon expand pushdown for this
                            // request and take the fallback path, still honoring $select pushdown.
                            logger?.LogDebug(
                                "OhData: $expand pushdown skipped for {EntitySet}: the collection projection was ineligible; delegate-less navigations stay EDM-only for this request.",
                                source.EntitySetName);
                            engagedExpandNavs = null;
                            items = ApplySelectPushdown(filtered).ToArray();
                        }
                        else
                        {
                            try
                            {
                                items = pushedQuery.ToArray();
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // A provider that cannot translate the folded projection must not
                                // become a 500: fall back to the full fetch + $select pushdown,
                                // which is the pre-#206-phase-2 behavior (delegate-less nav → EDM-only).
                                logger?.LogDebug(ex,
                                    "OhData: $expand pushdown query failed to translate for {EntitySet}; falling back (delegate-less navigations stay EDM-only for this request).",
                                    source.EntitySetName);
                                engagedExpandNavs = null;
                                items = ApplySelectPushdown(filtered).ToArray();
                            }
                        }
                    }
                    else
                    {
                        // No expand pushdown — $select-only path, byte-for-byte unchanged.
                        items = ApplySelectPushdown(filtered).ToArray();
                    }

                    // Gap 3: compute nextLink when MaxTop (or preferred page size) is set and page is full
                    string? nextLink = null;
                    int effectivePageSize = appliedPageSize ?? 0;
                    if (effectivePageSize > 0 && items.Length == effectivePageSize && options.Top is null)
                    {
                        int nextSkip = effectiveSkip + items.Length;
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
                        (finalItems, selectedProps) = await ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct, pushedLevelsNavNames);
                    }
                    catch (JsonException ex) when (engagedExpandNavs is { Count: > 0 })
                    {
                        // #206 phase 2 (adversarial-review hardening): the pushed graph tripped a
                        // serialization cycle the static back-reference guard missed (e.g. EF
                        // relationship fixup populated an untyped/base-typed back-navigation the
                        // projection did not itself materialize). Degrade to the (delegate-less →
                        // EDM-only) fallback instead of surfacing a 500: re-fetch WITHOUT the folded
                        // navigations, then run the same pipeline. The row COUNT is unchanged, so the
                        // nextLink computed above stays valid.
                        logger?.LogDebug(ex,
                            "OhData: $expand pushdown produced a serialization cycle for {EntitySet}; falling back (delegate-less navigations stay EDM-only for this request).",
                            source.EntitySetName);
                        // Disengage shaping BEFORE the fallback re-fetch — like the ineligible-projection
                        // and translation-failure fallbacks above — so ShapePushedExpandsInJson does not
                        // run against the degraded (EDM-only) data and emit a bogus Nav@odata.count. The
                        // re-fetch folds no navigations, so it cannot trip the cycle again.
                        engagedExpandNavs = null;
                        pushedLevelsNavNames = null; // nothing pushed after the fallback re-fetch
                        items = ApplySelectPushdown(filtered).ToArray();
                        (finalItems, selectedProps) = await ApplyCollectionPipelineAsync(items, options, source, s, jsonOptions, rootEdmType, registration, ctx.RequestServices, ct, pushedLevelsNavNames);
                    }

                    // #206 phase 2 (optioned expand): apply the JSON-side portion of each pushed
                    // expand's nested options — Nav@odata.count and count-deferred paging, plus nested
                    // $select projection — to the serialized parents. No-op unless a pushed expand
                    // actually carried $count or $select; the fallbacks above set engagedExpandNavs to
                    // null, so a request that abandoned pushdown does no shaping here.
                    if (engagedExpandNavs is { Count: > 0 })
                    {
                        ShapePushedExpandsInJson(finalItems, engagedExpandNavs, jsonOptions ?? _camelCaseSerializerOptions);
                    }

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
            ApplyOperationAuth(collReadAllRb, OhDataOperation.Read, keyBased: false);
        }

        bool hasCountSource = (source is IODataEntitySetEndpointSource odsCheck && odsCheck.HasGetODataQueryable)
            || source.HasGetQueryable || source.HasGetAll;
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
                    string odataId = BuildEntityId(ctx, prefix, name, parsedKey!);

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
                  MaxTop: null));
            ApplyOperationAuth(rb, OhDataOperation.Read);
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
                        // Deep insert (§32): when AllowDeepInsert is true, `result` (the handler's
                        // return value) may carry nested navigation values populated by the
                        // handler — SerializeToNode below serializes them inline automatically,
                        // satisfying §11.4.2.2's "return the created entity with related entities."
                        // #240: omit un-expanded navigations from the POST echo so it matches a read
                        // of the same type — EXCEPT when the profile opted into deep insert, where the
                        // 201 deliberately echoes the created graph inline (§11.4.2.2). The gate is
                        // entity-level because OmitUnexpandedNavigations strips ALL declared navs
                        // unconditionally (it never inspects whether a nav is populated); a per-request
                        // choice would require a value-aware strip. Accepted residual: a deep-insert
                        // profile doing a *non-deep* POST still echoes its (null/empty) navs. PUT/PATCH
                        // omit unconditionally — deep insert is POST-only, so there is no update
                        // equivalent to honour.
                        var createdNode = ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, odataId: odataId, etag: postEtag,
                            omitNavsForType: source.AllowDeepInsert ? null : rootEdmType);
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
                        return Results.Created(odataId, ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, odataId: odataId, etag: putEtag, omitNavsForType: rootEdmType));
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: putEtag, omitNavsForType: rootEdmType);
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
                        // #226: ignored properties get the same silent-skip as unknown members.
                        // This loop resolves members via CLR reflection (not the EDM), so EDM
                        // removal alone would not stop an ignored member from binding here.
                        if (clrProp is not null && !source.IgnoredPropertyNames.Contains(clrProp.Name))
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

                    EchoReturnRepresentationPreference(ctx);

                    // Gap 5: include @odata.id in PATCH response
                    // Gap 2: include @odata.etag in body
                    // S4 fix: canonical, URL-safe key literal built from parsedKey (see GetById above).
                    string odataId = BuildEntityId(ctx, prefix, name, parsedKey!);
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: patchEtag, omitNavsForType: rootEdmType);
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
                                        return Results.Ok(new Dictionary<string, object?>
                                        {
                                            ["@odata.context"] = context,
                                            ["@odata.id"] = BuildEntityId(baseUrl, refNavCapture.ChildEntitySetName, k)
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
                        var createdNode = ODataEntityNode(ctx, prefix, contextSegment, created, jsonOptions, odataId: childOdataId, omitNavsForType: navTargetEdmType);
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
                            return Results.Ok(envelope);
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
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, s);
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
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions, rootEdmType, s);
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
                    BodyType = ActionBodySchemaTypeFactory.GetOrCreate(
                        $"{name}.{actionCapture.Name}.Entity", bodyParams),
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

