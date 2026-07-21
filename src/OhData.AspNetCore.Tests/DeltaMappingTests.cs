using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Model / entity fixtures ──────────────────────────────────────────────────────
public enum DmStatus { Draft = 0, Active = 1, Archived = 2 }

public class DmEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int StatusCode { get; set; }
    public int? Rank { get; set; }
    public decimal Price { get; set; }
    public string? Secret { get; set; }
}

// Tier 1: pure-convention mirror of DmEntity, minus Secret (ignored in the profile).
public class DmDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int StatusCode { get; set; }
    public int? Rank { get; set; }
    public decimal Price { get; set; }
    public string? Secret { get; set; }
}

// Tier 2: declares only divergences from DmEntity.
public class DmV2Dto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";   // rename -> Name
    public decimal ComputedTotal { get; set; }       // ignore (no entity target)
    public DmStatus Status { get; set; }             // convert -> StatusCode (int)
    public int? Rank { get; set; }
    public decimal Price { get; set; }
}

// nullable-wrap: model int Level -> entity int? Level (automatic).
public class DmNwDto { public int Id { get; set; } public int Level { get; set; } }
public class DmNwEntity { public int Id { get; set; } public int? Level { get; set; } }

public sealed class DmGoodProfile : DeltaProfile
{
    public DmGoodProfile()
    {
        For<DmDto, DmEntity>()
            .Ignore(d => d.Secret);

        For<DmV2Dto, DmEntity>()
            .Rename(d => d.DisplayName, e => e.Name)
            .Ignore(d => d.ComputedTotal)
            .Convert(d => d.Status, e => e.StatusCode, s => (int)s);

        For<DmNwDto, DmNwEntity>();
    }
}

// ── Startup-invalid profiles (compiled lazily on IDeltaFactory resolution) ────────
public class DmStrictEntity { public int Id { get; set; } public int Level { get; set; } }
public class DmBadNullDto { public int Id { get; set; } public int? Level { get; set; } }
public class DmBadTypeDto { public int Id { get; set; } public string Level { get; set; } = ""; }
public class DmUnmappedDto { public int Id { get; set; } public string Extra { get; set; } = ""; }
public class DmTinyEntity { public int Id { get; set; } }
public class DmWideDto { public int Id { get; set; } public int Count { get; set; } }
public class DmWideEntity { public int Id { get; set; } public long Count { get; set; } }

public sealed class DmNullableNarrowingProfile : DeltaProfile
{
    public DmNullableNarrowingProfile() => For<DmBadNullDto, DmStrictEntity>();   // int? -> int (needs explicit)
}

public sealed class DmIncompatibleTypeProfile : DeltaProfile
{
    public DmIncompatibleTypeProfile() => For<DmBadTypeDto, DmStrictEntity>();    // string -> int
}

public sealed class DmUnmappedProfile : DeltaProfile
{
    public DmUnmappedProfile() => For<DmUnmappedDto, DmTinyEntity>();             // Extra has no target
}

public sealed class DmWideningNoConvertProfile : DeltaProfile
{
    public DmWideningNoConvertProfile() => For<DmWideDto, DmWideEntity>();        // int -> long (needs explicit)
}

public sealed class DmWideningConvertProfile : DeltaProfile
{
    public DmWideningConvertProfile() =>
        For<DmWideDto, DmWideEntity>().Convert(d => d.Count, e => e.Count, c => (long)c);
}

// Scan marker (an entity profile) alongside the delta profiles above, both in this assembly.
public class DmScanModel { public int Id { get; set; } }
public sealed class DmScanEntityProfile : EntitySetProfile<int, DmScanModel>
{
    public DmScanEntityProfile() : base(x => x.Id) { }
}

public class DeltaMappingTests
{
    private static IDeltaFactory BuildFactory<TProfile>() where TProfile : DeltaProfile
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.AddDeltaProfile<TProfile>());
        return services.BuildServiceProvider().GetRequiredService<IDeltaFactory>();
    }

    // ── Convention-only PATCH (Delta<Model> -> Delta<Entity>) ────────────────────
    [Fact]
    public void Patch_ConventionOnly_CopiesChangedProperties()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var delta = new Delta<DmDto>();
        delta.TrySetPropertyValue(nameof(DmDto.Name), "Renamed");
        delta.TrySetPropertyValue(nameof(DmDto.Price), 42.5m);

        Delta<DmEntity> entityDelta = factory.Create<DmDto, DmEntity>(delta);

        Assert.Equal(new[] { "Name", "Price" }, entityDelta.GetChangedPropertyNames().OrderBy(n => n));
        var entity = new DmEntity { Id = 1, Name = "old", Price = 1m };
        entityDelta.Patch(entity);
        Assert.Equal("Renamed", entity.Name);
        Assert.Equal(42.5m, entity.Price);
    }

    // ── Convention-only PUT/POST (Model -> Delta<Entity>) ────────────────────────
    [Fact]
    public void CreateFromModel_ConventionOnly_SetsEveryMappedProperty()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var model = new DmDto { Id = 7, Name = "n", StatusCode = 3, Rank = 9, Price = 5m, Secret = "leak" };

        Delta<DmEntity> entityDelta = factory.Create<DmDto, DmEntity>(model);

        // Every mapped property (Secret is ignored) is present in the changed set.
        Assert.Equal(
            new[] { "Id", "Name", "Price", "Rank", "StatusCode" },
            entityDelta.GetChangedPropertyNames().OrderBy(n => n));
        var entity = new DmEntity();
        entityDelta.Patch(entity);
        Assert.Equal(7, entity.Id);
        Assert.Equal("n", entity.Name);
        Assert.Equal(3, entity.StatusCode);
        Assert.Equal(9, entity.Rank);
        Assert.Null(entity.Secret); // ignored -> never written
    }

    // ── Rename ───────────────────────────────────────────────────────────────────
    [Fact]
    public void Patch_Rename_MapsToDifferentEntityProperty()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var delta = new Delta<DmV2Dto>();
        delta.TrySetPropertyValue(nameof(DmV2Dto.DisplayName), "V2Name");

        var entityDelta = factory.Create<DmV2Dto, DmEntity>(delta);

        Assert.Equal(new[] { "Name" }, entityDelta.GetChangedPropertyNames());
        var entity = new DmEntity();
        entityDelta.Patch(entity);
        Assert.Equal("V2Name", entity.Name);
    }

    // ── Ignore ───────────────────────────────────────────────────────────────────
    [Fact]
    public void Patch_Ignore_DropsIgnoredModelProperty()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var delta = new Delta<DmV2Dto>();
        delta.TrySetPropertyValue(nameof(DmV2Dto.ComputedTotal), 999m);
        delta.TrySetPropertyValue(nameof(DmV2Dto.Price), 10m);

        var entityDelta = factory.Create<DmV2Dto, DmEntity>(delta);

        // ComputedTotal never crosses the boundary; only Price does.
        Assert.Equal(new[] { "Price" }, entityDelta.GetChangedPropertyNames());
    }

    // ── Explicit convert ─────────────────────────────────────────────────────────
    [Fact]
    public void Patch_Convert_AppliesConverter()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var delta = new Delta<DmV2Dto>();
        delta.TrySetPropertyValue(nameof(DmV2Dto.Status), DmStatus.Archived);

        var entityDelta = factory.Create<DmV2Dto, DmEntity>(delta);
        var entity = new DmEntity();
        entityDelta.Patch(entity);

        Assert.Equal((int)DmStatus.Archived, entity.StatusCode);
    }

    // ── Nullable-wrap auto ───────────────────────────────────────────────────────
    [Fact]
    public void Patch_NullableWrap_IsAutomatic()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var delta = new Delta<DmNwDto>();
        delta.TrySetPropertyValue(nameof(DmNwDto.Level), 5);

        var entityDelta = factory.Create<DmNwDto, DmNwEntity>(delta);
        var entity = new DmNwEntity();
        entityDelta.Patch(entity);

        Assert.Equal(5, entity.Level);
    }

    // ── T? -> T requires explicit convert (startup fail-fast) ────────────────────
    [Fact]
    public void NullableNarrowing_WithoutConverter_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory<DmNullableNarrowingProfile>());
        Assert.Contains("Level", ex.Message);
        Assert.Contains(".Convert", ex.Message);
    }

    // ── Widening int -> long requires explicit convert; with converter it passes ──
    [Fact]
    public void WideningWithoutConverter_FailsFast_ButConverterSucceeds()
    {
        Assert.Throws<InvalidOperationException>(() => BuildFactory<DmWideningNoConvertProfile>());

        var factory = BuildFactory<DmWideningConvertProfile>();
        var delta = new Delta<DmWideDto>();
        delta.TrySetPropertyValue(nameof(DmWideDto.Count), 5);
        var entity = new DmWideEntity();
        factory.Create<DmWideDto, DmWideEntity>(delta).Patch(entity);
        Assert.Equal(5L, entity.Count);
    }

    // ── Updatable-allowlist translation (Ignore()d prop excluded from entity delta)
    [Fact]
    public void UpdatableAllowlist_ExcludesIgnoredAndUnmappedEntityProperties()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var entityDelta = factory.Create<DmDto, DmEntity>(new Delta<DmDto>());

        // Secret is ignored in the mapping -> not updatable on the entity delta even though the
        // entity has the property. A hostile post-hoc set is silently refused.
        Assert.False(entityDelta.TrySetPropertyValue("Secret", "hacked"));
        // A genuinely mapped property remains settable.
        Assert.True(entityDelta.TrySetPropertyValue("Name", "ok"));
    }

    // ── Startup fail-fast: incompatible type / unmapped property ─────────────────
    [Fact]
    public void IncompatibleType_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory<DmIncompatibleTypeProfile>());
        Assert.Contains("Level", ex.Message);
    }

    [Fact]
    public void UnmappedModelProperty_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory<DmUnmappedProfile>());
        Assert.Contains("Extra", ex.Message);
    }

    // ── Unregistered pair throws at call time ────────────────────────────────────
    [Fact]
    public void UnregisteredPair_ThrowsAtCallTime()
    {
        var factory = BuildFactory<DmGoodProfile>();
        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create<DmDto, DmTinyEntity>(new Delta<DmDto>()));
        Assert.Contains("no delta mapping registered", ex.Message);
    }

    // ── Assembly scan discovers BOTH profile kinds ───────────────────────────────
    [Fact]
    public void AssemblyScan_DiscoversEntityAndDeltaProfiles()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("dmscan", o => o
            .WithPrefix("/dmscan")
            .AddProfilesFromAssemblyOf<DmScanEntityProfile>());

        // Entity profile registered in DI as scoped.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(DmScanEntityProfile) && d.Lifetime == ServiceLifetime.Scoped);

        // Delta profile routed into the shared registry.
        var registry = (DeltaProfileRegistry)services
            .First(d => d.ServiceType == typeof(DeltaProfileRegistry)).ImplementationInstance!;
        Assert.Contains(typeof(DmGoodProfile), registry.Types);
    }

    // ── IsChanged / TryGetChanged sugar ──────────────────────────────────────────
    [Fact]
    public void IsChanged_And_TryGetChanged_ReflectPresence()
    {
        var delta = new Delta<DmDto>();
        delta.TrySetPropertyValue(nameof(DmDto.Name), "x");

        Assert.True(delta.IsChanged(d => d.Name));
        Assert.False(delta.IsChanged(d => d.Price));

        Assert.True(delta.TryGetChanged(d => d.Name, out string? name));
        Assert.Equal("x", name);
        Assert.False(delta.TryGetChanged(d => d.Price, out decimal _));
    }

    [Fact]
    public void DeltaExpressionSugar_RejectsNonMemberExpression()
    {
        var delta = new Delta<DmDto>();
        Assert.Throws<ArgumentException>(() => delta.IsChanged(d => d.Name.ToUpper()));
    }

    // ── Singleton thread-safety under concurrent first-use ───────────────────────
    [Fact]
    public void Factory_IsThreadSafe_UnderConcurrentCreate()
    {
        var factory = BuildFactory<DmGoodProfile>();

        Parallel.For(0, 2000, i =>
        {
            if (i % 2 == 0)
            {
                var delta = new Delta<DmDto>();
                delta.TrySetPropertyValue(nameof(DmDto.Name), "n" + i);
                var e = new DmEntity();
                factory.Create<DmDto, DmEntity>(delta).Patch(e);
                Assert.Equal("n" + i, e.Name);
            }
            else
            {
                var model = new DmV2Dto { DisplayName = "d" + i, Status = DmStatus.Active };
                var e = new DmEntity();
                factory.Create<DmV2Dto, DmEntity>(model).Patch(e);
                Assert.Equal("d" + i, e.Name);
                Assert.Equal((int)DmStatus.Active, e.StatusCode);
            }
        });
    }
}
