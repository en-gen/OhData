using Microsoft.OData.ModelBuilder;

namespace OhData.Abstractions;

internal interface IVisitModelBuilder
{
    internal void VisitModelBuilder(ODataModelBuilder builder, EntitySetDefaults defaults);
}