using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    public static RouteGroupBuilder MapAll(IEndpointRouteBuilder routes, OhDataRegistration registration)
    {
        var prefix = registration.Prefix;
        var group = routes.MapGroup(prefix);

        // Service document -- lists available entity sets
        group.MapGet("", (HttpContext ctx) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
            return Results.Ok(new Dictionary<string, object>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata",
                ["value"] = registration.Profiles
                    .Select(p => new { name = p.EntitySetName, kind = "EntitySet", url = p.EntitySetName })
                    .ToArray()
            });
        });

        // $metadata -- CSDL XML describing the EDM model
        group.MapGet("/$metadata", () =>
        {
            var sb = new StringBuilder();
            using var xmlWriter = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true });
            CsdlWriter.TryWriteCsdl(registration.EdmModel, xmlWriter, CsdlTarget.OData, out _);
            xmlWriter.Flush();
            return Results.Content(sb.ToString(), "application/xml");
        });

        // Resolve logger from the original routes ServiceProvider (group doesn't expose it)
        var loggerFactory = routes.ServiceProvider.GetService<ILoggerFactory>();

        // One set of CRUD routes per registered profile
        foreach (var profile in registration.Profiles)
        {
            _mapEntitySetMethod
                .MakeGenericMethod(profile.KeyType, profile.ModelType)
                .Invoke(null, new object[] { group, profile, registration, loggerFactory! });
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

    private static async Task<IResult?> CheckETagAsync(
        IEntitySetEndpointSource source,
        HttpContext ctx,
        object parsedKey,
        CancellationToken ct)
    {
        if (!source.HasETag) return null;
        if (!ctx.Request.Headers.TryGetValue("If-Match", out var ifMatch)) return null;
        var ifMatchValue = ifMatch.ToString().Trim('"');
        if (ifMatchValue == "*") return null; // wildcard -- skip check

        var current = await source.InvokeGetByIdAsync(parsedKey, ct);
        if (current is null)
            return ODataError(404, "NotFound", "Resource not found.");
        var currentETag = source.InvokeGetETag(current);
        if (currentETag != ifMatchValue)
            return Results.Problem(statusCode: 412, title: "Precondition Failed",
                detail: "The ETag does not match the current resource version.");
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
            else if (auth.Roles is { Length: > 0 })
                rb.RequireAuthorization(new AuthorizeAttribute { Roles = string.Join(",", auth.Roles) });
            else
                rb.RequireAuthorization();
        }

        if (authConfig is not null)
        {
            if (authConfig.Policy is not null)
                entityGroup.RequireAuthorization(authConfig.Policy);
            else if (authConfig.Roles is { Length: > 0 })
                entityGroup.RequireAuthorization(new AuthorizeAttribute { Roles = string.Join(",", authConfig.Roles) });
            else
                entityGroup.RequireAuthorization();
        }

        // Priority 1: ODataEntitySetProfile with direct ODataQueryOptions handler
        if (source is IODataEntitySetEndpointSource odataSource && odataSource.HasGetODataQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);
                var queryable = await odataSource.InvokeGetODataQueryableAsync(options, ct);

                // Materialize results
                var items = queryable is IQueryable<TModel> typed
                    ? (object)typed.ToArray()
                    : queryable.Cast<object>().ToArray();

                // Post-process: strip unselected properties using JsonNode when $select is active
                var finalItems = ApplySelectPostProcess(items, options);

                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
                var envelope = new Dictionary<string, object?>();
                envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                envelope["value"] = finalItems;
                return Results.Ok(envelope);
            }).WithTags(name).Produces(200).Produces(400);
        }
        // Priority 2: base GetQueryable (IQueryable without ODataQueryOptions)
        else if (source.HasGetQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                var queryable = (IQueryable<TModel>)(await source.InvokeGetQueryableAsync(ct))
                                .Cast<TModel>();

                var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);

                long? odataCount = null;
                if (options.Count?.Value == true)
                {
                    // Count before skip/top: apply filter only
                    var countQ = options.Filter is not null
                        ? (IQueryable<TModel>)options.Filter.ApplyTo(queryable, new ODataQuerySettings())
                        : queryable;
                    odataCount = countQ.LongCount();
                }

                // Apply filter/orderby/skip/top without $select to keep TModel intact.
                // $select is handled via JsonNode post-processing so ISelectExpandWrapper is avoided.
                var settings = new ODataQuerySettings();
                IQueryable<TModel> filtered = queryable;
                if (options.Filter is not null)
                    filtered = (IQueryable<TModel>)options.Filter.ApplyTo(filtered, settings);
                if (options.OrderBy is not null)
                    filtered = (IQueryable<TModel>)options.OrderBy.ApplyTo(filtered, settings);
                if (options.Skip is not null)
                    filtered = (IQueryable<TModel>)options.Skip.ApplyTo(filtered, settings);
                if (options.Top is not null)
                    filtered = (IQueryable<TModel>)options.Top.ApplyTo(filtered, settings);

                // Materialize as TModel[] so JsonSerializer can produce clean JSON
                var items = (object)filtered.ToArray();

                // Post-process: strip unselected properties using JsonNode when $select is active
                var finalItems = ApplySelectPostProcess(items, options);

                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
                var envelope = new Dictionary<string, object?>();
                envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                if (odataCount.HasValue) envelope["@odata.count"] = odataCount;
                envelope["value"] = finalItems;
                return Results.Ok(envelope);
            }).WithTags(name).Produces(200).Produces(400);
        }
        else if (source.HasGetAll)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("GET {Prefix}/{Name}", prefix, name);
                var result = await source.InvokeGetAllAsync(ct);

                var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);

                // Materialize the IEnumerable items
                var items = (object)((IEnumerable<TModel>)result!).ToArray();

                // Post-process: strip unselected properties using JsonNode when $select is active
                var finalItems = ApplySelectPostProcess(items, options);

                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
                var envelope = new Dictionary<string, object?>();
                envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                envelope["value"] = finalItems;
                return Results.Ok(envelope);
            }).WithTags(name).Produces(200).Produces(400);
        }

        if (source.HasGetQueryable || source.HasGetAll)
        {
            entityGroup.MapGet("/$count", async (HttpContext ctx, CancellationToken ct) =>
            {
                // Priority 1: ODataEntitySetProfile with direct ODataQueryOptions handler
        if (source is IODataEntitySetEndpointSource odataSource && odataSource.HasGetODataQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);
                var queryable = await odataSource.InvokeGetODataQueryableAsync(options, ct);

                // Materialize results
                var items = queryable is IQueryable<TModel> typed
                    ? (object)typed.ToArray()
                    : queryable.Cast<object>().ToArray();

                // Post-process: strip unselected properties using JsonNode when $select is active
                var finalItems = ApplySelectPostProcess(items, options);

                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
                var envelope = new Dictionary<string, object?>();
                envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                envelope["value"] = finalItems;
                return Results.Ok(envelope);
            }).WithTags(name).Produces(200).Produces(400);
        }
        // Priority 2: base GetQueryable (IQueryable without ODataQueryOptions)
        else if (source.HasGetQueryable)
                {
                    var q = (IQueryable<TModel>)(await source.InvokeGetQueryableAsync(ct)).Cast<TModel>();
                    var odataCtx = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
                    var options  = new ODataQueryOptions<TModel>(odataCtx, ctx.Request);
                    var filtered = options.Filter is not null
                        ? (IQueryable<TModel>)options.Filter.ApplyTo(q, new ODataQuerySettings())
                        : q;
                    return Results.Ok(filtered.LongCount());
                }

                var items = (IEnumerable<TModel>)(await source.InvokeGetAllAsync(ct))!;
                return Results.Ok(items.LongCount());
            }).WithTags(name).Produces<long>(200);
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
                if (result is not null && source.HasETag)
                    ctx.Response.Headers.ETag = $"\"{source.InvokeGetETag(result)}\"";
                return Results.Created($"{prefix}/{name}", result);
            });
            rb.Produces<TModel>(201).Produces(400);
        }

        if (source.HasPutById)
        {
            var rb = parentGroup.MapPut($"/{name}({{key}})", async (string key, TModel model, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PUT {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;
                    var result = await source.InvokePutByIdAsync(parsedKey, model, ct);
                    if (result is not null && source.HasETag)
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

        if (source.HasPatch)
        {
            var rb = parentGroup.MapMethods($"/{name}({{key}})", new[] { "PATCH" }, async (string key, TModel model, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PATCH {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var etagCheck = await CheckETagAsync(source, ctx, parsedKey, ct);
                    if (etagCheck is not null) return etagCheck;
                    var result = await source.InvokePatchAsync(parsedKey, model, ct);
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
                    return deleted ? Results.NoContent() : ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
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
                async (string key, CancellationToken ct) =>
                {
                    try
                    {
                        var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        var result = await nav.Handler(parsedKey, ct);
                        return result is not null
                            ? Results.Ok(result)
                            : ODataError(404, "NotFound", $"{name}({key})/{navPropertyName} not found.");
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
    }
}