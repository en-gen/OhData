using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #454 — whole-entity <c>PATCH</c> must not be able to move an entity's key.
///
/// The defect was that the guard and the delta loop consulted different sets: the guard read the
/// FIRST case-insensitive occurrence of the key property and stopped, while the loop applied EVERY
/// body property (resolved case-insensitively, and through <c>[JsonPropertyName]</c>) into a
/// last-writer-wins <c>Delta&lt;T&gt;</c>. A body whose first key occurrence matched the URL key
/// therefore passed validation and then had a later occurrence applied.
///
/// The fix makes both halves consult the same resolved set — the guard validates every occurrence
/// that resolves to the key CLR property, and the loop never puts the key into the delta at all.
/// A mismatch is REJECTED (400), matching the structural-property write route's
/// <c>KeyImmutableError</c> (<see cref="PropertyWriteTests"/>) and the pre-existing
/// single-occurrence 400.
/// </summary>
public class KeyImmutabilityTests
{
    private static HttpContent Json(string raw) =>
        new StringContent(raw, Encoding.UTF8, "application/json");

    // ── PATCH: duplicate / case-variant occurrences of the key ────────────────────

    [Fact]
    public async Task Patch_DuplicateKey_SameCase_LaterOccurrenceMismatches_Returns400()
    {
        // {"Id":1,"Id":999} — the first occurrence matches the URL key, the second does not.
        // Pre-fix: 200, and the stored key became 999.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)",
            Json("{\"Id\":1,\"Id\":999,\"name\":\"hacked\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("key", json.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Patch_DuplicateKey_CaseVariant_LaterOccurrenceMismatches_Returns400()
    {
        // {"id":1,"Id":999} — both spellings resolve to the same CLR key property.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)",
            Json("{\"id\":1,\"Id\":999,\"name\":\"hacked\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("key", json.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Patch_DuplicateKey_MismatchFirst_Returns400()
    {
        // Mirror image: the FIRST occurrence is wrong. This case already 400'd pre-fix (the guard
        // saw it) — pinned so the fix cannot regress it into an "only check the last one" hole.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)",
            Json("{\"id\":999,\"Id\":1,\"name\":\"hacked\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_KeyPresentAndMatching_Succeeds_AndKeyUnchanged()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)",
            Json("{\"id\":1,\"name\":\"Renamed\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("Id").GetInt32());
        Assert.Equal("Renamed", json.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Patch_DuplicateKey_BothMatching_Succeeds_AndKeyUnchanged()
    {
        // Every occurrence agrees with the URL key — nothing to reject, and the key is still not
        // written into the delta.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)",
            Json("{\"id\":1,\"Id\":1,\"name\":\"Renamed\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("Id").GetInt32());
        Assert.Equal("Renamed", json.GetProperty("Name").GetString());
    }

    // ── PATCH: the key reached under its renamed EDM name ─────────────────────────

    [Fact]
    public async Task Patch_RenamedKey_UnderEdmName_Mismatch_Returns400()
    {
        // The key carries [JsonPropertyName("code")]. Pre-fix the guard looked the key up by its
        // CLR name ("Key"), found nothing, and skipped validation entirely — while the delta loop
        // resolved "code" through the EDM name and applied it. A single occurrence was enough.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<KeyImmutableRenamedProfile>());
        var resp = await fx.Client.PatchAsync("/odata/KeyImmutableRenamed('A1')",
            Json("{\"code\":\"ZZ\",\"Name\":\"hacked\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("key", json.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Patch_RenamedKey_UnderEdmName_Matching_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<KeyImmutableRenamedProfile>());
        var resp = await fx.Client.PatchAsync("/odata/KeyImmutableRenamed('A1')",
            Json("{\"code\":\"A1\",\"Name\":\"Renamed\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A1", json.GetProperty("code").GetString());
        Assert.Equal("Renamed", json.GetProperty("Name").GetString());
    }

    // ── PUT is structurally immune — pin it ───────────────────────────────────────
    //
    // PUT deserializes the body to TModel FIRST and reads the key off the materialized model
    // (InvokeGetKeyString), so System.Text.Json has already collapsed duplicate/case-variant
    // occurrences to the single value the handler will receive: the guard and the handler cannot
    // disagree by construction. These two pin that property so a future refactor toward
    // raw-JsonElement parsing on PUT (which is what made PATCH vulnerable) cannot land silently.

    [Fact]
    public async Task Put_DuplicateKey_LastOccurrenceMismatches_Returns400()
    {
        // STJ collapses to Id=999 → the model the handler would receive disagrees with the URL.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PutAsync("/odata/Widgets(1)",
            Json("{\"id\":1,\"Id\":999,\"name\":\"hacked\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("key", json.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Put_DuplicateKey_LastOccurrenceMatches_Succeeds_AndKeyUnchanged()
    {
        // STJ collapses to Id=1 → agrees with the URL. The earlier hostile occurrence is gone
        // before anything compares or applies it, so this is a normal 200.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PutAsync("/odata/Widgets(1)",
            Json("{\"id\":999,\"Id\":1,\"name\":\"Replaced\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("Id").GetInt32());
        Assert.Equal("Replaced", json.GetProperty("Name").GetString());
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────

internal class KeyImmutableRenamedEntity
{
    [JsonPropertyName("code")]
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
}

internal class KeyImmutableRenamedProfile : EntitySetProfile<string, KeyImmutableRenamedEntity>
{
    private readonly List<KeyImmutableRenamedEntity> _store;

    public KeyImmutableRenamedProfile() : base(x => x.Key)
    {
        EntitySetName = "KeyImmutableRenamed";

        _store = new List<KeyImmutableRenamedEntity>
        {
            new() { Key = "A1", Name = "Alpha" },
            new() { Key = "B2", Name = "Beta" },
        };

        GetById = (k, ct) => Task.FromResult(_store.FirstOrDefault(e => e.Key == k));

        Patch = (k, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(e => e.Key == k);
            if (existing is null) return Task.FromResult<KeyImmutableRenamedEntity?>(null);
            delta.Patch(existing);
            return Task.FromResult<KeyImmutableRenamedEntity?>(existing);
        };
    }
}
