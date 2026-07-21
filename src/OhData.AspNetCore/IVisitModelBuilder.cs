using Microsoft.OData.ModelBuilder;

namespace OhData;

internal interface IVisitModelBuilder
{
    internal void VisitModelBuilder(ODataModelBuilder builder, EntitySetDefaults defaults);
}
