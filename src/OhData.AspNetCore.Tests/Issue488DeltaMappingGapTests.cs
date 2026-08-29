using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// #488 — further gaps where DeltaMappingCompiler validated against a model of
// Delta<T> that is not the model Delta<T> implements. Item 2 (a different-typed
// `new`-shadowed entity property) was already closed incidentally by #479 and is
// pinned by DeltaMappingValidationTests.DifferentTypedShadowedEntityProperty_*.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Item 1: a Convert() closure over an injected scoped dependency ──────────────

/// <summary>A scoped dependency that is disposed with its scope, exactly like a DbContext.</summary>
public sealed class DmScopedDep : IDisposable
{
    private bool _disposed;
    public long Widen(int v)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DmScopedDep));
        return v;
    }
    public void Dispose() => _disposed = true;
}

public sealed class DmCapturingConverterProfile : DeltaProfile
{
    private readonly DmScopedDep _dep;
    public DmCapturingConverterProfile(DmScopedDep dep)
    {
        _dep = dep;
        // Hoisted verbatim into the process-lifetime singleton plan, closing over the
        // startup scope's dependency.
        For<DmWideDto, DmWideEntity>().Convert(d => d.Count, e => e.Count, c => _dep.Widen(c));
    }
}

/// <summary>The remedy the rejection names: a converter that captures nothing.</summary>
public sealed class DmStaticConverterProfile : DeltaProfile
{
    public DmStaticConverterProfile() =>
        For<DmWideDto, DmWideEntity>().Convert(d => d.Count, e => e.Count, static c => (long)c);
}

/// <summary>An ordinary non-<c>static</c> lambda that happens to capture nothing must still be
/// accepted — Roslyn caches it on <c>&lt;&gt;c</c>, so it is already shared process-wide.</summary>
public sealed class DmNonStaticButNonCapturingConverterProfile : DeltaProfile
{
    public DmNonStaticButNonCapturingConverterProfile() =>
        For<DmWideDto, DmWideEntity>().Convert(d => d.Count, e => e.Count, c => (long)c);
}

/// <summary>A static method group captures no receiver and must be accepted.</summary>
public sealed class DmStaticMethodGroupConverterProfile : DeltaProfile
{
    private static long Widen(int v) => v;
    public DmStaticMethodGroupConverterProfile() =>
        For<DmWideDto, DmWideEntity>().Convert(d => d.Count, e => e.Count, Widen);
}

/// <summary>A dependency with NO instance fields, bound as a method group. The target is the
/// scoped receiver itself and is not compiler-generated, but a field count alone reports zero —
/// so this is the shape that proves the check does not rest on the field count.</summary>
public sealed class DmFieldlessDep { public long Widen(int v) => v; }
public sealed class DmFieldlessReceiverConverterProfile : DeltaProfile
{
    public DmFieldlessReceiverConverterProfile(DmFieldlessDep dep) =>
        For<DmWideDto, DmWideEntity>().Convert(d => d.Count, e => e.Count, dep.Widen);
}

// ── Item 4: get-only collection properties are tracked by Delta<T> ──────────────

public class DmTagsDto
{
    public int Id { get; set; }
    public List<int> Tags { get; } = new();        // get-only: Delta<T> tracks it anyway
}
public class DmTagsEntity
{
    public int Id { get; set; }
    public List<int> Tags { get; } = new();        // get-only on the entity side too
}
public sealed class DmTagsProfile : DeltaProfile
{
    public DmTagsProfile() => For<DmTagsDto, DmTagsEntity>();
}

/// <summary>Model side only: a get-only collection with no entity counterpart must be
/// reported at startup, not silently dropped at runtime.</summary>
public class DmOrphanTagsDto
{
    public int Id { get; set; }
    public List<int> Labels { get; } = new();
}
public sealed class DmOrphanTagsProfile : DeltaProfile
{
    public DmOrphanTagsProfile() => For<DmOrphanTagsDto, DmTinyEntity>();
}

/// <summary>Entity side only: a settable model collection onto a get-only entity collection.
/// Delta&lt;T&gt; clears and refills the existing instance, so this is writable in fact.</summary>
public class DmSettableTagsDto
{
    public int Id { get; set; }
    public List<int> Tags { get; set; } = new();
}
public sealed class DmSettableToGetOnlyTagsProfile : DeltaProfile
{
    public DmSettableToGetOnlyTagsProfile() => For<DmSettableTagsDto, DmTagsEntity>();
}

/// <summary>Delta&lt;T&gt; TRACKS a setter-less array and then throws when the write is applied
/// (it has no <c>Clear</c> method), so the tracked set alone is not licence to accept one.</summary>
public class DmBlobDto
{
    public int Id { get; set; }
    public byte[] Blob { get; set; } = Array.Empty<byte>();
}
public class DmBlobEntity
{
    public int Id { get; set; }
    public byte[] Blob { get; } = new byte[] { 1 };
}
public sealed class DmSetterlessArrayTargetProfile : DeltaProfile
{
    public DmSetterlessArrayTargetProfile() => For<DmBlobDto, DmBlobEntity>();
}

/// <summary>The "scalars/structural only" invariant must apply to the newly in-scope
/// properties too: a get-only collection of a CLASS element is still a navigation.</summary>
public class DmGetOnlyNavDto
{
    public int Id { get; set; }
    public List<DmNavChild> Children { get; } = new();
}
public class DmGetOnlyNavEntity
{
    public int Id { get; set; }
    public List<DmNavChild> Children { get; } = new();
}
public sealed class DmGetOnlyNavProfile : DeltaProfile
{
    public DmGetOnlyNavProfile() => For<DmGetOnlyNavDto, DmGetOnlyNavEntity>();
}

// ── Item 5(a): an open-generic profile in a scanned assembly ────────────────────

public class DmOpenGenericDeltaProfile<T> : DeltaProfile where T : class
{
    public DmOpenGenericDeltaProfile() => For<DmDto, DmEntity>();
}

public class DmOpenGenericEntityProfile<T> : EntitySetProfile<int, DmScanModel> where T : class
{
    public DmOpenGenericEntityProfile() : base(x => x.Id) { }
}

// ── Item 5(b): duplicate Rename()/Convert() for one source ──────────────────────

public sealed class DmDoubleRenameProfile : DeltaProfile
{
    public DmDoubleRenameProfile() =>
        For<DmConflictDto, DmConflictEntity>()
            .Rename(d => d.Val, e => e.A)
            .Rename(d => d.Val, e => e.B);     // the first silently disappeared
}

public sealed class DmDoubleConvertProfile : DeltaProfile
{
    public DmDoubleConvertProfile() =>
        For<DmConflictDto, DmConflictEntity>()
            .Convert(d => d.Val, e => e.A, static (int v) => v)
            .Convert(d => d.Val, e => e.B, static (int v) => (long)v);
}

public class Issue488DeltaMappingGapTests
{
    private static IDeltaFactory BuildFactory(params Type[] profileTypes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<DmScopedDep>();
        services.AddScoped<DmFieldlessDep>();
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

    // ═══ Item 1 — a capturing converter is refused at startup ════════════════════
    // Pre-fix: startup passed and every Create threw ObjectDisposedException, because
    // DeltaFactory.Build resolves profiles in a scope it disposes immediately while the
    // converter is hoisted verbatim into the singleton plan. A non-disposable dependency
    // was worse still: silent stale-instance reuse with no signal at all.

    [Fact]
    public void ConverterCapturingAScopedDependency_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildFactory(typeof(DmCapturingConverterProfile)));
        Assert.Contains("Count", ex.Message);
        Assert.Contains("captures state", ex.Message);
        Assert.Contains("static", ex.Message);
    }

    [Theory]
    [InlineData(typeof(DmStaticConverterProfile))]
    [InlineData(typeof(DmNonStaticButNonCapturingConverterProfile))]
    [InlineData(typeof(DmStaticMethodGroupConverterProfile))]
    public void NonCapturingConverter_StillCompilesAndRuns(Type profileType)
    {
        IDeltaFactory factory = BuildFactory(profileType);
        var entity = new DmWideEntity();
        factory.Create<DmWideDto, DmWideEntity>(new DmWideDto { Id = 1, Count = 7 }).Patch(entity);
        Assert.Equal(7L, entity.Count);
    }

    /// <summary>
    /// The shape a field-count-only check would miss: an instance method group over a dependency
    /// that declares no fields. The receiver is captured all the same, and it is the disposed
    /// startup-scope instance.
    /// </summary>
    [Fact]
    public void ConverterBoundToAFieldlessReceiver_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildFactory(typeof(DmFieldlessReceiverConverterProfile)));
        Assert.Contains("captures state", ex.Message);
    }

    // ═══ Item 4 — get-only collections are in scope on both sides ════════════════

    /// <summary>
    /// The end-to-end shape the issue names: pre-fix the compiler never saw <c>Tags</c> (no
    /// public setter), so no rule existed, <c>Create</c> dropped it, and the client's PATCH
    /// returned 200 with nothing persisted.
    /// </summary>
    [Fact]
    public void GetOnlyCollectionModelProperty_IsMapped_NotSilentlyDropped()
    {
        IDeltaFactory factory = BuildFactory(typeof(DmTagsProfile));
        var delta = new Delta<DmTagsDto>();
        Assert.True(delta.TrySetPropertyValue(nameof(DmTagsDto.Tags), new List<int> { 1, 2, 3 }));

        var entity = new DmTagsEntity();
        factory.Create<DmTagsDto, DmTagsEntity>(delta).Patch(entity);

        Assert.Equal(new[] { 1, 2, 3 }, entity.Tags);
    }

    /// <summary>A get-only collection with no entity counterpart is a real unmapped property and
    /// must be reported at startup like any other — pre-fix it compiled clean.</summary>
    [Fact]
    public void GetOnlyCollectionWithNoEntityCounterpart_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmOrphanTagsProfile)));
        Assert.Contains("Labels", ex.Message);
        Assert.Contains("no entity counterpart", ex.Message);
    }

    /// <summary>The entity-side half: <c>Delta&lt;T&gt;</c> admits a setter-less collection and
    /// clears-and-refills the existing instance, so OhData's stricter "public setter required"
    /// rule was rejecting a target that is writable in fact.</summary>
    [Fact]
    public void GetOnlyCollectionEntityTarget_IsWritable_NotRejected()
    {
        IDeltaFactory factory = BuildFactory(typeof(DmSettableToGetOnlyTagsProfile));
        var entity = new DmTagsEntity();
        entity.Tags.Add(99);

        factory.Create<DmSettableTagsDto, DmTagsEntity>(
            new DmSettableTagsDto { Id = 1, Tags = new List<int> { 4, 5 } }).Patch(entity);

        Assert.Equal(new[] { 4, 5 }, entity.Tags);
    }

    /// <summary>
    /// The measured limit of the entity-side widening, and the reason it is decided by probing the
    /// write rather than by trusting the tracked set: <c>Delta&lt;T&gt;</c> tracks a setter-less
    /// <c>byte[]</c> and then throws <c>SerializationException</c> when the write is applied, so
    /// adopting "tracked ⇒ writable" would have turned this startup rejection into a guaranteed
    /// per-request 500.
    /// </summary>
    [Fact]
    public void SetterlessArrayEntityTarget_StaysRejectedAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildFactory(typeof(DmSetterlessArrayTargetProfile)));
        Assert.Contains("Blob", ex.Message);
        Assert.Contains("not writable", ex.Message);
    }

    /// <summary>Widening the in-scope surface must not open a navigation-write hole: a get-only
    /// collection of a class element is still refused. Pre-fix it was invisible to the compiler
    /// entirely, so the "scalars/structural only" invariant did not cover it at all.</summary>
    [Fact]
    public void GetOnlyNavigationCollection_StillFailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(typeof(DmGetOnlyNavProfile)));
        Assert.Contains("Children", ex.Message);
        Assert.Contains("navigation/collection", ex.Message);
    }

    // ═══ Item 5(a) — open generics are excluded from the scan ════════════════════
    // Pre-fix an open-generic DeltaProfile was discovered, registered, and killed
    // MapOhData() with a raw MemberAccessException naming no remedy.

    [Fact]
    public void ProfileScan_SkipsOpenGenericProfiles()
    {
        var scanner = new ProfileScanner(Array.Empty<Type>());
        scanner.In(typeof(DmOpenGenericDeltaProfile<>).Assembly);
        Type[] found = scanner.Scan().ToArray();

        Assert.DoesNotContain(typeof(DmOpenGenericDeltaProfile<>), found);
        Assert.DoesNotContain(typeof(DmOpenGenericEntityProfile<>), found);
        // ... while still finding the ordinary concrete profiles beside them.
        Assert.Contains(typeof(DmGoodProfile), found);
        Assert.Contains(typeof(DmScanEntityProfile), found);
    }

    // ═══ Item 5(b) — a duplicate Rename()/Convert() is not last-writer-wins ══════

    [Fact]
    public void DuplicateRenameForOneSource_ThrowsAtTheCallSite()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new DmDoubleRenameProfile());
        Assert.Contains("Val", ex.Message);
        Assert.Contains("Rename()", ex.Message);
    }

    [Fact]
    public void DuplicateConvertForOneSource_ThrowsAtTheCallSite()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new DmDoubleConvertProfile());
        Assert.Contains("Val", ex.Message);
        Assert.Contains("Convert()", ex.Message);
    }

    // ═══ Item 5(c) — scan-then-explicit registration is not an error ═════════════
    // The reverse order was already silently fine; this order threw "Remove the duplicate
    // AddDeltaProfile call" at a user who made exactly one explicit call.

    [Fact]
    public void ScanThenExplicitDeltaProfileRegistration_IsANoOp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Registration only — nothing is resolved, so the intentionally-invalid profiles the
        // scan also finds are never compiled (same technique as the CoverageGapTests scans).
        services.AddOhData("i488c", o => o
            .WithPrefix("/i488c")
            .AddProfilesFrom(s => s.InAssemblyOf<DmGoodProfile>())
            .AddDeltaProfile<DmGoodProfile>());

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(DmGoodProfile)));
    }

    /// <summary>A genuine duplicate explicit call still throws — the fix narrows the message's
    /// claim to the case that actually made it.</summary>
    [Fact]
    public void TwoExplicitDeltaProfileRegistrations_StillThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("i488c2", o => o
                .WithPrefix("/i488c2")
                .AddDeltaProfile<DmGoodProfile>()
                .AddDeltaProfile<DmGoodProfile>()));
        Assert.Contains("Remove the duplicate AddDeltaProfile call", ex.Message);
    }

    // ═══ Documentation note from the issue — measured, not assumed ═══════════════

    /// <summary>
    /// <c>IsAutomaticallyCompatible</c>'s nullable-wrap line is unreachable: the CLR already
    /// answers <c>true</c> for <c>int? IsAssignableFrom int</c>. Pinned so nobody "simplifies"
    /// it in the wrong direction and concludes the nullable case is unhandled.
    /// </summary>
    [Fact]
    public void NullableWrap_IsAlreadyCoveredByIsAssignableFrom()
    {
        Assert.True(typeof(int?).IsAssignableFrom(typeof(int)));
        Assert.True(DeltaMappingCompiler.IsAutomaticallyCompatible(typeof(int), typeof(int?)));
        Assert.False(DeltaMappingCompiler.IsAutomaticallyCompatible(typeof(int?), typeof(int)));
    }
}
