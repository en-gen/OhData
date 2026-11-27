using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace OhData;

// SPIKE #867 -- PROTOTYPE ONLY, never a PR. See spike/NOTES.md.

/// <summary>What happens when the row source faults after response bytes were flushed.</summary>
internal enum StreamErrorMode
{
    /// <summary>Let the exception escape ExecuteAsync (Kestrel aborts the connection).</summary>
    Rethrow,
    /// <summary>HttpContext.Abort(): reset the connection.</summary>
    Abort,
    /// <summary>Close the array and append an in-band OData <c>error</c> member, then end the object.</summary>
    InBand,
}

/// <summary>
/// Streams a top-level collection: prefix, rows pulled lazily in chunks and written with a single
/// <see cref="Utf8JsonWriter"/>, flushed per chunk, then the trailing nextLink. The first chunk is
/// read and shaped BEFORE this result is returned, so everything that can fail before byte one
/// (translation, the first rows, the select/etag pipeline) still fails inside the filter pipeline.
/// </summary>
internal sealed class StreamedCollectionResult<TModel> : IResult
{
    private readonly IEnumerator<TModel> _rows;
    private readonly int _pageLimit;
    private readonly int _chunkSize;
    private readonly Func<TModel[], Task<(JsonArray Items, List<string>? SelectedProps)>> _pipeline;
    private readonly JsonSerializerOptions _options;
    private readonly OhDataRegistration _registration;
    private readonly long? _count;
    private readonly string? _preNextLink;
    private readonly Func<string?> _overflowLink;
    private readonly Func<List<string>?, string> _contextUrl;

    private int _taken;
    private bool _done;
    private bool _hasMore;
    private JsonArray _firstItems = new();
    private List<string>? _selectedProps;

    private StreamedCollectionResult(
        IEnumerator<TModel> rows, int pageLimit, int chunkSize,
        Func<TModel[], Task<(JsonArray, List<string>?)>> pipeline, JsonSerializerOptions options,
        OhDataRegistration registration, long? count, string? preNextLink,
        Func<string?> overflowLink, Func<List<string>?, string> contextUrl)
    {
        _rows = rows;
        _pageLimit = pageLimit;
        _chunkSize = chunkSize;
        _pipeline = pipeline;
        _options = options;
        _registration = registration;
        _count = count;
        _preNextLink = preNextLink;
        _overflowLink = overflowLink;
        _contextUrl = contextUrl;
    }

    /// <summary>Reads and shapes the first chunk (pre-headers), then returns the deferred result.</summary>
    internal static async Task<IResult> PrepareAsync(
        IEnumerator<TModel> rows, int pageLimit, OhDataRegistration registration,
        Func<Func<TModel[]>, TModel[]> firstChunkGuard,
        Func<TModel[], Task<(JsonArray, List<string>?)>> pipeline, JsonSerializerOptions envelopeOptions,
        long? count, string? preNextLink, Func<string?> overflowLink, Func<List<string>?, string> contextUrl)
    {
        var result = new StreamedCollectionResult<TModel>(
            rows, pageLimit, Math.Max(1, registration.StreamChunkSize), pipeline, envelopeOptions,
            registration, count, preNextLink, overflowLink, contextUrl);
        try
        {
            TModel[] first = firstChunkGuard(result.ReadChunk);
            (result._firstItems, result._selectedProps) = await pipeline(first).ConfigureAwait(false);
        }
        catch
        {
            rows.Dispose();
            throw;
        }

        return result;
    }

    private TModel[] ReadChunk()
    {
        var chunk = new List<TModel>(_chunkSize);
        while (chunk.Count < _chunkSize)
        {
            if (!_rows.MoveNext())
            {
                _done = true;
                break;
            }

            // The probe row: proves a further page exists, is never written.
            if (_taken == _pageLimit)
            {
                _hasMore = true;
                _done = true;
                break;
            }

            chunk.Add(_rows.Current);
            _taken++;
        }

        return chunk.ToArray();
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        System.Threading.Interlocked.Increment(ref _registration.StreamedResponseCount);
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";

        var writerOptions = new JsonWriterOptions
        {
            Encoder = _options.Encoder,
            Indented = _options.WriteIndented,
            MaxDepth = _options.MaxDepth,
#if NET9_0_OR_GREATER
            IndentCharacter = _options.IndentCharacter,
            IndentSize = _options.IndentSize,
            NewLine = _options.NewLine,
#endif
        };
        var writer = new Utf8JsonWriter(response.BodyWriter, writerOptions);
        CancellationToken ct = httpContext.RequestAborted;

        try
        {
            // If the whole page fit in the first chunk, every shape fact is known before byte one and
            // the buffered member order (context, count, nextLink, value) is reproduced exactly.
            bool endKnown = _done;
            string? upfrontLink = _preNextLink ?? (endKnown && _hasMore ? _overflowLink() : null);

            writer.WriteStartObject();
            writer.WriteString("@odata.context", _contextUrl(_selectedProps));
            if (_count.HasValue) writer.WriteNumber("@odata.count", _count.Value);
            if (upfrontLink is not null) writer.WriteString("@odata.nextLink", upfrontLink);
            writer.WritePropertyName("value");
            writer.WriteStartArray();
            WriteRows(writer, _firstItems);
            await FlushAsync(writer, response, ct).ConfigureAwait(false);

            try
            {
                while (!_done)
                {
                    TModel[] chunk = ReadChunk();
                    (JsonArray items, _) = await _pipeline(chunk).ConfigureAwait(false);
                    WriteRows(writer, items);
                    await FlushAsync(writer, response, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (
                _registration.StreamErrorMode != StreamErrorMode.Rethrow
                && ex is not OperationCanceledException)
            {
                if (_registration.StreamErrorMode == StreamErrorMode.Abort)
                {
                    httpContext.Abort();
                    return;
                }

                writer.WriteEndArray();
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteString("code", "InternalServerError");
                writer.WriteString("message", "An error occurred while streaming the response.");
                writer.WriteEndObject();
                writer.WriteEndObject();
                await FlushAsync(writer, response, ct).ConfigureAwait(false);
                return;
            }

            writer.WriteEndArray();
            if (!endKnown && _hasMore && _overflowLink() is { } trailing)
                writer.WriteString("@odata.nextLink", trailing);
            writer.WriteEndObject();
            await FlushAsync(writer, response, ct).ConfigureAwait(false);
        }
        finally
        {
            _rows.Dispose();
        }
    }

    private void WriteRows(Utf8JsonWriter writer, JsonArray items)
    {
        foreach (JsonNode? item in items)
        {
            if (item is null) writer.WriteNullValue();
            else item.WriteTo(writer, _options);
        }
    }

    private static async Task FlushAsync(Utf8JsonWriter writer, HttpResponse response, CancellationToken ct)
    {
        writer.Flush();
        await response.BodyWriter.FlushAsync(ct).ConfigureAwait(false);
    }
}
