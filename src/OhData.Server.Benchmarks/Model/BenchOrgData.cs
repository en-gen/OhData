using System.Collections.Generic;
using System.Linq;

namespace OhData.Server.Benchmarks.Model;

/// <summary>
/// Deterministic, seed-free dataset generator for the <see cref="BenchDepartment"/>/<see cref="BenchEmployee"/>
/// navigation fixture — the counterpart to <see cref="BenchmarkData"/> for the <see cref="BenchWidget"/>
/// fixture. Scale matches <see cref="BenchmarkData"/>'s precedent: 1000 leaf rows, 100-row page.
/// </summary>
internal static class BenchOrgData
{
    public const int DepartmentCount = 20;
    public const int EmployeeCount = 1000;

    /// <summary>Matches <see cref="BenchmarkData.PageSize"/> so the two fixtures page identically.</summary>
    public const int EmployeePageSize = BenchmarkData.PageSize;

    /// <summary>The whole department set (20 rows) fits in a single page.</summary>
    public const int DepartmentPageSize = DepartmentCount;

    /// <summary>Each manager has (up to) this many direct reports in the seeded tree.</summary>
    public const int ManagerBranchingFactor = 5;

    /// <summary>The single root of the manager tree (no <c>ManagerId</c>) — every other employee
    /// descends from it, so the tree is fully connected and deterministic depth-first from here.</summary>
    public const int RootEmployeeId = 1;

    /// <summary>
    /// <c>$levels</c> depth used by the benchmark scenario: root (1) + its 5 direct reports +
    /// their 25 reports = 31 employees, a bounded and meaningfully-sized recursive expand.
    /// </summary>
    public const int LevelsExpandDepth = 2;

    /// <summary>
    /// Matches OhData's <c>EntitySetDefaults.MaxExpansionDepth</c> default (3) — set explicitly on both
    /// hosts so the pairing can't silently drift apart if either framework's default ever changes.
    /// </summary>
    public const int MaxExpansionDepth = 3;

    public static List<BenchDepartment> CreateDepartments() =>
        Enumerable.Range(1, DepartmentCount)
            .Select(i => new BenchDepartment { Id = i, Name = $"Department-{i:D2}" })
            .ToList();

    /// <summary>
    /// Employee i (1-based) sits in department <c>((i-1) % DepartmentCount) + 1</c> — independent of
    /// the manager tree below, so the bidirectional Department/Employee pair and the self-referential
    /// Manager/Reports tree exercise two orthogonal relationships over the same rows.
    /// <para>
    /// The manager tree is a deterministic <see cref="ManagerBranchingFactor"/>-ary tree rooted at
    /// <see cref="RootEmployeeId"/>: 0-based index <c>j = i - 1</c>; employee <c>j == 0</c> is the root
    /// (no manager); employee <c>j &gt; 0</c> reports to employee index <c>(j - 1) / ManagerBranchingFactor</c>
    /// (1-based id = that index + 1). This gives the root exactly <see cref="ManagerBranchingFactor"/>
    /// direct reports (employees 2..6) and each of those exactly 5 reports of their own (employees
    /// 7..31), matching <see cref="LevelsExpandDepth"/>'s expected counts.
    /// </para>
    /// </summary>
    public static List<BenchEmployee> CreateEmployees() =>
        Enumerable.Range(1, EmployeeCount)
            .Select(i =>
            {
                int j = i - 1;
                int? managerId = j == 0 ? null : (j - 1) / ManagerBranchingFactor + 1;
                return new BenchEmployee
                {
                    Id = i,
                    Name = $"Employee-{i:D4}",
                    Salary = 50_000m + i * 37 % 40_000,
                    DepartmentId = j % DepartmentCount + 1,
                    ManagerId = managerId,
                };
            })
            .ToList();

    /// <summary>
    /// Seeds an empty EF Core-backed database with the deterministic dataset. Both hosts call this
    /// independently against their own <see cref="BenchOrgDbContext"/>/connection so each owns its own
    /// data — no shared mutable state between the two servers — while producing byte-for-byte identical
    /// rows, the same discipline <see cref="BenchmarkData.CreateWidgets"/> follows for the widget store.
    /// </summary>
    public static void Seed(BenchOrgDbContext db)
    {
        db.BenchDepartments.AddRange(CreateDepartments());
        db.BenchEmployees.AddRange(CreateEmployees());
        db.SaveChanges();
    }
}
