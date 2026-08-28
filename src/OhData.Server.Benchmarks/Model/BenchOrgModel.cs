using System.Collections.Generic;

namespace OhData.Server.Benchmarks.Model;

/// <summary>
/// Navigation-shaped fixture closing the benchmark suite's original coverage gap: <see cref="BenchWidget"/>
/// has zero EDM navigations, so the entire <c>$expand</c> subsystem (pushdown JOIN, nested <c>$expand</c>,
/// <c>$levels</c>, bidirectional back-references) went unmeasured even though it is OhData's headline
/// differentiator and its most heavily-modified subsystem. This pair is deliberately the same shape that
/// broke undetected in #323/#325/#326: a parent/child pair with a collection navigation AND a typed
/// back-reference on the child (bidirectional), plus a self-referential hierarchy on the child for
/// <c>$levels</c>.
/// <list type="bullet">
/// <item><description><see cref="BenchDepartment.Employees"/> — collection navigation, parent → children.</description></item>
/// <item><description><see cref="BenchEmployee.Department"/> — single-valued navigation, child → parent
/// (the back-reference half of the bidirectional pair).</description></item>
/// <item><description><see cref="BenchEmployee.Manager"/> / <see cref="BenchEmployee.Reports"/> —
/// self-referential single-valued / collection navigation (a manager tree), the shape <c>$levels</c>
/// requires.</description></item>
/// </list>
/// Unlike <see cref="BenchWidget"/> (a plain <c>List&lt;T&gt;</c> store — <c>$expand</c> pushdown is
/// gated to an EF Core-backed <c>IQueryable</c>; see <c>OhDataEndpointFactory.ResolveEfCoreAssembly</c>),
/// this pair is served from <see cref="BenchOrgDbContext"/> (EF Core Sqlite, in-memory keep-alive
/// connection) so the pushdown code path is actually exercised rather than silently falling back to the
/// EDM-only path. Both hosts read from an EF Core Sqlite-backed <c>IQueryable</c> for a fair, apples-to-
/// apples comparison — see <c>BenchmarkHosts</c>.
/// </summary>
public sealed class BenchDepartment
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<BenchEmployee> Employees { get; set; } = new();
}

/// <summary>See <see cref="BenchDepartment"/> for the navigation shapes this fixture exists to cover.</summary>
public sealed class BenchEmployee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Salary { get; set; }

    public int DepartmentId { get; set; }

    // Non-nullable annotation so the HasOptional selector satisfies the `class` constraint (matching
    // OhData.AspNetCore.Tests' RefHolder/RefTarget fixture) — the nullable FK below is what actually
    // makes the relationship optional; the value is genuinely null at runtime for the tree root, whose
    // ManagerId is null.
    public BenchDepartment Department { get; set; } = null!;

    public int? ManagerId { get; set; }
    public BenchEmployee Manager { get; set; } = null!;
    public List<BenchEmployee> Reports { get; set; } = new();
}
