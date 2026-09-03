using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Regression tests for #351 — <c>UseETag</c> hashed property values with a bare, parameterless
/// <c>ToString()</c>: no format specifier (so every date/time type lost its sub-second component,
/// and <see cref="TimeOnly"/> lost whole seconds) and no <see cref="IFormatProvider"/> (so the
/// same entity produced different ETags under different thread cultures).
/// <para>
/// The headline consequence was a genuine lost update, not a merely spurious 412: two writes in
/// the same second hashed identically, so a client holding a stale ETag passed the
/// <c>If-Match</c> precondition and clobbered a newer version with a <c>200</c>.
/// </para>
/// <para>
/// This file deliberately touches only public API plus the internal
/// <c>IEntitySetEndpointSource.InvokeGetETag</c>, and never names the new formatter type, so it
/// compiles and runs unchanged against pre-fix sources — which is how its ability to fail was
/// verified.
/// </para>
/// </summary>
public class ETagPrecisionTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    public class Reading
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Singleton store so state survives across the several sequential requests a
    /// stale-vs-current If-Match sequence needs.</summary>
    private sealed class ReadingStore
    {
        public readonly Dictionary<int, Reading> Items = new();
    }

    /// <summary>
    /// The exact pattern <c>docs/etags.md</c> recommends and the shipped TestBench uses:
    /// a key plus a "last modified" timestamp.
    /// </summary>
    private sealed class ReadingProfile : EntitySetProfile<int, Reading>
    {
        public ReadingProfile(ReadingStore store) : base(x => x.Id)
        {
            EntitySetName = "Readings";
            GetById = (id, ct) => OhDataResult.SuccessTask(store.Items.TryGetValue(id, out var r) ? r : null);
            Put = (id, reading, ct) =>
            {
                reading.Id = id;
                store.Items[id] = reading;
                return OhDataResult.SuccessTask(reading);
            };
            UseETag(x => x.Id, x => x.UpdatedAt);
        }
    }

    private static readonly DateTime BaseInstant =
        new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Task<TestFixture> BuildReadingHostAsync(ReadingStore store) =>
        TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ReadingProfile>(),
            configureServices: s => s.AddSingleton(store));

    // ── 1. Headline: same-second lost update ────────────────────────────────────

    /// <summary>
    /// Two writes inside the SAME second must produce different ETags, and the stale
    /// <c>If-Match</c> must be refused with 412 — not silently accepted with 200, overwriting the
    /// newer version. Pre-fix this test fails twice: the two ETags are byte-identical and the
    /// stale PUT returns 200 with the newer write clobbered.
    /// </summary>
    [Fact]
    public async Task SameSecondWrites_ProduceDistinctETags_AndStaleIfMatchIs412()
    {
        var store = new ReadingStore();
        // State A and state B differ by 800 ms — the same second, different representations.
        store.Items[1] = new Reading { Id = 1, Name = "state-A", UpdatedAt = BaseInstant.AddMilliseconds(100) };
        await using var fx = await BuildReadingHostAsync(store);

        // 1. Client reads state A and remembers its ETag.
        var readA = await fx.Client.GetAsync("/odata/Readings(1)");
        Assert.Equal(HttpStatusCode.OK, readA.StatusCode);
        string etagA = readA.Headers.ETag!.Tag;

        // 2. Another writer advances the server to state B, within the same second.
        store.Items[1] = new Reading { Id = 1, Name = "WRITTEN-BY-OTHER-CLIENT", UpdatedAt = BaseInstant.AddMilliseconds(900) };

        var readB = await fx.Client.GetAsync("/odata/Readings(1)");
        Assert.Equal(HttpStatusCode.OK, readB.StatusCode);
        string etagB = readB.Headers.ETag!.Tag;

        // A different representation MUST hash differently, even inside one second.
        Assert.NotEqual(etagA, etagB);

        // 3. The first client writes with its now-stale ETag. This must be refused.
        using var staleReq = new HttpRequestMessage(HttpMethod.Put, "/odata/Readings(1)")
        {
            Content = JsonContent.Create(new Reading
            {
                Id = 1,
                Name = "CLOBBERED-BY-STALE-CLIENT",
                UpdatedAt = BaseInstant.AddMilliseconds(100),
            }),
        };
        staleReq.Headers.TryAddWithoutValidation("If-Match", etagA);
        var staleResp = await fx.Client.SendAsync(staleReq);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResp.StatusCode);

        // 4. And the newer write survived — no lost update.
        Assert.Equal("WRITTEN-BY-OTHER-CLIENT", store.Items[1].Name);
    }

    // ── 2. Cross-second control (already worked pre-fix; must keep working) ─────

    [Fact]
    public async Task CrossSecondWrites_StaleIfMatchIs412_Control()
    {
        var store = new ReadingStore();
        store.Items[1] = new Reading { Id = 1, Name = "state-A", UpdatedAt = BaseInstant.AddMilliseconds(100) };
        await using var fx = await BuildReadingHostAsync(store);

        var readA = await fx.Client.GetAsync("/odata/Readings(1)");
        string etagA = readA.Headers.ETag!.Tag;

        // Advance a full second and a bit — coarse enough that even the pre-fix formatting saw it.
        store.Items[1] = new Reading { Id = 1, Name = "WRITTEN-BY-OTHER-CLIENT", UpdatedAt = BaseInstant.AddSeconds(1).AddMilliseconds(100) };

        var readB = await fx.Client.GetAsync("/odata/Readings(1)");
        Assert.NotEqual(etagA, readB.Headers.ETag!.Tag);

        using var staleReq = new HttpRequestMessage(HttpMethod.Put, "/odata/Readings(1)")
        {
            Content = JsonContent.Create(new Reading { Id = 1, Name = "CLOBBERED", UpdatedAt = BaseInstant.AddMilliseconds(100) }),
        };
        staleReq.Headers.TryAddWithoutValidation("If-Match", etagA);
        var staleResp = await fx.Client.SendAsync(staleReq);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResp.StatusCode);
        Assert.Equal("WRITTEN-BY-OTHER-CLIENT", store.Items[1].Name);
    }

    /// <summary>A current ETag still matches — the fix must not turn every write into a 412.</summary>
    [Fact]
    public async Task CurrentIfMatch_StillSucceeds()
    {
        var store = new ReadingStore();
        store.Items[1] = new Reading { Id = 1, Name = "state-A", UpdatedAt = BaseInstant.AddMilliseconds(100) };
        await using var fx = await BuildReadingHostAsync(store);

        var read = await fx.Client.GetAsync("/odata/Readings(1)");
        string etag = read.Headers.ETag!.Tag;

        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/Readings(1)")
        {
            Content = JsonContent.Create(new Reading { Id = 1, Name = "updated", UpdatedAt = BaseInstant.AddMilliseconds(250) }),
        };
        req.Headers.TryAddWithoutValidation("If-Match", etag);
        var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("updated", store.Items[1].Name);
        Assert.NotEqual(etag, resp.Headers.ETag!.Tag);
    }

    /// <summary>Identical state still hashes identically — the ETag must be a pure function of
    /// the selected values, not of the request or the clock.</summary>
    [Fact]
    public async Task IdenticalState_ProducesIdenticalETag_AcrossRequests()
    {
        var store = new ReadingStore();
        store.Items[1] = new Reading { Id = 1, Name = "state-A", UpdatedAt = BaseInstant.AddTicks(1234567) };
        await using var fx = await BuildReadingHostAsync(store);

        var first = await fx.Client.GetAsync("/odata/Readings(1)");
        var second = await fx.Client.GetAsync("/odata/Readings(1)");

        Assert.Equal(first.Headers.ETag!.Tag, second.Headers.ETag!.Tag);
    }

    // ── 3. Per-type precision coverage ──────────────────────────────────────────

    public class TypedThing
    {
        public int Id { get; set; }
        public DateTime Dt { get; set; }
        public DateTime DtLocal { get; set; }
        public DateTimeOffset Dto { get; set; }
        public DateOnly Date { get; set; }
        public TimeOnly Time { get; set; }
        public TimeSpan Span { get; set; }
        public Guid Ref { get; set; }
        public double Dbl { get; set; }
        public float Flt { get; set; }
        public decimal Dec { get; set; }
        public long Num { get; set; }
        public bool Flag { get; set; }
    }

    private sealed class TypedThingProfile : EntitySetProfile<int, TypedThing>
    {
        public TypedThingProfile() : base(x => x.Id)
        {
            EntitySetName = "TypedThings";
            UseETag(
                x => x.Dt, x => x.DtLocal, x => x.Dto, x => x.Date, x => x.Time, x => x.Span,
                x => x.Ref, x => x.Dbl, x => x.Flt, x => x.Dec, x => x.Num, x => x.Flag);
        }
    }

    /// <summary>Computes the ETag exactly as the framework does, without an HTTP round trip.</summary>
    private static string ETagOf<TKey, TModel>(EntitySetProfile<TKey, TModel> profile, TModel model)
        where TModel : class
        => ((IEntitySetEndpointSource)profile).InvokeGetETag(model!);

    private static TypedThing Baseline() => new()
    {
        Id = 1,
        Dt = BaseInstant.AddMilliseconds(100),
        DtLocal = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local).AddMilliseconds(100),
        Dto = new DateTimeOffset(BaseInstant).AddMilliseconds(100),
        Date = new DateOnly(2026, 1, 1),
        Time = new TimeOnly(14, 30, 0, 500),
        Span = TimeSpan.FromTicks(123456789),
        Ref = new Guid("00000000-0000-0000-0000-000000000001"),
        Dbl = 0.1 + 0.2,
        Flt = 1f / 3f,
        Dec = 1234.56m,
        Num = 42,
        Flag = true,
    };

    /// <summary>
    /// Each case names one subtle, genuinely-different value for one property type. Every one of
    /// them must move the ETag; the cases marked "(pre-fix collision)" did not before #351.
    /// </summary>
    [Theory]
    // Sub-second on DateTime — the reported lost-update case. (pre-fix collision)
    [InlineData("dt-same-second")]
    [InlineData("dt-one-tick")]                 // (pre-fix collision)
    [InlineData("dt-kind")]                     // DateTimeKind is part of the value
    // Local values: sub-second precision must survive the Kind-marker treatment, and a Local
    // value must stay distinct from the same wall-clock ticks stored as Utc or Unspecified.
    [InlineData("dt-local-same-second")]        // (pre-fix collision)
    [InlineData("dt-local-to-unspecified")]
    [InlineData("dt-local-to-utc")]
    // Sub-second on DateTimeOffset — what DateTimeOffset.UtcNow yields on every write.
    [InlineData("dto-same-second")]             // (pre-fix collision)
    [InlineData("dto-offset")]
    [InlineData("date-plus-day")]
    // TimeOnly is the worst case: the general pattern drops SECONDS as well as the fraction.
    [InlineData("time-same-second")]            // (pre-fix collision)
    [InlineData("time-plus-five-seconds")]      // (pre-fix collision)
    [InlineData("span-one-tick")]
    [InlineData("guid")]
    // Floating point must round-trip: these differ only in the last bits.
    [InlineData("double-nearby")]
    [InlineData("double-one-ulp")]
    [InlineData("float-one-ulp")]
    [InlineData("double-negative-zero")]
    // decimal scale is part of the representation; 1234.560m renders differently to 1234.56m.
    [InlineData("decimal-cent")]
    [InlineData("decimal-scale")]
    [InlineData("long")]
    [InlineData("bool")]
    public void SubtlePropertyChange_ChangesETag(string mutation)
    {
        var profile = new TypedThingProfile();
        var before = Baseline();
        var after = Mutate(Baseline(), mutation);

        string etagBefore = ETagOf(profile, before);
        string etagAfter = ETagOf(profile, after);

        Assert.True(etagBefore != etagAfter,
            $"ETag did not change for mutation '{mutation}'. Both were {etagBefore}.");
    }

    private static TypedThing Mutate(TypedThing t, string mutation)
    {
        switch (mutation)
        {
            case "dt-same-second": t.Dt = BaseInstant.AddMilliseconds(900); break;
            case "dt-one-tick": t.Dt = t.Dt.AddTicks(1); break;
            case "dt-kind": t.Dt = DateTime.SpecifyKind(t.Dt, DateTimeKind.Unspecified); break;
            case "dt-local-same-second":
                t.DtLocal = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local).AddMilliseconds(900);
                break;
            case "dt-local-to-unspecified": t.DtLocal = DateTime.SpecifyKind(t.DtLocal, DateTimeKind.Unspecified); break;
            case "dt-local-to-utc": t.DtLocal = DateTime.SpecifyKind(t.DtLocal, DateTimeKind.Utc); break;
            case "dto-same-second": t.Dto = new DateTimeOffset(BaseInstant).AddMilliseconds(900); break;
            case "dto-offset": t.Dto = t.Dto.ToOffset(TimeSpan.FromHours(2)); break;
            case "date-plus-day": t.Date = t.Date.AddDays(1); break;
            case "time-same-second": t.Time = new TimeOnly(14, 30, 0, 900); break;
            case "time-plus-five-seconds": t.Time = new TimeOnly(14, 30, 5, 500); break;
            case "span-one-tick": t.Span = t.Span.Add(TimeSpan.FromTicks(1)); break;
            case "guid": t.Ref = new Guid("00000000-0000-0000-0000-000000000002"); break;
            case "double-nearby": t.Dbl = 0.3; break;
            case "double-one-ulp": t.Dbl = Math.BitIncrement(t.Dbl); break;
            case "float-one-ulp": t.Flt = MathF.BitIncrement(t.Flt); break;
            case "double-negative-zero": t.Dbl = -0.0; break;
            case "decimal-cent": t.Dec = 1234.57m; break;
            case "decimal-scale": t.Dec = 1234.560m; break;
            case "long": t.Num = 43; break;
            case "bool": t.Flag = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }

        return t;
    }

    /// <summary>
    /// <c>-0.0</c> and <c>0.0</c> are different <see cref="double"/> bit patterns and a
    /// round-trippable formatter must keep them apart ("-0" vs "0").
    /// </summary>
    [Fact]
    public void NegativeZeroAndPositiveZero_DoNotCollide()
    {
        var profile = new TypedThingProfile();
        var negative = Baseline();
        negative.Dbl = -0.0;
        var positive = Baseline();
        positive.Dbl = 0.0;

        Assert.NotEqual(ETagOf(profile, negative), ETagOf(profile, positive));
    }

    [Fact]
    public void UnchangedState_ProducesIdenticalETag()
    {
        var profile = new TypedThingProfile();
        Assert.Equal(ETagOf(profile, Baseline()), ETagOf(profile, Baseline()));
    }

    /// <summary>
    /// The golden value. Every other assertion in this file is relational (two computed ETags
    /// compared against each other), which cannot catch a change that moves *both* sides — the
    /// exact shape of the #351 regression. This pins one literal ETag over a model covering every
    /// supported category, so any future change to the formatting or the framing fails CI and has
    /// to be a deliberate, documented decision.
    /// <para>
    /// It doubles as the machine-independence proof: the model carries a
    /// <see cref="DateTimeKind.Local"/> timestamp, so if the formatter ever re-imported
    /// <c>TimeZoneInfo.Local</c>'s offset (as <c>"O"</c> does natively) this value would differ
    /// between developer machines and CI. It is also asserted under a non-invariant culture.
    /// </para>
    /// <para>
    /// <b>If this test fails, do not just update the constant.</b> Every ETag the framework has
    /// ever issued changes with it — see the upgrade note in docs/etags.md.
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenETagValue_IsPinned()
    {
        const string Golden = "AbClBeSSBSIpXap9VGWOsZrEggQd/sMvuJUUV8O7Rl0=";

        var profile = new TypedThingProfile();

        Assert.Equal(Golden, ETagOf(profile, Baseline()));
        Assert.Equal(Golden, CultureScope.Run("de-DE", () => ETagOf(profile, Baseline())));
        Assert.Equal(Golden, CultureScope.Run("th-TH", () => ETagOf(profile, Baseline())));
    }

    /// <summary>Golden value for the binary path, pinned for the same reason.</summary>
    [Fact]
    public void GoldenETagValue_ForByteArrayRowVersion_IsPinned()
    {
        var profile = new VersionedProfile();

        Assert.Equal(
            "GQ8uG6NlitaOK6cOVuXIsK1K6w+cuoTvTZYfDmWXu1E=",
            ETagOf(profile, new Versioned { Id = 1, RowVersion = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } }));
    }

    // ── 4. Culture invariance ───────────────────────────────────────────────────

    public class Priced
    {
        public int Id { get; set; }
        public decimal Price { get; set; }
        public double Ratio { get; set; }
        public DateTime At { get; set; }
        public long Count { get; set; }
    }

    private sealed class PricedProfile : EntitySetProfile<int, Priced>
    {
        public PricedProfile() : base(x => x.Id)
        {
            EntitySetName = "Priced";
            UseETag(x => x.Price, x => x.Ratio, x => x.At, x => x.Count);
        }
    }

    [Theory]
    [InlineData("de-DE")]   // ',' decimal separator, '.' group separator
    [InlineData("fr-FR")]   // narrow-nbsp group separator
    [InlineData("sv-SE")]   // U+2212 minus sign
    [InlineData("th-TH")]   // Buddhist calendar — a bare DateTime.ToString() renders year 2569
    [InlineData("ar-SA")]   // Umm al-Qura calendar
    public void ETag_IsIdenticalAcrossCultures(string culture)
    {
        var profile = new PricedProfile();
        var model = new Priced
        {
            Id = 1,
            Price = -1234.56m,
            Ratio = 0.1 + 0.2,
            At = BaseInstant.AddTicks(1234567),
            Count = -9876543,
        };

        string enUs = CultureScope.Run("en-US", () => ETagOf(profile, model));
        string other = CultureScope.Run(culture, () => ETagOf(profile, model));

        Assert.Equal(enUs, other);
    }

    [Fact]
    public void ETag_IsIdenticalAcrossCultures_ForSpecialFloatingPointValues()
    {
        // Infinity renders as '∞' under de-DE with a bare ToString().
        var profile = new PricedProfile();
        foreach (double special in new[] { double.PositiveInfinity, double.NegativeInfinity, double.NaN })
        {
            var model = new Priced { Id = 1, Price = 1m, Ratio = special, At = BaseInstant, Count = 1 };
            Assert.Equal(
                CultureScope.Run("en-US", () => ETagOf(profile, model)),
                CultureScope.Run("de-DE", () => ETagOf(profile, model)));
        }
    }

    // ── 5. Adjacent collision vectors: null, separator, type ────────────────────

    public class Pair
    {
        public int Id { get; set; }
        public string? Left { get; set; }
        public string? Right { get; set; }
    }

    private sealed class PairProfile : EntitySetProfile<int, Pair>
    {
        public PairProfile() : base(x => x.Id)
        {
            EntitySetName = "Pairs";
            UseETag(x => x.Left, x => x.Right);
        }
    }

    /// <summary>Concatenation without length framing lets a value's bytes migrate across the
    /// boundary: ("ab","c") and ("a","bc") are different states and must hash differently.</summary>
    [Theory]
    [InlineData("ab", "c", "a", "bc")]
    [InlineData("", "abc", "abc", "")]
    // A value containing the byte the old implementation used as its delimiter.
    [InlineData("a\0b", "c", "a", "b\0c")]
    public void AdjacentValues_CannotBeReinterpretedAcrossTheBoundary(
        string leftA, string rightA, string leftB, string rightB)
    {
        var profile = new PairProfile();
        Assert.NotEqual(
            ETagOf(profile, new Pair { Id = 1, Left = leftA, Right = rightA }),
            ETagOf(profile, new Pair { Id = 1, Left = leftB, Right = rightB }));
    }

    /// <summary>Clearing a string property to <see langword="null"/> is a state change and must
    /// change the ETag; pre-fix, null and "" both contributed zero bytes.</summary>
    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    public void NullIsDistinguishableFromEmptyString(string? left, string? right)
    {
        var profile = new PairProfile();
        var withNulls = new Pair { Id = 1, Left = left, Right = right };
        var withEmpties = new Pair
        {
            Id = 1,
            Left = left ?? string.Empty,
            Right = right ?? string.Empty,
        };

        Assert.NotEqual(ETagOf(profile, withNulls), ETagOf(profile, withEmpties));
    }

    public class Loose
    {
        public int Id { get; set; }
        public object? Value { get; set; }
    }

    private sealed class LooseProfile : EntitySetProfile<int, Loose>
    {
        public LooseProfile() : base(x => x.Id)
        {
            EntitySetName = "Loose";
            UseETag(x => x.Value);
        }
    }

    /// <summary>A weakly-typed ETag input must not let two different CLR values that happen to
    /// render the same text collide.</summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("True", true)]
    [InlineData("42", 42L)]
    public void SameTextDifferentType_DoesNotCollide(string text, object other)
    {
        var profile = new LooseProfile();
        Assert.NotEqual(
            ETagOf(profile, new Loose { Id = 1, Value = text }),
            ETagOf(profile, new Loose { Id = 1, Value = other }));
    }

    [Fact]
    public void IntAndLongOfSameValue_DoNotCollide()
    {
        var profile = new LooseProfile();
        Assert.NotEqual(
            ETagOf(profile, new Loose { Id = 1, Value = 7 }),
            ETagOf(profile, new Loose { Id = 1, Value = 7L }));
    }

    // ── 6. byte[] row-version inputs keep working ───────────────────────────────

    public class Versioned
    {
        public int Id { get; set; }
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    private sealed class VersionedProfile : EntitySetProfile<int, Versioned>
    {
        public VersionedProfile() : base(x => x.Id)
        {
            EntitySetName = "Versioned";
            UseETag(x => x.RowVersion);
        }
    }

    [Fact]
    public void ByteArrayRowVersion_IsStillHashedRaw_AndDistinguishesValues()
    {
        var profile = new VersionedProfile();
        string a = ETagOf(profile, new Versioned { Id = 1, RowVersion = new byte[] { 1, 2, 3 } });
        string b = ETagOf(profile, new Versioned { Id = 1, RowVersion = new byte[] { 1, 2, 4 } });
        string aAgain = ETagOf(profile, new Versioned { Id = 1, RowVersion = new byte[] { 1, 2, 3 } });
        string empty = ETagOf(profile, new Versioned { Id = 1, RowVersion = Array.Empty<byte>() });

        Assert.NotEqual(a, b);
        Assert.Equal(a, aAgain);
        Assert.NotEqual(a, empty);
    }

    // ── 7. The other buffer shapes a row-version column arrives as ──────────────

    /// <summary>
    /// <see cref="ImmutableArray{T}"/>, <see cref="ReadOnlyMemory{T}"/>, <see cref="Memory{T}"/>
    /// and <see cref="ArraySegment{T}"/> over <see cref="byte"/> are all realistic row-version
    /// shapes, and none of them implements <see cref="IFormattable"/> or overrides
    /// <c>ToString()</c> — so before they were routed onto the binary path they formatted to their
    /// own type name, giving every row in the set the same ETag.
    /// </summary>
    public class BufferShapes
    {
        public int Id { get; set; }
        public ImmutableArray<byte> Immutable { get; set; }
        public ReadOnlyMemory<byte> ReadOnly { get; set; }
        public Memory<byte> Mutable { get; set; }
        public ArraySegment<byte> Segment { get; set; }
    }

    private sealed class ImmutableBufferProfile : EntitySetProfile<int, BufferShapes>
    {
        public ImmutableBufferProfile() : base(x => x.Id)
        {
            EntitySetName = "ImmutableBuffers";
            UseETag(x => x.Immutable);
        }
    }

    private sealed class ReadOnlyBufferProfile : EntitySetProfile<int, BufferShapes>
    {
        public ReadOnlyBufferProfile() : base(x => x.Id)
        {
            EntitySetName = "ReadOnlyBuffers";
            UseETag(x => x.ReadOnly);
        }
    }

    private sealed class MutableBufferProfile : EntitySetProfile<int, BufferShapes>
    {
        public MutableBufferProfile() : base(x => x.Id)
        {
            EntitySetName = "MutableBuffers";
            UseETag(x => x.Mutable);
        }
    }

    private sealed class SegmentBufferProfile : EntitySetProfile<int, BufferShapes>
    {
        public SegmentBufferProfile() : base(x => x.Id)
        {
            EntitySetName = "SegmentBuffers";
            UseETag(x => x.Segment);
        }
    }

    [Fact]
    public void ImmutableArrayRowVersion_DistinguishesValues()
    {
        var profile = new ImmutableBufferProfile();
        string a = ETagOf(profile, new BufferShapes { Id = 1, Immutable = ImmutableArray.Create<byte>(1, 2, 3) });
        string b = ETagOf(profile, new BufferShapes { Id = 1, Immutable = ImmutableArray.Create<byte>(9, 9, 9) });
        string aAgain = ETagOf(profile, new BufferShapes { Id = 1, Immutable = ImmutableArray.Create<byte>(1, 2, 3) });
        string uninitialized = ETagOf(profile, new BufferShapes { Id = 1 });
        string emptyArray = ETagOf(profile, new BufferShapes { Id = 1, Immutable = ImmutableArray<byte>.Empty });

        Assert.NotEqual(a, b);
        Assert.Equal(a, aAgain);
        // A `default` ImmutableArray is the absent state, not an empty buffer.
        Assert.NotEqual(uninitialized, emptyArray);
    }

    [Fact]
    public void ReadOnlyMemoryRowVersion_DistinguishesValues()
    {
        var profile = new ReadOnlyBufferProfile();
        string a = ETagOf(profile, new BufferShapes { Id = 1, ReadOnly = new byte[] { 1, 2, 3 } });
        string b = ETagOf(profile, new BufferShapes { Id = 1, ReadOnly = new byte[] { 9, 9, 9 } });

        Assert.NotEqual(a, b);
        Assert.Equal(a, ETagOf(profile, new BufferShapes { Id = 1, ReadOnly = new byte[] { 1, 2, 3 } }));
    }

    [Fact]
    public void MemoryRowVersion_DistinguishesValues()
    {
        var profile = new MutableBufferProfile();
        Assert.NotEqual(
            ETagOf(profile, new BufferShapes { Id = 1, Mutable = new byte[] { 1, 2, 3 } }),
            ETagOf(profile, new BufferShapes { Id = 1, Mutable = new byte[] { 9, 9, 9 } }));
    }

    [Fact]
    public void ArraySegmentRowVersion_DistinguishesValues()
    {
        var profile = new SegmentBufferProfile();
        Assert.NotEqual(
            ETagOf(profile, new BufferShapes { Id = 1, Segment = new ArraySegment<byte>(new byte[] { 1, 2, 3 }) }),
            ETagOf(profile, new BufferShapes { Id = 1, Segment = new ArraySegment<byte>(new byte[] { 9, 9, 9 }) }));
    }

    /// <summary>Every buffer shape carrying the same bytes hashes the same — deliberate: they are
    /// the same row version, however the model wraps them.</summary>
    [Fact]
    public void AllBufferShapes_WithTheSameBytes_AgreeWithByteArray()
    {
        byte[] raw = { 1, 2, 3 };
        string fromArray = ETagOf(new VersionedProfile(), new Versioned { Id = 1, RowVersion = raw });

        Assert.Equal(fromArray, ETagOf(new ImmutableBufferProfile(),
            new BufferShapes { Id = 1, Immutable = ImmutableArray.Create(raw) }));
        Assert.Equal(fromArray, ETagOf(new ReadOnlyBufferProfile(),
            new BufferShapes { Id = 1, ReadOnly = raw }));
        Assert.Equal(fromArray, ETagOf(new MutableBufferProfile(),
            new BufferShapes { Id = 1, Mutable = raw }));
        Assert.Equal(fromArray, ETagOf(new SegmentBufferProfile(),
            new BufferShapes { Id = 1, Segment = new ArraySegment<byte>(raw) }));
    }
}
