using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #351 startup validation: <c>MapOhData()</c> rejects a <c>UseETag</c> selector whose declared
/// type the hash cannot faithfully represent.
/// <para>
/// The failure being prevented is silent and total. A type that neither implements
/// <see cref="IFormattable"/> nor overrides <c>ToString()</c> formats to its own type name — the
/// same string for every row — so every entity in the set shares one ETag value and
/// <c>If-Match</c> degrades to a no-op that always succeeds. Nothing in any response reveals it;
/// the only observable symptom is a lost update. That is why this is a loud startup exception
/// rather than a log line.
/// </para>
/// </summary>
public class ETagSelectorValidationTests
{
    public class Doc
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
        public ImmutableArray<byte> ImmutableVersion { get; set; }
        public List<string> Tags { get; set; } = new();
        public TimeZoneInfo? Zone { get; set; }
        public Doc? Related { get; set; }
        public object? Loose { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private abstract class DocProfileBase : EntitySetProfile<int, Doc>
    {
        protected DocProfileBase() : base(x => x.Id)
        {
            GetById = (id, ct) => Task.FromResult<Doc?>(new Doc { Id = id });
        }
    }

    // ── Rejected ────────────────────────────────────────────────────────────────

    /// <summary>The shape that reaches this through the documented API: an ETag over a
    /// collection navigation property.</summary>
    private sealed class CollectionEtagProfile : DocProfileBase
    {
        public CollectionEtagProfile()
        {
            EntitySetName = "CollectionEtagDocs";
            UseETag(x => x.Tags);
        }
    }

    /// <summary>Overrides <c>ToString()</c>, so the constant-collapse check alone would miss it —
    /// but <see cref="TimeZoneInfo"/> returns its <c>DisplayName</c>, which is UI-culture
    /// dependent, reintroducing the exact bug #351 fixes.</summary>
    private sealed class TimeZoneEtagProfile : DocProfileBase
    {
        public TimeZoneEtagProfile()
        {
            EntitySetName = "TimeZoneEtagDocs";
            UseETag(x => x.Zone);
        }
    }

    private sealed class EntityEtagProfile : DocProfileBase
    {
        public EntityEtagProfile()
        {
            EntitySetName = "EntityEtagDocs";
            UseETag(x => x.Related);
        }
    }

    /// <summary>An <c>object</c>-typed selector cannot be checked at startup at all — its runtime
    /// type is whatever the row happens to hold — so it is rejected rather than trusted.</summary>
    private sealed class LooseEtagProfile : DocProfileBase
    {
        public LooseEtagProfile()
        {
            EntitySetName = "LooseEtagDocs";
            UseETag(x => x.Loose);
        }
    }

    /// <summary>The bad selector is the second of three — validation must check every one, not
    /// just the first.</summary>
    private sealed class MixedEtagProfile : DocProfileBase
    {
        public MixedEtagProfile()
        {
            EntitySetName = "MixedEtagDocs";
            UseETag(x => x.Name, x => x.Tags, x => x.UpdatedAt);
        }
    }

    [Fact]
    public async Task CollectionNavigationSelector_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<CollectionEtagProfile>()));

        Assert.Contains("CollectionEtagDocs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Tags", ex.Message, StringComparison.Ordinal);
        Assert.Contains("List", ex.Message, StringComparison.Ordinal);
        Assert.Contains("If-Match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeZoneInfoSelector_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<TimeZoneEtagProfile>()));

        Assert.Contains("Zone", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TimeZoneInfo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityReferenceSelector_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<EntityEtagProfile>()));

        Assert.Contains("Related", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObjectTypedSelector_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LooseEtagProfile>()));

        Assert.Contains("Loose", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationChecksEverySelector_NotJustTheFirst()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<MixedEtagProfile>()));

        Assert.Contains("Tags", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The message must tell the developer what to do instead, not just that it
    /// failed.</summary>
    [Fact]
    public async Task Message_NamesTheSupportedTypes_AndSuggestsAScalarProjection()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<CollectionEtagProfile>()));

        Assert.Contains("byte[]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IFormattable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("x => x.Something.Id", ex.Message, StringComparison.Ordinal);
    }

    // ── Accepted ────────────────────────────────────────────────────────────────

    private sealed class SupportedEtagProfile : DocProfileBase
    {
        public SupportedEtagProfile()
        {
            EntitySetName = "SupportedEtagDocs";
            UseETag(x => x.Name, x => x.UpdatedAt, x => x.RowVersion, x => x.ImmutableVersion);
        }
    }

    /// <summary>A computed selector: not a direct member access, but its declared type is still
    /// visible and must be accepted.</summary>
    private sealed class ComputedEtagProfile : DocProfileBase
    {
        public ComputedEtagProfile()
        {
            EntitySetName = "ComputedEtagDocs";
            UseETag(x => x.Name.Length, x => x.Tags.Count);
        }
    }

    [Fact]
    public async Task SupportedSelectors_MapCleanly()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<SupportedEtagProfile>());

        Assert.NotNull(fx.Client);
    }

    [Fact]
    public async Task ComputedSelectorsOfSupportedTypes_MapCleanly()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ComputedEtagProfile>());

        Assert.NotNull(fx.Client);
    }
}
