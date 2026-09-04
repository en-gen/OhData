using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #428: `$levels=max` was served at depths every numeric spelling rejected with 400. MS's validator
// rejects a NUMERIC N above min(MaxExpansionDepth, modelBoundMaxDepth) but only requires that minimum
// to be non-zero for IsMaxLevel -- it does not clamp -- and OhData resolved `max` against
// MaxExpansionDepth alone, in TWO independently transcribed places.
//
// Measured with the model-bound cap lowered to 5 and a profile at MaxExpansionDepth=8: $levels=5 ->
// 200 in 609 ms, 6..9 -> 400, `max` -> 200 in 5,477 ms at depth 8. At ~3x translation cost per level
// (#328) that is a cost multiplier -- on a stock build a profile at depth 15 extrapolates to hours of
// CPU for one request.
//
// Two things ship: one shared resolution function consulting BOTH bounds, and #328 deriving the
// model-bound cap from the MaxExpansionDepth ceiling so the divergence becomes unrepresentable rather
// than merely fixed. Hence the behavioural half asserts consistency at the ceiling while the unit half
// drives the function with the configuration that used to be reachable.
public class ExpandLevelsResolutionTests
{
    // ── The rule itself ─────────────────────────────────────────────────────────────────────────

    // The pre-#428 expression was `isMax ? remainingDepth : (int)level`, then `Math.Min(_,
    // remainingDepth)` — the model-bound cap appears nowhere in it. Every case below where
    // modelBoundCap < remainingDepth returns that expression's answer on the old code and the
    // clamped answer now.
    [Theory]
    // isMax, requested, remainingDepth, modelBoundCap, expected
    [InlineData(true, 0, 15, 5, 5)]     // #428 exactly: pre-fix this was 15
    [InlineData(true, 0, 8, 5, 5)]      // the measured repro above: pre-fix 8
    [InlineData(false, 13, 15, 5, 5)]   // numeric over the cap clamps too: pre-fix 13
    [InlineData(true, 0, 3, 6, 3)]      // shipped shape: cap >= depth, so depth governs
    [InlineData(true, 0, 6, 6, 6)]      // at the ceiling, both bounds agree
    [InlineData(false, 2, 6, 6, 2)]     // an under-cap numeric is untouched
    [InlineData(false, 99, 3, 6, 3)]    // numeric over remainingDepth clamps to it
    [InlineData(false, long.MaxValue, 3, 6, 3)] // no overflow on the long → int narrowing
    [InlineData(false, 0, 6, 6, 0)]     // 0 stays 0; each caller decides what 0 means
    public void ResolveLevelsBudget_ConsultsBothBounds(
        bool isMaxLevel, long requested, int remainingDepth, int modelBoundCap, int expected) =>
        Assert.Equal(expected, OhDataEndpointFactory.ResolveLevelsBudget(
            isMaxLevel, requested, remainingDepth, modelBoundCap));

    // The clamp is only unreachable on a shipped build while these two stay tied. Untie them —
    // raise the ceiling without raising the model-bound cap — and #428 is live again. This is the
    // tripwire for that.
    [Fact]
    public void ModelBoundCap_IsTiedToTheDepthCeiling() =>
        Assert.Equal(EntitySetDefaults.MaxExpansionDepthCeiling, OhDataEndpointFactory.MaxNestedExpandDepth);

    // ── The behaviour, end to end at the ceiling ────────────────────────────────────────────────

    // `$levels=max` must resolve to EXACTLY the depth the deepest accepted numeric spelling
    // resolves to — never one level deeper. Byte-equality of the two responses is the strongest
    // available statement of that, and it is what fails if `max` ever outruns the numeric bound
    // again.
    [Fact]
    public async Task LevelsMax_MatchesTheDeepestAcceptedNumericSpelling_Exactly()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        HttpResponseMessage viaMax = await fx.Client.GetAsync(
            "/odata/MaxLvNodes?$filter=ParentId eq null&$expand=Kids($levels=max)");
        HttpResponseMessage viaNumeric = await fx.Client.GetAsync(
            $"/odata/MaxLvNodes?$filter=ParentId eq null&$expand=Kids($levels={EntitySetDefaults.MaxExpansionDepthCeiling})");

        Assert.Equal(HttpStatusCode.OK, viaMax.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaNumeric.StatusCode);
        Assert.Equal(
            await viaNumeric.Content.ReadAsStringAsync(),
            await viaMax.Content.ReadAsStringAsync());
    }

    // The other half of the same statement: one level past the ceiling is rejected. If `max` were
    // resolving deeper than the numeric bound, these two facts would be inconsistent — a depth the
    // server refuses to be ASKED for is a depth it must not volunteer.
    [Fact]
    public async Task OneLevelPastTheCeiling_IsRejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        HttpResponseMessage tooDeep = await fx.Client.GetAsync(
            $"/odata/MaxLvNodes?$filter=ParentId eq null&$expand=Kids($levels={EntitySetDefaults.MaxExpansionDepthCeiling + 1})");
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
    }

    private static async Task<TestFixture> BuildAsync(SqliteConnection connection)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<MaxLvNodeProfile>(),
            configureServices: s => s.AddDbContext<MaxLvDbContext>(o => o.UseSqlite(connection)));

        using IServiceScope scope = fx.App.Services.CreateScope();
        MaxLvDbContext db = scope.ServiceProvider.GetRequiredService<MaxLvDbContext>();
        db.Database.EnsureCreated();
        // A chain DEEPER than the ceiling, so a request served one level too deep would show up as
        // an extra nesting level in the payload rather than as an identical body.
        for (int i = 1; i <= 10; i++)
            db.MaxLvNodes.Add(new MaxLvNode { Id = i, Name = "N" + i, ParentId = i == 1 ? null : i - 1 });
        await db.SaveChangesAsync();
        return fx;
    }
}

public sealed class MaxLvNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<MaxLvNode> Kids { get; set; } = new();
}

public sealed class MaxLvDbContext : DbContext
{
    public MaxLvDbContext(DbContextOptions<MaxLvDbContext> options) : base(options) { }
    public DbSet<MaxLvNode> MaxLvNodes => Set<MaxLvNode>();
    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<MaxLvNode>().HasMany(n => n.Kids).WithOne().HasForeignKey(n => n.ParentId);
}

// At the ceiling: the deepest configuration a shipped build accepts, which is where `$levels=max`
// had the most room to outrun the numeric bound.
public sealed class MaxLvNodeProfile : EntitySetProfile<int, MaxLvNode>
{
    public MaxLvNodeProfile(MaxLvDbContext db) : base(x => x.Id)
    {
        EntitySetName = "MaxLvNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        MaxExpansionDepth = EntitySetDefaults.MaxExpansionDepthCeiling;
        GetQueryable = () => db.MaxLvNodes.AsQueryable();
        HasMany(x => x.Kids);
    }
}
