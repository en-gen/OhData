using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// SPIKE #867 -- prototype only. Buffered vs streamed FULL response bytes, headers included, except
// Date / Content-Length / Transfer-Encoding (a framing difference is a finding, recorded in
// spike/NOTES.md, not a mismatch). Every "streamed" leg also asserts the streamed path actually
// ran (StreamedResponseCount), so a test cannot pass vacuously through the buffered fallback.

public sealed class SpItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string? Category { get; set; }
}

internal static class SpikeData
{
    internal static List<SpItem> Rows(int n) => Enumerable.Range(1, n)
        .Select(i => new SpItem { Id = i, Name = $"Item {i} \"q\" <&> é", Price = i * 1.25m, Category = i % 3 == 0 ? null : $"c{i % 3}" })
        .ToList();
}

/// <summary>Unbounded in-memory GetQueryable profile (no MaxTop): the plain-page shape.</summary>
internal sealed class SpikeAllProfile : EntitySetProfile<int, SpItem>
{
    public SpikeAllProfile() : base(x => x.Id)
    {
        EntitySetName = "SpikeAll";
        SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        var rows = SpikeData.Rows(25);
        GetQueryable = () => rows.AsQueryable();
    }
}

/// <summary>Paged in-memory GetQueryable profile: MaxTop 10 over 25 rows.</summary>
internal sealed class SpikePagedProfile : EntitySetProfile<int, SpItem>
{
    public SpikePagedProfile() : base(x => x.Id)
    {
        EntitySetName = "SpikePaged";
        SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        MaxTop = 10;
        var rows = SpikeData.Rows(25);
        GetQueryable = () => rows.AsQueryable();
    }
}

/// <summary>Priority-1 profile that reports its own TotalCount.</summary>
internal sealed class SpikeP1Profile : ODataEntitySetProfile<int, SpItem>
{
    public SpikeP1Profile() : base(x => x.Id)
    {
        EntitySetName = "SpikeP1";
        SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        MaxTop = 10;
        var rows = SpikeData.Rows(25);
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<SpItem>
        {
            Items = (IQueryable<SpItem>)options.ApplyTo(rows.AsQueryable()),
            TotalCount = rows.Count,
        });
    }
}

/// <summary>Priority-1 profile with NO TotalCount: $count=true needs the row total.</summary>
internal sealed class SpikeP1NoCountProfile : ODataEntitySetProfile<int, SpItem>
{
    public SpikeP1NoCountProfile() : base(x => x.Id)
    {
        EntitySetName = "SpikeP1NoCount";
        SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        var rows = SpikeData.Rows(25);
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<SpItem>
        {
            Items = (IQueryable<SpItem>)options.ApplyTo(rows.AsQueryable()),
        });
    }
}

internal sealed record SpikeWire(int Status, string Headers, byte[] Body)
{
    internal string Text => Encoding.UTF8.GetString(Body);
}

internal static class SpikeWireClient
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "Content-Length", "Transfer-Encoding",
    };

    internal static OhDataRegistration Registration(TestFixture fx) =>
        fx.App.Services.GetRequiredService<OhDataRegistrationCollection>().Default;

    internal static async Task<SpikeWire> GetAsync(TestFixture fx, string url)
    {
        using var resp = await fx.Client.GetAsync(url);
        var headers = resp.Headers.Concat(resp.Content.Headers)
            .Where(h => !Ignored.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
        return new SpikeWire((int)resp.StatusCode, string.Join("\n", headers), await resp.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Framing headers the comparison ignores, for the NOTES.md finding.</summary>
    internal static async Task<string> FramingAsync(TestFixture fx, string url)
    {
        using var resp = await fx.Client.GetAsync(url);
        return $"Content-Length={resp.Content.Headers.ContentLength?.ToString() ?? "(none)"} " +
               $"Transfer-Encoding={string.Join(",", resp.Headers.TransferEncoding)}";
    }

    /// <summary>Fetches buffered, then streamed; returns both plus how many responses were streamed.</summary>
    internal static async Task<(SpikeWire Buffered, SpikeWire Streamed, long StreamedCount)> BothAsync(
        TestFixture fx, string url, int chunkSize = 128)
    {
        OhDataRegistration reg = Registration(fx);
        reg.StreamCollections = false;
        SpikeWire buffered = await GetAsync(fx, url);

        reg.StreamCollections = true;
        reg.StreamChunkSize = chunkSize;
        long before = System.Threading.Interlocked.Read(ref reg.StreamedResponseCount);
        SpikeWire streamed = await GetAsync(fx, url);
        long streamedCount = System.Threading.Interlocked.Read(ref reg.StreamedResponseCount) - before;
        return (buffered, streamed, streamedCount);
    }

    internal static void AssertIdentical(SpikeWire buffered, SpikeWire streamed)
    {
        Assert.Equal(buffered.Status, streamed.Status);
        Assert.Equal(buffered.Headers, streamed.Headers);
        Assert.Equal(buffered.Text, streamed.Text);
        Assert.True(buffered.Body.AsSpan().SequenceEqual(streamed.Body), "response bytes differ");
    }

    /// <summary>Order-insensitive structural equality of the top-level members (values compared as raw JSON).</summary>
    internal static void AssertSemanticallyEqual(SpikeWire buffered, SpikeWire streamed)
    {
        using var b = JsonDocument.Parse(buffered.Body);
        using var s = JsonDocument.Parse(streamed.Body);
        var bm = b.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
        var sm = s.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
        Assert.Equal(bm.OrderBy(p => p.Key).ToList(), sm.OrderBy(p => p.Key).ToList());
    }
}

public class StreamingSpikeByteIdentityTests
{
    public static IEnumerable<object[]> PlainShapes() => new[]
    {
        new object[] { "plain page", "/odata/SpikeAll" },
        new object[] { "$count=true", "/odata/SpikeAll?$count=true" },
        new object[] { "$top/$skip no nextLink", "/odata/SpikeAll?$top=5&$skip=3" },
        new object[] { "$select", "/odata/SpikeAll?$select=Name,Price" },
        new object[] { "$select+$count", "/odata/SpikeAll?$select=Name&$count=true" },
        new object[] { "$orderby", "/odata/SpikeAll?$orderby=Price desc" },
        new object[] { "$filter", "/odata/SpikeAll?$filter=Id gt 5 and Id lt 12" },
        new object[] { "$filter+$orderby+$count", "/odata/SpikeAll?$filter=Id gt 5&$orderby=Name&$count=true" },
        new object[] { "empty page", "/odata/SpikeAll?$filter=Id gt 1000" },
        new object[] { "empty page $count", "/odata/SpikeAll?$filter=Id gt 1000&$count=true" },
    };

    public static IEnumerable<object[]> PlainShapesAllChunkSizes() =>
        from shape in PlainShapes()
        from chunk in new[] { 128, 4, 1 }
        select new object[] { shape[0], shape[1], chunk };

    [Theory]
    [MemberData(nameof(PlainShapesAllChunkSizes))]
    public async Task GetQueryable_NoNextLink_ByteIdentical(string shape, string url, int chunk)
    {
        _ = shape;
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SpikeAllProfile>());
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, url, chunk);

        Assert.Equal(1, count);
        Assert.Equal(200, streamed.Status);
        SpikeWireClient.AssertIdentical(buffered, streamed);
    }

    [Theory]
    [InlineData("server-driven page (MaxTop)", "/odata/SpikePaged")]
    [InlineData("$skip + server page", "/odata/SpikePaged?$skip=3")]
    [InlineData("$count + server page + $select", "/odata/SpikePaged?$count=true&$select=Name")]
    [InlineData("last page (no link)", "/odata/SpikePaged?$skip=20")]
    [InlineData("exact multiple, no spurious link", "/odata/SpikePaged?$skip=15")]
    [InlineData("$top within MaxTop", "/odata/SpikePaged?$top=4&$skip=2")]
    public async Task GetQueryable_NextLink_PageFitsFirstChunk_ByteIdentical(string shape, string url)
    {
        _ = shape;
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SpikePagedProfile>());
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, url, chunkSize: 128);

        Assert.Equal(1, count);
        SpikeWireClient.AssertIdentical(buffered, streamed);
    }

    // EXPECTED BLOCKED (finding, not a weakening): once the page spans more than one chunk, the
    // has-more probe row is only reached AFTER the first rows are on the wire, so nextLink cannot
    // precede "value" and the member order differs from the buffered envelope (context, count,
    // nextLink, value). The JSON is semantically equal; the bytes are not. Everything else (count,
    // context, rows, headers) is identical.
    [Theory]
    [InlineData("/odata/SpikePaged")]
    [InlineData("/odata/SpikePaged?$count=true&$select=Name")]
    public async Task GetQueryable_NextLink_PageSpansChunks_BytesDifferOnlyInMemberOrder(string url)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SpikePagedProfile>());
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, url, chunkSize: 4);

        Assert.Equal(1, count);
        Assert.Equal(buffered.Status, streamed.Status);
        Assert.Equal(buffered.Headers, streamed.Headers);
        Assert.NotEqual(buffered.Text, streamed.Text);
        SpikeWireClient.AssertSemanticallyEqual(buffered, streamed);
        Assert.Matches("\"value\":\\[.*\\],\"@odata\\.nextLink\":", streamed.Text);
        Assert.Matches("\"@odata\\.nextLink\":.*\"value\":\\[", buffered.Text);
    }

    [Theory]
    [InlineData("/odata/SpikeP1")]
    [InlineData("/odata/SpikeP1?$count=true")]
    [InlineData("/odata/SpikeP1?$top=4&$count=true")]
    [InlineData("/odata/SpikeP1?$filter=Id gt 3&$orderby=Id desc")]
    [InlineData("/odata/SpikeP1?$filter=Id gt 1000")]
    [InlineData("/odata/SpikeP1?$skip=20")]
    public async Task Priority1_ByteIdentical(string url)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SpikeP1Profile>());
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, url);

        Assert.True(count == 1, $"streamed={count} status={streamed.Status} body={streamed.Text}");
        SpikeWireClient.AssertIdentical(buffered, streamed);
    }

    // EXPECTED BLOCKED: a Priority-1 $count=true with no profile TotalCount reports the ROW TOTAL,
    // which is unknowable before the last row. The prototype refuses to stream and the buffered
    // path answers (count == 0 streamed responses). Bytes are identical because it IS the buffered path.
    [Fact]
    public async Task Priority1_CountWithoutTotalCount_BlockedFallsBackToBuffered()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SpikeP1NoCountProfile>());
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, "/odata/SpikeP1NoCount?$count=true");

        Assert.Equal(0, count);
        SpikeWireClient.AssertIdentical(buffered, streamed);
        Assert.Contains("\"@odata.count\":25", streamed.Text);

        // The same profile without $count streams fine.
        var (b2, s2, c2) = await SpikeWireClient.BothAsync(fx, "/odata/SpikeP1NoCount");
        Assert.Equal(1, c2);
        SpikeWireClient.AssertIdentical(b2, s2);
    }

    // EXPECTED BLOCKED: any $expand keeps the buffered path (batch-aware ExpandLevelAsync, pushdown
    // projection/carrier counts, ShapePushedExpandsInJson are all whole-page). Bytes are identical
    // because the buffered path answered; zero streamed responses proves it.
    [Theory]
    [InlineData("/odata/BeAuthors?$expand=Books")]
    [InlineData("/odata/BeAuthors?$expand=Books($top=2;$count=true)&$count=true")]
    public async Task Expand_BlockedFallsBackToBuffered(string url)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var fx = await BareExpandSqliteHarness.BuildAsync(connection, sink: null);
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, url);

        Assert.Equal(0, count);
        SpikeWireClient.AssertIdentical(buffered, streamed);
    }

    // The same EF-backed profile with no $expand DOES stream, byte-identically (EF SQLite).
    [Theory]
    [InlineData("/odata/BeAuthors")]
    [InlineData("/odata/BeAuthors?$select=Name")]
    [InlineData("/odata/BeAuthors?$orderby=Name desc&$filter=Id gt 0")]
    public async Task EfSqlite_NoExpand_ByteIdentical(string url)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var fx = await BareExpandSqliteHarness.BuildAsync(connection, sink: null);
        var (buffered, streamed, count) = await SpikeWireClient.BothAsync(fx, url);

        Assert.True(count == 1, $"streamed={count} status={streamed.Status} body={streamed.Text}");
        SpikeWireClient.AssertIdentical(buffered, streamed);
    }

    // Finding: framing. Buffered Results.Json vs streamed -- recorded, not asserted equal.
    [Fact]
    public async Task Framing_IsRecorded()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SpikeAllProfile>());
        OhDataRegistration reg = SpikeWireClient.Registration(fx);
        reg.StreamCollections = false;
        string buffered = await SpikeWireClient.FramingAsync(fx, "/odata/SpikeAll");
        reg.StreamCollections = true;
        string streamed = await SpikeWireClient.FramingAsync(fx, "/odata/SpikeAll");
        Console.WriteLine($"FRAMING buffered: {buffered} | streamed: {streamed}");
        Assert.NotNull(buffered);
        Assert.NotNull(streamed);
    }
}
