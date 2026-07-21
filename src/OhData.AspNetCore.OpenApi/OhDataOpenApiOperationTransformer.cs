using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OhData;

namespace OhData.AspNetCore.OpenApi;

/// <summary>
/// Microsoft.AspNetCore.OpenApi operation transformer that adds OData query parameters to
/// collection endpoints based on <see cref="OhDataQueryOptionsMetadata"/> attached to the
/// endpoint.
/// </summary>
/// <remarks>
/// Ships in the EnGen.OhData.AspNetCore.OpenApi package so the core server package carries
/// no dependency on Microsoft.AspNetCore.OpenApi. Register via:
/// <code>
/// builder.Services.AddOpenApi(o =&gt; o.AddOperationTransformer&lt;OhDataOpenApiOperationTransformer&gt;());
/// </code>
/// </remarks>
public sealed class OhDataOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;

        // Leg 4 (docs-fidelity): Microsoft.AspNetCore.OpenApi already reads WithSummary()/
        // WithDescription() natively, but this is applied explicitly too (guarded so it never
        // overwrites a value already set) for symmetry with OhDataNSwagOperationProcessor/
        // OhDataSwaggerOperationFilter, which need the explicit fallback -- see the comment there.
        if (string.IsNullOrEmpty(operation.Summary))
        {
            string? summary = endpointMetadata.OfType<IEndpointSummaryMetadata>().LastOrDefault()?.Summary;
            if (!string.IsNullOrEmpty(summary)) operation.Summary = summary;
        }
        if (string.IsNullOrEmpty(operation.Description))
        {
            string? description = endpointMetadata.OfType<IEndpointDescriptionMetadata>().LastOrDefault()?.Description;
            if (!string.IsNullOrEmpty(description)) operation.Description = description;
        }

        // Leg 2 (docs-fidelity): OhDataApiDescriptionProvider (registered by AddOhData) already
        // gave write routes a real request-body schema by the time operation transformers run --
        // it augments the ApiDescription's ParameterDescriptions/SupportedRequestFormats, which
        // the base document generator reads to build operation.RequestBody in the first place.
        // ApiParameterDescription itself has no free-text description field, though, so the
        // human-readable text from OhDataRequestBodyMetadata is applied here instead, directly
        // onto the already-built RequestBody.
        var bodyMetadata = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<OhDataRequestBodyMetadata>()
            .FirstOrDefault();
        if (bodyMetadata is not null && operation.RequestBody is not null)
        {
            operation.RequestBody.Description = bodyMetadata.Description;
        }

        var metadata = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<OhDataQueryOptionsMetadata>()
            .FirstOrDefault();

        if (metadata is null) { return Task.CompletedTask; }

        operation.Parameters ??= new List<IOpenApiParameter>();

        // Always add $top and $skip for paged collection endpoints
        if (operation.Parameters.All(p => p.Name != "$top"))
        {
            string topDesc = "Maximum number of items to return" +
                (metadata.MaxTop.HasValue ? $" (server cap: {metadata.MaxTop})" : "");
            operation.Parameters.Add(ODataParam("$top", topDesc));
            operation.Parameters.Add(ODataParam("$skip", "Number of items to skip (offset paging)"));
        }

        if (metadata.FilterEnabled)
        {
            operation.Parameters.Add(ODataParam("$filter",
                "OData filter expression (e.g. Price gt 10 and contains(Name,'Widget'))"));
        }

        if (metadata.OrderByEnabled)
        {
            operation.Parameters.Add(ODataParam("$orderby",
                "OData sort expression (e.g. Name asc,Price desc)"));
        }

        if (metadata.SelectEnabled)
        {
            operation.Parameters.Add(ODataParam("$select",
                "Comma-separated list of properties to include in the response"));
        }

        if (metadata.ExpandEnabled)
        {
            operation.Parameters.Add(ODataParam("$expand",
                "Comma-separated list of navigation properties to expand inline"));
        }

        if (metadata.CountEnabled)
        {
            operation.Parameters.Add(ODataParam("$count",
                "Include total matching count in the response envelope ($count=true)"));
        }

        if (metadata.SearchEnabled)
        {
            operation.Parameters.Add(ODataParam("$search", "Free-text search term"));
        }

        return Task.CompletedTask;
    }

    private static OpenApiParameter ODataParam(string name, string description) => new()
    {
        Name = name,
        In = ParameterLocation.Query,
        Required = false,
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        Description = description,
    };
}
