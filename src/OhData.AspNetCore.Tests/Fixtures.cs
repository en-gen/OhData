using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using OhData.Abstractions;
using OhData.Abstractions.AspNetCore.OData;

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
    }
}

internal class Child { public int Id { get; set; } public int ParentId { get; set; } public string Name { get; set; } = ""; }
internal class Parent { public int Id { get; set; } public string Name { get; set; } = ""; public IEnumerable<Child>? Children { get; set; } }

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

