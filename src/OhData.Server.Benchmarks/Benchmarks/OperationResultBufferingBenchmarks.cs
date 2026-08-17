using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// What #396's fix costs on the non-faulting path: the delta between serializing an operation
/// result straight into the response body (deferred, what <c>Results.Json</c> does) and serializing
/// it to a buffer first and writing that (eager, what <c>PreRenderedJson</c> does).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs measuring at all.</b> The #396 fix moves serialization of a bound/unbound
/// operation result from IResult-execution time into the endpoint-filter pipeline, so a fault can
/// still be converted to a 500 envelope instead of truncating an already-committed 200. The only
/// cost that buys is one full-payload <c>byte[]</c> allocation plus a copy: <c>SerializeToUtf8Bytes</c>
/// serializes into a pooled writer (the same one <c>SerializeAsync</c> uses) and then copies out to
/// an exactly-sized array. Both arms buffer; the eager arm buffers twice. "Don't regress the
/// non-faulting case by buffering large results unnecessarily" is the right worry, so the number is
/// measured rather than asserted either way.
/// </para>
/// <para>
/// <b>Unit under measurement.</b> Serialize one operation result and write it to
/// <see cref="Stream.Null"/> — the two arms differ by exactly the one line the fix changed, and
/// nothing else. Stream.Null stands in for the response body so the measurement isolates the
/// serialization/copy delta from HTTP and socket cost, which is unchanged by the fix and would
/// otherwise dominate. Read the ratio as an upper bound on the real-request impact: over a real
/// connection the same delta is amortised across the whole request.
/// </para>
/// <para>
/// <b>Why the shipped arm is not the fastest one here.</b> <see cref="Eager_PooledBufferWriter"/>
/// wins at every size (0.66x / 0.97x / 1.43x against the deferred baseline, with no steady-state
/// allocation and no LOH traffic), and it is deliberately NOT what ships. Driving a
/// <c>Utf8JsonWriter</c> directly means constructing <c>JsonWriterOptions</c> by hand from the
/// <c>JsonSerializerOptions</c> — Encoder, Indented, IndentCharacter, IndentSize, NewLine, MaxDepth,
/// SkipValidation — which <c>SerializeToUtf8Bytes</c> derives internally and correctly. Transcribing
/// that mapping is the same class of mistake as enumerating somebody else's exception types: get one
/// member wrong, or miss one a future runtime adds, and the response bytes change. #396's own
/// requirement is that a non-faulting operation stay byte-identical, so the arm that is
/// byte-identical by construction wins over the arm that is 18-43% faster on large payloads. The
/// pooled arm stays here as the measured record of the road not taken, in case the trade is ever
/// worth revisiting behind byte-for-byte differential tests.
/// </para>
/// <para>
/// <b>Sizes.</b> <see cref="ResultSize.Scalar"/> is the overwhelmingly common shape (a small DTO,
/// tens of bytes). <see cref="ResultSize.Page"/> is a realistic operation result. <see cref="ResultSize.Huge"/>
/// is the case the worry is actually about — a multi-megabyte payload, where an extra full-payload
/// allocation lands on the Large Object Heap. <see cref="MemoryDiagnoserAttribute"/> is on so the
/// allocation delta is visible next to the time delta rather than inferred from it.
/// </para>
/// <para>
/// <b>Config.</b> Same job shape and the same reasons as
/// <see cref="OpenTypeKeyValidationBenchmarks"/>: pinned <c>InvocationCount</c> so the pilot stage
/// cannot size ops-per-iteration differently between runs, <c>UnrollFactor(1)</c> as that requires,
/// and a warmup floor high enough to reach tier-1 + dynamic PGO before measurement.
/// </para>
/// </remarks>
[Config(typeof(OperationResultBufferingConfig))]
[MemoryDiagnoser]
public class OperationResultBufferingBenchmarks
{
    private sealed class OperationResultBufferingConfig : ManualConfig
    {
        public OperationResultBufferingConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
        }
    }

    public sealed class BenchOpRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public DateTimeOffset Stamp { get; set; }
        public decimal Amount { get; set; }
    }

    public enum ResultSize
    {
        /// <summary>A single small DTO — what a bound function returns in the common case.</summary>
        Scalar,

        /// <summary>1,000 rows (~200 KB) — a realistic operation result.</summary>
        Page,

        /// <summary>50,000 rows (~10 MB) — the LOH case the "don't buffer large results" worry is about.</summary>
        Huge,
    }

    [ParamsAllValues]
    public ResultSize Size { get; set; }

    private object _result = null!;
    private JsonSerializerOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Mirrors OhDataEndpointFactory._pascalCaseSerializerOptions.
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
        };

        _result = Size switch
        {
            ResultSize.Scalar => Row(0),
            ResultSize.Page => BuildRows(1_000),
            _ => BuildRows(50_000),
        };

        // Force JsonTypeInfo resolution out of the measured region.
        _ = JsonSerializer.SerializeToUtf8Bytes(_result, _options);
    }

    private static BenchOpRow Row(int i) => new()
    {
        Id = i,
        Name = "operation-result-row-" + i,
        Description = "a description long enough to make the payload representative of a real DTO",
        Stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
        Amount = 1234.56m + i,
    };

    private static List<BenchOpRow> BuildRows(int count)
    {
        var rows = new List<BenchOpRow>(count);
        for (int i = 0; i < count; i++) rows.Add(Row(i));
        return rows;
    }

    /// <summary>
    /// Deferred — what <c>Results.Json</c> did, and the reason a fault could truncate an already-
    /// committed 200. Baseline, because the decision is "what does moving off this cost".
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task Deferred_SerializeIntoResponseBody()
        => await JsonSerializer.SerializeAsync(Stream.Null, _result, _options);

    /// <summary>Eager — what <c>PreRenderedJson</c> does: serialize to bytes, then one write.</summary>
    [Benchmark]
    public async Task Eager_SerializeToBytesThenWrite()
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(_result, _options);
        await Stream.Null.WriteAsync(utf8.AsMemory(0, utf8.Length));
    }

    /// <summary>Eager into a growable heap buffer — drops <c>SerializeToUtf8Bytes</c>'s final
    /// exact-size copy, but still allocates (and, past 85 KB, still lands on the LOH).</summary>
    [Benchmark]
    public async Task Eager_ArrayBufferWriter()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, _result, _options);
        }
        await Stream.Null.WriteAsync(buffer.WrittenMemory);
    }

    /// <summary>Eager into a pooled buffer — no steady-state allocation and no LOH traffic at all,
    /// at the cost of a rent/return around the write.</summary>
    /// <remarks>The rent/return round-trip is the cost this arm exists to price, so the dispose
    /// stays INSIDE the measured method — every other arm likewise pays for its buffer in full
    /// here. Hoisting it into teardown would flatter this arm and make the four no longer
    /// comparable. <c>using var</c> and the try/finally it replaced compile to the same exception
    /// handler over the same try region (verified against the IL); only the finally body differs,
    /// by the null test <c>using</c> emits for a reference type.</remarks>
    [Benchmark]
    public async Task Eager_PooledBufferWriter()
    {
        using var buffer = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, _result, _options);
        }
        await Stream.Null.WriteAsync(buffer.WrittenMemory);
    }

    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
        private int _written;

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Grow(int sizeHint)
        {
            if (sizeHint < 1) sizeHint = 1;
            if (_buffer.Length - _written >= sizeHint) return;

            int required = _written + sizeHint;
            byte[] next = ArrayPool<byte>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
            _buffer.AsSpan(0, _written).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            if (_buffer.Length == 0) return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }
}
