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
        GetETag = widget => $"v{widget.Name.Length}"; // simple deterministic ETag
    }
}
