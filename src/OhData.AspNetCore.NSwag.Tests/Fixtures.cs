using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;

namespace OhData.AspNetCore.NSwag.Tests;

// ── Shared test entity ────────────────────────────────────────────────────────

internal class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

// ── Query-capability fixtures (mirrors OhDataQueryOptionsMetadata scenarios) ───

/// <summary>All query-capability flags on, plus a Search handler and a MaxTop cap.</summary>
internal class AllFlagsWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public AllFlagsWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "AllFlagsWidgets";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = 25;

        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        Search = (term, ct) => Task.FromResult<IEnumerable<Widget>>(
            _store.Where(w => w.Name.Contains(term)));
    }
}

/// <summary>No query-capability flags on — only GetAll, no Search handler.</summary>
internal class NoFlagsWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public NoFlagsWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "NoFlagsWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
    }
}

internal class FilterOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public FilterOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "FilterOnlyWidgets";
        FilterEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

internal class OrderByOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public OrderByOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "OrderByOnlyWidgets";
        OrderByEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

internal class SelectOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public SelectOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "SelectOnlyWidgets";
        SelectEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

internal class ExpandOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public ExpandOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "ExpandOnlyWidgets";
        ExpandEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

internal class CountOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public CountOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "CountOnlyWidgets";
        CountEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

/// <summary>Search only — GetAll (not GetQueryable) plus a Search handler, no other flags.</summary>
internal class SearchOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public SearchOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "SearchOnlyWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        Search = (term, ct) => Task.FromResult<IEnumerable<Widget>>(
            _store.Where(w => w.Name.Contains(term)));
    }
}

/// <summary>Only GetById configured — no collection GET, no $count route registered at all.
/// Used to verify what OhDataQueryOptionsMetadata actually looks like on a single-entity route.</summary>
internal class GetByIdOnlyWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public GetByIdOnlyWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "GetByIdOnlyWidgets";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
    }
}

/// <summary>Plain GetQueryable collection profile used by the duplicate-parameter-guard test.</summary>
internal class DupTopWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public DupTopWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "DupTopWidgets";
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}
