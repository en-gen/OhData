using Microsoft.EntityFrameworkCore;

namespace OhData.Server.Benchmarks.Model;

/// <summary>
/// EF Core context backing <see cref="BenchDepartment"/>/<see cref="BenchEmployee"/>. Both hosts open
/// their own Sqlite in-memory connection and their own instance of this context (see
/// <c>BenchmarkHosts</c>) — <see cref="BenchOrgData.Seed"/> then populates each independently with the
/// identical deterministic dataset, so there is no shared mutable state between the two servers, exactly
/// as the <see cref="BenchWidget"/> <c>List&lt;T&gt;</c> stores already avoid it.
/// </summary>
public sealed class BenchOrgDbContext : DbContext
{
    public BenchOrgDbContext(DbContextOptions<BenchOrgDbContext> options) : base(options) { }

    public DbSet<BenchDepartment> BenchDepartments => Set<BenchDepartment>();
    public DbSet<BenchEmployee> BenchEmployees => Set<BenchEmployee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchDepartment>()
            .HasMany(d => d.Employees)
            .WithOne(e => e.Department)
            .HasForeignKey(e => e.DepartmentId);

        // Self-referential manager tree (BenchEmployee.Manager / BenchEmployee.Reports).
        modelBuilder.Entity<BenchEmployee>()
            .HasOne(e => e.Manager)
            .WithMany(e => e.Reports)
            .HasForeignKey(e => e.ManagerId);
    }
}
