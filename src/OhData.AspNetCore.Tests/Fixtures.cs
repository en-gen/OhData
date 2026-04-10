using OhData.Abstractions;

namespace OhData.AspNetCore.Tests;

// ── Shared test entities and profiles ────────────────────────────────────────

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

/// <summary>Profile that intentionally configures no handlers — used to verify routes are omitted.</summary>
internal class EmptyProfile : EntitySetProfile<int, Widget>
{
    public EmptyProfile() : base(x => x.Id)
    {
        EntitySetName = "EmptyWidgets";
    }
}

/// <summary>Profile that requires authorization — used to verify auth is applied to routes.</summary>
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
