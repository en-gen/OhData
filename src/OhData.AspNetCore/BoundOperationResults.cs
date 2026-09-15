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

// Extracted from OhDataEndpointFactory (#671 phase 1, pure move): what a bound operation's result
// looks like on the wire and what bounds it -- the runtime envelope shape (collection, single
// entity, Edm primitive), the MaxTop ceiling and the $skip continuation applied to a collection
// result, and the OpenAPI response and paging metadata an operation route advertises (the
// query-parameter marker is shared with the unbound functions). The routes themselves are mapped in
// OhDataEndpointFactory, which calls this as BoundOperationResults.Foo(...).
internal static class BoundOperationResults
{
    // Leg 3 (docs-fidelity): a bound function/action's success response goes through
    // WrapBoundOpResult (see below), which chooses one of three shapes at runtime based on the
    // operation's actual return value: an IEnumerable<TModel> result gets the collection
    // envelope, a TModel result gets the single-entity envelope (documented as bare TModel,
    // mirroring the GetById precedent), and anything else is returned largely as-is. Mirror
    // that same dispatch here, using BoundOperationDefinition.ReturnType (the delegate's
    // declared, Task/ValueTask-unwrapped return type, computed once at bind time) so the
    // documented schema matches what WrapBoundOpResult will actually produce.
    internal static void AddBoundOperationProduces<TModel>(RouteHandlerBuilder rb, BoundOperationDefinition op)
        where TModel : class
    {
        Type? returnType = op.ReturnType;
        if (returnType is not null)
        {
            if (returnType == typeof(TModel))
            {
                rb.Produces<TModel>(200);
            }
            else if (DeclaresCollectionOf(returnType, typeof(TModel)))
            {
                rb.Produces<ODataCollectionResponse<TModel>>(200);
            }
            else
            {
                rb.Produces(200, returnType, "application/json");
            }
        }
        rb.Produces(204);
    }

    // #497: the SAME element predicate WrapBoundOpResult applies at runtime — assignability, not
    // equality — so a delegate declared `Task<List<TDerived>>` is documented as the collection
    // envelope it will actually be served in, rather than as a bare List<TDerived>. One predicate,
    // now literally one method, because #357 gave it a THIRD consumer: the $top/$skip metadata a
    // collection-returning bound function carries. Three copies of a rule about which shape an
    // operation produces is how an advertise-vs-serve divergence gets built.
    private static bool DeclaresCollectionOf(Type declaredReturnType, Type modelType) =>
        declaredReturnType != typeof(string)
        && typeof(System.Collections.IEnumerable).IsAssignableFrom(declaredReturnType)
        && declaredReturnType.GetInterfaces().Concat(new[] { declaredReturnType })
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                      && modelType.IsAssignableFrom(i.GetGenericArguments()[0]));

    // #357/#467: a bound operation whose DECLARED return type is a collection of the set's own type
    // honours $top/$skip and caps at MaxTop, so -- and only so -- its route documents them. Every
    // other field is false: #467's rule is that a field means "this route honours this option", never
    // "this route is of kind X".
    //
    // Attached at the ONE metadata site, as #467 requires: all three OpenAPI companion packages read
    // this record, and a rule transcribed into them separately is how the same wrong document
    // appeared in all three before.
    //
    // #543: bound ACTIONS are attached too. They were excluded on reasoning that is sound about the
    // CONTINUATION -- a nextLink is GET-addressable and POST is not -- and was wrongly taken to be
    // about the CEILING as well. An action now honours $top/$skip and validates against MaxTop; the
    // only thing it cannot do is emit a nextLink.
    internal static void AddBoundOperationPagingMetadata<TModel>(
        RouteHandlerBuilder rb, BoundOperationDefinition op, IEntitySetEndpointSource source)
        where TModel : class
    {
        if (op.ReturnType is not { } returnType) return;
        if (!DeclaresCollectionOf(returnType, typeof(TModel))) return;

        rb.WithMetadata(new OhDataQueryOptionsMetadata(
            FilterEnabled: false,
            OrderByEnabled: false,
            SelectEnabled: false,
            ExpandEnabled: false,
            CountEnabled: false,
            SearchEnabled: false,
            MaxTop: source.MaxTop,
            TopSkipSupported: true));
    }

    // Issue #181: build the query-parameter documentation marker for a bound/unbound *function*.
    // Each of these parameters is read from the query string at request time (see the function
    // registration loops), but the handler binds no minimal-API parameters, so ApiExplorer would
    // otherwise see none of them and the OpenAPI document would list "parameters: []". A trailing
    // CancellationToken is already excluded from Parameters by BoundOperationDefinition.From /
    // UnboundOperationDefinition.From. For entity-level functions the leading key parameter
    // (Parameters[0]) is a route parameter already documented via BindingSource.Path, so it is
    // skipped here. Returns null when there is nothing to document.
    internal static OhDataQueryParametersMetadata? BuildFunctionQueryParametersMetadata(
        ParameterInfo[] parameters, bool skipKey)
    {
        int start = skipKey ? 1 : 0;
        if (parameters.Length <= start) return null;

        var list = new List<OhDataQueryParameter>(parameters.Length - start);
        for (int i = start; i < parameters.Length; i++)
        {
            var p = parameters[i];
            list.Add(new OhDataQueryParameter
            {
                Name = p.Name!,
                Type = p.ParameterType,
                IsRequired = !p.HasDefaultValue,
            });
        }

        return new OhDataQueryParametersMetadata { Parameters = list };
    }

    // Wraps a bound-operation result in @odata.context when the return type matches TModel (§11.5.3);
    // primitives and other types return directly.
    //
    // #357: the operation-result twin of ApplyGetAllPaging, deliberately equivalent to it. A second
    // implementation because that one is a local function closing over the route's ODataQueryOptions,
    // which this route does not build. $top/$skip are read the way the NAVIGATION-collection route
    // reads them -- int.TryParse and that route's existing wording -- because building an
    // ODataQueryOptions here would start rejecting malformed values of options this route still
    // ignores. The MaxTop message IS shared verbatim with the collection routes: one condition must
    // not produce two envelopes depending on which route reached the same entity set.
    //
    // #543: `continuable` changes exactly one branch -- no $top sent and the result exceeds the
    // ceiling. A bound FUNCTION caps and emits a $skip nextLink; a bound ACTION THROWS, because
    // truncation and continuation are both off the table (see below).
    private static bool TryApplyOperationCollectionPaging(
        HttpContext ctx, IEntitySetEndpointSource startupSource, bool continuable, object[] items,
        string entitySetName, string operationName,
        out object[] page, out string? nextLink, out IResult? error)
    {
        page = items;
        nextLink = null;
        error = null;

        int skip = 0;
        if (ctx.Request.Query.TryGetValue("$skip", out var skipStr))
        {
            if (!int.TryParse(skipStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out skip) || skip < 0)
            {
                error = OhDataEndpointFactory.ODataError(400, "InvalidQueryOption",
                    $"The value of '$skip' ('{skipStr}') is invalid. It must be a non-negative integer.");
                return false;
            }
        }

        int? top = null;
        if (ctx.Request.Query.TryGetValue("$top", out var topStr))
        {
            if (!int.TryParse(topStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int topVal) || topVal < 0)
            {
                error = OhDataEndpointFactory.ODataError(400, "InvalidQueryOption",
                    $"The value of '$top' ('{topStr}') is invalid. It must be a non-negative integer.");
                return false;
            }
            top = topVal;

            // MaxTop caps an EXPLICIT $top exactly as the three collection read paths do, with the
            // identical message.
            if (startupSource.MaxTop.HasValue && topVal > startupSource.MaxTop.Value)
            {
                error = OhDataEndpointFactory.ODataError(400, "InvalidQueryOption",
                    $"The value of '$top' ({topVal}) exceeds the maximum allowed value ({startupSource.MaxTop.Value}).");
                return false;
            }
        }

        long preTotal = items.Length;
        IEnumerable<object> seq = items;
        if (skip > 0) seq = seq.Skip(skip);

        int? appliedPageSize = null;
        if (top is int t)
        {
            // An explicit $top bounds the response at the CLIENT's request, so there is nothing
            // silent about the truncation and nothing to continue -- true on the collection GET and
            // the bound function too, both of which also omit the nextLink in this branch. This is
            // therefore the one branch an action needs no special treatment in, and it is also the
            // remedy a client has for the refusal below.
            seq = seq.Take(t);
        }
        else if (continuable)
        {
            int? preferredPageSize = OhDataEndpointFactory.ParseMaxPageSize(ctx);
            appliedPageSize = preferredPageSize.HasValue
                ? (startupSource.MaxTop.HasValue
                    ? Math.Min(preferredPageSize.Value, startupSource.MaxTop.Value)
                    : preferredPageSize.Value)
                : startupSource.MaxTop;
            if (appliedPageSize.HasValue) seq = seq.Take(appliedPageSize.Value);
            if (preferredPageSize.HasValue)
                ctx.Response.Headers["Preference-Applied"] = $"{OhDataEndpointFactory.MaxPageSizePreference}={appliedPageSize!.Value}";
        }
        else if (startupSource.MaxTop is int cap && preTotal - skip > cap)
        {
            // #543: a bound ACTION whose result does not fit under the ceiling. Measured pre-fix,
            // MaxTop=10 over 25 rows: the FUNCTION capped at 10 with a $skip link and 400'd $top=999,
            // while the ACTION answered 200 len=25 with no link for every one of $top=999, $top=5,
            // $skip=20 and $top=abc. #357's "moot in practice" defence was refuted by Task<object>,
            // and #539 makes Task<IEnumerable<TModel>> the ordinary spelling.
            //
            // Three shapes, two unavailable here:
            //   * Cap and emit a nextLink -- INVALID. §11.2.5.7 defines a next link as one that
            //     "allows RETRIEVING the next partial set", and POST /Set/Action is not
            //     GET-addressable, so the link would 405. (NOT the withdrawn §11.5.4 claim; see #566.)
            //   * Cap silently -- forbidden by M1.
            //   * Refuse. What is left.
            //
            // A 500, not a 400, for #496's reason: the condition is decided entirely by server-side
            // state -- the profile declared the ceiling, the handler exceeded it, identically for
            // every request -- so blaming the client would be the defect #496 removed one route over.
            // A client CAN still get a served response with an explicit $top <= MaxTop, which is why
            // the branch above is not also refused.
            throw new InvalidOperationException(
                $"Entity set '{entitySetName}': bound action '{operationName}' returned " +
                $"{preTotal - skip} entities, which exceeds this entity set's MaxTop of {cap}. A " +
                "bound action's result cannot carry an @odata.nextLink -- a next link is a URL " +
                "the client GETs (OData Protocol 11.2.5.7) and POST is not a continuation -- so the " +
                "framework will not silently truncate it either. Return no more than MaxTop " +
                "entities from the handler, set MaxTop = null on the profile or in " +
                "EntitySetDefaults to opt this entity set out of the ceiling, or expose the " +
                "operation as a bound FUNCTION (BindFunction), which is pageable.");
        }

        page = ReferenceEquals(seq, items) ? items : seq.ToArray();

        // nextLink only when the DEFAULT cap was applied (omitted $top) and more rows remain beyond
        // this page -- the pre-paging total decides it exactly, so an exactly-full final page does
        // not walk a client into an empty trailing one.
        if (appliedPageSize is int ps && ps > 0 && skip + page.Length < preTotal)
            nextLink = OhDataEndpointFactory.BuildNextPageLinkWithSkip(ctx, skip + page.Length);

        return true;
    }

    internal static IResult WrapBoundOpResult(
        HttpContext ctx, string prefix, string entitySetName, object result, Type modelType,
        JsonSerializerOptions? jsonOptions, IEdmEntityType? rootEdmType, IEdmModel? edmModel,
        IEntitySetEndpointSource source, IEntitySetEndpointSource startupSource,
        string operationName, bool continuable)
    {
        var resultType = result.GetType();

        // #497 (the #462 defect class, fourth site): the element test used to be `== modelType`, exact
        // CLR equality, while the single-entity branch below already accepted a derived instance via
        // IsAssignableFrom. A handler declared Task<IEnumerable<TModel>> returning a List<TDerived> --
        // the ordinary TPH shape -- lists only IEnumerable<TDerived>, so it missed this branch, missed
        // the single-entity branch (IsAssignableFrom fails on a List), missed the primitive map, and
        // fell into the raw-graph PreRenderedJson. Measured: a bare array with no @odata.context, no
        // `value` envelope, the declared navigation served INLINE and no @odata.etag -- while the same
        // handler returning List<TModel> got the full envelope. A cyclic derived graph 500'd.
        //
        // Assignability cannot over-match: modelType is the set's own TModel, never object, and every
        // arm this could steal from tests for an element assignable to TModel. The string guard stays
        // because string is IEnumerable<char>.
        //
        // AddBoundOperationProduces carries the SAME predicate over the DECLARED return type, so the
        // document and the wire cannot disagree -- changing one without the other is the
        // advertise-vs-serve half of #497.
        bool isCollectionOfModel = false;
        if (resultType != typeof(string))
        {
            foreach (var iface in new[] { resultType }.Concat(resultType.GetInterfaces()))
            {
                if (iface.IsGenericType
                    && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    && modelType.IsAssignableFrom(iface.GetGenericArguments()[0]))
                {
                    isCollectionOfModel = true;
                    break;
                }
            }
        }

        if (isCollectionOfModel)
        {
            // Materialize the enumerable to an array so JSON serialization works correctly.
            // Cast via non-generic IEnumerable since the concrete type is IEnumerable<TModel>
            // not IEnumerable<object>.
            object[] coll = ((IEnumerable)result).Cast<object>().ToArray();
            string baseUrl = OhDataEndpointFactory.BuildBaseUrl(ctx, prefix);

            // #357: bound the collection exactly as #201 bounds GetAll -- before this, an operation
            // returning a collection of the set's own type bypassed MaxTop, $top/$skip and paging
            // entirely (measured: 77 entities for `?$top=2`). The situation is identical to GetAll's:
            // a fully materialized array with the framework owning the pipeline, so an offset
            // continuation is always honourable.
            //
            // Parsed HERE, in the runtime branch, not in the route: a handler declared Task<object>
            // returning a List<TModel> must not be a way around the ceiling. Metadata comes from the
            // DECLARED type, so the only divergence is serving a bound the document did not promise --
            // the safe direction.
            //
            // #543: #357 then excluded ACTIONS at the route, which put the bypass back on the one
            // kind that never reached this parse. Measured: a BindAction served all 25 rows against
            // MaxTop=10 and ignored $top. Actions reach the parse now; they just cannot emit a
            // nextLink.
            //
            // `startupSource` is load-bearing. `source` is the REQUEST-scoped instance, whose MaxTop
            // is null -- profiles are AddScoped and _resolvedMaxTop is assigned in VisitModelBuilder,
            // which only runs on the startup instance. Reading the ceiling off the request instance
            // silently disables the whole bound. `continuable` is a SEPARATE flag because conflating
            // "no continuation available" with "no ceiling applies" is how it became bypassable.
            string? opNextLink = null;
            if (!TryApplyOperationCollectionPaging(
                    ctx, startupSource, continuable, coll, entitySetName, operationName,
                    out coll, out opNextLink, out IResult? pagingError))
            {
                return pagingError!;
            }

            // #179: route the collection through the same serialize → ETag → omit-navs stages the
            // normal collection GET uses (ApplyCollectionPipelineAsync). A bound op returns the
            // entity set's own type but takes no $expand, so every declared navigation is omitted
            // (§4.5.1 / §11.2.4.2) and @odata.etag is injected per item when UseETag is set —
            // previously the raw CLR graph was handed to Results.Ok, leaking navs and dropping ETags.
            // #325/#326 (Option B): bounded (clause: null — a bound op takes no $expand), never
            // whole-graph, so a bound function/action returning tracked/cyclic entities is safe too.
            var serializerOptions = jsonOptions ?? OhDataEndpointFactory._pascalCaseSerializerOptions;
            var json = new JsonArray();
            foreach (object item in coll)
            {
                json.Add(ExpandEngine.SerializeBounded(item, rootEdmType, edmModel, clause: null, serializerOptions));
            }
            if (source.HasETag)
            {
                ExpandEngine.InjectETagsIntoJsonArray(json, coll, source);
            }
            // Defence-in-depth (#325/#326): practical no-op now.
            ExpandEngine.OmitUnexpandedNavigations(json, rootEdmType, clause: null, modelType, serializerOptions);

            // #495: rendered here rather than deferred to Results.Ok. The JsonArray above is
            // already materialized, but the envelope AROUND it was not: it is a
            // Dictionary<string, object?>, so the host's DictionaryKeyPolicy rewrote
            // `@odata.context`/`value` (measured: `@ODATA.CONTEXT`/`VALUE`), and the write happened
            // after the filter chain unwound. Rendering with the registration's owned options keeps
            // the envelope keys contractual while the payload inside the array still honours the
            // host's converters/encoder exactly as it did (#252) -- those already ran, above.
            var opEnvelope = new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{baseUrl}/$metadata#{entitySetName}",
            };
            // #357: between @odata.context and value, matching every other collection envelope the
            // framework emits (JSON Format §4.5 -- annotations precede what they describe).
            if (opNextLink is not null) opEnvelope["@odata.nextLink"] = opNextLink;
            opEnvelope["value"] = json;
            return OhDataEndpointFactory.PreRenderedJson(opEnvelope, OhDataEndpointFactory.EnvelopeOptions(serializerOptions));
        }

        if (resultType == modelType || modelType.IsAssignableFrom(resultType))
        {
            // #179: a single-TModel bound-op result rides the same omission + ETag path as GetById
            // so its shape matches a top-level read — un-expanded navigations stripped (§4.5.1 /
            // §11.2.4.2) and @odata.etag injected when UseETag is set.
            string? boundOpEtag = source.HasETag ? source.InvokeGetETag(result) : null;
            return OhDataEndpointFactory.ODataEntityResult(ctx, prefix, entitySetName, result, jsonOptions, edmModel,
                etag: boundOpEtag, omitNavsForType: rootEdmType);
        }

        // m5: primitive results get the JSON §11 individual-value envelope
        // ({"@odata.context":"...#Edm.<Type>","value":<primitive>}). Only types this framework
        // can confidently name as an Edm primitive are wrapped; anything else (a non-TModel
        // complex/DTO type) falls through unwrapped rather than risk asserting a wrong Edm type.
        Type underlyingResultType = Nullable.GetUnderlyingType(resultType) ?? resultType;
        if (s_edmPrimitiveTypeNames.TryGetValue(underlyingResultType, out string? edmTypeName))
        {
            string primitiveBaseUrl = OhDataEndpointFactory.BuildBaseUrl(ctx, prefix);
            // #495: this branch is the one #396 listed as a knowing residual ("only a host-
            // registered converter for a primitive type could fault there"). It can: measured with
            // a host JsonConverter<decimal> that throws, the client got an empty, envelope-less 500
            // with nothing logged, because Results.Ok deferred the write past the filter chain.
            // Pre-rendering moves that converter call inside the filter's scope, so a throwing one
            // now produces the logged 500 envelope like any other handler fault -- and a merely
            // reformatting one still reformats, because `value` here IS payload and the host owns
            // value formatting (#252). Only the envelope's keys are OhData's.
            return OhDataEndpointFactory.PreRenderedJson(new Dictionary<string, object?>
            {
                ["@odata.context"] = $"{primitiveBaseUrl}/$metadata#{edmTypeName}",
                ["value"] = result
            }, OhDataEndpointFactory.EnvelopeOptions(jsonOptions ?? OhDataEndpointFactory._pascalCaseSerializerOptions));
        }

        // Primitive/other (e.g. a non-TModel DTO) — no context wrapping. Serialize through the
        // owned options so its property names follow OhData's casing (#252) rather than leaking the
        // host's HttpJsonOptions naming policy via the ASP.NET Core Results.Ok serialization path.
        // #396: this is the one branch of this method that hands an arbitrary CLR graph to the
        // serializer — the two above emit a materialized JsonArray or an Edm primitive — so it is
        // the branch that has to serialize inside the filter's scope. See PreRenderedJson.
        return OhDataEndpointFactory.PreRenderedJson(result, jsonOptions ?? OhDataEndpointFactory._pascalCaseSerializerOptions);
    }

    // m5: CLR type -> Edm primitive type name, used to build the individual-value response
    // envelope for bound operations that return a bare primitive (JSON §11). Deliberately not
    // exhaustive of every Edm primitive kind — only the CLR types this framework's parameter/
    // return-type conversion already supports elsewhere (see the query-string/JSON-body
    // parameter converters in OhDataEndpointFactory).
    private static readonly Dictionary<Type, string> s_edmPrimitiveTypeNames = new()
    {
        [typeof(string)] = "Edm.String",
        [typeof(bool)] = "Edm.Boolean",
        [typeof(byte)] = "Edm.Byte",
        [typeof(sbyte)] = "Edm.SByte",
        [typeof(short)] = "Edm.Int16",
        [typeof(int)] = "Edm.Int32",
        [typeof(long)] = "Edm.Int64",
        [typeof(float)] = "Edm.Single",
        [typeof(double)] = "Edm.Double",
        [typeof(decimal)] = "Edm.Decimal",
        [typeof(Guid)] = "Edm.Guid",
        // OData v4 has no "DateTime" primitive; both CLR DateTime and DateTimeOffset map to
        // Edm.DateTimeOffset, matching FormatRawValue's ("o") treatment of the two types above.
        [typeof(DateTime)] = "Edm.DateTimeOffset",
        [typeof(DateTimeOffset)] = "Edm.DateTimeOffset",
        [typeof(DateOnly)] = "Edm.Date",
        [typeof(TimeOnly)] = "Edm.TimeOfDay",
        [typeof(TimeSpan)] = "Edm.Duration",
        [typeof(byte[])] = "Edm.Binary",
    };
}
