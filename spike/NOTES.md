# Spike #867: streamed top-level collection GET (prototype notes)

Not production code, never a PR. Branch `feature/867-streaming-spike`, base `f728d6a`.
Tags: [EST] measured or read, [INF] inferred, [OPEN] unresolved.

## What was built

- `OhDataRegistration.StreamCollections` (internal, default false) switches the top-level collection GET
  to `StreamedCollectionResult<TModel>` (`src/OhData.AspNetCore/StreamingSpike.cs`) on the
  GetQueryable route and the GetODataQueryable (Priority-1) route. Off = the buffered path, untouched.
  Also internal: `StreamChunkSize` (default 128), `StreamErrorMode`, `StreamedResponseCount`.
- Flow: `OpenRootEnumerator` (translation window, #494/#662 classification kept) -> read and shape the
  FIRST chunk before returning the IResult (so translation faults, first-rows faults, the select/etag
  pipeline all still fail inside the filter pipeline with the normal status) -> `ExecuteAsync` writes
  `{"@odata.context":..,["@odata.count":..,]value:[` with one `Utf8JsonWriter` over
  `Response.BodyWriter`, then pulls further chunks, runs each through the UNCHANGED
  `ExpandEngine.ApplyCollectionPipelineAsync` (so etag, select strip, nav omission are the shipped code),
  `WriteTo`s each row, `FlushAsync` per chunk, then `]` and the trailing `@odata.nextLink`.
- The has-more probe row is the same Take(pageSize+1) the buffered path uses; it is read, never written.

## Result: byte identity [EST]

`StreamingSpikeByteIdentityTests` (51 tests, all pass): full response bytes plus every header except
Date/Content-Length/Transfer-Encoding, buffered vs streamed, each streamed leg also asserting
`StreamedResponseCount` moved (no vacuous passes through the buffered fallback).

Byte-identical at chunk sizes 128, 4 and 1: plain page, `$count=true`, `$top/$skip`, `$select`,
`$select+$count`, `$orderby`, `$filter`, `$filter+$orderby+$count`, empty page (with and without
`$count`); server-driven paging with nextLink where the page fits the first chunk; Priority-1 plain,
`$count` (profile TotalCount), `$top`, `$filter+$orderby`, empty, `$skip`; EF SQLite plain, `$select`,
`$orderby+$filter`. Stop rule not triggered: plain pages matched on the first build.

Not identical (expected, asserted as such):

1. nextLink position when the page spans more than one chunk (`..._PageSpansChunks_BytesDifferOnlyInMemberOrder`).
   Buffered order is context, count, nextLink, value. Whether a next page exists is only known when the
   probe row is read, after the page's rows are on the wire, so streamed order is context, count, value,
   nextLink. JSON is semantically equal (order-insensitive comparison asserted); bytes are not.
   When the whole page is inside the first chunk the prototype knows the end before byte one and
   reproduces the buffered order exactly. [OPEN] whether any client depends on member order (the OData
   JSON examples put nextLink after `value`; I did not re-check the clause text).
2. Framing: TestServer synthesizes a Content-Length for both, so the identity tests cannot see framing.
   On Kestrel both buffered and streamed small success responses are `Transfer-Encoding: chunked`, no
   Content-Length (`Kestrel_FramingOfSuccessResponse_IsRecorded`) [EST]. The shipped buffered path
   (`Results.Json` -> `WriteAsJsonAsync`) already streams SERIALIZATION to the socket; what it buffers is
   the materialized row array and the `JsonArray`. So framing is not a regression; the saving available
   is the materialization, not the serialization (see Finding A).

## What blocks streaming, by shape

Streamed in the prototype: GetQueryable and Priority-1 with filter/orderby/select/top/skip/count/
server paging; select pushdown projections; per-row ETag injection (runs inside the unchanged pipeline,
per chunk; [OPEN] no dedicated ETag byte-identity test was written).

Blocked (falls back to the buffered path, asserted by `StreamedResponseCount == 0`):

| Shape | Why it blocks | Fallback in prototype |
|---|---|---|
| any `$expand` (engaged pushdown, Include fallback, delegate/batch) | `ExpandLevelAsync` batches navigation handlers over the WHOLE page (a per-chunk call turns one batch call into N/chunk calls and changes handler semantics); pushdown rewrites the root query into a projection/count-carrier whose per-parent counts are index-aligned to the page; `ShapePushedExpandsInJson`, paging links on parents and `EnforceRawExpandCeiling` are whole-page post-passes. The ceiling is a 400 and must be decided before byte one, but a breach can be found at any row | `HasExpand` check, buffered |
| Priority-1 `$count=true` with no profile `TotalCount` | the count is the row total (`items.Length`), unknowable before the last row | buffered |
| Priority-1 `$count` + paging without TotalCount | buffered path throws a 500 `InvalidOperationException` (profile contract breach) decided from `frameworkNextLink`, which is only known after the page is read | covered by the row above (never reaches streaming) |
| GetQueryable `$count=true` | NOT blocked: it is a separate `LongCount` query run before byte one, same as buffered (one extra round trip, unchanged) | streams |
| nextLink before `value` | see above; needs the probe row | after `]` |
| `FilterArithmeticFault` (DivideByZero/Overflow) at row N | 400 `InvalidQueryOption` in buffered; in-memory providers fault lazily at any row. Streamed: only the first chunk is under the guard, a fault after it is a post-header failure, not a 400 | first chunk only |
| exception mapping (`ConfigureExceptions`) / `HandlerFaultException` classification of enumeration faults | the mapping wraps the handler call, and the buffered group filter catch covers enumeration inside the route. After headers there is no catch | none |

Other behavioural differences found [EST/INF]:

- Stable order: paged or `$top` requests get `EnsureStableOrder` (ORDER BY key). On an in-memory
  `IQueryable` that is a full sort before the first row, so "streaming" an in-memory source only helps
  after the sort; on EF it is SQL ORDER BY and rows do stream. [EST] seen when the first probe fixture
  failed at 500 before byte one under default `MaxTop`.
- Resource lifetime: the row enumerator (EF `DbDataReader`, and the scoped `DbContext`) now lives for the
  whole response write. A slow client holds a database connection that the buffered path releases before
  writing. [INF]
- Logging/observability: a post-header fault logs "Request finished ... 200" (the #396 problem).
- Chunk granularity loses rows on a fault: rows already pulled into the failing chunk are discarded
  (rows 257..299 below were pulled and never written). [EST]
- Utf8JsonWriter options are hand-mapped from `JsonSerializerOptions` (Encoder, WriteIndented, MaxDepth,
  and IndentCharacter/IndentSize/NewLine on net9+): exactly the transcription risk the #396 note in
  `PreRenderedJson` warns about. Only default options and default encoder were byte-tested. [OPEN]
  `SkipValidation` is left false (writer validates structure), which differs from STJ's internal setting.

## Error after headers (row source throws at row 300; 256 rows already flushed; chunk 128)

Fixture: `SpikeFaultProfile`, `MaxTop = null` so no stable-order sort pulls the fault before byte one.
`StreamingSpikeErrorProbeTests`. Kestrel is the wire; TestServer is in-process contrast.

| Mode | Kestrel client sees | TestServer client sees |
|---|---|---|
| buffered (switch off) | `500` + `{"error":{"code":"InternalServerError",...}}`, Content-Length 111, valid JSON | same |
| (a) rethrow after flush | `200`, chunked; 256 complete rows (18,464 bytes) then `HttpIOException: The response ended prematurely (ResponseEnded)`; body is not valid JSON; no error body; the status the client already saw is 200 | `200`; 256 rows; `IOException` with inner `InvalidOperationException: boom at row 300` |
| (b) `HttpContext.Abort()` | `200`, chunked; connection reset (`IOException` <- `SocketException: forcibly closed`); rows received varied across three runs: 184, 256, 256 (RST discards bytes the client had not read yet) so the surviving prefix is nondeterministic | `IOException` <- `OperationCanceledException: The application aborted the request`, 0 body bytes read |
| (c) in-band `error` member | `200`, 256 rows, array closed, then `"error":{"code":"InternalServerError","message":"..."}}`; whole body is VALID JSON; no transport signal | same |

Reading: (a) is detectable at the transport layer (incomplete chunked body) by any HTTP client that
checks for it, but the status is already 200 and the client has 256 usable rows. (b) is detectable and
loud but the usable prefix is nondeterministic. (c) is the only mode that lets a client distinguish
"complete" from "failed" with a parseable body, and also the only one where a client that reads just
`value` silently treats a truncated result as complete [INF]. [OPEN] the OData protocol's in-stream error
clause (memoized as Part 1 section 9.5) was not re-read against the spec text; the member shape used in (c)
is a guess at conformance, not a verified one. Mixing (a) or (c) with `Preference-Applied`/nextLink
semantics is not defined by this prototype.

Incidental [EST]: with a default `MaxTop` the same fixture faulted during the stable-order sort, i.e.
before byte one, and answered a clean 500 envelope even with streaming on.

## Finding A: where the memory actually is [EST, smoke only, not a measurement]

A smoke run of the BDN class (ShortRun, 3 iterations, not a final measurement) and of the console probe
showed allocation per request nearly identical buffered vs streamed (e.g. EF SQLite GetODataQueryable,
1000 rows: 1477 KB vs 1393 KB; 10000 rows 12958 KB vs 13094 KB). The prototype streams the OUTPUT but
still builds a `JsonNode` graph per chunk through `ApplyCollectionPipelineAsync` (SerializeBounded ->
JsonObject per row), and the CLR entities. So allocation per request is dominated by the JsonNode
materialization, which chunking bounds in lifetime but not in total allocated bytes. Peak working set
should drop from O(page) to O(chunk); total allocation will not, until rows are serialized without
`JsonNode` (typed `JsonSerializer` per row), which is incompatible with the select-strip, nav-omission and
etag-injection stages as written. The final measurements (peak heap, gen counts, TTFB, 30 requests per
cell) are what answer this; they were deliberately not run.

## Harness map

- Tests: `src/OhData.AspNetCore.Tests/StreamingSpikeByteIdentityTests.cs`, `StreamingSpikeErrorProbeTests.cs`.
- BDN: `src/OhData.Server.Benchmarks/Benchmarks/StreamingCollectionBenchmarks.cs`
  (`--filter "*StreamingCollectionBenchmarks*"`; `[MemoryDiagnoser]`, `[ShortRunJob]`; params Backend x
  Route x Rows; methods Buffered (baseline) and Streamed flip the registration switch per call).
- Console harness: `dotnet run -c Release --project src/OhData.Server.Benchmarks -- --stream-probe
  [--host testserver|kestrel] [--runs 30] [--rows 100,1000,10000] [--out dir] [--smoke]`
  (`Streaming/StreamProbe.cs`). Per request: sampled peak managed heap delta, precise allocated bytes,
  gen0/1/2 counts, TTFB (ResponseHeadersRead then first body byte), total time; median and max per cell;
  markdown and CSV with commit, dirty flag, CPU, RAM, OS, .NET version, GC mode and run count. The
  Kestrel mode is `--host kestrel` (real loopback socket). Arms are interleaved, first arm alternates,
  and the harness throws if an arm did not stream (or did).
- Run BDN and the probe from this worktree only, never from the main checkout root.
