using Microsoft.EntityFrameworkCore;
using OhData.Abstractions;

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
            return Task.FromResult(widget);
        };

        PutById = (id, widget, ct) =>
        {
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };

        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);

        Patch = (id, widget, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            if (widget.Name != "") existing.Name = widget.Name;
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
        PutById = (id, widget, ct) =>
        {
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };
        UseETag(x => x.Name);
    }
}

internal class BoundOpsProfile : EntitySetProfile<int, Widget>
{
    // Non-static: each DI container gets its own instance to isolate tests.
    private readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };

    public BoundOpsProfile() : base(x => x.Id)
    {
        EntitySetName = "BoundWidgets";
        SelectEnabled = true;
        FilterEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);

        BindFunction(GetByName);
        BindFunction(DoubleCount);
        BindAction(ClearAll);
        BindAction(AddSuffix);
    }

    // Function: GET /BoundWidgets/GetByName?name=Alpha
    private Task<IEnumerable<Widget>> GetByName(string name) =>
        Task.FromResult<IEnumerable<Widget>>(_store.Where(w =>
            string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)));

    // Function: GET /BoundWidgets/DoubleCount?factor=2
    private Task<int> DoubleCount(int factor) =>
        Task.FromResult(_store.Count * factor);

    // Action: POST /BoundWidgets/ClearAll  (no body params)
    private void ClearAll() => _store.Clear();

    // Action: POST /BoundWidgets/AddSuffix  { "suffix": "!" }
    private void AddSuffix(string suffix)
    {
        foreach (var w in _store) w.Name += suffix;
    }
}

/// <summary>Profile for testing PUT null (no-match) returning 404.</summary>
internal class NullPutProfile : EntitySetProfile<int, Widget>
{
    public NullPutProfile() : base(x => x.Id)
    {
        EntitySetName = "NullPutWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(null);
        PutById = (id, widget, ct) => Task.FromResult<Widget>(null!); // always "not found"
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
        Post = (widget, ct) => { _store.Add(widget); return Task.FromResult(widget); };
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
internal class EntityBoundOpsProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };

    public EntityBoundOpsProfile() : base(x => x.Id)
    {
        EntitySetName = "EntityBoundWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

        BindEntityFunction(GetNameForKey);
        BindEntityAction(RenameWidget);
    }

    // Entity-level function: GET /EntityBoundWidgets(1)/GetNameForKey
    private Task<string> GetNameForKey(int key)
    {
        var widget = _store.FirstOrDefault(w => w.Id == key);
        return Task.FromResult(widget?.Name ?? "");
    }

    // Entity-level action: POST /EntityBoundWidgets(1)/RenameWidget { "newName": "..." }
    private Task RenameWidget(int key, string newName)
    {
        var widget = _store.FirstOrDefault(w => w.Id == key);
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
