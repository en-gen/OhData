using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using OhData;

namespace OhData.AspNetCore.Swashbuckle;

/// <summary>
/// Swashbuckle operation filter that adds OData query parameters to collection endpoints
/// based on <see cref="OhDataQueryOptionsMetadata"/> attached to the endpoint.
/// </summary>
/// <remarks>
/// Ships in the EnGen.OhData.AspNetCore.Swashbuckle package so the core server
/// package carries no Swashbuckle dependency. Register via:
/// <code>
/// builder.Services.AddSwaggerGen(c =&gt; c.OperationFilter&lt;OhDataSwaggerOperationFilter&gt;());
/// </code>
/// </remarks>
public sealed class OhDataSwaggerOperationFilter : IOperationFilter
{
    /// <inheritdoc/>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var endpointMetadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

        // Leg 4 (docs-fidelity): see the matching comment in OhDataNSwagOperationProcessor for
        // why this is applied explicitly rather than assumed to flow through automatically.
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

        // Leg 2 (docs-fidelity): see the matching comment in OhDataOpenApiOperationTransformer
        // for why this only sets the description text — the schema itself was already built by
        // Swashbuckle from the ApiParameterDescription OhDataApiDescriptionProvider added.
        var bodyMetadata = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<OhDataRequestBodyMetadata>()
            .FirstOrDefault();
        if (bodyMetadata is not null && operation.RequestBody is not null)
        {
            operation.RequestBody.Description = bodyMetadata.Description;
        }

        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<OhDataQueryOptionsMetadata>()
            .FirstOrDefault();

        if (metadata is null) { return; }

        if (operation.Parameters is null)
        {
            operation.Parameters = new List<IOpenApiParameter>();
        }

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
