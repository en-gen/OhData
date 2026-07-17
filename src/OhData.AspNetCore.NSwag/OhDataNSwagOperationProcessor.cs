using System.Linq;
using NJsonSchema;
using NSwag;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace OhData.AspNetCore;

/// <summary>
/// NSwag operation processor that adds OData query parameters to collection endpoints
/// based on <see cref="OhDataQueryOptionsMetadata"/> attached to the endpoint.
/// </summary>
/// <remarks>
/// Ships in the EnGen.OhData.AspNetCore.NSwag package so the core server
/// package carries no NSwag dependency. Register via:
/// <code>
/// builder.Services.AddOpenApiDocument(s =&gt; s.OperationProcessors.Add(new OhDataNSwagOperationProcessor()));
/// </code>
/// </remarks>
public sealed class OhDataNSwagOperationProcessor : IOperationProcessor
{
    /// <inheritdoc/>
    public bool Process(OperationProcessorContext context)
    {
        if (context is not AspNetCoreOperationProcessorContext aspNetCoreContext)
        {
            return true;
        }

        var metadata = aspNetCoreContext.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<OhDataQueryOptionsMetadata>()
            .FirstOrDefault();

        if (metadata is null) { return true; }

        var operation = context.OperationDescription.Operation;

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

        return true;
    }

    private static OpenApiParameter ODataParam(string name, string description) => new()
    {
        Name = name,
        Kind = OpenApiParameterKind.Query,
        IsRequired = false,
        Schema = new JsonSchema { Type = JsonObjectType.String },
        Description = description,
    };
}
