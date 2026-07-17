using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// NEW-1, verified end-to-end against a real (in-process) OhData server rather than just at
/// the <c>FilterTranslator</c> unit level: the client's Any/All <c>$it</c> translation from
/// PR #140 (B4) emits a spec-correct nav-path filter (e.g.
/// <c>Tags/any(t: t/Name eq 'Red')</c>), but the server rejected every nav-path filter with
/// 400 once <c>ValidatePropertyAllowlists</c> started calling <c>Validate()</c> unconditionally
/// (#141) -- nav-target types (Tag) carried no model-bound annotation of their own, so
/// Microsoft's validator treated every property on them as NotFilterable by default. These
/// tests fail with an <see cref="ODataClientException"/> (400) on the pre-fix server and pass
/// once nav-target types are marked fully permissive.
/// </summary>
public class NavPathFilterLiveServerTests : IAsyncDisposable
{
    private readonly TaggedItemClientTestFixture _fixture;
    private OhDataClient Client => _fixture.Client;

    public NavPathFilterLiveServerTests()
    {
        _fixture = TaggedItemClientTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Filter_NavPathAny_ServerAccepts()
    {
        var results = await Client.For<TaggedItem>("TaggedItems")
            .Filter(x => x.Tags.Any(t => t.Name == "Red"))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Foo", results[0].Name);
    }

    [Fact]
    public async Task Filter_NavPathAny_OuterParameterScoped_ServerAccepts()
    {
        // The B4 $it fix: the outer range variable (x) inside the Any lambda must resolve to
        // $it/Name, not silently drop to null. No item's own Name matches its own tag's Name
        // here, so this must come back empty rather than erroring.
        var results = await Client.For<TaggedItem>("TaggedItems")
            .Filter(x => x.Tags.Any(t => t.Name == x.Name))
            .ToListAsync();

        Assert.Empty(results);
    }
}
