using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace OhData.AspNetCore;

/// <summary>
/// Plain marker metadata attached to OhData write routes (entity POST/PUT/PATCH, nav-POST,
/// property PUT/PATCH, $ref POST/PUT, bound/unbound actions) to tell
/// <see cref="OhDataApiDescriptionProvider"/> which request-body documentation to synthesize for
/// that route.
/// </summary>
/// <remarks>
/// Deliberately implements no ASP.NET Core routing interface — not
/// <c>Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata</c>, not
/// <c>IEndpointMetadataProvider</c>, nothing. See the comment near the PATCH route registration
/// in <c>OhDataEndpointFactory</c> (and the "POST/PUT/PATCH deserialize the request body by
/// hand" note in CLAUDE.md) for why <c>.Accepts&lt;T&gt;()</c>/<c>IAcceptsMetadata</c>
/// specifically must never be attached to these routes: ASP.NET Core's own request
/// content-type short-circuit fires on that metadata ahead of this framework's manual
/// <c>IsJsonContentType()</c> check, replacing the OData error envelope with an empty,
/// framework-generated 415. A plain POCO with no recognized interface carries none of that
/// runtime behavior — it is inert to everything except the provider below, which reads it
/// purely for documentation purposes, after the routing/dispatch pipeline has already run.
/// </remarks>
public sealed class OhDataRequestBodyMetadata
{
    /// <summary>The CLR type used to generate the request body's JSON schema.</summary>
    public required Type BodyType { get; init; }

    /// <summary>Human-readable description of the request body, surfaced in the generated docs.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// Describes a single query-string parameter of a bound/unbound OData <em>function</em>, for
/// <see cref="OhDataApiDescriptionProvider"/> to turn into a query <see cref="ApiParameterDescription"/>.
/// See <see cref="OhDataQueryParametersMetadata"/> (issue #181).
/// </summary>
public sealed class OhDataQueryParameter
{
    /// <summary>The parameter name, as it appears in the query string (e.g. <c>count</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The parameter's CLR type, used to generate its JSON schema.</summary>
    public required Type Type { get; init; }

    /// <summary>
    /// <c>true</c> when the delegate parameter has no C# default value (the caller must supply it);
    /// <c>false</c> when it has one (e.g. <c>count = 10</c>), matching the runtime's
    /// <c>ParameterInfo.HasDefaultValue</c>-driven required/optional handling.
    /// </summary>
    public required bool IsRequired { get; init; }
}

/// <summary>
/// Plain marker metadata attached to OhData bound/unbound <em>function</em> routes (both
/// collection-level <c>GET /{EntitySet}/{Name}?param=value</c> and entity-level
/// <c>GET /{EntitySet}({key})/{Name}?param=value</c>, plus service-root unbound functions) to tell
/// <see cref="OhDataApiDescriptionProvider"/> which query-string parameters to document for that
/// route (issue #181).
/// </summary>
/// <remarks>
/// Analogous to <see cref="OhDataRequestBodyMetadata"/> (which documents write/action request
/// bodies): a function reads its parameters from the query string in a handler that takes only
/// <c>(HttpContext, CancellationToken)</c> — no bound minimal-API parameters — so ApiExplorer sees
/// no parameters for it at all and every generated OpenAPI document would otherwise render
/// <c>parameters: []</c>, even though the parameters are real and honored at runtime. Like
/// <see cref="OhDataRequestBodyMetadata"/>, this is an inert POCO implementing no ASP.NET Core
/// routing interface; it is read purely for documentation, after dispatch has already run.
/// </remarks>
public sealed class OhDataQueryParametersMetadata
{
    /// <summary>The function's query-string parameters, in declaration order.</summary>
    public required IReadOnlyList<OhDataQueryParameter> Parameters { get; init; }
}

/// <summary>
/// ApiExplorer post-processor that adds a JSON request-body description to any
/// <see cref="ApiDescription"/> whose endpoint carries <see cref="OhDataRequestBodyMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// OhData's write routes (entity POST/PUT/PATCH, nav-POST, property PUT/PATCH, $ref POST/PUT,
/// bound/unbound actions) read and JSON-deserialize their request bodies by hand rather than via
/// a bound minimal-API parameter (see the "POST/PUT/PATCH deserialize the request body by hand"
/// note in CLAUDE.md), so ApiExplorer sees no request body for them at all — every OpenAPI
/// document generator built on top of it (Microsoft.AspNetCore.OpenApi, NSwag, Swashbuckle)
/// renders no body editor for these routes without this provider.
/// </para>
/// <para>
/// Registered once, idempotently, inside <c>AddOhData</c> via <c>TryAddEnumerable</c>. Runs its
/// logic in <see cref="OnProvidersExecuted"/> rather than <see cref="OnProvidersExecuting"/>:
/// the ApiExplorer pipeline (<c>ApiDescriptionGroupCollectionProvider</c>) calls every
/// registered provider's <c>OnProvidersExecuting</c> (in ascending <see cref="Order"/>) before
/// calling any provider's <c>OnProvidersExecuted</c> (in descending <see cref="Order"/>), so by
/// the time this method runs, the framework's own endpoint-derived <see cref="ApiDescription"/>s
/// already exist in <see cref="ApiDescriptionProviderContext.Results"/> regardless of this
/// provider's own <see cref="Order"/> value.
/// </para>
/// </remarks>
internal sealed class OhDataApiDescriptionProvider : IApiDescriptionProvider
{
    // Swashbuckle's SwaggerGenerator dereferences ApiParameterDescription.ModelMetadata
    // unconditionally when building a request body's schema (GenerateRequestBodyFromBodyParameter)
    // and throws a NullReferenceException if it is null -- unlike Microsoft.AspNetCore.OpenApi and
    // NSwag, which tolerate a null ModelMetadata and fall back to reading .Type directly. A real
    // ModelMetadata is therefore required here, not just Type. EmptyModelMetadataProvider is the
    // lightweight, dependency-free, public implementation the framework itself ships for exactly
    // this kind of best-effort/no-validation metadata need (unlike DefaultModelMetadataProvider,
    // which requires MVC's internal composite-provider wiring and is not constructible standalone).
    // Deliberately not resolved from the host's DI container: IModelMetadataProvider is not
    // registered by default under AddOpenApi()/AddEndpointsApiExplorer()/AddSwaggerGen() (verified
    // empirically), and this metadata only drives documentation, never actual model binding or
    // validation, so a host-specific provider would add DI coupling for no behavioral benefit.
    private static readonly EmptyModelMetadataProvider s_modelMetadataProvider = new();

    /// <inheritdoc/>
    public int Order => 0;

    /// <inheritdoc/>
    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        // No-op: this provider only augments descriptions already produced by the framework's
        // own endpoint-metadata provider, which runs during OnProvidersExecuting. See the type
        // doc for why that logic lives in OnProvidersExecuted instead.
    }

    /// <inheritdoc/>
    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
        foreach (var description in context.Results)
        {
            AddRequestBody(description);
            AddFunctionQueryParameters(description);
        }
    }

    private static void AddRequestBody(ApiDescription description)
    {
        var metadata = description.ActionDescriptor.EndpointMetadata?
            .OfType<OhDataRequestBodyMetadata>()
            .FirstOrDefault();
        if (metadata is null) return;

        // Defensive: don't add a second body parameter if one is already present for some
        // reason (e.g. a future ASP.NET Core version starts inferring one on its own).
        if (description.ParameterDescriptions.Any(p => p.Source == BindingSource.Body)) return;

        description.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = "body",
            Source = BindingSource.Body,
            Type = metadata.BodyType,
            ModelMetadata = s_modelMetadataProvider.GetMetadataForType(metadata.BodyType),
            IsRequired = true,
        });

        if (!description.SupportedRequestFormats.Any(f =>
                string.Equals(f.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            description.SupportedRequestFormats.Add(new ApiRequestFormat { MediaType = "application/json" });
        }
    }

    // Issue #181: bound/unbound *functions* read their parameters from the query string, but
    // their handlers take only (HttpContext, CancellationToken) -- no bound minimal-API
    // parameters -- so ApiExplorer sees no parameters for them and every generated OpenAPI
    // document would render "parameters: []". (Actions get their body documented via
    // OhDataRequestBodyMetadata above; functions got nothing -- the asymmetry this fixes.)
    // Synthesize one query ApiParameterDescription per function parameter, which all three
    // document stacks (Microsoft.AspNetCore.OpenApi, Swashbuckle, NSwag) then render natively.
    private static void AddFunctionQueryParameters(ApiDescription description)
    {
        var metadata = description.ActionDescriptor.EndpointMetadata?
            .OfType<OhDataQueryParametersMetadata>()
            .FirstOrDefault();
        if (metadata is null) return;

        foreach (var parameter in metadata.Parameters)
        {
            // Defensive: skip a parameter already surfaced by the framework for this route (e.g.
            // if a future ASP.NET Core version starts inferring query parameters on its own).
            if (description.ParameterDescriptions.Any(p =>
                    p.Source == BindingSource.Query &&
                    string.Equals(p.Name, parameter.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            description.ParameterDescriptions.Add(new ApiParameterDescription
            {
                Name = parameter.Name,
                Source = BindingSource.Query,
                Type = parameter.Type,
                // A real ModelMetadata is required, not just Type -- see the s_modelMetadataProvider
                // comment above for why (Swashbuckle NREs on a null ModelMetadata).
                ModelMetadata = s_modelMetadataProvider.GetMetadataForType(parameter.Type),
                IsRequired = parameter.IsRequired,
            });
        }
    }
}
