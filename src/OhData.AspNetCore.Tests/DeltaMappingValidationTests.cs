using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
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
                typeof(OhDataBuilder).GetMethod(nameof(OhDataBuilder.AddDeltaProfile))!
                    .MakeGenericMethod(t).Invoke(o, null);
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
}
