using System.Linq;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData.UriParser;

namespace OhData;

// SPIKE #867 -- PROTOTYPE ONLY.
internal static class StreamingSpikeHelpers
{
    internal static bool HasExpand(ODataQueryOptions options) =>
        options.SelectExpand?.SelectExpandClause is { } clause
        && clause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any();
}
