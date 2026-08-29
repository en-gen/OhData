using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OData.ModelBuilder;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #489: Ignore() loses its EDM half under AdvancedConfigure ────────────────────────────────
//
// Ignore() withholds a property on TWO levels: the EDM removal rides the _configurators pipeline,
// and runtime suppression (routes, wire, PATCH binding) is applied separately. Overriding
// AdvancedConfigure returns from VisitModelBuilder BEFORE the configurator pipeline runs, so the EDM
// half is auto-ejected while the runtime half still applies. That is the stated contract of the
// eject hatch and it is correct — the developer has taken full EDM ownership.
//
// The consequence is not one a reader derives from either half. With both in play the property is
// back in $metadata and query-addressable while the wire still omits it, so $filter over it is a
// VALUE ORACLE: the value is never served, but it can be probed one predicate at a time. In the
// ordinary case the EDM removal makes the property indistinguishable from one that never existed —
// $filter naming it dies at ODL parse with the same "could not find a property named…" a genuinely
// nonexistent property produces, so the 400 cannot confirm existence.
//
// This is deliberately NOT fixed: re-imposing OhData's Ignore() on top of AdvancedConfigure would
// defeat the hatch, and it would be arbitrary — HasMany/HasOptional/HasRequired ride the SAME
// pipeline and stay ejected, so there is no principle that singles out Ignore()'s configurator. The
// mitigation is the one WarnWireShapeIsFlat established for exactly this shape: a legitimate
// configuration whose consequence is not predictable from either half gets one startup Warning
// naming it, plus explicit documentation.
//
// This suite pins THREE things:
//   1. the oracle itself, as characterization on both sides, so a future change cannot quietly move
//      the behaviour the docs now describe;
//   2. the warning's content;
//   3. its targeting — silent when Ignore() is used without the hatch, and silent when the hatch is
//      taken and the developer re-applies the EDM removal by hand.
public class Issue489IgnoreUnderAdvancedConfigureTests
{
    private static IEnumerable<string> IgnoreEjectWarnings(WarningCapture capture) =>
        capture.Warnings.Where(w => w.Contains(
            "is still declared in the EDM because this profile overrides AdvancedConfigure",
            StringComparison.Ordinal));

    private static Task<TestFixture> BuildAsync(WarningCapture capture, Action<OhDataBuilder> configure) =>
        TestHostBuilder.BuildAsync(
            configure,
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));

    // ── 1. the oracle, characterized ─────────────────────────────────────────────────────────

    /// <summary>
    /// The disclosure channel, stated as behaviour: the wire omits <c>Secret</c> on every row, and a
    /// <c>$filter</c> over it is accepted and answers truthfully. Both halves are asserted in one
    /// test because it is their combination that is the oracle.
    /// </summary>
    [Fact]
    public async Task UnderAdvancedConfigure_TheIgnoredPropertyIsWithheldOnTheWireButFilterable()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            capture, o => o.AddEntitySetProfile<Ia489EjectedProfile>());

        JsonElement page = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Ia489Ejected");
        JsonElement first = page.GetProperty("value")[0];
        Assert.False(first.TryGetProperty("Secret", out _));

        // $metadata advertises it, which is the disclosure that holds even when the override never
        // re-enables a single query capability.
        string metadata = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("Name=\"Secret\"", metadata, StringComparison.Ordinal);

        using HttpResponseMessage hit =
            await fx.Client.GetAsync("/odata/Ia489Ejected?$filter=Secret eq 'alpha-secret'");
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        JsonElement hitBody = JsonSerializer.Deserialize<JsonElement>(await hit.Content.ReadAsStringAsync());
        Assert.Equal(1, hitBody.GetProperty("value").GetArrayLength());
        Assert.Equal(1, hitBody.GetProperty("value")[0].GetProperty("Id").GetInt32());

        using HttpResponseMessage miss =
            await fx.Client.GetAsync("/odata/Ia489Ejected?$filter=Secret eq 'not-a-secret'");
        Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
        JsonElement missBody = JsonSerializer.Deserialize<JsonElement>(await miss.Content.ReadAsStringAsync());
        Assert.Equal(0, missBody.GetProperty("value").GetArrayLength());
    }

    /// <summary>
    /// The control that makes the test above mean something: WITHOUT the hatch the same
    /// <c>$filter</c> is rejected, and the rejection is the unknown-property one — indistinguishable
    /// from a property that never existed.
    /// </summary>
    [Fact]
    public async Task WithoutAdvancedConfigure_TheIgnoredPropertyIsNotFilterable()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            capture, o => o.AddEntitySetProfile<Ia489PlainProfile>());

        using HttpResponseMessage filtered =
            await fx.Client.GetAsync("/odata/Ia489Plain?$filter=Secret eq 'alpha-secret'");
        Assert.Equal(HttpStatusCode.BadRequest, filtered.StatusCode);

        using HttpResponseMessage nonexistent =
            await fx.Client.GetAsync("/odata/Ia489Plain?$filter=NoSuchThing eq 'x'");
        Assert.Equal(HttpStatusCode.BadRequest, nonexistent.StatusCode);
    }

    // ── 2. the warning's content ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnderAdvancedConfigure_OneWarningPerIgnoredPropertyStillInTheEdm()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            capture, o => o.AddEntitySetProfile<Ia489EjectedProfile>());

        string warning = Assert.Single(IgnoreEjectWarnings(capture));
        Assert.Contains("Ia489Ejected", warning, StringComparison.Ordinal);
        Assert.Contains("Secret", warning, StringComparison.Ordinal);
        Assert.Contains("$filter", warning, StringComparison.Ordinal);
        Assert.Contains("$metadata", warning, StringComparison.Ordinal);
        // It must name the remedy, not merely the symptom.
        Assert.Contains("EntityType.Ignore", warning, StringComparison.Ordinal);
    }

    // ── 3. targeting ─────────────────────────────────────────────────────────────────────────

    /// <summary>Ignore() without the hatch removes the property from the EDM — nothing to warn about.</summary>
    [Fact]
    public async Task WithoutAdvancedConfigure_NoWarning()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            capture, o => o.AddEntitySetProfile<Ia489PlainProfile>());

        Assert.Empty(IgnoreEjectWarnings(capture));
    }

    /// <summary>
    /// The hatch taken AND the EDM removal re-applied by hand — the configuration the documentation
    /// prescribes. The warning must key off the EDM as built, not off the mere presence of an
    /// override, or it fires on the correct configuration and teaches developers to ignore it.
    /// </summary>
    [Fact]
    public async Task UnderAdvancedConfigure_WithTheEdmRemovalReapplied_NoWarning()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            capture, o => o.AddEntitySetProfile<Ia489ReappliedProfile>());

        Assert.Empty(IgnoreEjectWarnings(capture));

        // Bounding half: the re-applied removal really did take effect, and $filter is otherwise
        // live on this profile — so the 400 is the removal, not a missing capability.
        using HttpResponseMessage filtered =
            await fx.Client.GetAsync("/odata/Ia489Reapplied?$filter=Secret eq 'alpha-secret'");
        Assert.Equal(HttpStatusCode.BadRequest, filtered.StatusCode);

        using HttpResponseMessage live =
            await fx.Client.GetAsync("/odata/Ia489Reapplied?$filter=Name eq 'Alpha'");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    /// <summary>A profile that ignores nothing stays silent under the hatch too.</summary>
    [Fact]
    public async Task UnderAdvancedConfigure_WithNoIgnoredProperties_NoWarning()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            capture, o => o.AddEntitySetProfile<Ia489NoIgnoreProfile>());

        Assert.Empty(IgnoreEjectWarnings(capture));
    }
}

// ── fixtures ─────────────────────────────────────────────────────────────────────────────────

public sealed class Ia489Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
}

internal abstract class Ia489ProfileBase : EntitySetProfile<int, Ia489Widget>
{
    protected static readonly List<Ia489Widget> Store = new()
    {
        new() { Id = 1, Name = "Alpha", Secret = "alpha-secret" },
        new() { Id = 2, Name = "Beta", Secret = "beta-secret" },
    };

    protected Ia489ProfileBase() : base(x => x.Id)
    {
        FilterEnabled = true;
        SelectEnabled = true;
        Ignore(x => x.Secret);
        GetQueryable = _ => Task.FromResult(Store.AsQueryable());
        GetById = (id, _) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
    }
}

/// <summary>#489: Ignore() plus the eject hatch — the EDM half is lost.</summary>
internal sealed class Ia489EjectedProfile : Ia489ProfileBase
{
    public Ia489EjectedProfile() => EntitySetName = "Ia489Ejected";

    // The shape docs/architecture.md prescribes for the hatch: the override owns key setup AND the
    // query capabilities, because taking the hatch is what disabled OhData's automatic versions.
    protected override void AdvancedConfigure(EntitySetConfiguration<Ia489Widget> configuration)
    {
        configuration.EntityType.HasKey(x => x.Id);
        configuration.EntityType.Filter().Select();
    }
}

/// <summary>Control: Ignore() with no override — both halves apply.</summary>
internal sealed class Ia489PlainProfile : Ia489ProfileBase
{
    public Ia489PlainProfile() => EntitySetName = "Ia489Plain";
}

/// <summary>Control: the hatch taken and the EDM removal re-applied by hand.</summary>
internal sealed class Ia489ReappliedProfile : Ia489ProfileBase
{
    public Ia489ReappliedProfile() => EntitySetName = "Ia489Reapplied";

    protected override void AdvancedConfigure(EntitySetConfiguration<Ia489Widget> configuration)
    {
        configuration.EntityType.HasKey(x => x.Id);
        configuration.EntityType.Filter().Select();
        configuration.EntityType.Ignore(x => x.Secret);
    }
}

/// <summary>Control: the hatch taken, nothing ignored.</summary>
internal sealed class Ia489NoIgnoreProfile : EntitySetProfile<int, Ia489Widget>
{
    public Ia489NoIgnoreProfile() : base(x => x.Id)
    {
        EntitySetName = "Ia489NoIgnore";
        GetById = (id, _) => Task.FromResult<Ia489Widget?>(new Ia489Widget { Id = id });
    }

    protected override void AdvancedConfigure(EntitySetConfiguration<Ia489Widget> configuration)
    {
        configuration.EntityType.HasKey(x => x.Id);
    }
}
