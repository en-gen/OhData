using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// What full <c>odataIdentifier</c> validation of dynamic-property keys costs on the SERIALIZE path
/// (#389), measured as the delta between the three states the check has been in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question.</b> <c>cab1de7</c> rejects a bag key that is null/empty/whitespace-only
/// (<c>string.IsNullOrWhiteSpace</c>, which reads one character of an ordinary key). <c>4a33b87</c>
/// widens that to the full CSDL §4.1 grammar, which reads EVERY character of EVERY key on every
/// serialized instance. An isolated tight-loop measurement put the widened check at 4.6 ns/key —
/// cheaper than the <c>declaredNames.Contains</c> hash lookup sitting beside it — while an in-situ
/// stopwatch harness put the same change at +26% on the serialize step, i.e. ~56 ns/key marginal.
/// A 12x disagreement between two measurements of one code path is not a detail; this class exists
/// to settle it under a real harness with error bars.
/// </para>
/// <para>
/// <b>Unit under measurement.</b> One <c>JsonSerializer.SerializeToNode</c> call over a 1,000-row
/// page typed as <c>IReadOnlyList&lt;object?&gt;</c> — the exact call and the exact argument shape
/// <c>OhDataEndpointFactory.SerializeBoundedCollection</c> makes for a collection GET. Not a tight
/// loop over the validator: the whole point is that the in-situ cost and the isolated cost disagree,
/// so measuring it in isolation again would answer the wrong question.
/// </para>
/// <para>
/// <b>Four arms, three of them differing by one branch.</b> A/B/C come from
/// <see cref="ArmedOpenTypeJsonOptions"/>, which transcribes the wrapper scaffolding (identical
/// across all three commits) and varies only the per-key check. <see cref="C_Shipped"/> is the real
/// <c>OpenTypeJsonOptions.Build</c>, carrying no transcription at all. <b>Read
/// <c>C_Replica</c> vs <c>C_Shipped</c> first</b>: they perform identical work, so the gap between
/// them is the transcription bias, measured in the same run as the numbers it would distort. If that
/// gap is not small relative to C−B, no A/B/C conclusion here is safe.
/// </para>
/// <para>
/// <b>Scenarios.</b> <see cref="KeyShape.RepeatingAscii"/> is the common case (20 names reused on
/// every row). <see cref="KeyShape.DistinctAscii"/> is 20,000 distinct keys — worth stating plainly,
/// because it is easy to call it the "cache-hostile" case and be wrong: the shipped cache is scoped
/// to the NON-ASCII fallback, so an all-ASCII page never consults it at all and this scenario
/// stresses string locality, not the cache. <see cref="KeyShape.DistinctNonAscii"/> is the case that
/// does stress it — 20,000 distinct non-ASCII keys against a 1024-entry table that fills on the
/// first operation and then freezes. <see cref="KeyShape.NoOpenType"/> is the control: no open
/// complex type in the model, so <c>Build</c> returns the base options reference-equal and all four
/// arms serialize through the SAME options instance. Any spread there is harness artefact, and every
/// other number in the run is suspect by exactly that much.
/// </para>
/// <para>
/// <b>Config.</b> Same job shape as <see cref="ExpandComparisonBenchmarks"/> and for the same
/// reasons — see its remarks for the full argument. <c>InvocationCount</c> is pinned so
/// BenchmarkDotNet's pilot stage cannot size ops-per-iteration differently between runs (that
/// bifurcation is what once got misread on this project as GC bimodality), <c>UnrollFactor(1)</c> is
/// required whenever it is, and <c>MinWarmupCount</c> floors adaptive warmup high enough to reach
/// tier-1 + dynamic PGO before measurement starts. <c>MaxWarmupCount</c> has to be raised alongside
/// it: BenchmarkDotNet's default max is 50 and it validates Min &lt; Max strictly.
/// </para>
/// </remarks>
[Config(typeof(OpenTypeKeyValidationConfig))]
[MemoryDiagnoser]
public class OpenTypeKeyValidationBenchmarks
{
    private sealed class OpenTypeKeyValidationConfig : ManualConfig
    {
        public OpenTypeKeyValidationConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
        }
    }

    public enum KeyShape
    {
        /// <summary>1,000 rows x 20 dynamic keys, the same 20 ASCII names on every row.</summary>
        RepeatingAscii,

        /// <summary>1,000 rows x 20 dynamic keys, all 20,000 distinct, all ASCII.</summary>
        DistinctAscii,

        /// <summary>1,000 rows x 20 dynamic keys, all 20,000 distinct, all non-ASCII.</summary>
        DistinctNonAscii,

        /// <summary>1,000 rows, no open complex type anywhere. Control.</summary>
        NoOpenType,
    }

    [ParamsAllValues]
    public KeyShape Shape { get; set; }

    private IReadOnlyList<object?> _page = Array.Empty<object?>();
    private JsonSerializerOptions _armA = null!;
    private JsonSerializerOptions _armB = null!;
    private JsonSerializerOptions _armCReplica = null!;
    private JsonSerializerOptions _armCShipped = null!;

    [GlobalSetup]
    public void Setup()
    {
        bool open = Shape != KeyShape.NoOpenType;

        // Exactly what OhDataEndpointFactory.MapAll does: build the container map off the EDM, then
        // hand it to Build. The EDM comes from ODataConventionModelBuilder, the same builder the
        // framework uses, so the DynamicPropertyDictionaryAnnotation that drives all of this is
        // produced by the real inference rather than written by hand.
        IEdmModel model = open ? BuildModel<BenchOpenRow>() : BuildModel<BenchClosedRow>();
        OpenTypeJsonOptions.OpenComplexTypeContainers containers =
            OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(model);

        // Guard the premise of every scenario rather than trusting the convention builder to have
        // inferred what this file assumes it did. A silent miss here would leave the open scenarios
        // measuring an unwrapped getter — i.e. four copies of arm A — and the result would look like
        // "the check is free".
        if (open && containers.IsEmpty)
            throw new InvalidOperationException("Expected BenchOpenMeta to be inferred as an open complex type.");
        if (!open && !containers.IsEmpty)
            throw new InvalidOperationException("Expected the control model to have no open complex type.");

        // Mirrors OhDataEndpointFactory's _pascalCaseSerializerOptions and the startup derivation
        // from it (PropertyNamingPolicy = the registration's, null by default).
        JsonSerializerOptions baseOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
        };

        _armA = ArmedOpenTypeJsonOptions.Build(baseOptions, containers, SerializeKeyCheckArm.None);
        _armB = ArmedOpenTypeJsonOptions.Build(baseOptions, containers, SerializeKeyCheckArm.NullOrWhiteSpace);
        _armCReplica = ArmedOpenTypeJsonOptions.Build(baseOptions, containers, SerializeKeyCheckArm.FullGrammar);
        // No Ignore()d names: this benchmark measures the key-validation loop, and the withheld-name
        // set only ever ADDS entries to the declared-name lookup that loop already performs — a
        // non-empty set here would measure a bigger HashSet, not a different code path.
        _armCShipped = OpenTypeJsonOptions.Build(
            baseOptions, containers, IgnoredPropertyJsonOptions.EmptyNameMap);

        _page = Shape switch
        {
            KeyShape.RepeatingAscii => BenchOpenTypeData.BuildRepeatingAscii(),
            KeyShape.DistinctAscii => BenchOpenTypeData.BuildDistinctAscii(),
            KeyShape.DistinctNonAscii => BenchOpenTypeData.BuildDistinctNonAscii(),
            _ => BenchOpenTypeData.BuildClosed(),
        };

        // Force each options instance to resolve and cache its JsonTypeInfo (which is when the
        // resolver modifier runs and the getter is wrapped) before any measurement, so the first
        // measured operation is not paying for contract construction. Also proves the arms actually
        // serialize: an invalid key or a shadowing key would throw here, at setup, rather than
        // mid-measurement.
        foreach (JsonSerializerOptions options in new[] { _armA, _armB, _armCReplica, _armCShipped })
        {
            _ = JsonSerializer.SerializeToNode(_page, options);
        }
    }

    private static IEdmModel BuildModel<TEntity>() where TEntity : class
    {
        ODataConventionModelBuilder builder = new ODataConventionModelBuilder();
        builder.EntitySet<TEntity>("Rows");
        return builder.GetEdmModel();
    }

    /// <summary>Arm A — the floor: getter wrapper and shadow lookup, no key validation.</summary>
    [Benchmark]
    public JsonNode? A_NoKeyCheck() => JsonSerializer.SerializeToNode(_page, _armA);

    /// <summary>
    /// Arm B — <c>cab1de7</c>, what ships today. Baseline, because the decision this measurement
    /// informs is "keep C or revert to B", so the ratio column should read as C-against-B.
    /// </summary>
    [Benchmark(Baseline = true)]
    public JsonNode? B_IsNullOrWhiteSpace() => JsonSerializer.SerializeToNode(_page, _armB);

    /// <summary>Arm C via the transcribed wrapper, calling the shipped validator.</summary>
    [Benchmark]
    public JsonNode? C_Replica() => JsonSerializer.SerializeToNode(_page, _armCReplica);

    /// <summary>
    /// Arm C through <c>OpenTypeJsonOptions.Build</c> itself — no transcription. The difference
    /// between this and <see cref="C_Replica"/> is the harness bias.
    /// </summary>
    [Benchmark]
    public JsonNode? C_Shipped() => JsonSerializer.SerializeToNode(_page, _armCShipped);
}
