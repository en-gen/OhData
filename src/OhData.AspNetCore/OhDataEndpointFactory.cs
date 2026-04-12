using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.UriParser;
using OhData.Abstractions;
using OhData.Abstractions.AspNetCore.OData;

namespace OhData.AspNetCore;

internal readonly record struct ODataErrorDetail(string Code, string Message, string? Target = null);

internal static class OhDataEndpointFactory
{
    private static readonly MethodInfo _mapEntitySetMethod =
        typeof(OhDataEndpointFactory)
            .GetMethod(nameof(MapEntitySet), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly string[] PatchMethod = new[] { "PATCH" };

    private static string BuildMetadataXml(IEdmModel model)
    {
        var sb = new StringBuilder();
        using var xmlWriter = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true });
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

    private static bool PrefersMinimal(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("Prefer", out var prefer) &&
        prefer.ToString().Contains("return=minimal", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ParseETagList(string raw)
    {
        // Split comma-separated ETags; handle wildcard "*"
        return raw.Split(',').Select(s => s.Trim().Trim('"'));
    }

    public static RouteGroupBuilder MapAll(IEndpointRouteBuilder routes, OhDataRegistration registration)
    {
        string prefix = registration.Prefix;
        var group = routes.MapGroup(prefix);

        // Gap 1: Add OData-Version: 4.0 header to all responses (§8.2.6)
        group.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers["OData-Version"] = "4.0";
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
        group.MapGet("/$metadata", () => Results.Content(metadataXml, "application/xml"))
            .ExcludeFromDescription();

        // Resolve logger from the original routes ServiceProvider (group doesn't expose it)
        var loggerFactory = routes.ServiceProvider.GetService<ILoggerFactory>();

        // One set of CRUD routes per registered profile
        foreach (var profile in registration.Profiles)
        {
            _mapEntitySetMethod
                .MakeGenericMethod(profile.KeyType, profile.ModelType)
                .Invoke(null, new object?[] { group, profile, registration, loggerFactory });
        }

        // Gap 7: Unbound functions/actions — registered once at service root level (§11.5.1)
        MapUnboundOperations(group, registration.UnboundOperations);

        return group;
    }

    private static void MapUnboundOperations(
        RouteGroupBuilder group,
        IReadOnlyList<UnboundOperationDefinition> unboundOps)
    {
        foreach (var op in unboundOps)
        {
            var opCapture = op;
            if (!op.IsAction)
            {
                // Unbound function: GET /{prefix}/{FunctionName}?params
                group.MapGet($"/{op.Name}", async (HttpContext ctx, CancellationToken ct) =>
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
                            catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or OverflowException)
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
                }).Produces(200).Produces(204).Produces(400);
            }
            else
            {
                // Unbound action: POST /{prefix}/{ActionName} with JSON body
                group.MapPost($"/{op.Name}", async (HttpContext ctx, CancellationToken ct) =>
                {
                    object?[] args = new object?[opCapture.Parameters.Length];
                    if (opCapture.Parameters.Length > 0)
                    {
                        try
                        {
                            var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                                ctx.Request.Body, cancellationToken: ct);
                            var jsonOptions = ctx.RequestServices
                                .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                                ?.Value?.SerializerOptions;
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
                }).Produces(200).Produces(204).Produces(400);
            }
        }
    }

    private static IResult ODataError(
        int status, string code, string message,
        string? target = null,
        IReadOnlyList<ODataErrorDetail>? details = null)
    {
        var errorObj = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (target is not null) errorObj["target"] = target;
        if (details is { Count: > 0 })
        {
            errorObj["details"] = details.Select(d =>
            {
                var dd = new Dictionary<string, object?> { ["code"] = d.Code, ["message"] = d.Message };
                if (d.Target is not null) dd["target"] = d.Target;
                return (object)dd;
            }).ToArray();
        }

        var body = new Dictionary<string, object> { ["error"] = errorObj };
        return status switch
        {
            400 => Results.BadRequest(body),
            404 => Results.NotFound(body),
            _ => Results.Json(body, statusCode: status)
        };
    }

    /// <remarks>
    /// This check is advisory, not atomic. Between the ETag read and the caller's write,
    /// another request may modify the resource. For true atomic concurrency, use
    /// data-store-level concurrency tokens (e.g., EF Core [Timestamp] / SQL WHERE RowVersion = @expected).
    /// The HTTP ETag mechanism provides a best-effort conflict signal, not a transaction guarantee.
    /// </remarks>
    private static async Task<IResult?> CheckETagAsync(
        IEntitySetEndpointSource source,
        HttpContext ctx,
        object parsedKey,
        CancellationToken ct)
    {
        if (!source.HasETag) return null;
        if (!ctx.Request.Headers.TryGetValue("If-Match", out var ifMatch)) return null;
        string raw = ifMatch.ToString().Trim();
        // Per RFC 7232: strip optional weak validator prefix W/" before comparing.
        if (raw.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(2);
        string ifMatchValue = raw.Trim('"');
        if (ifMatchValue == "*") return null; // wildcard -- skip check

        object? current = await source.InvokeGetByIdAsync(parsedKey, ct);
        if (current is null)
            return ODataError(404, "NotFound", "Resource not found.");
        string currentETag = source.InvokeGetETag(current);
        if (currentETag != ifMatchValue)
            return ODataError(412, "PreconditionFailed", "The ETag does not match the current resource version.");
        return null; // OK to proceed
    }

    // -- JsonNode $select post-processing helpers ---------------------------------

    // Serialize with camelCase so the filtered JsonArray has the same casing as the
    // rest of the OData response (which goes through ASP.NET Core's camelCase pipeline).
    // JsonArray nodes are returned as-is by Results.Ok, bypassing that pipeline.
    private static readonly JsonSerializerOptions _camelCaseSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static object ApplySelectPostProcess(object items, ODataQueryOptions options)
    {
        if (options.SelectExpand?.SelectExpandClause is null) return items;

        var selectedProps = ExtractSelectedProperties(options.SelectExpand.SelectExpandClause);
        if (selectedProps is null) return items;

        var json = JsonSerializer.SerializeToNode(items, _camelCaseSerializerOptions)!.AsArray();
        foreach (var item in json)
        {
            if (item is not JsonObject obj) continue;
            var toRemove = obj.Select(p => p.Key)
                             .Where(k => !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                             .ToList();
            foreach (string? key in toRemove) obj.Remove(key);
        }

        return json;
    }

    private static HashSet<string>? ExtractSelectedProperties(SelectExpandClause clause)
    {
        if (clause.AllSelected) return null;

        // The Microsoft.OData parser normalizes $select identifiers to the EDM property name
        // (e.g. both "$select=name" and "$select=Name" resolve to identifier "Name").
        // We use OrdinalIgnoreCase as a safety net for any serialization casing differences.
        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in clause.SelectedItems)
        {
            if (item is PathSelectItem psi)
                props.Add(psi.SelectedPath.FirstSegment.Identifier);
        }

        return props;
    }

    // Gap 8: $expand data loading (§11.2.4)
    private static async Task<object> ApplyExpandAsync(
        object[] items,
        ODataQueryOptions options,
        IEntitySetEndpointSource source,
        CancellationToken ct)
    {
        if (options.SelectExpand?.SelectExpandClause is null) return items;

        var expandedProps = options.SelectExpand.SelectExpandClause.SelectedItems
            .OfType<ExpandedNavigationSelectItem>()
            .Select(e => e.PathToNavigationProperty.FirstSegment.Identifier)
            .ToList();

        if (expandedProps.Count == 0) return items;

        var jsonOptions = default(JsonSerializerOptions?);
        var json = JsonSerializer.SerializeToNode(items)!.AsArray();

        foreach (string? propName in expandedProps)
        {
            var navRoute = source.NavigationRoutes.FirstOrDefault(n =>
                string.Equals(n.PropertyName, propName, StringComparison.OrdinalIgnoreCase));
            if (navRoute is null) continue;

            for (int i = 0; i < items.Length; i++)
            {
                var keyProp = items[i].GetType().GetProperty(source.KeyPropertyName,
                    BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (keyProp?.GetValue(items[i]) is not { } keyVal) continue;

                object? related = await navRoute.Handler(keyVal, ct);
                if (json[i] is JsonObject obj)
                {
                    obj[propName] = related is null
                        ? null
                        : JsonSerializer.SerializeToNode(related, jsonOptions);
                }
            }
        }

        return json;
    }

    // Gap 5: ODataEntityNode with optional @odata.id
    // Gap 2: optional @odata.etag in response body (§4.5.9)
    private static JsonObject ODataEntityNode(
        HttpContext ctx, string prefix, string contextSegment, object entity,
        string? odataId = null, string? etag = null)
    {
        var jsonOptions = ctx.RequestServices
            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            ?.Value?.SerializerOptions;
        var node = JsonSerializer.SerializeToNode(entity, jsonOptions)!.AsObject();
        string baseUrl = BuildBaseUrl(ctx, prefix);
        node["@odata.context"] = JsonValue.Create($"{baseUrl}/$metadata#{contextSegment}");
        if (odataId is not null)
            node["@odata.id"] = JsonValue.Create(odataId);
        // Gap 2: include @odata.etag in body matching the ETag response header (quoted)
        if (etag is not null)
            node["@odata.etag"] = JsonValue.Create($"\"{etag}\"");
        return node;
    }

    private static IResult ODataEntityResult(
        HttpContext ctx, string prefix, string name, object entity,
        string? odataId = null, string? etag = null) =>
        Results.Ok(ODataEntityNode(ctx, prefix, $"{name}/$entity", entity, odataId: odataId, etag: etag));

    // Called via reflection with TKey/TModel resolved from the profile's runtime types.
    private static void MapEntitySet<TKey, TModel>(
        RouteGroupBuilder parentGroup,
        IEntitySetEndpointSource source,
        OhDataRegistration registration,
        ILoggerFactory? loggerFactory)
        where TModel : class
    {
        string name = source.EntitySetName;
        string prefix = registration.Prefix;

        var logger = loggerFactory?.CreateLogger("OhData");

        // Create a sub-group for this entity set's collection-level routes.
        // Auth is applied to this group so collection endpoints are protected.
        var entityGroup = parentGroup.MapGroup($"/{name}");

        // Key-based routes map on parentGroup directly (with name literal in template) because
        // ASP.NET Core route groups insert a separator before non-slash-prefixed segments, which
        // would produce /name/({key}) instead of /name({key}). Auth is applied per-route below.
        AuthorizationConfig? authConfig = source.Authorization;

        static void ApplyAuth(RouteHandlerBuilder rb, AuthorizationConfig? auth)
        {
            if (auth is null) return;
            if (auth.Policy is not null)
                rb.RequireAuthorization(auth.Policy);
            if (auth.Roles is { Count: > 0 })
                rb.RequireAuthorization(policy => policy.RequireRole(auth.Roles.ToArray()));
            if (auth.Policy is null && auth.Roles is null or { Count: 0 })
                rb.RequireAuthorization();
        }

        if (authConfig is not null)
        {
            if (authConfig.Policy is not null)
                entityGroup.RequireAuthorization(authConfig.Policy);
            if (authConfig.Roles is { Count: > 0 })
                entityGroup.RequireAuthorization(policy => policy.RequireRole(authConfig.Roles.ToArray()));
            if (authConfig.Policy is null && authConfig.Roles is null or { Count: 0 })
                entityGroup.RequireAuthorization();
        }

        // Priority 1: ODataEntitySetProfile with direct ODataQueryOptions handler
        if (source is IODataEntitySetEndpointSource odataSource && odataSource.HasGetODataQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                    var options = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);
                    var queryable = await odataSource.InvokeGetODataQueryableAsync(options, ct);

                    object items = queryable is IQueryable<TModel> typed
                        ? (object)typed.ToArray()
                        : queryable.Cast<object>().ToArray();
                    object finalItems = ApplySelectPostProcess(items, options);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    return Results.Ok(new Dictionary<string, object?>
                    {
                        ["@odata.context"] = $"{baseUrl}/$metadata#{name}",
                        ["value"] = finalItems
                    });
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces(200).Produces(400);
        }
        // Priority 2: base GetQueryable (IQueryable without ODataQueryOptions)
        else if (source.HasGetQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var queryable = (IQueryable<TModel>)(await source.InvokeGetQueryableAsync(ct))
                                    .Cast<TModel>();

                    var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                    var options = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);

                    // Gap 4: $search on GetQueryable path — delegate to the Search handler, then
                    // apply remaining OData query options on top of the in-memory result set.
                    if (ctx.Request.Query.TryGetValue("$search", out var searchTermQ))
                    {
                        if (!source.HasSearch)
                        {
                            return ODataError(400, "UnsupportedQueryOption",
                                "This resource does not support $search. Configure the Search handler to enable it.");
                        }

                        var searchResults = await source.InvokeSearchAsync(searchTermQ.ToString(), ct);
                        var searchItems = searchResults.Cast<TModel>().AsQueryable();
                        // Continue with filter/orderby/top/skip on searchItems
                        queryable = searchItems;
                    }

                    long? odataCount = null;
                    if (options.Count?.Value == true)
                    {
                        var countQ = options.Filter is not null
                            ? (IQueryable<TModel>)options.Filter.ApplyTo(queryable, new ODataQuerySettings { PageSize = source.MaxTop })
                            : queryable;
                        odataCount = countQ.LongCount();
                    }

                    // Apply filter/orderby/skip/top without $select so TModel shape is preserved.
                    // $select is handled via JsonNode post-processing to avoid ISelectExpandWrapper casing issues.
                    var settings = new ODataQuerySettings { PageSize = source.MaxTop };
                    IQueryable<TModel> filtered = queryable;
                    if (options.Filter is not null)
                        filtered = (IQueryable<TModel>)options.Filter.ApplyTo(filtered, settings);
                    if (options.OrderBy is not null)
                        filtered = (IQueryable<TModel>)options.OrderBy.ApplyTo(filtered, settings);

                    // Gap 3: $skiptoken → treat as $skip when no $skip is present
                    int? tokenSkip = null;
                    if (options.Skip is null && ctx.Request.Query.TryGetValue("$skiptoken", out var tokenVal))
                    {
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(Uri.UnescapeDataString(tokenVal.ToString()));
                            tokenSkip = BitConverter.ToInt32(bytes, 0);
                        }
                        catch { /* ignore malformed token */ }
                    }

                    int effectiveSkip = tokenSkip ?? 0;
                    if (options.Skip is not null)
                        filtered = (IQueryable<TModel>)options.Skip.ApplyTo(filtered, settings);
                    else if (effectiveSkip > 0)
                        filtered = filtered.Skip(effectiveSkip);

                    if (options.Top is not null)
                        filtered = (IQueryable<TModel>)options.Top.ApplyTo(filtered, settings);
                    else if (source.MaxTop.HasValue)
                        filtered = filtered.Take(source.MaxTop.Value);

                    var items = filtered.ToArray();

                    // Gap 3: compute nextLink when MaxTop is set and page is full
                    string? nextLink = null;
                    if (source.MaxTop.HasValue && items.Length == source.MaxTop.Value)
                    {
                        int nextSkip = effectiveSkip + items.Length;
                        string token = Convert.ToBase64String(BitConverter.GetBytes(nextSkip));
                        nextLink = BuildNextPageLink(ctx, token);
                    }

                    // Gap 8: apply $expand inline data loading
                    object expandedItems = await ApplyExpandAsync(items, options, source, ct);
                    object finalItems = expandedItems is object[] arr
                        ? ApplySelectPostProcess(arr, options)
                        : ApplySelectPostProcess(expandedItems, options);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
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
            }).WithTags(name).Produces(200).Produces(400);
        }
        else if (source.HasGetAll)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    logger?.LogDebug("GET {Prefix}/{Name}", prefix, name);

                    var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                    var options = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);

                    if (options.Filter is not null || options.OrderBy is not null
                        || options.Top is not null || options.Skip is not null)
                    {
                        return ODataError(400, "UnsupportedQueryOption",
                            "This resource does not support $filter, $orderby, $top, or $skip. " +
                            "Configure GetQueryable to enable server-side query processing.");
                    }

                    // Gap 4: $search on GetAll path
                    if (ctx.Request.Query.TryGetValue("$search", out var searchTerm))
                    {
                        if (!source.HasSearch)
                        {
                            return ODataError(400, "UnsupportedQueryOption",
                                "This resource does not support $search. Configure the Search handler to enable it.");
                        }

                        var searchResults = await source.InvokeSearchAsync(searchTerm.ToString(), ct);
                        object[] searchItems = searchResults.ToArray();
                        object searchFinal = ApplySelectPostProcess((object)searchItems, options);
                        string searchBaseUrl = BuildBaseUrl(ctx, prefix);
                        return Results.Ok(new Dictionary<string, object?>
                        {
                            ["@odata.context"] = $"{searchBaseUrl}/$metadata#{name}",
                            ["value"] = searchFinal
                        });
                    }

                    object? result = await source.InvokeGetAllAsync(ct);
                    var enumerable = result as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    var rawItems = enumerable.ToArray();

                    // Gap 8: apply $expand inline data loading on GetAll path
                    object expandedItems = await ApplyExpandAsync(rawItems, options, source, ct);
                    object finalItems = expandedItems is object[] arr
                        ? ApplySelectPostProcess(arr, options)
                        : ApplySelectPostProcess(expandedItems, options);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    return Results.Ok(new Dictionary<string, object?>
                    {
                        ["@odata.context"] = $"{baseUrl}/$metadata#{name}",
                        ["value"] = finalItems
                    });
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces(200).Produces(400);
        }

        bool hasCountSource = (source is IODataEntitySetEndpointSource odsCheck && odsCheck.HasGetODataQueryable)
            || source.HasGetQueryable || source.HasGetAll;
        if (hasCountSource)
        {
            entityGroup.MapGet("/$count", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var odataCtxCount = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                    var options = new ODataQueryOptions<TModel>(odataCtxCount, ctx.Request);

                    if (source is IODataEntitySetEndpointSource odataCountSrc && odataCountSrc.HasGetODataQueryable)
                    {
                        // Priority 1 profiles apply query options themselves; don't re-apply $filter.
                        var queryable = (IQueryable<TModel>)(await odataCountSrc.InvokeGetODataQueryableAsync(options, ct)).Cast<TModel>();
                        return Results.Content(queryable.LongCount().ToString(), "text/plain");
                    }
                    if (source.HasGetQueryable)
                    {
                        var q = (IQueryable<TModel>)(await source.InvokeGetQueryableAsync(ct)).Cast<TModel>();
                        var filtered = options.Filter is not null
                            ? (IQueryable<TModel>)options.Filter.ApplyTo(q, new ODataQuerySettings { PageSize = source.MaxTop })
                            : q;
                        return Results.Content(filtered.LongCount().ToString(), "text/plain");
                    }
                    if (options.Filter is not null)
                    {
                        return ODataError(400, "UnsupportedQueryOption",
                            "$filter is not supported on this resource. Configure GetQueryable to enable server-side filtering.");
                    }

                    var items = await source.InvokeGetAllAsync(ct) as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    return Results.Content(items.LongCount().ToString(), "text/plain");
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces<long>(200).Produces(400);
        }

        if (source.HasGetById)
        {
            var rb = parentGroup.MapGet($"/{name}({{key}})", async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("GET {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    object? result = await source.InvokeGetByIdAsync(parsedKey, ct);
                    string? etagValue = null;
                    if (result is not null && source.HasETag)
                    {
                        etagValue = source.InvokeGetETag(result);
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
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
                    return ODataEntityResult(ctx, prefix, name, result, odataId: odataId, etag: etagValue);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(404);
            ApplyAuth(rb, authConfig);
        }

        if (source.HasPost)
        {
            // If-None-Match on POST is not supported: the framework cannot extract the key from
            // the body without knowing the key property. Developers should handle this themselves.
            var rb = entityGroup.MapPost("", async (TModel model, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("POST {Prefix}/{Name}", prefix, name);
                object? result = await source.InvokePostAsync(model, ct);
                if (result is null) return ODataError(400, "BadRequest", "Post handler returned null.");
                string? postEtag = null;
                if (source.HasETag)
                {
                    postEtag = source.InvokeGetETag(result);
                    ctx.Response.Headers.ETag = $"\"{postEtag}\"";
                }
                string keyStr = source.InvokeGetKeyString(result);
                string baseUrl = BuildBaseUrl(ctx, prefix);
                string odataId = $"{baseUrl}/{name}({keyStr})";

                // Gap 4: Prefer: return=minimal → 204 with Location header
                if (PrefersMinimal(ctx))
                {
                    ctx.Response.Headers.Location = odataId;
                    ctx.Response.Headers["Preference-Applied"] = "return=minimal";
                    return Results.NoContent();
                }

                // Gap 5: include @odata.id in POST response body
                // Gap 2: include @odata.etag in body
                var createdNode = ODataEntityNode(ctx, prefix, $"{name}/$entity", result, odataId: odataId, etag: postEtag);
                return Results.Created(odataId, createdNode);
            });
            rb.WithTags(name).Produces<TModel>(201).Produces(400);
        }

        if (source.HasPutById)
        {
            var rb = parentGroup.MapPut($"/{name}({{key}})", async (string key, TModel model, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PUT {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    string bodyKeyStr = source.InvokeGetKeyString(model);
                    string parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                    if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                        return ODataError(400, "BadRequest", "Key in URL does not match key in request body.", target: "key");
                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;
                    object? result = await source.InvokePutByIdAsync(parsedKey, model, ct);

                    // Gap 3: Upsert via PUT (§11.4.4) — create entity when result is null and AllowUpsert enabled
                    bool wasCreated = false;
                    if (result is null && source.AllowUpsert && source.HasPost)
                    {
                        result = await source.InvokePostAsync(model, ct);
                        wasCreated = true;
                    }

                    if (result is null) return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    string? putEtag = null;
                    if (source.HasETag)
                    {
                        putEtag = source.InvokeGetETag(result);
                        ctx.Response.Headers.ETag = $"\"{putEtag}\"";
                    }

                    // Gap 4: Prefer: return=minimal → 204
                    if (PrefersMinimal(ctx))
                    {
                        ctx.Response.Headers["Preference-Applied"] = "return=minimal";
                        if (wasCreated)
                            ctx.Response.Headers.Location = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
                        return Results.NoContent();
                    }

                    // Gap 5: include @odata.id in PUT response
                    // Gap 2: include @odata.etag in body
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
                    if (wasCreated)
                        return Results.Created(odataId, ODataEntityNode(ctx, prefix, $"{name}/$entity", result, odataId: odataId, etag: putEtag));
                    return ODataEntityResult(ctx, prefix, name, result, odataId: odataId, etag: putEtag);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404);
            ApplyAuth(rb, authConfig);
        }

        bool usePatchDelta = source is IODataEntitySetEndpointSource odataPatchSrc && odataPatchSrc.HasPatchDelta;
        if (source.HasPatch || usePatchDelta)
        {
            var rb = parentGroup.MapMethods($"/{name}({{key}})", PatchMethod, async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PATCH {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var jsonOptions = ctx.RequestServices
                        .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                        ?.Value?.SerializerOptions;
                    var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                        ctx.Request.Body, jsonOptions, ct);

                    var model = body.Deserialize<TModel>(jsonOptions)!;

                    // Only validate key mismatch if the key property was explicitly present in the body.
                    // PATCH is a partial update — the key may be omitted. URL key is authoritative.
                    if (TryGetJsonProperty(body, source.KeyPropertyName, out _))
                    {
                        string bodyKeyStr = source.InvokeGetKeyString(model);
                        string parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                        if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                            return ODataError(400, "BadRequest", "Key in URL does not match key in request body.", target: "key");
                    }

                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;

                    object? result;
                    if (usePatchDelta)
                    {
                        // Build Delta<TModel> from the JSON body — only properties present in the
                        // request are set, giving the handler true partial-update semantics.
                        var delta = new Microsoft.AspNetCore.OData.Deltas.Delta<TModel>();
                        foreach (var prop in body.EnumerateObject())
                        {
                            var clrProp = typeof(TModel).GetProperty(prop.Name,
                                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                            if (clrProp is not null)
                            {
                                object? value = prop.Value.Deserialize(clrProp.PropertyType, jsonOptions);
                                delta.TrySetPropertyValue(clrProp.Name, value);
                            }
                        }
                        result = await ((IODataEntitySetEndpointSource)source).InvokePatchDeltaAsync(parsedKey, delta, ct);
                    }
                    else
                    {
                        result = await source.InvokePatchAsync(parsedKey, model, ct);
                    }

                    string? patchEtag = null;
                    if (result is not null && source.HasETag)
                    {
                        patchEtag = source.InvokeGetETag(result);
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

                    // Gap 5: include @odata.id in PATCH response
                    // Gap 2: include @odata.etag in body
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
                    return ODataEntityResult(ctx, prefix, name, result, odataId: odataId, etag: patchEtag);
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.Accepts<TModel>("application/json");
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404);
            ApplyAuth(rb, authConfig);
        }

        if (source.HasDelete)
        {
            var rb = parentGroup.MapDelete($"/{name}({{key}})", async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("DELETE {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;
                    bool deleted = await source.InvokeDeleteAsync(parsedKey, ct);
                    if (!deleted && !source.IdempotentDelete)
                        return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    return Results.NoContent();
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces(204).Produces(400).Produces(404);
            ApplyAuth(rb, authConfig);
        }

        // Navigation property routes
        foreach (var nav in source.NavigationRoutes)
        {
            string navPropertyName = nav.PropertyName;
            bool navIsCollection = nav.IsCollection;
            var rb = parentGroup.MapGet($"/{name}({{key}})/{navPropertyName}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        object? result = await nav.Handler(parsedKey, ct);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name}({key})/{navPropertyName} not found.");
                        if (navIsCollection)
                        {
                            string baseUrl = BuildBaseUrl(ctx, prefix);
                            // Gap 5: apply $top/$skip/$count on navigation collection results
                            var rawColl = result as System.Collections.IEnumerable;
                            IEnumerable<object> items = rawColl is not null
                                ? rawColl.Cast<object>()
                                : new[] { result };

                            if (ctx.Request.Query.TryGetValue("$skip", out var skipStr)
                                && int.TryParse(skipStr, out int skipVal) && skipVal > 0)
                            {
                                items = items.Skip(skipVal);
                            }

                            long? navCount = null;
                            if (ctx.Request.Query.TryGetValue("$count", out var countVal)
                                && countVal == "true")
                            {
                                // Count before $top is applied (per OData spec)
                                navCount = items.LongCount();
                            }

                            if (ctx.Request.Query.TryGetValue("$top", out var topStr)
                                && int.TryParse(topStr, out int topVal) && topVal >= 0)
                            {
                                items = items.Take(topVal);
                            }

                            object[] itemArray = items.ToArray();
                            var envelope = new Dictionary<string, object?>();
                            envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}({key})/{navPropertyName}";
                            if (navCount.HasValue) envelope["@odata.count"] = navCount;
                            envelope["value"] = itemArray;
                            return Results.Ok(envelope);
                        }
                        return Results.Ok(result);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                    }
                })
                .WithTags(name)
                .Produces(200)
                .Produces(404);
            ApplyAuth(rb, authConfig);

            // Gap 6: $ref endpoints for navigation (§11.4.6)
            string navRefPropertyName = nav.PropertyName;
            bool navRefIsCollection = nav.IsCollection;

            // GET /{name}({key})/{nav}/$ref — returns reference envelope
            var refGetRb = parentGroup.MapGet($"/{name}({{key}})/{navRefPropertyName}/$ref",
                (string key, HttpContext ctx) =>
                {
                    try
                    {
                        object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        string baseUrl = BuildBaseUrl(ctx, prefix);
                        string context = $"{baseUrl}/$metadata#{name}({key})/{navRefPropertyName}/$ref";

                        if (navRefIsCollection)
                        {
                            // Return minimal compliant $ref envelope — full @odata.id population
                            // requires knowing the related entity's key property, which is not
                            // available without generic type context. Return empty value array.
                            return Results.Ok(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = context,
                                ["value"] = System.Array.Empty<object>()
                            });
                        }
                        else
                        {
                            return Results.Ok(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = context
                            });
                        }
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                    }
                })
                .WithTags(name)
                .Produces(200);
            ApplyAuth(refGetRb, authConfig);

            // POST /{name}({key})/{nav}/$ref (add relationship)
            if (nav.AddRef is not null)
            {
                var addRefDelegate = nav.AddRef;
                var refPostRb = parentGroup.MapPost($"/{name}({{key}})/{navRefPropertyName}/$ref",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        try
                        {
                            object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
                            if (!TryGetJsonProperty(body, "@odata.id", out var odataIdEl))
                                return ODataError(400, "BadRequest", "Request body must contain '@odata.id'.");
                            string relatedId = odataIdEl.GetString() ?? "";
                            await addRefDelegate(parsedKey, (object)relatedId, ct);
                            return Results.NoContent();
                        }
                        catch (FormatException ex)
                        {
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                        }
                    })
                    .WithTags(name)
                    .Produces(204)
                    .Produces(400);
                ApplyAuth(refPostRb, authConfig);
            }

            // DELETE /{name}({key})/{nav}/$ref (remove relationship)
            if (nav.RemoveRef is not null)
            {
                var removeRefDelegate = nav.RemoveRef;
                var refDeleteRb = parentGroup.MapDelete($"/{name}({{key}})/{navRefPropertyName}/$ref",
                    async (string key, HttpContext ctx, CancellationToken ct) =>
                    {
                        try
                        {
                            object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                            // For DELETE $ref on collection nav, the related id may come from query param $id
                            string relatedId = ctx.Request.Query.TryGetValue("$id", out var idVal)
                                ? idVal.ToString()
                                : "";
                            await removeRefDelegate(parsedKey, (object)relatedId, ct);
                            return Results.NoContent();
                        }
                        catch (FormatException ex)
                        {
                            logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                            return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                        }
                    })
                    .WithTags(name)
                    .Produces(204)
                    .Produces(400);
                ApplyAuth(refDeleteRb, authConfig);
            }
        }

        // Bound functions — GET /{EntitySet}/{FunctionName}?param=value
        foreach (var fn in source.BoundFunctions.Where(f => !f.IsEntityLevel))
        {
            var fnCapture = fn;
            var rb = entityGroup.MapGet($"/{fn.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
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
                        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or OverflowException)
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
                object? result = await fnCapture.Invoke(args, ct);
                if (result is null) return Results.NoContent();
                // Gap 1: @odata.context on function results when return type matches TModel
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType);
            }).WithTags(name).Produces(200).Produces(204).Produces(400);
        }

        // Bound actions — POST /{EntitySet}/{ActionName} with JSON body params
        // Note: TryGetJsonProperty (below) provides case-insensitive JSON property lookup,
        // matching the case-insensitive query string lookup used for bound functions.
        foreach (var action in source.BoundActions.Where(a => !a.IsEntityLevel))
        {
            var actionCapture = action;
            var rb = entityGroup.MapPost($"/{action.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
                object?[] args = new object?[actionCapture.Parameters.Length];
                if (actionCapture.Parameters.Length > 0)
                {
                    try
                    {
                        var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                            ctx.Request.Body, cancellationToken: ct);
                        var jsonOptions = ctx.RequestServices
                            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                            ?.Value?.SerializerOptions;
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
                object? result = await actionCapture.Invoke(args, ct);
                if (result is null) return Results.NoContent();
                // Gap 1: @odata.context on action results when return type matches TModel
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType);
            }).WithTags(name).Produces(200).Produces(204).Produces(400);
        }

        // Gap 7: Entity-level bound functions — GET /{name}({key})/{fn.Name}
        foreach (var fn in source.BoundFunctions.Where(f => f.IsEntityLevel))
        {
            var fnCapture = fn;
            var rb = parentGroup.MapGet($"/{name}({{key}})/{fn.Name}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
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
                                catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException or OverflowException)
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
                        object? result = await fnCapture.Invoke(args, ct);
                        if (result is null) return Results.NoContent();
                        // Gap 1: @odata.context on entity-level function results
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                })
                .WithTags(name).Produces(200).Produces(204).Produces(400);
            ApplyAuth(rb, authConfig);
        }

        // Gap 7: Entity-level bound actions — POST /{name}({key})/{action.Name}
        foreach (var action in source.BoundActions.Where(a => a.IsEntityLevel))
        {
            var actionCapture = action;
            var rb = parentGroup.MapMethods($"/{name}({{key}})/{action.Name}", new[] { "POST" },
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        object parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        object?[] args = new object?[actionCapture.Parameters.Length];
                        args[0] = parsedKey;
                        if (actionCapture.Parameters.Length > 1)
                        {
                            try
                            {
                                var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                                    ctx.Request.Body, cancellationToken: ct);
                                var jsonOptions = ctx.RequestServices
                                    .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                                    ?.Value?.SerializerOptions;
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
                        object? result = await actionCapture.Invoke(args, ct);
                        if (result is null) return Results.NoContent();
                        // Gap 1: @odata.context on entity-level action results
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                })
                .WithTags(name).Produces(200).Produces(204).Produces(400);
            ApplyAuth(rb, authConfig);
        }

    }

    // Gap 1: Wrap bound operation result with @odata.context when return type matches TModel (§11.5.3).
    // For collection results (IEnumerable<TModel>): context = {root}/$metadata#{EntitySet}
    // For single results (TModel): context = {root}/$metadata#{EntitySet}/$entity
    // For primitives/other types: return Results.Ok directly (no wrapping needed).
    private static IResult WrapBoundOpResult(
        HttpContext ctx, string prefix, string entitySetName, object result, Type modelType)
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
            return Results.Ok(new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata#{entitySetName}",
                ["value"] = coll
            });
        }

        // Check for single TModel
        if (resultType == modelType || modelType.IsAssignableFrom(resultType))
        {
            return ODataEntityResult(ctx, prefix, entitySetName, result);
        }

        // Primitive/other — no context wrapping
        return Results.Ok(result);
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
