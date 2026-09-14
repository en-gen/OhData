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
using static OhData.OhDataEndpointFactory;

namespace OhData;

// Extracted from OhDataEndpointFactory (#671 phase 1, pure move): the $expand engine --
// $expand pushdown, nav suppression, levels/windowing, and EF-Include reflection. See
// CLAUDE.md's $expand-pushdown and nav-suppression sections for the design history; this file
// is where that machinery now lives, called from OhDataEndpointFactory as ExpandEngine.Foo(...).
internal static class ExpandEngine
{
    // Unified collection pipeline: Serialize → ETag → Expand → Select.
    // Serialises exactly once using the owned jsonOptions (defensively falls back to the
    // PascalCase _pascalCaseSerializerOptions if ever null — in practice it is always supplied).
    // #294: a nested $top/$skip rejected against a delegate-backed navigation somewhere in the
    // expand tree throws Microsoft.OData.ODataException out of ExpandLevelAsync — every caller of
    // this method already catches that exception and converts it to 400 InvalidQueryOption.
    internal static async Task<(JsonArray Items, List<string>? SelectedProps)> ApplyCollectionPipelineAsync(
        object[] originalItems,
        ODataQueryOptions options,
        IEntitySetEndpointSource source,
        IEntitySetEndpointSource requestSource,
        JsonSerializerOptions? jsonOptions,
        IEdmEntityType? rootEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        CancellationToken ct,
        HashSet<string>? pushedLevelsNavNames = null,
        IReadOnlyList<EngagedExpand>? engagedExpandNavs = null,
        bool singleEntityRead = false)
    {
        // Stage 1: Serialize once using the configured naming policy.
        // #325/#326 (Option B): bounded by the root $expand clause (and any pushed $levels
        // budget), never by the object graph — see SerializeBounded's remarks. This is what makes
        // a plain GET over a self-referential/bidirectional model with tracked-entity relationship
        // fixup safe: navigations outside the clause are never handed to System.Text.Json at all,
        // so a cycle among them is structurally unreachable.
        var serializerOptions = jsonOptions ?? _pascalCaseSerializerOptions;
        SelectExpandClause? rootClauseForSerialize = options.SelectExpand?.SelectExpandClause;

        // #466: the RAW substrate's own $levels budget, unioned onto the PUSHED one. BuildExpandLookup
        // seeds a budget only for a name in this set, and the pushed set is null on GetAll, GetById,
        // Priority-1 and non-EF GetQueryable -- so `$levels=N` served ONE level there while the
        // explicit nested spelling served all N. Nothing needs loading: the rows are already in the
        // graph the handler returned.
        //
        // Membership is `ServeRaw AND some candidate has an opinion`, per level, through the same
        // ResolveNavTreatment every other site uses. RunDelegate/Blank are excluded because the raw
        // graph is not their answer; a no-opinion navigation because nothing loaded it.
        //
        // THE UNION FEEDS THE TWO SERIALIZATION STAGES ONLY, NEVER ExpandLevelAsync, and that is
        // load-bearing. Membership is decided at the level a name was found, while #440's omission arm
        // tests the same FLAT set against that name at a DIFFERENT level -- so a `Children` that is
        // ServeRaw-with-opinion at depth 2 and UNDECLARED at the root bypassed the omission arm and
        // emitted `"Children": []` on the root entity, under a 200, on a default configuration.
        // Measured. Issue466NavOmissionRegressionTests is the tripwire.
        HashSet<string>? levelsNavNames = pushedLevelsNavNames;
        if (rootClauseForSerialize is not null && ClauseHasLevels((SelectExpandClause)rootClauseForSerialize) &&
            CollectRawServedLevelsNavNames(
                (SelectExpandClause)rootClauseForSerialize, new[] { requestSource }, registration, requestServices,
                null, depth: 1) is { } rawLevelsNavNames)
        {
            if (levelsNavNames is null)
            {
                levelsNavNames = rawLevelsNavNames;
            }
            else
            {
                // Copy rather than mutate: the pushed set belongs to the caller.
                levelsNavNames = new HashSet<string>(levelsNavNames, StringComparer.OrdinalIgnoreCase);
                levelsNavNames.UnionWith(rawLevelsNavNames);
            }
        }

        // Perf fix (measured regression vs. develop, see SerializeBoundedCollection's remarks):
        // ONE batched call for the whole page instead of one SerializeBounded call per entity.
        // Fold-in #5 (#325/#326 review — maxLevels asymmetry) still applies: pass the SAME
        // source.MaxExpansionDepth ceiling Stage 3.5's OmitUnexpandedNavigations call below already
        // uses, instead of silently defaulting to the file-wide MaxNestedExpandDepth (12).
        JsonArray json = SerializeBoundedCollection(originalItems, rootEdmType, registration.EdmModel,
            rootClauseForSerialize,
            serializerOptions, maxLevels: source.MaxExpansionDepth, levelsNavNames: levelsNavNames);

        // Stage 2: Inject @odata.etag using the original (pre-expand) items for ETag computation.
        // #496 finding 4: the whole stage is marked as user code -- the profile's ETag selector runs
        // once per row inside it, and this pipeline is called from inside every read route's narrow
        // `catch (ODataException)`. One try per page, not one closure per row.
        if (source.HasETag)
        {
            try
            {
                InjectETagsIntoJsonArray(json, originalItems, requestSource);
            }
            catch (Exception ex) when (HandlerFaultException.IsMisclassifiable(ex))
            {
                throw new HandlerFaultException(ex);
            }
        }

        // Stage 3: Inject expanded nav properties (if $expand requested), including NESTED
        // $expand/$select clauses (issue #183, OData §11.2.4.2). Delegated to the recursive
        // ExpandLevelAsync so a single, uniform routine handles the root level and every deeper
        // level: $expand=Studio($expand=Movies) loads Movies on each expanded Studio, and nested
        // $select inside an $expand projects the related entities. Root-level $select is still
        // applied by Stage 4 below (it also needs to return the selected-property list for the
        // projected context URL); ExpandLevelAsync applies each deeper level's own $select.
        if (options.SelectExpand?.SelectExpandClause is { } rootClause &&
            rootClause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any())
        {
            // Pair each root CLR entity with its serialised JsonObject (same index/order). The
            // ETag reorder above replaces json[i] in place, so the parallelism still holds.
            var rootItems = new List<object>(originalItems.Length);
            var rootObjects = new List<JsonObject>(originalItems.Length);
            for (int i = 0; i < originalItems.Length; i++)
            {
                if (json[i] is JsonObject o)
                {
                    rootItems.Add(originalItems[i]);
                    rootObjects.Add(o);
                }
            }

            // #440: pushedLevelsNavNames is threaded in so the ServeRaw branch can tell a navigation
            // that was pushed through BuildLevelsNavBinding (loaded, keep it) from one nothing ever
            // loaded (omit it). The candidate set here is the SINGLE requesting profile, so
            // AnyCandidateHasOpinion at this level is exactly "this profile declares or routes the
            // navigation" — the same per-profile question WarnUndeclaredConventionNavigations asks,
            // and deliberately not a sibling-union question (a sibling entity set declaring the same
            // CLR member does not make THIS entity set able to serve it).
            //
            // #466: `pushedLevelsNavNames`, NOT the `levelsNavNames` union built above. The union is
            // flat and its membership is decided per level, so feeding it to a per-level omission
            // test lets a deep name suppress the omission of a same-named root navigation. See the
            // union site for the measurement.
            await ExpandLevelAsync(
                rootItems, rootObjects, rootClause, new[] { requestSource }, rootEdmType,
                registration, requestServices, serializerOptions, depth: 1, ct,
                source.MaxExpansionDepth, pushedLevelsNavNames);
        }

        // Stage 3.5: Omit navigation properties that were not $expand'd (issue #176).
        // System.Text.Json serialises the entire CLR graph, so every declared navigation
        // leaks into the payload — as [] (collection) or null (single) when unloaded, or with
        // data when a sibling $expand pulled it in. OData JSON Format v4.01 §4.5.1 / §11.2.4.2
        // require a non-expanded navigation to be OMITTED entirely, never emitted inline. This
        // pass removes each un-expanded navigation and recurses into the expanded ones so their
        // own un-expanded navigations are stripped too (face 3). Runs after Stage 3 so freshly
        // injected expansions are present, and before Stage 4 so $select still has final say.
        OmitUnexpandedNavigations(json, rootEdmType, options.SelectExpand?.SelectExpandClause, source.ModelType, serializerOptions,
            activeLevels: null, maxLevels: source.MaxExpansionDepth, levelsNavNames: levelsNavNames);

        // Stage 3.6 (#418/#463/#464): hold every RAW-SERVED collection expansion in this response to
        // MaxExpandTop, at every level. See EnforceRawExpandCeiling for the whole argument — what
        // counts as raw-served, why it is a 400 rather than a trim-and-link, and why an engaged
        // (pushed) navigation is skipped here and bounded by ShapePushedExpandsInJson instead.
        //
        // Sited HERE, in the shared pipeline, rather than at a route: all five read routes converge
        // on this method, and #418's original per-route siting on GetById alone is precisely how #464
        // (three unbounded collection paths) went unnoticed. Runs after Stage 3.5 so #440's omissions
        // and the un-expanded strip have already happened — there is no point measuring a collection
        // that is about to be removed — and before Stage 4 so the root $select cannot hide a breach.
        //
        // Inert, and byte-identically so, on the shipping default: MaxExpandTop is null.
        if (source.MaxExpandTop is int expandCeiling &&
            options.SelectExpand?.SelectExpandClause is { } ceilingClause)
        {
            EnforceRawExpandCeiling(
                json.OfType<JsonObject>().ToList(), ceilingClause, new[] { requestSource },
                source.ModelType, engagedExpandNavs, serializerOptions,
                expandCeiling, source.MaxExpansionDepth, source.EntitySetName, singleEntityRead,
                pathPrefix: string.Empty, pathSuffix: string.Empty, depth: 1);
        }

        // Stage 4: Strip unselected properties at the ROOT level (if $select requested). Deeper
        // levels have already had their own $select applied by ExpandLevelAsync in Stage 3.
        List<string>? selectedProps = null;
        if (options.SelectExpand?.SelectExpandClause is not null)
        {
            selectedProps = ExtractSelectedProperties(options.SelectExpand.SelectExpandClause);
            if (selectedProps is not null)
            {
                StripToSelectedProperties(json.OfType<JsonObject>(), selectedProps);
            }
        }

        return (json, selectedProps);
    }

    // Removes every property not in <paramref name="selectedProps"/> from each object, leaving
    // OData annotations (keys starting with '@', e.g. @odata.etag) untouched — they are metadata
    // and must survive $select. Shared by the root-level Stage-4 strip and the per-level nested
    // $select strip in ExpandLevelAsync so casing and annotation handling stay identical.
    //
    // OData §11.2.4.2: an INLINE control-information key is "name@odata.xxx" (e.g. a nested
    // expand's "Chapters@odata.count"). Its base name (the substring before '@') names the
    // property the annotation belongs to, so it must survive $select exactly when that property
    // does — otherwise stripping the enclosing level's non-selected keys would delete a nested
    // expand's count/annotations. We therefore keep a key when: '@' is at index 0 (top-level
    // annotation such as @odata.etag / @odata.id — existing behavior); or '@' appears later and
    // the base name is a selected/expanded property; or the whole key is itself selected.
    private static void StripToSelectedProperties(IEnumerable<JsonObject> objects, List<string> selectedProps)
    {
        foreach (JsonObject obj in objects)
        {
            var toRemove = obj.Select(p => p.Key)
                             .Where(k => !KeepUnderSelect(k, selectedProps))
                             .ToList();
            foreach (string? key in toRemove) obj.Remove(key);
        }
    }

    // Decides whether a single JSON key survives a $select strip. See StripToSelectedProperties
    // for the §11.2.4.2 rationale behind the inline-control-information (name@odata.xxx) case.
    private static bool KeepUnderSelect(string key, List<string> selectedProps)
    {
        int at = key.IndexOf('@');
        if (at == 0) return true;                       // top-level annotation (@odata.etag, @odata.id, ...)
        if (at > 0 &&                                    // inline control info: keep iff its property is selected
            selectedProps.Contains(key.Substring(0, at), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return selectedProps.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    // Deepest nesting level ExpandLevelAsync will follow, AND the model-bound `entityType.Expand(N)`
    // cap written into the EDM at startup (EntitySetProfile.VisitModelBuilder) — the cap Microsoft's
    // SelectExpandQueryValidator validates a NUMERIC $levels=N against. The clause tree the OData
    // parser builds is already finite (bounded by the depth the client actually wrote in $expand),
    // so this is not needed for correctness on well-formed requests — it is a guard against a
    // pathological / adversarial request that nests $expand extremely deep (§11.2.4.2 places no hard
    // cap). Beyond this depth the deeper related entities are simply not loaded.
    //
    // #328/#428: TIED to the MaxExpansionDepth ceiling, deliberately, and it must stay tied.
    // MaxExpansionDepth is what $levels=max resolves to; MaxNestedExpandDepth is what a numeric
    // $levels=N is validated against. While they could diverge (this was 12 while MaxExpansionDepth
    // was unbounded above), a profile at MaxExpansionDepth = 15 rejected $levels=13/14/15 with 400
    // and served $levels=max at depth 15 — the more expensive spelling was the one that got through
    // (#428). Deriving one from the other makes that divergence unrepresentable.
    // ExpandDepthCeilingTieTests is the tripwire.
    internal const int MaxNestedExpandDepth = EntitySetDefaults.MaxExpansionDepthCeiling;

    // #428: the ONE place $levels becomes a number of levels to load. Both substrates call it -- the
    // pushdown projection builder and the JSON keep/strip pass -- because they were independent
    // transcriptions of the same rule and disagreed about `$levels=max`.
    //
    // `max` was resolved against remainingDepth ALONE, while a NUMERIC N is validated against
    // min(MaxExpansionDepth, modelBoundCap) and the IsMaxLevel case only requires that minimum to be
    // non-zero. So a profile at depth 15 with a cap of 12 rejected $levels=13..15 and served `max` at
    // 15 -- the more expensive spelling was the one that got through, at ~3x per level (#328).
    //
    // Since #328 the cap is DERIVED from the MaxExpansionDepth ceiling, so on a shipped build it
    // cannot be lower and this clamp cannot fire -- the divergence is unrepresentable rather than
    // merely fixed. The parameter stays explicit so the rule is a testable function of its inputs and
    // so re-widening the ceiling cannot silently reopen #428.
    internal static int ResolveLevelsBudget(bool isMaxLevel, long requestedLevel, int remainingDepth, int modelBoundCap)
    {
        int cap = Math.Min(remainingDepth, modelBoundCap);
        long levels = isMaxLevel ? cap : requestedLevel;
        return (int)Math.Min(levels, (long)cap);
    }

    // Issue #183 / OData §11.2.4.2: recursively inject $expand'd navigation properties for one
    // level of a page of entities, then descend into each expanded navigation's own nested
    // $expand/$select clause. <paramref name="items"/> are the CLR entities at this level and
    // <paramref name="jsonItems"/> their already-serialised JsonObjects (parallel, same order);
    // mutations to jsonItems are what end up in the response. <paramref name="levelSource"/> is the
    // request-scoped endpoint source whose NavigationRoutes cover this level's entity type, and
    // <paramref name="levelEdmType"/> is that type in the EDM (used to resolve nested targets).
    //
    // Batching mirrors the top-level strategy per level: when a navigation exposes a BatchHandler
    // it is invoked once for the whole flattened set of entities at this level; otherwise the
    // per-entity Handler is called once per entity (N+1 within that one property). Nested levels
    // flatten every related entity across the page into a single set before recursing, so a
    // batch-capable navigation is still batched once per level rather than once per parent.
    // #294: a nested $top/$skip against a delegate-backed navigation anywhere in the expand tree
    // (see the RunDelegate branch below) throws Microsoft.OData.ODataException rather than
    // returning/threading an IResult — every caller of ApplyCollectionPipelineAsync already catches
    // that exception and converts it to 400 InvalidQueryOption.
    internal static async Task ExpandLevelAsync(
        IReadOnlyList<object> items,
        IReadOnlyList<JsonObject> jsonItems,
        SelectExpandClause clause,
        IReadOnlyList<IEntitySetEndpointSource> levelSources,
        IEdmEntityType? levelEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        JsonSerializerOptions serializerOptions,
        int depth,
        CancellationToken ct,
        int maxExpansionDepth,
        HashSet<string>? pushedLevelsNavNames = null)
    {
        if (items.Count == 0 || depth > MaxNestedExpandDepth || levelSources.Count == 0) return;

        // Cache the key PropertyInfo once per level (M-3 perf parity with the old inline loop).
        // #292: levelSources is a union of 2+ profiles only when the EDM couldn't disambiguate
        // which entity set a navigation targets (see ResolveRequestSourcesForEdmType); those
        // profiles all share the same CLR model type by construction, so the key property — a
        // structural convention on that type, not part of any delegate/authorization boundary —
        // is read off the first candidate.
        PropertyInfo? keyProp = items[0].GetType()
            .GetProperty(levelSources[0].KeyPropertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        foreach (ExpandedNavigationSelectItem expandItem in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string propName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;

            // Derive the expand key the way the serializer named the parent's property: honor a
            // per-property [JsonPropertyName] rename first (#184), then fall back to the naming
            // policy ("children" for camelCase, "Children" for PascalCase). Resolved off the actual
            // runtime entity type at this level. Must agree with OmitUnexpandedNavigations' key so
            // Stage 3.5 keeps (not strips) the expansion this injects.
            // #253 completion: propName is the EDM (JSON) nav name, so map JSON→CLR (a plain
            // GetProperty(jsonName) would miss the renamed CLR member and mis-derive the key under a
            // non-camelCase policy) before resolving the payload key off the CLR property.
            PropertyInfo? expandClrProp = ODataPropertyNaming.FindClrPropertyByEdmName(items[0].GetType(), propName);
            string expandKey = ResolveNavigationJsonKey(expandClrProp?.Name ?? propName, expandClrProp, serializerOptions);

            // Model B — declaring-set authority (OWNER DECISION 2026-07-26, FROZEN spec on issue
            // #293): resolve this navigation's treatment from levelSources, the exact-EDM-type
            // candidate set for this level (see ResolveRequestSourcesForEdmType). ResolveNavTreatment
            // is the SAME dispatch the pushdown gate uses (TryBuildEngagedExpand, over the equivalent
            // startup-profile candidate set for the child element type) — reusing it here means the
            // gate and this delegate path can never disagree: the gate only ever pushes down a
            // ServeRaw navigation (so RunDelegate/Blank always arrive here needing action), and this
            // path only ever runs the sole RunDelegate route or blanks a Blank one — plus, since
            // #440, omits a ServeRaw navigation that NO candidate at this level declares or routes
            // (see the ServeRaw branch: that sub-case is not a delegate decision at all, it is
            // "nothing ever loaded this, so there is no raw value to serve").
            NavTreatmentResult treatment = ResolveNavTreatment(propName, levelSources);
            bool isCollectionNav = (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                .NavigationProperty?.Type.IsCollection() ?? false;

            if (treatment.Treatment == NavTreatment.ServeRaw)
            {
                // Model B declaring-set authority (owner decision, FROZEN on #293): resolve this
                // navigation's treatment from levelSources, the exact-EDM-type candidate set for this
                // level. ResolveNavTreatment is the SAME dispatch the pushdown gate uses, so the gate and
                // this delegate path can never disagree -- the gate only pushes ServeRaw, so
                // RunDelegate/Blank always arrive here needing action, and this path runs the sole route or
                // blanks. Since #440 it also OMITS a ServeRaw navigation no candidate declares or routes:
                // that sub-case is not a delegate decision, it is "nothing ever loaded this".
                if (!treatment.AnyCandidateHasOpinion &&
                    !(pushedLevelsNavNames?.Contains(propName) ?? false))
                {
                    for (int i = 0; i < items.Count; i++) jsonItems[i].Remove(expandKey);
                    continue;
                }

                // DB(X) = ∅ over every candidate at this level and at least one DECLARED it: nobody
                // delegates this navigation, so whatever is already sitting at
                // jsonItems[i][expandKey] — an EF Include pushed down by the query, or the plain
                // serialized CLR graph — IS the raw, authoritative answer. Nothing to inject or
                // blank; leave it exactly as serialized.
                //
                // #320: but this branch does NOT recurse, so a DEEPER navigation reached only through
                // this ServeRaw parent's already-materialized graph never reaches the nested-$top/$skip
                // rejection below — its option was accepted, never applied, and answered 200. Scan the
                // subtree for that case before leaving. The scan is skipped entirely (no candidate
                // resolution, no profile instantiation) unless the subtree actually carries a nested
                // $top/$skip, so the common ServeRaw expand stays a bare `continue`.
                if (expandItem.SelectAndExpand is { } rawNested && ClauseHasNestedTopOrSkip(rawNested))
                {
                    IEdmEntityType? rawTargetEdmType =
                        (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                        .NavigationProperty?.ToEntityType();
                    EnsureNestedWindowIsApplicable(
                        rawNested, rawTargetEdmType, registration, requestServices, depth + 1);
                }
                continue;
            }

            // #294 + #320 (uniform rule): a nested $top/$skip inside $expand cannot be applied to a
            // navigation whose treatment is not ServeRaw — a RunDelegate nav's Handler/BatchHandler
            // returns the delegate's FULL answer for the given parent key(s) and nothing downstream
            // windows it, and a Blank nav is emptied outright. Silently ignoring the option returned
            // every related row (or none) under an unsuspicious 200 — the #294 bug. Reject instead of
            // guessing, consistent with the framework's "parse the option or reject it" contract, and
            // mirroring ValidateNestedTopCeiling's over-ceiling 400 above it in the request pipeline.
            //
            // Checked BEFORE the Blank branch and before either delegate branch below, so no handler
            // runs for a rejected request and the answer does not depend on which non-ServeRaw
            // treatment the navigation resolved to. Does not apply to ServeRaw: EF pushdown honors and
            // windows a nested $top there (and where it does NOT — a ServeRaw nav whose branch was
            // never pushed down at all — the option is still silently ignored; see the class note on
            // ClauseHasNestedTopOrSkip).
            // #650 widens this from $top/$skip to $filter and $orderby, which were SILENTLY
            // DROPPED here — `$expand=Lines($filter=Sku eq 'S1')` answered 200 with every row, which
            // is indistinguishable from a filter that matched everything. Same substrate, same
            // reason: the delegate's answer is not an IQueryable and nothing downstream re-shapes
            // it. $select is NOT in this set — it is applied below, to the materialized children.
            // #650: a nested $top/$skip is APPLIED on the RunDelegate path (below, after the count) and
            // still refused everywhere else. #294 refused it universally, reasoning that the delegate
            // "returns its FULL answer and nothing downstream windows it" — true then, and this change
            // is what makes it false: the framework now windows the materialized children itself.
            //
            // Blank keeps the refusal, and that is not an oversight. Under Blank the candidate sets
            // disagree, so the navigation is deliberately served EMPTY — the framework does not have
            // the real collection. Answering `$skip=5` from an empty array returns empty, which the
            // client cannot distinguish from "there were fewer than five", i.e. exactly the silent
            // wrong answer this whole issue is about. Same reason $filter/$orderby refuse there.
            if (treatment.Treatment != NavTreatment.RunDelegate
                && (expandItem.TopOption is not null || expandItem.SkipOption is not null))
            {
                // Thrown (not returned) for the same reason EnsureWithinExpandCeiling throws below:
                // it avoids IResult threading through this void recursive walk. All 5 collection-GET
                // call sites of ApplyCollectionPipelineAsync already catch Microsoft.OData.ODataException
                // and surface it as 400 InvalidQueryOption.
                throw NestedWindowRejection(propName, treatment.Treatment);
            }

            // #650: a nested $filter/$orderby is APPLIED here, in memory, against the children the
            // delegate returns — not refused. It used to be silently dropped, which is the defect;
            // refusing it would have been honest but strictly worse for the client when the option is
            // answerable, and it is: the delegate hands back the full related collection, so filtering
            // and ordering it is exactly what the client asked for.
            //
            // Falls back to a 400 only when the clause cannot be BOUND (a construct Microsoft's
            // binders reject for this element type). Loud either way; never dropped.
            Func<object?, object?>? navShaper = null;
            if (expandItem.FilterOption is not null || expandItem.OrderByOption is not null)
            {
                // treatment.Route is null under Blank (the candidate sets disagree, so the nav is
                // served empty); there is nothing to shape, and an option we cannot honour is still
                // refused rather than dropped — NestedClauseRejection's non-RunDelegate arm says so.
                Type? shapeElem = isCollectionNav ? treatment.Route?.NavItemType : null;
                if (shapeElem is null)
                {
                    // A single-valued navigation has nothing to filter or order.
                    throw NestedClauseRejection(
                        propName,
                        treatment.Treatment,
                        expandItem.FilterOption is not null ? "$filter" : "$orderby",
                        expandItem.FilterOption is not null ? "server-side filtering" : "server-side ordering");
                }

                try
                {
                    navShaper = TryBuildDelegateNavShaper(
                        expandItem.FilterOption, expandItem.OrderByOption, shapeElem,
                        registration.EdmModel, s_delegateNavBinderSettings);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw NestedClauseRejection(
                        propName,
                        treatment.Treatment,
                        expandItem.FilterOption is not null ? "$filter" : "$orderby",
                        expandItem.FilterOption is not null ? "server-side filtering" : "server-side ordering");
                }
            }

            if (treatment.Treatment == NavTreatment.Blank)
            {
                // DB(X) and DL(X) disagree at this level (some candidate delegates, another declares
                // the nav with no route — or 2+ candidates delegate via distinct routes): the framework
                // cannot tell which authoritative declaration governs, so it fails closed rather than
                // guessing. Overwrite explicitly — this key IS in the $expand clause, so Stage 3.5's
                // OmitUnexpandedNavigations (which only ever REMOVES un-$expand'd keys) would otherwise
                // keep whatever the parent handler/EF fixup happened to leave here.
                for (int i = 0; i < items.Count; i++)
                {
                    jsonItems[i][expandKey] = isCollectionNav ? new JsonArray() : null;
                }
                continue;
            }

            // #466: a MULTI-LEVEL $levels on a delegate-backed navigation is REJECTED, not truncated.
            // The delegate loads ONE level and nothing recurses, so `Nav($levels=3)` answered 200 with
            // one level while `Nav($expand=Nav($expand=Nav))` -- the same request spelled out --
            // answered 200 with three. Silent truncation, which M1 rules out.
            //
            // 400 rather than an implementation because WHICH delegate to run at depth 2 is not
            // settled for this substrate: Model B resolves depth >= 2 from the exact-EDM-type union
            // (Blank for a self-referential nav with a disagreeing sibling), while the PUSHDOWN path's
            // $levels deliberately never re-resolves and stays on the URL-named set (#318,
            // owner-settled). Implementing would mean making that decision here. Follows #294's
            // precedent exactly, a few lines above.
            //
            // $levels=1 is NOT rejected -- it restates a bare $expand, which this path serves. The
            // budget comes from the SAME ResolveLevelsBudget both loaders use.
            if (expandItem.LevelsOption is { } delegateLevels &&
                ResolveLevelsBudget(
                    delegateLevels.IsMaxLevel, delegateLevels.Level, maxExpansionDepth, MaxNestedExpandDepth) > 1)
            {
                throw LevelsOnDelegateRejection(propName);
            }

            // NavTreatment.RunDelegate: exactly one candidate at this level routes this navigation
            // back, and no candidate disagrees (declares it delegate-less) — that route is the sole,
            // unambiguous authority for it. Run it.
            NavigationRouteDefinition navRoute = treatment.Route!;

            // Load the related entity/collection for every entity at this level, keeping the CLR
            // results (relatedByIndex[i]) so deeper levels can read their keys.
            object?[] relatedByIndex = new object?[items.Count];
            if (navRoute.BatchHandler is not null)
            {
                var keys = new List<object>(items.Count);
                object?[] keyByIndex = new object?[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    object? keyVal = keyProp?.GetValue(items[i]);
                    keyByIndex[i] = keyVal;
                    if (keyVal is not null) keys.Add(keyVal);
                }

                IReadOnlyDictionary<object, object?> map = await AsHandlerFault(navRoute.BatchHandler(keys, ct));
                for (int i = 0; i < items.Count; i++)
                {
                    // A missing key means "no children" (collection → []) or "no related entity"
                    // (single → null), matching the per-entity fallback's empty/null defaults.
                    relatedByIndex[i] = keyByIndex[i] is { } k && map.TryGetValue(k, out object? v)
                        ? v
                        : (navRoute.IsCollection ? Array.Empty<object>() : null);
                }
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    relatedByIndex[i] = keyProp?.GetValue(items[i]) is { } keyVal
                        ? await AsHandlerFault(navRoute.Handler(keyVal, ct))
                        : (navRoute.IsCollection ? Array.Empty<object>() : null);
                }
            }

            // Resolve the navigation target's EDM entity type and nested $expand/$select clause up
            // front — needed both by the #325/#326 bounded splice immediately below AND by the
            // hasNestedExpand recursion further down (single computation, reused).
            IEdmNavigationProperty? navProp =
                (expandItem.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?.NavigationProperty;
            IEdmEntityType? targetEdmType = navProp?.ToEntityType();
            SelectExpandClause? nestedClause = expandItem.SelectAndExpand;

            // Inject the serialised related value onto each parent JsonObject.
            // #325/#326 (Option B): the delegate's own returned graph gets the SAME bounded
            // treatment as Stage 1 — a delegate can return tracked/cyclic entities (T30/T31), and
            // this splice is a second, independent serialization event Stage 1's walker never
            // reaches (relatedByIndex[i] is the delegate's freshly returned object, not something
            // read off the root item via reflection). Bounded by nestedClause exactly as Stage 1 is
            // bounded by the root clause: any navigation nestedClause itself keeps gets a
            // reflection-read splice here, and — for one that resolves to RunDelegate/Blank at the
            // NEXT level — the hasNestedExpand recursion below still unconditionally overwrites it
            // (same ordering guarantee as Stage 1: walker first, delegate-safety overwrite after).
            for (int i = 0; i < items.Count; i++)
            {
                // #650: shape BEFORE serializing and before counting. Order matters and follows
                // §11.2.5.5 — the count is of the FILTERED collection — which falls out of doing this
                // first: the array the count is taken from below is already the filtered one.
                if (navShaper is not null && relatedByIndex[i] is not null)
                {
                    relatedByIndex[i] = navShaper(relatedByIndex[i]);
                }

                // Fold-in #2: cardinality comes from the EDM (isCollectionNav, already resolved
                // above), never sniffed from relatedByIndex[i]'s own CLR shape.
                jsonItems[i][expandKey] = SerializeBounded(
                    relatedByIndex[i], targetEdmType, registration.EdmModel, nestedClause, serializerOptions,
                    isCollectionValue: isCollectionNav);

                // #650: a nested $count IS answerable here, unlike $filter/$orderby/$top above, so it
                // is implemented rather than refused — refusing something free would be gratuitous.
                // Honest because the delegate's answer is not windowed: nothing between it and here
                // truncates (the $top/$skip rejection above is what guarantees that), so this array
                // IS the full related collection and its length IS the count. Contrast the pushdown
                // path, where materialization is capped at MaxExpandTop + 1 and WriteNestedCountAndWindow
                // must call EnsureWithinExpandCeiling before it can trust the number.
                if (isCollectionNav && expandItem.CountOption == true
                    && jsonItems[i][expandKey] is JsonArray countArr)
                {
                    jsonItems[i][$"{expandKey}@odata.count"] = countArr.Count;
                }

                // #650: window LAST, and specifically after the count — §11.2.5.5 makes
                // Nav@odata.count the count of the collection after $filter and BEFORE $top/$skip, so
                // counting a windowed array would report the page size as the total. That is #379's
                // defect one level down. The pushdown path sequences these the same way
                // (WriteNestedCountAndWindow counts, then calls ApplyNestedWindow).
                //
                // MaxExpandTop is deliberately NOT imposed here: bounding a delegate's answer behind
                // its back stays out of scope (see the Declared deviations table). This applies only
                // the window the CLIENT asked for.
                if (isCollectionNav
                    && (expandItem.SkipOption is not null || expandItem.TopOption is not null)
                    && jsonItems[i][expandKey] is JsonArray windowArr)
                {
                    ApplyNestedWindow(
                        windowArr,
                        expandItem.SkipOption is long dsk ? (int)Math.Min(dsk, int.MaxValue) : null,
                        expandItem.TopOption is long dtp ? (int)Math.Min(dtp, int.MaxValue) : null);
                }
            }

            if (nestedClause is null) continue;

            bool hasNestedExpand = nestedClause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any();
            bool hasNestedSelect = !nestedClause.AllSelected;
            if (!hasNestedExpand && !hasNestedSelect) continue;

            // Flatten every related entity across the whole page into one (CLR, JsonObject) set so
            // a deeper batch navigation is invoked once per level, and nested $select is applied to
            // all of them in one pass.
            var childItems = new List<object>();
            var childObjects = new List<JsonObject>();
            for (int i = 0; i < items.Count; i++)
            {
                object? related = relatedByIndex[i];
                JsonNode? node = jsonItems[i][expandKey];
                if (navRoute.IsCollection)
                {
                    if (related is System.Collections.IEnumerable seq && node is JsonArray arr)
                    {
                        int j = 0;
                        foreach (object? elem in seq)
                        {
                            if (elem is not null && j < arr.Count && arr[j] is JsonObject childObj)
                            {
                                childItems.Add(elem);
                                childObjects.Add(childObj);
                            }
                            j++;
                        }
                    }
                }
                else if (related is not null && node is JsonObject childObj)
                {
                    childItems.Add(related);
                    childObjects.Add(childObj);
                }
            }

            if (childItems.Count == 0) continue;

            if (hasNestedExpand)
            {
                // The request-scoped source(s) that legitimately serve the NEXT level's own
                // NavigationRoutes (nav handlers may capture scoped dependencies such as a
                // DbContext) — navProp/targetEdmType were already resolved above for the bounded
                // splice. #292: unions every profile exposing the same CLR/EDM type so the per-nav
                // lookup above (routeMatches) can fail closed on conflicts instead of a single
                // arbitrary FirstOrDefault picking whichever profile happens to be first in
                // registration/iteration order.
                IReadOnlyList<IEntitySetEndpointSource> targetSources = ResolveRequestSourcesForEdmType(
                    targetEdmType, registration, requestServices);

                if (targetSources.Count > 0)
                {
                    await ExpandLevelAsync(
                        childItems, childObjects, (SelectExpandClause)nestedClause, targetSources, targetEdmType,
                        registration, requestServices, serializerOptions, depth + 1, ct,
                        maxExpansionDepth);
                }
                // If no candidate set is registered/resolvable at all, the deeper expansion
                // cannot be loaded here; Stage 3.5's OmitUnexpandedNavigations still keeps the
                // (empty) nav per the clause, mirroring the pre-#183 limitation for unregistered
                // navigation targets. This is safe precisely because zero candidates means no
                // profile anywhere exposes the type, so no delegate-safety union applies to it.
            }

            // Apply this navigation's nested $select to the just-injected children (reuses the
            // root-level strip so casing / annotation handling are identical). Runs after the
            // deeper recursion so nested $expand keeps final say over what data is present, and
            // ExtractSelectedProperties preserves expanded nav names so they survive projection.
            if (hasNestedSelect)
            {
                List<string>? nestedSelected = ExtractSelectedProperties((SelectExpandClause)nestedClause);
                if (nestedSelected is not null) StripToSelectedProperties(childObjects, nestedSelected);
            }
        }
    }

    // #294/#320: the single place the nested-$top/$skip rejection message is built, so the two throw
    // sites (the navigation reached directly by ExpandLevelAsync, and one reached only through a
    // ServeRaw parent's materialized graph) can never drift apart. The RunDelegate wording is
    // byte-identical to the message #294 shipped — it is quoted in docs and asserted in tests.

    // #650: binder settings for the in-memory delegate path. HandleNullPropagation is TRUE here and
    // FALSE on the pushdown path (cachedBinderSettings), and the difference is required rather than an
    // oversight: SQL already evaluates `x.X eq 'y'` over a NULL row to "not matched", so the SQL path
    // needs no guards, while LINQ-to-Objects would dereference and throw NullReferenceException on the
    // same data. Microsoft's own default resolves the same way round -- False for IQueryable, True for
    // IEnumerable. Shared as a static: ODataQuerySettings is read-only in every consumer it reaches
    // (see the note above cachedBinderSettings), unlike ODataQueryContext which is strictly per request.
    private static readonly ODataQuerySettings s_delegateNavBinderSettings =
        new() { HandleNullPropagation = HandleNullPropagationOption.True };

    // #650: apply a delegate-backed navigation's nested $filter/$orderby to the children the delegate
    // already returned, in memory. Built once per (navigation, request) and reused for every parent.
    //
    // The BINDING is Microsoft's own FilterBinder/OrderByBinder via the same BindNavShape the pushdown
    // path uses, so a clause means the same thing on both paths; only the EXECUTION differs —
    // LINQ-to-Objects here, SQL there. That divergence is real (string comparison, null ordering and
    // culture follow the CLR rather than the database's collation) and it is the same divergence
    // Microsoft.AspNetCore.OData has when [EnableQuery] runs over an in-memory source, which is why
    // it is documented rather than avoided.
    //
    // Deliberately NOT routed through ApplyNavShape: that composes $skip/$top and the MaxExpandTop
    // count bound as well, all of which are pushdown concerns here ($top/$skip on a delegate-backed
    // nav remain a 400, #294). Composing only what this path implements keeps the two honest.
    //
    // Returns null when nothing is requested. THROWS when a clause cannot be bound, which the caller
    // turns into the same 400 the option used to get unconditionally — a clause we cannot honour is
    // still refused loudly, never dropped.
    private static Func<object?, object?>? TryBuildDelegateNavShaper(
        FilterClause? filter, OrderByClause? orderBy, Type elem, IEdmModel model,
        ODataQuerySettings binderSettings)
    {
        if (filter is null && orderBy is null) return null;

        NavShapeBindings bound = BindNavShape(filter, orderBy, elem, model, binderSettings);

        ParameterExpression src = Expression.Parameter(typeof(object), "src");

        // #664: Cast, never a Convert to IEnumerable<elem>. The value handed in is not always the
        // delegate's own typed collection -- ExpandLevelAsync substitutes Array.Empty<object>() for a
        // parent with no related rows, on both the batch and the per-entity branch -- and a hard
        // convert threw InvalidCastException out of the compiled shaper, so ONE childless parent
        // anywhere in the page 500'd the whole request. Cast is right for both shapes and for any
        // third the substitution grows later.
        Expression seq = Expression.Call(
            _enumerableCast.MakeGenericMethod(elem),
            Expression.Convert(src, typeof(System.Collections.IEnumerable)));

        if (bound.Predicate is not null)
            seq = Expression.Call(_enumerableWhere.MakeGenericMethod(elem), seq, bound.Predicate);

        if (bound.OrderBy is { Count: > 0 })
        {
            bool first = true;
            foreach ((LambdaExpression keySelector, bool descending) in bound.OrderBy)
            {
                MethodInfo op = (first, descending) switch
                {
                    (true, false) => _enumerableOrderBy,
                    (true, true) => _enumerableOrderByDescending,
                    (false, false) => _enumerableThenBy,
                    (false, true) => _enumerableThenByDescending,
                };
                seq = Expression.Call(
                    op.MakeGenericMethod(elem, keySelector.ReturnType), seq, keySelector);
                first = false;
            }
        }

        // Materialise: the caller serialises the result and may count it, and a lazy sequence would
        // re-run the predicate on every enumeration.
        seq = Expression.Call(_enumerableToList.MakeGenericMethod(elem), seq);

        return Expression.Lambda<Func<object?, object?>>(
            Expression.Convert(seq, typeof(object)), src).Compile();
    }

    // #650: the $filter/$orderby twin. Deliberately a SEPARATE method rather than a parameter on
    // NestedWindowRejection, whose RunDelegate wording is byte-identical to what #294 shipped and is
    // quoted in docs and asserted in tests — folding a format argument into it would put those bytes
    // one edit away from moving. Same shape, same two remedies, so the two read as one rule.
    private static Microsoft.OData.ODataException NestedClauseRejection(
        string navName, NavTreatment treatment, string option, string capability) =>
        treatment == NavTreatment.RunDelegate
            ? new Microsoft.OData.ODataException(
                $"A nested {option} is not supported on the delegate-backed navigation '{navName}'; " +
                $"declare it delegate-less (no Handler/BatchHandler) to enable {capability}, " +
                "or remove the option.")
            : new Microsoft.OData.ODataException(
                $"A nested {option} is not supported on the navigation '{navName}': the entity sets " +
                "exposing this type disagree about whether it is delegate-backed, so it is served " +
                $"empty and no {option} can be applied. Remove the option.");

    private static Microsoft.OData.ODataException NestedWindowRejection(string navName, NavTreatment treatment) =>
        treatment == NavTreatment.RunDelegate
            ? new Microsoft.OData.ODataException(
                $"A nested $top/$skip is not supported on the delegate-backed navigation '{navName}'; " +
                "declare it delegate-less (no Handler/BatchHandler) to enable server-side windowing, " +
                "or remove the option.")
            : new Microsoft.OData.ODataException(
                $"A nested $top/$skip is not supported on the navigation '{navName}': the entity sets " +
                "exposing this type disagree about whether it is delegate-backed, so it is served " +
                "empty and no window can be applied. Remove the option.");

    // #650: the #320 position-specific twin of NestedWindowRejection. See its throw site for why the
    // condition is about WHERE the navigation was reached rather than how it is declared.
    private static Microsoft.OData.ODataException NestedWindowUnderRawParentRejection(string navName) =>
        new(
            $"A nested $top/$skip is not supported on '{navName}' here: it is expanded beneath a " +
            "parent served from its own materialized graph, so its handler never runs and there is " +
            "nothing to window. Expand it directly, or remove the option.");

    // #466: the message for a multi-level $levels on a delegate-backed navigation. Deliberately
    // shaped like NestedWindowRejection's RunDelegate arm — same substrate, same reason (the option
    // cannot be applied to a delegate's answer), same two remedies — and it names the spelling that
    // DOES have an answer, because that is the whole point of the issue: the explicit nested chain
    // recurses through this method, so every deeper level is RESOLVED through Model B — run through
    // the level's own delegate where the candidate set agrees, Blanked where it does not — instead of
    // being dropped without a word.
    private static Microsoft.OData.ODataException LevelsOnDelegateRejection(string navName) =>
        new(
            $"A '$levels' expansion of more than one level is not supported on the delegate-backed " +
            $"navigation '{navName}'; the delegate loads a single level, so the deeper levels would " +
            "be silently dropped. Spell the depth out with nested $expand, or declare the navigation " +
            "delegate-less (no Handler/BatchHandler) to enable the server-side $levels recursion.");

    // #320: true when the clause carries a $top or $skip on ANY navigation at any depth below it. A
    // pure clause walk -- no EDM lookup, no candidate resolution -- so the common expand with no
    // nested window pays nothing. Unbounded recursion like its siblings; the depth and breadth
    // ceilings have already rejected an oversized tree.
    //
    // SCOPE, deliberate and not fixed here: "not applicable" is resolved from the Model B treatment,
    // not from whether the option was in fact applied. A ServeRaw navigation whose branch was never
    // pushdown-windowed still ignores its nested $top/$skip silently. Rejecting that would make the
    // answer depend on whether pushdown happened to engage -- an internal decision invisible to the
    // client -- and would turn requests honoured today into 400s. #352 owns it.
    //
    // #464 narrowed that note: the MaxExpandTop CEILING no longer has the same reach hole, since
    // EnforceRawExpandCeiling now bounds every raw collection expansion. What remains true is only
    // that the nested WINDOW is still silently ignored on those paths.
    private static bool ClauseHasNestedTopOrSkip(SelectExpandClause clause)
    {
        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            if (item.TopOption is not null || item.SkipOption is not null) return true;
            if (item.SelectAndExpand is { } deeper && ClauseHasNestedTopOrSkip(deeper)) return true;
        }
        return false;
    }

    // #466: does this $expand tree carry a $levels anywhere? A pure clause walk, and the gate that
    // keeps CollectRawServedLevelsNavNames (which DOES resolve candidate sets per level) off the
    // overwhelmingly common expand that carries no $levels at all. Same shape and same bounding
    // argument as ClauseHasNestedTopOrSkip above.
    private static bool ClauseHasLevels(SelectExpandClause clause)
    {
        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            if (item.LevelsOption is not null) return true;
            if (item.SelectAndExpand is { } deeper && ClauseHasLevels(deeper)) return true;
        }
        return false;
    }

    // #466: the navigations carrying $levels whose recursion the RAW substrate serves — i.e. the ones
    // BuildExpandLookup must seed a levels budget for so SerializeBounded/OmitUnexpandedNavigations
    // keep the self-reference down to the depth requested, exactly as they already do for a PUSHED
    // $levels (CollectPushedLevelsNavNames) and for the explicit nested spelling.
    //
    // Candidates are resolved through the SAME ResolveRequestSourcesForEdmType and the treatment
    // through the SAME ResolveNavTreatment the real descent uses, so this cannot disagree with it.
    // The walk descends only through a ServeRaw navigation, because that is exactly the boundary of
    // the raw substrate: ExpandLevelAsync's ServeRaw branch leaves the materialized graph in place
    // and does not recurse, so everything below it is read straight off that graph, while a
    // RunDelegate/Blank navigation replaces the value and owns its own subtree.
    //
    // AnyCandidateHasOpinion is required — see the union at the call site for why (it is what keeps
    // #440's omission arm untouched).
    private static HashSet<string>? CollectRawServedLevelsNavNames(
        SelectExpandClause clause,
        IReadOnlyList<IEntitySetEndpointSource> levelSources,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        HashSet<string>? names,
        int depth)
    {
        if (depth > MaxNestedExpandDepth) return names;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string navName = item.PathToNavigationProperty.FirstSegment.Identifier;
            NavTreatmentResult treatment = ResolveNavTreatment(navName, levelSources);
            if (treatment.Treatment != NavTreatment.ServeRaw || !treatment.AnyCandidateHasOpinion) continue;

            if (item.LevelsOption is not null)
            {
                (names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(navName);
            }

            if (item.SelectAndExpand is { } nested &&
                nested.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any())
            {
                IEdmEntityType? childEdmType =
                    (item.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                    .NavigationProperty?.ToEntityType();
                names = CollectRawServedLevelsNavNames(
                    nested,
                    ResolveRequestSourcesForEdmType(childEdmType, registration, requestServices),
                    registration, requestServices, names, depth + 1);
            }
        }

        return names;
    }

    // #320: walks the $expand subtree under a ServeRaw navigation and throws the same 400 the direct
    // path throws for a nested $top/$skip on a non-ServeRaw navigation.
    //
    // Needed because ExpandLevelAsync's ServeRaw branch `continue`s without recursing -- correctly,
    // the raw value IS the answer -- so a delegate-backed grandchild reached only through a
    // delegate-less parent's graph was never resolved, and its nested $top/$skip was accepted, never
    // applied, and answered 200 with every related row.
    //
    // It cannot turn a honoured request into a 400: a nested window is honoured only when the whole
    // branch was pushed, and a branch is pushed only when EVERY level is ServeRaw -- so whenever this
    // scan finds a non-ServeRaw navigation, the option was certainly not applied.
    //
    // Resolved through the SAME helpers the real descent uses, so the scan cannot disagree with what
    // it stands in for.
    //
    // #440 pairing: an undeclared navigation has no opinion, so ResolveNavTreatment reports it
    // ServeRaw and this scan does not reject it. That is intended -- #440 REMOVES it from the payload,
    // so there is nothing for a window to apply to, and rejecting would charge the client a 400 for
    // the developer's missing declaration.
    private static void EnsureNestedWindowIsApplicable(
        SelectExpandClause clause,
        IEdmEntityType? levelEdmType,
        OhDataRegistration registration,
        IServiceProvider requestServices,
        int depth)
    {
        if (levelEdmType is null || depth > MaxNestedExpandDepth) return;

        IReadOnlyList<IEntitySetEndpointSource> candidates =
            ResolveRequestSourcesForEdmType(levelEdmType, registration, requestServices);
        if (candidates.Count == 0) return;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string navName = item.PathToNavigationProperty.FirstSegment.Identifier;
            NavTreatment navTreatment = ResolveNavTreatment(navName, candidates).Treatment;

            if (navTreatment != NavTreatment.ServeRaw &&
                (item.TopOption is not null || item.SkipOption is not null))
            {
                // #650: still refused HERE, and the reason is now specific to this position rather
                // than to the navigation. Reached directly, a delegate-backed navigation's nested
                // $top/$skip is applied (ExpandLevelAsync windows the materialized children).
                // Reached BENEATH a raw-served parent it is not: the rows come out of the parent's
                // own materialized graph and this navigation's delegate never runs, so there is
                // nothing for the window to be applied to. Refusing keeps #320's guarantee — the
                // option is never dropped without a trace — and the message no longer offers
                // "declare it delegate-less", which is no longer the remedy for this shape.
                // Blank keeps its own message: the sets disagree about this navigation, which refuses
                // it wherever it is reached, so naming the POSITION would name the lesser reason.
                throw navTreatment == NavTreatment.RunDelegate
                    ? NestedWindowUnderRawParentRejection(navName)
                    : NestedWindowRejection(navName, navTreatment);
            }

            if (item.SelectAndExpand is { } deeper)
            {
                IEdmEntityType? deeperEdmType =
                    (item.PathToNavigationProperty.FirstSegment as NavigationPropertySegment)?
                    .NavigationProperty?.ToEntityType();
                EnsureNestedWindowIsApplicable(
                    deeper, deeperEdmType, registration, requestServices, depth + 1);
            }
        }
    }

    // #292: the request-scoped sources that legitimately serve a navigation's target EDM type,
    // replacing a FirstOrDefault-by-CLR-type that was registration-order dependent.
    //
    // Always the UNION, and it can legitimately return 2+ -- the caller resolves per-navigation
    // ambiguity from the full list, so a genuine conflict fails closed instead of being decided here.
    //
    // Deliberately does not prefer the EDM's navigation-source binding. Measured, the three cases
    // are: exactly one exposing set gives a REAL binding (and a one-element union, so they agree);
    // two or more gives an EdmUnknownEntitySet placeholder and an EMPTY binding list; none gives the
    // same placeholder. So the binding is redundant where it exists and absent where it would matter.
    //
    // DO NOT restore it as load-bearing: registering an unrelated second set over the child type
    // moves the model from one case to the next and DELETES the binding, silently. Anything built on
    // it must test `is IEdmEntitySet and not IEdmUnknownEntitySet` and treat absence as a real
    // outcome.
    //
    // Empty list when no profile exposes the type at all.
    private static IReadOnlyList<IEntitySetEndpointSource> ResolveRequestSourcesForEdmType(
        IEdmEntityType? targetEdmType, OhDataRegistration registration, IServiceProvider requestServices)
    {
        List<IEntitySetEndpointSource> candidates = new();
        foreach (IEntitySetEndpointSource profile in ResolveProfilesForEdmType(targetEdmType, registration))
        {
            if (requestServices.GetService(profile.GetType()) is IEntitySetEndpointSource instance)
                candidates.Add(instance);
        }
        return candidates;
    }

    // Model B candidate resolution (FROZEN spec, issue #293): the startup profiles whose entity
    // set's EDM entity type is EXACTLY <paramref name="targetEdmType"/> (matched by
    // <see cref="IEdmEntityType.FullTypeName"/> — never CLR-type assignability, and never registration
    // order). This is "the candidate set S" the decision table in ResolveNavTreatment partitions into
    // DB/DL. Shared by both call sites that need a level's candidate set:
    //   - the pushdown gate (TryBuildEngagedExpand → ResolveProfilesForClrType below), which only has
    //     structural facts available at query-plan time, so the startup singletons here suffice;
    //   - the delegate expansion path (ExpandLevelAsync → ResolveRequestSourcesForEdmType above), which
    //     re-resolves each of these same candidates through the request scope so their handlers may
    //     capture scoped dependencies (e.g. a DbContext).
    // Because both paths start from this exact same set, the gate and the delegate path can never
    // compute a different candidate set for the same navigation — only ResolveNavTreatment's decision
    // over that set matters, and it is likewise shared.
    internal static IReadOnlyList<IEntitySetEndpointSource> ResolveProfilesForEdmType(
        IEdmEntityType? targetEdmType, OhDataRegistration registration)
    {
        if (targetEdmType is null) return Array.Empty<IEntitySetEndpointSource>();

        string targetName = targetEdmType.FullTypeName();
        List<IEntitySetEndpointSource> candidates = new();
        foreach (IEntitySetEndpointSource profile in registration.Profiles)
        {
            IEdmEntityType? setType = registration.EdmModel.EntityContainer?
                .FindEntitySet(profile.EntitySetName)?.EntityType;
            if (setType is not null && setType.FullTypeName() == targetName) candidates.Add(profile);
        }
        return candidates;
    }

    // Gate-side convenience over ResolveProfilesForEdmType: resolves a CLR element type (as seen at
    // query-plan time, e.g. binding.ElementType) to its declared EDM entity type via the same
    // EdmClrTypeMap lookup IsMemberInitProjectable already relies on, then defers to
    // ResolveProfilesForEdmType so the gate's candidate set is computed by the exact same EDM-type
    // match the delegate path uses — never CLR-type equality/assignability on its own, which is what
    // made #293's original fix over-broad (matching a base/derived CLR type rather than the exact EDM
    // entity type the level is actually reached through).
    //
    // #508: the lookup used to be model.FindDeclaredType(clrType.FullName), which matches on the EDM
    // type's FULL NAME and so returns null for EVERY type on a renamed schema. The candidate set was
    // then empty, ResolveNavTreatment saw no candidates, and the gate deferred — silently, model-wide.
    // See EdmClrTypeMap for why the annotation route has no such failure mode, and why the lookup
    // stays EXACT here.
    private static IReadOnlyList<IEntitySetEndpointSource> ResolveProfilesForClrType(
        Type clrType, IEdmModel model, OhDataRegistration registration)
    {
        IEdmEntityType? edmType = EdmClrTypeMap.FindEntityType(model, clrType);
        return ResolveProfilesForEdmType(edmType, registration);
    }

    // Model B navigation treatment (owner decision 2026-07-26, FROZEN on #293). Each candidate set's
    // OWN declaration is authoritative for its OWN navigations; a delegate on a sibling never
    // retroactively poisons a nav another set legitimately serves raw. Fail-closed blanking happens
    // only on genuine disagreement.
    //
    // Partitions the exact-EDM-type candidate set for one level:
    //   DB(nav) = candidates that ROUTE it back;  DL(nav) = candidates that DECLARE it with no route.
    //   A candidate that does neither has no opinion and is ignored.
    //     DB empty                -> ServeRaw     (nobody delegates)
    //     DB one route, DL empty  -> RunDelegate  (sole unambiguous authority)
    //     DB and DL both non-empty-> Blank        (delegate-backed vs delegate-less disagree)
    //     DB 2+                   -> Blank        (distinct delegate routes disagree)
    // Deterministic: only set membership is read, never registration order.
    //
    // Used by BOTH the pushdown gate and the delegate expansion path, so they cannot diverge. #440
    // split off the other half of ServeRaw -- nobody declares OR routes it, so nothing ever loaded it
    // -- which the delegate path OMITS rather than emitting as null; see AnyCandidateHasOpinion.
    internal enum NavTreatment { ServeRaw, RunDelegate, Blank }

    // #440: AnyCandidateHasOpinion is "DB(navName) ∪ DL(navName) is non-empty" — i.e. at least one
    // candidate at this level either routes or declares the navigation. It is the COMPLEMENT of the
    // decision table's own "a candidate that neither routes nor declares the nav has no opinion on it
    // and is ignored" clause: when every candidate is in that category, DB and DL are both empty and
    // this is false. Reported here rather than recomputed by the caller so the two can never disagree
    // about what "has an opinion" means.
    //
    // It changes NO Treatment. The four rows above are byte-identical, the pushdown gate reads only
    // .Treatment, and Issue322ModelBClassificationTests pins the whole table through the Treatment
    // property alone. What it lets ExpandLevelAsync do is distinguish ServeRaw's two populations,
    // which are NOT the same claim: "a candidate declared this nav delegate-less, so the raw value is
    // authoritative" versus "nobody at this level has any opinion, so nothing ever chose to load it
    // and there is no authoritative value to serve". Emitting the second as null asserts that no
    // related entity exists, which the framework never determined.
    internal readonly record struct NavTreatmentResult(
        NavTreatment Treatment, NavigationRouteDefinition? Route, bool AnyCandidateHasOpinion);

    internal static NavTreatmentResult ResolveNavTreatment(string navName, IReadOnlyList<IEntitySetEndpointSource> candidates)
    {
        List<NavigationRouteDefinition>? delegateBacked = null; // DB(navName)
        bool anyDelegateLess = false; // DL(navName) non-empty?

        foreach (IEntitySetEndpointSource candidate in candidates)
        {
            NavigationRouteDefinition? route = candidate.NavigationRoutes.FirstOrDefault(n =>
                string.Equals(n.PropertyName, navName, StringComparison.OrdinalIgnoreCase));
            if (route is not null)
            {
                (delegateBacked ??= new List<NavigationRouteDefinition>()).Add(route);
            }
            else if (candidate.NavigationPropertyNames.Any(n => string.Equals(n, navName, StringComparison.OrdinalIgnoreCase)))
            {
                anyDelegateLess = true;
            }
        }

        bool anyOpinion = delegateBacked is not null || anyDelegateLess;
        if (delegateBacked is null) return new NavTreatmentResult(NavTreatment.ServeRaw, null, anyOpinion);
        if (delegateBacked.Count == 1 && !anyDelegateLess)
            return new NavTreatmentResult(NavTreatment.RunDelegate, delegateBacked[0], anyOpinion);
        return new NavTreatmentResult(NavTreatment.Blank, null, anyOpinion); // disagreement, or 2+ distinct routes
    }

    // JSON Format §4.5.1 / §11.2.4.2: a navigation not requested via $expand MUST NOT appear in the
    // payload -- never inline as an empty array or null. STJ has no notion of $expand, so this pass
    // walks the serialized JSON against the EDM and removes every navigation not expanded at its own
    // level, recursing into the expanded ones. It only OMITS; the data is injected beforehand by
    // ExpandLevelAsync. Only EDM-declared navigations are touched, so structural properties and
    // @odata.* annotations are safe by construction.
    //
    // Post-#325/#326 this is a PRACTICAL no-op at all five call sites -- SerializeBounded never writes
    // an un-expanded navigation in the first place. It stays wired in as defence against a
    // CALLER-level mistake (a future site that forgets SerializeBounded, or passes the wrong clause),
    // never against a decision-table bug: BuildExpandLookup/TryKeepNav are the single shared source of
    // the keep/recurse rules, so the two cannot drift on what "kept" means.
    internal static void OmitUnexpandedNavigations(
        JsonNode? node,
        IEdmEntityType? edmType,
        SelectExpandClause? clause,
        Type? clrType,
        JsonSerializerOptions? serializerOptions,
        (string Nav, int Remaining)? activeLevels = null,
        int maxLevels = MaxNestedExpandDepth,
        HashSet<string>? levelsNavNames = null)
    {
        if (edmType is null) return;

        // A JsonArray is a top-level collection or an expanded collection navigation — every
        // element is an entity of the same type sharing the same $expand context. A JsonObject is
        // a single entity. Anything else (null, i.e. an expanded single-valued navigation with no
        // related entity, or a primitive) has no navigations to strip and is left as-is.
        if (node is JsonArray array)
        {
            foreach (JsonNode? element in array)
            {
                OmitUnexpandedNavigations(element, edmType, clause, clrType, serializerOptions, activeLevels, maxLevels, levelsNavNames);
            }
            return;
        }
        if (node is not JsonObject obj) return;

        // Navigation name → its nested $expand clause, for the navigations expanded at THIS level.
        // Presence means "keep and recurse"; absence means "remove". #206: a nav carrying $levels=N is
        // ALSO recorded in levelsRemaining as its resolved recursion budget, so its self-reference is
        // kept (not stripped) at every level down to the depth actually loaded — Microsoft keeps
        // $levels implicit (a single top-level item), so without this the recursive levels below the
        // first would be stripped as "unexpanded". The keep is gated to navs that were actually PUSHED
        // (levelsNavNames): a delegate-backed $levels nav takes the delegate path (which loads only the
        // first level), so its deeper self-references must still be stripped as before — otherwise the
        // delegate's raw serialized graph would leak beyond depth 1.
        (Dictionary<string, SelectExpandClause?>? expanded, Dictionary<string, int>? levelsRemaining) =
            BuildExpandLookup(clause, levelsNavNames, maxLevels);

        // NavigationProperties() (not DeclaredNavigationProperties()) so inherited navigations on a
        // derived entity type are covered too. edmType is always an entity type here — the root is
        // the entity set's type and recursion passes navProp.ToEntityType() — so no complex-type
        // branch is needed.
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            // Match on the serialised key. #184: resolve the CLR property so a per-property
            // [JsonPropertyName] rename is honored ahead of the naming policy — System.Text.Json
            // writes a renamed nav under the attribute's exact name (it is NOT run through
            // PropertyNamingPolicy), so keying off the policy-converted name alone would miss a
            // renamed nav (leaking it inline) and a sibling $expand would write a second,
            // differently-cased key. Falls back to the naming-policy name when unrenamed, so a
            // symmetric JsonNamingPolicy (snake_case, etc.) still round-trips exactly.
            // #253 completion: navProp.Name is the EDM (JSON) navigation name, so map JSON→CLR to
            // reach the renamed CLR member and derive the payload key off it (a plain
            // GetProperty(jsonName) would miss it and mis-case the key under a non-camelCase policy).
            PropertyInfo? clrNavProp = clrType is null
                ? null
                : ODataPropertyNaming.FindClrPropertyByEdmName(clrType, navProp.Name);
            string serializedKey = ResolveNavigationJsonKey(clrNavProp?.Name ?? navProp.Name, clrNavProp, serializerOptions);

            // Fold-in #6 (de-duplication): TryKeepNav is the SAME keep/recurse rule SerializeBounded
            // uses (see its remarks below) — the two can no longer independently drift on what
            // "kept" means.
            NavKeepDecision decision = TryKeepNav(navProp.Name, expanded, levelsRemaining, activeLevels);
            if (!decision.Keep)
            {
                obj.Remove(serializedKey);
                continue;
            }

            // Recurse into the expanded value to strip ITS un-expanded navigations. obj[key]
            // is null when the expanded single-valued nav had no related entity — the recursive
            // call no-ops on a null node, so no separate presence check is needed. The nested
            // CLR type (element type for a collection nav) carries [JsonPropertyName] resolution
            // one level deeper.
            OmitUnexpandedNavigations(obj[serializedKey], navProp.ToEntityType(), decision.NestedClause,
                NavElementClrType(clrNavProp), serializerOptions, decision.ChildActive, maxLevels, levelsNavNames);
        }
    }

    // Fold-in #6 (#325/#326 review, de-duplication): the keep/recurse decision table shared by
    // OmitUnexpandedNavigations (JSON-strip substrate, above) and SerializeBounded (CLR-read+splice
    // substrate, below) — previously reimplemented byte-for-byte identically in both methods, which
    // had ALREADY drifted once (one passed maxLevels: source.MaxExpansionDepth at its Stage 1 call
    // site, the other silently defaulted to the file-wide MaxNestedExpandDepth ceiling — see fold-in
    // #5). Extracting a single shared implementation makes that class of drift structurally
    // impossible: the two methods now call the SAME code, so they can no longer disagree on what
    // "kept" means — only on what each one DOES with a kept/dropped decision (strip a JSON key vs.
    // never write a CLR value to it in the first place). This is orthogonal to (and does not weaken)
    // the defence-in-depth OmitUnexpandedNavigations itself provides — that comes from requiring the
    // CALL at every site, not from forking the decision logic (see its own remarks above).
    //
    // Returns navigation name -> its nested $expand clause for every navigation expanded at THIS
    // level, and (for a $levels-carrying self-referential nav that was actually PUSHED —
    // levelsNavNames) the resolved recursion budget. See OmitUnexpandedNavigations' original #206
    // remarks for the full $levels rationale.
    private static (Dictionary<string, SelectExpandClause?>? Expanded, Dictionary<string, int>? LevelsRemaining)
        BuildExpandLookup(SelectExpandClause? clause, HashSet<string>? levelsNavNames, int maxLevels)
    {
        Dictionary<string, SelectExpandClause?>? expanded = null;
        Dictionary<string, int>? levelsRemaining = null;
        if (clause is not null)
        {
            foreach (ExpandedNavigationSelectItem expandItem in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
            {
                string navName = expandItem.PathToNavigationProperty.FirstSegment.Identifier;
                (expanded ??= new Dictionary<string, SelectExpandClause?>(StringComparer.OrdinalIgnoreCase))
                    [navName] = expandItem.SelectAndExpand;
                if (expandItem.LevelsOption is { } lv && levelsNavNames is not null && levelsNavNames.Contains(navName))
                {
                    // #428: the SAME resolution rule TryBuildEngagedExpand uses, called rather than
                    // re-spelled — these two used to be independent transcriptions of "what does
                    // $levels resolve to", and they disagreed. Math.Max(_, 1) stays HERE and not in
                    // the shared helper: this side wants a floor (a kept nav needs at least one
                    // level of budget), while TryBuildEngagedExpand wants < 1 to mean "not pushable".
                    int resolved = ResolveLevelsBudget(lv.IsMaxLevel, lv.Level, maxLevels, MaxNestedExpandDepth);
                    (levelsRemaining ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))[navName] =
                        Math.Max(resolved, 1);
                }
            }
        }
        return (expanded, levelsRemaining);
    }

    // Fold-in #6: whether navPropName is kept (explicitly $expand'd at this level, or still within
    // an inherited $levels budget) and, when kept, the nested clause / child recursion budget to
    // carry into the next level. Keep-vs-drop mirrors OmitUnexpandedNavigations' original #206
    // remarks exactly — this record IS that rule, not a re-derivation of it.
    private readonly record struct NavKeepDecision(
        bool Keep, SelectExpandClause? NestedClause, (string Nav, int Remaining)? ChildActive);

    private static NavKeepDecision TryKeepNav(
        string navPropName,
        Dictionary<string, SelectExpandClause?>? expanded,
        Dictionary<string, int>? levelsRemaining,
        (string Nav, int Remaining)? activeLevels)
    {
        SelectExpandClause? nested = null;
        bool explicitlyExpanded = expanded is not null && expanded.TryGetValue(navPropName, out nested);
        // #206 ($levels): keep the self-referential nav even without an explicit nested item when a
        // parent $levels expansion still has recursion budget for it.
        bool keptByLevels = !explicitlyExpanded && activeLevels is { } al &&
            string.Equals(al.Nav, navPropName, StringComparison.OrdinalIgnoreCase) && al.Remaining > 0;

        if (!explicitlyExpanded && !keptByLevels) return new NavKeepDecision(false, null, null);

        SelectExpandClause? nestedClause = explicitlyExpanded ? nested : null;
        // Resolve the levels budget carried into the next recursion: a fresh $levels=N at this
        // level seeds N-1; otherwise the inherited budget decrements. When it reaches 0 the nav
        // is kept at this level but its own self-reference below is stripped (depth honored).
        int? nextLevels =
            levelsRemaining is not null && levelsRemaining.TryGetValue(navPropName, out int freshLevels)
                ? freshLevels - 1
                : (keptByLevels ? activeLevels!.Value.Remaining - 1 : (int?)null);
        (string, int)? childActive = nextLevels is int nl && nl > 0 ? (navPropName, nl) : null;

        return new NavKeepDecision(true, nestedClause, childActive);
    }

    // #325/#326 (owner decision, frozen): serialize only what the clause asked for, instead of
    // serializing the whole CLR graph and stripping afterwards. Whole-graph serialization is bounded
    // by the OBJECT graph while omission is bounded by the $expand CLAUSE, so EF relationship fixup
    // made SerializeToNode throw on a cycle before omission ever ran.
    //
    // Recursion is bounded by the clause tree, so a CLR reference cycle is structurally unreachable:
    // navigations are suppressed at the JsonTypeInfo level and spliced back per kept navigation.
    //
    // For a ServeRaw navigation the CLR value read here is final. For RunDelegate/Blank this may
    // splice a stale guess, which ExpandLevelAsync unconditionally overwrites afterwards -- a hard
    // ordering requirement from Model B (#292/#293). This method never consults NavTreatment.
    //
    // A null edmType is the deliberate whole-graph fallback for ODataEntityNode's deep-insert
    // opt-out (§11.4.2.2); do not "fix" it.
    //
    // Keys off value.GetType(), and #343: the navigation NAMES must come from the RUNTIME type's EDM
    // type too, not the declared one -- a navigation declared only on a derived type escaped
    // suppression and 500'd on a plain GET. `model` is threaded because Microsoft.OData.Edm gives an
    // IEdmEntityType no back-reference to its model.
    //
    // isCollectionValue comes from EDM cardinality, NEVER from `value is IEnumerable`: an entity
    // whose CLR class implements IEnumerable would be walked element-wise and corrupt the response.
    // #628: JSON Format §4.5.3 -- "The odata.type annotation MUST appear in minimal or full
    // metadata if the type cannot be heuristically determined ... and one of the following is true:
    // The type is derived from the type specified for the (collection of) entities". Identical in
    // 4.0 and 4.01 (checked separately: #372 was exactly a 4.0-vs-4.01 difference).
    //
    // Without it a derived row is indistinguishable from the base type it was declared as, so a
    // conforming client deserializes it into the base and silently drops the derived members -- the
    // same silent-loss shape as #529, one layer out on the wire. Microsoft.AspNetCore.OData emits it
    // (its own E2E expectations assert {"@odata.type":"#NS.Manager",...} inside a collection of
    // Person), so this is a divergence from MS as well as from the spec.
    //
    // Resolved through EdmClrTypeMap, whose lookup is EXACT (#508) -- which is what this needs: the
    // question is "what type IS this instance", never "what does it inherit from". A runtime type
    // the EDM does not map yields no annotation rather than a guess.
    private static void AnnotateDerivedType(
        JsonObject obj, Type clrType, IEdmStructuredType declared, IEdmModel? model)
    {
        if (model is null) return;
        if (EdmClrTypeMap.FindStructuredType(model, clrType) is not { } runtimeType) return;
        if (ReferenceEquals(runtimeType, declared)) return;

        obj["@odata.type"] = "#" + runtimeType.FullTypeName();
    }

    internal static JsonNode? SerializeBounded(
        object? value,
        IEdmEntityType? edmType,
        IEdmModel? model,
        SelectExpandClause? clause,
        JsonSerializerOptions? serializerOptions,
        (string Nav, int Remaining)? activeLevels = null,
        int maxLevels = MaxNestedExpandDepth,
        HashSet<string>? levelsNavNames = null,
        bool isCollectionValue = false)
    {
        if (value is null) return null;

        JsonSerializerOptions opts = serializerOptions ?? _pascalCaseSerializerOptions;

        if (edmType is null)
        {
            // Deliberate whole-graph fallback — see remarks above.
            return JsonSerializer.SerializeToNode(value, opts);
        }

        if (isCollectionValue)
        {
            // #337: the NESTED level is batched like the root -- one SerializeToNode for the whole
            // homogeneous sibling set. Batching used to fire only for the root page, and its fast path
            // ("the clause keeps no navigation") is unreachable for any $expand by construction, so
            // ~99% of an $expand payload was still serialized one entity at a time.
            //
            // Materialized into a List<object?> by ENUMERATION, never handed to STJ as the concrete
            // collection, for two load-bearing reasons: (1) an `object` element declared type makes STJ
            // resolve each element's RUNTIME type, so a DERIVED entity in a base-typed collection still
            // emits its own members -- the concrete List<TBase> would serialize by declared type and
            // silently drop them; (2) index alignment, since SerializeBoundedCollection pairs
            // batched[i] with values[i] and building the list by the same enumeration STJ walks keeps
            // that true even when a collection's indexer order disagrees with its enumeration order.
            //
            // `object`-declared is NOT the same as runtime-TYPED dispatch: it triggers STJ's
            // POLYMORPHIC RE-ENTRY and emits the nearest [JsonPolymorphic] ancestor's discriminator,
            // which the per-element call it replaces never did. Hence suppressPolymorphicMetadata.
            var elements = ((IEnumerable)value).Cast<object?>().ToList();
            return SerializeBoundedCollection(elements, edmType, model, clause, opts, maxLevels, levelsNavNames,
                activeLevels, suppressPolymorphicMetadata: true);
        }

        Type clrType = value.GetType();
        JsonSerializerOptions navSuppressed = GetNavSuppressedOptions(opts, model, (IEdmEntityType)edmType, clrType);
        JsonNode? node = JsonSerializer.SerializeToNode(value, clrType, navSuppressed);

        // Fold-in #2 (200→500 regression): a custom JsonConverter on the ENTITY type itself may
        // write a non-object shape (e.g. a bare string, as develop's whole-graph serializer would
        // also produce) — there are no navigations to splice into that, so return it as-is instead
        // of forcing .AsObject() and throwing InvalidOperationException.
        if (node is not JsonObject obj) return node;

        // #628: before the navigation splice, so an expanded navigation's own annotation (added by
        // the recursive call) cannot be confused with this level's.
        AnnotateDerivedType(obj, clrType, edmType, model);

        // Navigation name -> its nested $expand clause, for navigations expanded at THIS level —
        // and, for a $levels-carrying self-referential nav that was actually PUSHED (levelsNavNames),
        // the resolved recursion budget. Fold-in #6: shared with OmitUnexpandedNavigations via
        // BuildExpandLookup/TryKeepNav so the two can never disagree on what "kept" means.
        (Dictionary<string, SelectExpandClause?>? expanded, Dictionary<string, int>? levelsRemaining) =
            BuildExpandLookup(clause, levelsNavNames, maxLevels);

        SpliceKeptNavigations(obj, value, clrType, (IEdmEntityType)edmType, model, expanded, levelsRemaining, activeLevels, opts,
            maxLevels, levelsNavNames);

        return obj;
    }

    // Perf fix (measured regression vs. develop — GetAllPage/Filter/OrderBy/CountTrue/Select/
    // TopSkip allocation up 26-40%): the per-entity splice loop SerializeBounded ran inline above,
    // extracted so SerializeBoundedCollection (below) can reuse it WITHOUT re-deriving the
    // keep/recurse decision (expanded/levelsRemaining) per entity — every entity at the SAME level
    // shares the SAME clause, so BuildExpandLookup only needs to run ONCE per batch, not once per
    // entity. This is the single source both SerializeBounded and SerializeBoundedCollection call
    // to splice kept navigations onto an already nav-suppressed JsonObject; the two can no longer
    // independently drift on what gets spliced or how.
    private static void SpliceKeptNavigations(
        JsonObject obj,
        object value,
        Type clrType,
        IEdmEntityType edmType,
        IEdmModel? model,
        Dictionary<string, SelectExpandClause?>? expanded,
        Dictionary<string, int>? levelsRemaining,
        (string Nav, int Remaining)? activeLevels,
        JsonSerializerOptions opts,
        int maxLevels,
        HashSet<string>? levelsNavNames)
    {
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            // Perf (fold-in #7): decide keep/drop BEFORE any reflection — FindClrPropertyByEdmName/
            // ResolveNavigationJsonKey cost nothing for the (usually much more numerous) navigations
            // this level did NOT ask to see; they were already removed from `obj` by
            // GetNavSuppressedOptions above, so a dropped nav needs no further work at all.
            NavKeepDecision decision = TryKeepNav(navProp.Name, expanded, levelsRemaining, activeLevels);
            if (!decision.Keep) continue; // not requested — already suppressed above

            PropertyInfo? clrNavProp = ODataPropertyNaming.FindClrPropertyByEdmName(clrType, navProp.Name);
            object? navValue = clrNavProp?.GetValue(value);

            // Fold-in #1 (#325/#326 regression, DATA EXPOSURE): GetNavSuppressedOptions strips every
            // EDM navigation from clrType's JsonTypeInfo so System.Text.Json never walks into the
            // (potentially cyclic) graph at all — that suppression exists PURELY to keep the graph
            // walk bounded, never to decide member visibility. Splicing obj[serializedKey]
            // unconditionally below would therefore bypass whatever the BASE (un-suppressed) options
            // would themselves have decided about this member: a [JsonIgnore]'d navigation, one
            // hidden by JsonIgnoreCondition.WhenWritingNull/WhenWritingDefault, or one carrying a
            // custom [JsonConverter] (whose shape this recursive splice cannot honor) must all stay
            // absent/unmodified exactly as they would on develop — a clause-kept nav is never a
            // license to resurrect what the base serializer options would themselves have hidden.
            if (clrNavProp is not null && !IsNavVisibleInBaseOptions(opts, clrType, clrNavProp, value, navValue))
            {
                continue;
            }

            string serializedKey = ResolveNavigationJsonKey(clrNavProp?.Name ?? navProp.Name, clrNavProp, opts);
            if (navValue is null)
            {
                obj[serializedKey] = navProp.Type.IsCollection() ? new JsonArray() : null;
                continue;
            }

            obj[serializedKey] = SerializeBounded(
                navValue, navProp.ToEntityType(), model, decision.NestedClause, opts, decision.ChildActive, maxLevels,
                levelsNavNames, isCollectionValue: navProp.Type.IsCollection());
        }
    }

    // The collection-aware entry point: ONE SerializeToNode over the whole page under nav-suppressed
    // options, then kept navigations spliced in per element -- and the splice pass is skipped
    // entirely when the clause keeps none. SerializeBounded is per ENTITY, which measured +26-40%
    // allocated bytes across the read benchmarks when nothing was expanded.
    //
    // #337: this is also SerializeBounded's nested-collection branch, so each homogeneous sibling set
    // is one call too -- measured 1 batched call over ~1 KB against 1,000 calls over ~82 KB.
    //
    // Uses the SAME BuildExpandLookup/TryKeepNav table and the SAME SpliceKeptNavigations as the
    // per-entity path, so the two cannot disagree about what "kept" means. The keep/recurse decision
    // is computed once for the batch, which is valid only because every element is a sibling at one
    // level under one clause and one activeLevels budget.
    //
    // Array element order MUST match values' source order: the splice pairs batched[i]/values[i] by
    // index. SerializeBoundedWalkerTests asserts that pairing explicitly, because a heterogeneous nav
    // pattern is what turns a misaligned splice into wrong data on the wrong entity rather than a
    // coincidental pass.
    private static JsonArray SerializeBoundedCollection(
        IReadOnlyList<object?> values,
        IEdmEntityType? edmType,
        IEdmModel? model,
        SelectExpandClause? clause,
        JsonSerializerOptions? serializerOptions,
        int maxLevels = MaxNestedExpandDepth,
        HashSet<string>? levelsNavNames = null,
        (string Nav, int Remaining)? activeLevels = null,
        bool suppressPolymorphicMetadata = false)
    {
        JsonSerializerOptions opts = serializerOptions ?? _pascalCaseSerializerOptions;
        if (values.Count == 0) return new JsonArray();

        if (edmType is null)
        {
            // Deliberate whole-graph fallback — mirrors SerializeBounded's own edmType:null branch
            // (see its remarks). One call for the whole collection, exactly as develop's original
            // single Stage-1 SerializeToNode(object[], ...) call: values is typed as
            // IReadOnlyList<object>, so System.Text.Json resolves each element by its own runtime
            // type (the same "boxed object" polymorphism develop's array call already relied on),
            // not by a single shared declared type.
            return JsonSerializer.SerializeToNode(values, opts) as JsonArray ?? new JsonArray();
        }

        (Dictionary<string, SelectExpandClause?>? expanded, Dictionary<string, int>? levelsRemaining) =
            BuildExpandLookup(clause, levelsNavNames, maxLevels);

        // Fast-path probe: does the clause keep ANY navigation of edmType at this level? Exactly
        // the same TryKeepNav rule SpliceKeptNavigations applies per entity below, evaluated ONCE
        // for the whole batch. No $expand (or an entity type with zero EDM navigations, like the
        // benchmark model) always lands here with anyNavKept == false.
        bool anyNavKept = false;
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            if (TryKeepNav(navProp.Name, expanded, levelsRemaining, activeLevels).Keep)
            {
                anyNavKept = true;
                break;
            }
        }

        // Walk the DISTINCT runtime types present, once, before the single batched serialize call
        // below. GetNavSuppressedOptions returns the SAME derived options instance regardless of
        // clrType (see CreateNavSuppressionState), so any successful call captures it.
        //
        // #482: this loop is NO LONGER load-bearing for suppression correctness, and must not be
        // read as if it were. It used to be the pre-population that "guaranteed" every element type
        // had a suppression set before its JsonTypeInfo was resolved — a guarantee that covered only
        // the types in THIS collection and left every transitively reached type frozen
        // un-suppressed. The modifier now computes each type's set itself from the seeded schema, so
        // a type this loop never sees is suppressed exactly as one it does. What the loop still does
        // is seed the model (once) and pair each runtime type with the caller's declared EDM type for
        // the no-ClrTypeAnnotation residue — plus the polymorphism probe piggy-backed below.
        JsonSerializerOptions? navSuppressed = null;
        HashSet<Type>? seenTypes = null;
        bool polymorphic = false;
        // #628: piggy-backed on the same distinct-type pass as the polymorphism probe below, so a
        // homogeneous collection -- the overwhelmingly common case -- costs one cached EDM lookup
        // for the single runtime type present and nothing per element.
        bool anyDerived = false;
        foreach (object? value in values)
        {
            if (value is null) continue;
            Type t = value.GetType();
            if ((seenTypes ??= new HashSet<Type>()).Add(t))
            {
                navSuppressed = GetNavSuppressedOptions(opts, model, (IEdmEntityType)edmType, t);
                if (!anyDerived && model is not null &&
                    EdmClrTypeMap.FindStructuredType(model, t) is { } runtimeEdmType &&
                    !ReferenceEquals(runtimeEdmType, edmType))
                {
                    anyDerived = true;
                }

                // Piggy-backed on the distinct-type pass that already exists, so the polymorphism
                // test costs one cached lookup per DISTINCT runtime type per collection — never a
                // per-element check on the hot path.
                if (suppressPolymorphicMetadata && !polymorphic && EmitsPolymorphicMetadata(opts, t))
                {
                    polymorphic = true;
                }
            }
        }
        if (navSuppressed is null)
        {
            // Every element was null, so no runtime type was available to pre-populate suppression
            // for and there is nothing to serialize. #337: a JSON null per element, NOT an empty
            // array — a nested collection navigation may legitimately hold nulls, and the
            // per-element path this replaces emitted exactly one null per null element
            // (SerializeBounded returns null for a null value), so this is what keeps the nested
            // level byte-identical.
            //
            // The root call site's behaviour is unchanged either way, but NOT because "a page never
            // contains a null entity" — that would be an assumption about handler behaviour, not an
            // invariant. It is because a null entity in the page fails EARLIER in the pipeline: a
            // GetQueryable returning [null, null] 500s upstream of this method on develop and on
            // this branch alike, so the two shapes are never distinguishable at the root.
            // Independently, this shape is the safer one: develop's `return new JsonArray()` would
            // leave the returned array SHORTER than originalItems, which Stage 2 (ETag injection)
            // and Stage 3 (expansion) index by originalItems.Length — a latent desync this removes.
            var allNull = new JsonArray();
            for (int i = 0; i < values.Count; i++) allNull.Add((JsonNode?)null);
            return allNull;
        }

        if (polymorphic)
        {
            // #337 correctness fallback: at least one element's type hierarchy has polymorphism
            // CONFIGURED (a [JsonPolymorphic] ancestor, or the equivalent set up by a custom
            // TypeInfoResolver — see EmitsPolymorphicMetadata, which asks STJ rather than reading
            // attributes). Batching hands STJ an `object`-declared element, which makes it take the
            // polymorphic re-entry path and write a type discriminator ("$kind", "$type", ...) that
            // the per-element call never wrote. That is not a cosmetic difference: the discriminator
            // is an arbitrary STJ key in an OData payload (not @odata.type), StripToSelectedProperties
            // /KeepUnderSelect only preserve keys containing '@' so it silently vanishes under
            // $select, and the standalone navigation route (which still serializes per element)
            // would disagree with $expand on the same navigation.
            //
            // So for these collections only, serialize exactly as before batching: one
            // SerializeToNode per element with declared type == runtime type. Everything else keeps
            // the batched path — this costs the optimization only for genuinely polymorphic models.
            //
            // Deliberately NOT applied at the root call site (suppressPolymorphicMetadata defaults to
            // false): the root page was ALREADY batched before #337, so develop already emits the
            // discriminator there. Suppressing it at the root would be a second output change, not a
            // fix — byte-identity with develop is the acceptance criterion, not internal symmetry.
            var perElement = new JsonArray();
            foreach (object? value in values)
            {
                perElement.Add(SerializeBounded(
                    value, edmType, model, clause, opts, activeLevels, maxLevels, levelsNavNames));
            }
            return perElement;
        }

        JsonArray batched = JsonSerializer.SerializeToNode(values, navSuppressed) as JsonArray ?? new JsonArray();

        // #628: ahead of the fast path on purpose. The annotation is required whether or not any
        // navigation was kept, so sitting behind `anyNavKept` would emit it only for requests that
        // happened to carry $expand.
        if (anyDerived)
        {
            for (int i = 0; i < values.Count && i < batched.Count; i++)
            {
                if (values[i] is { } derivedValue && batched[i] is JsonObject derivedObj)
                {
                    AnnotateDerivedType(derivedObj, derivedValue.GetType(), edmType, model);
                }
            }
        }

        if (!anyNavKept) return batched; // FAST PATH: nothing to splice.

        for (int i = 0; i < values.Count && i < batched.Count; i++)
        {
            // node-is-not-JsonObject guard (fold-in #2, 200→500 regression fix): a custom
            // JsonConverter on the entity type may write a non-object shape — nothing to splice
            // into. A null values[i] (defensive; the collection GET path never hands this method a
            // null entity) is likewise left as whatever the batched call already produced for it.
            if (values[i] is not { } value || batched[i] is not JsonObject obj) continue;

            SpliceKeptNavigations(obj, value, value.GetType(), (IEdmEntityType)edmType, model, expanded, levelsRemaining,
                activeLevels, opts, maxLevels, levelsNavNames);
        }

        return batched;
    }

    // True when the BASE (pre-suppression) JsonTypeInfo would itself emit clrNavProp for this
    // instance. Resolved through the SAME resolver GetNavSuppressedOptions falls back to, never
    // opts.GetTypeInfo directly: an options instance that has never been handed to Serialize and
    // carries no explicit TypeInfoResolver throws NotSupportedException from GetTypeInfo even though
    // SerializeToNode tolerates it -- and _pascalCaseSerializerOptions is exactly such an instance.
    //
    // BOTH [JsonIgnore] SPELLINGS ARE DECIDED BY ShouldSerialize, NOT BY ABSENCE. Measured on .NET
    // 10.0.11, an unconditionally ignored member STAYS in Properties with Get/Set nulled and a
    // ShouldSerialize returning false -- so the `return` inside the loop answers for it, not the
    // trailing `return false`. (A previous comment here claimed the member was removed entirely.)
    //
    // That is load-bearing, not cosmetic: OpenTypeJsonOptions.Build snapshots its declared-name set
    // from this same collection, so a navigation carrying [JsonIgnore] still collides with a bag key
    // and still hard-fails instead of being quietly shadowed.
    //
    // The trailing `return false` covers only members genuinely absent from the base contract --
    // above all one an earlier modifier REMOVED, which is how Ignore() works and is the real
    // difference from [JsonIgnore]: a removed member is why extension data can capture it.
    //
    // A custom [JsonConverter] on the property is treated as not natively visible too -- its wire
    // shape cannot be reproduced by the recursive splice, so it is omitted rather than corrupted.
    private static bool IsNavVisibleInBaseOptions(
        JsonSerializerOptions opts, Type clrType, PropertyInfo clrNavProp, object entityValue, object? navValue)
    {
        if (clrNavProp.GetCustomAttribute<JsonConverterAttribute>() is not null) return false;

        JsonTypeInfo? typeInfo = GetBaseTypeInfo(opts, clrType);
        if (typeInfo is null) return false;
        foreach (JsonPropertyInfo p in typeInfo.Properties)
        {
            // #462/#343, a fifth instance of the same defect class, found by the shared fixture rather
            // than by the issues. `pi != clrNavProp` is PropertyInfo equality, which compares
            // ReflectedType, and for an INHERITED navigation on a DERIVED instance the two reflection
            // walks disagree -- measured on .NET 10.0.11, FindClrPropertyByEdmName reports the derived
            // type while STJ's AttributeProvider reports the base, so `==` is false where
            // HasSameMetadataDefinitionAs is true.
            //
            // The loop therefore never matched, the trailing `return false` answered "not visible",
            // and a navigation the client explicitly $expand'ed was silently DROPPED from every derived
            // instance while base instances in the same page kept theirs -- the exact mirror of #343.
            //
            // Safe for the same reason it is in OpenTypeJsonOptions.Build: both sides are members of
            // clrType or one of its bases, and one type's member list cannot hold two instantiations of
            // a single generic definition.
            if (p.AttributeProvider is not PropertyInfo pi || !pi.HasSameMetadataDefinitionAs(clrNavProp)) continue;
            return p.ShouldSerialize is null || p.ShouldSerialize(entityValue, navValue);
        }
        // Absent from the base contract entirely. NOT the [JsonIgnore] case — that member is still in
        // Properties and was answered by the ShouldSerialize return above (see the note on this
        // method). This is the modifier-REMOVED case (Ignore(), #226) and the
        // no-metadata-for-the-type case.
        return false;
    }

    // One derived JsonSerializerOptions per baseOptions -- N entity types share one instance and
    // therefore one JsonTypeInfo cache.
    //
    // #482: the modifier computes the suppression set ITSELF from (typeInfo.Type, the EDM) at
    // contract-resolution time. Do not reintroduce pre-population keyed on which route reached the
    // type: STJ caches a JsonTypeInfo on first use, so a type reached transitively (an open-type bag
    // value, an object-declared member) froze un-suppressed for the process lifetime -- a 500 on
    // every subsequent plain GET of that set.
    //
    // #508: the map is keyed off ClrTypeAnnotation, NOT model.FindDeclaredType(clrType.FullName),
    // which misses every type on a renamed schema. #507: keyed by IEdmStructuredType, not entity
    // types -- a complex type's entity-typed member is a navigation ON THE COMPLEX TYPE.
    //
    // Suppressed, never served: §4.5.1 omits a non-expanded navigation, and SpliceKeptNavigations
    // only walks the entity type's own, so nothing could put it back.
    private sealed record NavSuppressionState(
        JsonSerializerOptions Derived,
        IJsonTypeInfoResolver BaseResolver,
        ConcurrentDictionary<Type, HashSet<string>> NavClrNamesByType,
        ConcurrentDictionary<Type, JsonTypeInfo?> BaseTypeInfoByType,
        ConcurrentDictionary<Type, bool> PolymorphicByType,
        ConcurrentDictionary<Type, NavSourceBinding> EdmTypeByClrType,
        ConcurrentDictionary<IEdmModel, bool> SeededModels,
        object SeedGate);

    // #482: what the seeded map holds. The MODEL travels with the EDM type because the
    // navigation -> CLR member mapping the builder recorded lives as an annotation ON THE MODEL, and
    // Microsoft.OData.Edm gives an IEdmNavigationProperty no back-reference to the model that owns
    // it - the same reason IEdmModel is threaded through the SerializeBounded family (#343).
    // #507: IEdmStructuredType, not IEdmEntityType — a complex type carrying an entity-typed member
    // declares navigations too, and they were never in the map (see CreateNavSuppressionState).
    private readonly record struct NavSourceBinding(IEdmModel? Model, IEdmStructuredType EdmType);

    private static readonly ConditionalWeakTable<JsonSerializerOptions, NavSuppressionState>
        s_navSuppressedOptionsCache = new();

    private static NavSuppressionState CreateNavSuppressionState(JsonSerializerOptions baseOptions)
    {
        var navClrNamesByType = new ConcurrentDictionary<Type, HashSet<string>>();
        var edmTypeByClrType = new ConcurrentDictionary<Type, NavSourceBinding>();
        IJsonTypeInfoResolver baseResolver = baseOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        var derived = new JsonSerializerOptions(baseOptions);
        derived.TypeInfoResolver = baseResolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            // #482: COMPUTE, never look up and give up. The old code did TryGetValue and returned
            // when the type had no entry — which is precisely how a transitively reached type froze
            // un-suppressed forever. GetOrAdd means the answer for a type is decided HERE, once, from
            // the EDM, no matter who reached it or in what order.
            HashSet<string> navClrNames =
                navClrNamesByType.GetOrAdd(typeInfo.Type, t => BuildNavClrNames(t, edmTypeByClrType));
            if (navClrNames.Count == 0) return;
            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo prop && navClrNames.Contains(prop.Name))
                    typeInfo.Properties.RemoveAt(i);
            }
        });
        return new NavSuppressionState(
            derived, baseResolver, navClrNamesByType,
            new ConcurrentDictionary<Type, JsonTypeInfo?>(), new ConcurrentDictionary<Type, bool>(),
            edmTypeByClrType, new ConcurrentDictionary<IEdmModel, bool>(), new object());
    }

    // #482: the modifier's pure function -- the CLR property names on clrType backing a navigation of
    // ANY EDM entity type on clrType's own base chain.
    //
    // The base walk covers a runtime type the EDM does not declare at all: an EF lazy-loading proxy, a
    // DynamicProxy subclass, any derived type returned through a base-typed set. Its nearest EDM-known
    // ancestor's navigations are resolved AGAINST THE RUNTIME TYPE, never the ancestor, so a shadowed
    // member resolves to the one STJ will actually put on the contract.
    //
    // UNION over the chain, not nearest-wins, and not interchangeable: navigation sets are a
    // suppression boundary, so a derived type's set must never shadow its base's. Same policy as
    // InheritedNameSets.Resolve; interfaces deliberately not walked.
    //
    // Cost: memoized per distinct runtime type per options instance, nothing per request. The schema
    // walk that feeds it measures ~0.9 us per EDM entity type, paid once at MapOhData().
    private static HashSet<string> BuildNavClrNames(
        Type clrType, ConcurrentDictionary<Type, NavSourceBinding> edmTypeByClrType)
    {
        var navClrNames = new HashSet<string>(StringComparer.Ordinal);
        for (Type? cur = clrType; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            if (edmTypeByClrType.TryGetValue(cur, out NavSourceBinding binding))
                AddNavClrNames(navClrNames, binding.Model, binding.EdmType, clrType);
        }
        return navClrNames;
    }

    // #482: one walk of the model's schema mapping every EDM type to its CLR type, so BuildNavClrNames
    // is TOTAL rather than dependent on who called first. Called from MapAll before any request, and
    // defensively from GetNavSuppressedOptions, since the SerializeBounded family is reachable from
    // the _pascalCaseSerializerOptions fallback without going through MapAll.
    //
    // CONCURRENCY: whichever thread resolves a JsonTypeInfo first decides that type's behaviour for
    // the process lifetime, so "seeded" must never be observable before the map is complete. A bare
    // TryAdd guard would do exactly that -- the loser concludes "already seeded" and serializes
    // against a half-filled map. The flag is written INSIDE the lock and LAST.
    //
    // Additive across models: two registrations sharing one options instance union their schemas.
    // The residue is a CLR type declared by BOTH with DIFFERENT navigations, whose first-resolved
    // contract wins -- which #458 already refuses within a registration.
    private static void SeedNavSuppressionModel(NavSuppressionState state, IEdmModel? model)
    {
        if (model is null || state.SeededModels.ContainsKey((IEdmModel)model)) return;
        lock (state.SeedGate)
        {
            if (state.SeededModels.ContainsKey((IEdmModel)model)) return;
            // EdmClrTypeMap reads ODataConventionModelBuilder's own ClrTypeAnnotation for every
            // STRUCTURED type the schema declares — the same "read the builder's own annotation"
            // route OpenTypeJsonOptions takes for a complex type's dynamic-property container, and
            // for the same reason: it involves no name convention, so a renamed schema namespace
            // cannot make it miss. Absent only for a hand-built IEdmModel, which OhData never
            // produces; GetNavSuppressedOptions' caller pairing below covers that residue for
            // directly served types.
            //
            // #507: entity AND complex. The walk used to be OfType<IEdmEntityType>(), which is why a
            // complex type's own entity-typed navigation was never in any suppression set.
            foreach (KeyValuePair<Type, IEdmStructuredType> pair in EdmClrTypeMap.ForModel((IEdmModel)model))
            {
                state.EdmTypeByClrType.TryAdd(pair.Key, new NavSourceBinding(model, pair.Value));
            }
            state.SeededModels[model] = true;
        }
    }

    // #482: called once per registration from MapAll, with the SAME options instance every route
    // closure is handed, so the schema walk happens before any request rather than on whichever
    // request happens to arrive first. Purely a map fill — it resolves no JsonTypeInfo and installs
    // no modifier, so it is inert with respect to the ignore -> open-type -> nav-suppression modifier
    // ordering invariant (OpenTypeModifierOrderingTests).
    internal static void PrimeNavSuppression(JsonSerializerOptions baseOptions, IEdmModel? model)
    {
        SeedNavSuppressionModel(
            s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState), model);
    }

    // #343: the suppression set comes from the RUNTIME type, not the declared EDM type alone.
    // #325/#326 rest on the premise that no navigation reaches STJ unless the clause asked for it;
    // enumerating only edmType.NavigationProperties() broke that for a navigation declared on a
    // DERIVED type, which then emitted inline and 500'd on a cycle -- on a plain GET, no query string.
    //
    // Suppressed, NOT served: §4.5.1 omits a non-expanded navigation, and the clause binds against
    // the DECLARED type, so a derived-declared nav has no route into `expanded` and "serve it" would
    // mean serving it unconditionally.
    //
    // #482 moved the mechanism, not the decision: the resolver modifier computes the set itself at
    // contract-resolution time, so all this method does is seed the CLR->EDM map before the caller's
    // SerializeToNode. #508: that seeding goes through EdmClrTypeMap, never
    // FindDeclaredType(clrType.FullName), which silently returns null on a renamed schema.
    //
    // The two TryAdds are the residue guard for a hand-built IEdmModel carrying no ClrTypeAnnotation;
    // TryAdd never overwrites what the seed already placed.
    private static JsonSerializerOptions GetNavSuppressedOptions(
        JsonSerializerOptions baseOptions, IEdmModel? model, IEdmEntityType edmType, Type clrType)
    {
        NavSuppressionState state = s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState);
        SeedNavSuppressionModel(state, model);
        if (!state.EdmTypeByClrType.ContainsKey(clrType))
        {
            if (EdmClrTypeMap.FindStructuredType(model, clrType) is { } runtimeEdmType)
                state.EdmTypeByClrType.TryAdd(clrType, new NavSourceBinding(model, runtimeEdmType));
            state.EdmTypeByClrType.TryAdd(clrType, new NavSourceBinding(model, edmType));
        }
        return state.Derived;
    }

    // The CLR property names on clrType backing edmType's navigations (NavigationProperties() is
    // inherited-inclusive). #507: edmType is an IEdmStructuredType, because a COMPLEX type carries
    // navigations too -- one per entity-typed member.
    //
    // TWO routes, UNIONED, because either alone has a blind spot (#482):
    //   (1) FindClrPropertyByEdmName -- the same lookup the splice uses, so suppression and splice
    //       cannot disagree about which member is meant.
    //   (2) the builder's own ClrPropertyInfoAnnotation -- authoritative. Route (1) matches on the
    //       EDM NAME, so an EDM-level rename defeats it: measured, `HasMany(b => b.Children).Name =
    //       "Kids"` leaves the annotation reporting `Children` while the lookup for `Kids` returns
    //       null, so that navigation was NEVER suppressed on any route.
    // (2) is absent for a hand-built IEdmModel and (1) covers members (2) never recorded.
    // Over-suppression is not a risk: a name not on the contract removes nothing.
    //
    // KNOWN and out of scope: SpliceKeptNavigations still reads through route (1) alone, so an
    // EDM-renamed navigation that IS $expanded splices as an empty array. Strictly better than
    // before, when the same request emitted the whole un-suppressed graph under the wrong key.
    private static void AddNavClrNames(
        HashSet<string> into, IEdmModel? model, IEdmStructuredType edmType, Type clrType)
    {
        foreach (IEdmNavigationProperty navProp in edmType.NavigationProperties())
        {
            PropertyInfo? clrProp = ODataPropertyNaming.FindClrPropertyByEdmName(clrType, navProp.Name);
            if (clrProp is not null) into.Add(clrProp.Name);

            string? declaredMember = model?
                .GetAnnotationValue<Microsoft.OData.ModelBuilder.ClrPropertyInfoAnnotation>(navProp)?
                .ClrPropertyInfo?.Name;
            if (declaredMember is not null) into.Add(declaredMember);
        }
    }

    // Fold-in #1 support: the BASE (un-suppressed) JsonTypeInfo for clrType under baseOptions,
    // resolved via the captured BaseResolver fallback (see CreateNavSuppressionState) and cached
    // alongside the nav-suppression state for that baseOptions instance. Returns null when the
    // resolver has no metadata for clrType at all (e.g. a non-object/primitive runtime type reached
    // through a base-typed navigation) — treated as "not visible" by the caller, which is always
    // safe (omission, never corruption).
    private static JsonTypeInfo? GetBaseTypeInfo(JsonSerializerOptions baseOptions, Type clrType)
    {
        NavSuppressionState state = s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState);
        return state.BaseTypeInfoByType.GetOrAdd(clrType, type => state.BaseResolver.GetTypeInfo(type, baseOptions));
    }

    // #337 correctness support: would System.Text.Json write polymorphic metadata (a type
    // discriminator) for an instance of <paramref name="clrType"/> when it is reached through an
    // `object`-declared slot — i.e. through the batched SerializeBoundedCollection call rather than
    // a per-element SerializeToNode(value, value.GetType(), ...)?
    //
    // Asks STJ, never the attributes. Polymorphism is only USUALLY declared with
    // [JsonPolymorphic]/[JsonDerivedType]; it can equally be configured by a custom
    // TypeInfoResolver or a JsonTypeInfo modifier, which attribute reflection would miss entirely
    // (and would then silently re-introduce the discriminator leak this guards against). The
    // resolved JsonTypeInfo.PolymorphismOptions is the single authority for both spellings, and it
    // is resolved through the SAME captured BaseResolver every other lookup in this file uses (see
    // IsNavVisibleInBaseOptions' remarks on why `opts.GetTypeInfo` is not safe here).
    //
    // Walks base classes AND interfaces because the discriminator comes from the nearest configured
    // ANCESTOR, not from clrType itself: for `A : Base` where Base carries the configuration, A's
    // own type info has no PolymorphismOptions at all.
    //
    // Memoized per (clrType, options) alongside the rest of the per-options state, so a collection
    // pays one dictionary lookup per distinct runtime type and the hierarchy walk happens once.
    private static bool EmitsPolymorphicMetadata(JsonSerializerOptions baseOptions, Type clrType)
    {
        NavSuppressionState state = s_navSuppressedOptionsCache.GetValue(baseOptions, CreateNavSuppressionState);
        if (state.PolymorphicByType.TryGetValue(clrType, out bool cached)) return cached;

        bool result = false;
        for (Type? t = clrType; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (GetBaseTypeInfo(baseOptions, t)?.PolymorphismOptions is not null) { result = true; break; }
        }
        // Interfaces are unordered and have no single "nearest" ancestor to walk, so unlike the base
        // chain above there is nothing to step through - the question is purely "does ANY of them
        // configure polymorphism", which Any states directly and short-circuits identically.
        result = result || clrType.GetInterfaces()
            .Any(iface => GetBaseTypeInfo(baseOptions, iface)?.PolymorphismOptions is not null);

        // Racing writers compute the same answer, so last-write-wins is safe here.
        state.PolymorphicByType[clrType] = result;
        return result;
    }

    // #338 (perf): the resolved key, memoized per (PropertyInfo, JsonSerializerOptions). Those two
    // ARE the full dependency set of the computation below — the [JsonPropertyName] rename is a
    // function of the property alone, and the fallback is a function of the options'
    // PropertyNamingPolicy alone — so the key is exactly as wide as the answer and no wider.
    // Keying on PropertyInfo alone would be WRONG: two registrations may carry different naming
    // policies (OhDataBuilder.WithJsonPropertyNamingPolicy is per-registration), and they would
    // collide on the un-renamed branch.
    //
    // Shaped as ConditionalWeakTable<options, ConcurrentDictionary<PropertyInfo, string>> rather
    // than one strong-keyed ConcurrentDictionary<(options, prop), string> for the same reason
    // s_navSuppressedOptionsCache is (see fold-in #7 there): a strong options key leaks an entry per
    // distinct JsonSerializerOptions for the life of the process, which a test suite that builds a
    // fresh WebApplicationFactory host per class hits hard. The inner PropertyInfo keys are
    // collected with the options entry that roots them.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<PropertyInfo, string>>
        s_navJsonKeyCache = new();

    // #184: resolve the JSON key a navigation property serializes to. A per-property
    // [System.Text.Json.Serialization.JsonPropertyName] rename wins (STJ emits it verbatim);
    // otherwise the naming policy converts the CLR name (and a null policy leaves it unchanged).
    //
    // #338 (perf): GetCustomAttribute is not cheap and this is a hot-path call — OmitUnexpandedNavigations
    // reaches it once per EDM navigation per JSON object (~3,000 times on a 1,000-row, 3-navigation
    // $expand), inside a pass its own header documents as a PRACTICAL no-op. Memoized here rather
    // than reordered around the keep/drop test, because BOTH branches of that test need the key
    // (the drop branch to obj.Remove it, the keep branch to index into it), so a reorder saves
    // nothing — see OmitUnexpandedNavigations.
    private static string ResolveNavigationJsonKey(
        string navClrName, PropertyInfo? clrNavProp, JsonSerializerOptions? serializerOptions)
    {
        // No CLR property (AdvancedConfigure EDM with no matching member): nothing stable to key a
        // cache entry on, and no attribute lookup to save — the naming-policy call is all there is.
        //
        // CALLER INVARIANT: when clrNavProp is non-null, navClrName IS clrNavProp.Name — every call
        // site passes `clrNavProp?.Name ?? <edm fallback>` or `prop.Name` directly. The cache below
        // therefore keys on the property alone and recomputes from prop.Name. A defensive
        // navClrName != clrNavProp.Name branch was tried here and removed: it is unreachable at all
        // five call sites, and an unexecutable branch on a hot path can never be validated.
        if (clrNavProp is null)
        {
            return serializerOptions?.PropertyNamingPolicy?.ConvertName(navClrName) ?? navClrName;
        }

        // A null options argument and _pascalCaseSerializerOptions produce identical answers (the
        // latter's PropertyNamingPolicy is null), so they can safely share one cache entry — the
        // same substitution every other method in this file makes for a null options argument.
        JsonSerializerOptions optionsKey = serializerOptions ?? _pascalCaseSerializerOptions;
        return s_navJsonKeyCache.GetOrCreateValue(optionsKey).GetOrAdd(
            clrNavProp,
            static (prop, opts) =>
            {
                JsonPropertyNameAttribute? rename = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (rename is not null) return rename.Name;
                return opts.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
            },
            optionsKey);
    }

    // #184: the CLR type carrying a navigation target's own properties — the element type for a
    // collection navigation (List<T>/T[]/IEnumerable<T>), or the property type itself for a
    // single-valued navigation — so nested [JsonPropertyName] resolution can recurse. Returns null
    // when the CLR property is unknown (e.g. AdvancedConfigure EDM with no matching CLR member).
    private static Type? NavElementClrType(PropertyInfo? clrNavProp)
    {
        if (clrNavProp is null) return null;
        Type navType = clrNavProp.PropertyType;
        if (navType == typeof(string)) return navType;
        if (navType.IsArray) return navType.GetElementType();
        foreach (Type iface in new[] { navType }
            .Concat(navType.GetInterfaces())
            .Where(iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            return iface.GetGenericArguments()[0];
        }
        return navType;
    }

    // Batch 4: Inject @odata.etag into a JsonArray using the original (pre-expand) items array
    // to compute each ETag. Per OData-JSON §4.5, annotations precede the properties they describe,
    // so rebuild each JsonObject with @odata.etag first.
    internal static JsonArray InjectETagsIntoJsonArray(JsonArray json, object[] originalItems, IEntitySetEndpointSource source)
    {
        for (int i = 0; i < Math.Min(json.Count, originalItems.Length); i++)
        {
            if (json[i] is JsonObject obj)
            {
                string etag = source.InvokeGetETag(originalItems[i]);
                var reordered = new JsonObject { ["@odata.etag"] = JsonValue.Create($"\"{etag}\"") };
                foreach (var prop in obj.ToList())
                {
                    obj.Remove(prop.Key);
                    reordered[prop.Key] = prop.Value;
                }
                json[i] = reordered;
            }
        }
        return json;
    }

    // M3: returns the client's $select (+ $expand) property list, in request order and
    // de-duplicated, so both the Stage-4 body filter and the projected context URL
    // ("#Set(prop1,prop2)", JSON §10.7/§10.8) agree on exactly which properties were selected
    // and in what order. Ordinal-case as normalized by the Microsoft.OData parser (which
    // resolves $select identifiers to the EDM property name regardless of the casing the
    // client sent).
    internal static List<string>? ExtractSelectedProperties(SelectExpandClause clause)
    {
        if (clause.AllSelected) return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var props = new List<string>();
        foreach (var item in clause.SelectedItems)
        {
            if (item is PathSelectItem psi)
            {
                string id = psi.SelectedPath.FirstSegment.Identifier;
                if (seen.Add(id)) props.Add(id);
            }
        }

        // When only $expand (no $select) is used, AllSelected is false but SelectedItems
        // has no PathSelectItems — only ExpandedNavigationSelectItems. An empty set would
        // strip every property in Stage 4, so treat this as "keep all".
        if (props.Count == 0) return null;

        // Preserve expanded nav properties so they survive Stage 4 when $select and
        // $expand are combined (e.g. $select=Name&$expand=Children keeps both).
        foreach (var ensi in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string id = ensi.PathToNavigationProperty.FirstSegment.Identifier;
            if (seen.Add(id)) props.Add(id);
        }

        return props;
    }

    /// <summary>
    /// #206: composes the <c>$select</c> member-init projection
    /// (<c>x =&gt; new TModel { A = x.A, ... }</c>) onto <paramref name="query"/> when the
    /// request is eligible, returning <paramref name="query"/> unchanged (full fetch — today's
    /// behavior) otherwise. The projection set is selected structural properties ∪ the entity
    /// key (always: <c>@odata.id</c>, expansion correlation, <c>$skiptoken</c>) ∪ the
    /// <c>UseETag</c> properties (so <c>@odata.etag</c> is identical with and without
    /// pushdown). The lambda is built per request and deliberately UNCACHED: <c>$select</c>
    /// combinations are client-controlled and unbounded, so a lambda cache keyed by select-set
    /// would be an unbounded-growth vector (#202 hardening ethos); LINQ providers' own query
    /// caches key structurally and absorb repeated shapes.
    /// <para>
    /// #206 phase 2: when <paramref name="expandNavs"/> is supplied (the $expand pushdown path),
    /// each pushed navigation is folded into the same member-init — a collection as
    /// <c>Nav = x.Nav[.Where(f)][.OrderBy(o)][.Skip(s)][.Take(t)].ToList()</c> (the nested
    /// $filter/$orderby/$top/$skip of the expand, bound by Microsoft's FilterBinder/OrderByBinder;
    /// see BuildShapedNavAccess), a single-valued reference as <c>Ref = x.Ref</c> — so one EF Core
    /// query loads the related rows via a JOIN. When <c>null</c> (the $select-only path) the
    /// projection is byte-for-byte what it was before. Ineligibility (no ctor / unknowable ETag
    /// names / complex or unsettable structural member, or a nested clause the binder cannot bind)
    /// returns <paramref name="query"/> unchanged; the caller detects that by reference and abandons
    /// expand pushdown for the request, so the folded navigations are never partially applied.
    /// </para>
    /// </summary>
    internal static IQueryable<TModel> TryApplySelectProjection<TModel>(
        IQueryable<TModel> query,
        IReadOnlyList<string> selectedNames,
        IEntitySetEndpointSource source,
        bool hasParameterlessCtor,
        IReadOnlyDictionary<string, StructuralPropertyInfo> structuralByName,
        ILogger? logger,
        IReadOnlyList<EngagedExpand>? expandNavs = null,
        IEdmModel? edmModel = null,
        ODataQuerySettings? binderSettings = null,
        Action<string>? onIneligible = null)
    {
        if (!TryBuildProjectionInit<TModel>(
                selectedNames, source, hasParameterlessCtor, structuralByName, logger, expandNavs,
                edmModel, binderSettings, carrierCounted: null,
                out ParameterExpression px, out Expression pinit, out _, out string? reason))
        {
            // #322: the ONE consumer of the reason is the #305 400 below, which used to recite the
            // whole eligibility RULE ("a public parameterless constructor, settable non-complex
            // properties, ...") at a developer whose model satisfied all of it. Reported, never
            // reconstructed at the failure site — a re-derivation there would be a second copy of
            // these checks, free to drift from the ones that actually decided.
            if (reason is not null) onIneligible?.Invoke(reason);
            return query;
        }

        return query.Select(Expression.Lambda<Func<TModel, TModel>>(pinit, px));
    }

    /// <summary>
    /// #334: the shared core of the root member-init projection — the eligibility checks, the
    /// structural member binds and the folded <c>$expand</c> navigation binds — extracted verbatim
    /// from <see cref="TryApplySelectProjection"/> so the count-carrier projection
    /// (<see cref="TryApplyCarrierProjection"/>) reuses every one of them rather than duplicating
    /// the logic. Returns <c>false</c> for exactly the ineligibility cases that used to
    /// <c>return query</c> unchanged.
    /// <para>
    /// <paramref name="carrierCounted"/> names the top-level engaged expands whose
    /// <c>Nav@odata.count</c> is to be carried as an independent scalar subquery. Those navigations
    /// get their nested <c>$skip</c>/<c>$top</c> pushed to SQL (<c>countViaCarrier</c>) instead of
    /// the count bound, and one count expression each is emitted into <paramref name="countExprs"/>,
    /// index-aligned with that list. <c>null</c> (the ordinary path) changes nothing.
    /// </para>
    /// </summary>
    internal static bool TryBuildProjectionInit<TModel>(
        IReadOnlyList<string> selectedNames,
        IEntitySetEndpointSource source,
        bool hasParameterlessCtor,
        IReadOnlyDictionary<string, StructuralPropertyInfo> structuralByName,
        ILogger? logger,
        IReadOnlyList<EngagedExpand>? expandNavs,
        IEdmModel? edmModel,
        ODataQuerySettings? binderSettings,
        IReadOnlyList<EngagedExpand>? carrierCounted,
        out ParameterExpression parameter,
        out Expression entityInit,
        out List<Expression?>? countExprs,
        out string? ineligibilityReason)
    {
        parameter = null!;
        entityInit = null!;
        countExprs = null;
        ineligibilityReason = null;

        if (!hasParameterlessCtor)
        {
            ineligibilityReason =
                $"'{typeof(TModel).Name}' has no public parameterless constructor (a positional record has none)";
            logger?.LogDebug(
                "OhData: $select pushdown skipped for {EntitySet}: {Model} has no public parameterless constructor.",
                source.EntitySetName, typeof(TModel).Name);
            return false;
        }

        // #529 refused this only on the EXPAND path, reasoning that a $select-only projection emits
        // just the selected members, "where no derived property is reachable and the runtime type is
        // unobservable". #628 made that reasoning FALSE: the runtime type is now reported as
        // @odata.type, which JSON Format §4.5.3 requires for a derived instance -- and a member-init
        // can construct nothing but the declared type, so the projection erases the very identity the
        // annotation has to carry. Measured: with the projection engaged, $select=Id on a polymorphic
        // root emitted no annotation at all. The scope is therefore the whole pushdown, not the
        // expand half of it.
        if (edmModel is not null && HasDerivedEntityTypes(edmModel, typeof(TModel)))
        {
            ineligibilityReason =
                $"'{typeof(TModel).Name}' has derived types in the EDM, and a member-init projection " +
                "can only construct the declared type -- every row would come back as " +
                $"'{typeof(TModel).Name}' with the derived types' own properties dropped";
            logger?.LogDebug(
                "OhData: pushdown projection skipped for {EntitySet}: {Model} is polymorphic.",
                source.EntitySetName, typeof(TModel).Name);
            return false;
        }

        // Selected names can include expanded-navigation identifiers (ExtractSelectedProperties
        // keeps them for the JSON trim); those are not structural and are skipped here —
        // expansion loads via delegates correlated by the always-projected key. Nested $select
        // paths ($select=address/city) arrive as their top-level identifier and project the
        // whole member; the JSON trim shapes the nested object.
        var members = new Dictionary<string, StructuralPropertyInfo>(StringComparer.Ordinal);
        foreach (StructuralPropertyInfo selectedProp in selectedNames
            .Where(structuralByName.ContainsKey)
            .Select(name => structuralByName[name]))
        {
            members[selectedProp.Name] = selectedProp;
        }

        foreach (StructuralPropertyInfo structural in structuralByName.Values
            .Where(p => p.IsKey))
        {
            members[structural.Name] = structural;
        }

        if (source.HasETag)
        {
            if (source.ETagPropertyNames is null)
            {
                ineligibilityReason =
                    "its UseETag selector is not a direct property selector, so the properties the " +
                    "ETag is computed from cannot be identified and projected";
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: UseETag selector property names are unknowable (non-direct selector).",
                    source.EntitySetName);
                return false;
            }

            foreach (string name in source.ETagPropertyNames)
            {
                // #253: structuralByName is keyed by the EDM name (which may be a [JsonPropertyName]
                // rename), but UseETag selector names are CLR property names — match on the CLR name.
                StructuralPropertyInfo? etagProp = structuralByName.Values
                    .FirstOrDefault(p => string.Equals(p.Property.Name, name, StringComparison.Ordinal));
                if (etagProp is null)
                {
                    ineligibilityReason =
                        $"the UseETag property '{name}' is not a structural property of " +
                        $"'{typeof(TModel).Name}'";
                    logger?.LogDebug(
                        "OhData: $select pushdown skipped for {EntitySet}: UseETag property '{Property}' is not a structural property.",
                        source.EntitySetName, name);
                    return false;
                }

                members[etagProp.Name] = etagProp;
            }
        }

        foreach (StructuralPropertyInfo member in members.Values)
        {
            // Complex-typed members are a phase-1 boundary: projecting an EF-owned complex
            // property under a TRACKING queryable throws inside EF ("owned entity without a
            // corresponding owner"), turning a working request into a 500. byte[] is classified
            // primitive (s_primitiveClrTypes), so rowversion ETag inputs keep pushdown.
            if (member.IsComplex)
            {
                ineligibilityReason =
                    $"its structural property '{member.Name}' is complex-typed " +
                    $"({member.Property.PropertyType.Name}), and projecting an EF-owned complex " +
                    "property under a tracking query is not supported";
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: '{Property}' is complex-typed (owned-entity projection is a phase-1 boundary).",
                    source.EntitySetName, member.Name);
                return false;
            }

            if (member.Property.SetMethod is not { IsPublic: true })
            {
                ineligibilityReason =
                    $"its structural property '{member.Name}' has no public setter";
                logger?.LogDebug(
                    "OhData: $select pushdown skipped for {EntitySet}: '{Property}' has no public setter.",
                    source.EntitySetName, member.Name);
                return false;
            }
        }

        ParameterExpression x = Expression.Parameter(typeof(TModel), "x");
        var bindings = members.Values
            .Select(m => (MemberBinding)Expression.Bind(m.Property, Expression.Property(x, m.Property)))
            .ToList();

        // #206 phase 2: fold each pushed $expand navigation into the same member-init so the LINQ
        // provider loads the related rows as part of this one query (EF Core translates a collection
        // navigation projected with .ToList() into a JOIN, and a single-valued navigation into an
        // outer join). Nested $filter/$orderby/$top/$skip become a filtered/ordered/paged Include via
        // BuildShapedNavAccess. Eligibility of each binding — settable property, non-cyclic related
        // type, List-assignable collection — was decided at startup in BuildExpandNavBinding.
        if (expandNavs is { Count: > 0 })
        {
            try
            {
                foreach (EngagedExpand nav in expandNavs)
                {
                    // #334: is this one of the navs whose count the carrier supplies as an
                    // independent scalar subquery? Matched on the CLR PropertyInfo, which is the
                    // identity BuildExpandNavBinding assigns once at startup.
                    int carrierIndex = -1;
                    if (carrierCounted is not null)
                    {
                        for (int ci = 0; ci < carrierCounted.Count; ci++)
                        {
                            if (ReferenceEquals(carrierCounted[ci].Binding.Property, nav.Binding.Property))
                            {
                                carrierIndex = ci;
                                break;
                            }
                        }
                    }

                    // #254: the ROOT entity set's resolved MaxExpandTop governs at every depth (the
                    // same rule MaxExpansionDepth follows), so it is read from `source` once here.
                    Expression access = BuildShapedNavAccess(
                        x, nav, (IEdmModel)edmModel!, (ODataQuerySettings)binderSettings!, source.MaxExpandTop,
                        countViaCarrier: carrierIndex >= 0);
                    bindings.Add(Expression.Bind(nav.Binding.Property, access));

                    if (carrierIndex >= 0)
                    {
                        // #334: the count is a SECOND, INDEPENDENT expression rooted at the same
                        // navigation-access node — filtered but never ordered or windowed — mirroring
                        // the CreateTotalCountExpression / ProjectAsWrapper split
                        // Microsoft.AspNetCore.OData's SelectExpandBinder.BuildExpandedProperty makes.
                        // Because neither chain reads the other, $count=true no longer perturbs the
                        // $top translation.
                        (countExprs ??= new List<Expression?>(new Expression?[carrierCounted!.Count]))
                            [carrierIndex] = BuildNavCountExpression(
                                x, nav, (IEdmModel)edmModel!, (ODataQuerySettings)binderSettings!);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or Microsoft.OData.ODataException)
            {
                // FAIL LOUD (owner directive): a nested expand option MS's binder cannot translate
                // must not degrade the response to EDM-only under a 200 -- that is #298/#300's class
                // of bug. Message stays generic per S7; logged at Debug so the operator sees which
                // clause tripped it.
                //
                // The type list is narrow so a genuine framework bug (an NRE from a binder defect)
                // surfaces as a 500 rather than a 400 "your query is bad".
                //
                // #494 deliberately left this one alone: it classifies by exception TYPE, which was
                // wrong at the execution-time catches because those wrap a call that touches the
                // database. This one is pure in-memory expression construction -- all translation, no
                // materialization phase to separate it from.
                logger?.LogDebug(ex,
                    "OhData: $expand pushdown failed for {EntitySet}: a nested expand option could not be bound.",
                    source.EntitySetName);
                throw new Microsoft.OData.ODataException(
                    $"The '$expand' on '{source.EntitySetName}' could not be processed: a nested expand " +
                    "option could not be translated. Simplify the nested $filter/$orderby, or write an " +
                    "expand delegate for this navigation to take full control of its query shape.");
            }
        }

        parameter = x;
        entityInit = Expression.MemberInit(Expression.New(typeof(TModel)), bindings);
        return true;
    }

    /// <summary>
    /// #334: the count leg — <c>owner.Nav[.Where(f)].Count()</c>. Rooted at the SAME
    /// navigation-access node the windowed page is built from, but with NO OrderBy/Skip/Take, so it
    /// translates as a plain correlated scalar aggregate rather than a collection projected out of a
    /// windowed set (the APPLY/LATERAL shape #298/#300/#304 established SQLite cannot translate).
    /// <para>
    /// The nested <c>$filter</c> rides into the count — and only the <c>$filter</c> — because OData
    /// §11.2.4.2 defines <c>Nav@odata.count</c> as the size of the FULL filtered collection, not of
    /// the returned page.
    /// </para>
    /// </summary>
    private static Expression BuildNavCountExpression(
        Expression owner, EngagedExpand engaged, IEdmModel model, ODataQuerySettings binderSettings)
    {
        Type elem = engaged.Binding.ElementType;
        Expression access = Expression.Property(owner, engaged.Binding.Property);
        NavShapeBindings bound = BindNavShape(engaged.Filter, engaged.OrderBy, elem, model, binderSettings);
        if (bound.Predicate is not null)
            access = Expression.Call(_enumerableWhere.MakeGenericMethod(elem), access, bound.Predicate);
        return Expression.Call(_enumerableCount.MakeGenericMethod(elem), access);
    }

    /// <summary>
    /// #334: the count-carrier projection —
    /// <c>new ExpandCountCarrier&lt;TModel&gt; { Entity = new TModel { … }, C0 = x.Nav.Where(f).Count(), … }</c>
    /// — so ONE query returns both the SQL-windowed page and an exact per-parent
    /// <c>Nav@odata.count</c>. Returns <c>null</c> when the root projection is ineligible (the same
    /// conditions <see cref="TryApplySelectProjection"/> bails on) or when a count expression could
    /// not be produced for every requested nav, in which case the caller falls back to the ordinary
    /// projection and the count-defers-paging behaviour of #254/#298/#304.
    /// </summary>
    internal static IQueryable<ExpandCountCarrier<TModel>>? TryApplyCarrierProjection<TModel>(
        IQueryable<TModel> query,
        IReadOnlyList<string> selectedNames,
        IEntitySetEndpointSource source,
        bool hasParameterlessCtor,
        IReadOnlyDictionary<string, StructuralPropertyInfo> structuralByName,
        ILogger? logger,
        IReadOnlyList<EngagedExpand> expandNavs,
        IEdmModel edmModel,
        ODataQuerySettings binderSettings,
        IReadOnlyList<EngagedExpand> carrierCounted)
    {
        if (!TryBuildProjectionInit<TModel>(
                selectedNames, source, hasParameterlessCtor, structuralByName, logger, expandNavs,
                edmModel, binderSettings, carrierCounted,
                out ParameterExpression x, out Expression entityInit, out List<Expression?>? countExprs,
                out _))
        {
            // No reason is surfaced here: a null return falls through to the ordinary projection
            // below, which re-runs the same checks and reports the reason to ITS caller.
            return null;
        }

        if (countExprs is null || countExprs.Count != carrierCounted.Count ||
            countExprs.Any(static c => c is null))
        {
            return null;
        }

        Type carrierType = typeof(ExpandCountCarrier<TModel>);
        var carrierBindings = new List<MemberBinding>(countExprs.Count + 1)
        {
            Expression.Bind(carrierType.GetProperty(nameof(ExpandCountCarrier<TModel>.Entity))!, entityInit),
        };
        for (int i = 0; i < countExprs.Count; i++)
            carrierBindings.Add(Expression.Bind(carrierType.GetProperty($"C{i}")!, countExprs[i]!));

        return query.Select(Expression.Lambda<Func<TModel, ExpandCountCarrier<TModel>>>(
            Expression.MemberInit(Expression.New(carrierType), carrierBindings), x));
    }

    /// <summary>
    /// #334: the projection slot for a nested <c>$count</c>. The root projection normally emits
    /// <c>new TModel { … }</c>, which has nowhere to put a count scalar — that absence is precisely
    /// why <c>$count=true</c> used to have to suppress the <c>$top</c> SQL bound and count the
    /// materialized array instead. (Microsoft.AspNetCore.OData has the slot already: its
    /// <c>SelectExpandWrapper</c>'s <c>PropertyContainer</c> carries <c>Collection</c> and
    /// <c>TotalCount</c> side by side.)
    /// <para>
    /// FIXED SLOTS, not an array or a List: a member-init of settable scalar members is what every
    /// LINQ provider can translate. <see cref="ExpandCountCarrierSlots"/> covers any realistic
    /// number of counted, windowed, top-level navigations in one request; a request that exceeds it
    /// simply falls back to the pre-#334 path rather than failing.
    /// </para>
    /// <para>
    /// The carrier NEVER reaches the serializer: the collection route unwraps it to
    /// <c>TModel[]</c> immediately after <c>ToArray()</c>, so nothing in the JSON shaping pipeline
    /// (SerializeBounded / SerializeBoundedCollection / SpliceKeptNavigations /
    /// OmitUnexpandedNavigations / StripToSelectedProperties) ever sees a wrapper type.
    /// </para>
    /// </summary>
    internal sealed class ExpandCountCarrier<T>
    {
        public T Entity { get; set; } = default!;
        public int C0 { get; set; }
        public int C1 { get; set; }
        public int C2 { get; set; }
        public int C3 { get; set; }
        public int C4 { get; set; }
        public int C5 { get; set; }
        public int C6 { get; set; }
        public int C7 { get; set; }

        public int Slot(int i) => i switch
        {
            0 => C0,
            1 => C1,
            2 => C2,
            3 => C3,
            4 => C4,
            5 => C5,
            6 => C6,
            7 => C7,
            _ => throw new ArgumentOutOfRangeException(nameof(i)),
        };
    }

    /// <summary>#334: how many counted navs one carrier projection can hold. See <see cref="ExpandCountCarrier{T}"/>.</summary>
    internal const int ExpandCountCarrierSlots = 8;

    // #206 phase 2 (Option A1): a navigation the $expand pushdown folds into the collection
    // projection — the CLR property to bind, whether it is a collection (materialized with
    // .ToList() so EF Core emits the JOIN) or a single-valued reference, and the related element
    // type. Built once at startup for each DELEGATE-LESS navigation that survives the safety
    // checks (see BuildExpandNavBinding); delegate-backed navigations never appear here.
    internal readonly record struct ExpandNavBinding(PropertyInfo Property, bool IsCollection, Type ElementType);

    // #206 phase 2: cached open generic Enumerable.ToList<T>, closed per collection-navigation binding.
    private static readonly MethodInfo _enumerableToList =
        typeof(Enumerable).GetMethod(nameof(Enumerable.ToList), BindingFlags.Public | BindingFlags.Static)!;

    // #206 phase 2 (optioned expand): cached open-generic Enumerable operators used to fold a
    // filtered / ordered / paged Include into the collection projection. The nested $filter/$orderby/
    // $top/$skip of a $expand are pushed to SQL by composing these onto the navigation access
    // (x.Nav.Where(f).OrderBy(o).Skip(s).Take(t).ToList()); EF Core translates the result to a single
    // JOIN with a ROW_NUMBER window for paging. The Where/OrderBy predicates are produced by
    // Microsoft's own OData binders (FilterBinder/OrderByBinder), never a hand-rolled translator.
    private static readonly MethodInfo _enumerableCast = typeof(Enumerable)
        .GetMethod(nameof(Enumerable.Cast), new[] { typeof(System.Collections.IEnumerable) })!;

    private static readonly MethodInfo _enumerableWhere = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Where) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);
    private static readonly MethodInfo _enumerableOrderBy = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.OrderBy) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableOrderByDescending = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.OrderByDescending) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableThenBy = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.ThenBy) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableThenByDescending = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.ThenByDescending) && m.GetParameters().Length == 2);
    private static readonly MethodInfo _enumerableSkip = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Skip) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(int));
    private static readonly MethodInfo _enumerableTake = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Take) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(int));
    // #206 phase 2 (multi-level expand): Enumerable.Select<TSource,TResult>(source, selector) — the
    // element-wise projection folded into a JOIN'd collection when a nested $expand (or $levels)
    // recurses one level deeper. EF Core translates a collection navigation projected element-wise
    // with .ToList() into a ThenInclude-style JOIN, so the whole delegate-less chain loads in one query.
    private static readonly MethodInfo _enumerableSelect = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Select) && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

    // #334: Enumerable.Count<T>(IEnumerable<T>) — the PARAMETERLESS overload. Composed on the
    // filtered but UN-WINDOWED navigation access to obtain an exact Nav@odata.count as a correlated
    // scalar aggregate, independent of the Skip/Take window composed on the same nav for the page.
    private static readonly MethodInfo _enumerableCount = typeof(Enumerable).GetMethods()
        .First(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1);

    // #206 phase 2 (optioned expand): the OData filter/orderby binders are stateless — all per-bind
    // state flows through the QueryBinderContext argument — so a single shared instance is reused
    // across requests (matching this file's cache-the-reflection-machinery ethos).
    private static readonly FilterBinder _filterBinder = new();
    private static readonly OrderByBinder _orderByBinder = new();

    // #206 phase 2 (optioned + multi-level expand): one delegate-less navigation the request
    // $expand'd, resolved for pushdown. Carries the startup binding plus the request's parsed nested
    // clauses. Filter/OrderBy/Skip/Top are pushed to SQL via BuildShapedNavAccess; Count and
    // NestedSelect are applied afterward on the serialized JSON (ShapePushedExpandsInJson) so the
    // wire stays a plain POCO in the configured naming policy (PascalCase by default) — no
    // SelectExpandWrapper ever reaches the serializer. When
    // Count is requested, Skip/Top are DEFERRED to the JSON pass instead of SQL so the emitted
    // Nav@odata.count reflects the full filtered collection (OData §11.2.4.2), not the page.
    // <para>#206 (recursion): <c>Children</c> holds each pushed nested $expand one level deeper —
    // folded into the same JOIN'd query as an element-wise projection (EF ThenInclude). A branch is
    // only recorded here when it is delegate-less AND pushable AT EVERY level; a delegate-backed (or
    // otherwise non-pushable) nested nav defers the whole parent off pushdown (see
    // TryBuildEngagedExpand), so a pushed branch can never EF-include a delegate navigation — the
    // delegate-safety invariant holds at any depth by construction. <c>Levels</c> (&gt; 0) marks a
    // <c>$levels=N</c> self-referential expand recursed N deep against the same <c>Binding</c>;
    // <c>Children</c> is then null (the recursion re-uses this binding), while
    // Filter/OrderBy/Skip/Top/Count/NestedSelect — when present (#254) — apply at EVERY level.</para>
    internal readonly record struct EngagedExpand(
        ExpandNavBinding Binding,
        FilterClause? Filter,
        OrderByClause? OrderBy,
        int? Skip,
        int? Top,
        bool Count,
        List<string>? NestedSelect,
        IReadOnlyList<EngagedExpand>? Children,
        int Levels);

    // #206 phase 2 (optioned + multi-level expand): resolve a $expand item that targets a
    // delegate-less, pushdown-eligible navigation into an EngagedExpand (recursing into its own
    // nested $expand), or return false to DEFER the whole branch off the pushdown path (it then stays
    // EDM-only for the request, exactly as before). Deferred cases: $search/$compute/$apply
    // (unsupported inside a pushed expand); a nested $expand whose child is delegate-backed, cyclic,
    // or a non-member-init-projectable type (the parent is deferred wholesale so a pushed branch is
    // delegate-less AND projectable end-to-end); an intermediate level whose element type cannot be
    // member-init-projected. The nested options $filter/$orderby/$top/$skip/$count/$select are honored
    // at every level. $levels is handled by the caller via BuildLevelsNavBinding; #254 lets a $levels
    // item carry $filter/$orderby/$skip/$top/$count/$select (applied at every level of the recursion),
    // while a $levels item carrying its own nested $expand is still deferred.
    // <paramref name="remainingDepth"/> is the resolved MaxExpansionDepth budget for the whole chain
    // (Microsoft's SelectExpandQueryValidator already 400s a request nesting deeper, so this is a
    // belt-and-suspenders cap that never partially applies a too-deep graph).
    internal static bool TryBuildEngagedExpand(
        ExpandedNavigationSelectItem item, ExpandNavBinding binding, IEdmModel model,
        OhDataRegistration registration, int remainingDepth, out EngagedExpand engaged)
    {
        engaged = default;
        if (remainingDepth < 1) return false;
        if (item.SearchOption is not null || item.ComputeOption is not null || item.ApplyOption is not null)
        {
            return false; // $search/$compute/$apply inside an expand — not implemented on the pushdown path
        }

        // $levels: bounded self-referential recursion over the binding BuildLevelsNavBinding
        // resolved. #254: the recursion carries the item's other nested options at every level, as
        // ODL's own ProcessLevels does. Still deferred: a $levels item carrying its own nested
        // $expand, where depth accounting against MaxExpansionDepth is ambiguous.
        //
        // #293 micro-decision (A), FROZEN: this branch never calls ResolveNavTreatment -- it recurses
        // the SAME already-resolved binding at every level, so $levels resolves entirely from the
        // URL-named set and serves raw even when another set exposes the type with disagreeing
        // config. The explicit nested form re-resolves per level and blanks on disagreement.
        //
        // #318: a non-ServeRaw child defers the WHOLE PARENT branch off pushdown, so the parent comes
        // back empty too -- measured, `$expand=Children($expand=Children)` loses BOTH levels while
        // `$levels=2` serves both. Do NOT "fix" the asymmetry by making $levels blank; the frozen
        // spec lists the $levels suite under tests that stay green.
        if (item.LevelsOption is not null)
        {
            SelectExpandClause? lc = item.SelectAndExpand;
            if (lc is not null && lc.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any())
                return false; // $levels + nested $expand — deferred

            int levels = ResolveLevelsBudget(
                item.LevelsOption.IsMaxLevel, item.LevelsOption.Level, remainingDepth, MaxNestedExpandDepth);
            if (levels < 1) return false;
            if (!IsMemberInitProjectable(binding.ElementType, model)) return false;

            int? levelsSkip = item.SkipOption is long lsk ? (int)Math.Min(lsk, int.MaxValue) : null;
            int? levelsTop = item.TopOption is long ltp ? (int)Math.Min(ltp, int.MaxValue) : null;
            List<string>? levelsSelect = lc is not null ? ExtractSelectedProperties((SelectExpandClause)lc) : null;
            if (levelsSelect is not null)
            {
                // THE $levels + $select TRAP: the recursion is IMPLICIT in ODL — the nested clause of a
                // $levels item holds NO ExpandedNavigationSelectItem for the self-navigation — so
                // ExtractSelectedProperties never sees it and the strip would delete the self-nav key
                // (and its Nav@odata.count) at every level. Append the self-nav's EDM name so
                // StripToSelectedProperties/KeepUnderSelect keep both. Resolved through
                // ODataPropertyNaming.ResolveEdmName (never the raw CLR name) so a [JsonPropertyName]-
                // renamed self-navigation keeps working — the same call CollectPushedLevelsNavNames uses.
                string selfNavEdmName = ODataPropertyNaming.ResolveEdmName(binding.Property);
                if (!levelsSelect.Contains(selfNavEdmName, StringComparer.OrdinalIgnoreCase))
                    levelsSelect.Add(selfNavEdmName);
            }

            engaged = new EngagedExpand(
                binding, item.FilterOption, item.OrderByOption, levelsSkip, levelsTop,
                item.CountOption == true, levelsSelect, Children: null, Levels: levels);
            return true;
        }

        SelectExpandClause? nested = item.SelectAndExpand;
        List<EngagedExpand>? children = null;
        var childItems = nested?.SelectedItems.OfType<ExpandedNavigationSelectItem>().ToList();
        if (childItems is { Count: > 0 })
        {
            // An intermediate level (one with its own nested $expand) is projected element-wise into a
            // fresh member-init so the deeper navigations fold in. That requires the element type to be
            // member-init-projectable; otherwise defer the whole branch (stays EDM-only, never a 500).
            if (remainingDepth < 2) return false;
            if (!IsMemberInitProjectable(binding.ElementType, model)) return false;

            // Model B pushdown gate (FROZEN spec, issue #293): resolve the candidate set for this
            // element CLR type — every startup profile whose entity set's EDM type is EXACTLY this
            // type (ResolveProfilesForClrType; never CLR-type assignability, never registration
            // order) — and use the SAME ResolveNavTreatment dispatch the delegate expansion path
            // uses (ExpandLevelAsync) to decide each nested nav's treatment. A nav only stays
            // EF-includable (pushed down) when its treatment is ServeRaw (DB(nav) = ∅ over the
            // candidate set); RunDelegate and Blank both defer the WHOLE parent branch off pushdown
            // so it resolves via the delegate expansion path instead, which alone knows how to invoke
            // the sole delegate or write the blanked value. Computing the gate's candidate set with
            // the exact same helper the delegate path uses is what guarantees the two can never
            // diverge on the same navigation.
            IReadOnlyList<IEntitySetEndpointSource> childCandidates =
                ResolveProfilesForClrType(binding.ElementType, model, registration);

            foreach (ExpandedNavigationSelectItem childItem in childItems)
            {
                string childNavName = childItem.PathToNavigationProperty.FirstSegment.Identifier;
                if (ResolveNavTreatment(childNavName, childCandidates).Treatment != NavTreatment.ServeRaw)
                    return false; // RunDelegate or Blank — defer whole branch (never EF-included)

                if (BuildExpandNavBinding(binding.ElementType, childNavName, model) is not { } childBinding)
                    return false; // cyclic / non-projectable nested nav — defer whole branch

                if (!TryBuildEngagedExpand(childItem, childBinding, model, registration, remainingDepth - 1, out EngagedExpand childEngaged))
                    return false; // deeper level not pushable — defer whole branch

                (children ??= new List<EngagedExpand>()).Add(childEngaged);
            }
        }

        // Filter/OrderBy/Top/Skip/Count are only valid on a collection-valued expand; the OData parser
        // rejects them on a single-valued reference, so they arrive null there and this stays a bare
        // single-valued include (BuildShapedNavAccess returns x.Ref unchanged) carrying only $select.
        int? skip = item.SkipOption is long s ? (int)Math.Min(s, int.MaxValue) : null;
        int? top = item.TopOption is long t ? (int)Math.Min(t, int.MaxValue) : null;
        List<string>? nestedSelect = nested is not null ? ExtractSelectedProperties((SelectExpandClause)nested) : null;

        engaged = new EngagedExpand(
            binding, item.FilterOption, item.OrderByOption, skip, top, item.CountOption == true, nestedSelect,
            children, Levels: 0);
        return true;
    }

    // #206 phase 2 (multi-level expand): true when an element type can be projected into a fresh
    // member-init at an INTERMEDIATE expand level (i.e. one that folds deeper navigations). Requires a
    // public parameterless constructor and every scalar structural property (per the EDM) to be a
    // public-settable CLR property that is not complex-typed — projecting an EF-owned complex property
    // under a tracking queryable throws (the same phase-1 boundary TryApplySelectProjection guards). A
    // type that fails this defers its parent branch off pushdown (stays EDM-only), never a 500.
    //
    // #323 fold-in (review): memoized — as of #323 (Change A/B) this runs per request, per engaged
    // expand, per level, and does GetConstructor + FindDeclaredType + per-property reflection each time,
    // for a result that is invariant for a given (elementType, model) pair for the lifetime of that EDM
    // model. Cached per this file's existing static-cache convention (e.g. s_efIncludeMethodCache);
    // reference equality on IEdmModel is correct here since the same model instance is reused for every
    // request within one registration, and different registrations never share a model instance.
    private static readonly ConcurrentDictionary<(Type ElementType, IEdmModel Model), bool>
        s_memberInitProjectableCache = new();

    // #529: does the EDM declare a type deriving from this one (a TPH hierarchy)? Cached on the same
    // (type, model) key and for the same reason as s_memberInitProjectableCache above.
    private static readonly ConcurrentDictionary<(Type ElementType, IEdmModel Model), bool>
        s_hasDerivedEntityTypesCache = new();

    // #529: a member-init projection can only construct the DECLARED type, so on a TPH hierarchy every
    // row materializes as the base and the derived types' own properties are dropped -- silently, under
    // a 200. The projection is therefore refused for a polymorphic root and the request falls to the
    // Include path (#305 Path A), which loads real entities and so preserves each row's runtime type.
    // Walks BaseType rather than calling an Edm extension so the net8.0 and net10.0 builds agree.
    private static bool HasDerivedEntityTypes(IEdmModel model, Type clrType) =>
        s_hasDerivedEntityTypesCache.GetOrAdd((clrType, model), static key =>
        {
            (Type clrType, IEdmModel model) = key;
            // #508: EdmClrTypeMap, never model.FindDeclaredType(clrType.FullName) -- the latter answers
            // null for every type on a renamed schema, which would silently disable this check.
            if (EdmClrTypeMap.FindEntityType(model, clrType) is not { } edmType) return false;

            foreach (IEdmStructuredType candidate in model.SchemaElements
                         .OfType<IEdmStructuredType>()
                         .Where(t => !ReferenceEquals(t, edmType)))
            {
                for (IEdmStructuredType? b = candidate.BaseType; b is not null; b = b.BaseType)
                {
                    if (ReferenceEquals(b, edmType)) return true;
                }
            }
            return false;
        });

    private static bool IsMemberInitProjectable(Type elementType, IEdmModel model) =>
        s_memberInitProjectableCache.GetOrAdd((elementType, model), static key =>
        {
            (Type elementType, IEdmModel model) = key;
            if (elementType.GetConstructor(Type.EmptyTypes) is null) return false;
            // #508: EdmClrTypeMap, not model.FindDeclaredType(elementType.FullName) — the latter
            // matches on the EDM type's full name, so on a renamed schema it answered null for every
            // element type, this method answered false for every element type, and TryBuildEngagedExpand
            // deferred EVERY $expand branch off pushdown. See EdmClrTypeMap.
            if (EdmClrTypeMap.FindEntityType(model, elementType) is not { } edmType)
            {
                return false;
            }

            foreach (IEdmStructuralProperty sp in edmType.StructuralProperties())
            {
                if (sp.Type.Definition is IEdmComplexType) return false; // owned-entity projection boundary
                // #253: sp.Name is the EDM name, which may be a [JsonPropertyName] rename — resolve back
                // to the CLR property by EDM name (falls back to a plain CLR-name match for un-renamed).
                PropertyInfo? clrProp = ODataPropertyNaming.FindClrPropertyByEdmName(elementType, sp.Name);
                if (clrProp is null || clrProp.SetMethod is not { IsPublic: true }) return false;
            }
            return true;
        });

    // #206 phase 2 (multi-level expand): the public-settable, non-complex scalar structural CLR
    // properties of <paramref name="elementType"/> (per the EDM), bound as <c>n.Prop</c> into an
    // intermediate level's fresh member-init. Callers gate on IsMemberInitProjectable first, so every
    // returned property is guaranteed settable and present.
    private static IEnumerable<PropertyInfo> ScalarStructuralClrProps(Type elementType, IEdmModel model)
    {
        // #508: EdmClrTypeMap, not model.FindDeclaredType(elementType.FullName). Callers gate on
        // IsMemberInitProjectable, which resolves through the same lookup — so the two must never be
        // able to disagree about which EDM type backs the element type.
        if (EdmClrTypeMap.FindEntityType(model, elementType) is not { } edmType)
            yield break;
        foreach (IEdmStructuralProperty sp in edmType.StructuralProperties()
            .Where(sp => sp.Type.Definition is not IEdmComplexType))
        {
            // #253: sp.Name is the EDM name (possibly a [JsonPropertyName] rename) — resolve to CLR.
            PropertyInfo? clrProp = ODataPropertyNaming.FindClrPropertyByEdmName(elementType, sp.Name);
            if (clrProp is { SetMethod.IsPublic: true }) yield return clrProp;
        }
    }

    // #206 phase 2 (optioned + multi-level expand): build the navigation access expression folded into
    // the collection projection for one engaged expand, relative to <paramref name="owner"/> (the
    // top-level query parameter, or a deeper element parameter when recursing). For a collection nav
    // this is owner.Nav.Where(filter).OrderBy/ThenBy(key…).Skip(s).Take(t)[.Select(memberInit)].ToList()
    // — each stage present only when the request carried it, and the .Select present only when a nested
    // $expand folds deeper navigations (EF ThenInclude). The Where/OrderBy lambdas come from Microsoft's
    // FilterBinder/OrderByBinder (bound against the nav element type), so nested $filter/$orderby
    // translate with the exact OData semantics the top-level collection path uses — no bespoke
    // OData→LINQ translator. Skip/Take are omitted here when $count is requested (the JSON pass pages
    // after counting). A single-valued reference has no collection operators; it is projected into a
    // null-guarded member-init whenever its element type is member-init-projectable (#323) — LEAF
    // expands included, not only ones with deeper nested navigations — and returned unchanged (bare)
    // only when the element type is not projectable. A $levels expand delegates to BuildLevelsNavAccess
    // (bounded self-referential recursion). Runs inside the caller's try/catch: a binder that cannot
    // bind a clause throws, and the caller then abandons pushdown for the request (the nav stays
    // EDM-only) rather than surfacing a 500.
    // #334: <paramref name="countViaCarrier"/> is set by TryBuildProjectionInit for the top-level
    // counted navs whose Nav@odata.count the carrier projection supplies as an independent scalar
    // subquery. It only ever reaches this level's ApplyNavShape — never a nested BuildMemberInit
    // call below — so a counted nav at depth >= 2 keeps the pre-#334 path by construction.
    private static Expression BuildShapedNavAccess(
        Expression owner, EngagedExpand engaged, IEdmModel model, ODataQuerySettings binderSettings,
        int? maxExpandTop, bool countViaCarrier = false)
    {
        ExpandNavBinding nav = engaged.Binding;

        if (engaged.Levels > 0)
        {
            // #254 (item 2): a $levels expand may now carry $filter/$orderby/$skip/$top/$count. Bind
            // the Where/OrderBy lambdas ONCE here and reuse them at every level of the recursion — the
            // nav element type is invariant under $levels (BuildLevelsNavBinding requires
            // elementType == ownerType) and expression trees are immutable, so re-binding per level
            // would allocate identical nodes for no benefit.
            NavShapeBindings levelsBound = BindNavShape(engaged.Filter, engaged.OrderBy, nav.ElementType, model, binderSettings);
            return BuildLevelsNavAccess(owner, engaged, engaged.Levels, model, levelsBound);
        }

        Expression access = Expression.Property(owner, nav.Property);
        Type elem = nav.ElementType;

        if (!nav.IsCollection)
        {
            // Single-valued reference. #323 (Change A): project it into a fresh member-init
            // (null-guarded so a missing reference stays null) whenever the element type is
            // member-init-projectable — a LEAF expand (no nested children) included, not only one
            // that carries deeper pushed navigations. BuildMemberInit handles an empty child list.
            // Materializing every leaf through a fresh POCO rather than the bare EF-tracked entity is
            // what makes a serialization cycle structurally impossible on this path (no entity with a
            // live back-reference navigation is ever handed to the serializer). When the element type
            // is NOT projectable, fall back to the bare reference (EF outer join loads the full
            // related entity) exactly as before.
            if (IsMemberInitProjectable(elem, model))
            {
                Expression init = BuildMemberInit(
                    access, elem, engaged.Children ?? Array.Empty<EngagedExpand>(), model, binderSettings,
                    maxExpandTop);
                return Expression.Condition(
                    Expression.Equal(access, Expression.Constant(null, elem)),
                    Expression.Constant(null, elem), init);
            }
            return access;
        }

        access = ApplyNavShape(
            access, engaged, elem, model, BindNavShape(engaged.Filter, engaged.OrderBy, elem, model, binderSettings), maxExpandTop,
            countViaCarrier: countViaCarrier);

        // #323 (Change A): fold EVERY element-wise projection — leaf or intermediate — into the query
        // whenever the element type is member-init-projectable, not only when a nested $expand folds
        // deeper delegate-less navigations in (EF ThenInclude). Same structural-cycle-impossibility
        // rationale as the single-valued branch above: a leaf collection is now a List<T> of fresh
        // POCOs, never the bare EF-tracked related entities, so a bidirectional back-reference can no
        // longer close a parent<->child object cycle for System.Text.Json. BuildMemberInit handles an
        // empty child list. When the element type is NOT projectable, this stays the bare
        // .ToList() of full related entities (all columns materialized) exactly as before.
        if (IsMemberInitProjectable(elem, model))
        {
            ParameterExpression n = Expression.Parameter(elem, "n");
            LambdaExpression proj = Expression.Lambda(
                BuildMemberInit(
                    n, elem, engaged.Children ?? Array.Empty<EngagedExpand>(), model, binderSettings,
                    maxExpandTop),
                n);
            access = Expression.Call(_enumerableSelect.MakeGenericMethod(elem, elem), access, proj);
        }

        return Expression.Call(_enumerableToList.MakeGenericMethod(elem), access);
    }

    // #254 (item 2): the OData-bound lambdas for one engaged expand's nested $filter/$orderby, split
    // out of the shaping step (BindNavShape → ApplyNavShape) so the $levels recursion can bind ONCE
    // and apply at every level. Null members mean "the request carried no such clause".
    internal readonly record struct NavShapeBindings(
        LambdaExpression? Predicate,
        IReadOnlyList<(LambdaExpression Key, bool Descending)>? OrderBy);

    // #206 phase 2 (optioned expand) / #254: bind a collection expand's nested $filter/$orderby with
    // Microsoft's own FilterBinder/OrderByBinder. A fresh QueryBinderContext per bind: it holds the
    // binder's `$it` lambda parameter and other per-clause state, so filter and orderby each get their
    // own rather than sharing one. Throws (via the binders) on a clause that cannot be bound — the
    // caller's try/catch then abandons pushdown for the request.
    //
    // #650 takes the clauses directly rather than an EngagedExpand, so the DELEGATE-backed path can
    // bind the very same way. An EngagedExpand is a pushdown concept — it carries an ExpandNavBinding
    // resolved against a navigation that engaged the projection — and a delegate-backed navigation has
    // none by definition. Synthesising a hollow one to satisfy the signature would have made the two
    // paths look related where they are not; the two clauses are all this ever read.
    private static NavShapeBindings BindNavShape(
        FilterClause? filter, OrderByClause? orderByClause, Type elem, IEdmModel model,
        ODataQuerySettings binderSettings)
    {
        LambdaExpression? predicate = null;
        if (filter is not null)
        {
            var ctx = new QueryBinderContext(model, binderSettings, elem);
            predicate = (LambdaExpression)_filterBinder.BindFilter(filter, ctx);
        }

        List<(LambdaExpression, bool)>? orderBy = null;
        if (orderByClause is not null)
        {
            var ctx = new QueryBinderContext(model, binderSettings, elem);
            OrderByBinderResult? result = _orderByBinder.BindOrderBy(orderByClause, ctx);
            for (OrderByBinderResult? cur = result; cur is not null; cur = cur.ThenBy)
            {
                orderBy ??= new List<(LambdaExpression, bool)>();
                orderBy.Add(((LambdaExpression)cur.OrderByExpression, cur.Direction == OrderByDirection.Descending));
            }
        }

        return new NavShapeBindings(predicate, orderBy);
    }

    // #206 phase 2 (optioned expand) / #254: compose the already-bound nested $filter/$orderby plus the
    // nested $skip/$top (and the #254 MaxExpandTop count bound) onto the navigation-access expression,
    // returning the shaped (un-materialized) IEnumerable. Pure expression assembly — no binding — so
    // the $levels recursion can call it once per level with the same NavShapeBindings.
    //
    // <paramref name="deferPagingToJson"/> (#300): true ONLY for the $levels recursion (set by its sole
    // caller, BuildLevelsNavAccess). Inside that recursion every level BOTH windows a collection AND
    // projects a further (self-referential) collection out of it — the same "double collection on one
    // level" shape #298 hit for $count — which requires SQL APPLY/LATERAL that not every provider (SQLite
    // among them) translates. When true, no SQL Skip/Take (nor the #298 count-bound Take below) is
    // composed at all; the caller windows in the JSON pass instead (ShapeLevelsInJson), exactly like the
    // count bound already deferred there via <paramref name="maxExpandTop"/> being null on that call.
    //
    // <paramref name="countViaCarrier"/> (#334): true for a top-level, projection-LEAF, non-$levels
    // collection expand that carries BOTH $count and a nested $skip/$top window, and whose exact
    // count the caller is obtaining as an independent correlated scalar subquery (see
    // BuildNavCountExpression / ExpandCountCarrier). Because the count no longer rides on the
    // materialized array's length, this level no longer has to fetch the whole filtered collection
    // to count it: $skip/$top compose to SQL exactly as they do without $count.
    internal static Expression ApplyNavShape(
        Expression access, EngagedExpand engaged, Type elem, IEdmModel model,
        in NavShapeBindings bound, int? maxExpandTop, bool deferPagingToJson = false,
        bool countViaCarrier = false)
    {
        if (bound.Predicate is not null)
            access = Expression.Call(_enumerableWhere.MakeGenericMethod(elem), access, bound.Predicate);

        if (bound.OrderBy is { Count: > 0 })
        {
            bool first = true;
            foreach ((LambdaExpression keySelector, bool descending) in bound.OrderBy)
            {
                MethodInfo op = (first, descending) switch
                {
                    (true, false) => _enumerableOrderBy,
                    (true, true) => _enumerableOrderByDescending,
                    (false, false) => _enumerableThenBy,
                    (false, true) => _enumerableThenByDescending,
                };
                access = Expression.Call(op.MakeGenericMethod(elem, keySelector.ReturnType), access, keySelector);
                first = false;
            }
        }

        // #298 fix: the SQL Take(cap+1) count bound is only safe to compose when this level is a
        // projection LEAF (no nested $expand children of its own). A level WITH children is further
        // projected element-wise a few lines below in BuildShapedNavAccess (the .Select(...) that folds
        // the deeper navigation) — windowing THIS level's collection and ALSO projecting a further
        // collection out of each of its elements in the same query is the "double collection" shape that
        // requires SQL APPLY/LATERAL, which not every provider translates (SQLite among them); composing
        // the bound there risked exactly the untranslatable shape #298 reported. For a level with
        // children the ceiling is still enforced — just in the JSON pass (WriteNestedCountAndWindow)
        // instead of as a SQL LIMIT, the same trade the $levels path already takes below.
        bool isProjectionLeaf = engaged.Children is not { Count: > 0 };
        int? countBound = !deferPagingToJson && isProjectionLeaf && engaged.Count && maxExpandTop is int cap
            ? (int)Math.Min((long)cap + 1, int.MaxValue)
            : null;

        // #334: this level's count comes from an independent scalar subquery, so the count bound is
        // NOT composed and the requested window goes to SQL instead (see the composition block
        // below). `countBound` above is deliberately still COMPUTED — `paging` reads it, and keeping
        // that decision byte-identical is what makes the deterministic child-key ORDER BY tiebreaker
        // (and therefore WHICH rows land in the page, and therefore the response body) unchanged.
        bool carrierCount = countViaCarrier && !deferPagingToJson && isProjectionLeaf && engaged.Count;

        // #313: a BARE pushed leaf (no $count, no explicit $top of its own — $skip alone included) used
        // to compose NO SQL Take at all, leaving the single most common $expand shape unbounded by
        // MaxExpandTop. Same trade as countBound above (SQL Take(cap+1), gated to a projection leaf for
        // the same APPLY/LATERAL reason), just for the no-$count case. Mutually exclusive with
        // countBound (that one requires engaged.Count; this one requires !engaged.Count), and with an
        // explicit $top (Top is null here) since $top must win over the default ceiling bound.
        int? defaultLeafBound = !deferPagingToJson && isProjectionLeaf && !engaged.Count && engaged.Top is null
            && maxExpandTop is int defaultCap
            ? (int)Math.Min((long)defaultCap + 1, int.MaxValue)
            : null;

        // Whenever paging is in play -- pushed to SQL now, or deferred to the JSON window -- stabilize
        // the order so WHICH rows land in the page is deterministic. Mirrors the root path's
        // EnsureStableOrder (#241): the nav element's single key as a FINAL tiebreaker, a ThenBy after
        // an explicit nested $orderby so a non-unique sort column still pages stably.
        //
        // Applied even when Skip/Take/countBound end up NOT composed to SQL, so the deferred JSON
        // window still pages over a deterministic order. A composite or unresolvable key is left to
        // the provider -- best-effort, never throws.
        bool paging = (engaged.Skip is int s && s > 0) || engaged.Top is int || countBound is not null
            || defaultLeafBound is not null;
        if (paging && TryGetKeyClrProperty(model, elem) is { } keyProp)
        {
            ParameterExpression e = Expression.Parameter(elem, "e");
            LambdaExpression keySelector = Expression.Lambda(Expression.Property(e, keyProp), e);
            MethodInfo tiebreak = bound.OrderBy is { Count: > 0 } ? _enumerableThenBy : _enumerableOrderBy;
            access = Expression.Call(tiebreak.MakeGenericMethod(elem, keyProp.PropertyType), access, keySelector);
        }

        // #300 fix: inside the $levels recursion (deferPagingToJson), no SQL Skip/Take is composed at
        // all — every level both windows a collection and projects a further (self-referential)
        // collection out of it, the same untranslatable APPLY/LATERAL shape as the #298 count case.
        // $skip/$top are applied instead in the JSON pass (ShapeLevelsInJson), exactly as the count
        // bound already deferred (maxExpandTop: null) for this path. Outside $levels, Skip/Take push to
        // SQL only when $count is absent; with $count the full (ordered) filtered set is materialized
        // so the JSON pass can count it before paging (see EngagedExpand remarks) — bounded by
        // countBound (#254/#298) so an unbounded child collection can no longer be materialized.
        //
        // #304 fix: the raw (no-$count) Skip/Take below is ALSO only safe at a projection LEAF, for
        // exactly the same reason the #298 count bound is leaf-gated above — a level with children is
        // further projected element-wise a few lines below in BuildShapedNavAccess, so windowing THIS
        // level's collection and projecting a further collection out of each of its elements in the
        // SAME query is the same untranslatable "double collection" SQL APPLY/LATERAL shape, just
        // without $count in the mix. For a level with children the window is instead applied in the
        // JSON pass (ShapePushedExpandsInJson → ApplyNestedWindow), bounded by the same MaxExpandTop
        // ceiling WriteNestedCountAndWindow already enforces for the $count case.
        if (!deferPagingToJson)
        {
            if (carrierCount)
            {
                // #334: bound the FETCH by the requested window — Skip/Take compose to SQL exactly
                // as they do without $count. The count no longer rides on the array length, so
                // bounding the fetch cannot under-report Nav@odata.count (OData §11.2.4.2); the
                // JSON pass reads the carrier's exact value instead (ShapePushedExpandsInJson).
                //
                // The residual Take(cap + 1) when no $top was given preserves the pre-#334 DoS
                // bound: with a true count <= cap it never truncates the window, and with a true
                // count > cap the re-sited ceiling check 400s on the carrier's exact value before
                // the page is ever used — so MaxExpandTop breach behaviour is unchanged. With no
                // ceiling configured (the shipping default, #313) there is no residual bound, which
                // is also exactly what the pre-#334 path composed for this shape (`countBound` is
                // null when `maxExpandTop` is null): the $skip/$top the client asked for is now the
                // only bound, where before there was none at all.
                if (engaged.Skip is int csk && csk > 0)
                    access = Expression.Call(_enumerableSkip.MakeGenericMethod(elem), access, Expression.Constant(csk));
                long limit = engaged.Top is int ctp ? ctp : long.MaxValue;
                if (maxExpandTop is int ccap) limit = Math.Min(limit, (long)ccap + 1);
                if (limit < int.MaxValue)
                    access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant((int)limit));
            }
            else if (!engaged.Count)
            {
                if (isProjectionLeaf)
                {
                    if (engaged.Skip is int sk && sk > 0)
                        access = Expression.Call(_enumerableSkip.MakeGenericMethod(elem), access, Expression.Constant(sk));
                    if (engaged.Top is int tp)
                        access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(tp));
                    // #313: no explicit $top → fall back to the default ceiling bound (composed AFTER
                    // any $skip, same as the explicit-$top Take above) so a bare (or $skip-only) leaf is
                    // no longer an unbounded materialization.
                    else if (defaultLeafBound is int leafBound)
                        access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(leafBound));
                }
                // else: a level WITH children — defer the $skip/$top window to the JSON pass (#304).
            }
            else if (countBound is int rowBound)
            {
                access = Expression.Call(_enumerableTake.MakeGenericMethod(elem), access, Expression.Constant(rowBound));
            }
        }

        return access;
    }

    // #206 phase 2 (multi-level expand): the fresh member-init projected for one element of an
    // intermediate expand level — <c>new Elem { scalar1 = source.scalar1, …, ChildNav = &lt;folded&gt; }</c>
    // — binding every scalar structural property (so nothing is column-pruned) and folding each nested
    // pushed navigation via BuildShapedNavAccess. <paramref name="source"/> is the element expression
    // (a Select lambda parameter for a collection, or the reference access for a single-valued nav).
    // Callers gate on IsMemberInitProjectable so every scalar bind is settable and non-complex.
    private static Expression BuildMemberInit(
        Expression source, Type elemType, IReadOnlyList<EngagedExpand> children,
        IEdmModel model, ODataQuerySettings binderSettings, int? maxExpandTop)
    {
        var bindings = new List<MemberBinding>();
        AddScalarBindings(bindings, source, elemType, model);
        foreach (EngagedExpand child in children)
        {
            bindings.Add(Expression.Bind(child.Binding.Property,
                BuildShapedNavAccess(source, child, model, binderSettings, maxExpandTop)));
        }
        return Expression.MemberInit(Expression.New(elemType), bindings);
    }

    // #206 phase 2 (multi-level expand): bind every scalar structural property of <paramref name="elemType"/>
    // as <c>member = source.member</c> into <paramref name="bindings"/> — the "don't column-prune an
    // intermediate level" rule shared by BuildMemberInit and BuildLevelsNavAccess. Callers gate on
    // IsMemberInitProjectable first, so every returned property is settable and non-complex.
    private static void AddScalarBindings(List<MemberBinding> bindings, Expression source, Type elemType, IEdmModel model)
    {
        foreach (PropertyInfo p in ScalarStructuralClrProps(elemType, model))
            bindings.Add(Expression.Bind(p, Expression.Property(source, p)));
    }

    // #206 phase 2 ($levels): build the bounded self-referential recursion for a $levels=N expand.
    // Returns the value assigned to <paramref name="owner"/>.Nav: each level is projected into a FRESH
    // member-init recursing the SAME navigation one level shallower, and the deepest level empties the
    // self-navigation (an empty collection / a null reference) so the graph is finite — no parent<->child
    // object cycle can form for System.Text.Json.
    //
    // #254 (item 2): the expand's nested $filter/$orderby/$skip/$top are applied at EVERY level of the
    // recursion, matching the semantics ODL itself implements — Microsoft's
    // SelectExpandQueryOption.ProcessLevels rewrites $levels=N into N nested
    // ExpandedNavigationSelectItems each carrying the SAME Filter/OrderBy/Top/Skip/Count options. The
    // <paramref name="bound"/> lambdas were bound ONCE by the caller: the nav element type is invariant
    // under $levels, so the same LambdaExpression is valid (and immutable) at every level. A nested
    // $expand under $levels is still deferred off pushdown by TryBuildEngagedExpand.
    private static Expression BuildLevelsNavAccess(
        Expression owner, EngagedExpand engaged, int remaining, IEdmModel model,
        in NavShapeBindings bound)
    {
        ExpandNavBinding nav = engaged.Binding;
        Type elem = nav.ElementType; // == owner's type (a true self-reference; see BuildLevelsNavBinding)
        Expression access = Expression.Property(owner, nav.Property);

        if (nav.IsCollection)
        {
            // #254/#300: NEITHER the MaxExpandTop count bound NOR $skip/$top are pushed into SQL here
            // (hence maxExpandTop: null AND deferPagingToJson: true). Inside a $levels projection every
            // level both WINDOWS a collection and projects a further (self-referential) collection out
            // of it, which EF Core can only translate with SQL APPLY/LATERAL — SQLite and other
            // providers without APPLY fail to translate, and the request would then silently degrade to
            // EDM-only (no data AND no count/window — #300 was exactly this for $skip/$top). The
            // ceiling and the $skip/$top window are both enforced/applied instead at EVERY level, in the
            // JSON pass (ShapeLevelsInJson → WriteNestedCountAndWindow / WriteNestedWindowOnly): a count
            // breach OR a $skip/$top-only breach is a 400 (#316), never a truncated count nor an
            // unbounded materialization, and $skip/$top window the already-materialized array. What is
            // given up is only the SQL-side cost bound — consistent with E3, which already leaves an
            // omitted nested $top unbounded.
            access = ApplyNavShape(access, engaged, elem, model, bound, maxExpandTop: null, deferPagingToJson: true);

            ParameterExpression n = Expression.Parameter(elem, "n");
            var bindings = new List<MemberBinding>();
            AddScalarBindings(bindings, n, elem, model);
            Expression deeper = remaining > 1
                ? BuildLevelsNavAccess(n, engaged, remaining - 1, model, bound)
                // Leaf (#335): a NEW empty list, so the self-navigation serializes as [] rather than
                // null and the recursion terminates without loading a further level.
                //
                // This used to be `n.Nav.Take(0).ToList()`, which reads the same [] but is not free:
                // it still NAMES the navigation, so EF Core composes a real N+1'th join level for it —
                // a full-table ROW_NUMBER() window whose every row is then discarded by
                // `WHERE "row" <= 0` (#335). Worse, translation cost for a pushed nested projection is
                // ~3x per collection level (#328), so the dead level is a full FACTOR OF 3 on the whole
                // request, not a constant. Measured on the #328 harness: depth 9, 3,830 ms -> 1,544 ms;
                // depth 10, 11,453 ms -> 4,582 ms. Expression.New(List<elem>) names no navigation, so
                // EF evaluates it client-side per row and emits exactly N joins for $levels=N.
                //
                // Byte-identical by construction: both shapes produce an empty List<elem> assigned to
                // the same member, and the deepest level is where the recursion stops, so no level that
                // carries data is affected. LevelsJoinCountSqliteTests pins both halves — the join
                // count AND the exact response bytes.
                : Expression.New(typeof(List<>).MakeGenericType(elem));
            bindings.Add(Expression.Bind(nav.Property, deeper));
            LambdaExpression proj = Expression.Lambda(Expression.MemberInit(Expression.New(elem), bindings), n);
            Expression projected = Expression.Call(_enumerableSelect.MakeGenericMethod(elem, elem), access, proj);
            return Expression.Call(_enumerableToList.MakeGenericMethod(elem), projected);
        }

        // Single-valued self-reference (e.g. a Manager chain): a null-guarded fresh member-init. The
        // OData parser rejects $filter/$orderby/$skip/$top/$count on a single-valued navigation, so
        // there is nothing to shape here — only a nested $select can reach this shape, and that is
        // applied on the serialized JSON (ShapeLevelsInJson).
        var refBindings = new List<MemberBinding>();
        AddScalarBindings(refBindings, access, elem, model);
        Expression refDeeper = remaining > 1
            ? BuildLevelsNavAccess(access, engaged, remaining - 1, model, bound)
            : Expression.Constant(null, elem);
        refBindings.Add(Expression.Bind(nav.Property, refDeeper));
        Expression refInit = Expression.MemberInit(Expression.New(elem), refBindings);
        return Expression.Condition(
            Expression.Equal(access, Expression.Constant(null, elem)),
            Expression.Constant(null, elem), refInit);
    }

    // #206 phase 2 (optioned expand): the CLR property for a navigation element type's single EDM key,
    // used to stabilize nested paging (see BuildShapedNavAccess). Returns null for a composite key, a
    // keyless type, or a CLR name that does not resolve — the caller then simply skips stabilization.
    internal static PropertyInfo? TryGetKeyClrProperty(IEdmModel model, Type elem)
    {
        // #508: EdmClrTypeMap, not model.FindDeclaredType(elem.FullName). On a renamed schema this
        // answered null for every element type, which made every navigation non-pageable (#313) and
        // dropped the nested-paging tiebreaker.
        if (EdmClrTypeMap.FindEntityType(model, elem) is not { } entityType) return null;
        var keys = entityType.Key().ToList();
        if (keys.Count != 1) return null; // composite / keyless → leave order to the provider
        // #253: the EDM key name may be a [JsonPropertyName] rename — resolve back to the CLR property.
        return ODataPropertyNaming.FindClrPropertyByEdmName(elem, keys[0].Name);
    }

    // ── #313 stage 5: bare-$expand continuation paging ───────────────────────────────────────────
    //
    // One navigation of one entity set that a bare `?$expand=Nav` may page: over the ceiling it is
    // trimmed to MaxExpandTop and annotated with `Nav@odata.nextLink`, and a continuation route
    // `GET /{Set}({key})/{Nav}?$skip=N` is registered to serve the rest.
    //
    // Everything here is resolved ONCE at startup, off structural facts only.
    internal readonly record struct ExpandPagingNav(
        // The EDM (JSON) navigation name — the URL segment, and the key the emission site matches on.
        // Honors [JsonPropertyName] exactly as $expand and $metadata do.
        string EdmName,
        PropertyInfo NavProperty,
        Type ElementType,
        // The child element type's single-key CLR property, resolved through the SAME
        // TryGetKeyClrProperty call ApplyNavShape uses to compose page 1's tiebreaker. Sharing the
        // call — not re-deriving the key — is what makes page 1's ORDER BY and the continuation's
        // ORDER BY provably the same column (§4.5 of the design). A composite/unresolvable key makes
        // the navigation non-pageable outright rather than silently unordered.
        PropertyInfo ChildKeyProperty);

    // The shared pageability predicate. Two call sites -- continuation-route registration and link
    // emission -- and they must agree: a link with no route is a 404, a route with no link is a
    // delegate-safety hole.
    //
    // ServeRaw is resolved through ResolveNavTreatment, not through "this profile routes it", which
    // is what keeps a delegate-backed navigation from getting a raw continuation route.
    //
    // #421: the candidate set is `new[] { profile }`, the URL-named set ALONE -- byte-for-byte what
    // the root read path uses, so this route can expose no row that path does not already expose to
    // the same caller. The sibling union it used to use disagreed with the read path at depth 1, and
    // withholding the route withheld the LINK too, leaving an over-ceiling bare $expand 400ing
    // forever with ExpandPagingEnabled silently inert.
    //
    // Page size is MaxExpandTop, never MaxTop: they are independent knobs, and with MaxTop null the
    // continuation would be unbounded. Remaining conditions are the ones under which a link could
    // exist at all -- ExpandPagingEnabled, ExpandEnabled, HasGetQueryable, ExpandPushdownEnabled,
    // collection-valued, resolvable single key. Empty list on the default registration, which is
    // what keeps its route table byte-identical.
    internal static IReadOnlyList<ExpandPagingNav> ResolveExpandPagingNavigations(
        IEntitySetEndpointSource profile, Type modelType, OhDataRegistration registration)
    {
        if (!profile.ExpandPagingEnabled || profile.MaxExpandTop is not int ||
            !profile.ExpandEnabled || !profile.HasGetQueryable || !profile.ExpandPushdownEnabled)
        {
            return Array.Empty<ExpandPagingNav>();
        }

        // The entity set must exist in the container for the continuation URL this predicate implies
        // to be addressable at all. Kept as a guard only — #421 removed the sibling union that used
        // to be the reason this lookup was here.
        if (registration.EdmModel.EntityContainer?.FindEntitySet(profile.EntitySetName) is null)
            return Array.Empty<ExpandPagingNav>();

        // #421: THE CANDIDATE SET IS `new[] { profile }` — the URL-named set ALONE, byte-for-byte the
        // array ApplyCollectionPipelineAsync passes as the root level's `levelSources`. See this
        // method's remarks above for why the sibling union that used to be here protected nothing.
        IReadOnlyList<IEntitySetEndpointSource> candidates = new[] { profile };

        List<ExpandPagingNav> result = new();
        // NavigationPropertyNames (not the EDM's navigations) deliberately: it is the set the pushdown
        // gate itself builds pushdownExpandNavs from, so a nav absent from it can never be in the
        // engaged tree and could never receive a link. It is also the set BuildStructuralProperties
        // subtracts, so a continuation route can never collide with a structural-property route.
        foreach (string navName in profile.NavigationPropertyNames)
        {
            if (BuildExpandNavBinding(modelType, navName, registration.EdmModel) is not { } binding) continue;
            if (!binding.IsCollection) continue;
            if (ResolveNavTreatment(navName, candidates).Treatment != NavTreatment.ServeRaw) continue;
            if (TryGetKeyClrProperty(registration.EdmModel, binding.ElementType) is not { } childKey) continue;

            result.Add(new ExpandPagingNav(
                ODataPropertyNaming.ResolveEdmName(binding.Property), binding.Property,
                binding.ElementType, childKey));
        }
        return result;
    }

    // #313 stage 5: a one-field holder for the parent key value referenced from the continuation's
    // Where predicate. Expression.Constant(box) + Expression.Field is the exact shape the C# compiler
    // emits for a captured local, which is what EF Core's parameter extraction recognises — so the
    // key becomes a SQL PARAMETER rather than a literal baked into the command text. An
    // Expression.Constant of the value itself would produce a distinct SQL string per key and defeat
    // the provider's plan cache on a route whose whole purpose is to be called repeatedly.
    internal sealed class ContinuationKeyBox<T>
    {
        public T Value = default!;
    }

    // #313 stage 5: the continuation page, written as plain LINQ so the composed shape is readable
    // rather than assembled from MethodInfos. The only reason it is generic-and-reflected at all is
    // that the navigation element type is not a type parameter of MapEntitySet; its sole caller
    // closes it once at startup and compiles a delegate over it, so no reflection runs per request
    // and no TargetInvocationException can wrap a provider fault.
    //
    // SelectMany over a key-pinned parent is what makes the provider emit an INNER JOIN with
    // LIMIT/OFFSET rather than the partitioned ROW_NUMBER() window page 1 uses — an index seek, not
    // a window over the whole child table. The OrderBy is unconditional (see the call site).
    private static object[] ContinuationPage<TParent, TElement, TChildKey>(
        IQueryable<TParent> parents,
        Expression<Func<TParent, IEnumerable<TElement>>> navSelector,
        Expression<Func<TElement, TChildKey>> childKeySelector,
        int skip, int take)
    {
        TElement[] page = parents
            .SelectMany(navSelector)
            .OrderBy(childKeySelector)
            .Skip(skip)
            .Take(take)
            .ToArray();
        object[] boxed = new object[page.Length];
        for (int i = 0; i < page.Length; i++) boxed[i] = page[i]!;
        return boxed;
    }

    internal static readonly MethodInfo _continuationPageMethod =
        typeof(ExpandEngine).GetMethod(
            nameof(ContinuationPage), BindingFlags.NonPublic | BindingFlags.Static)!;

    // #313 stage 5: "truly bare", as a plan-time predicate over the already-parsed EngagedExpand.
    // The rule in one sentence: a nested option list that normalizes to the IDENTITY transform is
    // bare; anything else is not. Only two no-ops survive the parser — `$skip=0` (ApplyNavShape
    // already guards `sk > 0`, so it composed nothing) and `$count=false` (EngagedExpand.Count is
    // `item.CountOption == true`, so absent and false are already the same value) — and both are
    // therefore treated as bare, giving a faithful `?$skip={cap}` continuation.
    //
    // Everything else keeps the 400 stage 2 gave it (§5's fail-closed matrix): a nested
    // $filter/$orderby/$select cannot be carried by a $skip-only link; $levels and a level WITH
    // children are not SQL-bounded at all, so a link there would advertise a bound that does not
    // exist; an explicit $top means the client asked for exactly N rows and got them, so the response
    // is complete with respect to the request and a link would be wrong.
    private static bool IsBareContinuableLeaf(in EngagedExpand e) =>
        e.Binding.IsCollection
        && e.Levels == 0
        && e.Children is not { Count: > 0 }
        && e.Filter is null
        && e.OrderBy is null
        && e.Top is null
        && e.Skip is null or 0
        && !e.Count
        && e.NestedSelect is not { Count: > 0 };

    // #313 stage 5: what the JSON shaping pass needs to write a continuation link, threaded from the
    // GetQueryable collection route. Non-null ONLY at depth 1 — the recursive ShapePushedExpandsInJson
    // calls pass null, which is exactly what keeps depth >= 2 on its 400 (§5).
    //
    // ParentItems is the CLR page, index-parallel with the JsonObjects being shaped. It is threaded
    // because <b>the parent key is not in the JSON</b>: a root $select strips it and the shaping pass
    // runs after the strip (G6), so reading the key off the payload would produce a broken link for
    // exactly the requests that need one most. ExpandLevelAsync maintains the same parallel
    // items/jsonItems pair for the same reason and is the precedent followed here.
    // #412: <paramref name="RequestedPageSize"/> is this request's `Prefer: [odata.]maxpagesize=N`,
    // or null when the client asked for nothing. It NARROWS the nested page — never the ceiling — and
    // only on the one arm that emits a continuation link. See ShapePushedExpandsInJson's bare-leaf arm.
    internal sealed record ExpandPagingContext(
        string BaseUrl,
        string EntitySetName,
        PropertyInfo ParentKeyProperty,
        IReadOnlyList<object> ParentItems,
        IReadOnlyDictionary<string, ExpandPagingNav> PageableByEdmName,
        int? RequestedPageSize);

    // #206 phase 2 (optioned + multi-level expand): apply the JSON-side portion of a pushed expand's
    // nested options to the already-serialized parent objects (in the configured naming policy —
    // PascalCase by default) — $count (emit
    // Nav@odata.count), the count-deferred $skip/$top paging, nested $select projection, and
    // (recursively) the same shaping for each deeper pushed level. Filter/OrderBy (and paging when
    // $count is absent) were already applied in SQL by BuildShapedNavAccess, so this touches only the
    // navs that actually need post-serialization shaping. Reuses StripToSelectedProperties so nested
    // $select casing/annotation handling is identical to the root-level strip.
    // #206 ($levels): the CLR property names of every navigation this request pushed with $levels,
    // walked recursively through the engaged tree. OmitUnexpandedNavigations uses this to keep the
    // bounded recursion of ONLY these (delegate-less, pushed) navs — a delegate-backed $levels nav is
    // never in the engaged tree, so its deeper self-references stay stripped as before. Returns null
    // (the common no-$levels case) so the keep is a strict no-op unless a $levels expand was pushed.
    internal static HashSet<string>? CollectPushedLevelsNavNames(IReadOnlyList<EngagedExpand>? engaged)
    {
        if (engaged is null) return null;
        HashSet<string>? names = null;
        void Walk(IReadOnlyList<EngagedExpand> level)
        {
            foreach (EngagedExpand e in level)
            {
                // #253 completion: OmitUnexpandedNavigations matches against EDM (JSON) nav names, so
                // record the binding's EDM name (a [JsonPropertyName]-renamed self-referential nav
                // resolves to its JSON name, an un-renamed one to its CLR name).
                if (e.Levels > 0) (names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(ODataPropertyNaming.ResolveEdmName(e.Binding.Property));
                if (e.Children is { Count: > 0 }) Walk(e.Children);
            }
        }
        Walk(engaged);
        return names;
    }

    // #313 stage 5: <paramref name="paging"/> is non-null ONLY on the depth-1 call from the
    // GetQueryable collection route; every recursive call below passes null, which is what keeps
    // depth >= 2 on the 400 stage 2 gave it (§5, and O5 on the issue).
    // #334: <paramref name="carrierCounts"/> maps a counted navigation's CLR property to that nav's
    // exact per-parent count, index-parallel with <paramref name="parents"/>. Non-null ONLY on the
    // depth-1 call from the GetQueryable collection route (like <paramref name="paging"/>), and only
    // for navs the carrier projection actually carried; every other counted nav still counts its
    // materialized array through WriteNestedCountAndWindow.
    internal static void ShapePushedExpandsInJson(
        IEnumerable<JsonObject> parents, IReadOnlyList<EngagedExpand> engaged,
        JsonSerializerOptions serializerOptions, int? maxExpandTop,
        ExpandPagingContext? paging = null,
        IReadOnlyDictionary<PropertyInfo, int[]>? carrierCounts = null)
    {
        foreach (EngagedExpand e in engaged)
        {
            // A level with a nested $count/$select OR deeper pushed children needs JSON work; a pure
            // leaf whose options were fully handled in SQL is skipped. (A purely structural $levels
            // recursion carries no Count/NestedSelect/Children either, so it is skipped here too —
            // UNLESS it carries a nested $skip/$top: #300 fixed BuildLevelsNavAccess to no longer push
            // those to SQL for the $levels path, so a $levels item with $skip/$top and nothing else
            // now needs the JSON pass too, or the window would silently never be applied.)
            bool hasChildren = e.Children is { Count: > 0 };
            bool levelsNeedsJsonPaging = e.Levels > 0 && (e.Skip is int || e.Top is int);
            // #313: a bare collection leaf (no $count, no $top/$skip of its own) now carries a SQL
            // Take(MaxExpandTop+1) bound composed by ApplyNavShape (defaultLeafBound) — so it still
            // needs to be visited here to enforce the ceiling, even though there is no count/select/
            // children work to do otherwise. Also the entry point for a BARE $levels recursion (which
            // carries no children by construction — TryBuildEngagedExpand defers a nested $expand under
            // $levels off pushdown), whose per-level ceiling ShapeLevelsInJson enforces below. Gated on
            // `maxExpandTop is int` so an uncapped registration keeps skipping the walk outright.
            bool needsLeafCeilingCheck = e.Binding.IsCollection && !hasChildren && e.Top is null && maxExpandTop is int;
            if (!e.Count && e.NestedSelect is null && !hasChildren && !levelsNeedsJsonPaging && !needsLeafCeilingCheck)
                continue;

            // #254 (item 2): a $levels expand may now carry $count/$select. Its recursion is implicit
            // (there is no per-level EngagedExpand — the SAME binding repeats), so shape every level by
            // walking the serialized graph down the self-navigation.
            if (e.Levels > 0)
            {
                ShapeLevelsInJson(parents, e, e.Levels, serializerOptions, maxExpandTop);
                continue;
            }

            PropertyInfo prop = e.Binding.Property;
            string key = ResolveNavigationJsonKey(prop.Name, prop, serializerOptions);

            // #313 stage 5: is THIS engaged expand the one shape a $skip-only continuation can serve?
            // Resolved once per expand item rather than per parent — it is a plan-time fact.
            ExpandPagingNav? pageableNav = null;
            if (paging is not null && maxExpandTop is int && IsBareContinuableLeaf(e) &&
                paging.PageableByEdmName.TryGetValue(
                    ODataPropertyNaming.ResolveEdmName(prop), out ExpandPagingNav resolvedNav))
            {
                pageableNav = resolvedNav;
            }

            // #334: this nav's exact per-parent counts, when the carrier projection supplied them.
            int[]? navCounts = null;
            carrierCounts?.TryGetValue(prop, out navCounts);

            // Index into paging.ParentItems. The two lists are built index-parallel by the caller
            // (see the ShapePushedExpandsInJson call site in the GetQueryable route), so this counter
            // and the foreach below stay in step by construction.
            int parentIndex = -1;
            foreach (JsonObject parent in parents)
            {
                parentIndex++;
                JsonNode? node = parent[key];
                if (e.Binding.IsCollection && node is JsonArray arr)
                {
                    if (e.Count && navCounts is not null)
                    {
                        // Defensive: navCounts is built index-parallel with `parents` in ONE loop at
                        // the single call site that supplies it, so this cannot fire. It throws
                        // rather than falling through to WriteNestedCountAndWindow, because that
                        // path counts the materialized array — which the carrier has already
                        // SQL-windowed — and would therefore report the PAGE size as the collection
                        // count. A 500 is strictly better than a silently wrong @odata.count.
                        if ((uint)parentIndex >= (uint)navCounts.Length)
                            throw new InvalidOperationException("OhData: nested $count carrier desynchronised from the serialized page.");

                        // #334: exact count from SQL, and the page was ALREADY windowed in SQL — so
                        // no JSON window is applied here (that is the whole point: the fetch was
                        // bounded to the requested window instead of to the ceiling). The
                        // MaxExpandTop ceiling is re-sited from "materialized array length" to "the
                        // exact count"; see the EnsureWithinExpandCeiling(int, …) overload for why
                        // that is the same predicate rather than merely a similar one.
                        int exact = navCounts[parentIndex];
                        EnsureWithinExpandCeiling(exact, key, maxExpandTop, "'$count'");
                        parent[$"{key}@odata.count"] = exact;
                    }
                    else if (e.Count)
                    {
                        WriteNestedCountAndWindow(parent, key, arr, e, maxExpandTop);
                    }
                    // #304: a children level with $skip/$top but no $count was never SQL-windowed
                    // (ApplyNavShape deferred it here) — apply that window now, BEFORE recursing into
                    // children, so only the surviving (windowed) parents are shaped further. $skip=0 is
                    // guarded out (mirrors ApplyNavShape's own `sk > 0` guard) so a no-op $skip=0 doesn't
                    // trip the MaxExpandTop ceiling below — but $top is NOT guarded on > 0: $top=0 must
                    // still window (to an empty array), never fall through to "no window at all".
                    else if (hasChildren && ((e.Skip is int sk && sk > 0) || e.Top is int))
                    {
                        WriteNestedWindowOnly(arr, key, e, maxExpandTop);
                    }
                    // #313: bare children (nested $expand, no $count/$skip/$top of its own) can't be
                    // SQL-windowed at all (the same APPLY/LATERAL constraint documented on
                    // ApplyNavShape's isProjectionLeaf gate) — so it was, and still is, fully
                    // materialized here. It now needs the ceiling check the windowed shapes above
                    // already get, applied BEFORE recursing into children so a breach 400s before
                    // descending any further.
                    else if (hasChildren && maxExpandTop is int)
                    {
                        EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$expand'");
                    }
                    // #313: bare leaf (no children, no $count, no explicit $top — a lone $skip=0 no-op
                    // included). ApplyNavShape now SQL-bounds this shape to MaxExpandTop+1 rows
                    // (defaultLeafBound), so arr.Count > cap here means the true collection exceeds the
                    // configured budget.
                    //
                    // #313 stage 5: for the truly-bare subset on an opted-in profile that breach becomes
                    // a continuation instead of a 400 — trim to the ceiling and annotate. Everything
                    // else still takes the EnsureWithinExpandCeiling 400 below, so the M1 rule ("no
                    // bound without either a link or a 400") holds at this commit as at every other.
                    else if (!hasChildren && e.Top is null && maxExpandTop is int leafCap)
                    {
                        // #412: the client may ask for a SMALLER nested page with
                        // `Prefer: [odata.]maxpagesize=N` (Protocol §8.2.8.5 — "each collection within
                        // the response"). It narrows the page, never the ceiling: clamping the ceiling
                        // to a client-supplied number would let a header turn a 200 into a 400, and
                        // MaxExpandTop is the server's own DoS bound. So the preference is read ONLY
                        // on this arm, and ONLY when a link is actually going out — trimming without a
                        // link is the silent truncation M1 forbids, which is why a non-pageable
                        // navigation below still measures itself against maxExpandTop and not against
                        // the request.
                        int leafPage = leafCap;
                        if (pageableNav is not null && paging!.RequestedPageSize is int requested && requested < leafCap)
                            leafPage = requested;

                        // WriteNestedNextLink reports false when it could not build a link (a null key
                        // value); it leaves the array untouched in that case, so the ceiling's 400
                        // applies exactly as it would for any other non-pageable over-ceiling shape.
                        // Never a trim without a link.
                        if (pageableNav is not { } pnav || arr.Count <= leafPage
                            || !WriteNestedNextLink(parent, key, arr, leafPage, pnav, paging!, parentIndex))
                        {
                            EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$expand'");
                        }
                    }
                    // Recurse into deeper pushed levels on the (paged) elements BEFORE this level's
                    // $select strip — the strip keeps expanded-nav names (ExtractSelectedProperties), so
                    // the children survive, and shaping deeper counts/selects sees the full child graph.
                    if (hasChildren)
                        ShapePushedExpandsInJson(arr.OfType<JsonObject>(), e.Children!, serializerOptions, maxExpandTop);
                    if (e.NestedSelect is not null)
                        StripToSelectedProperties(arr.OfType<JsonObject>(), e.NestedSelect);
                }
                else if (!e.Binding.IsCollection && node is JsonObject one)
                {
                    if (hasChildren)
                        ShapePushedExpandsInJson(new[] { one }, e.Children!, serializerOptions, maxExpandTop);
                    if (e.NestedSelect is not null)
                        StripToSelectedProperties(new[] { one }, e.NestedSelect);
                }
            }
        }
    }

    // #254/#304: the shared MaxExpandTop ceiling check for a collection level whose windowing was
    // deferred to the JSON pass — because it couldn't be SQL-bounded, the full (filtered) collection had
    // to be materialized first (see ApplyNavShape's isProjectionLeaf gate), so a collection larger than
    // the configured budget is a DoS exposure the same way an unbounded nested $count materialization
    // would be. <paramref name="verb"/> names what couldn't be computed/applied in the resulting message
    // (e.g. "'$count'" or "'$top'/'$skip'") so the two call sites (count vs. plain windowing) get a
    // distinct-but-same-family message. Throws Microsoft.OData.ODataException, caught by the collection
    // route's existing handler and returned as a 400 InvalidQueryOption — no IResult threading through
    // this void recursive walk.
    private static void EnsureWithinExpandCeiling(JsonArray arr, string key, int? maxExpandTop, string verb) =>
        EnsureWithinExpandCeiling(arr.Count, key, maxExpandTop, verb);

    // #334: the same check against a count that did NOT come from a materialized array — the
    // carrier's exact scalar. Split out (rather than duplicated at the call site) so the message the
    // client sees is byte-identical whichever way the count was obtained. The predicate is also
    // equivalent, not merely similar: the pre-#334 array was Take(cap + 1)-bounded, so
    // `arr.Count > cap` was already exactly `trueCount > cap` — with the carrier the breach signal
    // is simply exact rather than a saturated proxy for it.
    private static void EnsureWithinExpandCeiling(int count, string key, int? maxExpandTop, string verb)
    {
        if (maxExpandTop is int cap && count > cap)
        {
            throw new Microsoft.OData.ODataException(
                $"The nested {verb} on '{key}' cannot be computed: the related collection exceeds the " +
                $"maximum of {cap} entities. Narrow it with a nested $filter.");
        }
    }

    // #313 stage 5: trim one over-ceiling bare leaf to the ceiling and annotate it with
    // Nav@odata.nextLink -- the one shape a $skip-only continuation can faithfully serve.
    //
    // TRIM AND LINK ARE ONE STEP, OR NEITHER HAPPENS. Both bail-outs are checked BEFORE the array is
    // touched, and the method reports whether it linked so the caller can fall back to the 400. An
    // earlier revision trimmed first and returned early, leaving a silently truncated array with
    // neither a link nor an error -- the one outcome M1 forbids outright.
    //
    // Trims by "while > cap" rather than removing one assumed probe row, so it stays correct if
    // ApplyNavShape's cap + 1 bound ever widens.
    //
    // The 4.0 long-form annotation name: the short form is a SHOULD and this framework emits
    // OData-Version: 4.0 throughout. Placement after the array is fine -- §20.2 exempts nextLink from
    // the "immediately prior" rule.
    //
    // THE KEY COMES FROM THE CLR ENTITY, NEVER THE PAYLOAD: a root $select strips the key property and
    // this pass runs after the strip, so parent["Id"] is absent for exactly the requests that most
    // need a working link. paging.ParentItems is threaded in for this one purpose.
    private static bool WriteNestedNextLink(
        JsonObject parent, string key, JsonArray arr, int cap,
        in ExpandPagingNav nav, ExpandPagingContext paging, int parentIndex)
    {
        // Defensive: a desynchronised index would silently produce a link for the WRONG parent, which
        // is worse than no link. Unreachable through the single call site, which builds ParentItems
        // index-parallel with the JsonObject list it iterates — kept as an assertion, not as a case
        // the tests can reach.
        if ((uint)parentIndex >= (uint)paging.ParentItems.Count) return false;

        // Reachable: TKey is unconstrained, so a string or Nullable<T> key property can hold null on a
        // returned entity, and ODataEntityKeyUrlFormatter.Format throws on null. Without a key there is
        // no addressable continuation, so this collection is not pageable after all and takes the 400.
        object? parentKey = paging.ParentKeyProperty.GetValue(paging.ParentItems[parentIndex]);
        if (parentKey is null) return false;

        while (arr.Count > cap) arr.RemoveAt(arr.Count - 1);

        // Page 1's child offset is always 0 — a bare expand carries no $skip by definition — so the
        // first continuation hop is always ?$skip={cap}. The root page's own offset never appears
        // here: the root's continuation is a $skiptoken on a DIFFERENT path served by a DIFFERENT
        // route, and neither link builder reads the response body (§4.6).
        parent[$"{key}@odata.nextLink"] =
            $"{paging.BaseUrl}/{paging.EntitySetName}({ODataEntityKeyUrlFormatter.Format(parentKey)})" +
            $"/{nav.EdmName}?$skip={cap.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    // #418/#463/#464: the MaxExpandTop ceiling over RAW-SERVED collection expansions -- rows the
    // framework never composed (an Include inside a delegate, a tracked graph, a non-EF IQueryable,
    // a branch pushdown declined). No nested window is applied to those, so this is their only
    // bound. Enforced per level, from ApplyCollectionPipelineAsync, where all five read routes meet.
    //
    // Depth 1 classifies via ResolveNavTreatment (#293, frozen) and skips RunDelegate -- a delegate's
    // rows are the developer's answer (#313 O6). BELOW depth 1 there is no classification test:
    // ExpandLevelAsync's ServeRaw branch does not recurse, so everything down there is raw whatever
    // the navigation says. Applying the depth-1 test at every level served five chapters through a
    // delegate that ran zero times.
    //
    // 400, never trim-and-link: page 1 and the continuation cannot be shown to agree on an order
    // here (the framework composes neither side), and a link over a disagreeing order silently skips
    // and duplicates rows. Engaged navigations are skipped -- ShapePushedExpandsInJson handles those.
    internal static void EnforceRawExpandCeiling(
        IReadOnlyList<JsonObject> levelObjects,
        SelectExpandClause? clause,
        IReadOnlyList<IEntitySetEndpointSource> rootSources,
        Type? levelClrType,
        IReadOnlyList<EngagedExpand>? engaged,
        JsonSerializerOptions serializerOptions,
        int cap,
        int maxExpansionDepth,
        string entitySetName,
        bool singleEntityRead,
        string pathPrefix,
        string pathSuffix,
        int depth)
    {
        if (clause is null || levelObjects.Count == 0 || depth > MaxNestedExpandDepth) return;

        foreach (ExpandedNavigationSelectItem item in clause.SelectedItems.OfType<ExpandedNavigationSelectItem>())
        {
            string edmName = item.PathToNavigationProperty.FirstSegment.Identifier;

            // Engaged: bounded (and, where #313 allows, trimmed-and-linked) by
            // ShapePushedExpandsInJson / ShapeLevelsInJson instead. Skip the whole branch — a level
            // under an engaged one is engaged too, so that pass covers all of it.
            if (IsEngagedNav(engaged, edmName)) continue;

            // THE MODEL B GATE IS A DEPTH-1 GATE, AND ONLY A DEPTH-1 GATE. At depth 1 a navigation's
            // treatment decides whether anything other than the root handler produced its rows: a
            // RunDelegate nav's rows came from the developer's own delegate (#313 O6 — not the
            // framework's to bound), and a Blank one was overwritten with []/null by ExpandLevelAsync
            // (nothing to bound). Both are skipped, and skipping them also stops the descent, so this
            // walk never enters a delegate's subtree.
            //
            // BELOW depth 1 the same test would be wrong, and applying it there was the #464 defect
            // reproduced one level down. Everything reached here is under a ServeRaw parent by
            // construction, and ExpandLevelAsync's ServeRaw branch DOES NOT RECURSE — so at depth >= 2
            // no delegate ran, nothing was blanked, and every value in the payload is the root
            // handler's own raw graph whatever the EDM/profile classification of the navigation that
            // names it. MEASURED, cap = 2, GetAll, Author -Books(delegate-less)-> Book
            // -Chapters(DELEGATE)-> : `?$expand=Books($expand=Chapters)` served five chapters with the
            // Chapters delegate invoked ZERO times. Classifying those rows as "the delegate's answer"
            // and exempting them cited a delegate that never ran. The same holds for a Blank-
            // classified navigation down here: nothing blanked it either, so its rows are served raw.
            // So: check and descend regardless of classification.
            if (depth == 1 && ResolveNavTreatment(edmName, rootSources).Treatment != NavTreatment.ServeRaw)
            {
                continue;
            }

            PropertyInfo? navClr = levelClrType is null
                ? null
                : ODataPropertyNaming.FindClrPropertyByEdmName(levelClrType, edmName);
            string jsonKey = ResolveNavigationJsonKey(navClr?.Name ?? edmName, navClr, serializerOptions);

            bool descend = item.SelectAndExpand is { } nestedClause &&
                           nestedClause.SelectedItems.OfType<ExpandedNavigationSelectItem>().Any();

            // #466 + #463: a $levels recursion serves the SAME navigation N levels down, and since
            // #466 it does so on the raw substrate too. Its deeper levels carry no clause item of
            // their own (the recursion is implicit — the same navigation repeats), so they would be
            // invisible to the clause walk and unbounded, which is #463's hole re-opened along the
            // $levels axis. Walk them explicitly, exactly as ShapeLevelsInJson does for the pushed
            // recursion, through the SAME ResolveLevelsBudget both loaders use (#428).
            int levelsBudget = item.LevelsOption is { } lv
                ? ResolveLevelsBudget(lv.IsMaxLevel, lv.Level, maxExpansionDepth, MaxNestedExpandDepth)
                : 0;
            bool needChildren = descend || levelsBudget > 1;
            List<JsonObject>? children = null;

            foreach (JsonObject obj in levelObjects)
            {
                JsonNode? node = obj[jsonKey];
                if (node is JsonArray arr)
                {
                    if (arr.Count > cap)
                    {
                        throw RawExpandCeilingBreach(
                            jsonKey, cap, entitySetName, singleEntityRead, pathPrefix + edmName + pathSuffix);
                    }
                    if (needChildren) (children ??= new List<JsonObject>()).AddRange(arr.OfType<JsonObject>());
                }
                else if (needChildren && node is JsonObject one)
                {
                    // A single-valued navigation holds at most one related entity, so there is
                    // nothing to bound here — but its own children may be collections.
                    (children ??= new List<JsonObject>()).Add(one);
                }
            }

            // Levels 2..N of a $levels recursion: same navigation, same key, same ceiling. Checked
            // per level and top-down, so a breach 400s before the walk descends any further.
            if (levelsBudget > 1 && children is not null)
            {
                IReadOnlyList<JsonObject> levelNodes = children;
                for (int remaining = levelsBudget - 1; remaining >= 1 && levelNodes.Count > 0; remaining--)
                {
                    var deeper = new List<JsonObject>();
                    foreach (JsonObject obj in levelNodes)
                    {
                        JsonNode? node = obj[jsonKey];
                        if (node is JsonArray arr)
                        {
                            if (arr.Count > cap)
                            {
                                throw RawExpandCeilingBreach(
                                    jsonKey, cap, entitySetName, singleEntityRead,
                                    pathPrefix + edmName + pathSuffix);
                            }
                            deeper.AddRange(arr.OfType<JsonObject>());
                        }
                        else if (node is JsonObject one)
                        {
                            deeper.Add(one);
                        }
                    }
                    levelNodes = deeper;
                }
            }

            if (!descend || children is null) continue;

            EnforceRawExpandCeiling(
                children, item.SelectAndExpand,
                // Never read again: the Model B gate above is depth-1-only, so no deeper level
                // resolves a candidate set at all. That is why ResolveRequestSourcesForEdmType is
                // NOT called here — a per-level candidate resolution whose answer is discarded would
                // be per-request cost buying a decision this walk deliberately does not make.
                rootSources,
                NavElementClrType(navClr),
                engaged: null, // unreachable otherwise: this level was not engaged, so no child of it is
                serializerOptions, cap, maxExpansionDepth,
                entitySetName, singleEntityRead,
                pathPrefix + edmName + "($expand=", ")" + pathSuffix, depth + 1);
        }
    }

    // #463/#464: is this navigation covered by the pushdown's own JSON shaping pass at this level?
    // Matched on the binding's EDM name, exactly as CollectPushedLevelsNavNames records one.
    private static bool IsEngagedNav(IReadOnlyList<EngagedExpand>? engaged, string edmName)
    {
        if (engaged is null) return false;
        foreach (EngagedExpand e in engaged)
        {
            if (string.Equals(
                    ODataPropertyNaming.ResolveEdmName(e.Binding.Property), edmName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // #418/#463/#464: the two remediation messages.
    //
    // The single-entity arm is #418's, byte-identical: it is asserted in tests and quoted in docs,
    // and at depth 1 <paramref name="expandPath"/> is exactly the navigation's EDM name — which is
    // what that message always interpolated. Deeper levels get the whole path back
    // ("Books($expand=Chapters)"), so the suggested collection-route request is the SAME request the
    // client actually made rather than a truncation of it.
    //
    // Neither arm is EnsureWithinExpandCeiling's. That one ends "Narrow it with a nested $filter",
    // which is actively false advice for a raw-served expansion: a nested $filter is one of the
    // options this substrate silently ignores, so following it returns the same 400.
    private static Microsoft.OData.ODataException RawExpandCeilingBreach(
        string jsonKey, int cap, string entitySetName, bool singleEntityRead, string expandPath) =>
        singleEntityRead
            ? new Microsoft.OData.ODataException(
                $"The nested '$expand' on '{jsonKey}' cannot be served from a single-entity read: the " +
                $"related collection exceeds the maximum of {cap} entities. A single-entity read " +
                "applies no nested $filter/$orderby/$top window and cannot page an expanded " +
                $"collection, so request it through the collection route instead — e.g. " +
                $"GET /{entitySetName}?$filter=<key eq …>&$expand={expandPath}.")
            : new Microsoft.OData.ODataException(
                $"The nested '$expand' on '{jsonKey}' cannot be served: the related collection " +
                $"exceeds the maximum of {cap} entities. This expansion was not pushed down to the " +
                "data source, so no nested $filter/$orderby/$top window is applied to it and it " +
                "cannot be paged. Narrow the related data where it is loaded, raise MaxExpandTop, or " +
                "make the navigation pushdown-eligible (declared without a delegate, over an EF Core " +
                "IQueryable).");

    // #206 phase 2 (optioned expand) / #254: emit <c>Nav@odata.count</c> for one pushed collection
    // expand and apply its count-deferred $skip/$top window.
    //
    // OData §11.2.4.2 requires the emitted count to be the FULL filtered collection, not the page —
    // which is exactly why the #254 ceiling breach is a 400 rather than a silent truncation: the
    // materialization was bounded to MaxExpandTop + 1 rows in SQL (ApplyNavShape), so seeing more than
    // MaxExpandTop rows here means the true count is unknowable within the configured budget.
    private static void WriteNestedCountAndWindow(
        JsonObject parent, string key, JsonArray arr, EngagedExpand e, int? maxExpandTop)
    {
        EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$count'");

        // Count reflects the full filtered collection (paging was deferred to here).
        parent[$"{key}@odata.count"] = arr.Count;
        ApplyNestedWindow(arr, e);
    }

    // #304: a collection level with children carrying $skip/$top but NO $count (ApplyNavShape composed
    // no SQL Skip/Take at all for this shape — see its isProjectionLeaf gate) — apply the deferred
    // window here, in the JSON pass, BEFORE the caller recurses into children, so only the surviving
    // (windowed) parents are shaped further. Enforces the same MaxExpandTop ceiling as
    // WriteNestedCountAndWindow (the collection had to be fully materialized to window it here at all),
    // but — unlike that method — never emits <c>@odata.count</c>: no $count was requested on this shape.
    private static void WriteNestedWindowOnly(JsonArray arr, string key, EngagedExpand e, int? maxExpandTop)
    {
        EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$top'/'$skip'");
        ApplyNestedWindow(arr, e);
    }

    // #298/#300: the $skip/$top window shared by the $count case above (WriteNestedCountAndWindow) and
    // the $levels no-$count case (ShapeLevelsInJson) below — split out so there is exactly one place
    // that windows a JsonArray in-place, rather than two copies of the same [skip, end) rebuild.
    private static void ApplyNestedWindow(JsonArray arr, EngagedExpand e) =>
        ApplyNestedWindow(arr, e.Skip, e.Top);

    // #650: the same window, callable without an EngagedExpand so the delegate-backed path shares this
    // implementation rather than transcribing it. One site, two consumers.
    private static void ApplyNestedWindow(JsonArray arr, int? skipOption, int? topOption)
    {
        int skip = skipOption is int sk && sk > 0 ? Math.Min(sk, arr.Count) : 0;
        int end = topOption is int tp ? Math.Min(arr.Count, skip + Math.Max(tp, 0)) : arr.Count;
        if (skip > 0 || end < arr.Count)
        {
            // Rebuild to the [skip, end) window in one O(n) pass (Clear detaches the captured nodes so
            // they can be re-added) rather than repeated RemoveAt(0).
            var window = new List<JsonNode?>(end - skip);
            for (int i = skip; i < end; i++) window.Add(arr[i]);
            arr.Clear();
            foreach (JsonNode? node in window) arr.Add(node);
        }
    }

    // #254 (item 2): apply a $levels expand's nested $count/$select at EVERY level of the recursion.
    // <paramref name="parents"/> are the serialized entities at the current level and
    // <paramref name="remaining"/> the levels still loaded beneath them. Descends the SAME navigation
    // key each time — the $levels recursion re-uses one EngagedExpand rather than a per-level tree.
    //
    // The nested $select strip is applied to a level only AFTER descending into it, so the deeper
    // level's own Nav@odata.count is already written and survives the strip: NestedSelect carries the
    // self-navigation's EDM name (appended at plan time in TryBuildEngagedExpand), which is exactly
    // what KeepUnderSelect keys the "Nav@odata.count" inline-control-information rule off.
    private static void ShapeLevelsInJson(
        IEnumerable<JsonObject> parents, EngagedExpand e, int remaining,
        JsonSerializerOptions serializerOptions, int? maxExpandTop)
    {
        if (remaining < 1) return;

        PropertyInfo prop = e.Binding.Property;
        string key = ResolveNavigationJsonKey(prop.Name, prop, serializerOptions);

        var next = new List<JsonObject>();
        foreach (JsonObject parent in parents)
        {
            JsonNode? node = parent[key];
            if (e.Binding.IsCollection && node is JsonArray arr)
            {
                // #300: $skip/$top on a $levels expand are never pushed to SQL (ApplyNavShape's
                // deferPagingToJson) — they must be windowed here regardless of whether $count also
                // rides along. WriteNestedCountAndWindow does count-emission + windowing when $count
                // is requested; otherwise #316: WriteNestedWindowOnly (not a bare ApplyNestedWindow) so
                // the same MaxExpandTop ceiling is enforced here too — without it, a $levels recursion
                // with $skip/$top and no $count materialized every level's full collection with no
                // bound at all. Same $skip=0 no-op guard as the #304 pushed-expand path above (mirrors
                // ApplyNavShape's `sk > 0`); $top is never guarded on > 0 (a $top=0 window must still
                // collapse to empty, not be skipped).
                if (e.Count) WriteNestedCountAndWindow(parent, key, arr, e, maxExpandTop);
                else if ((e.Skip is int sk && sk > 0) || e.Top is int) WriteNestedWindowOnly(arr, key, e, maxExpandTop);
                // #313: a BARE level (no $count, no $skip/$top of its own — a nested $select alone
                // included) fired NEITHER arm above, so the ceiling was enforced NOWHERE on this path:
                // BuildLevelsNavAccess passes deferPagingToJson: true AND maxExpandTop: null, so
                // ApplyNavShape composes no SQL bound either. That made `Nav($levels=1)` — a
                // spec-equivalent restatement of a bare `$expand=Nav`, byte-identical response and all —
                // a one-parameter bypass of the very ceiling the bare shape is now rejected by. Checked
                // per level, on every level, so a breach 400s before the walk descends any further.
                // Same verb as the bare pushed-expand arm in ShapePushedExpandsInJson, so the two
                // spellings of the same request produce the same message.
                else EnsureWithinExpandCeiling(arr, key, maxExpandTop, "'$expand'");
                next.AddRange(arr.OfType<JsonObject>());
            }
            else if (!e.Binding.IsCollection && node is JsonObject one)
            {
                next.Add(one);
            }
        }

        if (next.Count == 0) return;

        ShapeLevelsInJson(next, e, remaining - 1, serializerOptions, maxExpandTop);
        if (e.NestedSelect is not null) StripToSelectedProperties(next, e.NestedSelect);
    }

    // #206 phase 2 (Option A1): builds the startup-time $expand pushdown binding for one
    // DELEGATE-LESS navigation (by CLR property name), or returns null when it is not eligible to
    // be folded into the collection projection. Only navigations declared WITHOUT a custom expand
    // delegate reach this method (the caller filters out every navigation that owns a
    // NavigationRouteDefinition), so provenance — "no delegate exists" — is already established;
    // this method only adds the structural safety checks. A navigation qualifies when it maps to a
    // settable CLR property and, for a collection, whose member type can accept a List&lt;TElement&gt;
    // (the .ToList() the projection emits). #323 (Change B): a navigation back to TModel (a
    // bidirectional relationship) is EXCLUDED only when the element type is also NOT member-init-
    // projectable — a projectable element type is always materialized through BuildShapedNavAccess's
    // fresh-POCO member-init (Change A), which structurally cannot close a parent&lt;-&gt;child object
    // cycle regardless of what navigations it declares, so the guard is unnecessary (and wrongly
    // conservative) there. An un-projectable element type keeps today's conservative defer. Everything
    // else stays EDM-only.
    internal static ExpandNavBinding? BuildExpandNavBinding<TModel>(string navPropertyName, IEdmModel model) =>
        BuildExpandNavBinding(typeof(TModel), navPropertyName, model);

    // #206 phase 2 (Option A1 / multi-level): non-generic core — build the pushdown binding for a
    // navigation <paramref name="navPropertyName"/> declared on <paramref name="ownerType"/> (the
    // root model at the top level, or a nested element type when recursing), or null when it is not
    // eligible. #323 (Change B): the back-reference guard is narrowed to the un-projectable residue —
    // checked against the OWNER at this level, so a nested nav that navigates back to its own parent
    // (a bidirectional relationship) is excluded exactly as at the root, but ONLY when its element
    // type cannot be member-init-projected (see the remarks above).
    internal static ExpandNavBinding? BuildExpandNavBinding(Type ownerType, string navPropertyName, IEdmModel model)
    {
        // #253 completion: navPropertyName is the EDM (JSON) navigation name — a [JsonPropertyName]-
        // renamed nav arrives here as its JSON name (from NavigationPropertyNames or the parser's
        // resolved identifier), so map JSON→CLR to reach the actual CLR member EF Include needs.
        PropertyInfo? navProp = ODataPropertyNaming.FindClrPropertyByEdmName(ownerType, navPropertyName);
        if (navProp is null || navProp.SetMethod is not { IsPublic: true }) return null;

        Type? elementType = NavElementClrType(navProp);
        if (elementType is null) return null;

        // #323 (Change B): a member-init-projectable element type is always materialized through a
        // fresh POCO (Change A in BuildShapedNavAccess), never the bare EF-tracked entity, so a
        // navigation back to ownerType can no longer close a serialization cycle on this path — only
        // an UN-projectable element type still risks materializing the bare (potentially cyclic)
        // related entity, so only that residue keeps the conservative defer.
        if (!IsMemberInitProjectable(elementType, model) && TypeHasNavigationTo(elementType, ownerType))
        {
            return null; // cyclic AND un-projectable — stays EDM-only
        }

        // NavElementClrType returns the property type itself for a single-valued reference and the
        // element type for a collection, so "element differs from property" identifies a collection.
        bool isCollection = navProp.PropertyType != elementType;

        if (isCollection &&
            !navProp.PropertyType.IsAssignableFrom(typeof(List<>).MakeGenericType(elementType)))
        {
            return null; // e.g. an array-typed collection nav; a List<T> cannot be assigned to it
        }

        return new ExpandNavBinding(navProp, isCollection, elementType);
    }

    // #206 phase 2 ($levels): build the pushdown binding for a SELF-REFERENTIAL navigation targeted by
    // $levels=N — the only shape the OData parser accepts $levels on. Unlike BuildExpandNavBinding this
    // deliberately allows the (inherently cyclic) self-reference: the $levels projection recurses a
    // BOUNDED number of times into FRESH member-init POCOs (each level's deeper nav is loaded then
    // emptied at the leaf), so no parent<->child object cycle can form for System.Text.Json. Requires
    // the navigation's element type to equal the owner type (a true recursive hierarchy) and, for a
    // collection, a List-assignable member. Returns null (→ pushdown skipped, nav stays EDM-only) for a
    // route-backed nav (checked by the caller), a non-self-referential target, or an unsettable property.
    internal static ExpandNavBinding? BuildLevelsNavBinding(Type ownerType, string navPropertyName)
    {
        // #253 completion: navPropertyName is the EDM (JSON) navigation name — map JSON→CLR so a
        // renamed self-referential nav still resolves to its CLR member for the $levels projection.
        PropertyInfo? navProp = ODataPropertyNaming.FindClrPropertyByEdmName(ownerType, navPropertyName);
        if (navProp is null || navProp.SetMethod is not { IsPublic: true }) return null;

        Type? elementType = NavElementClrType(navProp);
        if (elementType is null || elementType != ownerType) return null; // $levels needs a true self-reference

        bool isCollection = navProp.PropertyType != elementType;
        if (isCollection &&
            !navProp.PropertyType.IsAssignableFrom(typeof(List<>).MakeGenericType(elementType)))
        {
            return null;
        }

        return new ExpandNavBinding(navProp, isCollection, elementType);
    }

    // #206 phase 2: true when <paramref name="type"/> declares a public property that navigates
    // back to <paramref name="target"/> (or a base/interface in target's hierarchy) — i.e. a
    // navigation that would close a serialization cycle IF the related entity were ever handed to
    // the serializer bare (untransformed by a fresh-POCO projection). The assignability check is
    // intentionally broadened in BOTH directions on the property type AND the collection element
    // type (adversarial-review hardening): a back-reference need not be the exact TModel — a base
    // class or interface that TModel implements (or that is assignable from TModel) also closes a
    // cycle. Over-matching here only forces a safe fallback, never incorrect data, so the
    // conservative direction is correct. Implementation UNCHANGED by #323 — only its callers'
    // interpretation of a `true` result changed: BuildExpandNavBinding's guard (Change B) consults it
    // only for an element type that is NOT member-init-projectable (a projectable type is always
    // materialized through a fresh POCO — Change A — so a back-reference there can no longer close a
    // cycle) and is still load-bearing for THAT class post-#325/#326 (belt-and-suspenders alongside
    // SerializeBounded, and still the only thing that decides pushdown ELIGIBILITY in the first
    // place — SerializeBounded only makes the RESULT safe to serialize once a shape is pushed). The
    // #305 Include fallback's OWN former use of this method (FindCyclicLeafExpand, Change C) was
    // REMOVED by #325/#326 (Option B) — see the removal note below FindLevelsExpand,
    // immediately preceding ApplyIncludeFallback.
    private static bool TypeHasNavigationTo(Type type, Type target)
    {
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (target.IsAssignableFrom(p.PropertyType) || p.PropertyType.IsAssignableFrom(target)) return true;
            Type? elem = NavElementClrType(p);
            if (elem is not null && elem != p.PropertyType &&
                (target.IsAssignableFrom(elem) || elem.IsAssignableFrom(target)))
            {
                return true;
            }
        }
        return false;
    }

    // #206 phase 2: expand pushdown reads related rows through the LINQ provider (a projection with
    // .ToList()), which only actually loads navigation data for an EF Core provider. On a
    // LINQ-to-objects (or any non-EF) provider the same projection would read un-populated CLR
    // navigations and return empty/null data, so pushdown is gated to EF Core queryables and every
    // other provider takes the (delegate-less → EDM-only) fallback path.
    //
    // #305 fold-in: also doubles as the EF Core assembly resolver for the Path A Include fallback
    // below — the caller gates entry on the result being non-null (EF Core-backed) and reuses the
    // SAME resolved assembly to locate EntityFrameworkQueryableExtensions.Include by REFLECTION: this
    // package has no compile-time dependency on Microsoft.EntityFrameworkCore, so the Include-fallback
    // machinery must locate EF Core's own extension methods off whatever assembly the host app
    // actually loaded, never a `using Microsoft.EntityFrameworkCore;` reference. Previously this walk
    // ran twice per request (once as a bool-returning gate, once again to fetch the assembly); now it
    // runs once and the result is threaded through.
    internal static Assembly? ResolveEfCoreAssembly(IQueryable query)
    {
        for (Type? t = query.Provider.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Namespace is { } ns &&
                ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                return t.Assembly;
            }
        }
        return null;
    }

    // #616: MethodInfo.Invoke always wraps the callee's own exception. The caller's catch narrows on
    // the callee's REAL type and #494 classifies a provider fault by WHEN it was raised, so the
    // wrapper must reach neither.
    private static object InvokeUnwrapped(MethodInfo method, params object?[] args)
    {
        try
        {
            return method.Invoke(null, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    // #616: every root-to-LEAF path through one top-level expand's tree. EF's ThenInclude continues
    // from the navigation most recently included, so sibling branches cannot share one chain -- each
    // path is its own Include(...).ThenInclude(...) chain, and the shared prefix is re-issued.
    //
    // Re-issuing is safe only because the prefix is re-issued IDENTICALLY. Measured on EF Core 10:
    // repeating a filtered Include with the same predicate is accepted and the earlier chain's
    // ThenIncludes are preserved, while repeating one with a DIFFERENT predicate throws
    // InvalidOperationException ("The filters 'navigation...'"). A navigation appears once in the
    // engaged tree, so the second case is unreachable from here.
    //
    // Including a leaf includes its ancestors, so leaf paths alone cover the whole tree.
    private static void CollectExpandPaths(
        EngagedExpand node, List<EngagedExpand> prefix, List<IReadOnlyList<EngagedExpand>> paths)
    {
        prefix.Add(node);
        if (node.Children is { Count: > 0 })
        {
            foreach (EngagedExpand child in node.Children)
            {
                CollectExpandPaths(child, prefix, paths);
            }
        }
        else
        {
            paths.Add(prefix.ToArray());
        }

        prefix.RemoveAt(prefix.Count - 1);
    }

    // The CLR type a ThenInclude off this navigation binds its lambda parameter to.
    private static Type NextPreviousClrType(ExpandNavBinding nav) =>
        nav.IsCollection ? nav.ElementType : nav.Property.PropertyType;

    // #305 Path A: reflection handle for EF Core's
    // Include&lt;TEntity,TProperty&gt;(IQueryable&lt;TEntity&gt;, Expression&lt;Func&lt;TEntity,TProperty&gt;&gt;)
    // — the two-generic-parameter, lambda-based overload (EF Core also exposes a one-generic-parameter
    // STRING-path overload, excluded here by generic-arity). Cached per assembly: GetMethods()/LINQ
    // filtering is not free, and this resolves on the hot GetQueryable pushdown path whenever the root
    // projection is ineligible (see ApplyIncludeFallback).
    private static readonly ConcurrentDictionary<Assembly, MethodInfo?> s_efIncludeMethodCache = new();

    // #616: EF Core exposes TWO ThenInclude overloads, distinguished only by the shape of the
    // PREVIOUS navigation -- IIncludableQueryable<TEntity, IEnumerable<TPrev>> after a collection,
    // IIncludableQueryable<TEntity, TPrev> after a reference. Picking the wrong one is a runtime
    // failure, not a compile error, so the collection one is selected by testing that second type
    // argument for IEnumerable<> rather than by position in GetMethods(), which has no defined order.
    private static readonly ConcurrentDictionary<Assembly, (MethodInfo? Collection, MethodInfo? Reference)>
        s_efThenIncludeMethodCache = new();

    internal static (MethodInfo? Collection, MethodInfo? Reference) ResolveEfThenIncludeMethods(
        Assembly efAssembly) =>
        s_efThenIncludeMethodCache.GetOrAdd(efAssembly, static asm =>
        {
            Type? ext = asm.GetType("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
            MethodInfo[] candidates = ext?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "ThenInclude" && m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 3 && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.IsGenericType &&
                    m.GetParameters()[0].ParameterType.GetGenericArguments().Length == 2)
                .ToArray() ?? Array.Empty<MethodInfo>();

            static bool PreviousIsCollection(MethodInfo m)
            {
                Type prev = m.GetParameters()[0].ParameterType.GetGenericArguments()[1];
                return prev.IsGenericType && prev.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            }

            return (candidates.FirstOrDefault(PreviousIsCollection),
                    candidates.FirstOrDefault(m => !PreviousIsCollection(m)));
        });

    internal static MethodInfo? ResolveEfIncludeMethod(Assembly efAssembly) =>
        s_efIncludeMethodCache.GetOrAdd(efAssembly, static asm =>
        {
            Type? ext = asm.GetType("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
            return ext?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Include" && m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.IsGenericType &&
                    m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IQueryable<>));
        });

    // #305 fold-in (review): the FIRST top-level engaged expand that carries a nested $expand or
    // $levels — the scope ApplyIncludeFallback below does not serve (see its remarks). Checked by the
    // caller BEFORE invoking ApplyIncludeFallback, and OUTSIDE the try/catch that wraps the actual
    // Include construction+execution, so this validation's specific/actionable ODataException message
    // reaches the client verbatim (via the route's outer ODataException handler) instead of being
    // caught and overwritten by the generic provider-failure catch around the real Include call.
    // #616 narrowed this from "nested $expand OR $levels" to $levels alone: a nested $expand is now
    // served by chaining ThenInclude (see ApplyIncludeFallback). $levels stays refused because its
    // depth is decided per request by the data, so there is no statically-known nesting to build a
    // chain from -- and the projection path's own $levels support (BuildLevelsNavAccess) works by
    // emitting a Select per level, which filtered Include does not accept.
    internal static EngagedExpand? FindLevelsExpand(IReadOnlyList<EngagedExpand> engaged)
    {
        foreach (EngagedExpand e in engaged)
        {
            if (e.Levels > 0) return e;
            if (e.Children is { Count: > 0 } && FindLevelsExpand(e.Children) is { } nested) return nested;
        }
        return null;
    }

    // #323 (Change C) formerly guarded ApplyIncludeFallback below with a FindCyclicLeafExpand check
    // that rejected (400) any leaf whose element type navigated back to the root model — Include
    // populates TRACKED entities, so EF's own relationship fixup can wire up a back-reference the
    // member-init projection path's Change A structurally forecloses. #325/#326 (OWNER DECISIONS,
    // FROZEN spec — Option B) REMOVED that guard: the same clause-bounded serialization walker
    // (SerializeBounded) that fixes #325's plain-GET tracked-entity cycle makes the Include
    // fallback's tracked-entity graph safe to serve too, regardless of which two instances the
    // cycle closes between — a back-reference to the root (what Change C caught), a sibling
    // cross-reference, or a self-referential leaf element type (#326's two previously-still-500
    // classes) are all now served correctly. Rejecting a request the framework can now answer
    // would be backwards relative to the #305/#323 "serve, don't silently drop or reject" direction.
    // See IncludeFallbackSqliteTests.cs's IncludeFallbackCyclicLeafTests for the coverage (flipped
    // from asserting 400 to asserting real served data).

    // #305 Path A: populate the engaged $expand navigations via EF's own Include when the root
    // projection is ineligible for a member-init Select. Before #305 those dropped to EDM-only under a
    // 200, serializing the CLR default -- typically an empty collection -- as silently wrong data.
    // Resolved by reflection off the SAME EF assembly ResolveEfCoreAssembly already confirmed.
    //
    // MaxExpandTop bounds materialization exactly as on the member-init path: the same ApplyNavShape
    // windowing, never "load all then trim".
    //
    // SCOPE, a documented deviation from the settled design: LEAF engaged expands only. The caller has
    // already rejected nested $filter/$orderby and nested $expand/$levels before calling. Those fail
    // loud rather than risk a reflection-built ThenInclude chain, which is materially riskier here than
    // on the member-init path: EF's navigation fixup can wire up a tracked self-referential navigation
    // beyond the requested depth even when it was never Include'd, which fresh POCOs never risk.
    //
    // #325/#326: a cyclic back-reference is now SERVED rather than rejected -- SerializeBounded makes
    // the tracked graph safe whichever two instances close the cycle.
    internal static IQueryable<TModel> ApplyIncludeFallback<TModel>(
        IQueryable<TModel> query, IReadOnlyList<EngagedExpand> engaged, MethodInfo includeMethod,
        (MethodInfo? Collection, MethodInfo? Reference) thenIncludeMethods,
        IEdmModel model, int? maxExpandTop, ODataQuerySettings binderSettings)
        where TModel : class
    {
        var paths = new List<IReadOnlyList<EngagedExpand>>();
        foreach (EngagedExpand e in engaged)
        {
            CollectExpandPaths(e, new List<EngagedExpand>(), paths);
        }

        foreach (IReadOnlyList<EngagedExpand> path in paths)
        {
            EngagedExpand e = path[0];
            ParameterExpression owner = Expression.Parameter(typeof(TModel), "x");
            Expression access = Expression.Property(owner, e.Binding.Property);
            if (e.Binding.IsCollection)
            {
                // #529: a nested $filter/$orderby is bound here rather than refused. EF Core's
                // filtered Include accepts exactly the Where -> OrderBy/ThenBy -> Skip/Take sequence
                // ApplyNavShape composes, so this is the same shaping the member-init projection path
                // applies -- through the same BindNavShape, never a second transcription of it.
                //
                // #616: a level with children composes NO SQL window here, and that is what keeps the
                // untranslatable shape unreachable rather than merely unlikely -- ApplyNavShape gates
                // the #298 count bound, the #313 default leaf bound and #304's explicit $skip/$top on
                // isProjectionLeaf, so such a level gets Where/OrderBy only and its window moves to
                // the JSON pass. Measured on EF Core 10 / SQLite: a windowed parent beside a nested
                // collection throws "Translating this query requires the SQL APPLY operation, which is
                // not supported on SQLite"; Where/OrderBy on the parent translates.
                NavShapeBindings bound = BindNavShape(e.Filter, e.OrderBy, e.Binding.ElementType, model, binderSettings);
                access = ApplyNavShape(access, e, e.Binding.ElementType, model, bound, maxExpandTop);
            }

            LambdaExpression lambda = Expression.Lambda(access, owner);
            MethodInfo closedInclude = includeMethod.MakeGenericMethod(typeof(TModel), access.Type);
            object chain = InvokeUnwrapped(closedInclude, query, lambda);
            Type previousClrType = NextPreviousClrType(e.Binding);

            for (int depth = 1; depth < path.Count; depth++)
            {
                EngagedExpand child = path[depth];
                ParameterExpression parent = Expression.Parameter(previousClrType, "p");
                Expression childAccess = Expression.Property(parent, child.Binding.Property);
                if (child.Binding.IsCollection)
                {
                    NavShapeBindings childBound =
                        BindNavShape(child.Filter, child.OrderBy, child.Binding.ElementType, model, binderSettings);
                    childAccess = ApplyNavShape(
                        childAccess, child, child.Binding.ElementType, model, childBound, maxExpandTop);
                }

                MethodInfo? thenInclude = child.Binding.IsCollection
                    ? thenIncludeMethods.Collection
                    : thenIncludeMethods.Reference;
                if (thenInclude is null)
                {
                    throw new Microsoft.OData.ODataException(
                        $"The nested '$expand' on '{child.Binding.Property.Name}' could not be " +
                        "processed: the underlying provider does not expose a usable ThenInclude API. " +
                        "Write an expand delegate for this navigation instead.");
                }

                chain = InvokeUnwrapped(
                    thenInclude.MakeGenericMethod(typeof(TModel), previousClrType, childAccess.Type),
                    chain, Expression.Lambda(childAccess, parent));
                previousClrType = NextPreviousClrType(child.Binding);
            }

            query = (IQueryable<TModel>)chain;
        }
        return query;
    }
}
