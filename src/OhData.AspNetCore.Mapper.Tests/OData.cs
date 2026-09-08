using System;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// Parses a query option against the same EDM OhData builds for the model, so a unit test binds the
/// clause the server would bind rather than an expression written to look like one.
/// </summary>
internal static class OData
{
    public static readonly IEdmModel Model = Build();

    public static FilterClause ParseFilter(string filter) =>
        Parser($"Products?$filter={Uri.EscapeDataString(filter)}").ParseFilter()!;

    public static OrderByClause ParseOrderBy(string orderBy) =>
        Parser($"Products?$orderby={Uri.EscapeDataString(orderBy)}").ParseOrderBy()!;

    private static ODataUriParser Parser(string relativeUri) =>
        new(Model, new Uri("http://localhost/odata/"), new Uri(relativeUri, UriKind.Relative));

    private static IEdmModel Build()
    {
        ODataConventionModelBuilder builder = new();
        builder.EntitySet<ProductDto>("Products");
        builder.EntitySet<CategoryDto>("Categories");
        builder.EntitySet<TagDto>("Tags");
        builder.EntitySet<ReviewDto>("Reviews");
        return builder.GetEdmModel();
    }
}
