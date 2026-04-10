using System.Reflection;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm.Csdl;
using OhData.Abstractions;

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

        // Service document — lists available entity sets
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

        // $metadata — CSDL XML describing the EDM model
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

        if (source.HasGetQueryable)
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

                var result = options.ApplyTo(queryable, new ODataQuerySettings());

                // If $select is active, result is IQueryable of wrapper types — serialize as-is
                var items = result is IQueryable<TModel> typed
                    ? (object)typed.ToArray()
                    : result.Cast<object>().ToArray();

                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
                var envelope = new Dictionary<string, object?>();
                envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                if (odataCount.HasValue) envelope["@odata.count"] = odataCount;
                envelope["value"] = items;
                return Results.Ok(envelope);
            }).WithTags(name);
        }
        else if (source.HasGetAll)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("GET {Prefix}/{Name}", prefix, name);
                var result = await source.InvokeGetAllAsync(ct);
                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}";
                var envelope = new Dictionary<string, object?>();
                envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                envelope["value"] = result;
                return Results.Ok(envelope);
            }).WithTags(name);
        }

        if (source.HasGetQueryable || source.HasGetAll)
        {
            entityGroup.MapGet("/$count", async (HttpContext ctx, CancellationToken ct) =>
            {
                if (source.HasGetQueryable)
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
            }).WithTags(name);
        }

        if (source.HasGetById)
        {
            var rb = parentGroup.MapGet($"/{name}({{key}})", async (string key, CancellationToken ct) =>
            {
                logger?.LogDebug("GET {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var result = await source.InvokeGetByIdAsync(parsedKey, ct);
                    return result is not null ? Results.Ok(result) : ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                }
            });
            ApplyAuth(rb, authConfig);
        }

        if (source.HasPost)
        {
            entityGroup.MapPost("", async (TModel model, CancellationToken ct) =>
            {
                logger?.LogDebug("POST {Prefix}/{Name}", prefix, name);
                var result = await source.InvokePostAsync(model, ct);
                return Results.Created($"{prefix}/{name}", result);
            });
        }

        if (source.HasPutById)
        {
            var rb = parentGroup.MapPut($"/{name}({{key}})", async (string key, TModel model, CancellationToken ct) =>
            {
                logger?.LogDebug("PUT {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var result = await source.InvokePutByIdAsync(parsedKey, model, ct);
                    return Results.Ok(result);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                }
            });
            ApplyAuth(rb, authConfig);
        }

        if (source.HasPatch)
        {
            var rb = parentGroup.MapMethods($"/{name}({{key}})", new[] { "PATCH" }, async (string key, TModel model, CancellationToken ct) =>
            {
                logger?.LogDebug("PATCH {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var result = await source.InvokePatchAsync(parsedKey, model, ct);
                    return result is not null ? Results.Ok(result) : ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                }
            });
            ApplyAuth(rb, authConfig);
        }

        if (source.HasDelete)
        {
            var rb = parentGroup.MapDelete($"/{name}({{key}})", async (string key, CancellationToken ct) =>
            {
                logger?.LogDebug("DELETE {Prefix}/{Name}({Key})", prefix, name, key);
                try
                {
                    var parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var deleted = await source.InvokeDeleteAsync(parsedKey, ct);
                    return deleted ? Results.NoContent() : ODataError(404, "NotFound", $"{name} with key '{key}' was not found.");
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", key, name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                }
            });
            ApplyAuth(rb, authConfig);
        }
    }
}
