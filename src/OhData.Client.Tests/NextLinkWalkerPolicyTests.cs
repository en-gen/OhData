using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// #460. The <c>@odata.nextLink</c> walker followed whatever URL a response <em>body</em> named,
/// with the <see cref="HttpClient"/>'s <see cref="HttpClient.DefaultRequestHeaders"/> —
/// <c>Authorization</c> among them — attached, and with no cap on iterations.
/// <list type="bullet">
///   <item><b>Credential exposure.</b> Building a fresh request for a body-named host is not a
///   redirect, so <see cref="HttpClientHandler"/>'s cross-origin credential stripping never runs.
///   A response-body injection exfiltrates the bearer token.</item>
///   <item><b>No termination guarantee.</b> A server echoing the same link forever drives
///   <c>ToAsyncEnumerable</c>/<c>ToListAsync</c> unboundedly; <c>ToListAsync</c> accumulates until
///   the process runs out of memory.</item>
/// </list>
/// <para>
/// These run against a scripted <see cref="HttpMessageHandler"/> rather than a live server on
/// purpose: the defect is about a link the <em>server</em> chose, and no real OhData server will
/// emit a hostile one. The handler is also the only vantage point from which "the foreign host
/// never saw the token" is directly observable.
/// </para>
/// </summary>
public class NextLinkWalkerPolicyTests
{
    private const string BaseAddress = "https://api.example.com/v1/";
    private const string Token = "SECRET-TOKEN";

    // ── Scripted handler ────────────────────────────────────────────────────────

    private sealed record Seen(Uri Url, string? Authorization);

    /// <summary>
    /// Answers every request with a body chosen by <paramref name="script"/>, recording the URL and
    /// the <c>Authorization</c> header actually put on the wire.
    /// </summary>
    private sealed class ScriptedHandler(Func<Uri, int, string> script) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Seen(request.RequestUri!, request.Headers.Authorization?.ToString()));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(script(request.RequestUri!, Requests.Count - 1), Encoding.UTF8, "application/json"),
            };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (HttpResponseMessage response in _responses) response.Dispose();
                _responses.Clear();
            }
            base.Dispose(disposing);
        }
    }

    private static string Page(int id, string? nextLink) =>
        nextLink is null
            ? $$"""{"value":[{"Id":{{id}},"Name":"W{{id}}"}]}"""
            : $$"""{"value":[{"Id":{{id}},"Name":"W{{id}}"}],"@odata.nextLink":"{{nextLink}}"}""";

    private static (OhDataClient Client, ScriptedHandler Handler, HttpClient Http) Build(
        Func<Uri, int, string> script, OhDataClientOptions? options = null)
    {
        var handler = new ScriptedHandler(script);
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return (new OhDataClient(http, options), handler, http);
    }

    // ── Termination guarantee ───────────────────────────────────────────────────

    [Fact]
    public async Task SameLinkForever_StopsAtTheHopCap()
    {
        // The exact shape from the issue: the server never stops handing back a nextLink.
        var (client, handler, http) = Build(
            (_, i) => Page(i, BaseAddress + "Widgets?$skip=2"),
            new OhDataClientOptions { MaxNextLinkHops = 3 });

        using (client)
        using (http)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.For<PolicyWidget>().ToListAsync());

            Assert.Contains("nextLink", ex.Message, StringComparison.Ordinal);
            Assert.Contains("3 hops", ex.Message, StringComparison.Ordinal);

            // The cap counts HOPS, so the first page plus three followed links = four requests.
            // Pinning the count is what proves the loop actually stopped rather than the assertion
            // merely observing an exception from somewhere else.
            Assert.Equal(4, handler.Requests.Count);
        }
    }

    [Fact]
    public async Task SameLinkForever_StopsAtTheHopCap_OnTheAnnotatedWalker()
    {
        // ToAnnotatedAsyncEnumerable is a second, independent loop over the same envelope field.
        var (client, handler, http) = Build(
            (_, i) => Page(i, BaseAddress + "Widgets?$skip=2"),
            new OhDataClientOptions { MaxNextLinkHops = 2 });

        using (client)
        using (http)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (ODataAnnotatedEntity<PolicyWidget> _ in
                    client.For<PolicyWidget>().ToAnnotatedAsyncEnumerable())
                {
                    // drain
                }
            });

            Assert.Contains("2 hops", ex.Message, StringComparison.Ordinal);
            Assert.Equal(3, handler.Requests.Count);
        }
    }

    [Fact]
    public void MaxNextLinkHops_RejectsANonPositiveCap() =>
        // A cap of 0 would mean "never follow a link", which is not a paging policy anyone wants
        // silently; a negative one is meaningless. Fail at configuration time, not mid-enumeration.
        Assert.Throws<ArgumentOutOfRangeException>(() => new OhDataClientOptions { MaxNextLinkHops = 0 });

    [Fact]
    public async Task DefaultCap_IsNotReachedByAnOrdinaryPagingRun()
    {
        // Guard against a cap so tight it breaks real paging: three pages must sail through on the
        // shipped default with nothing configured.
        var (client, handler, http) = Build((_, i) =>
            i < 2 ? Page(i, $"{BaseAddress}Widgets?$skip={i + 1}") : Page(i, null));

        using (client)
        using (http)
        {
            List<PolicyWidget> items = await client.For<PolicyWidget>().ToListAsync();

            Assert.Equal(3, items.Count);
            Assert.Equal(3, handler.Requests.Count);
        }
    }

    // ── Same-origin default: what still works ───────────────────────────────────

    [Fact]
    public async Task RelativeNextLink_StillResolvesAgainstTheBaseAddress()
    {
        // Pre-existing, correct behaviour: a relative link is resolved by HttpClient against
        // BaseAddress, which makes it same-origin by construction. The origin check must not
        // mistake "no authority to compare" for "a different authority".
        var (client, handler, http) = Build((_, i) =>
            i == 0 ? Page(0, "Widgets?$skip=1") : Page(1, null));

        using (client)
        using (http)
        {
            List<PolicyWidget> items = await client.For<PolicyWidget>().ToListAsync();

            Assert.Equal(2, items.Count);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("https://api.example.com/v1/Widgets?$skip=1", handler.Requests[1].Url.ToString());
        }
    }

    [Fact]
    public async Task AbsoluteSameOriginNextLink_IsFollowed()
    {
        // Same origin, different path and port omitted vs. the base address's implicit 443.
        var (client, handler, http) = Build((_, i) =>
            i == 0 ? Page(0, "https://api.example.com/v1/Widgets?$skip=1") : Page(1, null));

        using (client)
        using (http)
        {
            List<PolicyWidget> items = await client.For<PolicyWidget>().ToListAsync();

            Assert.Equal(2, items.Count);
            Assert.Equal(2, handler.Requests.Count);
        }
    }

    // ── Same-origin default: what it refuses ────────────────────────────────────

    [Fact]
    public async Task CrossHostNextLink_IsRefused_AndTheTokenNeverLeaves()
    {
        var (client, handler, http) = Build((_, i) =>
            i == 0 ? Page(0, "https://evil.example.org/steal") : Page(1, null));

        using (client)
        using (http)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.For<PolicyWidget>().ToListAsync());

            Assert.Contains("evil.example.org", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(OhDataClientOptions.FollowCrossOriginNextLinks), ex.Message, StringComparison.Ordinal);

            // THE assertion for this issue. Not "an exception was thrown" — no request reached the
            // foreign host at all, so the bearer token could not have been attached to one.
            Assert.Single(handler.Requests);
            Assert.DoesNotContain(handler.Requests, r => r.Url.Host == "evil.example.org");
            Assert.DoesNotContain(handler.Requests, r => r.Url.Host != "api.example.com");
        }
    }

    [Fact]
    public async Task CrossHostNextLink_IsRefused_OnTheAnnotatedWalkerToo()
    {
        var (client, handler, http) = Build((_, i) =>
            i == 0 ? Page(0, "https://evil.example.org/steal") : Page(1, null));

        using (client)
        using (http)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (ODataAnnotatedEntity<PolicyWidget> _ in
                    client.For<PolicyWidget>().ToAnnotatedAsyncEnumerable())
                {
                    // drain
                }
            });

            Assert.Single(handler.Requests);
            Assert.DoesNotContain(handler.Requests, r => r.Url.Host == "evil.example.org");
        }
    }

    [Theory]
    // Same host, different scheme: a downgrade to plaintext puts the token on the wire in the clear.
    [InlineData("http://api.example.com/v1/Widgets")]
    // Same host and scheme, different port: a different service on the same machine.
    [InlineData("https://api.example.com:8443/v1/Widgets")]
    // A sibling subdomain is a different origin under RFC 6454, and a different trust boundary.
    [InlineData("https://internal.example.com/v1/Widgets")]
    // A host that merely has the base address as a suffix — the classic prefix/suffix-match bypass.
    [InlineData("https://api.example.com.evil.org/v1/Widgets")]
    public async Task EachOriginComponent_IsPartOfTheComparison(string hostileLink)
    {
        var (client, handler, http) = Build((_, i) => i == 0 ? Page(0, hostileLink) : Page(1, null));

        using (client)
        using (http)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.For<PolicyWidget>().ToListAsync());

            Assert.Single(handler.Requests);
        }
    }

    // ── The opt-in ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CrossHostNextLink_IsFollowedWhenExplicitlyOptedIn()
    {
        var (client, handler, http) = Build(
            (url, i) => i == 0 ? Page(0, "https://other.example.org/v1/Widgets") : Page(1, null),
            new OhDataClientOptions { FollowCrossOriginNextLinks = true });

        using (client)
        using (http)
        {
            List<PolicyWidget> items = await client.For<PolicyWidget>().ToListAsync();

            Assert.Equal(2, items.Count);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("other.example.org", handler.Requests[1].Url.Host);

            // Pinned deliberately, and it is the reason the opt-in is opt-IN: HttpClient attaches
            // DefaultRequestHeaders to every request regardless of host, so turning this on really
            // does send the credential to the foreign origin. Anyone changing the flag's meaning
            // has to change this assertion and see what it says.
            Assert.Equal($"Bearer {Token}", handler.Requests[1].Authorization);
        }
    }

    [Fact]
    public async Task OptIn_DoesNotDisableTheHopCap()
    {
        // The two halves of #460 are independent: trusting a service's origins says nothing about
        // whether its paging terminates.
        var (client, handler, http) = Build(
            (_, i) => Page(i, "https://other.example.org/v1/Widgets"),
            new OhDataClientOptions { FollowCrossOriginNextLinks = true, MaxNextLinkHops = 2 });

        using (client)
        using (http)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.For<PolicyWidget>().ToListAsync());

            Assert.Equal(3, handler.Requests.Count);
        }
    }
}

[ODataEntitySet("Widgets")]
internal sealed class PolicyWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
