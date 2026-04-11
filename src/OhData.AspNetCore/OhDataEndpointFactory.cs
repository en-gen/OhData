using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
            throw new InvalidOperationException(
                "Failed to generate OData CSDL metadata: " +
                string.Join("; ", errors.Select(e => e.ToString())));
        xmlWriter.Flush();
        return sb.ToString();
    }

    private static string BuildBaseUrl(HttpContext ctx, string prefix) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";

    public static RouteGroupBuilder MapAll(IEndpointRouteBuilder routes, OhDataRegistration registration)
    {
        var prefix = registration.Prefix;
        var group = routes.MapGroup(prefix);

        // Pre-compute static responses that are determined at startup.
        var metadataXml = BuildMetadataXml(registration.EdmModel);
        var serviceDocEntitySets = registration.Profiles
            .Select(p => new { name = p.EntitySetName, kind = "EntitySet", url = p.EntitySetName })
            .ToArray();

        // Service document -- lists available entity sets
        group.MapGet("", (HttpContext ctx) =>
        {
            var baseUrl = BuildBaseUrl(ctx, prefix);
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

        return group;
    }

    private static IResult ODataError(int status, string code, string message)
    {
        var body = new Dictionary<string, object>
        {
            ["error"] = new Dictionary<string, string> { ["code"] = code, ["message"] = message }
        };
        return status switch
        {
            400 => Results.BadRequest(body),
            404 => Results.NotFound(body),
            _   => Results.Json(body, statusCode: status)
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
        var raw = ifMatch.ToString().Trim();
        // Per RFC 7232: strip optional weak validator prefix W/" before comparing.
        if (raw.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(2);
        var ifMatchValue = raw.Trim('"');
        if (ifMatchValue == "*") return null; // wildcard -- skip check

        var current = await source.InvokeGetByIdAsync(parsedKey, ct);
        if (current is null)
            return ODataError(404, "NotFound", "Resource not found.");
        var currentETag = source.InvokeGetETag(current);
        if (currentETag != ifMatchValue)
            return ODataError(412, "PreconditionFailed", "The ETag does not match the current resource version.");
        return null; // OK to proceed
    }

    // -- JsonNode $select post-processing helpers ---------------------------------

    private static object ApplySelectPostProcess(object items, ODataQueryOptions options)
    {
        if (options.SelectExpand?.SelectExpandClause is null) return items;

        var selectedProps = ExtractSelectedProperties(options.SelectExpand.SelectExpandClause);
        if (selectedProps is null) return items;

        var json = JsonSerializer.SerializeToNode(items)!.AsArray();
        foreach (var item in json)
        {
            if (item is not JsonObject obj) continue;
            var toRemove = obj.Select(p => p.Key)
                             .Where(k => !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                             .ToList();
            foreach (var key in toRemove) obj.Remove(key);
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

    // Called via reflection with TKey/TModel resolved from the profile's runtime types.
    private static void MapEntitySet<TKey, TModel>(
        RouteGroupBuilder parentGroup,
        IEntitySetEndpointSource source,
        OhDataRegistration registration,
        ILoggerFactory? loggerFactory)
        where TModel : class
    {
        var name = source.EntitySetName;
        var prefix = registration.Prefix;

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
                    var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);
                    var queryable = await odataSource.InvokeGetODataQueryableAsync(options, ct);

                    var items = queryable is IQueryable<TModel> typed
                        ? (object)typed.ToArray()
                        : queryable.Cast<object>().ToArray();
                    var finalItems = ApplySelectPostProcess(items, options);

                    var baseUrl = BuildBaseUrl(ctx, prefix);
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
                    var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);

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
                    if (options.Skip is not null)
                        filtered = (IQueryable<TModel>)options.Skip.ApplyTo(filtered, settings);
                    if (options.Top is not null)
                        filtered = (IQueryable<TModel>)options.Top.ApplyTo(filtered, settings);
                    else if (source.MaxTop.HasValue)
                        filtered = filtered.Take(source.MaxTop.Value);

                    var items = (object)filtered.ToArray();
                    var finalItems = ApplySelectPostProcess(items, options);

                    var baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                    if (odataCount.HasValue) envelope["@odata.count"] = odataCount;
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
                    var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);

                    if (options.Filter is not null || options.OrderBy is not null
                        || options.Top is not null || options.Skip is not null)
                        return ODataError(400, "UnsupportedQueryOption",
                            "This resource does not support $filter, $orderby, $top, or $skip. " +
                            "Configure GetQueryable to enable server-side query processing.");

                    var result = await source.InvokeGetAllAsync(ct);
                    var enumerable = result as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    var items = (object)enumerable.ToArray();
                    var finalItems = ApplySelectPostProcess(items, options);

                    var baseUrl = BuildBaseUrl(ctx, prefix);
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

        var hasCountSource = (source is IODataEntitySetEndpointSource odsCheck && odsCheck.HasGetODataQueryable)
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
                        return ODataError(400, "UnsupportedQueryOption",
                            "$filter is not supported on this resource. Configure GetQueryable to enable server-side filtering.");
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
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var result = await source.InvokeGetByIdAsync(parsedKey, ct);
                    if (result is not null && source.HasETag)
                        ctx.Response.Headers.ETag = $"\"{source.InvokeGetETag(result)}\"";
                    return result is not null ? Results.Ok(result) : ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
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
                var result = await source.InvokePostAsync(model, ct);
                if (result is null) return ODataError(400, "BadRequest", "Post handler returned null.");
                if (source.HasETag)
                    ctx.Response.Headers.ETag = $"\"{source.InvokeGetETag(result)}\"";
                var keyStr = source.InvokeGetKeyString(result);
                var baseUrl = BuildBaseUrl(ctx, prefix);
                return Results.Created($"{baseUrl}/{name}({keyStr})", result);
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
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var bodyKeyStr = source.InvokeGetKeyString(model);
                    var parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                    if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                        return ODataError(400, "BadRequest", "Key in URL does not match key in request body.");
                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;
                    var result = await source.InvokePutByIdAsync(parsedKey, model, ct);
                    if (result is null) return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    if (source.HasETag)
                        ctx.Response.Headers.ETag = $"\"{source.InvokeGetETag(result)}\"";
                    return Results.Ok(result);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404);
            ApplyAuth(rb, authConfig);
        }

        var usePatchDelta = source is IODataEntitySetEndpointSource odataPatchSrc && odataPatchSrc.HasPatchDelta;
        if (source.HasPatch || usePatchDelta)
        {
            var rb = parentGroup.MapMethods($"/{name}({{key}})", PatchMethod, async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PATCH {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
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
                        var bodyKeyStr = source.InvokeGetKeyString(model);
                        var parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                        if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                            return ODataError(400, "BadRequest", "Key in URL does not match key in request body.");
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
                                var value = prop.Value.Deserialize(clrProp.PropertyType, jsonOptions);
                                delta.TrySetPropertyValue(clrProp.Name, value);
                            }
                        }
                        result = await ((IODataEntitySetEndpointSource)source).InvokePatchDeltaAsync(parsedKey, delta, ct);
                    }
                    else
                    {
                        result = await source.InvokePatchAsync(parsedKey, model, ct);
                    }

                    if (result is not null && source.HasETag)
                        ctx.Response.Headers.ETag = $"\"{source.InvokeGetETag(result)}\"";
                    return result is not null ? Results.Ok(result) : ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                }
                catch (JsonException ex)
                {
                    return ODataError(400, "InvalidBody", ex.Message);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
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
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;
                    var deleted = await source.InvokeDeleteAsync(parsedKey, ct);
                    if (!deleted && !source.IdempotentDelete)
                        return ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                    return Results.NoContent();
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                }
            });
            rb.WithTags(name).Produces(204).Produces(400).Produces(404);
            ApplyAuth(rb, authConfig);
        }

        // Navigation property routes
        foreach (var nav in source.NavigationRoutes)
        {
            var navPropertyName = nav.PropertyName;
            var rb = parentGroup.MapGet($"/{name}({{key}})/{navPropertyName}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        var result = await nav.Handler(parsedKey, ct);
                        if (result is null)
                            return ODataError(404, "NotFound", $"{name}({key})/{navPropertyName} not found.");
                        if (nav.IsCollection)
                        {
                            var baseUrl = BuildBaseUrl(ctx, prefix);
                            return Results.Ok(new Dictionary<string, object?>
                            {
                                ["@odata.context"] = $"{baseUrl}/$metadata#{name}({key})/{navPropertyName}",
                                ["value"] = result
                            });
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
        }

        // Bound functions — GET /{EntitySet}/{FunctionName}?param=value
        foreach (var fn in source.BoundFunctions)
        {
            var rb = entityGroup.MapGet($"/{fn.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
                var args = new object?[fn.Parameters.Length];
                for (int i = 0; i < fn.Parameters.Length; i++)
                {
                    var param = fn.Parameters[i];
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
                                $"Cannot convert parameter '{param.Name}' value to {param.ParameterType.Name}.");
                        }
                    }
                    else if (param.HasDefaultValue)
                    {
                        args[i] = param.DefaultValue;
                    }
                    else
                    {
                        return ODataError(400, "MissingParameter",
                            $"Required parameter '{param.Name}' is missing.");
                    }
                }
                var result = await fn.Invoke(args, ct);
                return result is not null ? Results.Ok(result) : Results.NoContent();
            }).WithTags(name).Produces(200).Produces(204).Produces(400);
        }

        // Bound actions — POST /{EntitySet}/{ActionName} with JSON body params
        // Note: TryGetJsonProperty (below) provides case-insensitive JSON property lookup,
        // matching the case-insensitive query string lookup used for bound functions.
        foreach (var action in source.BoundActions)
        {
            var rb = entityGroup.MapPost($"/{action.Name}", async (HttpContext ctx, CancellationToken ct) =>
            {
                var args = new object?[action.Parameters.Length];
                if (action.Parameters.Length > 0)
                {
                    try
                    {
                        var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                            ctx.Request.Body, cancellationToken: ct);
                        var jsonOptions = ctx.RequestServices
                            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                            ?.Value?.SerializerOptions;
                        for (int i = 0; i < action.Parameters.Length; i++)
                        {
                            var param = action.Parameters[i];
                            if (TryGetJsonProperty(body, param.Name!, out var val))
                                args[i] = val.Deserialize(param.ParameterType, jsonOptions);
                            else if (param.HasDefaultValue)
                                args[i] = param.DefaultValue;
                            else
                                return ODataError(400, "MissingParameter",
                                    $"Required parameter '{param.Name}' is missing.");
                        }
                    }
                    catch (JsonException ex)
                    {
                        return ODataError(400, "InvalidBody", ex.Message);
                    }
                }
                var result = await action.Invoke(args, ct);
                return result is not null ? Results.Ok(result) : Results.NoContent();
            }).WithTags(name).Produces(200).Produces(204).Produces(400);
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