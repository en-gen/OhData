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

    private static string SanitizeLogValue(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);

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

    public static RouteGroupBuilder MapAll(IEndpointRouteBuilder routes, OhDataRegistration registration)
    {
        string prefix = registration.Prefix;
        var group = routes.MapGroup(prefix);
        // Resolve JsonOptions once at startup so handlers don't pay DI lookup per request.
        var startupJsonOptions = routes.ServiceProvider
            .GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            ?.Value?.SerializerOptions;

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
                    // §8.2.3: Reject Accept headers that don't include application/json or */*.
                    string accept = ctx.HttpContext.Request.Headers.Accept.ToString();
                    if (!string.IsNullOrEmpty(accept)
                        && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                        && !accept.Contains("*/*", StringComparison.OrdinalIgnoreCase))
                    {
                        return ODataError(406, "NotAcceptable",
                            "The server can only produce application/json responses. " +
                            "Set Accept: application/json or omit the Accept header.");
                    }
                }
            }

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
        if (etagList.Contains("*")) return null; // wildcard -- always matches

        object? current = await requestSource.InvokeGetByIdAsync(parsedKey!, ct);
        if (current is null)
            return ODataError(404, "NotFound", "Resource not found.");
        string currentETag = requestSource.InvokeGetETag(current);
        if (!etagList.Contains(currentETag))
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

    // Unified collection pipeline: Serialize → ETag → Expand → Select.
    // Serialises exactly once with _camelCaseSerializerOptions, then applies each stage in order.
    private static async Task<JsonArray> ApplyCollectionPipelineAsync(
        object[] originalItems,
        ODataQueryOptions options,
        IEntitySetEndpointSource source,
        IEntitySetEndpointSource requestSource,
        CancellationToken ct)
    {
        // Stage 1: Serialize once with camelCase options.
        JsonArray json = JsonSerializer.SerializeToNode(originalItems, _camelCaseSerializerOptions)!.AsArray();

        // Stage 2: Inject @odata.etag using the original (pre-expand) items for ETag computation.
        if (source.HasETag)
        {
            InjectETagsIntoJsonArray(json, originalItems, requestSource);
        }

        // Stage 3: Inject expanded nav properties (if $expand requested).
        if (options.SelectExpand?.SelectExpandClause is not null)
        {
            var expandedProps = options.SelectExpand.SelectExpandClause.SelectedItems
                .OfType<ExpandedNavigationSelectItem>()
                .Select(e => e.PathToNavigationProperty.FirstSegment.Identifier)
                .ToList();

            if (expandedProps.Count > 0)
            {
                // Cache the key PropertyInfo once outside the inner loop (M-3 perf fix).
                PropertyInfo? cachedKeyProp = originalItems.Length > 0
                    ? originalItems[0].GetType().GetProperty(source.KeyPropertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                    : null;

                foreach (string? propName in expandedProps)
                {
                    // Use requestSource for nav route handlers — they capture scoped dependencies.
                    var navRoute = requestSource.NavigationRoutes.FirstOrDefault(n =>
                        string.Equals(n.PropertyName, propName, StringComparison.OrdinalIgnoreCase));
                    if (navRoute is null) continue;

                    for (int i = 0; i < originalItems.Length; i++)
                    {
                        if (cachedKeyProp?.GetValue(originalItems[i]) is not { } keyVal) continue;

                        object? related = await navRoute.Handler(keyVal, ct);
                        if (json[i] is JsonObject obj)
                        {
                            obj[propName] = related is null
                                ? null
                                : JsonSerializer.SerializeToNode(related, _camelCaseSerializerOptions);
                        }
                    }
                }
            }
        }

        // Stage 4: Strip unselected properties (if $select requested).
        if (options.SelectExpand?.SelectExpandClause is not null)
        {
            var selectedProps = ExtractSelectedProperties(options.SelectExpand.SelectExpandClause);
            if (selectedProps is not null)
            {
                foreach (JsonObject obj in json.OfType<JsonObject>())
                {
                    var toRemove = obj.Select(p => p.Key)
                                     // OData annotations (e.g. @odata.etag) are metadata and must survive $select.
                                     .Where(k => !k.StartsWith("@", StringComparison.Ordinal) &&
                                                 !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                                     .ToList();
                    foreach (string? key in toRemove) obj.Remove(key);
                }
            }
        }

        return json;
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

        // When only $expand (no $select) is used, AllSelected is false but SelectedItems
        // has no PathSelectItems — only ExpandedNavigationSelectItems. An empty set would
        // strip every property in Stage 4, so treat this as "keep all".
        if (props.Count == 0) return null;

        // Preserve expanded nav properties so they survive Stage 4 when $select and
        // $expand are combined (e.g. $select=Name&$expand=Children keeps both).
        foreach (var ensi in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            props.Add(ensi.PathToNavigationProperty.FirstSegment.Identifier);
        }

        return props;
    }

    // Batch 3: build the navigation collection envelope, applying $select if present.
    // Returns (envelope, null) on success or (null, errorResult) when $select contains
    // an unknown property name.
    private static (Dictionary<string, object?>? Envelope, IResult? Error) BuildNavEnvelope(
        string baseUrl, string name, string key, string navPropertyName,
        long? navCount, object[] itemArray, HttpContext ctx, Type? navItemType)
    {
        // Apply $select post-processing for navigation results if requested.
        // We parse the $select query param directly (navigation routes don't go through
        // ODataQueryOptions) and filter the serialized items.
        object valueToReturn = itemArray;
        if (ctx.Request.Query.TryGetValue("$select", out var selectParam) && !string.IsNullOrEmpty(selectParam))
        {
            var selectedProps = new HashSet<string>(
                selectParam.ToString().Split(',').Select(p => p.Trim()),
                StringComparer.OrdinalIgnoreCase);

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

            var json = JsonSerializer.SerializeToNode(itemArray, _camelCaseSerializerOptions)!.AsArray();
            foreach (JsonObject obj in json.OfType<JsonObject>())
            {
                var toRemove = obj.Select(p => p.Key)
                                 .Where(k => !selectedProps.Contains(k, StringComparer.OrdinalIgnoreCase))
                                 .ToList();
                foreach (string? k in toRemove) obj.Remove(k);
            }
            valueToReturn = json;
        }

        var envelope = new Dictionary<string, object?>();
        envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}({key})/{navPropertyName}";
        if (navCount.HasValue) envelope["@odata.count"] = navCount;
        envelope["value"] = valueToReturn;
        return (envelope, null);
    }

    // Gap 5: ODataEntityNode with optional @odata.id
    // Gap 2: optional @odata.etag in response body (§4.5.9)
    // OData-JSON §4.5: annotations SHOULD appear before the properties they describe.
    // Build a new JsonObject with annotations first, then copy entity properties.
    private static JsonObject ODataEntityNode(
        HttpContext ctx, string prefix, string contextSegment, object entity,
        JsonSerializerOptions? jsonOptions, string? odataId = null, string? etag = null)
    {
        var serialized = JsonSerializer.SerializeToNode(entity, jsonOptions)!.AsObject();
        string baseUrl = BuildBaseUrl(ctx, prefix);

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
        JsonSerializerOptions? jsonOptions, string? odataId = null, string? etag = null) =>
        Results.Ok(ODataEntityNode(ctx, prefix, $"{name}/$entity", entity, jsonOptions, odataId: odataId, etag: etag));

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

        // Collection-level routes use a sub-group so they can use the short "" template.
        var entityGroup = entityAuthGroup.MapGroup($"/{name}");

        // Cache ODataQueryContext and ODataQuerySettings once at startup so each request
        // does not allocate new instances. Both are read-only after construction.
        var cachedODataQueryContext = new ODataQueryContext(registration.EdmModel, typeof(TModel), null);
        var cachedCountSettings = new ODataQuerySettings();
        var cachedQuerySettings = new ODataQuerySettings { PageSize = source.MaxTop };

        // Priority 1: ODataEntitySetProfile with direct ODataQueryOptions handler
        if (source is IODataEntitySetEndpointSource odataSource && odataSource.HasGetODataQueryable)
        {
            entityGroup.MapGet("", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var s = ResolveHandlers(ctx);
                    var odataSrc = (IODataEntitySetEndpointSource)s;
                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);
                    var odataResult = await odataSrc.InvokeGetODataQueryableAsync(options, ct);
                    var queryable = odataResult.Items is IQueryable<TModel> typedQ
                        ? typedQ
                        : odataResult.Items.Cast<TModel>().AsQueryable();

                    object[] items = queryable.ToArray();

                    JsonArray finalItems = await ApplyCollectionPipelineAsync(items, options, source, s, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                    // $count=true: prefer TotalCount if profile provided it (pre-paging), otherwise
                    // fall back to items.Length (post-paging).
                    if (options.Count?.Value == true)
                    {
                        envelope["@odata.count"] = odataResult.TotalCount ?? (long)items.Length;
                    }
                    // nextLink: prefer profile-provided next link over framework-computed.
                    if (odataResult.NextLink is not null)
                    {
                        envelope["@odata.nextLink"] = odataResult.NextLink;
                    }
                    envelope["value"] = finalItems;
                    return Results.Ok(envelope);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces(200).Produces(400)
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
                    var s = ResolveHandlers(ctx);
                    var queryable = (IQueryable<TModel>)(await s.InvokeGetQueryableAsync(ct))
                                    .Cast<TModel>();

                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);

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
                        odataCount = countQ.LongCount();
                    }

                    // Apply filter/orderby/skip/top without $select so TModel shape is preserved.
                    // $select is handled via JsonNode post-processing to avoid ISelectExpandWrapper casing issues.
                    IQueryable<TModel> filtered = queryable;
                    if (options.Filter is not null)
                        filtered = (IQueryable<TModel>)options.Filter.ApplyTo(filtered, cachedQuerySettings);
                    if (options.OrderBy is not null)
                        filtered = (IQueryable<TModel>)options.OrderBy.ApplyTo(filtered, cachedQuerySettings);

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

                    // Batch 4: Prefer: maxpagesize=N — client-requested page limit (§8.2.8.3).
                    // $top takes precedence; maxpagesize overrides source.MaxTop when $top is absent.
                    int? preferredPageSize = ParseMaxPageSize(ctx);
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
                        int? pageLimit = preferredPageSize ?? source.MaxTop;
                        if (pageLimit.HasValue)
                            filtered = filtered.Take(pageLimit.Value);
                        if (preferredPageSize.HasValue)
                            ctx.Response.Headers["Preference-Applied"] = $"maxpagesize={preferredPageSize.Value}";
                    }

                    var items = filtered.ToArray();

                    // Gap 3: compute nextLink when MaxTop (or preferred page size) is set and page is full
                    string? nextLink = null;
                    int effectivePageSize = preferredPageSize ?? (source.MaxTop ?? 0);
                    if (effectivePageSize > 0 && items.Length == effectivePageSize && options.Top is null)
                    {
                        int nextSkip = effectiveSkip + items.Length;
                        string token = Convert.ToBase64String(BitConverter.GetBytes(nextSkip));
                        nextLink = BuildNextPageLink(ctx, token);
                    }

                    JsonArray finalItems = await ApplyCollectionPipelineAsync(items, options, source, s, ct);

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
            }).WithTags(name).Produces(200).Produces(400)
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

                        var searchResults = await s.InvokeSearchAsync(searchTerm.ToString(), ct);
                        object[] searchItems = searchResults.ToArray();

                        JsonArray searchFinal = await ApplyCollectionPipelineAsync(searchItems, options, source, s, ct);
                        string searchBaseUrl = BuildBaseUrl(ctx, prefix);
                        var searchEnvelope = new Dictionary<string, object?>();
                        searchEnvelope["@odata.context"] = $"{searchBaseUrl}/$metadata#{name}";
                        // Batch 5: include @odata.count for search results when $count=true is requested.
                        if (options.Count?.Value == true)
                            searchEnvelope["@odata.count"] = (long)searchItems.Length;
                        searchEnvelope["value"] = searchFinal;
                        return Results.Ok(searchEnvelope);
                    }

                    object? result = await s.InvokeGetAllAsync(ct);
                    var enumerable = result as IEnumerable<TModel> ?? Enumerable.Empty<TModel>();
                    var rawItems = enumerable.ToArray();

                    JsonArray finalItems = await ApplyCollectionPipelineAsync(rawItems, options, source, s, ct);

                    string baseUrl = BuildBaseUrl(ctx, prefix);
                    var envelope = new Dictionary<string, object?>();
                    envelope["@odata.context"] = $"{baseUrl}/$metadata#{name}";
                    // Batch 5: §11.2.6.5 — include @odata.count when $count=true is requested on GetAll path.
                    if (options.Count?.Value == true)
                        envelope["@odata.count"] = (long)rawItems.Length;
                    envelope["value"] = finalItems;
                    return Results.Ok(envelope);
                }
                catch (Microsoft.OData.ODataException ex)
                {
                    return ODataError(400, "InvalidQueryOption", ex.Message);
                }
            }).WithTags(name).Produces(200).Produces(400)
              .WithMetadata(new OhDataQueryOptionsMetadata(
                  FilterEnabled: false,
                  OrderByEnabled: false,
                  SelectEnabled: false,
                  ExpandEnabled: false,
                  CountEnabled: false,
                  SearchEnabled: source.HasSearch,
                  MaxTop: null));
        }

        bool hasCountSource = (source is IODataEntitySetEndpointSource odsCheck && odsCheck.HasGetODataQueryable)
            || source.HasGetQueryable || source.HasGetAll;
        if (hasCountSource)
        {
            entityGroup.MapGet("/$count", async (HttpContext ctx, CancellationToken ct) =>
            {
                try
                {
                    var s = ResolveHandlers(ctx);
                    var options = new ODataQueryOptions<TModel>(cachedODataQueryContext, ctx.Request);

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
            }).WithTags(name).Produces<long>(200).Produces(400)
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
                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
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
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: etagValue);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(404)
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
            // If-None-Match on POST is not supported: the framework cannot extract the key from
            // the body without knowing the key property. Developers should handle this themselves.
            var rb = entityGroup.MapPost("", async (TModel model, HttpContext ctx, CancellationToken ct) =>
            {
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
                string keyStr = s.InvokeGetKeyString(result);
                string baseUrl = BuildBaseUrl(ctx, prefix);
                string odataId = $"{baseUrl}/{name}({keyStr})";

                // Gap 4: Prefer: return=minimal → 204 with Location header
                if (PrefersMinimal(ctx))
                {
                    ctx.Response.Headers.Location = odataId;
                    // §8.3.3: Content-Location on 204 mirrors the Location of the created entity.
                    ctx.Response.Headers["Content-Location"] = odataId;
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
                    var createdNode = ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, odataId: odataId, etag: postEtag);
                    return Results.Created(odataId, createdNode);
                }
            });
            rb.WithTags(name).Produces<TModel>(201).Produces(400);
        }

        if (source.HasPut)
        {
            var rb = entityAuthGroup.MapPut($"/{name}({{key}})", async (string key, TModel model, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PUT {Prefix}/{Name}({Key})", prefix, name, SanitizeLogValue(key));
                try
                {
                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    string bodyKeyStr = s.InvokeGetKeyString(model);
                    string parsedKeyStr = string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey);
                    if (!string.Equals(parsedKeyStr, bodyKeyStr, StringComparison.Ordinal))
                        return ODataError(400, "BadRequest", "Key in URL does not match key in request body.", target: "key");
                    var etagCheck = await CheckETagAsync(source, s, ctx, parsedKey!, ct);
                    if (etagCheck is not null) return etagCheck;
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
                            ctx.Response.Headers.Location = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
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
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
                    if (wasCreated)
                        return Results.Created(odataId, ODataEntityNode(ctx, prefix, $"{name}/$entity", result, jsonOptions, odataId: odataId, etag: putEtag));
                    return ODataEntityResult(ctx, prefix, name, result, jsonOptions, odataId: odataId, etag: putEtag);
                }
                catch (FormatException ex)
                {
                    logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                    return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                }
            });
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404);
        }

        if (source.HasPatch)
        {
            var rb = entityAuthGroup.MapMethods($"/{name}({{key}})", PatchMethod, async (string key, HttpContext ctx, CancellationToken ct) =>
            {
                logger?.LogDebug("PATCH {Prefix}/{Name}({Key})", prefix, name, SanitizeLogValue(key));
                try
                {
                    var s = ResolveHandlers(ctx);
                    object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                    var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                        ctx.Request.Body, jsonOptions, ct);

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
                    string odataId = $"{BuildBaseUrl(ctx, prefix)}/{name}({key})";
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
            rb.Accepts<TModel>("application/json");
            rb.WithTags(name).Produces<TModel>(200).Produces(400).Produces(404);
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

        // Navigation property routes
        foreach (var nav in source.NavigationRoutes)
        {
            string navPropertyName = nav.PropertyName;
            bool navIsCollection = nav.IsCollection;
            Type? navItemType = nav.NavItemType;
            var rb = entityAuthGroup.MapGet($"/{name}({{key}})/{navPropertyName}",
                async (string key, HttpContext ctx, CancellationToken ct) =>
                {
                    try
                    {
                        var s = ResolveHandlers(ctx);
                        var requestNav = s.NavigationRoutes.First(n => n.PropertyName == navPropertyName);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        object? result = await requestNav.Handler(parsedKey!, ct);
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
                            // Batch 3: apply $select post-processing to navigation collection results
                            var (navEnv, navEnvError) = BuildNavEnvelope(baseUrl, name, key, navPropertyName, navCount, itemArray, ctx, navItemType);
                            if (navEnvError is not null) return navEnvError;
                            return Results.Ok(navEnv);
                        }
                        return Results.Ok(result);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'");
                    }
                })
                .WithTags(name)
                .Produces(200)
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
                            if (result is null) return Results.NotFound();
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
                    .Produces(200)
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
                        string context = $"{baseUrl}/$metadata#{name}({key})/{navRefPropertyName}/$ref";

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
                                    // Cache PropertyInfo outside the loop — all children share the same type.
                                    PropertyInfo? cachedRefKeyProp = null;
                                    foreach (object child in childEnum)
                                    {
                                        cachedRefKeyProp ??= child.GetType().GetProperty(
                                            refNavCapture.ChildKeyPropertyName,
                                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                        if (cachedRefKeyProp?.GetValue(child) is { } k)
                                        {
                                            string formattedKey = string.Format(CultureInfo.InvariantCulture, "{0}", k);
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
                                    var kProp = child.GetType().GetProperty(
                                        refNavCapture.ChildKeyPropertyName,
                                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                    if (kProp?.GetValue(child) is { } k)
                                    {
                                        string childKey = string.Format(CultureInfo.InvariantCulture, "{0}", k);
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
                .Produces(200);

            // POST /{name}({key})/{nav}/$ref   — collection nav: add a link (§11.4.6.2)
            // PUT  /{name}({key})/{nav}/$ref   — single-value nav: set the link (§11.4.6.3)
            if (nav.AddRef is not null)
            {
                string addRefNavPropertyName = navRefPropertyName;
                async Task<IResult> handleAddOrSetRef(string key, HttpContext ctx, CancellationToken ct)
                {
                    try
                    {
                        var s = ResolveHandlers(ctx);
                        var requestNav = s.NavigationRoutes.First(n => n.PropertyName == addRefNavPropertyName);
                        object? parsedKey = ODataKeyParser.Parse(key, typeof(TKey));
                        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
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

                if (navRefIsCollection)
                {
                    entityAuthGroup.MapPost($"/{name}({{key}})/{navRefPropertyName}/$ref", handleAddOrSetRef)
                        .WithTags(name)
                        .Produces(204)
                        .Produces(400);
                }
                else
                {
                    entityAuthGroup.MapPut($"/{name}({{key}})/{navRefPropertyName}/$ref", handleAddOrSetRef)
                        .WithTags(name)
                        .Produces(204)
                        .Produces(400);
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
                object? result = await requestFn.Invoke(args, ct);
                if (result is null) return Results.NoContent();
                // Gap 1: @odata.context on function results when return type matches TModel
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions);
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
                var s = ResolveHandlers(ctx);
                var requestAction = s.BoundActions.First(a => a.Name == actionCapture.Name && !a.IsEntityLevel);
                object?[] args = new object?[actionCapture.Parameters.Length];
                if (actionCapture.Parameters.Length > 0)
                {
                    try
                    {
                        var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                            ctx.Request.Body, cancellationToken: ct);
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
                return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions);
            }).WithTags(name).Produces(200).Produces(204).Produces(400);
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
                        object? result = await requestFn.Invoke(args, ct);
                        if (result is null) return Results.NoContent();
                        // Gap 1: @odata.context on entity-level function results
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                })
                .WithTags(name).Produces(200).Produces(204).Produces(400);
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
                            try
                            {
                                var body = await JsonSerializer.DeserializeAsync<JsonElement>(
                                    ctx.Request.Body, cancellationToken: ct);
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
                        return WrapBoundOpResult(ctx, prefix, name, result, source.ModelType, jsonOptions);
                    }
                    catch (FormatException ex)
                    {
                        logger?.LogWarning(ex, "OhData: bad key '{Key}' for {Name}", SanitizeLogValue(key), name);
                        return ODataError(400, "BadRequest", $"Invalid key format for {name}: '{key}'", target: "key");
                    }
                })
                .WithTags(name).Produces(200).Produces(204).Produces(400);
        }

    }

    // Gap 1: Wrap bound operation result with @odata.context when return type matches TModel (§11.5.3).
    // For collection results (IEnumerable<TModel>): context = {root}/$metadata#{EntitySet}
    // For single results (TModel): context = {root}/$metadata#{EntitySet}/$entity
    // For primitives/other types: return Results.Ok directly (no wrapping needed).
    private static IResult WrapBoundOpResult(
        HttpContext ctx, string prefix, string entitySetName, object result, Type modelType,
        JsonSerializerOptions? jsonOptions)
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
            return ODataEntityResult(ctx, prefix, entitySetName, result, jsonOptions);
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

