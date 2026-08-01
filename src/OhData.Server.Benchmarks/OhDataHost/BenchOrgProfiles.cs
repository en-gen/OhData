using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.OhDataHost;

/// <summary>
/// OhData profile for <see cref="BenchDepartment"/>. Mirrors
/// <see cref="OhData.Server.Benchmarks.MsODataHost.BenchDepartmentsController"/>. <c>Employees</c> is
/// declared with the bare (delegate-less) <c>HasMany</c> overload, which opts the navigation INTO
/// OhData's <c>$expand</c> pushdown (a single JOIN'd query via EF Core <c>Include</c>) rather than the
/// N+1 per-entity delegate path — this is the exact subsystem the benchmark suite was missing coverage
/// for. See docs/query-options.md "Multi-level $expand and $levels".
/// </summary>
internal sealed class BenchDepartmentProfile : EntitySetProfile<int, BenchDepartment>
{
    public BenchDepartmentProfile(BenchOrgDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BenchDepartments";

        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = BenchOrgData.DepartmentPageSize;
        MaxExpansionDepth = BenchOrgData.MaxExpansionDepth; // set explicitly to mirror the MS host's [EnableQuery(MaxExpansionDepth = ...)] — see BenchOrgData.MaxExpansionDepth.

        GetQueryable = _ => Task.FromResult(db.BenchDepartments.AsQueryable());
        GetById = async (id, ct) => await db.BenchDepartments.FirstOrDefaultAsync(d => d.Id == id, ct);

        HasMany(x => x.Employees); // delegate-less -> SQL-JOIN $expand pushdown
    }
}

/// <summary>
/// OhData profile for <see cref="BenchEmployee"/>. Mirrors
/// <see cref="OhData.Server.Benchmarks.MsODataHost.BenchEmployeesController"/>. All three navigations
/// are declared bare (delegate-less), so every one of them opts into pushdown:
/// <list type="bullet">
/// <item><description><c>Department</c> — single-valued back-reference (the bidirectional half of the
/// Department/Employee pair; this exact shape is what #323 fixed pushdown for).</description></item>
/// <item><description><c>Manager</c> — single-valued self-referential navigation.</description></item>
/// <item><description><c>Reports</c> — collection self-referential navigation; the only shape the OData
/// parser accepts <c>$levels</c> on.</description></item>
/// </list>
/// </summary>
internal sealed class BenchEmployeeProfile : EntitySetProfile<int, BenchEmployee>
{
    public BenchEmployeeProfile(BenchOrgDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BenchEmployees";

        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = BenchOrgData.EmployeePageSize;
        MaxExpansionDepth = BenchOrgData.MaxExpansionDepth; // set explicitly to mirror the MS host's [EnableQuery(MaxExpansionDepth = ...)] — see BenchOrgData.MaxExpansionDepth.

        GetQueryable = _ => Task.FromResult(db.BenchEmployees.AsQueryable());
        GetById = async (id, ct) => await db.BenchEmployees.FirstOrDefaultAsync(e => e.Id == id, ct);

        HasOptional(x => x.Department); // delegate-less -> pushdown (bidirectional back-reference)
        HasOptional(x => x.Manager);    // delegate-less -> pushdown (self-referential, single-valued)
        HasMany(x => x.Reports);        // delegate-less -> pushdown ($levels-eligible)
    }
}
