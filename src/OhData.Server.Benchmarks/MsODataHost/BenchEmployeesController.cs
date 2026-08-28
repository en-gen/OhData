using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.MsODataHost;

/// <summary>
/// Classic Microsoft.AspNetCore.OData controller over <see cref="BenchOrgDbContext"/>, mirroring
/// <see cref="OhData.Server.Benchmarks.OhDataHost.BenchEmployeeProfile"/>. The <c>Get()</c> collection
/// action is what the <c>$levels</c> benchmark scenario hits, filtered down to a single root row
/// (<c>BenchEmployees?$filter=Id eq 1&amp;$expand=Reports($levels=2)</c> — see
/// <see cref="OhData.Server.Benchmarks.BenchmarkRequests.EmployeeLevelsUrl"/> for why it goes through
/// the collection route with <c>$filter</c> rather than <c>GetById</c>).
/// <para>
/// Deliberately does NOT set <c>[EnableQuery(PageSize = ...)]</c> — same reasoning as
/// <c>BenchDepartmentsController</c>: <c>PageSize</c> wraps every collection in the response, including
/// each level of a <c>$levels</c> recursion, in a <c>TruncatedCollection</c>; composing that implicit
/// windowing with the recursive <c>$levels</c> expand requires the SQL <c>APPLY</c> operation, which
/// SQLite's EF Core provider does not support, and the request 500s. No scenario in this suite queries
/// <c>BenchEmployees</c> unfiltered, so there is no default-page-size behavior to lose by leaving it unset.
/// </para>
/// </summary>
public sealed class BenchEmployeesController : ODataController
{
    private readonly BenchOrgDbContext _db;

    public BenchEmployeesController(BenchOrgDbContext db) => _db = db;

    [EnableQuery(MaxTop = BenchOrgData.EmployeePageSize, MaxExpansionDepth = BenchOrgData.MaxExpansionDepth)]
    public IActionResult Get() => Ok(_db.BenchEmployees);

    [EnableQuery(MaxExpansionDepth = BenchOrgData.MaxExpansionDepth)]
    public IActionResult Get(int key)
    {
        BenchEmployee? employee = _db.BenchEmployees.FirstOrDefault(e => e.Id == key);
        return employee is null ? NotFound() : Ok(employee);
    }
}
