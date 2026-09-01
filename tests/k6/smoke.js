// OhData smoke test -- one pass over EVERY route family the TestBench exposes.
//
// This script's job is "the stack is wired correctly end to end", so it takes ONE representative
// case per family rather than a matrix; the matrices live in conformance.js. What earns a place
// here is a family that, if it broke, would break silently for every user of it.
//
// Runs against the real containerized TestBench over real HTTP (tests/docker-compose.k6.yml ->
// Kestrel on :8080). That is the whole reason this layer exists: the repo's xUnit suites all use
// WebApplicationFactory/TestServer, which is in-process and bypasses the HTTP stack -- so response
// headers, content negotiation, conditional requests, real body limits and "follow a link the
// server issued" can only be tested honestly here.

import { group, check } from 'k6';
import {
  BASE_URL, SEEDED_MOVIE_ID, SEEDED_MOVIE_TITLE, SEEDED_MOVIE_COUNT, SEEDED_MOVIE_CAST_COUNT,
  SEEDED_MOVIE_STUDIO_NAME, SEEDED_GENRE_COUNT, MISSING_ID, UNLINKED_ACTOR_ID, MOVIE_MAX_TOP,
  newMovie,
} from './lib/seed.js';
import {
  get, post, put, patch, del, jsonParams, header, body, expectStatus, expectError,
  expectUnsupportedOption, q, reports,
} from './lib/odata.js';

export const options = {
  thresholds: {
    // http_req_failed is omitted: the test deliberately sends requests that expect 4xx/5xx
    // responses (error cases, missing-entity GETs, the 501/400 taxonomy, a 412 precondition),
    // so the failure rate is inherently > 1%. Individual correctness is covered by `checks`.
    //
    // rate==1.00, not rate>0.99. Every check here is deterministic against fixed seed data --
    // there is no flake budget to spend, and a fractional threshold means a regression that
    // breaks one assertion out of two hundred still ships green. This threshold is the ONLY
    // thing that makes the script able to fail the build: a failing check() does not by itself
    // fail a k6 run.
    'checks': ['rate==1.00'],
    'http_req_duration{group:::collection GET}': ['p(95)<500'],
  },
};

// ── Setup: create the entities the mutating groups work on ───────────────────
// Seeded rows are never mutated by this script: every write targets a movie created here, so a
// re-run against a long-lived container starts from the same place.

export function setup() {
  const p = jsonParams();

  const forWrites = post(`${BASE_URL}/v1/Movies`, JSON.stringify(newMovie({ title: 'K6WriteTarget' })), { params: p });
  check(forWrites, { 'setup: write-target created (201)': (r) => r.status === 201 });

  const forDelete = post(`${BASE_URL}/v1/Movies`, JSON.stringify(newMovie({ title: 'K6DeleteTarget' })), { params: p });
  check(forDelete, { 'setup: delete-target created (201)': (r) => r.status === 201 });

  const forEtag = post(`${BASE_URL}/v1/Movies`, JSON.stringify(newMovie({ title: 'K6EtagTarget' })), { params: p });
  check(forEtag, { 'setup: etag-target created (201)': (r) => r.status === 201 });

  // v2 target for the navigation/$ref/action groups -- v2 is where Cast/Studio have handlers.
  const forRefs = post(`${BASE_URL}/v2/Movies`, JSON.stringify(newMovie({ title: 'K6RefTarget' })), { params: p });
  check(forRefs, { 'setup: ref-target created (201)': (r) => r.status === 201 });

  const id = (r) => (r.status === 201 ? JSON.parse(r.body).Id : null);
  return {
    writeId: id(forWrites),
    deleteId: id(forDelete),
    etagId: id(forEtag),
    refId: id(forRefs),
  };
}

export function teardown(data) {
  for (const id of [data.writeId, data.etagId, data.refId]) {
    if (id) del(`${BASE_URL}/v1/Movies(${id})`);
  }
  // deleteId is consumed by the DELETE group.
}

export default function (data) {
  const { writeId, deleteId, etagId, refId } = data;
  const p = jsonParams();

  // ── service surface ────────────────────────────────────────────────────────
  group('service surface', () => {
    const svc = get(`${BASE_URL}/v1`);
    const svcBody = body(svc);
    check(svc, {
      'service document 200': (r) => r.status === 200,
      'service document has @odata.context': () => svcBody && svcBody['@odata.context'] !== undefined,
      'service document lists Movies': () =>
        svcBody && Array.isArray(svcBody.value) && svcBody.value.some((e) => e.name === 'Movies'),
    });

    const meta = get(`${BASE_URL}/v1/$metadata`);
    check(meta, {
      '$metadata 200': (r) => r.status === 200,
      '$metadata is XML': (r) => (header(r, 'Content-Type') || '').indexOf('xml') >= 0,
      '$metadata declares the Movie entity type': (r) => r.body.indexOf('EntityType Name="Movie"') >= 0,
    });
  });

  // ── collection GET ─────────────────────────────────────────────────────────
  group('collection GET', () => {
    const res = get(`${BASE_URL}/v1/Movies`);
    const b = body(res);
    check(res, {
      'status 200': (r) => r.status === 200,
      'Content-Type is application/json': (r) => (header(r, 'Content-Type') || '').indexOf('application/json') >= 0,
      'has @odata.context': () => b !== null && b['@odata.context'] !== undefined,
      'has value array': () => b !== null && Array.isArray(b.value),
      'value is non-empty': () => b !== null && b.value.length > 0,
    });
  });

  // ── $filter ────────────────────────────────────────────────────────────────
  group('$filter', () => {
    // Property names must match the EDM model (PascalCase: Year, Rating, Title). Values with
    // spaces are percent-encoded because k6 sends URLs verbatim and Kestrel treats a literal
    // space in the request line as a terminator (400, empty body).
    const cases = [
      { qs: `$filter=${q('Year eq 1994')}`, label: 'eq numeric', expect: (v) => v.length >= 3 && v.every((m) => m.Year === 1994) },
      { qs: `$filter=${q('Year ne 1994')}`, label: 'ne numeric', expect: (v) => v.every((m) => m.Year !== 1994) },
      { qs: `$filter=${q('Rating gt 9')}`, label: 'gt numeric', expect: (v) => v.length >= 1 && v.every((m) => m.Rating > 9) },
      { qs: `$filter=${q('Rating lt 7')}`, label: 'lt numeric', expect: (v) => v.every((m) => m.Rating < 7) },
      { qs: `$filter=${q('Rating ge 9.3')}`, label: 'ge numeric', expect: (v) => v.length >= 2 && v.every((m) => m.Rating >= 9.3) },
      { qs: `$filter=${q('Rating le 9.3')}`, label: 'le numeric', expect: (v) => v.every((m) => m.Rating <= 9.3) },
      { qs: `$filter=${q("contains(Title,'God')")}`, label: 'contains', expect: (v) => v.length >= 1 && v.every((m) => m.Title.includes('God')) },
      { qs: `$filter=${q("startswith(Title,'The')")}`, label: 'startswith', expect: (v) => v.length >= 1 && v.every((m) => m.Title.startsWith('The')) },
      { qs: `$filter=${q("endswith(Title,'er')")}`, label: 'endswith', expect: (v) => v.length >= 1 && v.every((m) => m.Title.endsWith('er')) },
      { qs: `$filter=${q('Year gt 2000 and Rating gt 8')}`, label: 'and', expect: (v) => v.length >= 1 && v.every((m) => m.Year > 2000 && m.Rating > 8) },
      { qs: `$filter=${q('Year lt 1980 or Year gt 2020')}`, label: 'or', expect: (v) => v.length >= 1 && v.every((m) => m.Year < 1980 || m.Year > 2020) },
    ];

    for (const tc of cases) {
      const res = get(`${BASE_URL}/v1/Movies?${tc.qs}`);
      const b = body(res);
      check(res, {
        [`filter ${tc.label} 200`]: (r) => r.status === 200,
        [`filter ${tc.label} results correct`]: () => b !== null && Array.isArray(b.value) && tc.expect(b.value),
      });
    }
  });

  // ── $orderby ───────────────────────────────────────────────────────────────
  group('$orderby', () => {
    const asc = get(`${BASE_URL}/v1/Movies?$orderby=Rating`);
    check(asc, {
      'orderby rating asc 200': (r) => r.status === 200,
      'orderby rating asc ordered': (r) => {
        const v = JSON.parse(r.body).value;
        for (let i = 1; i < v.length; i++) if (v[i].Rating < v[i - 1].Rating) return false;
        return true;
      },
    });

    const desc = get(`${BASE_URL}/v1/Movies?$orderby=${q('Rating desc')}`);
    check(desc, {
      'orderby rating desc 200': (r) => r.status === 200,
      'orderby rating desc ordered': (r) => {
        const v = JSON.parse(r.body).value;
        for (let i = 1; i < v.length; i++) if (v[i].Rating > v[i - 1].Rating) return false;
        return true;
      },
    });

    const multi = get(`${BASE_URL}/v1/Movies?$orderby=${q('GenreCode,Rating desc')}`);
    check(multi, {
      'orderby multi-property 200': (r) => r.status === 200,
      'orderby multi-property has results': (r) => JSON.parse(r.body).value.length > 0,
    });
  });

  // ── $select ────────────────────────────────────────────────────────────────
  group('$select', () => {
    const one = get(`${BASE_URL}/v1/Movies?$select=title`);
    check(one, {
      '$select single 200': (r) => r.status === 200,
      '$select single has Title': (r) => { const v = JSON.parse(r.body).value; return v.length > 0 && v[0].Title !== undefined; },
      '$select single drops Rating': (r) => { const v = JSON.parse(r.body).value; return v.length > 0 && v[0].Rating === undefined; },
    });

    const two = get(`${BASE_URL}/v1/Movies?$select=title,year`);
    check(two, {
      '$select multi 200': (r) => r.status === 200,
      '$select multi has Title and Year': (r) => { const v = JSON.parse(r.body).value; return v.length > 0 && v[0].Title !== undefined && v[0].Year !== undefined; },
      '$select multi drops GenreCode': (r) => { const v = JSON.parse(r.body).value; return v.length > 0 && v[0].GenreCode === undefined; },
    });
  });

  // ── $top/$skip ─────────────────────────────────────────────────────────────
  group('$top/$skip', () => {
    const top = get(`${BASE_URL}/v1/Movies?$top=1`);
    check(top, {
      '$top=1 200': (r) => r.status === 200,
      '$top=1 returns exactly 1': (r) => JSON.parse(r.body).value.length === 1,
    });

    // $skip is asserted by VALUE, not by length: an ordered page skipped by one must start at
    // the second row of the unskipped page. A length-only check passes even if $skip is dropped.
    const page = get(`${BASE_URL}/v1/Movies?$orderby=Id&$top=3`);
    const skipped = get(`${BASE_URL}/v1/Movies?$orderby=Id&$top=3&$skip=1`);
    check(skipped, {
      '$skip=1 200': (r) => r.status === 200,
      '$skip=1 shifts the window by one row': () => {
        const a = body(page).value, b2 = body(skipped).value;
        return a.length === 3 && b2.length === 3 && a[1].Id === b2[0].Id && a[2].Id === b2[1].Id;
      },
    });
  });

  // ── $count ─────────────────────────────────────────────────────────────────
  group('$count', () => {
    const standalone = get(`${BASE_URL}/v1/Movies/$count`);
    check(standalone, {
      '/$count 200': (r) => r.status === 200,
      // §11.2.6.5: the count is a bare scalar with media type text/plain -- not a JSON envelope.
      '/$count is text/plain': (r) => (header(r, 'Content-Type') || '').indexOf('text/plain') >= 0,
      '/$count body is a bare integer': (r) => /^\d+$/.test(r.body.trim()),
      [`/$count >= ${SEEDED_MOVIE_COUNT}`]: (r) => parseInt(r.body, 10) >= SEEDED_MOVIE_COUNT,
    });

    const inline = get(`${BASE_URL}/v1/Movies?$count=true`);
    const ib = body(inline);
    check(inline, {
      '$count=true 200': (r) => r.status === 200,
      '$count=true has @odata.count': () => ib !== null && typeof ib['@odata.count'] === 'number',
      // The inline count is the TOTAL, not the page length -- MaxTop caps the page at 50 while
      // the catalog is larger, so equality here would mean the count had been taken after paging.
      '@odata.count matches /$count': () => ib !== null && ib['@odata.count'] === parseInt(standalone.body, 10),
    });
  });

  // ── single entity GET ──────────────────────────────────────────────────────
  group('single entity GET', () => {
    const found = get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})`);
    check(found, {
      'GetById 200': (r) => r.status === 200,
      'GetById returns the addressed key': (r) => JSON.parse(r.body).Id === SEEDED_MOVIE_ID,
      'GetById returns the right title': (r) => JSON.parse(r.body).Title === SEEDED_MOVIE_TITLE,
    });

    const missing = get(`${BASE_URL}/v1/Movies(${MISSING_ID})`);
    expectError(missing, 404, 'NotFound', 'GetById missing key');
  });

  // ── $expand (v2 -- ExpandEnabled is off on v1 by design) ───────────────────
  group('$expand', () => {
    const single = get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})?$expand=Cast`);
    const sb = body(single);
    check(single, {
      '$expand on GetById 200': (r) => r.status === 200,
      '$expand=Cast is inlined': () => sb !== null && Array.isArray(sb.Cast),
      '$expand=Cast returns the seeded cast': () => sb !== null && sb.Cast.length === SEEDED_MOVIE_CAST_COUNT,
      '$expand=Cast carries real actor data': () => sb !== null && sb.Cast.every((a) => typeof a.Name === 'string' && a.Name.length > 0),
    });

    // Single-valued navigation, batch-loaded (MovieProfileV2 uses HasRequired's batchGet
    // overload), so this is the one-query-per-page path rather than one query per row.
    const coll = get(`${BASE_URL}/v2/Movies?$expand=Studio&$orderby=Id&$top=3`);
    const cb = body(coll);
    check(coll, {
      '$expand on collection 200': (r) => r.status === 200,
      '$expand=Studio is inlined on every row': () => cb !== null && cb.value.length === 3 && cb.value.every((m) => m.Studio && typeof m.Studio.Name === 'string'),
      '$expand=Studio resolves the right studio': () => cb !== null && cb.value[0].Studio.Name === SEEDED_MOVIE_STUDIO_NAME,
    });

    // Not expanded => omitted entirely (JSON Format §4.5.1), never null and never inlined.
    const bare = get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})`);
    check(bare, {
      'un-expanded navigation is omitted, not null': (r) => {
        const b = JSON.parse(r.body);
        return !('Cast' in b) && !('Studio' in b);
      },
    });
  });

  // ── navigation routes (v2) ─────────────────────────────────────────────────
  group('navigation routes', () => {
    const castColl = get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Cast`);
    const cb = body(castColl);
    check(castColl, {
      'nav collection GET 200': (r) => r.status === 200,
      'nav collection has @odata.context': () => cb !== null && cb['@odata.context'] !== undefined,
      'nav collection returns the seeded cast': () => cb !== null && cb.value.length === SEEDED_MOVIE_CAST_COUNT,
    });

    const castCount = get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Cast/$count`);
    check(castCount, {
      'nav /$count 200': (r) => r.status === 200,
      'nav /$count is text/plain': (r) => (header(r, 'Content-Type') || '').indexOf('text/plain') >= 0,
      'nav /$count agrees with the nav collection': (r) => parseInt(r.body, 10) === SEEDED_MOVIE_CAST_COUNT,
    });

    const studio = get(`${BASE_URL}/v2/Movies(${SEEDED_MOVIE_ID})/Studio`);
    check(studio, {
      'single-valued nav GET 200': (r) => r.status === 200,
      'single-valued nav returns the related entity': (r) => JSON.parse(r.body).Name === SEEDED_MOVIE_STUDIO_NAME,
    });
  });

  // ── $ref link management (v2) ──────────────────────────────────────────────
  group('$ref', () => {
    if (!refId) return;
    const navRef = `${BASE_URL}/v2/Movies(${refId})/Cast/$ref`;

    const before = get(`${BASE_URL}/v2/Movies(${refId})/Cast`);
    check(before, { '$ref: new movie starts with no cast': (r) => JSON.parse(r.body).value.length === 0 });

    // §11.4.6.2 -- add a link to an EXISTING entity.
    const added = post(navRef, JSON.stringify({ '@odata.id': `Actors(${UNLINKED_ACTOR_ID})` }), { params: jsonParams() });
    expectStatus(added, 204, '$ref POST (add link)');

    const linked = get(`${BASE_URL}/v2/Movies(${refId})/Cast`);
    check(linked, {
      '$ref POST actually linked the actor': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length === 1 && v[0].Id === UNLINKED_ACTOR_ID;
      },
    });

    const refEnvelope = get(navRef);
    const rb = body(refEnvelope);
    check(refEnvelope, {
      '$ref GET 200': (r) => r.status === 200,
      '$ref GET returns a link envelope': () => rb !== null && Array.isArray(rb.value) && rb.value.length === 1 && typeof rb.value[0]['@odata.id'] === 'string',
    });

    // §11.4.6.3 -- remove the link; the related id travels in $id for a collection navigation.
    const removed = del(`${navRef}?$id=Actors(${UNLINKED_ACTOR_ID})`);
    expectStatus(removed, 204, '$ref DELETE (remove link)');

    const unlinked = get(`${BASE_URL}/v2/Movies(${refId})/Cast`);
    check(unlinked, { '$ref DELETE actually removed the link': (r) => JSON.parse(r.body).value.length === 0 });
  });

  // ── bound operations ───────────────────────────────────────────────────────
  group('bound operations', () => {
    // Collection-bound FUNCTION (§11.5.3): GET /Movies/TopRated?count=5
    const fn = get(`${BASE_URL}/v1/Movies/TopRated?count=5`);
    const fb = body(fn);
    check(fn, {
      'bound function 200': (r) => r.status === 200,
      'bound function returns an OData collection envelope': () => fb !== null && fb['@odata.context'] !== undefined && Array.isArray(fb.value),
      'bound function honours its parameter': () => fb !== null && fb.value.length === 5,
      'bound function really returned the top-rated rows': () => {
        const v = fb.value;
        for (let i = 1; i < v.length; i++) if (v[i].Rating > v[i - 1].Rating) return false;
        return true;
      },
    });

    // Entity-bound ACTION (§11.5.4): POST /Movies({key})/Rate { "rating": 8.5 }
    // Rate folds the new value into a running average, so its effect is observable: the target
    // was created with rating 1.25 / ratingCount 1, so one call must move both.
    if (writeId) {
      const before = body(get(`${BASE_URL}/v1/Movies(${writeId})`));
      const act = post(`${BASE_URL}/v1/Movies(${writeId})/Rate`, JSON.stringify({ rating: 8.5 }), { params: jsonParams() });
      const ab = body(act);
      check(act, {
        'bound action 200': (r) => r.status === 200,
        'bound action returns its DTO': () => ab !== null && ab.MovieId === writeId,
        'bound action incremented RatingCount': () => ab !== null && ab.RatingCount === before.RatingCount + 1,
        'bound action folded the new rating in': () => ab !== null && ab.Rating !== before.Rating,
      });
    }
  });

  // ── @odata.nextLink: follow a link the SERVER issued ───────────────────────
  group('nextLink', () => {
    // MaxTop = 50 on both movie profiles and the catalog is larger, so a bare collection GET
    // pages. Following the server's own link is something TestServer cannot exercise honestly:
    // the link is an absolute URL the server built from the request it actually received.
    const page1 = get(`${BASE_URL}/v1/Movies?$orderby=Id`);
    const b1 = body(page1);
    check(page1, {
      'page 1 is capped at MaxTop': () => b1 !== null && b1.value.length === MOVIE_MAX_TOP,
      'page 1 carries @odata.nextLink': () => b1 !== null && typeof b1['@odata.nextLink'] === 'string',
    });

    if (b1 && typeof b1['@odata.nextLink'] === 'string') {
      const page2 = get(b1['@odata.nextLink']);
      const b2 = body(page2);
      const firstIds = b1 ? b1.value.map((m) => m.Id) : [];
      check(page2, {
        'following @odata.nextLink 200': (r) => r.status === 200,
        'page 2 is non-empty': () => b2 !== null && b2.value.length > 0,
        'page 2 does not repeat page 1': () => b2 !== null && b2.value.every((m) => firstIds.indexOf(m.Id) < 0),
      });
    }
  });

  // ── ETag / conditional write ───────────────────────────────────────────────
  group('etag round-trip', () => {
    if (!etagId) return;

    const read = get(`${BASE_URL}/v1/Movies(${etagId})`);
    const live = header(read, 'ETag');
    check(read, {
      'GET emits an ETag': () => typeof live === 'string' && live.length > 0,
      'ETag is a quoted entity-tag': () => typeof live === 'string' && live.charAt(0) === '"',
    });

    // A stale If-Match must be refused BEFORE the handler runs (#478), so the entity is
    // unchanged afterwards -- asserted, because a 412 that still wrote would look identical.
    const stale = patch(`${BASE_URL}/v1/Movies(${etagId})`, JSON.stringify({ title: 'ShouldNotStick' }),
      { params: jsonParams({ 'If-Match': '"definitely-not-the-current-etag"' }) });
    expectError(stale, 412, 'PreconditionFailed', 'PATCH with a stale If-Match');

    const afterStale = get(`${BASE_URL}/v1/Movies(${etagId})`);
    check(afterStale, {
      'a refused write mutated nothing': (r) => JSON.parse(r.body).Title !== 'ShouldNotStick',
    });

    const fresh = patch(`${BASE_URL}/v1/Movies(${etagId})`, JSON.stringify({ title: 'EtagWriteApplied' }),
      { params: jsonParams({ 'If-Match': live }) });
    check(fresh, {
      'PATCH with the live If-Match 200': (r) => r.status === 200,
      'PATCH with the live If-Match applied the change': (r) => JSON.parse(r.body).Title === 'EtagWriteApplied',
      'the write moved the ETag on': (r) => header(r, 'ETag') !== live,
    });
  });

  // ── writes ─────────────────────────────────────────────────────────────────
  group('POST', () => {
    const res = post(`${BASE_URL}/v1/Movies`, JSON.stringify(newMovie({ title: 'K6NewMovie', genreCode: 'SCIFI' })), { params: p });
    const b = body(res);
    check(res, {
      'POST 201': (r) => r.status === 201,
      // §11.4.2: a 201 must locate the created entity. Real HTTP is the only place these are
      // headers rather than an in-process object. OData-EntityId is deliberately NOT asserted
      // here: §8.3.3 requires it when the response does not carry the entity, which is the
      // Prefer: return=minimal 204 -- conformance.js asserts it there, where it belongs.
      'POST sets Location': (r) => typeof header(r, 'Location') === 'string',
      'POST sets Content-Location': (r) => typeof header(r, 'Content-Location') === 'string',
      'POST sets an ETag on the created entity': (r) => typeof header(r, 'ETag') === 'string',
      'POST echoes the created entity': () => b !== null && b.Id !== undefined && b.Title === 'K6NewMovie',
    });

    if (b && b.Id !== undefined) {
      // The Location the server issued must actually address the entity.
      const located = get(header(res, 'Location'));
      check(located, {
        'the Location header addresses the created entity': (r) => r.status === 200 && JSON.parse(r.body).Id === b.Id,
      });
      del(`${BASE_URL}/v1/Movies(${b.Id})`);
    }
  });

  group('PUT', () => {
    if (!writeId) return;
    const res = put(`${BASE_URL}/v1/Movies(${writeId})`,
      JSON.stringify(newMovie({ id: writeId, title: 'K6Updated', year: 2026, rating: 7.5, genreCode: 'ACTION', studioId: 3 })),
      { params: p });
    check(res, {
      'PUT 200': (r) => r.status === 200,
      'PUT applied the title': (r) => JSON.parse(r.body).Title === 'K6Updated',
      'PUT applied the rating': (r) => JSON.parse(r.body).Rating === 7.5,
    });
  });

  group('PATCH', () => {
    if (!writeId) return;
    const res = patch(`${BASE_URL}/v1/Movies(${writeId})`, JSON.stringify({ rating: 6.25 }), { params: p });
    check(res, {
      'PATCH 200': (r) => r.status === 200,
      'PATCH changed the named property': (r) => JSON.parse(r.body).Rating === 6.25,
      'PATCH left unnamed properties alone': (r) => JSON.parse(r.body).Title === 'K6Updated',
    });
  });

  group('DELETE', () => {
    if (deleteId) {
      expectStatus(del(`${BASE_URL}/v1/Movies(${deleteId})`), 204, 'DELETE existing');
      expectError(get(`${BASE_URL}/v1/Movies(${deleteId})`), 404, 'NotFound', 'GET after DELETE');
    }
    // MovieProfile leaves IdempotentDelete at its default (true), so deleting a key that is not
    // there is a no-op 204 rather than a 404.
    expectStatus(del(`${BASE_URL}/v1/Movies(${MISSING_ID})`), 204, 'DELETE missing key (idempotent)');
  });

  // ── query-option taxonomy: one 501 and one 400 ─────────────────────────────
  group('query option taxonomy', () => {
    // 501 = "can't": $apply is implemented nowhere, so no profile setting turns it on.
    expectUnsupportedOption(
      get(`${BASE_URL}/v1/Movies?$apply=${q('groupby((Year))')}`),
      '$apply on a collection GET');

    // 400 = "won't": $expand IS implemented on this route, and v1's MovieProfile leaves
    // ExpandEnabled false. Same option, same server, 200 on v2 -- which is the point of the
    // taxonomy and is pinned as a control right below.
    expectError(get(`${BASE_URL}/v1/Movies?$expand=Cast`), 400, 'UnsupportedQueryOption',
      '$expand with ExpandEnabled=false');
    expectStatus(get(`${BASE_URL}/v2/Movies?$expand=Cast&$top=1`), 200,
      'control: the same $expand on the set that enables it');
  });

  // ── error cases ────────────────────────────────────────────────────────────
  group('error cases', () => {
    expectError(get(`${BASE_URL}/v1/Movies?$filter=NOTVALID(((`), 400, 'InvalidQueryOption', 'malformed $filter');
    expectError(get(`${BASE_URL}/v1/Movies(notanint)`), 400, 'BadRequest', 'malformed key');
    expectError(post(`${BASE_URL}/v1/Movies`, 'not json at all', { params: jsonParams() }), 400, 'InvalidBody', 'malformed body');
    expectError(post(`${BASE_URL}/v1/Movies`, 'title=x', { params: { headers: { 'Content-Type': 'text/plain' } } }),
      415, 'UnsupportedMediaType', 'non-JSON Content-Type');
  });

  // ── versioning ─────────────────────────────────────────────────────────────
  group('versioning', () => {
    const v1 = get(`${BASE_URL}/v1/Movies`);
    check(v1, { 'v1 context names v1': (r) => JSON.parse(r.body)['@odata.context'].indexOf('/v1/') >= 0 });

    const v2 = get(`${BASE_URL}/v2/Movies`);
    check(v2, { 'v2 context names v2': (r) => JSON.parse(r.body)['@odata.context'].indexOf('/v2/') >= 0 });

    const actors = get(`${BASE_URL}/v2/Actors`);
    check(actors, { 'v2 Actors 200': (r) => r.status === 200, 'v2 Actors returns a collection': (r) => Array.isArray(JSON.parse(r.body).value) });

    // /v1/Actors is not mapped at all, so ASP.NET Core routing answers it -- the request never
    // reaches the OData endpoint group and therefore carries no OData-Version. Asserting the
    // ABSENCE keeps the exemption honest rather than merely granted.
    expectStatus(get(`${BASE_URL}/v1/Actors`, { odataVersion: false }), 404, 'v1 has no Actors set');

    const genres = get(`${BASE_URL}/v1/Genres`);
    check(genres, {
      'v1 Genres 200': (r) => r.status === 200,
      'Genres is the full static lookup': (r) => JSON.parse(r.body).value.length === SEEDED_GENRE_COUNT,
    });
    expectStatus(get(`${BASE_URL}/v2/Genres`), 200, 'v2 Genres');
  });
}

export function handleSummary(data) {
  return reports('smoke', data);
}
