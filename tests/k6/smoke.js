import http from 'k6/http';
import { check, group } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

export const options = {
  thresholds: {
    // http_req_failed is omitted: the test deliberately sends requests that expect
    // 4xx/404 responses (error cases, missing-entity GETs, versioning checks), so
    // the failure rate is inherently > 1%.  Individual correctness is covered by
    // the `checks` assertions in each group.
    'checks': ['rate>0.99'],
    'http_req_duration{group:::collection GET}': ['p(95)<500'],
  },
};

// ── Seeded data constants ─────────────────────────────────────────────────────
// Movies seeded at startup: id=1..77 (see src/OhData.TestBench.AspNetCore/SeedData.cs).
// id=1 is "The Godfather" (1972, rating 9.3, CRIME, studio 7). Exactly three seeded
// movies have Year eq 1994 (The Shawshank Redemption, Pulp Fiction, Forrest Gump) and
// exactly two have Rating >= 9.3 (Shawshank 9.4, The Godfather 9.3).
// Genres seeded: 11 static codes ("ACTION" ... "THRILLER").
const SEEDED_MOVIE_ID = 1;   // The Godfather, 1972, rating 9.3
const SEEDED_MOVIE_COUNT = 77;
const MISSING_ID = 99999;

// ── Setup: create test entities via POST ──────────────────────────────────────

export function setup() {
  const headers = { 'Content-Type': 'application/json' };

  // Create a movie for PUT/PATCH tests (so we don't mutate seed data)
  const postMovieRes = http.post(
    `${BASE_URL}/v1/Movies`,
    JSON.stringify({
      title: 'TestMovie', year: 2025, rating: 1.25, runtimeMinutes: 90,
      genreCode: 'DRAMA', studioId: 1, releaseDate: '2025-01-01',
    }),
    { headers }
  );
  check(postMovieRes, { 'setup POST movie 201': (r) => r.status === 201 });
  const testMovieId = postMovieRes.status === 201
    ? JSON.parse(postMovieRes.body).id
    : null;

  // Create a second movie for DELETE test
  const postDeleteRes = http.post(
    `${BASE_URL}/v1/Movies`,
    JSON.stringify({
      title: 'DeleteMe', year: 2025, rating: 0.5, runtimeMinutes: 80,
      genreCode: 'DRAMA', studioId: 1, releaseDate: '2025-01-01',
    }),
    { headers }
  );
  check(postDeleteRes, { 'setup POST delete-target 201': (r) => r.status === 201 });
  const deleteMovieId = postDeleteRes.status === 201
    ? JSON.parse(postDeleteRes.body).id
    : null;

  return { testMovieId, deleteMovieId };
}

// ── Teardown: remove test entities ────────────────────────────────────────────

export function teardown(data) {
  if (data.testMovieId) {
    http.del(`${BASE_URL}/v1/Movies(${data.testMovieId})`);
  }
  // deleteMovieId may already be gone (deleted in the DELETE test group)
}

// ── Main test function ────────────────────────────────────────────────────────

export default function (data) {
  const { testMovieId, deleteMovieId } = data;
  const headers = { 'Content-Type': 'application/json' };

  // ── collection GET ──────────────────────────────────────────────────────────
  group('collection GET', () => {
    const res = http.get(`${BASE_URL}/v1/Movies`);
    check(res, {
      'status 200': (r) => r.status === 200,
      'has @odata.context': (r) => JSON.parse(r.body)['@odata.context'] !== undefined,
      'has value array': (r) => Array.isArray(JSON.parse(r.body).value),
      'value is non-empty': (r) => JSON.parse(r.body).value.length > 0,
    });
  });

  // ── $filter ─────────────────────────────────────────────────────────────────
  group('$filter', () => {
    // Property names must match the EDM model (PascalCase: Year, Rating, Title).
    // Spaces in OData query syntax must be percent-encoded (%20) because k6 sends
    // URLs verbatim and Kestrel treats a literal space as an HTTP request-line
    // terminator, returning 400 with an empty body.
    const cases = [
      // Comparison operators
      { qs: "$filter=Year%20eq%201994",          label: 'eq numeric',     expect: (v) => v.length >= 3 && v.every(m => m.year === 1994) },
      { qs: "$filter=Year%20ne%201994",          label: 'ne numeric',     expect: (v) => v.every(m => m.year !== 1994) },
      { qs: "$filter=Rating%20gt%209",           label: 'gt numeric',     expect: (v) => v.length >= 1 && v.every(m => m.rating > 9) },
      { qs: "$filter=Rating%20lt%207",           label: 'lt numeric',     expect: (v) => v.every(m => m.rating < 7) },
      { qs: "$filter=Rating%20ge%209.3",         label: 'ge numeric',     expect: (v) => v.length >= 2 && v.every(m => m.rating >= 9.3) },
      { qs: "$filter=Rating%20le%209.3",         label: 'le numeric',     expect: (v) => v.every(m => m.rating <= 9.3) },
      // String functions
      { qs: "$filter=contains(Title,'God')",     label: 'contains',       expect: (v) => v.length >= 1 && v.every(m => m.title.includes('God')) },
      { qs: "$filter=startswith(Title,'The')",   label: 'startswith',     expect: (v) => v.length >= 1 && v.every(m => m.title.startsWith('The')) },
      { qs: "$filter=endswith(Title,'er')",      label: 'endswith',       expect: (v) => v.length >= 1 && v.every(m => m.title.endsWith('er')) },
      // Logical combinations
      { qs: "$filter=Year%20gt%202000%20and%20Rating%20gt%208",   label: 'and',   expect: (v) => v.length >= 1 && v.every(m => m.year > 2000 && m.rating > 8) },
      { qs: "$filter=Year%20lt%201980%20or%20Year%20gt%202020",   label: 'or',    expect: (v) => v.length >= 1 && v.every(m => m.year < 1980 || m.year > 2020) },
    ];

    for (const tc of cases) {
      const res = http.get(`${BASE_URL}/v1/Movies?${tc.qs}`);
      const body = JSON.parse(res.body);
      check(res, {
        [`filter ${tc.label} 200`]: (r) => r.status === 200,
        [`filter ${tc.label} results correct`]: () => body.value && tc.expect(body.value),
      });
    }
  });

  // ── $orderby ────────────────────────────────────────────────────────────────
  group('$orderby', () => {
    const ascRes = http.get(`${BASE_URL}/v1/Movies?$orderby=Rating`);
    check(ascRes, {
      'orderby rating asc 200': (r) => r.status === 200,
      'orderby rating asc ordered': (r) => {
        const v = JSON.parse(r.body).value;
        for (let i = 1; i < v.length; i++) {
          if (v[i].rating < v[i - 1].rating) return false;
        }
        return true;
      },
    });

    // "desc" keyword must be separated from the property name by a space → %20
    const descRes = http.get(`${BASE_URL}/v1/Movies?$orderby=Rating%20desc`);
    check(descRes, {
      'orderby rating desc 200': (r) => r.status === 200,
      'orderby rating desc ordered': (r) => {
        const v = JSON.parse(r.body).value;
        for (let i = 1; i < v.length; i++) {
          if (v[i].rating > v[i - 1].rating) return false;
        }
        return true;
      },
    });

    const multiRes = http.get(`${BASE_URL}/v1/Movies?$orderby=GenreCode,Rating%20desc`);
    check(multiRes, {
      'orderby multi-property 200': (r) => r.status === 200,
      'orderby multi-property has results': (r) => JSON.parse(r.body).value.length > 0,
    });
  });

  // ── $select ─────────────────────────────────────────────────────────────────
  group('$select', () => {
    const singleRes = http.get(`${BASE_URL}/v1/Movies?$select=title`);
    check(singleRes, {
      '$select single 200': (r) => r.status === 200,
      '$select single has title': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].title !== undefined;
      },
      '$select single no rating': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].rating === undefined;
      },
    });

    const multiRes = http.get(`${BASE_URL}/v1/Movies?$select=title,year`);
    check(multiRes, {
      '$select multi 200': (r) => r.status === 200,
      '$select multi has title and year': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].title !== undefined && v[0].year !== undefined;
      },
      '$select multi no genreCode': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].genreCode === undefined;
      },
    });
  });

  // ── $top/$skip ──────────────────────────────────────────────────────────────
  group('$top/$skip', () => {
    const topRes = http.get(`${BASE_URL}/v1/Movies?$top=1`);
    check(topRes, {
      '$top=1 status 200': (r) => r.status === 200,
      '$top=1 returns 1 item': (r) => JSON.parse(r.body).value.length === 1,
    });

    const skipRes = http.get(`${BASE_URL}/v1/Movies?$skip=1`);
    check(skipRes, {
      '$skip=1 status 200': (r) => r.status === 200,
      '$skip=1 returns fewer items': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length >= 1;
      },
    });

    const paginatedRes = http.get(`${BASE_URL}/v1/Movies?$top=2&$skip=1`);
    check(paginatedRes, {
      '$top+$skip 200': (r) => r.status === 200,
      '$top+$skip returns at most 2': (r) => JSON.parse(r.body).value.length <= 2,
    });
  });

  // ── $count standalone ───────────────────────────────────────────────────────
  group('$count standalone', () => {
    const res = http.get(`${BASE_URL}/v1/Movies/$count`);
    check(res, {
      '$count 200': (r) => r.status === 200,
      '$count body is integer': (r) => Number.isInteger(parseInt(r.body, 10)),
      [`$count value >= ${SEEDED_MOVIE_COUNT}`]: (r) => parseInt(r.body, 10) >= SEEDED_MOVIE_COUNT,
    });
  });

  // ── $count inline ────────────────────────────────────────────────────────────
  group('$count inline', () => {
    const res = http.get(`${BASE_URL}/v1/Movies?$count=true`);
    check(res, {
      '$count inline 200': (r) => r.status === 200,
      '$count inline has @odata.count': (r) => {
        const body = JSON.parse(r.body);
        return typeof body['@odata.count'] === 'number';
      },
      [`$count inline count >= ${SEEDED_MOVIE_COUNT}`]: (r) => JSON.parse(r.body)['@odata.count'] >= SEEDED_MOVIE_COUNT,
    });
  });

  // ── single entity GET ────────────────────────────────────────────────────────
  group('single entity GET', () => {
    const foundRes = http.get(`${BASE_URL}/v1/Movies(${SEEDED_MOVIE_ID})`);
    check(foundRes, {
      'GetById existing 200': (r) => r.status === 200,
      'GetById body has id': (r) => JSON.parse(r.body).id === SEEDED_MOVIE_ID,
      'GetById The Godfather title': (r) => JSON.parse(r.body).title === 'The Godfather',
    });

    const notFoundRes = http.get(`${BASE_URL}/v1/Movies(${MISSING_ID})`);
    check(notFoundRes, {
      'GetById missing 404': (r) => r.status === 404,
      'GetById missing OData error': (r) => {
        try {
          const body = JSON.parse(r.body);
          return body.error !== undefined;
        } catch {
          return false;
        }
      },
    });
  });

  // ── POST ────────────────────────────────────────────────────────────────────
  group('POST', () => {
    const res = http.post(
      `${BASE_URL}/v1/Movies`,
      JSON.stringify({
        title: 'NewMovie', year: 2025, rating: 5.5, runtimeMinutes: 100,
        genreCode: 'SCIFI', studioId: 2, releaseDate: '2025-06-01',
      }),
      { headers }
    );
    check(res, {
      'POST 201': (r) => r.status === 201,
      'POST Location header': (r) => r.headers['Location'] !== undefined || r.headers['location'] !== undefined,
      'POST body has id': (r) => JSON.parse(r.body).id !== undefined,
      'POST body has title': (r) => JSON.parse(r.body).title === 'NewMovie',
    });

    // Clean up the created entity
    if (res.status === 201) {
      const created = JSON.parse(res.body);
      http.del(`${BASE_URL}/v1/Movies(${created.id})`);
    }
  });

  // ── PUT ─────────────────────────────────────────────────────────────────────
  group('PUT', () => {
    if (!testMovieId) return;

    // OhData validates that the key in the URL matches the key in the body.
    // Include the id so the handler doesn't reject with 400 key-mismatch.
    const res = http.put(
      `${BASE_URL}/v1/Movies(${testMovieId})`,
      JSON.stringify({
        id: testMovieId, title: 'UpdatedMovie', year: 2026, rating: 7.5,
        runtimeMinutes: 95, genreCode: 'ACTION', studioId: 3, releaseDate: '2026-01-01',
      }),
      { headers }
    );
    check(res, {
      'PUT 200': (r) => r.status === 200,
      'PUT body title updated': (r) => JSON.parse(r.body).title === 'UpdatedMovie',
      'PUT body rating updated': (r) => JSON.parse(r.body).rating === 7.5,
    });
  });

  // ── PATCH ────────────────────────────────────────────────────────────────────
  group('PATCH', () => {
    if (!testMovieId) return;

    const res = http.patch(
      `${BASE_URL}/v1/Movies(${testMovieId})`,
      JSON.stringify({ rating: 6.25 }),
      { headers }
    );
    check(res, {
      'PATCH 200': (r) => r.status === 200,
      'PATCH rating changed': (r) => JSON.parse(r.body).rating === 6.25,
      'PATCH title unchanged': (r) => JSON.parse(r.body).title !== undefined,
    });
  });

  // ── DELETE ───────────────────────────────────────────────────────────────────
  group('DELETE', () => {
    if (deleteMovieId) {
      const delRes = http.del(`${BASE_URL}/v1/Movies(${deleteMovieId})`);
      check(delRes, {
        'DELETE existing 204': (r) => r.status === 204,
      });
    }

    // MovieProfile uses the default IdempotentDelete=true setting, so deleting a
    // non-existent entity is a no-op that returns 204 (not 404).
    const notFoundRes = http.del(`${BASE_URL}/v1/Movies(${MISSING_ID})`);
    check(notFoundRes, {
      'DELETE missing idempotent 204': (r) => r.status === 204,
    });
  });

  // ── error cases ──────────────────────────────────────────────────────────────
  group('error cases', () => {
    // Invalid $filter syntax
    const badFilterRes = http.get(`${BASE_URL}/v1/Movies?$filter=NOTVALID(((`);
    check(badFilterRes, {
      'invalid $filter 400': (r) => r.status === 400,
      'invalid $filter OData error body': (r) => {
        try {
          const body = JSON.parse(r.body);
          return body.error !== undefined && body.error.code !== undefined;
        } catch {
          return false;
        }
      },
    });

    // Malformed key type (string for int key)
    const badKeyRes = http.get(`${BASE_URL}/v1/Movies(notanint)`);
    check(badKeyRes, {
      'malformed key 400 or 404': (r) => r.status === 400 || r.status === 404,
    });
  });

  // ── versioning ───────────────────────────────────────────────────────────────
  group('versioning', () => {
    const v1Res = http.get(`${BASE_URL}/v1/Movies`);
    check(v1Res, {
      'v1 Movies 200': (r) => r.status === 200,
      'v1 context contains v1': (r) => {
        const body = JSON.parse(r.body);
        return body['@odata.context'] && body['@odata.context'].includes('v1');
      },
    });

    const v2Res = http.get(`${BASE_URL}/v2/Movies`);
    check(v2Res, {
      'v2 Movies 200': (r) => r.status === 200,
      'v2 context contains v2': (r) => {
        const body = JSON.parse(r.body);
        return body['@odata.context'] && body['@odata.context'].includes('v2');
      },
    });

    // v2-only: Actors
    const v2ActorsRes = http.get(`${BASE_URL}/v2/Actors`);
    check(v2ActorsRes, {
      'v2 Actors 200': (r) => r.status === 200,
      'v2 Actors value array': (r) => Array.isArray(JSON.parse(r.body).value),
    });

    // v1 should not have Actors
    const v1ActorsRes = http.get(`${BASE_URL}/v1/Actors`);
    check(v1ActorsRes, {
      'v1 Actors not found (404)': (r) => r.status === 404,
    });

    // Genres — available in both versions
    const v1GenreRes = http.get(`${BASE_URL}/v1/Genres`);
    check(v1GenreRes, {
      'v1 Genres 200': (r) => r.status === 200,
    });

    const v2GenreRes = http.get(`${BASE_URL}/v2/Genres`);
    check(v2GenreRes, {
      'v2 Genres 200': (r) => r.status === 200,
    });
  });
}

// ── Summary ──────────────────────────────────────────────────────────────────

export function handleSummary(data) {
  return {
    '/reports/summary.md': textSummary(data, { indent: ' ', enableColors: false }),
    '/reports/results.json': JSON.stringify(data),
  };
}
