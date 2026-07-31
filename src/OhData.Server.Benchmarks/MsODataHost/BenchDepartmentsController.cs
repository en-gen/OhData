using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.MsODataHost;

/// <summary>
/// Classic Microsoft.AspNetCore.OData controller over <see cref="BenchOrgDbContext"/>, mirroring
/// <see cref="OhData.Server.Benchmarks.OhDataHost.BenchDepartmentProfile"/>. <c>MaxExpansionDepth</c> is
/// set explicitly to match OhData's resolved default (<see cref="BenchOrgData.MaxExpansionDepth"/>) so
/// both hosts reject/accept the same nesting depth.
/// <para>
/// Deliberately does NOT set <c>[EnableQuery(PageSize = ...)]</c> on <see cref="Get()"/> (unlike
/// <c>BenchWidgetsController</c>/<c>BenchEmployeesController</c>, which do): Microsoft.AspNetCore.OData's
/// <c>PageSize</c> wraps EVERY collection in the response in a <c>TruncatedCollection</c> — not just the
/// root — so it would also truncate the expanded <c>Employees</c> collection (up to 50 rows/department)
/// down to whatever <c>PageSize</c> is set to, which OhData's bare (no nested <c>$top</c>) <c>$expand</c>
/// never does (see docs/query-options.md: a bare <c>$expand=Nav</c> is deliberately left unbounded).
/// Worse, composing that implicit windowing with a FURTHER nested <c>$expand</c> (the
/// <c>Employees($expand=Manager)</c> scenario) requires the SQL <c>APPLY</c> operation, which SQLite's EF
/// Core provider does not support, and the request 500s. <see cref="BenchOrgData.DepartmentCount"/> (20)
/// is small enough that an explicit page ceiling isn't needed for this fixture — both hosts return the
/// full set either way — so leaving it unset keeps the two frameworks' actually-comparable behavior
/// paired instead of tripping a MS-OData-specific default-paging quirk that OhData doesn't share.
/// </para>
/// </summary>
public sealed class BenchDepartmentsController : ODataController
{
    private readonly BenchOrgDbContext _db;

    public BenchDepartmentsController(BenchOrgDbContext db) => _db = db;

    [EnableQuery(MaxTop = BenchOrgData.DepartmentPageSize, MaxExpansionDepth = BenchOrgData.MaxExpansionDepth)]
    public IActionResult Get() => Ok(_db.BenchDepartments);

    [EnableQuery(MaxExpansionDepth = BenchOrgData.MaxExpansionDepth)]
    public IActionResult Get(int key)
    {
        BenchDepartment? department = _db.BenchDepartments.FirstOrDefault(d => d.Id == key);
        return department is null ? NotFound() : Ok(department);
    }
}
