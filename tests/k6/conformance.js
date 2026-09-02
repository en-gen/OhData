// OhData conformance matrices, over real HTTP against the containerized TestBench.
//
// smoke.js takes one case per route family. This file takes the volume that would drown it, and
// it is driven from TABLES rather than copy-paste so a rule that moves in one place fails in one
// place. What is here, and why it is here rather than in an xUnit suite:
//
//   * The 501/400 taxonomy, every route family x every system query option. The tables mirror
//     `s_*ImplementedOptions` in src/OhData.AspNetCore/OhDataEndpointFactory.cs one for one:
//     an option listed there gets its per-route expected status, everything else is derived as
//     501. Narrowing one of those arrays therefore breaks a check here rather than silently
//     changing the wire.
//   * §11.2.9's /$count contract, asserted on the NUMBER. A status-only assertion passes even
//     when an option that MUST NOT affect the count was applied.
//   * Content negotiation, conditional requests, Prefer, 413 and the OData-Version header --
//     the things WebApplicationFactory/TestServer cannot exercise honestly. RequestBodySize-
//     FeatureTests exists precisely because TestHost supplies no IHttpMaxRequestBodySizeFeature
//     at all, and #496 found the 413 shipping WITHOUT OData-Version because of middleware
//     ordering. Both are one HTTP hop away from being provable here, and zero hops away
//     in-process.

import { group, check, fail } from 'k6';
import {
  BASE_URL, SEEDED_MOVIE_ID, SEEDED_MOVIE_CAST_COUNT, YEAR_1994, YEAR_1994_COUNT,
  MISSING_ID, SEEDED_GENRE_CODE, UNLINKED_ACTOR_ID, MOVIE_MAX_TOP, newMovie,
} from './lib/seed.js';
import {
  get, post, put, patch, del, req, jsonParams, header, body, expectStatus, expectError,
  expectUnsupportedOption, q, reports,
} from './lib/odata.js';

export const options = {
  thresholds: {
    // http_req_failed is omitted for the reason smoke.js gives, and more strongly: most of this
    // file's requests are SUPPOSED to be 4xx/5xx -- the taxonomy matrix alone is ~100 refusals.
    // A per-request failure rate says nothing here; `checks` is the signal.
    //
    // rate==1.00 is what makes this file able to fail the build. A failing check() does not by
    // itself fail a k6 run, and a fractional threshold would let a regression that breaks a
    // handful of the ~450 checks below ship green.
    'checks': ['rate==1.00'],
  },
};

// ─────────────────────────────────────────────────────────────────────────────
// The system query options this suite probes, with a value that is syntactically well-formed
// (so a refusal is provably the sigil rule and not a parse failure) and non-empty (an EMPTY
// value is #402's construction guard, a different rule with a different answer -- see the
// "malformed values" group below, which tests it deliberately).
const PROBE = {
  '$filter': `$filter=${q(`Year eq ${YEAR_1994}`)}`,
  '$orderby': '$orderby=Id',
  '$top': '$top=1',
  '$skip': '$skip=1',
  '$select': '$select=Id',
  '$expand': '$expand=Cast',
  '$count': '$count=true',
  '$search': '$search=alpha',
  '$skiptoken': '$skiptoken=abc',
  '$format': '$format=json',
  '$apply': `$apply=${q('groupby((Year))')}`,
  '$compute': `$compute=${q('Year as Y')}`,
  '$index': '$index=1',
  '$deltatoken': '$deltatoken=x',
  // Not a system option in any OData version. Refused by the SIGIL, fail-closed, so a system
  // option added to a future version cannot quietly start being dropped.
  '$unknown': '$unknown=1',
  // A typo of a real one. #359 reported "$Select is applied while $slect is ignored and neither
  // is rejected" as one inconsistency; it is resolved by rejecting $slect, never by starting to
  // reject $Select -- which the mixed-case control below pins.
  '$slect': '$slect=Id',
};
const ALL_PROBES = Object.keys(PROBE);

// The three refusal wordings that ride on FindUnsupportedSystemQueryOption, and only three.
// Asserting them separates a SIGIL refusal from a structural one that happens to share the 501
// and the code -- which is exactly what distinguishes the GetAll route's $filter (in the
// implemented array, refused earlier with its own remedy) from an option that is not in the
// array at all.
const WORDING = {
  generic: (opt) => `The query option '${opt}' is not supported.`,
  navCollection: (opt) => `This navigation route does not support ${opt}. Supported query options are`,
  navSingle: (opt) => `This navigation route does not support ${opt}. A single-valued navigation route supports no data`,
};

// Route families, each mirroring one `s_*ImplementedOptions` array.
//
// `implemented` is that array, with the status this particular route/profile answers for each
// entry -- 200 where the option applies, 400 where the route implements it and this profile
// switched it off ("won't"), and 501 for the two flag-INDEPENDENT structural refusals on the
// GetAll path ("can't", and refused with their own message before the sigil rule is reached).
// Everything NOT in `implemented` is derived as 501 + the route's wording.
const ROUTES = [
  {
    name: 'collection GET (GetQueryable)',
    url: `${BASE_URL}/v2/Movies`,
    wording: 'generic',
    implemented: {
      '$filter': 200, '$orderby': 200, '$top': 200, '$skip': 200, '$select': 200,
      '$expand': 200, '$count': 200,
      // Implemented leg, no Search handler configured -> 400 "won't", not 501.
      '$search': 400,
      // Implemented; 'abc' is a malformed VALUE -> 400, and its own code.
      '$skiptoken': 400,
      '$format': 200,
    },
  },
  {
    name: 'collection GET (GetAll)',
    url: `${BASE_URL}/v1/Genres`,
    wording: 'generic',
    values: {
      '$filter': `$filter=${q(`Code eq '${SEEDED_GENRE_CODE}'`)}`,
      '$orderby': '$orderby=Code',
      '$select': '$select=Code',
      '$expand': '$expand=Nothing',
    },
    implemented: {
      // 501 and flag-independent: this path has no IQueryable, so no profile setting turns it
      // on. Listed in s_getAllCollectionImplementedOptions all the same, because it is refused
      // EARLIER with a message naming GetQueryable as the remedy and one condition must not
      // produce two envelopes depending on which check saw it first -- asserted below.
      '$filter': 501, '$orderby': 501,
      '$top': 200, '$skip': 200,
      // GenreProfile enables no capability flags: "won't".
      '$select': 400, '$expand': 400, '$count': 400, '$search': 400,
      '$format': 200,
      // NOTE: $skiptoken is deliberately absent -- #201 continues this path with $skip and
      // nothing here ever reads a $skiptoken, so accepting one discarded a client's
      // continuation in silence. It is derived as 501 below, which is the assertion.
    },
    structuralRefusal: {
      '$filter': 'This resource does not support $filter or $orderby. Configure GetQueryable',
      '$orderby': 'This resource does not support $filter or $orderby. Configure GetQueryable',
    },
  },
  {
    name: 'entity-set /$count',
    url: `${BASE_URL}/v2/Movies/$count`,
    wording: 'generic',
    // §11.2.9 partitions the options: $filter AFFECTS the count (and is applied), while
    // $top/$skip/$orderby/$expand MUST NOT affect it (accepted and ignored). $select rides with
    // them by the clause's positive half. $search affects the count and this route cannot apply
    // it, so it is refused -- derived as 501 below.
    implemented: {
      '$filter': 200, '$top': 200, '$skip': 200, '$orderby': 200, '$expand': 200,
      '$select': 200, '$format': 200,
    },
  },
  {
    name: 'GetById',
    url: `${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})`,
    wording: 'generic',
    implemented: { '$select': 200, '$expand': 200, '$format': 200 },
  },
  {
    name: 'navigation collection',
    url: `${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Cast`,
    wording: 'navCollection',
    values: { '$expand': '$expand=Nothing' },
    implemented: {
      '$select': 200, '$orderby': 200, '$skip': 200, '$top': 200, '$count': 200, '$format': 200,
    },
  },
  {
    name: 'navigation single-valued',
    url: `${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Studio`,
    wording: 'navSingle',
    // ONE template, TWO handlers. The single-valued branch reads no query option at all, so it
    // implements $format and nothing else -- sharing the collection branch's set here accepted
    // and DISCARDED $select/$orderby/$top/$count under a 200.
    implemented: { '$format': 200 },
  },
  {
    name: 'navigation /$count',
    url: `${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Cast/$count`,
    wording: 'generic',
    values: { '$expand': '$expand=Nothing' },
    // The same §11.2.9 partition, resolved differently in one class: this handler invokes the
    // navigation delegate and counts what comes back, applying no data option whatsoever -- so
    // $filter is refused here where the entity-set /$count applies it.
    implemented: {
      '$top': 200, '$skip': 200, '$orderby': 200, '$expand': 200, '$select': 200, '$format': 200,
    },
  },
  {
    name: 'bound function',
    url: `${BASE_URL}/v2/Movies/TopRated?count=2`,
    wording: 'generic',
    // $top/$skip are listed UNCONDITIONALLY on the operation routes, not derived from the
    // declared return type: the ceiling is applied in the runtime collection branch, so such a
    // route really can emit a "$skip=N" continuation, and refusing them would refuse a link the
    // server itself had just issued.
    implemented: { '$top': 200, '$skip': 200, '$format': 200 },
  },
];

function probeUrl(route, option) {
  const qs = (route.values && route.values[option]) || PROBE[option];
  return route.url + (route.url.indexOf('?') >= 0 ? '&' : '?') + qs;
}

export function setup() {
  const p = jsonParams();
  const mk = (path, title) => {
    const r = post(`${BASE_URL}${path}`, JSON.stringify(newMovie({ title })), { params: p });
    check(r, { [`setup: ${title} created`]: (x) => x.status === 201 });
    return r.status === 201 ? JSON.parse(r.body).Id : null;
  };
  return {
    actionId: mk('/v1/Movies', 'K6ConfAction'),
    writeId: mk('/v1/Movies', 'K6ConfWrite'),
    etagId: mk('/v1/Movies', 'K6ConfEtag'),
    refId: mk('/v2/Movies', 'K6ConfRef'),
  };
}

export function teardown(data) {
  for (const id of [data.actionId, data.writeId, data.etagId, data.refId]) {
    if (id) del(`${BASE_URL}/v1/Movies(${id})`);
  }
}

export default function (data) {
  taxonomy();
  taxonomyBoundAction(data.actionId);
  nextLinkNeverEchoesARefusedOption();
  countContract();
  contentNegotiation();
  odataMaxVersion();
  malformedOptionValues();
  bodyNullability(data.writeId);
  odataBind(data.writeId);
  errorEnvelopes(data.refId);
  conditionalRequests(data.etagId, data.refId);
  preferHeader(data.writeId);
  bodySizeLimit();
  documentedResiduals();
}

// ── 1. The 501/400 taxonomy, every route family x every option ───────────────
function taxonomy() {
  group('taxonomy: 501 is "can\'t", 400 is "won\'t"', () => {
    for (const route of ROUTES) {
      for (const option of ALL_PROBES) {
        const url = probeUrl(route, option);
        const res = get(url);
        const b = body(res);

        if (Object.prototype.hasOwnProperty.call(route.implemented, option)) {
          const expected = route.implemented[option];
          check(res, {
            [`[${route.name}] ${option} is implemented -> ${expected}`]: (r) => r.status === expected,
          });
          // The two GetAll structural refusals share 501 + UnsupportedQueryOption with the sigil
          // rule, and are told apart ONLY by the message. Asserting it is what makes moving
          // $filter out of s_getAllCollectionImplementedOptions a failure rather than a no-op.
          if (route.structuralRefusal && route.structuralRefusal[option]) {
            check(res, {
              [`[${route.name}] ${option} keeps its own remedy message`]: () =>
                b !== null && b.error && b.error.message.indexOf(route.structuralRefusal[option]) === 0,
            });
          }
        } else {
          expectUnsupportedOption(res, `[${route.name}] ${option}`);
          check(res, {
            [`[${route.name}] ${option} uses the ${route.wording} wording`]: () =>
              b !== null && b.error && b.error.message.indexOf(WORDING[route.wording](option)) === 0,
          });
        }
      }
    }

    // $format is in EVERY implemented set and must stay there: it is negotiated once on the
    // group filter, never reaches a route handler, and cannot change a row.
    for (const route of ROUTES) {
      check(null, {
        [`[${route.name}] $format is in the implemented set`]: () =>
          Object.prototype.hasOwnProperty.call(route.implemented, '$format'),
      });
    }

    // OrdinalIgnoreCase is ALIGNMENT, not leniency: MS applies $Select/$TOP today, so they stay
    // applied. The change rejects $slect (above), never $Select.
    check(get(`${BASE_URL}/v2/Movies?$Select=Id&$TOP=1`), {
      'mixed-case $Select/$TOP are honoured, not refused': (r) => r.status === 200,
      'mixed-case $Select really projected': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length === 1 && v[0].Id !== undefined && v[0].Title === undefined;
      },
    });

    // Part 2 §5.2 reserves '$' for system options, so a key WITHOUT one is a custom option and
    // is untouched -- which is why the framework's own continuation offset is spelled
    // 'ohdata-skiptoken'. A bound function's own parameters ride the same rule.
    expectStatus(get(`${BASE_URL}/v2/Movies?ohdata-custom=1&$top=1`), 200, 'a non-$ custom option is ignored');
    expectStatus(get(`${BASE_URL}/v2/Movies/TopRated?count=2`), 200, "an operation's own non-$ parameter is untouched");
  });
}

// ── 2. The same rule on a bound ACTION, where the gate must run before the write ──
function taxonomyBoundAction(actionId) {
  group('taxonomy: a refused ACTION mutates nothing', () => {
    if (!actionId) return;
    const rate = `${BASE_URL}/v1/Movies(${actionId})/Rate`;
    const readCount = () => body(get(`${BASE_URL}/v1/Movies(${actionId})`)).RatingCount;

    const before = readCount();

    // s_boundOperationImplementedOptions is $top/$skip/$format; everything else is refused.
    // The gate runs BEFORE parameter binding and before the handler delegate, so this whole
    // sweep must leave RatingCount exactly where it was -- which a status-only assertion would
    // not notice, because a 501 emitted AFTER the write looks identical on the wire.
    for (const option of ALL_PROBES) {
      if (option === '$top' || option === '$skip' || option === '$format') continue;
      const res = post(`${rate}?${PROBE[option]}`, JSON.stringify({ rating: 8.5 }), { params: jsonParams() });
      expectUnsupportedOption(res, `[bound action] ${option}`);
    }

    check(null, {
      'every refused action left RatingCount untouched': () => readCount() === before,
    });

    // ...and the three it does implement really do run.
    let expected = before;
    for (const option of ['$format', '$top', '$skip']) {
      const res = post(`${rate}?${PROBE[option]}`, JSON.stringify({ rating: 8.5 }), { params: jsonParams() });
      expected += 1;
      const b = body(res);
      check(res, {
        [`[bound action] ${option} is implemented -> 200`]: (r) => r.status === 200,
        [`[bound action] ${option} let the action run`]: () => b !== null && b.RatingCount === expected,
      });
    }
  });
}

// ── 3. #359's other half: a refused option is never echoed into a link ───────
function nextLinkNeverEchoesARefusedOption() {
  group('a refused option is never echoed into an @odata.nextLink', () => {
    // BuildNextPageLinkWithSkip copies the WHOLE incoming query string, so on an ungated route
    // an unrecognized option came back inside the server's own nextLink under a 200. Each case
    // is paired with a control on the same fixture that DOES emit a link, so "no link" cannot
    // pass by the route simply having stopped paging.
    const collControl = get(`${BASE_URL}/v2/Movies`);
    check(collControl, {
      'control: the collection route does emit a nextLink': (r) => typeof JSON.parse(r.body)['@odata.nextLink'] === 'string',
    });
    const collRefused = get(`${BASE_URL}/v2/Movies?$unknown=evil`);
    check(collRefused, {
      'collection: refused before any link is built': (r) => r.status === 501 && r.body.indexOf('nextLink') < 0 && r.body.indexOf('evil') < 0,
    });

    // The operation route is where #359's link half actually shipped: it had a nextLink and no
    // option gate at all. count=999 exceeds MaxTop, so the control really pages.
    const opControl = get(`${BASE_URL}/v2/Movies/TopRated?count=999`);
    const ob = body(opControl);
    check(opControl, {
      'control: the bound function pages at MaxTop': () => ob !== null && ob.value.length === MOVIE_MAX_TOP,
      'control: the bound function emits a nextLink': () => ob !== null && typeof ob['@odata.nextLink'] === 'string',
    });
    const opRefused = get(`${BASE_URL}/v2/Movies/TopRated?count=999&$unknown=evil`);
    check(opRefused, {
      'bound function: refused before any link is built': (r) => r.status === 501 && r.body.indexOf('nextLink') < 0 && r.body.indexOf('evil') < 0,
    });
  });
}

// ── 4. §11.2.9's /$count contract, asserted on the NUMBER ───────────────────
function countContract() {
  group('§11.2.9: what may and may not affect a /$count', () => {
    const countOf = (qs) => {
      const res = get(`${BASE_URL}/v2/Movies/$count${qs}`);
      return { status: res.status, n: parseInt(res.body, 10), res: res };
    };

    const base = countOf('');
    check(base.res, {
      '/$count 200': () => base.status === 200,
      '/$count is a bare text/plain integer': (r) => (header(r, 'Content-Type') || '').indexOf('text/plain') >= 0 && /^\d+$/.test(r.body.trim()),
    });

    // AFFECTS the count: the number must move, and must agree with the sibling collection route
    // asked the same question. Cross-checking the two generators is the assertion -- a hardcoded
    // constant would only prove the seed data is unchanged.
    const filtered = countOf(`?$filter=${q(`Year eq ${YEAR_1994}`)}`);
    const inline = body(get(`${BASE_URL}/v2/Movies?$filter=${q(`Year eq ${YEAR_1994}`)}&$count=true`));
    check(filtered.res, {
      '$filter changes the count': () => filtered.status === 200 && filtered.n === YEAR_1994_COUNT,
      '$filter: /$count agrees with @odata.count on the collection route': () => inline !== null && inline['@odata.count'] === filtered.n,
      '$filter: the filtered count really is smaller than the total': () => filtered.n < base.n,
    });

    // MUST NOT affect the count. Asserting the NUMBER is the whole point: an accidentally
    // applied $top would still be a 200 with a plausible-looking body.
    const mustNotAffect = [
      ['$top=1', '$top'],
      ['$skip=5', '$skip'],
      ['$orderby=Rating', '$orderby'],
      ['$expand=Cast', '$expand'],
      ['$select=Id', '$select'],
      [`$top=1&$skip=5&$orderby=Rating&$expand=Cast&$select=Id`, 'all five at once'],
    ];
    for (const [qs, label] of mustNotAffect) {
      const c = countOf(`?${qs}`);
      check(c.res, {
        [`${label} MUST NOT affect the count`]: () => c.status === 200 && c.n === base.n,
      });
    }

    // $search affects the count and this route cannot apply it, so ignoring it would answer a
    // wrong number under a 200. Refused instead.
    expectUnsupportedOption(get(`${BASE_URL}/v2/Movies/$count?$search=alpha`), '/$count $search');

    // The navigation /$count applies even less -- it invokes the delegate and counts the result
    // -- so $filter is refused there too, while the four MUST-NOTs are still accepted no-ops.
    const navCount = (qs) => {
      const res = get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Cast/$count${qs}`);
      return { status: res.status, n: parseInt(res.body, 10), res: res };
    };
    const navBase = navCount('');
    check(navBase.res, {
      'nav /$count matches the seeded cast': () => navBase.status === 200 && navBase.n === SEEDED_MOVIE_CAST_COUNT,
    });
    for (const qs of ['$top=1', '$skip=1', '$orderby=Id', '$select=Id']) {
      const c = navCount(`?${qs}`);
      check(c.res, { [`nav /$count: ${qs} MUST NOT affect the count`]: () => c.status === 200 && c.n === navBase.n });
    }
    expectUnsupportedOption(get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Cast/$count?$filter=${q('Id eq 1')}`), 'nav /$count $filter');
  });
}

// ── 5. Content negotiation (§11.2.12, §8.2.3) ───────────────────────────────
function contentNegotiation() {
  group('content negotiation', () => {
    const ct = (r) => header(r, 'Content-Type') || '';

    for (const fmt of ['json', 'application/json']) {
      const res = get(`${BASE_URL}/v1/Movies?$format=${encodeURIComponent(fmt)}&$top=1`);
      check(res, {
        [`$format=${fmt} 200`]: (r) => r.status === 200,
        [`$format=${fmt} serves application/json`]: (r) => ct(r).indexOf('application/json') >= 0,
      });
    }
    for (const fmt of ['xml', 'atom', 'text/plain']) {
      expectError(get(`${BASE_URL}/v1/Movies?$format=${encodeURIComponent(fmt)}&$top=1`), 400, 'UnsupportedFormat', `$format=${fmt}`);
    }

    // §11.2.12: $format OVERRIDES Accept. A rejected $format wins over an Accept the server
    // could otherwise have satisfied.
    expectError(get(`${BASE_URL}/v1/Movies?$format=xml&$top=1`, { params: { headers: { Accept: 'application/json' } } }),
      400, 'UnsupportedFormat', '$format overrides a satisfiable Accept');

    const acceptCases = [
      ['application/json', 200], ['application/*', 200], ['*/*', 200],
      ['application/json;q=0.8', 200],
      ['application/xml', 406], ['text/html', 406],
      // "application/json;q=0" means "not acceptable" -- honouring q-values is why negotiation
      // goes through AcceptHeaderPermits rather than substring-scanning the header.
      ['application/json;q=0', 406],
    ];
    for (const [accept, expected] of acceptCases) {
      const res = get(`${BASE_URL}/v1/Movies?$top=1`, { params: { headers: { Accept: accept } } });
      check(res, { [`Accept: ${accept} -> ${expected}`]: (r) => r.status === expected });
      if (expected === 406) expectError(res, 406, 'NotAcceptable', `Accept: ${accept}`);
    }

    // /$count serves the count as text/plain (§11.2.6.5), so it can satisfy text/plain as well
    // as JSON -- a client reading the content types the OpenAPI document advertises must not
    // get a 406 for asking correctly.
    const countPlain = get(`${BASE_URL}/v1/Movies/$count`, { params: { headers: { Accept: 'text/plain' } } });
    check(countPlain, {
      '/$count accepts text/plain': (r) => r.status === 200,
      '/$count answers text/plain': (r) => ct(r).indexOf('text/plain') >= 0,
    });
    expectStatus(get(`${BASE_URL}/v1/Movies/$count`, { params: { headers: { Accept: 'application/json' } } }), 200, '/$count accepts application/json');
    expectError(get(`${BASE_URL}/v1/Movies/$count`, { params: { headers: { Accept: 'application/xml' } } }), 406, 'NotAcceptable', '/$count Accept: application/xml');

    // $metadata is application/xml and is exempt from the JSON-only checks entirely.
    const meta = get(`${BASE_URL}/v1/$metadata`, { params: { headers: { Accept: 'application/xml' } } });
    check(meta, {
      '$metadata accepts application/xml': (r) => r.status === 200,
      '$metadata answers application/xml': (r) => ct(r).indexOf('application/xml') >= 0,
    });

    // A structural property's /$value is a raw scalar, so text/plain is producible there too.
    const value = get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})/Title/$value`, { params: { headers: { Accept: 'text/plain' } } });
    check(value, {
      '/$value accepts text/plain': (r) => r.status === 200,
      '/$value serves the raw scalar, unquoted': (r) => r.body === 'The Godfather',
    });

    // Request side: a non-JSON Content-Type on a write is 415 with the OData envelope, not
    // ASP.NET Core's empty short-circuit.
    expectError(post(`${BASE_URL}/v1/Movies`, 'title=x', { params: { headers: { 'Content-Type': 'text/plain' } } }),
      415, 'UnsupportedMediaType', 'POST with Content-Type: text/plain');
    expectError(put(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})`, 'x', { params: { headers: { 'Content-Type': 'application/xml' } } }),
      415, 'UnsupportedMediaType', 'PUT with Content-Type: application/xml');
  });
}

// ── 6. OData-MaxVersion (§8.2.7) ────────────────────────────────────────────
function odataMaxVersion() {
  group('OData-MaxVersion', () => {
    for (const v of ['4.0', '4.01']) {
      expectStatus(get(`${BASE_URL}/v1/Movies?$top=1`, { params: { headers: { 'OData-MaxVersion': v } } }), 200, `OData-MaxVersion: ${v}`);
    }
    for (const v of ['3.0', '2.0', 'bogus']) {
      expectError(get(`${BASE_URL}/v1/Movies?$top=1`, { params: { headers: { 'OData-MaxVersion': v } } }),
        400, 'UnsupportedODataVersion', `OData-MaxVersion: ${v}`);
    }
  });
}

// ── 7. #402: a malformed option VALUE is 400, whatever it throws ────────────
function malformedOptionValues() {
  group('malformed option values are 400, not 500', () => {
    // An empty value throws at ODataQueryOptions CONSTRUCTION time, and the throw set is not
    // restricted to ODataException -- $skiptoken= raises ArgumentException from its own ctor,
    // which is what escaped as a client-reachable 500. All of these must be 400.
    const empties = ['$filter=', '$orderby=', '$top=', '$skip=', '$count=', '$search=', '$skiptoken='];
    for (const qs of empties) {
      const res = get(`${BASE_URL}/v2/Movies?${qs}`);
      check(res, { [`empty ${qs} is a client error, never a 500`]: (r) => r.status === 400 || r.status === 501 });
      check(res, { [`empty ${qs} still carries the OData envelope`]: () => { const b = body(res); return b !== null && b.error && typeof b.error.code === 'string'; } });
    }

    // The sharpest statement of the taxonomy in one option: '$skiptoken=' is 400 (a malformed
    // VALUE for functionality the route implements) on the three collection routes, and 501
    // (unimplemented FUNCTIONALITY) on the two that never implemented it.
    expectError(get(`${BASE_URL}/v2/Movies?$skiptoken=`), 400, 'InvalidQueryOption', 'empty $skiptoken on a collection GET');
    expectUnsupportedOption(get(`${BASE_URL}/v2/Movies/$count?$skiptoken=`), 'empty $skiptoken on /$count');
    expectUnsupportedOption(get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})?$select=Id&$skiptoken=`), 'empty $skiptoken on GetById');

    // Malformed but non-empty values, and an out-of-range one.
    expectError(get(`${BASE_URL}/v1/Movies?$filter=NOTVALID(((`), 400, 'InvalidQueryOption', 'unparseable $filter');
    expectError(get(`${BASE_URL}/v1/Movies?$top=abc`), 400, 'InvalidQueryOption', 'non-integer $top');
    expectError(get(`${BASE_URL}/v1/Movies?$top=999`), 400, 'InvalidQueryOption', '$top above MaxTop');
    expectError(get(`${BASE_URL}/v1/Movies?$filter=${q('NoSuchProp eq 1')}`), 400, 'InvalidQueryOption', '$filter over an undeclared property');
    expectError(get(`${BASE_URL}/v1/Movies?$orderby=NoSuchProp`), 400, 'InvalidQueryOption', '$orderby over an undeclared property');
    expectError(get(`${BASE_URL}/v2/Movies?$expand=NoSuchNav`), 400, 'InvalidQueryOption', '$expand over an undeclared navigation');
  });
}

// ── 8. Request-body nullability (#355 / #544 / #545) ────────────────────────
function bodyNullability(writeId) {
  group('request-body nullability', () => {
    const p = jsonParams();

    // An EXPLICIT null for a property the EDM declares Nullable="false" is 400 with the property
    // named in `target` -- the boundary check that replaced EF's generic 500.
    for (const prop of [['title', 'Title'], ['genreCode', 'GenreCode']]) {
      const bodyJson = newMovie({}); bodyJson[prop[0]] = null;
      const res = post(`${BASE_URL}/v1/Movies`, JSON.stringify(bodyJson), { params: p });
      expectError(res, 400, 'InvalidBody', `POST with ${prop[0]}: null`);
      const b = body(res);
      check(res, {
        [`POST ${prop[0]}: null names the property in target`]: () => b !== null && b.error.target === prop[1],
        [`POST ${prop[0]}: null quotes the EDM, not the CLR`]: () => b !== null && b.error.message.indexOf('non-nullable by the service metadata') > 0,
      });
    }

    // OMITTING it is ACCEPTED (#544/#545). §11.4.2's only MUST-fail is about values SPECIFIED in
    // the request; the omission leg #355 shipped made the wire answer depend on the CLR
    // initializer, which $metadata cannot describe.
    const omitted = newMovie({}); delete omitted.title;
    const created = post(`${BASE_URL}/v1/Movies`, JSON.stringify(omitted), { params: p });
    check(created, { 'POST omitting a non-nullable property is accepted': (r) => r.status === 201 });
    if (created.status === 201) del(`${BASE_URL}/v1/Movies(${JSON.parse(created.body).Id})`);

    // A non-nullable VALUE type is excluded from the gate deliberately -- a JSON null there is
    // already a deserializer error, so checking it would cost a boxing read to answer a question
    // with one possible answer. Still a 400, worded by the deserializer.
    const nullYear = newMovie({}); nullYear.year = null;
    expectError(post(`${BASE_URL}/v1/Movies`, JSON.stringify(nullYear), { params: p }), 400, 'InvalidBody', 'POST with year: null');

    if (!writeId) return;

    // PUT: same rule, both directions.
    const putNull = newMovie({ id: writeId }); putNull.title = null;
    expectError(put(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify(putNull), { params: p }), 400, 'InvalidBody', 'PUT with title: null');
    const putOmit = newMovie({ id: writeId }); delete putOmit.title;
    expectStatus(put(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify(putOmit), { params: p }), 200, 'PUT omitting a non-nullable property');

    // PATCH checks only what the body NAMED -- a Delta is a change set, so an absent property
    // means "leave it alone" and can never be the omission case.
    const patchRes = patch(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify({ title: null }), { params: p });
    expectError(patchRes, 400, 'InvalidBody', 'PATCH with title: null');
    check(patchRes, { 'PATCH title: null names Title in target': () => { const b = body(patchRes); return b !== null && b.error.target === 'Title'; } });
    expectStatus(patch(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify({ rating: 3.5 }), { params: p }), 200, 'PATCH naming only a nullable-irrelevant property');
  });
}

// ── 9. @odata.bind is 501 on every write verb (#456) ────────────────────────
function odataBind(writeId) {
  group('@odata.bind is 501 on every write verb', () => {
    const p = jsonParams();
    const bind = { 'Studio@odata.bind': 'Studios(1)' };
    const countBefore = parseInt(get(`${BASE_URL}/v1/Movies/$count`).body, 10);

    // v1 (AllowDeepWrites false) and v2 (AllowDeepWrites TRUE) must answer identically: the flag
    // controls what a nested GRAPH does, and @odata.bind sends no graph -- it names an entity to
    // link, which this framework does not implement at all.
    for (const version of ['v1', 'v2']) {
      expectError(post(`${BASE_URL}/${version}/Movies`, JSON.stringify(newMovie(bind)), { params: p }),
        501, 'NotImplemented', `POST /${version}/Movies with @odata.bind`);
    }

    if (writeId) {
      expectError(put(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify(newMovie(Object.assign({ id: writeId }, bind))), { params: p }),
        501, 'NotImplemented', 'PUT with @odata.bind');
      expectError(patch(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify(bind), { params: p }),
        501, 'NotImplemented', 'PATCH with @odata.bind');
      expectError(put(`${BASE_URL}/v1/Movies(${writeId})/Title`, JSON.stringify({ value: { 'Studio@odata.bind': 'Studios(1)' } }), { params: p }),
        501, 'NotImplemented', 'structural-property PUT with @odata.bind');

      // DELIBERATE RESIDUAL, pinned so a change to it is a decision: the property-write route
      // scans the `value` MEMBER, not the whole envelope, so a bind annotation that is a SIBLING
      // of `value` is never seen and the write proceeds. Measured 204.
      expectStatus(put(`${BASE_URL}/v1/Movies(${writeId})/Title`, JSON.stringify({ value: 'K6PropWrite', 'Studio@odata.bind': 'Studios(1)' }), { params: p }),
        204, 'property write: an @odata.bind SIBLING of `value` is out of scope (residual)');
      check(get(`${BASE_URL}/v1/Movies(${writeId})/Title`), {
        'property write: the residual really performed the write': (r) => JSON.parse(r.body).value === 'K6PropWrite',
      });
    }

    // The refusal happens before the handler, so the refused creates really created nothing.
    check(null, {
      'a refused @odata.bind POST created no entity': () => parseInt(get(`${BASE_URL}/v1/Movies/$count`).body, 10) === countBefore,
    });
  });
}

// ── 10. The OData error envelope across representative failures ─────────────
function errorEnvelopes(refId) {
  group('error envelope shape', () => {
    // expectError already asserts {"error":{"code","message"}} everywhere it is used. This group
    // covers the shapes the rest of the file does not reach, and the two boundaries where the
    // envelope deliberately does NOT apply.
    expectError(get(`${BASE_URL}/v1/Movies(notanint)`), 400, 'BadRequest', 'unparseable key');
    check(get(`${BASE_URL}/v1/Movies(notanint)`), {
      'unparseable key names `key` in target': (r) => { const b = body(r); return b !== null && b.error.target === 'key'; },
    });
    expectError(get(`${BASE_URL}/v1/Movies(${MISSING_ID})`), 404, 'NotFound', 'missing entity');
    expectError(get(`${BASE_URL}/v1/Genres('NOSUCH')`), 404, 'NotFound', 'missing entity with a string key');
    expectError(patch(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})`, '[1,2,3]', { params: jsonParams() }), 400, 'InvalidBody', 'PATCH with a non-object body');
    expectError(post(`${BASE_URL}/v1/Movies`, 'not json at all', { params: jsonParams() }), 400, 'InvalidBody', 'POST with a non-JSON body');

    // A genuine unhandled handler exception. The TestBench's addRef delegate parses the key out
    // of '@odata.id' itself and throws FormatException on a value with no key segment -- which
    // is user code, so it reaches the group-level filter. It must come back as 500 WITH the
    // envelope and WITHOUT the exception's own message (never ex.Message, never a stack trace).
    if (refId) {
      const res = post(`${BASE_URL}/v2/Movies(${refId})/Cast/$ref`, JSON.stringify({ '@odata.id': 'no-key-here' }), { params: jsonParams() });
      expectError(res, 500, 'InternalServerError', 'a handler exception');
      check(res, {
        'a 500 does not leak the handler exception': (r) =>
          r.body.indexOf('FormatException') < 0 && r.body.indexOf('@odata.id') < 0 && r.body.indexOf('ODataRefKey') < 0,
      });
      // ...and the well-formed-but-semantically-wrong shapes next to it are 400, not 500.
      expectError(post(`${BASE_URL}/v2/Movies(${refId})/Cast/$ref`, JSON.stringify({ '@odata.id': 123 }), { params: jsonParams() }),
        400, 'BadRequest', '$ref with a non-string @odata.id');
      expectError(post(`${BASE_URL}/v2/Movies(${refId})/Cast/$ref`, JSON.stringify({ '@odata.id': null }), { params: jsonParams() }),
        400, 'BadRequest', '$ref with a null @odata.id');
      expectError(post(`${BASE_URL}/v2/Movies(${refId})/Cast/$ref`, '{}', { params: jsonParams() }),
        400, 'BadRequest', '$ref with no @odata.id');
    }

    // The two documented boundaries. 405 and the unmapped-path 404 are answered by ASP.NET Core
    // ROUTING, above the OData endpoint group, so they carry neither the envelope nor
    // OData-Version. Asserted rather than assumed, so the boundary is pinned where it is.
    const methodNotAllowed = req('DELETE', `${BASE_URL}/v1/Movies`, null, { odataVersion: false });
    check(methodNotAllowed, {
      'DELETE on a collection is 405 from routing': (r) => r.status === 405,
      '405 carries Allow': (r) => (header(r, 'Allow') || '').indexOf('GET') >= 0,
      '405 has no OData envelope (routing answered it)': (r) => r.body === '' || r.body === null,
    });
  });
}

// ── 11. Conditional requests (RFC 9110 §13, OData §11.4.1.1, #478) ──────────
function conditionalRequests(etagId, refId) {
  group('conditional requests', () => {
    if (!etagId) return;
    const url = `${BASE_URL}/v1/Movies(${etagId})`;
    const read = get(url);
    const live = header(read, 'ETag');
    check(read, { 'GET emits a strong ETag': () => typeof live === 'string' && live.charAt(0) === '"' });
    if (typeof live !== 'string') return;

    const weakLive = `W/${live}`;

    // §8.2.5 conditional GET. If-None-Match uses WEAK comparison (§13.1.2), so W/"<current>"
    // matches and answers 304.
    for (const [tag, label] of [[live, 'strong'], [weakLive, 'weak W/ form']]) {
      const res = get(url, { params: { headers: { 'If-None-Match': tag } } });
      check(res, {
        [`If-None-Match with the ${label} live tag -> 304`]: (r) => r.status === 304,
        [`304 (${label}) has no body`]: (r) => r.body === '' || r.body === null,
        [`304 (${label}) still carries the ETag`]: (r) => header(r, 'ETag') === live,
      });
    }
    expectStatus(get(url, { params: { headers: { 'If-None-Match': '"stale"' } } }), 200, 'If-None-Match with a stale tag');

    // If-Match on a write uses STRONG comparison (§13.1.1): a W/-prefixed entry is DROPPED, never
    // unwrapped, so If-Match: W/"<current>" is a 412 even though the tag is current. That
    // asymmetry with the 304 above is the whole rule, and it is invisible without both halves.
    const cases = [
      ['"stale"', 412, 'a stale tag'],
      [weakLive, 412, 'the live tag in weak form'],
      ['"stale", "also-stale"', 412, 'a list of stale tags'],
    ];
    for (const [tag, expected, label] of cases) {
      const res = patch(url, JSON.stringify({ title: 'MustNotStick' }), { params: jsonParams({ 'If-Match': tag }) });
      expectError(res, expected, 'PreconditionFailed', `PATCH If-Match ${label}`);
    }
    check(get(url), { 'no refused If-Match write landed': (r) => JSON.parse(r.body).Title !== 'MustNotStick' });

    // The accepting directions.
    expectStatus(patch(url, JSON.stringify({ rating: 2.5 }), { params: jsonParams({ 'If-Match': live }) }), 200, 'PATCH If-Match the live tag');
    const live2 = header(get(url), 'ETag');
    check(null, { 'the write moved the ETag on': () => live2 !== live });
    expectStatus(patch(url, JSON.stringify({ rating: 2.75 }), { params: jsonParams({ 'If-Match': '*' }) }), 200, 'PATCH If-Match: *');
    const live3 = header(get(url), 'ETag');
    expectStatus(patch(url, JSON.stringify({ rating: 3 }), { params: jsonParams({ 'If-Match': `"stale", ${live3}` }) }), 200, 'PATCH If-Match a list containing the live tag');

    // If-None-Match is evaluated ONLY when If-Match is absent; a request carrying both is
    // decided by If-Match.
    const live4 = header(get(url), 'ETag');
    expectStatus(patch(url, JSON.stringify({ rating: 3.25 }), { params: jsonParams({ 'If-Match': live4, 'If-None-Match': live4 }) }),
      200, 'If-Match wins when both headers are present');

    // DELETE is gated too.
    expectError(del(url, null, { params: { headers: { 'If-Match': '"stale"' } } }), 412, 'PreconditionFailed', 'DELETE with a stale If-Match');
    expectStatus(get(url), 200, 'the refused DELETE did not delete');

    // #478: the $ref write routes previously DISCARDED If-Match and performed the write with a
    // 204 -- a lost update on relationship state. Real HTTP is where a discarded request header
    // is visible at all.
    if (refId) {
      const refUrl = `${BASE_URL}/v2/Movies(${refId})/Cast/$ref`;
      const linkBody = JSON.stringify({ '@odata.id': `Actors(${UNLINKED_ACTOR_ID})` });
      expectError(post(refUrl, linkBody, { params: jsonParams({ 'If-Match': '"stale"' }) }), 412, 'PreconditionFailed', '$ref POST with a stale If-Match');
      check(get(`${BASE_URL}/v2/Movies(${refId})/Cast`), {
        'the refused $ref write created no link': (r) => JSON.parse(r.body).value.length === 0,
      });
      const refLive = header(get(`${BASE_URL}/v2/Movies(${refId})`), 'ETag');
      expectStatus(post(refUrl, linkBody, { params: jsonParams({ 'If-Match': refLive }) }), 204, '$ref POST with the live If-Match');
      check(get(`${BASE_URL}/v2/Movies(${refId})/Cast`), {
        'the accepted $ref write created the link': (r) => JSON.parse(r.body).value.length === 1,
      });
      expectError(del(`${refUrl}?$id=Actors(${UNLINKED_ACTOR_ID})`, null, { params: { headers: { 'If-Match': '"stale"' } } }),
        412, 'PreconditionFailed', '$ref DELETE with a stale If-Match');
      check(get(`${BASE_URL}/v2/Movies(${refId})/Cast`), {
        'the refused $ref DELETE removed nothing': (r) => JSON.parse(r.body).value.length === 1,
      });
    }
  });
}

// ── 12. Prefer / Preference-Applied (§8.2.8, RFC 7240) ──────────────────────
function preferHeader(writeId) {
  group('Prefer', () => {
    // §8.2.8.3 maxpagesize, both the plain and the odata.-prefixed spelling. RFC 7240 forbids
    // claiming a preference that was not applied, so Preference-Applied must echo the value the
    // server really used -- and the page really has to be that long.
    for (const spelling of ['maxpagesize=5', 'odata.maxpagesize=5']) {
      const res = get(`${BASE_URL}/v1/Movies`, { params: { headers: { Prefer: spelling } } });
      const b = body(res);
      check(res, {
        [`Prefer: ${spelling} -> 200`]: (r) => r.status === 200,
        [`Prefer: ${spelling} really narrows the page to 5`]: () => b !== null && b.value.length === 5,
        [`Prefer: ${spelling} echoes Preference-Applied`]: (r) => (header(r, 'Preference-Applied') || '').indexOf('maxpagesize=5') >= 0,
        [`Prefer: ${spelling} still emits a continuation`]: () => b !== null && typeof b['@odata.nextLink'] === 'string',
      });
    }

    // A maxpagesize above MaxTop cannot lift the server's own ceiling.
    const overCap = get(`${BASE_URL}/v1/Movies`, { params: { headers: { Prefer: 'maxpagesize=500' } } });
    check(overCap, { 'Prefer: maxpagesize cannot exceed MaxTop': (r) => JSON.parse(r.body).value.length === MOVIE_MAX_TOP });

    if (!writeId) return;

    // §8.2.8.1/§8.2.8.7 return=minimal / return=representation.
    const minimal = patch(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify({ rating: 4 }), { params: jsonParams({ Prefer: 'return=minimal' }) });
    check(minimal, {
      'Prefer: return=minimal on PATCH -> 204': (r) => r.status === 204,
      'Prefer: return=minimal has no body': (r) => r.body === '' || r.body === null,
      'Prefer: return=minimal echoes Preference-Applied': (r) => header(r, 'Preference-Applied') === 'return=minimal',
      'Prefer: return=minimal still carries the new ETag': (r) => typeof header(r, 'ETag') === 'string',
    });

    const representation = patch(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify({ rating: 4.5 }), { params: jsonParams({ Prefer: 'return=representation' }) });
    check(representation, {
      'Prefer: return=representation on PATCH -> 200': (r) => r.status === 200,
      'Prefer: return=representation returns the entity': (r) => JSON.parse(r.body).Rating === 4.5,
      'Prefer: return=representation echoes Preference-Applied': (r) => header(r, 'Preference-Applied') === 'return=representation',
    });

    const created = post(`${BASE_URL}/v1/Movies`, JSON.stringify(newMovie({ title: 'K6PreferMinimal' })), { params: jsonParams({ Prefer: 'return=minimal' }) });
    check(created, {
      'Prefer: return=minimal on POST -> 204': (r) => r.status === 204,
      'a 204 create still locates the entity': (r) => typeof header(r, 'Location') === 'string' && typeof header(r, 'OData-EntityId') === 'string',
      'a 204 create sets Content-Location': (r) => typeof header(r, 'Content-Location') === 'string',
    });
    const loc = header(created, 'Location');
    if (loc) {
      check(get(loc), { 'the Location of a minimal create addresses the entity': (r) => r.status === 200 });
      del(loc);
    }
  });
}

// ── 13. The real Kestrel body limit (#203 / #474 / #496) ────────────────────
function bodySizeLimit() {
  group('request body size limit', () => {
    // Runs under real Kestrel, which TestServer cannot stand in for: it supplies no
    // IHttpMaxRequestBodySizeFeature at all (RequestBodySizeFeatureTests fakes one).
    //
    // MovieProfile's 64 KB MaxRequestBodyBytes is BELOW the framework default, so it is applied
    // unconditionally -- #474's Math.Min clamp engages only for the default itself.
    //
    // #598: a 30 MB body here matched the default AND Kestrel's own limit, so the 413 was written
    // while k6 was still uploading -- an early response over an undrained body, which reset the
    // connection about half the time.
    const LIMIT = 65536;
    const oversized = `{"title":"${'a'.repeat(LIMIT)}","year":2025}`;
    const res = post(`${BASE_URL}/v1/Movies`, oversized, { params: jsonParams() });
    expectError(res, 413, 'RequestEntityTooLarge', 'a body over the limit');
    check(res, {
      '413 names both the actual size and the limit': () => {
        const b = body(res);
        return b !== null && b.error.message.indexOf(String(LIMIT)) > 0;
      },
      // #496 finding 2. The 413 is emitted by the third of five group filters, which
      // short-circuits above the filter that used to write OData-Version -- so this response
      // shipped with no OData-Version at all. It is written by the OUTERMOST filter now, and
      // this assertion is the reason that placement is load-bearing. `req` has already checked
      // the header; this restates WHY the 413 in particular is the case that matters.
      '413 still carries OData-Version (the header must survive a short-circuit)': (r) => header(r, 'OData-Version') === '4.0',
    });
    // #601 is NOT asserted here and cannot be: the fast-reject answers before reading the body, so
    // Kestrel closes the connection, and RFC 9110 §7.6.1 requires the server to send
    // `Connection: close` -- but Go's net/http strips hop-by-hop headers from the response, so k6
    // never sees it (measured: the check fails while a raw socket sees the header). Pinned in
    // RequestBodySizeLimitTests instead. The effect is visible here all the same: k6's client
    // honours the close and opens a new connection, which is why the request AFTER this one now
    // succeeds -- it used to fail about half the time, never retried because it is a POST.

    // A body comfortably under the limit is unaffected.
    const ok = post(`${BASE_URL}/v1/Movies`, JSON.stringify(newMovie({ title: 'a'.repeat(1000) })), { params: jsonParams() });
    check(ok, { 'a body under the limit is accepted': (r) => r.status === 201 });
    if (ok.status === 201) del(`${BASE_URL}/v1/Movies(${JSON.parse(ok.body).Id})`);
  });
}

// ── 14. Deliberate residuals, pinned so a "fix" is a decision rather than a slip ──
function documentedResiduals() {
  group('documented residuals', () => {
    // The service document and $metadata ignore every query option -- deliberately, and recorded
    // as such in docs/query-options.md. Neither generates a link, so neither carries #359's echo.
    // Pinned here so the table in that document is not read as the whole URL surface, and so a
    // future change to it is a choice.
    expectStatus(get(`${BASE_URL}/v1/$metadata?$unknown=1`), 200, '$metadata ignores query options');
    expectStatus(get(`${BASE_URL}/v1?$unknown=1`), 200, 'the service document ignores query options');

    // #560: the structural-property READS were on that list and are not any more -- they were the
    // one residual that answered differently from the sibling entity route over the same resource.
    // $select is refused although GET /Movies({key}) implements it: this handler reads no option.
    expectUnsupportedOption(
      get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})/Title?$unknown=1`), 'a property read: $unknown');
    expectUnsupportedOption(
      get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})/Title?$select=Title`), 'a property read: $select');
    expectUnsupportedOption(
      get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})/Title/$value?$unknown=1`), 'a property $value: $unknown');
    expectStatus(
      get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})/Title?$format=json`), 200,
      'a property read still accepts $format');

    // The GetAll route answers InvalidQueryOption rather than UnsupportedQueryOption for an
    // EMPTY-valued unimplemented option, because TryBuildQueryOptions runs before the sigil gate
    // there. A pre-existing asymmetry, not one the sigil rule introduced.
    const emptyOnGetAll = get(`${BASE_URL}/v1/Genres?$skiptoken=`);
    check(emptyOnGetAll, {
      'GetAll: an empty unimplemented option is 400, not 501 (known asymmetry)': (r) => r.status === 400,
    });
    expectUnsupportedOption(get(`${BASE_URL}/v1/Genres?$skiptoken=abc`), 'GetAll: a non-empty $skiptoken');
  });
}

export function handleSummary(data) {
  return reports('conformance', data);
}
