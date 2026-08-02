using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;

namespace OhData.Server.Benchmarks.Model;

/// <summary>
/// Seeded dataset generator for the <see cref="BenchDepartment"/>/<see cref="BenchEmployee"/> navigation
/// fixture — the counterpart to <see cref="BenchmarkData"/> for the <see cref="BenchWidget"/> fixture.
/// <para>
/// <b>Volume is fixed; distribution and values are seeded.</b> <see cref="DepartmentCount"/> and
/// <see cref="EmployeeCount"/> are compile-time constants — every run has exactly the same number of
/// rows — but WHICH department each employee lands in (a pronounced, seed-shuffled skew rather than the
/// old perfectly-uniform round robin), the shape of the manager tree, and every employee/department name
/// and salary are all derived from <see cref="DefaultSeed"/> (or a <c>--seed</c>/<c>OHDATA_BENCH_SEED</c>
/// override — see <see cref="BenchSeedResolver"/>), so a given seed reproduces byte-identical data and a
/// different seed produces a genuinely different (but equally-shaped) dataset. This closes two gaps
/// documented as known limitations when this fixture was introduced: uniform fan-out hides exactly the
/// regime where nested-<c>$top</c> windowing strategies diverge from "materialize everything and count",
/// and a perfectly uniform 50/department split gave every department the same size, so skew never
/// mattered to any scenario in this suite.
/// </para>
/// </summary>
internal static class BenchOrgData
{
    public const int DepartmentCount = 20;
    public const int EmployeeCount = 1000;

    /// <summary>Matches <see cref="BenchmarkData.PageSize"/> so the two fixtures page identically.</summary>
    public const int EmployeePageSize = BenchmarkData.PageSize;

    /// <summary>
    /// Deliberately LESS than <see cref="DepartmentCount"/> so the root <c>BenchDepartments</c>
    /// collection actually pages — under the old <c>DepartmentPageSize == DepartmentCount</c>, every
    /// department-rooted scenario in this suite returned its entire result set on page one and root
    /// paging was never exercised by anything. 12 of 20 keeps the first page a meaningful majority while
    /// leaving a genuine second page (8 departments) for the paging path to cover. Every department-rooted
    /// benchmark request now sends an explicit <c>$top=DepartmentPageSize</c> (see
    /// <see cref="BenchmarkRequests"/>) so both hosts window to the same page size — Microsoft's
    /// <c>[EnableQuery(MaxTop=...)]</c> only caps a client-*supplied* <c>$top</c>, it does not apply one
    /// on its own the way OhData's <c>MaxTop</c> does, so leaving the request unpaged would silently make
    /// the two hosts return different row counts for the identical URL.
    /// </summary>
    public const int DepartmentPageSize = 12;

    /// <summary>The single root of the manager tree (no <c>ManagerId</c>) — every other employee
    /// descends from it, so the tree is fully connected and deterministic depth-first from here.</summary>
    public const int RootEmployeeId = 1;

    /// <summary>
    /// <c>$levels</c> depth used by the benchmark scenario. The manager tree's branching factor is now
    /// seed-derived (see <see cref="MinManagerBranchingFactor"/>/<see cref="MaxManagerBranchingFactor"/>),
    /// so the exact employee count at this depth varies by seed — what stays invariant (and is what
    /// makes the scenario meaningful) is that the tree is connected, acyclic, single-rooted, and bounded
    /// in depth, so a depth-2 expand is always a small, well-defined, non-empty subtree. Both hosts always
    /// agree on the exact result for a given seed; see the "$levels" smoke check.
    /// </summary>
    public const int LevelsExpandDepth = 2;

    /// <summary>
    /// Matches OhData's <c>EntitySetDefaults.MaxExpansionDepth</c> default (3) — set explicitly on both
    /// hosts so the pairing can't silently drift apart if either framework's default ever changes.
    /// </summary>
    public const int MaxExpansionDepth = 3;

    /// <summary>Lower bound of the seed-derived manager-tree branching factor (see <see cref="Blueprint"/>).</summary>
    public const int MinManagerBranchingFactor = 3;

    /// <summary>Upper bound of the seed-derived manager-tree branching factor (see <see cref="Blueprint"/>).</summary>
    public const int MaxManagerBranchingFactor = 7;

    /// <summary>
    /// Zipf-like exponent controlling department fan-out skew: <c>weight(rank) = 1 / rank^exponent</c>.
    /// 1.3 gives a pronounced-but-not-cartoonish profile at this fixture's scale (20 departments / 1000
    /// employees) — empirically, the largest department lands around a third of all employees and the
    /// smallest around a handful (single-digit to low double-digit) — see <see cref="ComputeSkewedDepartmentSizes"/>.
    /// </summary>
    private const double DepartmentSkewExponent = 1.3;

    /// <summary>
    /// The committed, reviewable default seed. Overridable per-run with <c>--seed N</c> on the command
    /// line, or <c>OHDATA_BENCH_SEED</c> in the environment for CI pinning without touching the command
    /// line — see <see cref="BenchSeedResolver"/> for the full precedence chain. A fixed default (rather
    /// than a random one) is deliberate: this suite exists to publish comparable numbers, and a number
    /// nobody can reproduce from the checked-out commit isn't useful for that job. Exploring how the
    /// suite behaves under a different data shape is what <c>--seed</c> is for.
    /// </summary>
    public const int DefaultSeed = 42;

    /// <summary>
    /// The fully-generated dataset for a given seed: every <see cref="BenchDepartment"/>/<see cref="BenchEmployee"/>
    /// row plus the seed-derived parameters that produced them, exposed so both diagnostics (smoke check
    /// department-size reporting) and both hosts' seeding can read the exact same values without either
    /// regenerating anything.
    /// </summary>
    public sealed record Blueprint(
        int Seed,
        int ManagerBranchingFactor,
        IReadOnlyList<int> DepartmentSizes,
        IReadOnlyList<BenchDepartment> Departments,
        IReadOnlyList<BenchEmployee> Employees);

    private static readonly object BlueprintLock = new();
    private static Blueprint? _cachedBlueprint;

    /// <summary>
    /// Builds (or returns the already-built) dataset for <paramref name="seed"/>. Generation runs at
    /// most once per process per seed — the RNG-touching work (skew shuffle, manager-tree branching
    /// factor, every Bogus-generated name/salary) happens exactly once, and is cached so that seeding
    /// N hosts from the same process reads identical, already-materialized values rather than invoking
    /// the RNG a second time. This is what makes cross-host data identity (mandatory — see <see cref="Seed"/>)
    /// hold BY CONSTRUCTION rather than by any discipline around resetting RNG state between calls.
    /// </summary>
    public static Blueprint GetOrCreateBlueprint(int seed)
    {
        lock (BlueprintLock)
        {
            if (_cachedBlueprint is { } cached && cached.Seed == seed)
                return cached;

            _cachedBlueprint = Build(seed);
            return _cachedBlueprint;
        }
    }

    private static Blueprint Build(int seed)
    {
        var rng = new Random(seed);

        // Bogus's Randomizer.Seed is a process-wide STATIC — mutating it would leak into anything else
        // in the process that touches Bogus. Scoping the seed to this one Faker instance instead (via
        // Faker.Random) avoids that hazard entirely rather than relying on discipline to reset it.
        var faker = new Faker { Random = new Randomizer(seed) };

        int branchingFactor = rng.Next(MinManagerBranchingFactor, MaxManagerBranchingFactor + 1);
        IReadOnlyList<int> departmentSizes = ComputeSkewedDepartmentSizes(rng);

        var departments = new List<BenchDepartment>(DepartmentCount);
        for (int id = 1; id <= DepartmentCount; id++)
            departments.Add(new BenchDepartment { Id = id, Name = $"{faker.Commerce.Department()}-{id:D2}" });

        int[] departmentForEmployee = new int[EmployeeCount + 1]; // 1-based; index 0 unused
        int cursor = 1;
        for (int dept = 1; dept <= DepartmentCount; dept++)
        {
            int size = departmentSizes[dept - 1];
            for (int k = 0; k < size; k++)
                departmentForEmployee[cursor++] = dept;
        }

        var employees = new List<BenchEmployee>(EmployeeCount);
        for (int id = 1; id <= EmployeeCount; id++)
        {
            int j = id - 1;
            int? managerId = j == 0 ? null : (j - 1) / branchingFactor + 1;
            employees.Add(new BenchEmployee
            {
                Id = id,
                Name = faker.Name.FullName(),
                Salary = faker.Finance.Amount(50_000m, 190_000m, 2), // already rounded to 2 decimals
                DepartmentId = departmentForEmployee[id],
                ManagerId = managerId,
            });
        }

        return new Blueprint(seed, branchingFactor, departmentSizes, departments, employees);
    }

    /// <summary>
    /// Produces a per-department employee count that sums to exactly <see cref="EmployeeCount"/>, gives
    /// every department at least one employee, and is skewed: department sizes follow a Zipf-like
    /// <c>1/rank^exponent</c> curve (see <see cref="DepartmentSkewExponent"/>), but WHICH department gets
    /// which rank is shuffled by <paramref name="rng"/> — so the skew shape is fixed while which
    /// department ends up the outlier varies with the seed. The largest-remainder method distributes the
    /// rounding remainder so the total lands on <see cref="EmployeeCount"/> exactly rather than
    /// approximately.
    /// </summary>
    private static IReadOnlyList<int> ComputeSkewedDepartmentSizes(Random rng)
    {
        double[] weights = new double[DepartmentCount];
        for (int rank = 0; rank < DepartmentCount; rank++)
            weights[rank] = 1.0 / Math.Pow(rank + 1, DepartmentSkewExponent);

        int[] departmentByRank = Enumerable.Range(1, DepartmentCount).ToArray();
        Shuffle(departmentByRank, rng);

        double totalWeight = weights.Sum();
        double[] rawShares = weights.Select(w => w / totalWeight * EmployeeCount).ToArray();

        int[] sizeByRank = rawShares.Select(s => Math.Max(1, (int)Math.Floor(s))).ToArray();
        int remainder = EmployeeCount - sizeByRank.Sum();

        int[] ranksByFractionDesc = Enumerable.Range(0, DepartmentCount)
            .OrderByDescending(rank => rawShares[rank] - Math.Floor(rawShares[rank]))
            .ToArray();

        int cursor = 0;
        while (remainder > 0)
        {
            sizeByRank[ranksByFractionDesc[cursor % DepartmentCount]]++;
            remainder--;
            cursor++;
        }

        // Defensive only: at this fixture's scale (1000 employees / 20 departments) every department's
        // raw share already clears the floor of 1, so this never actually trims anything — but a future
        // change to the constants shouldn't be able to silently break the "sums to exactly EmployeeCount"
        // invariant just because the floor-of-1 clamp above pushed the total over.
        while (remainder < 0)
        {
            int largest = Array.IndexOf(sizeByRank, sizeByRank.Max());
            if (sizeByRank[largest] <= 1)
                break;
            sizeByRank[largest]--;
            remainder++;
        }

        int[] sizeByDepartment = new int[DepartmentCount];
        for (int rank = 0; rank < DepartmentCount; rank++)
            sizeByDepartment[departmentByRank[rank] - 1] = sizeByRank[rank];
        return sizeByDepartment;
    }

    private static void Shuffle(int[] items, Random rng)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Seeds an empty EF Core-backed database with <paramref name="seed"/>'s dataset. Both hosts call
    /// this independently against their own <see cref="BenchOrgDbContext"/>/connection (see
    /// <c>BenchmarkHosts</c>) — <b>cross-host data identity is mandatory</b>: the entire comparison rests
    /// on both hosts seeing byte-identical rows within a run (verified directly by the smoke check's
    /// row-hash assertion). This holds by construction here rather than by discipline: <see cref="GetOrCreateBlueprint"/>
    /// generates the row VALUES exactly once per seed and caches them, so a second call for the same seed
    /// (the second host) never touches the RNG again. What this method materializes into <paramref name="db"/>
    /// is a fresh CLONE of each blueprint row rather than the cached instances themselves — EF Core
    /// performs navigation-property fixup on <c>AddRange</c> (populating <c>BenchDepartment.Employees</c>,
    /// resolving <c>BenchEmployee.Manager</c>, etc.) against whichever <see cref="BenchOrgDbContext"/>
    /// tracks the object; sharing the literal instances across two independently-tracked contexts would
    /// let one host's fixup mutate objects the other host then inserts, so each host still gets its own
    /// independent object graph — identical values, no shared mutable state, matching the discipline
    /// <see cref="BenchmarkData.CreateWidgets"/> already follows for the widget store.
    /// </summary>
    public static void Seed(BenchOrgDbContext db, int seed)
    {
        Blueprint blueprint = GetOrCreateBlueprint(seed);
        db.BenchDepartments.AddRange(blueprint.Departments.Select(CloneDepartment));
        db.BenchEmployees.AddRange(blueprint.Employees.Select(CloneEmployee));
        db.SaveChanges();
    }

    private static BenchDepartment CloneDepartment(BenchDepartment d) => new() { Id = d.Id, Name = d.Name };

    private static BenchEmployee CloneEmployee(BenchEmployee e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Salary = e.Salary,
        DepartmentId = e.DepartmentId,
        ManagerId = e.ManagerId,
    };
}
