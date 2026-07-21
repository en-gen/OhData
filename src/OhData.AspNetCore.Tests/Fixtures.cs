using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using OhData;

namespace OhData.AspNetCore.Tests;

// â”€â”€ Shared test entities and profiles â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

internal class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal class WidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store;

    public WidgetProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        CountEnabled = true;

        _store = new List<Widget>
        {
            new() { Id = 1, Name = "Sprocket" },
            new() { Id = 2, Name = "Cog" },
        };

        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);

        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };

        Put = (id, widget, ct) =>
        {
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };

        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);

        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
    }
}

/// <summary>Profile that intentionally configures no handlers â€” used to verify routes are omitted.</summary>
internal class EmptyProfile : EntitySetProfile<int, Widget>
{
    public EmptyProfile() : base(x => x.Id)
    {
        EntitySetName = "EmptyWidgets";
    }
}

/// <summary>Profile backed by IQueryable with $select enabled â€” used to test response shaping.</summary>
internal class QueryableWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store;

    public QueryableWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "QueryableWidgets";
        SelectEnabled = true;
        FilterEnabled = true;
        CountEnabled = true;

        _store = new List<Widget>
        {
            new() { Id = 1, Name = "Sprocket" },
            new() { Id = 2, Name = "Cog" },
        };

        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

/// <summary>Profile backed by EF Core InMemory IQueryable â€” used to verify $select works with EF Core.</summary>
internal class EfCoreWidgetProfile : EntitySetProfile<int, Widget>
{
    public EfCoreWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "EfWidgets";
        SelectEnabled = true;
        FilterEnabled = true;

        GetQueryable = (ct) =>
        {
            var opts = new DbContextOptionsBuilder<WidgetDbContext>()
                .UseInMemoryDatabase("EfCoreWidgets")
                .Options;
            var ctx = new WidgetDbContext(opts);
            if (!ctx.Widgets.Any())
            {
                ctx.Widgets.AddRange(
                    new Widget { Id = 1, Name = "Sprocket" },
                    new Widget { Id = 2, Name = "Cog" }
                );
                ctx.SaveChanges();
            }
            return Task.FromResult(ctx.Widgets.AsQueryable());
        };
    }
}

internal class WidgetDbContext : DbContext
{
    public WidgetDbContext(DbContextOptions<WidgetDbContext> options) : base(options) { }
    public DbSet<Widget> Widgets { get; set; } = null!;
}

/// <summary>Profile that requires authorization â€” used to verify auth is applied to routes.</summary>
internal class AuthorizedWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store;

    public AuthorizedWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "AuthorizedWidgets";

        _store = new List<Widget>
        {
            new() { Id = 1, Name = "Sprocket" },
        };

        RequireAuthorization();

        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
    }
}

internal class Child { public int Id { get; set; } public int ParentId { get; set; } public string Name { get; set; } = ""; }
internal class Parent { public int Id { get; set; } public string Name { get; set; } = ""; public IEnumerable<Child>? Children { get; set; } public Child? PrimaryChild { get; set; } }

internal class ParentWithChildrenProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new() { new() { Id = 1, Name = "Parent1" } };
    private static readonly List<Child> _children = new() { new() { Id = 1, ParentId = 1, Name = "Child1" } };

    public ParentWithChildrenProfile() : base(x => x.Id)
    {
        GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
            {
                var parent = _parents.FirstOrDefault(p => p.Id == parentId);
                if (parent is null) return Task.FromResult<IEnumerable<Child>>(null!);
                return Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId));
            });
    }
}

internal class ETagWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public ETagWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "ETagWidgets";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Put = (id, widget, ct) =>
        {
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };
        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);
        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
        UseETag(x => x.Name);
    }
}

internal class BoundOpsStore
{
    public List<Widget> Items { get; } = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };
}

internal class BoundOpsProfile : EntitySetProfile<int, Widget>
{
    private readonly BoundOpsStore _store;

    public BoundOpsProfile(BoundOpsStore store) : base(x => x.Id)
    {
        _store = store;
        EntitySetName = "BoundWidgets";
        SelectEnabled = true;
        FilterEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store.Items);

        BindFunction(GetByName);
        BindFunction(DoubleCount);
        BindAction(ClearAll);
        BindAction(AddSuffix);
    }

    // Function: GET /BoundWidgets/GetByName?name=Alpha
    private Task<IEnumerable<Widget>> GetByName(string name) =>
        Task.FromResult<IEnumerable<Widget>>(_store.Items.Where(w =>
            string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)));

    // Function: GET /BoundWidgets/DoubleCount?factor=2
    private Task<int> DoubleCount(int factor) =>
        Task.FromResult(_store.Items.Count * factor);

    // Action: POST /BoundWidgets/ClearAll  (no body params)
    private void ClearAll() => _store.Items.Clear();

    // Action: POST /BoundWidgets/AddSuffix  { "suffix": "!" }
    private void AddSuffix(string suffix)
    {
        foreach (var w in _store.Items) w.Name += suffix;
    }
}

/// <summary>Profile for testing PUT null (no-match) returning 404.</summary>
internal class NullPutProfile : EntitySetProfile<int, Widget>
{
    public NullPutProfile() : base(x => x.Id)
    {
        EntitySetName = "NullPutWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(null);
        Put = (id, widget, ct) => Task.FromResult<Widget>(null!); // always "not found"
    }
}

/// <summary>Profile for testing bound function with Guid parameter (H5).</summary>
internal class GuidFunctionProfile : EntitySetProfile<int, Widget>
{
    public GuidFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "GuidFnWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
        BindFunction(EchoGuid);
    }

    private Task<string> EchoGuid(Guid id) => Task.FromResult(id.ToString());
}

/// <summary>Profile for testing void Task bound action returns 204 (H2).</summary>
internal class VoidActionProfile : EntitySetProfile<int, Widget>
{
    public bool WasCalled { get; private set; }

    public VoidActionProfile() : base(x => x.Id)
    {
        EntitySetName = "VoidActionWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
        BindAction(DoNothing);
    }

    private Task DoNothing() { WasCalled = true; return Task.CompletedTask; }
}

/// <summary>Profile for testing GetQueryable MaxTop enforcement (C3).</summary>
internal class MaxTopProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = Enumerable.Range(1, 20)
        .Select(i => new Widget { Id = i, Name = $"W{i}" }).ToList();

    public MaxTopProfile() : base(x => x.Id)
    {
        EntitySetName = "MaxTopWidgets";
        MaxTop = 5; // per-profile cap
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

/// <summary>Leg 1 (docs-fidelity): GetAll profile with a MaxTop cap, for $top/$skip tests.</summary>
internal class GetAllMaxTopProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = Enumerable.Range(1, 20)
        .Select(i => new Widget { Id = i, Name = $"W{i}" }).ToList();

    public GetAllMaxTopProfile() : base(x => x.Id)
    {
        EntitySetName = "GetAllMaxTopWidgets";
        MaxTop = 5; // per-profile cap
        CountEnabled = true;
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
    }
}

/// <summary>Profile for testing role-based authorization (M9 — IReadOnlyList Roles path).</summary>
internal class RoleAuthProfile : EntitySetProfile<int, Widget>
{
    public RoleAuthProfile() : base(x => x.Id)
    {
        EntitySetName = "RoleWidgets";
        RequireRoles("Admin");
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
    }
}

/// <summary>Profile for testing GetAll returning null (H1 — null-safe).</summary>
internal class NullGetAllProfile : EntitySetProfile<int, Widget>
{
    public NullGetAllProfile() : base(x => x.Id)
    {
        EntitySetName = "NullGetAllWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(null!);
    }
}

/// <summary>Profile with decimal key for key parser testing (H3).</summary>
internal class DecimalItem { public decimal Id { get; set; } public string Name { get; set; } = ""; }
internal class DecimalKeyProfile : EntitySetProfile<decimal, DecimalItem>
{
    private static readonly List<DecimalItem> _store = new() { new() { Id = 1.5m, Name = "Half" } };
    public DecimalKeyProfile() : base(x => x.Id)
    {
        EntitySetName = "DecimalItems";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>Profile for testing multi-registration in a single host (test gap).</summary>
internal class SecondProfile : EntitySetProfile<int, Widget>
{
    public SecondProfile() : base(x => x.Id)
    {
        EntitySetName = "SecondWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(new[] { new Widget { Id = 99, Name = "Second" } });
    }
}

/// <summary>Profile with DateTimeOffset key for key parser testing (H3 revisit).</summary>
internal class DateTimeOffsetItem { public DateTimeOffset Id { get; set; } public string Name { get; set; } = ""; }
internal class DateTimeOffsetKeyProfile : EntitySetProfile<DateTimeOffset, DateTimeOffsetItem>
{
    private static readonly DateTimeOffset _key = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly List<DateTimeOffsetItem> _store = new() { new() { Id = _key, Name = "Item" } };
    public DateTimeOffsetKeyProfile() : base(x => x.Id)
    {
        EntitySetName = "DateTimeOffsetItems";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>Profile with DateTime key for key parser testing (H3 revisit).</summary>
internal class DateTimeItem { public DateTime Id { get; set; } public string Name { get; set; } = ""; }
internal class DateTimeKeyProfile : EntitySetProfile<DateTime, DateTimeItem>
{
    private static readonly DateTime _key = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly List<DateTimeItem> _store = new() { new() { Id = _key, Name = "Item" } };
    public DateTimeKeyProfile() : base(x => x.Id)
    {
        EntitySetName = "DateTimeItems";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>Profile with DateOnly key for key parser testing (H3 revisit).</summary>
internal class DateOnlyItem { public DateOnly Id { get; set; } public string Name { get; set; } = ""; }
internal class DateOnlyKeyProfile : EntitySetProfile<DateOnly, DateOnlyItem>
{
    private static readonly DateOnly _key = new DateOnly(2024, 3, 20);
    private static readonly List<DateOnlyItem> _store = new() { new() { Id = _key, Name = "Item" } };
    public DateOnlyKeyProfile() : base(x => x.Id)
    {
        EntitySetName = "DateOnlyItems";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>Profile with both policy AND roles — verifies auth is applied additively to all route types.</summary>
internal class PolicyAndRolesWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public PolicyAndRolesWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "PolicyRoleWidgets";
        RequireAuthorization("TestPolicy");
        RequireRoles("Admin");

        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) => { _store.Add(widget); return Task.FromResult<Widget?>(widget); };
    }
}

/// <summary>Profile for testing DELETE non-idempotent behavior (returns 404 when not found).</summary>
internal class NonIdempotentDeleteProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public NonIdempotentDeleteProfile() : base(x => x.Id)
    {
        EntitySetName = "NonIdempotentWidgets";
        IdempotentDelete = false;
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);
    }
}

/// <summary>Profile for testing entity-level bound functions and actions (Gap 7).</summary>
internal class EntityBoundOpsStore
{
    public List<Widget> Items { get; } = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };
}

internal class EntityBoundOpsProfile : EntitySetProfile<int, Widget>
{
    private readonly EntityBoundOpsStore _store;

    public EntityBoundOpsProfile(EntityBoundOpsStore store) : base(x => x.Id)
    {
        _store = store;
        EntitySetName = "EntityBoundWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store.Items);
        GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(w => w.Id == id));

        BindEntityFunction(GetNameForKey);
        BindEntityAction(RenameWidget);
    }

    // Entity-level function: GET /EntityBoundWidgets(1)/GetNameForKey
    private Task<string> GetNameForKey(int key)
    {
        var widget = _store.Items.FirstOrDefault(w => w.Id == key);
        return Task.FromResult(widget?.Name ?? "");
    }

    // Entity-level action: POST /EntityBoundWidgets(1)/RenameWidget { "newName": "..." }
    private Task RenameWidget(int key, string newName)
    {
        var widget = _store.Items.FirstOrDefault(w => w.Id == key);
        if (widget is not null) widget.Name = newName;
        return Task.CompletedTask;
    }
}

/// <summary>Profile for testing $expand data loading (Gap 8) using the GetQueryable path.</summary>
internal class ExpandableParentProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new() { new() { Id = 1, Name = "Parent1" } };
    private static readonly List<Child> _children = new() { new() { Id = 1, ParentId = 1, Name = "Child1" } };

    public ExpandableParentProfile() : base(x => x.Id)
    {
        EntitySetName = "ExpandableParents";
        ExpandEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId)));
    }
}

// ── Batch 2 gap fixtures ──────────────────────────────────────────────────────

/// <summary>Profile for testing @odata.etag in response body (Gap 2, batch 2).</summary>
internal class ETagBodyProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public ETagBodyProfile() : base(x => x.Id)
    {
        EntitySetName = "ETagBodyWidgets";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };
        Put = (id, widget, ct) =>
        {
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
        UseETag(x => x.Name);
    }
}

/// <summary>Profile for testing upsert via PUT (Gap 3, batch 2).</summary>
internal class UpsertProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Existing" } };

    public UpsertProfile() : base(x => x.Id)
    {
        EntitySetName = "UpsertWidgets";
        AllowUpsert = true;

        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = widget.Id == 0 ? (_store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1) : widget.Id;
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };
        Put = (id, widget, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget>(null!); // signal "not found" → upsert
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };
    }
}

/// <summary>Profile for testing $search query option (Gap 4, batch 2).</summary>
internal class SearchableWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
        new() { Id = 3, Name = "Gamma" },
    };

    public SearchableWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "SearchableWidgets";
        CountEnabled = true;
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        Search = (term, ct) =>
            Task.FromResult<IEnumerable<Widget>>(
                _store.Where(w => w.Name.Contains(term, System.StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Profile for testing $search rejection when no handler (Gap 4, batch 2).</summary>
internal class NoSearchProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Alpha" } };

    public NoSearchProfile() : base(x => x.Id)
    {
        EntitySetName = "NoSearchWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
    }
}

/// <summary>Profile for testing navigation query options + $ref (Gap 5 + Gap 6, batch 2).</summary>
internal class NavQueryProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new() { new() { Id = 1, Name = "Parent1" } };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "Child1" },
        new() { Id = 2, ParentId = 1, Name = "Child2" },
        new() { Id = 3, ParentId = 1, Name = "Child3" },
    };
    private static readonly List<(int parentId, string relatedId)> _refs = new();

    public NavQueryProfile() : base(x => x.Id)
    {
        EntitySetName = "NavQueryParents";
        GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId)),
            addRef: (parentId, relatedId, ct) =>
            {
                _refs.Add((parentId, relatedId));
                return Task.CompletedTask;
            },
            removeRef: (parentId, relatedId, ct) =>
            {
                _refs.RemoveAll(r => r.parentId == parentId && r.relatedId == relatedId);
                return Task.CompletedTask;
            });
    }
}

internal class NavOrderChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}

internal class NavOrderParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<NavOrderChild>? Children { get; set; }
}

/// <summary>Profile for testing M-3: $orderby on navigation collection routes.</summary>
internal class NavOrderByProfile : EntitySetProfile<int, NavOrderParent>
{
    private static readonly List<NavOrderParent> _parents = new() { new() { Id = 1, Name = "Parent1" } };

    // Deliberately unsorted, with duplicate Category values so a multi-key
    // "$orderby=Category asc,Name desc" has a meaningful tie-break to exercise.
    private static readonly List<NavOrderChild> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "Charlie", Category = "B" },
        new() { Id = 2, ParentId = 1, Name = "Alpha",   Category = "A" },
        new() { Id = 3, ParentId = 1, Name = "Bravo",   Category = "B" },
        new() { Id = 4, ParentId = 1, Name = "Delta",   Category = "A" },
    };

    public NavOrderByProfile() : base(x => x.Id)
    {
        EntitySetName = "NavOrderByParents";
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<NavOrderChild>>(_children.Where(c => c.ParentId == parentId)));
    }
}

/// <summary>Profile for testing $expand on the GetAll path (Gap 8, batch 2).</summary>
internal class ExpandableGetAllProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "ParentA" },
        new() { Id = 2, Name = "ParentB" },
    };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "Child1" },
        new() { Id = 2, ParentId = 2, Name = "Child2" },
    };

    public ExpandableGetAllProfile() : base(x => x.Id)
    {
        EntitySetName = "ExpandableGetAllParents";
        ExpandEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(_parents);

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId)));
    }
}

/// <summary>Profile for testing @odata.context on function results (Gap 1, batch 2).</summary>
internal class ContextFunctionProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };

    public ContextFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "ContextFnWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        BindFunction(GetFirst);
        BindFunction(GetAll2);
    }

    // Returns single TModel — should get context=..#EntitySet/$entity
    private Task<Widget?> GetFirst() => Task.FromResult<Widget?>(_store.FirstOrDefault());

    // Returns IEnumerable<TModel> — should get context=..#EntitySet and value array
    private Task<IEnumerable<Widget>> GetAll2() => Task.FromResult<IEnumerable<Widget>>(_store);
}

// ── Batch 3 fixtures ─────────────────────────────────────────────────────────

/// <summary>
/// Profile that extends <see cref="ODataEntitySetProfile{TKey,TModel}"/> and sets
/// <c>GetODataQueryable</c> so the Priority-1 handler code path is exercised.
/// The profile receives <see cref="ODataQueryOptions{TModel}"/> directly and applies
/// them to the in-memory list — demonstrating full ODataQueryOptions pushdown.
/// </summary>
internal class ODataWidgetProfile : ODataEntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
        new() { Id = 3, Name = "Bolt" },
    };

    public ODataWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "ODataWidgets";
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        SelectEnabled = true;

        // Priority-1 handler: profile applies ODataQueryOptions itself.
        GetODataQueryable = (options, ct) =>
        {
            var q = _store.AsQueryable();
            // Apply filter/orderby/skip/top via the options object.
            var applied = options.ApplyTo(q) as IQueryable<Widget> ?? q;
            return System.Threading.Tasks.Task.FromResult(new ODataQueryResult<Widget> { Items = applied });
        };

        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };

        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);
    }
}

/// <summary>
/// Profile that uses <c>Patch</c> with <see cref="Delta{T}"/> for partial-update semantics.
/// </summary>
internal class DeltaPatchWidgetProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
    };

    public DeltaPatchWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "DeltaWidgets";

        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

        // Delta-aware partial update: only the properties present in the request body are changed.
        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
    }
}

/// <summary>
/// Navigation profile for Batch 3 nav/$count and nav/$select tests.
/// Uses the existing Parent/Child entities.
/// </summary>
internal class NavCountProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "Parent1" },
        new() { Id = 2, Name = "Parent2" },
    };

    private static readonly List<Child> _children = new()
    {
        new() { Id = 10, ParentId = 1, Name = "ChildA" },
        new() { Id = 11, ParentId = 1, Name = "ChildB" },
        new() { Id = 20, ParentId = 2, Name = "ChildC" },
    };

    public NavCountProfile() : base(x => x.Id)
    {
        EntitySetName = "NavCountParents";
        GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(w => w.Id == id));

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<Child>>(
                    _children.Where(c => c.ParentId == parentId).ToList()));
    }
}

// ── Batch 4 fixtures ─────────────────────────────────────────────────────────

/// <summary>
/// Profile with ETag and GetAll so tests can verify @odata.etag appears in collection responses.
/// </summary>
internal class ETagCollectionProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
    };

    public ETagCollectionProfile() : base(x => x.Id)
    {
        EntitySetName = "ETagCollWidgets";
        SelectEnabled = true;
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        UseETag(x => x.Name);
    }
}

/// <summary>
/// Profile with GetQueryable and MaxTop for Prefer: maxpagesize tests.
/// </summary>
internal class MaxPageSizeProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = Enumerable.Range(1, 20)
        .Select(i => new Widget { Id = i, Name = $"Widget{i}" })
        .ToList();

    public MaxPageSizeProfile() : base(x => x.Id)
    {
        EntitySetName = "MaxPageWidgets";
        FilterEnabled = true;
        OrderByEnabled = true;
        GetQueryable = (ct) => Task.FromResult<IQueryable<Widget>>(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
    }
}

/// <summary>
/// Profile that sets source.HasETag for If-Match list parsing tests.
/// </summary>
internal class ETagIfMatchProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

    public ETagIfMatchProfile() : base(x => x.Id)
    {
        EntitySetName = "IfMatchWidgets";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Put = (id, widget, ct) =>
        {
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };
        UseETag(x => x.Name);
    }
}

// -- Partial-update (delta semantics) test fixtures ----------------------------

/// <summary>Three-field entity used to verify that PATCH preserves fields not in the request body.</summary>
internal class PatchItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

/// <summary>
/// Profile for testing <c>GET /{EntitySet}({key})/{nav}/$ref</c> with populated <c>@odata.id</c>
/// references when <c>refTargetEntitySet</c> is configured (M-2).
/// </summary>
internal class NavRefProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new() { new() { Id = 1, Name = "Parent1" } };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 10, ParentId = 1, Name = "ChildA" },
        new() { Id = 11, ParentId = 1, Name = "ChildB" },
    };

    public NavRefProfile() : base(x => x.Id)
    {
        EntitySetName = "NavRefParents";
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId)),
            refTargetEntitySet: "Children");
    }
}

/// <summary>
/// Profile for testing <c>GET /{EntitySet}({key})/{nav}/$ref</c> on a single-valued
/// navigation with populated <c>@odata.id</c> when <c>refTargetEntitySet</c> is configured
/// (V3, OData §11.4.6.1).
/// </summary>
internal class NavRefSingleProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "Parent1", PrimaryChild = new Child { Id = 42, ParentId = 1, Name = "OnlyChild" } },
    };

    public NavRefSingleProfile() : base(x => x.Id)
    {
        EntitySetName = "NavRefSingleParents";
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasOptional(
            navigation: x => x.PrimaryChild!,
            get: (parentId, ct) =>
                Task.FromResult(_parents.FirstOrDefault(p => p.Id == parentId)?.PrimaryChild),
            refTargetEntitySet: "Children");
    }
}

/// <summary>
/// Profile that demonstrates the new Patch delta semantics: the handler receives the
/// pre-fetched entity with only request-body fields overwritten. No manual merge needed.
/// </summary>
internal class PatchItemStore
{
    public List<PatchItem> Items { get; } = new()
    {
        new() { Id = 1, Name = "Widget", Price = 9.99m },
        new() { Id = 2, Name = "Gadget", Price = 19.99m },
    };
}

internal class PatchItemProfile : EntitySetProfile<int, PatchItem>
{
    private readonly PatchItemStore _store;

    public PatchItemProfile(PatchItemStore store) : base(x => x.Id)
    {
        _store = store;
        EntitySetName = "PatchItems";

        GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(x => x.Id == id));

        Patch = (id, delta, ct) =>
        {
            var existing = _store.Items.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<PatchItem?>(null);
            delta.Patch(existing);
            return Task.FromResult<PatchItem?>(existing);
        };
    }
}

// ── Coverage-gap fixtures ─────────────────────────────────────────────────────

/// <summary>
/// H-1: Profile using GetODataQueryable that returns ODataQueryResult with a
/// pre-set TotalCount (10) but only 2 items. Used to verify that $count=true
/// uses TotalCount from the result rather than the item count.
/// </summary>
internal class ODataTotalCountProfile : ODataEntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };

    public ODataTotalCountProfile() : base(x => x.Id)
    {
        EntitySetName = "TotalCountWidgets";
        CountEnabled = true;

        // Returns only 2 items but advertises TotalCount = 10 (simulating a pre-paged query)
        GetODataQueryable = (options, ct) =>
            System.Threading.Tasks.Task.FromResult(new ODataQueryResult<Widget>
            {
                Items = _store.AsQueryable(),
                TotalCount = 10,
            });
    }
}

/// <summary>
/// M-4: Profile with GetQueryable + ETag + Expand + Select — all three pipeline
/// features enabled simultaneously. Used to verify the unified JSON pipeline.
/// </summary>
internal class ETagExpandSelectProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "P1" },
        new() { Id = 2, Name = "P2" },
    };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "C1" },
        new() { Id = 2, ParentId = 2, Name = "C2" },
    };

    public ETagExpandSelectProfile() : base(x => x.Id)
    {
        EntitySetName = "ETagExpandSelectParents";
        SelectEnabled = true;
        ExpandEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId)));

        UseETag(x => x.Name);
    }
}

// ── Batch-aware $expand fixtures (REVIEW.md M-1) ──────────────────────────────

/// <summary>
/// Shared mutable call-counting state for batch-expand fixtures, registered as a DI
/// singleton so tests can observe how many times a batch loader was invoked across a
/// request, independent of the per-request scoped profile instance.
/// </summary>
internal class BatchCallCounter
{
    public int ChildrenCalls;
    public int PrimaryChildCalls;
    public readonly List<int> ChildrenKeyCounts = new();
    public readonly List<int> PrimaryChildKeyCounts = new();
}

/// <summary>
/// Profile for testing batch-aware <c>$expand</c> (M-1) on the <c>GetQueryable</c> path.
/// 100 parents, two batch-loaded nav properties (<c>Children</c>, a collection nav via
/// <c>HasMany</c>; <c>PrimaryChild</c>, a single-valued nav via <c>HasOptional</c>).
/// Parent 1 has no children (lookup miss → []). Parent 2 has no primary child (map miss → null).
/// </summary>
internal class BatchExpandQueryableProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = BuildParents();
    private static readonly List<Child> _children = BuildChildren();

    private static List<Parent> BuildParents()
    {
        var parents = new List<Parent>();
        for (int i = 1; i <= 100; i++)
        {
            parents.Add(new Parent { Id = i, Name = $"P{i}" });
        }
        return parents;
    }

    private static List<Child> BuildChildren()
    {
        var children = new List<Child>();
        // Parent 1 intentionally has zero children (lookup-miss coverage).
        for (int i = 2; i <= 100; i++)
        {
            children.Add(new Child { Id = i * 10 + 1, ParentId = i, Name = $"C{i}-1" });
            children.Add(new Child { Id = i * 10 + 2, ParentId = i, Name = $"C{i}-2" });
        }
        return children;
    }

    public BatchExpandQueryableProfile(BatchCallCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BatchExpandParents";
        ExpandEnabled = true;
        SelectEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!, batchGetAll: (ids, ct) =>
        {
            counter.ChildrenCalls++;
            counter.ChildrenKeyCounts.Add(ids.Count);
            ILookup<int, Child> lookup = _children.Where(c => ids.Contains(c.ParentId)).ToLookup(c => c.ParentId);
            return Task.FromResult(lookup);
        });

        HasOptional(x => x.PrimaryChild!, batchGet: (ids, ct) =>
        {
            counter.PrimaryChildCalls++;
            counter.PrimaryChildKeyCounts.Add(ids.Count);
            // Parent 2 is intentionally absent from the result (map-miss coverage);
            // every other requested parent gets a primary child derived from its id.
            IReadOnlyDictionary<int, Child?> map = ids
                .Where(id => id != 2)
                .ToDictionary(id => id, id => (Child?)new Child { Id = id * 100, ParentId = id, Name = $"Primary{id}" });
            return Task.FromResult(map);
        });
    }
}

/// <summary>
/// Profile for testing batch-aware <c>$expand</c> (M-1) on the <c>GetAll</c> path — proves the
/// batch collection call-count fix applies regardless of which collection GET pipeline runs.
/// </summary>
internal class BatchExpandGetAllProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "GA1" },
        new() { Id = 2, Name = "GA2" },
        new() { Id = 3, Name = "GA3" },
    };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "GC1" },
        new() { Id = 2, ParentId = 1, Name = "GC2" },
        new() { Id = 3, ParentId = 3, Name = "GC3" },
        // Parent 2 has no children — lookup miss.
    };

    public BatchExpandGetAllProfile(BatchCallCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BatchExpandGetAllParents";
        ExpandEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(_parents);

        HasMany(x => x.Children!, batchGetAll: (ids, ct) =>
        {
            counter.ChildrenCalls++;
            counter.ChildrenKeyCounts.Add(ids.Count);
            ILookup<int, Child> lookup = _children.Where(c => ids.Contains(c.ParentId)).ToLookup(c => c.ParentId);
            return Task.FromResult(lookup);
        });
    }
}

/// <summary>
/// Profile for testing batch-aware <c>$expand</c> (M-1) on the Priority-1
/// <see cref="ODataEntitySetProfile{TKey,TModel}"/> path (<c>GetODataQueryable</c>).
/// </summary>
internal class BatchExpandODataProfile : ODataEntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "OD1" },
        new() { Id = 2, Name = "OD2" },
    };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "ODC1" },
        new() { Id = 2, ParentId = 2, Name = "ODC2" },
    };

    public BatchExpandODataProfile(BatchCallCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BatchExpandODataParents";
        ExpandEnabled = true;

        GetODataQueryable = (options, ct) =>
        {
            var q = _parents.AsQueryable();
            var applied = options.ApplyTo(q) as IQueryable<Parent> ?? q;
            return Task.FromResult(new ODataQueryResult<Parent> { Items = applied });
        };
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!, batchGetAll: (ids, ct) =>
        {
            counter.ChildrenCalls++;
            counter.ChildrenKeyCounts.Add(ids.Count);
            ILookup<int, Child> lookup = _children.Where(c => ids.Contains(c.ParentId)).ToLookup(c => c.ParentId);
            return Task.FromResult(lookup);
        });
    }
}

/// <summary>
/// Profile that mixes a batch-loaded nav (<c>Children</c>) with a per-entity nav
/// (<c>PrimaryChild</c>) so a single <c>$expand=Children,PrimaryChild</c> request exercises
/// both code paths in the same pipeline pass.
/// </summary>
internal class MixedBatchExpandProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "M1", PrimaryChild = new Child { Id = 101, ParentId = 1, Name = "MPC1" } },
        new() { Id = 2, Name = "M2", PrimaryChild = new Child { Id = 102, ParentId = 2, Name = "MPC2" } },
        new() { Id = 3, Name = "M3", PrimaryChild = new Child { Id = 103, ParentId = 3, Name = "MPC3" } },
    };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "MC1" },
        new() { Id = 2, ParentId = 2, Name = "MC2" },
    };

    public MixedBatchExpandProfile(BatchCallCounter counter) : base(x => x.Id)
    {
        EntitySetName = "MixedBatchExpandParents";
        ExpandEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        // Batch path.
        HasMany(x => x.Children!, batchGetAll: (ids, ct) =>
        {
            counter.ChildrenCalls++;
            counter.ChildrenKeyCounts.Add(ids.Count);
            ILookup<int, Child> lookup = _children.Where(c => ids.Contains(c.ParentId)).ToLookup(c => c.ParentId);
            return Task.FromResult(lookup);
        });

        // Deliberately per-entity (non-batch) path.
        HasOptional(x => x.PrimaryChild!, get: (parentId, ct) =>
        {
            counter.PrimaryChildCalls++;
            return Task.FromResult(_parents.FirstOrDefault(p => p.Id == parentId)?.PrimaryChild);
        }, refTargetEntitySet: null);
    }
}

/// <summary>
/// Profile that registers navigation properties using ONLY the batch overloads (no per-entity
/// <c>get</c>/<c>getAll</c> handler). Proves the auto-derived <see cref="NavigationRouteDefinition.Handler"/>
/// keeps the standalone <c>GET /{Set}({key})/{nav}</c> route, nav <c>$count</c>, and <c>$ref</c>
/// working without the developer writing a second handler.
/// </summary>
internal class BatchOnlyNavProfile : EntitySetProfile<int, Parent>
{
    private static readonly List<Parent> _parents = new()
    {
        new() { Id = 1, Name = "BO1" },
        new() { Id = 2, Name = "BO2" },
    };
    private static readonly List<Child> _children = new()
    {
        new() { Id = 10, ParentId = 1, Name = "BOC1" },
        new() { Id = 11, ParentId = 1, Name = "BOC2" },
    };

    public BatchOnlyNavProfile() : base(x => x.Id)
    {
        EntitySetName = "BatchOnlyParents";
        ExpandEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!, batchGetAll: (ids, ct) =>
            Task.FromResult(_children.Where(c => ids.Contains(c.ParentId)).ToLookup(c => c.ParentId)));

        HasOptional(
            navigation: x => x.PrimaryChild!,
            batchGet: (ids, ct) =>
            {
                IReadOnlyDictionary<int, Child?> map = _parents
                    .Where(p => ids.Contains(p.Id))
                    .ToDictionary(p => p.Id, p => (Child?)_children.FirstOrDefault(c => c.ParentId == p.Id));
                return Task.FromResult(map);
            },
            refTargetEntitySet: "Children");
    }
}

// ── Property-access (I-6) fixtures ─────────────────────────────────────────────

/// <summary>Complex (non-primitive, non-navigation) sub-object used to test /$value's 400-on-complex path.</summary>
internal class Dimensions
{
    public decimal Width { get; set; }
    public decimal Height { get; set; }
}

internal class PropertyAccessItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public DateTime ReleasedAt { get; set; }
    public Dimensions? Size { get; set; }
}

/// <summary>
/// Profile with GetById and a variety of structural property types (string, nullable string,
/// decimal, DateTime, complex) — used to exercise individual property-read routes (I-6).
/// PropertyAccessEnabled is left at its default (inherits true from EntitySetDefaults).
/// </summary>
internal class PropertyAccessProfile : EntitySetProfile<int, PropertyAccessItem>
{
    internal static readonly List<PropertyAccessItem> Store = new()
    {
        new()
        {
            Id = 1,
            Name = "Widget",
            Description = "A test widget",
            Price = 9.99m,
            ReleasedAt = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            Size = new Dimensions { Width = 10.5m, Height = 3.2m },
        },
        new()
        {
            Id = 2,
            Name = "Gadget",
            Description = null,
            Price = 19.99m,
            ReleasedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = null,
        },
    };

    public PropertyAccessProfile() : base(x => x.Id)
    {
        EntitySetName = "PropertyAccessItems";
        GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>Same shape as <see cref="PropertyAccessProfile"/> but with property access opted out.</summary>
internal class PropertyAccessDisabledProfile : EntitySetProfile<int, PropertyAccessItem>
{
    public PropertyAccessDisabledProfile() : base(x => x.Id)
    {
        EntitySetName = "PropertyAccessDisabledItems";
        PropertyAccessEnabled = false;
        GetById = (id, ct) => Task.FromResult<PropertyAccessItem?>(
            new PropertyAccessItem { Id = 1, Name = "X" });
    }
}

/// <summary>
/// Profile whose entity-level bound function is deliberately named the same as a structural
/// property ("Name") on <see cref="Widget"/> — both would claim
/// <c>GET /PropertyCollisionWidgets({key})/Name</c>. Used to test the startup route-collision
/// validation (design §6): <c>app.MapOhData()</c> must throw <see cref="InvalidOperationException"/>.
/// </summary>
internal class PropertyCollisionProfile : EntitySetProfile<int, Widget>
{
    public PropertyCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "PropertyCollisionWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(new Widget { Id = 1, Name = "X" });
        BindEntityFunction(Name);
    }

    // Method name "Name" intentionally collides with the Widget.Name structural property.
    private Task<string> Name(int key) => Task.FromResult("collision");
}

/// <summary>
/// Profile whose entity-bound function has zero parameters (besides the trailing
/// CancellationToken), so there is nowhere to place the entity key. Used to test the S6
/// startup validation: <c>BindEntityFunction</c> must throw <see cref="InvalidOperationException"/>
/// rather than registering a route that fails with <c>IndexOutOfRangeException</c> at request time.
/// </summary>
internal class ZeroParamEntityFunctionProfile : EntitySetProfile<int, Widget>
{
    public ZeroParamEntityFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "ZeroParamFnWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(new Widget { Id = 1, Name = "X" });
        BindEntityFunction(NoParams);
    }

    private Task<string> NoParams() => Task.FromResult("oops");
}

/// <summary>
/// Profile whose entity-bound action has zero parameters. Same S6 validation as
/// <see cref="ZeroParamEntityFunctionProfile"/>, for <c>BindEntityAction</c>.
/// </summary>
internal class ZeroParamEntityActionProfile : EntitySetProfile<int, Widget>
{
    public ZeroParamEntityActionProfile() : base(x => x.Id)
    {
        EntitySetName = "ZeroParamActionWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(new Widget { Id = 1, Name = "X" });
        BindEntityAction(NoParams);
    }

    private Task NoParams() => Task.CompletedTask;
}

/// <summary>
/// Profile whose entity-bound function's first parameter is the wrong type (<c>string</c>
/// instead of the entity's <c>int</c> key). Used to test the S6 startup validation: the framework
/// places the parsed route key into <c>args[0]</c> assuming it matches <c>TKey</c>, and
/// <c>BindEntityFunction</c> must reject a mismatched first parameter at bind time.
/// </summary>
internal class WrongKeyTypeEntityFunctionProfile : EntitySetProfile<int, Widget>
{
    public WrongKeyTypeEntityFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "WrongKeyTypeFnWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(new Widget { Id = 1, Name = "X" });
        BindEntityFunction(BadFirstParam);
    }

    // First parameter should be 'int' (TKey) but is 'string'.
    private Task<string> BadFirstParam(string notTheKey) => Task.FromResult(notTheKey);
}


// ── #176: un-expanded navigation omission fixtures ────────────────────────────

internal class OmitActor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Navigation target with its OWN navigation (<c>Movies</c>). When a movie's <c>Studio</c> is
/// $expand'd, the studio must not carry this un-expanded back-reference (issue #176, face 3).
/// </summary>
internal class OmitStudio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<OmitMovie>? Movies { get; set; }
}

internal class OmitMovie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public OmitStudio? Studio { get; set; }            // single-valued nav (face 2)
    public IEnumerable<OmitActor>? Cast { get; set; }  // collection nav (face 1)
}

/// <summary>
/// #176 regression fixture. Every movie carries a fully populated <c>Studio</c> and <c>Cast</c>
/// on the CLR object, so without the fix both navigations would serialise inline into every read
/// response. The expanded studio itself carries <c>Movies</c>, exercising the nested-leak face.
/// </summary>
internal class OmitNavMovieProfile : EntitySetProfile<int, OmitMovie>
{
    private static OmitStudio MakeStudio() => new()
    {
        Id = 7,
        Name = "Skyline",
        Movies = new List<OmitMovie> { new() { Id = 1, Title = "Ascent" } },
    };

    private static readonly List<OmitMovie> _movies = new()
    {
        new()
        {
            Id = 1,
            Title = "Ascent",
            Studio = MakeStudio(),
            Cast = new List<OmitActor> { new() { Id = 100, Name = "Ada" }, new() { Id = 101, Name = "Ben" } },
        },
        new()
        {
            Id = 2,
            Title = "Ballad",
            Studio = MakeStudio(),
            Cast = new List<OmitActor>(), // empty collection → would leak as [] without the fix
        },
        new()
        {
            Id = 3,
            Title = "Crest",
            Studio = null, // no related studio → $expand=Studio yields "studio": null
            Cast = new List<OmitActor>(),
        },
    };

    public OmitNavMovieProfile() : base(x => x.Id)
    {
        EntitySetName = "OmitNavMovies";
        ExpandEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<OmitMovie>>(_movies);
        GetById = (id, ct) => Task.FromResult(_movies.FirstOrDefault(m => m.Id == id));

        HasOptional(
            navigation: x => x.Studio!,
            get: (movieId, ct) => Task.FromResult(_movies.FirstOrDefault(m => m.Id == movieId)?.Studio),
            refTargetEntitySet: null);

        HasMany(
            navigation: x => x.Cast!,
            getAll: (movieId, ct) => Task.FromResult<IEnumerable<OmitActor>>(
                _movies.FirstOrDefault(m => m.Id == movieId)?.Cast ?? Enumerable.Empty<OmitActor>()));
    }
}


// ── #179: omission on nav-route and bound-op read paths ───────────────────────

/// <summary>
/// Navigation element/target type that carries its OWN navigation (<c>Films</c>). #176 only wired
/// the omission into the top-level reads; #179 extends it to single-valued nav GET, nav-collection
/// GET, and bound-operation results. On any of those, this back-reference must be omitted (OData
/// JSON §4.5.1 / §11.2.4.2) so an entity's shape never depends on which route returned it.
/// </summary>
internal class NavLeakStudio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<NavLeakFilm>? Films { get; set; } // own nav — would leak on nav-route reads
}

internal class NavLeakFilm
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public NavLeakStudio? Studio { get; set; }               // single-valued nav (target has own nav)
    public IEnumerable<NavLeakStudio>? CoStudios { get; set; } // collection nav (element has own nav)
}

/// <summary>
/// #179 regression fixture. Every film carries a fully populated <c>Studio</c> and <c>CoStudios</c>,
/// and every studio carries a populated <c>Films</c> back-reference, so without the fix these
/// navigations would serialise inline on the single-valued nav GET, the nav-collection GET, and the
/// bound-operation results. <c>UseETag</c> is set so the bound-op paths are also asserted to inject
/// <c>@odata.etag</c>, matching the normal collection/GetById paths.
///
/// Both bound operations that return the set's own entity type are FUNCTIONS: Microsoft.OData's
/// <c>ActionConfiguration.Returns&lt;T&gt;</c> rejects an entity return type ("Use ReturnsFromEntitySet"),
/// so a bound action can't declare one in the EDM. The single- and collection-of-TModel branches of
/// <c>WrapBoundOpResult</c> are the same code regardless of function-vs-action caller, so the function
/// coverage exercises them fully.
/// </summary>
internal class NavLeakFilmProfile : EntitySetProfile<int, NavLeakFilm>
{
    private static NavLeakStudio MakeStudio(int id, string name) => new()
    {
        Id = id,
        Name = name,
        // Populated back-reference: would leak as a nested "films" array without the #179 fix.
        Films = new List<NavLeakFilm> { new() { Id = 1, Title = "Ascent" } },
    };

    private static readonly List<NavLeakFilm> _films = new()
    {
        new()
        {
            Id = 1,
            Title = "Ascent",
            Studio = MakeStudio(7, "Skyline"),
            CoStudios = new List<NavLeakStudio> { MakeStudio(8, "Harbor"), MakeStudio(9, "Vista") },
        },
        new()
        {
            Id = 2,
            Title = "Ballad",
            Studio = MakeStudio(7, "Skyline"),
            CoStudios = new List<NavLeakStudio> { MakeStudio(8, "Harbor") },
        },
    };

    public NavLeakFilmProfile() : base(x => x.Id)
    {
        EntitySetName = "NavLeakFilms";
        UseETag(x => x.Title);

        GetAll = (ct) => Task.FromResult<IEnumerable<NavLeakFilm>>(_films);
        GetById = (id, ct) => Task.FromResult(_films.FirstOrDefault(f => f.Id == id));

        HasOptional(
            navigation: x => x.Studio!,
            get: (filmId, ct) => Task.FromResult(_films.FirstOrDefault(f => f.Id == filmId)?.Studio),
            refTargetEntitySet: null);

        HasMany(
            navigation: x => x.CoStudios!,
            getAll: (filmId, ct) => Task.FromResult<IEnumerable<NavLeakStudio>>(
                _films.FirstOrDefault(f => f.Id == filmId)?.CoStudios ?? Enumerable.Empty<NavLeakStudio>()));

        BindFunction(TopRated);    // collection of TModel — bound-op collection path
        BindFunction(GetFeatured); // single TModel — bound-op single path
    }

    // Bound function returning a collection of the set's own type.
    private Task<IEnumerable<NavLeakFilm>> TopRated() => Task.FromResult<IEnumerable<NavLeakFilm>>(_films);

    // Bound function returning a single entity of the set's own type.
    private Task<NavLeakFilm?> GetFeatured() => Task.FromResult(_films.FirstOrDefault());
}


// ── #184: [JsonPropertyName]-renamed navigation omission/expand fixtures ───────

internal class RenamedNavActor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal class RenamedNavStudio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// #184 fixture. Both navigations carry a per-property <c>[JsonPropertyName]</c> rename, so
/// System.Text.Json serialises them under keys ("starring", "producedBy") that the naming policy
/// would never produce from the CLR names ("Cast", "Studio"). Before the fix, omission keyed off
/// the policy-converted name and so left the renamed nav leaking inline, while <c>$expand</c> wrote
/// a second, differently-cased key. The EDM (and hence <c>$expand</c>) still uses the CLR property
/// name; only the JSON key is renamed.
/// </summary>
internal class RenamedNavMovie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("starring")]
    public IEnumerable<RenamedNavActor>? Cast { get; set; } // collection nav, JSON key "starring"

    [System.Text.Json.Serialization.JsonPropertyName("producedBy")]
    public RenamedNavStudio? Studio { get; set; }           // single nav, JSON key "producedBy"
}

internal class RenamedNavMovieProfile : EntitySetProfile<int, RenamedNavMovie>
{
    private static readonly List<RenamedNavMovie> _movies = new()
    {
        new()
        {
            Id = 1,
            Title = "Ascent",
            Studio = new RenamedNavStudio { Id = 7, Name = "Skyline" },
            Cast = new List<RenamedNavActor> { new() { Id = 100, Name = "Ada" }, new() { Id = 101, Name = "Ben" } },
        },
        new()
        {
            Id = 2,
            Title = "Ballad",
            Studio = new RenamedNavStudio { Id = 8, Name = "Harbor" },
            Cast = new List<RenamedNavActor> { new() { Id = 102, Name = "Cy" } },
        },
    };

    public RenamedNavMovieProfile() : base(x => x.Id)
    {
        EntitySetName = "RenamedNavMovies";
        ExpandEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<RenamedNavMovie>>(_movies);
        GetById = (id, ct) => Task.FromResult(_movies.FirstOrDefault(m => m.Id == id));

        HasOptional(
            navigation: x => x.Studio!,
            get: (movieId, ct) => Task.FromResult(_movies.FirstOrDefault(m => m.Id == movieId)?.Studio),
            refTargetEntitySet: null);

        HasMany(
            navigation: x => x.Cast!,
            getAll: (movieId, ct) => Task.FromResult<IEnumerable<RenamedNavActor>>(
                _movies.FirstOrDefault(m => m.Id == movieId)?.Cast ?? Enumerable.Empty<RenamedNavActor>()));
    }
}

// ── #253: [JsonPropertyName] on STRUCTURAL properties → one OData/EDM name ──────
//
// A structural property carrying [JsonPropertyName] must expose that name on EVERY surface:
// $metadata, the response payload, and $select/$filter/$orderby (server-accepted). Before the
// fix the EDM used the CLR name while the payload used the rename, so $select=<clrName> — the
// only spelling the EDM accepted — dropped the property from the response (silent data loss).

internal class RenamedStructCustomer
{
    public int Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("emailAddress")]
    public string Email { get; set; } = "";

    public string Name { get; set; } = "";

    public IEnumerable<RenamedStructOrder>? Orders { get; set; }
}

internal class RenamedStructOrder
{
    public int Id { get; set; }

    // Renamed structural property on a nav-target type — exercises the nested $select drop.
    [System.Text.Json.Serialization.JsonPropertyName("displayLabel")]
    public string Label { get; set; } = "";
}

internal class RenamedStructCustomerProfile : EntitySetProfile<int, RenamedStructCustomer>
{
    private static readonly List<RenamedStructCustomer> _data = new()
    {
        new() { Id = 1, Email = "ada@example.com", Name = "Ada",
            Orders = new List<RenamedStructOrder> { new() { Id = 10, Label = "First" } } },
        new() { Id = 2, Email = "ben@example.com", Name = "Ben",
            Orders = new List<RenamedStructOrder> { new() { Id = 20, Label = "Second" } } },
    };

    public RenamedStructCustomerProfile() : base(x => x.Id)
    {
        EntitySetName = "RenamedStructCustomers";
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_data.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_data.FirstOrDefault(c => c.Id == id));
        Patch = (id, delta, ct) =>
        {
            var existing = _data.FirstOrDefault(c => c.Id == id);
            if (existing is null) return Task.FromResult<RenamedStructCustomer?>(null);
            delta.Patch(existing);
            return Task.FromResult<RenamedStructCustomer?>(existing);
        };

        HasMany(
            navigation: x => x.Orders!,
            getAll: (custId, ct) => Task.FromResult<IEnumerable<RenamedStructOrder>>(
                _data.FirstOrDefault(c => c.Id == custId)?.Orders ?? Enumerable.Empty<RenamedStructOrder>()));
    }
}

// A [JsonPropertyName] on the KEY property — the EDM key ref is renamed, but value-based key
// routing (Widgets('A1')) is name-independent and must keep working.
internal class RenamedKeyEntity
{
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
}

internal class RenamedKeyProfile : EntitySetProfile<string, RenamedKeyEntity>
{
    private static readonly List<RenamedKeyEntity> _data = new()
    {
        new() { Key = "A1", Name = "Alpha" },
        new() { Key = "B2", Name = "Beta" },
    };

    public RenamedKeyProfile() : base(x => x.Key)
    {
        EntitySetName = "RenamedKeyEntities";
        SelectEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_data.AsQueryable());
        GetById = (k, ct) => Task.FromResult(_data.FirstOrDefault(e => e.Key == k));
    }
}

// A rename that collides with a sibling property's OData name — must fail fast at startup.
internal class CollidingRenameEntity
{
    public int Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("Name")]
    public string Email { get; set; } = "";

    public string Name { get; set; } = "";
}

internal class CollidingRenameProfile : EntitySetProfile<int, CollidingRenameEntity>
{
    public CollidingRenameProfile() : base(x => x.Id)
    {
        EntitySetName = "CollidingRenames";
        GetById = (id, ct) => Task.FromResult<CollidingRenameEntity?>(null);
    }
}

// Interaction of [JsonPropertyName] with Ignore() (#226): the ignored property is gone from the
// OData surface entirely; the other renamed property still exposes its rename.
internal class RenamedIgnoreEntity
{
    public int Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("publicEmail")]
    public string Email { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("secretNote")]
    public string InternalNotes { get; set; } = "";
}

internal class RenamedIgnoreProfile : EntitySetProfile<int, RenamedIgnoreEntity>
{
    private static readonly List<RenamedIgnoreEntity> _data = new()
    {
        new() { Id = 1, Email = "e@x.com", InternalNotes = "hidden" },
    };

    public RenamedIgnoreProfile() : base(x => x.Id)
    {
        EntitySetName = "RenamedIgnores";
        SelectEnabled = true;
        Ignore(x => x.InternalNotes);
        GetQueryable = (ct) => Task.FromResult(_data.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_data.FirstOrDefault(e => e.Id == id));
    }
}
