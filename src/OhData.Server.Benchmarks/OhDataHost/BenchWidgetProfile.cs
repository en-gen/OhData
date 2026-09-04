using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using OhData;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.OhDataHost;

/// <summary>
/// OhData profile for <see cref="BenchWidget"/>. Mirrors the operations exposed by
/// <see cref="OhData.Server.Benchmarks.MsODataHost.BenchWidgetsController"/> so the two pipelines
/// are compared on an equal footing: same dataset, same query surface, same "don't mutate the
/// shared store" discipline (mutating writes would make iteration N+1 measure a different
/// dataset than iteration N — see OhData.Client.Benchmarks/ServerPipelineBenchmarks.cs for the
/// same pattern).
/// </summary>
internal sealed class BenchWidgetProfile : EntitySetProfile<int, BenchWidget>
{
    // Static: OhData profiles are scoped services (constructed once per request), so an instance
    // field would re-seed the 1000-widget dataset on every request and the benchmark would
    // measure dataset creation instead of the pipeline. The MS OData controller's store is
    // static for the same reason (controllers are also constructed per request).
    private static readonly List<BenchWidget> Store = BenchmarkData.CreateWidgets();

    public BenchWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "BenchWidgets";

        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        MaxTop = BenchmarkData.PageSize;

        GetQueryable = () => Store.AsQueryable();

        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(w => w.Id == id));

        Post = (widget, _) =>
        {
            widget.Id = BenchmarkData.WidgetCount + 1;
            return OhDataResult.Success<BenchWidget>(widget);
        };

        Put = (id, widget, _) =>
        {
            widget.Id = id;
            return OhDataResult.Success(widget);
        };

        Patch = (id, delta, _) =>
        {
            BenchWidget? existing = Store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return OhDataResult.Success<BenchWidget?>(null);
            BenchWidget copy = existing.Clone();
            delta.Patch(copy);
            return OhDataResult.Success<BenchWidget?>(copy);
        };

        Delete = (_, __) => OhDataResult.Success(true);
    }
}
