using System;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// B3, verified end-to-end against a real (in-process) OhData server rather than just at the
/// <c>FilterTranslator</c> unit level: prior to the fix, <c>x.CreatedAt &gt; DateTime.Now</c>
/// (<see cref="DateTimeKind.Local"/>) and any <see cref="DateTimeKind.Unspecified"/> comparison
/// emitted an offset-less literal that the Microsoft URI parser embedded in
/// <c>OhDataEndpointFactory</c> rejects with 400 — the exact "bread-and-butter" repro from the
/// adversarial review. These tests fail with an <see cref="ODataClientException"/> (400) on the
/// pre-fix translator and pass on the fixed one.
/// </summary>
public class DateTimeFilterLiveServerTests : IAsyncDisposable
{
    private readonly TemporalClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public DateTimeFilterLiveServerTests()
    {
        _fixture = TemporalClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Filter_DateTimeKindUtc_ServerAccepts()
    {
        var results = await Client.For<TemporalWidget>("TemporalWidgets")
            .Filter(x => x.CreatedAt > new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .ToListAsync();

        Assert.Contains(results, w => w.Name == "New");
    }

    [Fact]
    public async Task Filter_DateTimeKindLocal_ServerAccepts()
    {
        // The review's exact repro shape: comparing against a Kind=Local value (the Kind of
        // DateTime.Now). Regression: previously emitted an offset-less literal → server 400.
        // Uses a fixed anchor (rather than DateTime.Now itself) so the assertion is
        // deterministic instead of racing the "New" fixture row's UtcNow timestamp.
        var anchor = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local);

        var results = await Client.For<TemporalWidget>("TemporalWidgets")
            .Filter(x => x.CreatedAt > anchor)
            .ToListAsync();

        Assert.Contains(results, w => w.Name == "New");
        Assert.DoesNotContain(results, w => w.Name == "Old");
    }

    [Fact]
    public async Task Filter_DateTimeKindUnspecified_ServerAccepts()
    {
        var anchor = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        var results = await Client.For<TemporalWidget>("TemporalWidgets")
            .Filter(x => x.CreatedAt > anchor)
            .ToListAsync();

        Assert.Contains(results, w => w.Name == "New");
        Assert.DoesNotContain(results, w => w.Name == "Old");
    }
}
