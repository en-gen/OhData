using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class S378Thing { public int Id { get; set; } public string Source { get; set; } = ""; }

public sealed class S378DualProfile : EntitySetProfile<int, S378Thing>
{
    public static int GetAllCalls;

    public S378DualProfile() : base(x => x.Id)
    {
        EntitySetName = "S378Duals";
        FilterEnabled = true; CountEnabled = true;
        GetAll = (CancellationToken _) =>
        {
            Interlocked.Increment(ref GetAllCalls);
            return Task.FromResult<IEnumerable<S378Thing>>(
                new[] { new S378Thing { Id = 1, Source = "FROM-GETALL" } });
        };
        GetQueryable = (CancellationToken _) => Task.FromResult(
            new[] { new S378Thing { Id = 2, Source = "FROM-GETQUERYABLE" } }.AsQueryable());
    }
}

public sealed class S378GetAllOnlyProfile : EntitySetProfile<int, S378Thing>
{
    public S378GetAllOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "S378GetAllOnly";
        GetAll = (CancellationToken _) => Task.FromResult<IEnumerable<S378Thing>>(
            new[] { new S378Thing { Id = 1, Source = "FROM-GETALL" } });
    }
}

public sealed class S378QueryableOnlyProfile : EntitySetProfile<int, S378Thing>
{
    public S378QueryableOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "S378QueryableOnly";
        GetQueryable = (CancellationToken _) => Task.FromResult(
            new[] { new S378Thing { Id = 2, Source = "FROM-GETQUERYABLE" } }.AsQueryable());
    }
}

/// <summary>
/// #378 — a collection handler shadowed by a higher-precedence one is dead, and now says so.
/// <para>
/// Measured before the change, with a profile setting both: <c>GetAll</c> was invoked <b>zero</b>
/// times on the collection GET, on <c>/$count</c>, and on <c>/$count</c>'s <c>$filter</c> fallback,
/// and no OhData line was emitted at any level including <c>Trace</c>.
/// </para>
/// </summary>
public sealed class Issue378ShadowedHandlerWarningTests
{
    private static IEnumerable<string> ShadowWarnings(WarningCapture capture) =>
        capture.Warnings.Where(w => w.Contains("takes precedence", System.StringComparison.Ordinal));

    private static Task<TestFixture> BuildAsync(WarningCapture capture, System.Action<OhDataBuilder> configure) =>
        TestHostBuilder.BuildAsync(configure,
            configureServices: sv => sv.AddLogging(lb =>
            {
                lb.SetMinimumLevel(LogLevel.Debug);
                lb.AddProvider(capture);
            }));

    [Fact]
    public async Task BothSet_WarnsOnce_NamingTheWinnerAndTheDeadHandler()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(capture, o => o.AddEntitySetProfile<S378DualProfile>());

        string warning = Assert.Single(ShadowWarnings(capture));
        Assert.Contains("S378Duals", warning, System.StringComparison.Ordinal);
        Assert.Contains("GetAll", warning, System.StringComparison.Ordinal);
        Assert.Contains("GetQueryable", warning, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothSet_TheWarningIsTrue_GetAllIsNeverInvoked()
    {
        // The warning claims GetAll is never invoked. Assert the claim, not just the text --
        // including on /$count, whose GetAll fallback is the one route where it might have been.
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(capture, o => o.AddEntitySetProfile<S378DualProfile>());
        S378DualProfile.GetAllCalls = 0;

        foreach (string url in new[]
                 {
                     "/odata/S378Duals", "/odata/S378Duals?$count=true",
                     "/odata/S378Duals/$count", "/odata/S378Duals/$count?$filter=Id gt 0",
                 })
        {
            var resp = await fx.Client.GetAsync(url);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            Assert.DoesNotContain("FROM-GETALL", await resp.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        }

        Assert.Equal(0, S378DualProfile.GetAllCalls);
    }

    [Theory]
    [InlineData(typeof(S378GetAllOnlyProfile))]
    [InlineData(typeof(S378QueryableOnlyProfile))]
    public async Task OneHandlerOnly_IsSilent(System.Type profileType)
    {
        // The control that matters: a warning firing on a correct configuration is the failure mode
        // #440 and #481 both establish as worse than no warning.
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(capture, o =>
        {
            if (profileType == typeof(S378GetAllOnlyProfile)) o.AddEntitySetProfile<S378GetAllOnlyProfile>();
            else o.AddEntitySetProfile<S378QueryableOnlyProfile>();
        });

        Assert.Empty(ShadowWarnings(capture));
    }
}
