using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #537: an earlier, refuted attempt at this issue (a comment documenting the cache as bounded)
/// shipped a test that only sent names FAILING <c>ApplyNavOrderBy</c>'s <c>IsKnownEdmName</c> gate —
/// which passed while the documented invariant was false. The real defect: <c>IsKnownEdmName</c>
/// bounded which PROPERTIES could pass, but <c>FindClrPropertyByEdmName</c>'s cache was keyed on the
/// caller's raw, unnormalized <c>$orderby</c> token, so the gate never bounded which STRINGS reached
/// the cache as keys. Measured pre-fix: 256 case spellings of one 8-letter property name produced 256
/// permanent cache entries, and a single request with 200 comma-separated spellings added 200 more.
///
/// The fix (see <c>ODataPropertyNaming</c>) keys the cache by <c>Type</c> alone, so no client-supplied
/// string is ever part of a cache key for ANY of its 13 production callers, not only this one — the
/// tests below pin both directions: the path that must now share one entry regardless of casing, and
/// the rejected-name path that must still answer 400 (kept from the refuted branch, since it was fine
/// as far as it went).
/// </summary>
public class Issue537MemoCacheInvariantTests
{
    /// <summary>
    /// THE ACCEPTED PATH — the one the refuted comment-only fix never exercised. "Category" is an
    /// 8-letter real property on <see cref="NavOrderChild"/>, giving 2^8 = 256 distinct case
    /// spellings, matching the issue's own measurement exactly. All 256 are sent comma-joined in ONE
    /// request (the shape of the issue's second measurement: "ONE request ... +200 cache entries").
    /// </summary>
    [Fact]
    public async Task NavOrderBy_ManyCaseSpellingsOfARealProperty_AddOneCacheEntryNotN()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavOrderByProfile>());

        // Warm the route once first so any one-time, legitimate caching for NavOrderChild has
        // already happened before the baseline below — the assertion is about what 256 DIFFERENTLY
        // CASED requests for the SAME property add on top of that, not about the first lookup ever.
        HttpResponseMessage warm = await fx.Client.GetAsync(
            "/odata/NavOrderByParents(1)/Children?$orderby=Category");
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);

        int before = TotalCacheEntryCount();

        string[] spellings = AllCaseSpellings("Category").ToArray();
        Assert.Equal(256, spellings.Length); // 2^8 — sanity check against the issue's own measurement
        string orderBy = string.Join(",", spellings);

        HttpResponseMessage response = await fx.Client.GetAsync(
            $"/odata/NavOrderByParents(1)/Children?$orderby={Uri.EscapeDataString(orderBy)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        int after = TotalCacheEntryCount();

        // Pre-#537 this delta was 256 (one new (Type, string) entry per case spelling — reproduced by
        // temporarily reverting ODataPropertyNaming.cs; see the PR description for the exact number).
        // Post-#537 the cache is keyed by Type alone, so 256 differently cased requests for one
        // property can add at most the handful of entries a single request legitimately touches —
        // never one per spelling.
        Assert.True(after - before <= 5,
            $"expected at most a handful of new cache entries, got {after - before} (before={before}, after={after})");
    }

    /// <summary>
    /// The rejected-name path, kept from the refuted branch's attempt at this issue (it was fine as
    /// far as it went — it just wasn't the whole invariant). An unmatched name still answers 400, and
    /// — now trivially, since no cache in <c>ODataPropertyNaming</c> stores a string-shaped key at all
    /// — can never leave a trace of the client's text behind in any of its caches.
    /// </summary>
    [Fact]
    public async Task NavOrderBy_UnmatchedPropertyNames_NeverAppearInAnyCache()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavOrderByProfile>());

        string marker = "Zq537" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < 25; i++)
        {
            HttpResponseMessage response = await fx.Client.GetAsync(
                $"/odata/NavOrderByParents(1)/Children?$orderby={marker}{i}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty(CacheKeyStringsContaining(marker));
    }

    private static IEnumerable<string> AllCaseSpellings(string word)
    {
        int n = word.Length;
        for (int mask = 0; mask < (1 << n); mask++)
        {
            char[] chars = new char[n];
            for (int i = 0; i < n; i++)
            {
                bool upper = (mask & (1 << i)) != 0;
                chars[i] = upper ? char.ToUpperInvariant(word[i]) : char.ToLowerInvariant(word[i]);
            }
            yield return new string(chars);
        }
    }

    /// <summary>
    /// Sums entries across every private static <c>ConcurrentDictionary&lt;,&gt;</c>-shaped field on
    /// <c>ODataPropertyNaming</c>. Deliberately does not name a field, so this stays meaningful
    /// whatever the cache is keyed by.
    /// </summary>
    private static int TotalCacheEntryCount()
    {
        Type naming = typeof(OhDataRegistration).Assembly.GetType("OhData.ODataPropertyNaming")!;
        int total = 0;
        foreach (FieldInfo field in naming.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!field.FieldType.IsGenericType
                || field.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Concurrent.ConcurrentDictionary<,>))
            {
                continue;
            }
            if (field.GetValue(null) is not IEnumerable cache) continue;
            foreach (object? _ in cache) total++;
        }
        return total;
    }

    /// <summary>
    /// Reports any cache KEY (or, for a tuple key, any string component of it — the shape the cache
    /// had before #537) whose string content contains <paramref name="marker"/>. Shared shape with
    /// <c>WriteBodyContractTests.NameCacheStringsContaining</c>.
    /// </summary>
    private static List<string> CacheKeyStringsContaining(string marker)
    {
        Type naming = typeof(OhDataRegistration).Assembly.GetType("OhData.ODataPropertyNaming")!;
        var hits = new List<string>();
        foreach (FieldInfo field in naming.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (field.GetValue(null) is not IEnumerable cache) continue;
            foreach (object? entry in cache)
            {
                if (entry is null) continue;
                object? key = entry.GetType().GetProperty("Key")?.GetValue(entry);
                CollectKeyStrings(key, marker, hits);
            }
        }
        return hits;
    }

    private static void CollectKeyStrings(object? key, string marker, List<string> hits)
    {
        switch (key)
        {
            case null:
                return;
            case string s:
                if (s.Contains(marker, StringComparison.Ordinal)) hits.Add(s);
                return;
            case Type:
                return; // a Type key can never equal a client-supplied marker string
        }

        Type t = key.GetType();
        if (t.Namespace == "System" && t.Name.StartsWith("ValueTuple", StringComparison.Ordinal))
        {
            foreach (FieldInfo f in t.GetFields())
            {
                CollectKeyStrings(f.GetValue(key), marker, hits);
            }
        }
    }
}
