using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.AspNetCore.OData.Query.Validator;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.UriParser;


namespace OhData;

// Extracted from OhDataEndpointFactory (#671 phase 1, pure move): the query-option gate --
// sigil/capability enforcement for every read route -- and the classifiers that decide whether a
// query fault is the client's option's fault (400) or the server's (500): #358/#385 arithmetic,
// #494/#662 provider translation. Called from OhDataEndpointFactory as QueryOptionGate.Foo(...).
internal static class QueryOptionGate
{
    // #358 (adversarial review R2, HIGH): signals that evaluating (enumerating/counting) a
    // $filter- or $orderby-ApplyTo'd query raised a client-triggerable arithmetic fault (div/mod
    // by zero, decimal overflow) -- thrown only by EvaluateQueryWithArithmeticFaultGuard below,
    // and caught by a dedicated clause on each collection-read route's OUTER try. A dedicated
    // type rather than reusing Microsoft.OData.ODataException: the $expand-pushdown Include-fallback
    // and pushed-query materialize sites in OhDataEndpointFactory and ExpandEngine already catch
    // ODataException for an unrelated reason (provider translation failures) and rewrite it into
    // a different, $expand-specific message -- reusing ODataException here would let those catches
    // intercept and mask this fault instead of it reaching the route's own 400 InvalidQueryOption
    // handling.
    internal sealed class FilterArithmeticFaultException(string message) : Exception(message);

    /// <summary>
    /// Evaluates <paramref name="materialize"/> — an enumeration (<c>ToArray</c>) or
    /// <c>LongCount</c> of the $filter/$orderby-ApplyTo'd query for the current request — and
    /// converts a <see cref="DivideByZeroException"/>/<see cref="OverflowException"/> raised
    /// DURING that specific call into a <see cref="FilterArithmeticFaultException"/> the calling
    /// route's own catch clause turns into 400 InvalidQueryOption.
    /// <para>
    /// Scope (adversarial review R2, HIGH — "narrow the try"): callers must wrap ONLY the
    /// materialization call itself, never handler invocation, <c>ApplyCollectionPipelineAsync</c>
    /// (nav delegates, batch handlers, ETag computation), or JSON serialization. An arithmetic
    /// fault raised from any of those is a genuine server bug, not a client input problem, and
    /// must reach the group-level exception filter (logged, 500) like any other unexpected
    /// exception — never be relabeled 400 just because it happens to share an exception type with
    /// a bad $filter.
    /// </para>
    /// <para>
    /// Guard (same review, same finding): only engages when <paramref name="options"/> actually
    /// carries a $filter or $orderby. Without either, no client-supplied expression could be the
    /// cause of a fault raised while enumerating this query — e.g. a profile's own
    /// <c>GetQueryable</c> Select projection dividing by zero is enumerated at this exact call
    /// site with NO $filter in the request, and must 500 (a genuine handler bug), not 400. When
    /// the guard doesn't match, the exception is left alone and propagates normally.
    /// </para>
    /// <para>
    /// Provider note: this only ever engages when the .NET runtime itself raises the exception —
    /// LINQ-to-Objects and EF Core's InMemory provider evaluate arithmetic client-side. A real
    /// relational provider (SQL Server, PostgreSQL, SQLite) may instead defer the fault into the
    /// database (raising a <c>DbException</c> subclass, or in SQLite's case treating division by
    /// zero as NULL and returning zero matching rows) — neither is caught here. That gap is
    /// tracked separately; see #358's follow-up issue for a provider-independent fix.
    /// </para>
    /// </summary>
    internal static T EvaluateQueryWithArithmeticFaultGuard<TModel, T>(
        Func<T> materialize, ODataQueryOptions<TModel> options, ILogger? logger, string entitySetName)
    {
        bool hasFilter = options.Filter is not null;
        bool hasOrderBy = options.OrderBy is not null;
        try
        {
            return materialize();
        }
        catch (Exception ex) when ((ex is DivideByZeroException or OverflowException) && (hasFilter || hasOrderBy))
        {
            logger?.LogDebug(ex,
                "OhData: arithmetic fault evaluating $filter/$orderby for {EntitySet}.", entitySetName);
            string option = (hasFilter, hasOrderBy) switch
            {
                (true, true) => "$filter or $orderby expression",
                (true, false) => "$filter expression",
                _ => "$orderby expression",
            };
            throw new FilterArithmeticFaultException($"The {option} could not be evaluated: {ex.Message}");
        }
    }

    // #494: signals that the underlying LINQ provider could not TRANSLATE the query shape the
    // request asked for -- thrown only by TranslateThenMaterialize below, and caught by the three
    // $expand-pushdown execution sites, which rewrite it into their own 400 message. A dedicated
    // type for the same reason FilterArithmeticFaultException is one: the catch clauses around the
    // call sites already catch ODataException for other reasons.
    internal sealed class QueryTranslationFailedException(Exception inner)
        : Exception(inner.Message, inner);

    // #494: the shapes a provider raises for a query it cannot translate. One predicate, three
    // consumers -- TranslateThenMaterialize, CountRootQuery and CanCompile -- because all three ask
    // the same question and must move together. ObjectDisposedException derives from
    // InvalidOperationException and is never a statement about the client's query.
    private static bool IsTranslationCandidate(Exception ex) =>
        ex is not ObjectDisposedException
        && ex is InvalidOperationException or NotSupportedException or Microsoft.OData.ODataException;

    /// <summary>
    /// Materializes a ROOT collection read, answering <c>400</c> when the provider could not
    /// translate a query shape the framework composed from the client's own options (#662).
    /// </summary>
    /// <remarks>
    /// Detection is <see cref="TranslateThenMaterialize"/>'s; attribution is
    /// <see cref="SourceCompilesOnEfCore"/>'s. The CHANGELOG and <c>CLAUDE.md</c> carry the measured
    /// shapes and the alternatives that were rejected.
    /// </remarks>
    internal static TModel[] MaterializeRootQuery<TModel>(
        IQueryable<TModel> source, Func<IQueryable<TModel>> build,
        ODataQueryOptions<TModel> options, ILogger? logger, string entitySetName)
    {
        try
        {
            return EvaluateQueryWithArithmeticFaultGuard(
                () => TranslateThenMaterialize(build), options, logger, entitySetName);
        }
        catch (QueryTranslationFailedException ex)
        {
            if (ComposedOptionPhrase(options) is not { } blamed || !SourceCompilesOnEfCore(source)) throw;

            throw RootTranslationFailure(ex.InnerException ?? ex, blamed, logger, entitySetName);
        }
    }

    /// <summary>
    /// Counts a ROOT query, answering <c>400</c> for the same condition (#662).
    /// </summary>
    /// <remarks>
    /// A scalar aggregate has no <c>GetEnumerator</c> seam, so the phase is established afterwards
    /// and only on the failure path, by asking whether the composed query compiles at all.
    /// </remarks>
    internal static long CountRootQuery<TModel>(
        IQueryable<TModel> source, IQueryable<TModel> counted,
        ODataQueryOptions<TModel> options, ILogger? logger, string entitySetName)
    {
        try
        {
            return EvaluateQueryWithArithmeticFaultGuard(
                () => counted.LongCount(), options, logger, entitySetName);
        }
        catch (Exception ex) when (IsTranslationCandidate(ex))
        {
            // Outside the exception FILTER deliberately: a filter runs before the failed frames
            // unwind, and the CLR swallows anything it throws itself, so a probe there would lose
            // its own faults without a trace.
            //
            // §11.2.9 forbids $orderby from affecting a count, so a framework-composed count query
            // never carries one and $filter is the only thing here that can be blamed. CanCompile
            // enumerates rather than counts, which is the right question: what must translate is the
            // composed shape, not the aggregate over it. If it DOES compile the fault was at
            // execution, so it is the server's and keeps its 500.
            if (options.Filter is null || !SourceCompilesOnEfCore(source) || CanCompile(counted)) throw;

            throw RootTranslationFailure(ex, "'$filter'", logger, entitySetName);
        }
    }

    /// <summary>
    /// Whether the source the profile handed over is an EF Core query that compiles on its own —
    /// i.e. whether a translation failure can honestly be attributed to what the framework added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two conditions, both confirmed defects when absent (#662); read that issue before removing
    /// one. The provider must be <b>EF Core</b>, because the phase split rests on
    /// <c>GetEnumerator()</c> doing no I/O, which is an EF property and not an
    /// <see cref="IQueryable"/> contract. And the <b>unmodified source</b> must compile, because
    /// presence of an option is not attribution.
    /// </para>
    /// <para>
    /// Not free: the second half compiles a query. It is only ever reached after one has already
    /// failed.
    /// </para>
    /// </remarks>
    private static bool SourceCompilesOnEfCore<TModel>(IQueryable<TModel> source) =>
        ExpandEngine.ResolveEfCoreAssembly(source) is not null && CanCompile(source);

    /// <summary>
    /// Whether the provider can compile <paramref name="query"/>, established without executing it.
    /// </summary>
    /// <remarks>
    /// The enumerator is created — which is what compiles the query, with no connection opened —
    /// and disposed. Callers gate on an EF Core provider first, because that is the only one this
    /// holds for.
    /// </remarks>
    private static bool CanCompile<TModel>(IQueryable<TModel> query)
    {
        try
        {
            using IEnumerator<TModel> probe = query.GetEnumerator();
            return true;
        }
        catch (Exception ex) when (IsTranslationCandidate(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Names the framework-composed options a root read can blame, quoted for the message, or
    /// <c>null</c> when it composed nothing and so can attribute nothing.
    /// </summary>
    /// <remarks>
    /// The quoting deliberately differs from <see cref="EvaluateQueryWithArithmeticFaultGuard"/>'s
    /// (<c>"$filter expression"</c>): those are two shipped messages, not one style.
    /// </remarks>
    private static string? ComposedOptionPhrase<TModel>(ODataQueryOptions<TModel> options) =>
        (options.Filter is not null, options.OrderBy is not null) switch
        {
            (true, true) => "'$filter' and '$orderby'",
            (true, false) => "'$filter'",
            (false, true) => "'$orderby'",
            _ => null,
        };

    /// <summary>
    /// The <c>400</c> a root translation failure becomes. It shares the clause
    /// <c>could not be translated by the underlying data provider</c> with the <c>$expand</c>
    /// pushdown's message — the diagnosis and the remedy differ, because the causes do.
    /// </summary>
    private static Microsoft.OData.ODataException RootTranslationFailure(
        Exception real, string blamedOption, ILogger? logger, string entitySetName)
    {
        // Warning, not Debug: #494 records that Debug is invisible at production log levels, so the
        // operator saw a spike of client errors and no server-side signal at all.
        logger?.LogWarning(real,
            "OhData: the root query failed to translate for {EntitySet}.", entitySetName);

        return new Microsoft.OData.ODataException(
            $"The {blamedOption} on '{entitySetName}' could not be processed: the query shape it " +
            "produced could not be translated by the underlying data provider. Simplify the " +
            "expression, or expose the members it addresses in the queryable this entity set reads " +
            "from.");
    }

    /// <summary>
    /// Enumerates <paramref name="build"/>'s query, separating the provider's TRANSLATION phase
    /// from its MATERIALIZATION phase so the two can be classified differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #494. The three $expand-pushdown execution sites used to wrap a whole
    /// <c>query.ToArray()</c> in <c>catch (ex is InvalidOperationException or
    /// NotSupportedException or ODataException)</c> and answer <c>400</c> "could not be translated
    /// by the underlying data provider". The premise -- recorded in those comments and in
    /// <c>ExpandPushdownExceptionClassificationTests</c> -- was that a real infrastructure fault
    /// arrives as a <c>DbException</c> subclass or a <c>TimeoutException</c>, so an
    /// <c>InvalidOperationException</c> could only be EF's translation failure. That premise is
    /// false, and the counterexamples are the ones that matter under load: SqlClient reports
    /// connection-pool exhaustion as a plain <c>InvalidOperationException</c> ("Timeout expired ...
    /// max pool size was reached") from <c>SqlConnection.Open</c>; <c>ObjectDisposedException</c>
    /// DERIVES from <c>InvalidOperationException</c>, so a disposed <c>DbContext</c> matched too;
    /// and EF's own "a second operation was started on this context instance" is an
    /// <c>InvalidOperationException</c>. Under any of those, an <c>$expand</c> request answered
    /// <c>400</c> -- telling client retry logic NOT to retry -- while the same request without
    /// <c>$expand</c> correctly answered <c>500</c>.
    /// </para>
    /// <para>
    /// The populations are separated POSITIVELY rather than by widening or narrowing the type
    /// list, because no type list can separate them: EF raises a translation failure BEFORE any
    /// command executes. <c>IQueryable&lt;T&gt;.GetEnumerator()</c> is what compiles the query
    /// (<c>EntityQueryProvider.Execute</c>), and the connection is not opened until the first
    /// <c>MoveNext()</c>. Verified on EF Core 10 / .NET 10.0.11 against SQLite: an untranslatable
    /// <c>Where</c> throws <c>InvalidOperationException</c> ("The LINQ expression ... could not be
    /// translated") out of <c>GetEnumerator()</c>, with the enumerator never created. So the
    /// <c>build</c> delegate and <c>GetEnumerator</c> are the translation window -- expression
    /// construction included, which is why this takes a factory rather than a query (the Include
    /// fallback builds its query by reflection and deliberately unwraps its own
    /// <c>TargetInvocationException</c> so the real type reaches this filter) -- and everything
    /// from the first <c>MoveNext</c> onward propagates untouched to the group-level exception
    /// filter, i.e. a logged <c>500</c>.
    /// </para>
    /// <para>
    /// <c>ObjectDisposedException</c> is excluded from the translation window as well: a disposed
    /// context can fail at compile time too, and "the object is gone" is never a statement about
    /// the client's query.
    /// </para>
    /// <para>
    /// A provider that translated lazily -- inside <c>MoveNext</c> rather than
    /// <c>GetEnumerator</c> -- would surface its translation failures as <c>500</c> here instead of
    /// <c>400</c>. That is the safe direction (loud either way, and never a false "retry is
    /// pointless"), and EF Core, the only provider this path is reachable with, does not do it.
    /// </para>
    /// </remarks>
    internal static T[] TranslateThenMaterialize<T>(Func<IQueryable<T>> build)
    {
        IEnumerator<T> enumerator;
        try
        {
            enumerator = build().GetEnumerator();
        }
        catch (Exception ex) when (IsTranslationCandidate(ex))
        {
            throw new QueryTranslationFailedException(ex);
        }

        // Materialization window. Nothing is caught here on purpose -- see the remarks above.
        using (enumerator)
        {
            var buffer = new List<T>();
            while (enumerator.MoveNext()) buffer.Add(enumerator.Current);
            return buffer.ToArray();
        }
    }

    // B1 fix: capability-flag enforcement (Minimal item 7 — "parse the option or reject it").
    // FilterEnabled/OrderByEnabled/SelectEnabled/ExpandEnabled/CountEnabled were previously
    // decorative on the GetQueryable and Priority-1 collection paths: the flags only drove EDM
    // model-bound capability annotations (Swagger/$metadata advertisement), never a runtime
    // gate. This helper is the runtime gate: a disabled option present in the query string is
    // rejected with a specific "UnsupportedQueryOption" error naming the option, mirroring the
    // wording the GetAll path already uses for its own wholesale $filter/$orderby/$top/$skip
    // rejection (it structurally cannot support those regardless of any flag).
    internal static IResult? CheckDisabledQueryOption(HttpContext ctx, string queryOptionName, bool enabled, string flagName)
    {
        if (enabled) return null;
        if (!ctx.Request.Query.ContainsKey(queryOptionName)) return null;
        return OhDataEndpointFactory.ODataError(400, "UnsupportedQueryOption",
            $"This resource does not support {queryOptionName}. Set {flagName} = true on the " +
            "profile (or the corresponding EntitySetDefaults property) to enable it.");
    }

    // Applies CheckDisabledQueryOption across the full $filter/$orderby/$select/$expand/$count
    // set — the gate used by the GetQueryable and Priority-1 collection GET routes. $filter and
    // $orderby are optionally skipped (checkFilterOrderBy: false) on paths that already reject
    // them structurally regardless of the flag (the GetAll path, which has no ApplyTo pipeline).
    internal static IResult? CheckCollectionQueryOptionCapabilities(
        HttpContext ctx, IEntitySetEndpointSource source, string[] implementedOptions,
        bool checkFilterOrderBy = true)
    {
        // #196/#359: reject every $-prefixed system option this route does not implement, rather
        // than ignoring it silently (Minimal-conformance item 7 — "parse the option or reject
        // it"). The set is the caller's, because the GetAll path implements one option fewer.
        IResult? unimplemented = CheckUnsupportedSystemQueryOptions(ctx, implementedOptions);
        if (unimplemented is not null) return unimplemented;

        if (checkFilterOrderBy)
        {
            IResult? r = CheckDisabledQueryOption(ctx, "$filter", source.FilterEnabled, nameof(IEntitySetEndpointSource.FilterEnabled));
            if (r is not null) return r;
            r = CheckDisabledQueryOption(ctx, "$orderby", source.OrderByEnabled, nameof(IEntitySetEndpointSource.OrderByEnabled));
            if (r is not null) return r;
        }

        IResult? sr = CheckDisabledQueryOption(ctx, "$select", source.SelectEnabled, nameof(IEntitySetEndpointSource.SelectEnabled));
        if (sr is not null) return sr;
        sr = CheckDisabledQueryOption(ctx, "$expand", source.ExpandEnabled, nameof(IEntitySetEndpointSource.ExpandEnabled));
        if (sr is not null) return sr;
        sr = CheckDisabledQueryOption(ctx, "$count", source.CountEnabled, nameof(IEntitySetEndpointSource.CountEnabled));
        return sr;
    }

    // ── Unsupported system query options: one matcher, every read route ──────────────────
    //
    // #359/#380/#353. The rule is the '$' SIGIL, not a name allowlist: a non-'$' key is a custom
    // option (Part 2 §5.2) and passes through, a '$' key the route does not implement is refused
    // whether or not any OData version defines it. Sets are PER ROUTE and deliberately differ.
    //
    // 501 IS "CAN'T", 400 IS "WON'T". 501 where no profile setting could make this request work on
    // this route (§9.3.1 and §13.1.1 item 7, both MUSTs); 400 where it could and the adopter
    // declined -- a capability flag, an allowlist, a malformed value (#402). So the same option can
    // be 501 on one entity set and 400 on another; that is per-resource conformance, not a bug.
    //
    // OrdinalIgnoreCase matches MS (ODataQueryOptions.IsSystemQueryOption lowercases by default), so
    // $Select stays honoured. $format is in every set: it is handled once on the group filter.
    //
    // Full derivation, the measured before/after and the rejected uniform-400 reading: #359.

    private const string FormatOption = "$format";

    // The two collection GET routes over an IQueryable -- GetQueryable (Priority-2) and Priority-1.
    // GetAll has its own array below. The listed options are the ones the ROUTE implements; whether
    // this profile permits them is CheckDisabledQueryOption's separate question.
    //
    // #475: this array is now the PRIORITY-2 set only. It used to serve both, and the comment here
    // declined to split it on the grounds that the Priority-1 contract hands the whole
    // ODataQueryOptions to the profile, so "the framework does not read it" is not "the request does
    // not honour it". True -- but the consequence was that a P1 profile with no $search handling
    // answered 200 with the FULL collection to a client that asked for a subset, which a client
    // cannot detect (an unfiltered result is indistinguishable from a search that matched
    // everything), and §11.2.5 makes refusing it a MUST.
    //
    // The split is resolved by asking the profile instead of guessing: see
    // BuildPriority1ImplementedOptions. Microsoft.AspNetCore.OData ignores $search without an
    // ISearchBinder by choice ("If the developer doesn't provide the search binder, let's ignore the
    // $search clause"), so this is a deliberate divergence from MS -- the same one #359/#380/#353
    // already made, where §11.2.5's MUST outweighs aligning with MS.
    // #475: the Priority-1 route's implemented-option set, from the profile's own declaration.
    // $format is always present -- it never reaches a route handler (§11.2.10 is negotiated once on
    // the group filter) and cannot change a row. Built once per entity set at startup.
    internal static string[] BuildPriority1ImplementedOptions(OhDataSystemQueryOption honoured)
    {
        var names = new List<string>(10) { FormatOption };
        if (honoured.HasFlag(OhDataSystemQueryOption.Filter)) names.Add("$filter");
        if (honoured.HasFlag(OhDataSystemQueryOption.OrderBy)) names.Add("$orderby");
        if (honoured.HasFlag(OhDataSystemQueryOption.Top)) names.Add("$top");
        if (honoured.HasFlag(OhDataSystemQueryOption.Skip)) names.Add("$skip");
        if (honoured.HasFlag(OhDataSystemQueryOption.Select)) names.Add("$select");
        if (honoured.HasFlag(OhDataSystemQueryOption.Expand)) names.Add("$expand");
        if (honoured.HasFlag(OhDataSystemQueryOption.Count)) names.Add("$count");
        if (honoured.HasFlag(OhDataSystemQueryOption.SkipToken)) names.Add("$skiptoken");
        if (honoured.HasFlag(OhDataSystemQueryOption.Search)) names.Add("$search");
        return names.ToArray();
    }

    internal static readonly string[] s_collectionImplementedOptions =
    {
        "$filter", "$orderby", "$top", "$skip", "$select", "$expand", "$count",
        "$search", "$skiptoken", FormatOption,
    };

    // The GetAll (simple read) collection route. Identical except that $skiptoken is NOT
    // implemented: #201's ApplyGetAllPaging continues with $skip and nothing on this path ever
    // reads a $skiptoken, so accepting one meant discarding a client's continuation in silence.
    // $filter/$orderby are listed even though this path always refuses them — it does so earlier,
    // with a message naming GetQueryable as the remedy, and one condition must not produce two
    // different envelopes depending on which check saw it first.
    internal static readonly string[] s_getAllCollectionImplementedOptions =
    {
        "$filter", "$orderby", "$top", "$skip", "$select", "$expand", "$count",
        "$search", FormatOption,
    };

    // GET /{Set}/$count. §11.2.9 partitions the options and specifies each class:
    //   affects the count ($filter, $search) -- applied where this route can, refused where it
    //     cannot, because ignoring one returns a wrong number under a 200 (#353);
    //   "MUST NOT be affected by $top, $skip, $orderby, or $expand" -- accepted and IGNORED, since
    //     under §13.1.1 item 7 ignoring them IS following the specification;
    //   everything else -- refused like any other route.
    // $select and $format are accepted-and-ignored by ruling: $select changes an item's shape, never
    // its membership, and $format is already answered by the group filter, so refusing it would give
    // one option two envelopes. Accept: application/xml still 406s. MS behaves the same way, and
    // Microsoft.OData.Client depends on it -- it appends /$count to a query string it has already
    // built, so LongCount() after OrderBy/Take/Skip sends those along. Do not narrow this set; #579
    // reverted exactly that.
    internal static readonly string[] s_countRouteImplementedOptions =
    {
        "$filter", "$top", "$skip", "$orderby", "$expand", "$select", FormatOption,
    };

    // GET /{Set}({key}). A single entity: there is nothing to filter, order, window or count.
    // $select/$expand are the whole implemented surface (#380).
    internal static readonly string[] s_getByIdImplementedOptions =
    {
        "$select", "$expand", FormatOption,
    };

    // GET /{Set}({key})/{Prop} and its /$value (#560). Both ride GetById and read NO query option
    // at all — the handler goes straight from the property accessor to the envelope — so even
    // $select, which the sibling entity route implements, is refused rather than silently dropped.
    internal static readonly string[] s_propertyRouteImplementedOptions = { FormatOption };

    // GET /{Set}({key})/{Nav} — the delegate-backed navigation COLLECTION route, which parses
    // $select/$orderby/$skip/$top/$count directly off the query string.
    private static readonly string[] s_navCollectionImplementedOptions =
    {
        "$select", "$orderby", "$skip", "$top", "$count", FormatOption,
    };

    // GET /{Set}({key})/{Nav} where the navigation is SINGLE-VALUED (HasOptional/HasRequired).
    // Same route template, different handler branch, and the branch reads NO query option at all:
    // it serializes the related entity through ODataEntityNode, whose own remark records that no
    // $expand is possible on that path, and every option the collection branch applies is applied
    // inside `if (navIsCollection)` — $select in BuildNavEnvelope, $orderby/$skip/$top/$count in
    // the branch body. Sharing the collection set here therefore accepted and DISCARDED
    // $select/$orderby/$top/$count under a 200 (measured), which is #380's own defect statement —
    // "known, implemented-elsewhere options being silently dropped on a route that does not
    // implement them" — on a route this change touches, and left GET /Set(1)?$orderby=X a 400
    // beside GET /Set(1)/Owner?$orderby=X a 200.
    //
    // The refusal is the fix rather than implementing $select here: this change is about refusing
    // what is not implemented, and $select on a single-valued nav needs the projection, the
    // allowlist validation and the @odata.context projection suffix that ODataEntityResult carries
    // and ODataEntityNode does not — a feature, with its own wire-shape decisions.
    private static readonly string[] s_navSingleImplementedOptions = { FormatOption };

    // GET /{Set}({key})/{Nav}/$count. The same §11.2.9 partition as the entity-set /$count one
    // level down, resolved differently in exactly one class because this handler applies even
    // less: it invokes the navigation delegate and counts what comes back, applying NO data option
    // whatsoever.
    //
    //   AFFECTS THE COUNT -- $filter AND $search are both refused here, where the entity-set route
    //         refuses only $search. That is not an inconsistency; it is the same rule reaching a
    //         different answer on a route with no IQueryable. §11.2.9 requires the count to be
    //         taken after applying them, this route cannot, and ignoring either would answer a
    //         wrong number under a 200.
    //   MUST NOT AFFECT THE COUNT -- $top/$skip/$orderby/$expand, and $select by the positive
    //         half's "items matching the request" (see the ruling on the entity-set array), are
    //         accepted and ignored. They were accepted no-ops here from 1.0.0 through 1.6.0 and
    //         are again.
    //   NEITHER -- refused, as everywhere else.
    //
    // $format carries the same "not refused, never negotiated" meaning it carries above.
    internal static readonly string[] s_navCountImplementedOptions =
    {
        "$top", "$skip", "$orderby", "$expand", "$select", FormatOption,
    };

    // GET /{Set}({key})/{Nav}?$skip=N — the #313 bare-$expand continuation. $skip only.
    internal static readonly string[] s_expandContinuationImplementedOptions =
    {
        "$skip", FormatOption,
    };

    // The four BOUND operation routes. An operation's own parameters are non-'$' keys, so the sigil
    // rule never examines them; what it examines is the system options, of which #357 and #543 made
    // $top/$skip real here.
    //
    // #359's SECOND half lands here and nowhere else: BuildNextPageLinkWithSkip copies the WHOLE
    // incoming query string, so on an ungated route an unrecognized option came back inside the
    // server's own nextLink under a 200 -- `?$unknown=evil` -> `"@odata.nextLink":
    // ".../TopRated?%24unknown=evil&%24skip=2"`.
    //
    // $top/$skip are listed UNCONDITIONALLY, not off ReturnType the way TopSkipSupported is, and the
    // asymmetry is deliberate: the bound is applied in the RUNTIME collection branch, so such a route
    // really can emit a $skip continuation, and deriving the set from the declared type would make
    // the server refuse a link it had just issued. Where the result is not a collection they are
    // accepted no-ops -- the same answer the /$count routes give, reached by a different road.
    internal static readonly string[] s_boundOperationImplementedOptions =
    {
        "$top", "$skip", FormatOption,
    };

    // The two UNBOUND operation routes — GET|POST /{Op} on the outer group. They serve the
    // handler's result through PreRenderedJson with no collection branch, no MaxTop bound and no
    // continuation link, so $top/$skip are not implemented there and are refused rather than
    // ignored. (That they carry no ceiling at all is a separate, pre-existing gap.)
    internal static readonly string[] s_unboundOperationImplementedOptions = { FormatOption };

    /// <summary>
    /// Returns the first <c>$</c>-prefixed query-string key that is not in
    /// <paramref name="implemented"/>, or <c>null</c> when the request carries none.
    /// <para>
    /// Short-circuits on an empty query string before touching anything, so a request with no
    /// options at all costs one <see cref="IQueryCollection.Count"/> read and allocates nothing —
    /// load-bearing on <c>GetById</c>, which is the hottest read route and deliberately builds no
    /// <see cref="ODataQueryOptions{TEntity}"/> unless <c>$select</c>/<c>$expand</c> is present.
    /// The check itself only reads key strings: no parsing, no options construction.
    /// </para>
    /// </summary>
    // #576: every route that consults this can answer 501, and each one declares .Produces(501)
    // at its own registration -- keep the two in step when adding a route to either side.
    internal static string? FindUnsupportedSystemQueryOption(HttpContext ctx, string[] implemented)
    {
        IQueryCollection query = ctx.Request.Query;
        if (query.Count == 0) return null;

        foreach (string key in query.Keys)
        {
            if (key.Length == 0 || key[0] != '$') continue;

            bool ok = false;
            for (int i = 0; i < implemented.Length; i++)
            {
                if (string.Equals(key, implemented[i], StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    break;
                }
            }
            if (!ok) return key;
        }
        return null;
    }

    // The generic envelope, shared verbatim with the four names #196 already rejected on the
    // collection routes so that generalising the rule did not re-word a message that has shipped
    // since 1.0.0. Used by the collection GETs, /$count, GetById and the navigation /$count.
    internal static IResult? CheckUnsupportedSystemQueryOptions(HttpContext ctx, string[] implemented)
    {
        string? option = FindUnsupportedSystemQueryOption(ctx, implemented);
        return option is null
            ? null
            : OhDataEndpointFactory.ODataError(501, "UnsupportedQueryOption", $"The query option '{option}' is not supported.");
    }

    // The navigation route keeps its own wording, which names the options it does support — it
    // has shipped that way since S1/B1 and is more useful than the generic line on a route whose
    // supported set is small and non-obvious. Same matcher underneath.
    //
    // isCollection is the nav's STARTUP-captured NavigationRouteDefinition.IsCollection, so the set
    // and the message describe the branch the request will actually reach. The two branches share
    // one route template and used to share one set; see s_navSingleImplementedOptions.
    //
    // NEITHER message names $format, which both branches accept. Deliberate, and the two are
    // consistent about it: $format is not a DATA query option — §11.2.10 negotiation is handled
    // once on the group filter and never reaches a route handler — which is the framing the
    // single-valued line states outright and the collection line inherits. The collection line's
    // bytes are additionally pinned as unchanged by the generalisation
    // (NavCollection_ClosedListNames_KeepTheirExactEnvelope), so adding $format to it would be a
    // wire change to a message that has shipped since S1/B1 in exchange for restating something
    // no route refuses.
    internal static IResult? CheckNavUnsupportedQueryOptions(HttpContext ctx, bool isCollection)
    {
        string? option = FindUnsupportedSystemQueryOption(
            ctx, isCollection ? s_navCollectionImplementedOptions : s_navSingleImplementedOptions);
        if (option is null) return null;

        return OhDataEndpointFactory.ODataError(501, "UnsupportedQueryOption", isCollection
            ? $"This navigation route does not support {option}. Supported query options " +
              "are $select, $orderby, $skip, $top, and $count."
            : $"This navigation route does not support {option}. A single-valued navigation " +
              "route supports no data query options.");
    }

    // #202: per-entity-set validation settings, built once per set from the source's resolved
    // complexity limits (MaxExpansionDepth default 3, node counts 10000/1000/1000 as before) so an
    // implementor can tighten them per profile or globally via WithDefaults. AllowedQueryOptions=All
    // etc. is retained so the only checks these run are the per-property allowlist annotations and
    // the complexity ceilings — $top/$skip/$count keep their own dedicated enforcement (see the
    // ValidatePropertyAllowlists remark). MaxExpansionDepth is now enforced (was hardcoded 0/disabled):
    // a $expand nesting deeper than the limit — including a $levels that resolves deeper (#206) — is
    // rejected with 400 by Microsoft's SelectExpandQueryValidator rather than silently truncated.
    internal static ODataValidationSettings BuildValidationSettings(IEntitySetEndpointSource source) => new()
    {
        AllowedQueryOptions = AllowedQueryOptions.All,
        AllowedArithmeticOperators = AllowedArithmeticOperators.All,
        AllowedFunctions = AllowedFunctions.AllFunctions,
        AllowedLogicalOperators = AllowedLogicalOperators.All,
        MaxExpansionDepth = source.MaxExpansionDepth,
        MaxAnyAllExpressionDepth = source.MaxAnyAllExpressionDepth,
        MaxNodeCount = source.MaxFilterNodeCount,
        MaxOrderByNodeCount = source.MaxOrderByNodeCount,
    };

    // #385: a literal zero divisor is refused BEFORE the query executes, so every provider gives the
    // same answer. #358 catches DivideByZeroException/OverflowException, which only fires where the
    // CLR evaluates the expression -- measured, the same URL split three ways: LINQ-to-Objects and
    // EF InMemory raised and answered 400, SQLite evaluated x/0 to NULL and answered 200 with zero
    // rows, and SQL Server (Msg 8134) and PostgreSQL (SQLSTATE 22012) raised DbException subclasses
    // that reached the group filter as an unhandled 500 -- an anonymous client could drive those at
    // will on the two databases most deployments use.
    //
    // Catching DbException instead was rejected: it is not portable, and it would also catch a
    // connection dropped mid-enumeration and report it as a client error, which is #494's defect.
    // An AST walk is provider-independent, costs no execution, and is honest -- X div 0 is
    // unevaluable on every backend, so 400 is right everywhere. SQLite's empty 200 was a wrong
    // answer under a success status, of the same family as #353/#354.
    //
    // Scope is the LITERAL divisor only. `A div B` where some row's B is 0 stays provider-dependent
    // and is still covered by #358's runtime guard where the CLR evaluates it.
    internal static string? FindLiteralZeroDivisor<TModel>(ODataQueryOptions<TModel> options)
    {
        if (options.Filter?.FilterClause?.Expression is { } filter && DividesByLiteralZero(filter))
        {
            return "$filter";
        }

        for (OrderByClause? clause = options.OrderBy?.OrderByClause; clause is not null; clause = clause.ThenBy)
        {
            if (clause.Expression is { } expression && DividesByLiteralZero(expression))
            {
                return "$orderby";
            }
        }

        return null;
    }

    private static bool DividesByLiteralZero(QueryNode node)
    {
        switch (node)
        {
            case BinaryOperatorNode binary:
                if (binary.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Modulo
                    && IsLiteralZero(binary.Right))
                {
                    return true;
                }
                return DividesByLiteralZero(binary.Left) || DividesByLiteralZero(binary.Right);

            // ODL wraps operands in ConvertNode, so a naive `is ConstantNode` test on the right-hand
            // side misses `Quantity div 0` outright.
            case ConvertNode convert:
                return DividesByLiteralZero(convert.Source);
            case UnaryOperatorNode unary:
                return DividesByLiteralZero(unary.Operand);

            // A divisor inside contains(...)/round(...) or inside any()/all() is just as unevaluable.
            case SingleValueFunctionCallNode call:
                return call.Parameters.Any(DividesByLiteralZero);
            case CollectionFunctionCallNode collectionCall:
                return collectionCall.Parameters.Any(DividesByLiteralZero);
            case AnyNode any:
                return any.Body is not null && DividesByLiteralZero(any.Body);
            case AllNode all:
                return all.Body is not null && DividesByLiteralZero(all.Body);

            default:
                return false;
        }
    }

    // Matched by VALUE across the numeric types rather than by parsing the constant's text: the
    // literal's CLR type varies with how it was written (measured: `div 0` yields Int32, `div 0.0`
    // yields Single), and a text comparison would miss one spelling or invent matches.
    private static bool IsLiteralZero(QueryNode node)
    {
        while (node is ConvertNode convert) node = convert.Source;
        if (node is not ConstantNode constant || constant.Value is null) return false;

        return constant.Value switch
        {
            sbyte v => v == 0,
            byte v => v == 0,
            short v => v == 0,
            ushort v => v == 0,
            int v => v == 0,
            uint v => v == 0,
            long v => v == 0,
            ulong v => v == 0,
            float v => v == 0,
            double v => v == 0,
            decimal v => v == 0,
            _ => false,
        };
    }

    // #254 (E1): enforce the per-navigation MaxExpandTop ceiling on an EXPLICIT nested $top at every
    // depth of the $expand tree, before any handler or query runs. Runs IN ADDITION TO (never instead
    // of) ValidatePropertyAllowlists on each collection read path.
    //
    // Deliberately depth- AND pushdown-independent: a nested $top is rejected on a delegate-backed
    // navigation too. That mirrors the root MaxTop, which 400s an over-large $top on every read path
    // regardless of how the collection is ultimately loaded — the ceiling is a statement about what
    // the client may ask for, not about how the server would have served it.
    //
    // Returns the 400 OData error to return, or null when every nested $top is within the ceiling
    // (including the no-ceiling case, MaxExpandTop = null).
    internal static IResult? ValidateNestedTopCeiling(SelectExpandClause? clause, int? cap)
    {
        if (clause is null || cap is not int max) return null;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            if (item.TopOption is long top && top > max)
            {
                string nav = item.PathToNavigationProperty.FirstSegment.Identifier;
                return OhDataEndpointFactory.ODataError(400, "InvalidQueryOption",
                    $"The value of '$top' ({top}) on the expanded navigation '{nav}' exceeds the maximum allowed value ({max}).");
            }

            IResult? deeper = ValidateNestedTopCeiling(item.SelectAndExpand, cap);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    // #429: reject a $expand tree with more expansions than the resolved MaxExpandBreadth, before any
    // handler or query runs.
    //
    // Depth is one axis. Translation cost multiplies by ~3 per level AND by the navigations expanded
    // at each level, so capping depth alone leaves the other factor free -- measured at the DEFAULT
    // depth of 3 on a six-navigation model, 4.1 s of single-core CPU for a 1,952-byte response. EF's
    // compiled-query cache is no defence: each distinct navigation SUBSET is a distinct key.
    //
    // The count spans the WHOLE TREE because a per-level cap of B under depth D still admits B^D
    // (6^6 = 55,986 at the shipped ceiling). Counting DISTINCT NAMES would be weaker still -- the
    // most expensive shapes measured reuse six names over six levels.
    //
    // Deliberately pushdown-independent, like ValidateNestedTopCeiling: it is a statement about what
    // the client may ASK for, not about how the server would have served it.
    internal static IResult? ValidateExpandBreadth(SelectExpandClause? clause, int cap, int maxExpansionDepth)
    {
        if (clause is null) return null;
        int count = CountExpandNodes((SelectExpandClause)clause, maxExpansionDepth, cap);
        if (count <= cap) return null;

        // The message states the LIMIT, not the request's actual count: CountExpandNodes stops as
        // soon as the limit is passed, because an adversarial tree is exactly the input we must not
        // walk in full in order to reject it. The limit is the actionable half anyway.
        return OhDataEndpointFactory.ODataError(400, "InvalidQueryOption",
            $"The request expands more than {cap} navigations. '$expand' is limited to {cap} " +
            "navigation expansions counted across every level of the expansion tree (a " +
            "'$levels=N' expansion counts as N). Request fewer navigations, or raise " +
            "MaxExpandBreadth on the entity set profile or in WithDefaults.");
    }

    // Counts navigation expansions in the whole $expand tree, stopping as soon as <paramref
    // name="cap"/> is exceeded. A $levels=N item counts as N — its resolved level count, through the
    // SAME ResolveLevelsBudget the loaders use (#428), so the guard cannot disagree with them about
    // what a $levels resolves to — because that is what it costs: one nested projection level each,
    // exactly like the equivalent explicit chain.
    private static int CountExpandNodes(SelectExpandClause clause, int maxExpansionDepth, int cap)
    {
        int count = 0;
        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            count += item.LevelsOption is { } lv
                ? Math.Max(1, ExpandEngine.ResolveLevelsBudget(lv.IsMaxLevel, lv.Level, maxExpansionDepth, ExpandEngine.MaxNestedExpandDepth))
                : 1;
            if (count > cap) return count;

            if (item.SelectAndExpand is { } nested)
            {
                count += CountExpandNodes(nested, maxExpansionDepth, cap - count);
                if (count > cap) return count;
            }
        }
        return count;
    }
}
