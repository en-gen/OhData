using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>In-memory store of domain entities for the delta-mapping e2e profile.</summary>
public sealed class DmStore
{
    private readonly Dictionary<int, DmEntity> _items = new();
    public void Seed(DmEntity e) => _items[e.Id] = e;
    public DmEntity? Get(int id) => _items.TryGetValue(id, out var e) ? e : null;
}

// A DTO-backed entity set whose PATCH handler translates the DTO delta into an entity delta via
// the injected IDeltaFactory, then applies + "persists" it. The framework never applies/persists.
public sealed class DmWidgetProfile : EntitySetProfile<int, DmDto>
{
    public DmWidgetProfile(DmStore store, IDeltaFactory deltas) : base(x => x.Id)
    {
        EntitySetName = "Widgets";

        GetById = (id, ct) => Task.FromResult(store.Get(id) is { } e ? ToDto(e) : null);

        Patch = (id, delta, ct) =>
        {
            DmEntity? entity = store.Get(id);
            if (entity is null) return Task.FromResult<DmDto?>(null);
            deltas.Create<DmDto, DmEntity>(delta).Patch(entity); // DTO-delta -> entity-delta -> apply
            return Task.FromResult<DmDto?>(ToDto(entity));
        };
    }

    private static DmDto ToDto(DmEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        StatusCode = e.StatusCode,
        Rank = e.Rank,
        Price = e.Price,
        Secret = e.Secret,
    };
}

public class DeltaMappingEndToEndTests
{
    [Fact]
    public async Task Patch_ThroughRealProfile_AppliesMappedChangesAndHonorsIgnoreAllowlist()
    {
        var store = new DmStore();
        store.Seed(new DmEntity { Id = 1, Name = "orig", Price = 3m, Secret = "s0" });

        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<DmWidgetProfile>().AddDeltaProfile<DmGoodProfile>(),
            configureServices: s => s.AddSingleton(store));

        // The client sends both a mapped property (name) and an ignored one (secret).
        var response = await fx.Client.PatchAsync("/odata/Widgets(1)",
            JsonContent.Create(new { name = "patched", secret = "hacked" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        DmEntity entity = store.Get(1)!;
        Assert.Equal("patched", entity.Name);      // mapped -> applied
        Assert.Equal("s0", entity.Secret);          // ignored in the delta mapping -> untouched
        Assert.Equal(3m, entity.Price);             // not sent -> untouched
    }

    [Fact]
    public async Task Patch_ThroughRealProfile_NonExistentKey_Returns404()
    {
        var store = new DmStore();

        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<DmWidgetProfile>().AddDeltaProfile<DmGoodProfile>(),
            configureServices: s => s.AddSingleton(store));

        var response = await fx.Client.PatchAsync("/odata/Widgets(999)",
            JsonContent.Create(new { name = "x" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
