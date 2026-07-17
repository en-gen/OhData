using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.MsODataHost;

/// <summary>
/// Classic Microsoft.AspNetCore.OData controller: ODataController + [EnableQuery] over the same
/// List&lt;BenchWidget&gt;-backed store the OhData profile uses. Handlers deliberately mirror
/// <see cref="OhData.Server.Benchmarks.OhDataHost.BenchWidgetProfile"/>: reads come from the
/// seeded store, writes return the would-be result without mutating it so every benchmark
/// iteration sees the identical dataset.
/// </summary>
public sealed class BenchWidgetsController : ODataController
{
    // One store per host process; seeded identically to the OhData host's store.
    private static readonly List<BenchWidget> Store = BenchmarkData.CreateWidgets();

    [EnableQuery(PageSize = BenchmarkData.PageSize, MaxTop = BenchmarkData.PageSize)]
    public IActionResult Get()
    {
        return Ok(Store.AsQueryable());
    }

    [EnableQuery]
    public IActionResult Get(int key)
    {
        BenchWidget? widget = Store.FirstOrDefault(w => w.Id == key);
        return widget is null ? NotFound() : Ok(widget);
    }

    public IActionResult Post([FromBody] BenchWidget widget)
    {
        widget.Id = BenchmarkData.WidgetCount + 1;
        return Created(widget);
    }

    public IActionResult Put(int key, [FromBody] BenchWidget widget)
    {
        widget.Id = key;
        return Updated(widget);
    }

    public IActionResult Patch(int key, [FromBody] Delta<BenchWidget> delta)
    {
        BenchWidget? existing = Store.FirstOrDefault(w => w.Id == key);
        if (existing is null) return NotFound();
        BenchWidget copy = existing.Clone();
        delta.Patch(copy);
        return Updated(copy);
    }

    public IActionResult Delete(int key)
    {
        // Idempotent delete without mutating the shared store (symmetric with the OhData profile).
        return NoContent();
    }
}
