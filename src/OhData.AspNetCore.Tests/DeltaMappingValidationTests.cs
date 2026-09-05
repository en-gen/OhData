using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Reference-assignable (inheritance) automatic mapping ─────────────────────────
public class DmBasePayload { public string Tag { get; set; } = ""; }
public class DmDerivedPayload : DmBasePayload { }
public class DmRefModel { public int Id { get; set; } public DmDerivedPayload Payload { get; set; } = new(); }
public class DmRefEntity { public int Id { get; set; } public DmBasePayload Payload { get; set; } = new(); }
public sealed class DmRefAssignableProfile : DeltaProfile
{
    public DmRefAssignableProfile() => For<DmRefModel, DmRefEntity>(); // Derived -> Base is automatic
}

// ── Duplicate (model, entity) pair across two profiles ───────────────────────────
public sealed class DmDuplicatePairProfile : DeltaProfile
{
    public DmDuplicatePairProfile() => For<DmDto, DmEntity>().Ignore(d => d.Secret);
}

// ── Convert source-selector cast mistake (regression for the request-time 500) ───
public sealed class DmConvertSourceCastProfile : DeltaProfile
{
    // TFrom is inferred as long from the cast; the model property is int -> must fail at STARTUP.
    public DmConvertSourceCastProfile() =>
        For<DmWideDto, DmWideEntity>().Convert(d => (long)d.Count, e => e.Count, (long c) => c);
}

// ── Entity target is get-only (not writable) ─────────────────────────────────────
public class DmRoDto { public int Id { get; set; } public string Name { get; set; } = ""; }
public class DmRoEntity { public int Id { get; set; } public string Name { get; } = ""; }
public sealed class DmReadOnlyTargetProfile : DeltaProfile
{
    public DmReadOnlyTargetProfile() => For<DmRoDto, DmRoEntity>();
}

// ── Rename + Convert on the same model property (ambiguous) ───────────────────────
public class DmConflictDto { public int Id { get; set; } public int Val { get; set; } }
public class DmConflictEntity { public int Id { get; set; } public int A { get; set; } public long B { get; set; } }
public sealed class DmRenameConvertConflictProfile : DeltaProfile
{
    public DmRenameConvertConflictProfile() =>
        For<DmConflictDto, DmConflictEntity>()
            .Rename(d => d.Val, e => e.A)
            .Convert(d => d.Val, e => e.B, (int v) => (long)v);
}

// ── Two model properties targeting one entity property (ambiguous) ───────────────
public class DmDupTargetDto { public int Id { get; set; } public string A { get; set; } = ""; public string B { get; set; } = ""; }
public class DmDupTargetEntity { public int Id { get; set; } public string Name { get; set; } = ""; }
public sealed class DmDuplicateTargetProfile : DeltaProfile
{
    public DmDuplicateTargetProfile() =>
        For<DmDupTargetDto, DmDupTargetEntity>()
            .Rename(d => d.A, e => e.Name)
            .Rename(d => d.B, e => e.Name);
}

// ── BUG1: navigation/collection auto-map must fail fast (scalars/structural only) ─
public class DmNavChild { public int Id { get; set; } }
public class DmNavModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<DmNavChild> Children { get; set; } = new();   // SAME-typed collection on both sides
}
public class DmNavEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<DmNavChild> Children { get; set; } = new();
}
// Children is a navigation-collection with an identical entity counterpart: without the invariant it
// would auto-map (identity-compatible) and silently write the collection onto Delta<TEntity>.
public sealed class DmNavCollectionProfile : DeltaProfile
{
    public DmNavCollectionProfile() => For<DmNavModel, DmNavEntity>();
}
// Ignore()ing the collection resolves it — the mapping compiles clean.
public sealed class DmNavCollectionIgnoredProfile : DeltaProfile
{
    public DmNavCollectionIgnoredProfile() => For<DmNavModel, DmNavEntity>().Ignore(d => d.Children);
}

// Collection-shaped scalars and value types must stay mappable, not be misclassified as navigations.
public class DmScalarModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";                    // IEnumerable<char> but a scalar
    public byte[] Blob { get; set; } = Array.Empty<byte>();    // Edm.Binary scalar (an array)
    public DmStatus Status { get; set; }                       // enum
    public Guid Ref { get; set; }
    public DateTime When { get; set; }
    public DateTimeOffset WhenOffset { get; set; }
    public decimal Amount { get; set; }
    public int? Maybe { get; set; }                            // nullable value type
}
public class DmScalarEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] Blob { get; set; } = Array.Empty<byte>();
    public DmStatus Status { get; set; }
    public Guid Ref { get; set; }
    public DateTime When { get; set; }
    public DateTimeOffset WhenOffset { get; set; }
    public decimal Amount { get; set; }
    public int? Maybe { get; set; }
}
public sealed class DmScalarsProfile : DeltaProfile
{
    public DmScalarsProfile() => For<DmScalarModel, DmScalarEntity>();   // all pure convention
}

// ── R2-2: primitive/scalar collections are STRUCTURAL and must auto-map (not be rejected) ─
public class DmPrimCollModel
{
    public int Id { get; set; }
    public List<int> Numbers { get; set; } = new();            // Collection(Edm.Int32) — structural
    public string[] Tags { get; set; } = Array.Empty<string>(); // Collection(Edm.String) — structural
    public List<DateTime> Dates { get; set; } = new();          // Collection(Edm.DateTimeOffset) — structural
}
public class DmPrimCollEntity
{
    public int Id { get; set; }
    public List<int> Numbers { get; set; } = new();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public List<DateTime> Dates { get; set; } = new();
}
// Identically-typed primitive collections on both sides: they auto-map by identity and must NOT be
// misclassified as navigations (the regression this guards against).
public sealed class DmPrimitiveCollectionsProfile : DeltaProfile
{
    public DmPrimitiveCollectionsProfile() => For<DmPrimCollModel, DmPrimCollEntity>();
}

// ── R2-3: a property in both Ignore() and Convert() must fail fast ────────────────
public class DmIcDto { public int Id { get; set; } public int Val { get; set; } }
public class DmIcEntity { public int Id { get; set; } public long Val { get; set; } }
public sealed class DmIgnoreConvertConflictProfile : DeltaProfile
{
    public DmIgnoreConvertConflictProfile() =>
        For<DmIcDto, DmIcEntity>()
            .Ignore(d => d.Val)
            .Convert(d => d.Val, e => e.Val, (int v) => (long)v);
}

// ── BUG2: a property in both Rename() and Ignore() must fail fast ─────────────────
public class DmRiDto { public int Id { get; set; } public string Name { get; set; } = ""; }
public class DmRiEntity { public int Id { get; set; } public string Label { get; set; } = ""; }
public sealed class DmRenameIgnoreConflictProfile : DeltaProfile
{
    public DmRenameIgnoreConflictProfile() =>
        For<DmRiDto, DmRiEntity>()
            .Rename(d => d.Name, e => e.Label)
            .Ignore(d => d.Name);
}

// ═══════════════════════════════════════════════════════════════════════════════
// #479 / #480 — the existing DmDto/DmEntity family extended with the two defect
// shapes. Derived entities carry the defect so every existing mapping, profile and
// assertion above stays exactly as it was.
// ═══════════════════════════════════════════════════════════════════════════════

// ── #479 (a): Delta<T> drops [NotMapped] properties from its property surface ────
public class DmAuditedEntity : DmEntity
{
    [NotMapped] public string Audit { get; set; } = "";
}
public class DmAuditedDto : DmDto { public string Audit { get; set; } = ""; }
public sealed class DmNotMappedTargetProfile : DeltaProfile
{
    public DmNotMappedTargetProfile() => For<DmAuditedDto, DmAuditedEntity>().Ignore(d => d.Secret);
}
// Ignore()ing the model property is the documented remedy and must still compile clean.
public sealed class DmNotMappedIgnoredProfile : DeltaProfile
{
    public DmNotMappedIgnoredProfile() =>
        For<DmAuditedDto, DmAuditedEntity>().Ignore(d => d.Secret).Ignore(d => d.Audit);
}

// ── #479 (b): Delta<T> requires a PUBLIC getter as well as a public setter ───────
public class DmPrivateGetterEntity : DmEntity
{
    public string Trace { private get; set; } = "";
}
public class DmTracedDto : DmDto { public string Trace { get; set; } = ""; }
public sealed class DmPrivateGetterTargetProfile : DeltaProfile
{
    public DmPrivateGetterTargetProfile() => For<DmTracedDto, DmPrivateGetterEntity>().Ignore(d => d.Secret);
}

// ── #479 (c): [IgnoreDataMember] is excluded by the same Delta<T> rule ───────────
public class DmIgnoreDataMemberEntity : DmEntity
{
    [IgnoreDataMember] public string Note { get; set; } = "";
}
public class DmNotedDto : DmDto { public string Note { get; set; } = ""; }
public sealed class DmIgnoreDataMemberTargetProfile : DeltaProfile
{
    public DmIgnoreDataMemberTargetProfile() => For<DmNotedDto, DmIgnoreDataMemberEntity>().Ignore(d => d.Secret);
}

// ── #479 (d): [DataContract] is a WHOLE-TYPE switch — Delta<T> then tracks only
//    [DataMember]-marked properties, so one class-level attribute discards every
//    write to the entity. Broader than the per-property cases the issue lists. ────
[DataContract]
public class DmDataContractEntity : DmEntity { }
public sealed class DmDataContractTargetProfile : DeltaProfile
{
    public DmDataContractTargetProfile() => For<DmDto, DmDataContractEntity>().Ignore(d => d.Secret);
}

// ── Incidental: a `new`-shadowed entity property of a DIFFERENT type. Delta<T>'s
//    own _allProperties build (ToDictionary by name) throws on it, so constructing
//    the validation probe surfaces it at startup. Filed separately as #488 item 2;
//    this pins the incidental improvement so a refactor cannot silently drop it. ──
public class DmShadowBaseEntity : DmEntity { public object Val { get; set; } = ""; }
public class DmShadowEntity : DmShadowBaseEntity { public new string Val { get; set; } = ""; }
public class DmShadowDto : DmDto { public string Val { get; set; } = ""; }
public sealed class DmShadowProfile : DeltaProfile
{
    public DmShadowProfile() => For<DmShadowDto, DmShadowEntity>().Ignore(d => d.Secret);
}

// ── #480 (a): the standard EF Core shape — protected parameterless ctor beside a
//    public parameterized one. Delta<T>.Reset uses Activator.CreateInstance. ──────
public class DmProtectedCtorEntity : DmEntity
{
    protected DmProtectedCtorEntity() { }
    public DmProtectedCtorEntity(int id) => Id = id;
}
public sealed class DmProtectedCtorProfile : DeltaProfile
{
    public DmProtectedCtorProfile() => For<DmDto, DmProtectedCtorEntity>().Ignore(d => d.Secret);
}

// ── #480 (b): an abstract entity type ────────────────────────────────────────────
public abstract class DmAbstractEntity : DmEntity { }
public sealed class DmAbstractEntityProfile : DeltaProfile
{
    public DmAbstractEntityProfile() => For<DmDto, DmAbstractEntity>().Ignore(d => d.Secret);
}

// ── #480 (c): a positional record — no parameterless constructor at all ──────────
public record DmRecordEntity(int Id);
public sealed class DmRecordEntityProfile : DeltaProfile
{
    public DmRecordEntityProfile() => For<DmScanModel, DmRecordEntity>();
}

public class DeltaMappingValidationTests
{
    private static IDeltaFactory BuildFactory(params Type[] profileTypes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o =>
        {
            foreach (Type t in profileTypes)
            {
                // #665: an extension method on DeltaProfileRegistration in the mapper package, so
                // the receiver is the first argument rather than the target instance.
                typeof(DeltaProfileRegistration).GetMethod(nameof(DeltaProfileRegistration.AddDeltaProfile))!
                    .MakeGenericMethod(t).Invoke(null, new object[] { o });
            }
        });
        return services.BuildServiceProvider().GetRequiredService<IDeltaFactory>();
    }

    // Reference-assignable (Derived -> Base) is part of the automatic subset.
    [Fact]
    public void ReferenceAssignable_IsAutomatic()
    {
        var factory = BuildFactory(typeof(DmRefAssignableProfile));
        var payload = new DmDerivedPayload { Tag = "t" };
        var delta = new Delta<DmRefModel>();
        delta.TrySetPropertyValue(nameof(DmRefModel.Payload), payload);

        var entity = new DmRefEntity();
        factory.Create<DmRefModel, DmRefEntity>(delta).Patch(entity);

        Assert.Same(payload, entity.Payload);
    }

    // Model -> delta path exercising a converter directly (not just via convention).
    [Fact]
    public void CreateFromModel_WithConverter_AppliesConverter()
    {
        var factory = BuildFactory(typeof(DmGoodProfile));
        var model = new DmV2Dto { DisplayName = "n", Status = DmStatus.Archived, Price = 2m };

        var entity = new DmEntity();
        factory.Create<DmV2Dto, DmEntity>(model).Patch(entity);

        Assert.Equal("n", entity.Name);
        Assert.Equal((int)DmStatus.Archived, entity.StatusCode);
    }

    // A nullable/reference property explicitly set to null flows through the delta path.
    [Fact]
    public void Patch_NullValue_FlowsThrough()
    {
        var factory = BuildFactory(typeof(DmGoodProfile));
        var delta = new Delta<DmDto>();
        delta.TrySetPropertyValue(nameof(DmDto.Rank), null);

        var entity = new DmEntity { Rank = 5 };
        factory.Create<DmDto, DmEntity>(delta).Patch(entity);

        Assert.Null(entity.Rank);
    }

    [Fact]
    public void DuplicatePairAcrossProfiles_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildFactory(typeof(DmGoodProfile), typeof(DmDuplicatePairProfile)));
        Assert.Contains("duplicate delta mapping", ex.Message);
    }

    [Fact]
    public void ConvertWithSourceSelectorCast_FailsFastAtStartup()
    {
        // Regression: casting inside the source selector makes TFrom (long) diverge from the model
        // property (int); this must fail at startup, not throw InvalidCastException per request.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildFactory(typeof(DmConvertSourceCastProfile)));
        Assert.Contains("input type must match", ex.Message);
    }

    [Fact]
    public void GetOnlyEntityTarget_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmReadOnlyTargetProfile)));
        Assert.Contains("not writable", ex.Message);
    }

    [Fact]
    public void RenameAndConvertSameProperty_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmRenameConvertConflictProfile)));
        Assert.Contains("both Rename() and Convert()", ex.Message);
    }

    [Fact]
    public void TwoModelPropertiesToOneEntityProperty_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmDuplicateTargetProfile)));
        Assert.Contains("targeted by more than one", ex.Message);
    }

    // The whole point of forcing IDeltaFactory in MapOhData: an invalid mapping fails at startup.
    [Fact]
    public async Task InvalidDeltaProfile_FailsFast_AtMapOhData()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddDeltaProfile<DmUnmappedProfile>()));
    }

    // ── BUG1: navigation/collection auto-map is rejected at startup ───────────────
    [Fact]
    public void NavigationCollection_AutoMap_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmNavCollectionProfile)));
        Assert.Contains("Children", ex.Message);
        Assert.Contains("navigation/collection", ex.Message);
        Assert.Contains("Ignore() it or map it explicitly with Convert()", ex.Message);
    }

    // ── BUG1: the same navigation/collection fails fast at MapOhData() too ────────
    [Fact]
    public async Task NavigationCollection_AutoMap_FailsFastAtMapOhData()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddDeltaProfile<DmNavCollectionProfile>()));
        Assert.Contains("navigation/collection", ex.Message);
    }

    // ── BUG1: Ignore()ing the navigation/collection compiles clean ───────────────
    [Fact]
    public void NavigationCollection_Ignored_CompilesClean()
    {
        IDeltaFactory factory = BuildFactory(typeof(DmNavCollectionIgnoredProfile));
        var delta = new Delta<DmNavModel>();
        delta.TrySetPropertyValue(nameof(DmNavModel.Name), "ok");

        Delta<DmNavEntity> entityDelta = factory.Create<DmNavModel, DmNavEntity>(delta);

        // Name still maps; the ignored collection is absent from the updatable allowlist.
        Assert.Equal(new[] { "Name" }, entityDelta.GetChangedPropertyNames());
        Assert.False(entityDelta.TrySetPropertyValue("Children", new List<DmNavChild>()));
    }

    // ── BUG1: collection-shaped scalars / value types still auto-map ─────────────
    [Fact]
    public void CollectionShapedScalars_StillAutoMap()
    {
        IDeltaFactory factory = BuildFactory(typeof(DmScalarsProfile));   // compiles clean = no false positive
        var model = new DmScalarModel
        {
            Id = 1,
            Name = "n",
            Blob = new byte[] { 1, 2, 3 },
            Status = DmStatus.Archived,
            Ref = Guid.NewGuid(),
            When = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
            WhenOffset = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
            Amount = 9.5m,
            Maybe = 4,
        };

        var entity = new DmScalarEntity();
        factory.Create<DmScalarModel, DmScalarEntity>(model).Patch(entity);

        Assert.Equal("n", entity.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, entity.Blob);   // byte[] mapped, not rejected as a collection
        Assert.Equal(DmStatus.Archived, entity.Status);
        Assert.Equal(model.Ref, entity.Ref);
        Assert.Equal(model.When, entity.When);
        Assert.Equal(model.WhenOffset, entity.WhenOffset);
        Assert.Equal(9.5m, entity.Amount);
        Assert.Equal(4, entity.Maybe);
    }

    // ── BUG2: a property in both Rename() and Ignore() fails fast ────────────────
    [Fact]
    public void RenameAndIgnoreSameProperty_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmRenameIgnoreConflictProfile)));
        Assert.Contains("both Ignore() and Rename()", ex.Message);
    }

    // ── R2-2: primitive/scalar collections stay STRUCTURAL and auto-map ──────────
    [Fact]
    public void PrimitiveScalarCollections_StillAutoMap()
    {
        // Compiles clean (no false-positive rejection) and copies the collections by identity.
        IDeltaFactory factory = BuildFactory(typeof(DmPrimitiveCollectionsProfile));
        var model = new DmPrimCollModel
        {
            Id = 1,
            Numbers = new List<int> { 1, 2, 3 },
            Tags = new[] { "a", "b" },
            Dates = new List<DateTime> { new(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc) },
        };

        var entity = new DmPrimCollEntity();
        factory.Create<DmPrimCollModel, DmPrimCollEntity>(model).Patch(entity);

        Assert.Equal(new[] { 1, 2, 3 }, entity.Numbers);
        Assert.Equal(new[] { "a", "b" }, entity.Tags);
        Assert.Equal(model.Dates, entity.Dates);
    }

    // ── R2-2: a collection of a CLASS element is still rejected as a navigation ──
    [Fact]
    public void ClassElementCollection_AutoMap_StillFailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmNavCollectionProfile)));
        Assert.Contains("navigation/collection", ex.Message);
    }

    // ── R2-2: element-type classification boundaries (adversarial) ───────────────
    [Theory]
    [InlineData(typeof(List<int>), false)]                      // primitive
    [InlineData(typeof(List<int?>), false)]                     // nullable value
    [InlineData(typeof(int[]), false)]                          // primitive array
    [InlineData(typeof(string[]), false)]                       // string array
    [InlineData(typeof(List<DateTime>), false)]                 // struct
    [InlineData(typeof(List<Guid>), false)]                     // struct
    [InlineData(typeof(List<DmStatus>), false)]                 // enum
    [InlineData(typeof(List<decimal>), false)]                  // struct
    [InlineData(typeof(string), false)]                         // collection-shaped scalar
    [InlineData(typeof(byte[]), false)]                         // Edm.Binary scalar
    [InlineData(typeof(List<byte[]>), false)]                   // collection of binary scalars
    [InlineData(typeof(Dictionary<string, int>), false)]        // element KeyValuePair<,> is a struct
    [InlineData(typeof(int), false)]                            // not a collection at all
    [InlineData(typeof(List<DmNavChild>), true)]                // collection of a class
    [InlineData(typeof(IReadOnlyList<DmNavChild>), true)]       // interface collection of a class
    [InlineData(typeof(DmNavChild[]), true)]                    // array of a class
    [InlineData(typeof(System.Collections.IEnumerable), true)]  // bare non-generic — conservative
    public void IsNavigationCollectionType_Classifies(Type type, bool expected)
        => Assert.Equal(expected, DeltaMappingCompiler.IsNavigationCollectionType(type));

    // ── R2-3: a property in both Ignore() and Convert() fails fast ───────────────
    [Fact]
    public void IgnoreAndConvertSameProperty_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmIgnoreConvertConflictProfile)));
        Assert.Contains("both Ignore() and Convert()", ex.Message);
    }

    // ═══ #479 — a target Delta<TEntity> does not track must fail at STARTUP ══════
    // Pre-fix all three compiled clean and the write was discarded with no signal.

    [Fact]
    public void NotMappedEntityTarget_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmNotMappedTargetProfile)));
        Assert.Contains("Audit", ex.Message);
        Assert.Contains("not tracked by Delta<DmAuditedEntity>", ex.Message);
        Assert.Contains("[NotMapped]", ex.Message);
        Assert.Contains("silently discarded", ex.Message);
    }

    [Fact]
    public void PrivateGetterEntityTarget_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmPrivateGetterTargetProfile)));
        Assert.Contains("Trace", ex.Message);
        Assert.Contains("not tracked by Delta<DmPrivateGetterEntity>", ex.Message);
        Assert.Contains("public getter", ex.Message);
    }

    [Fact]
    public void IgnoreDataMemberEntityTarget_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmIgnoreDataMemberTargetProfile)));
        Assert.Contains("Note", ex.Message);
        Assert.Contains("[IgnoreDataMember]", ex.Message);
    }

    // The worst shape of #479: one class-level [DataContract] and Delta<T> tracks NOTHING,
    // so every property of the mapping was discarded — not just the annotated one.
    [Fact]
    public void DataContractEntityWithoutDataMembers_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmDataContractTargetProfile)));
        Assert.Contains("[DataMember]", ex.Message);
        Assert.Contains("not tracked by Delta<DmDataContractEntity>", ex.Message);
        // Every mapped property, not merely one, is reported.
        foreach (string name in new[] { "Id", "Name", "StatusCode", "Rank", "Price" })
            Assert.Contains($"Entity property '{name}'", ex.Message);
    }

    // Incidental consequence of constructing the validation probe: #488 item 2 (a
    // different-typed `new`-shadowed entity property) now fails at startup instead of
    // throwing ArgumentException on the first Create. Pinned, not deliberately fixed.
    [Fact]
    public void DifferentTypedShadowedEntityProperty_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmShadowProfile)));
        Assert.Contains("could not be constructed for validation", ex.Message);
        Assert.Contains("Val", ex.Message);
    }

    // The remedy the message names must actually work — and the untouched properties
    // must still map, so the new rejection is not an over-broad reject-the-whole-type.
    [Fact]
    public void NotMappedEntityTarget_Ignored_CompilesCleanAndStillMapsTheRest()
    {
        IDeltaFactory factory = BuildFactory(typeof(DmNotMappedIgnoredProfile));
        var delta = new Delta<DmAuditedDto>();
        delta.TrySetPropertyValue(nameof(DmAuditedDto.Name), "ok");
        delta.TrySetPropertyValue(nameof(DmAuditedDto.Audit), "should not travel");

        Delta<DmAuditedEntity> entityDelta = factory.Create<DmAuditedDto, DmAuditedEntity>(delta);
        var entity = new DmAuditedEntity();
        entityDelta.Patch(entity);

        Assert.Equal("ok", entity.Name);
        Assert.Equal("", entity.Audit);
        // The Ignore()d names never reach the entity-side allowlist (the #243 boundary claim).
        Assert.DoesNotContain("Audit", entityDelta.UpdatableProperties);
        Assert.DoesNotContain("Secret", entityDelta.UpdatableProperties);
    }

    // ═══ #479 second half — a runtime rejection is never discarded ═══════════════
    // Startup validation now catches every known cause, so this is reached only by
    // handing the factory a plan the compiler would have refused. That is the point:
    // if TrySetPropertyValue ever answers false again, it must be loud.

    private static DeltaFactory FactoryWithUnvalidatedPlan()
    {
        var rule = new CompiledPropertyRule(
            nameof(DmAuditedDto.Audit),
            nameof(DmAuditedEntity.Audit),
            Converter: null,
            ModelAccessor: m => ((DmAuditedDto)m).Audit);
        var plan = new DeltaMappingPlan(
            typeof(DmAuditedDto), typeof(DmAuditedEntity),
            new[] { nameof(DmAuditedEntity.Audit) },
            new[] { rule });
        return new DeltaFactory(new Dictionary<(Type, Type), DeltaMappingPlan>
        {
            [(typeof(DmAuditedDto), typeof(DmAuditedEntity))] = plan,
        });
    }

    [Fact]
    public void RejectedWrite_FromDelta_Throws_RatherThanSilentlyDiscarding()
    {
        DeltaFactory factory = FactoryWithUnvalidatedPlan();
        var delta = new Delta<DmAuditedDto>();
        delta.TrySetPropertyValue(nameof(DmAuditedDto.Audit), "x");

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create<DmAuditedDto, DmAuditedEntity>(delta));
        Assert.Contains("Audit", ex.Message);
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public void RejectedWrite_FromModel_Throws_RatherThanSilentlyDiscarding()
    {
        DeltaFactory factory = FactoryWithUnvalidatedPlan();
        var model = new DmAuditedDto { Audit = "x" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create<DmAuditedDto, DmAuditedEntity>(model));
        Assert.Contains("Audit", ex.Message);
        Assert.Contains("rejected", ex.Message);
    }

    // ═══ #480 — an entity Delta<T> cannot instantiate must fail at STARTUP ═══════
    // Pre-fix all three compiled clean and threw MissingMethodException per request.

    [Fact]
    public void ProtectedParameterlessCtorEntity_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmProtectedCtorProfile)));
        Assert.Contains("DmProtectedCtorEntity", ex.Message);
        Assert.Contains("public parameterless constructor", ex.Message);
    }

    [Fact]
    public void AbstractEntity_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmAbstractEntityProfile)));
        Assert.Contains("DmAbstractEntity", ex.Message);
        Assert.Contains("abstract", ex.Message);
    }

    [Fact]
    public void PositionalRecordEntity_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmRecordEntityProfile)));
        Assert.Contains("DmRecordEntity", ex.Message);
        Assert.Contains("public parameterless constructor", ex.Message);
    }

    [Fact]
    public async Task UnconstructableEntity_FailsFastAtMapOhData()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddDeltaProfile<DmProtectedCtorProfile>()));
        Assert.Contains("public parameterless constructor", ex.Message);
    }
}
