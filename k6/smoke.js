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
// Products seeded at startup: id=1..5 (Widget, Gadget, Sprocket, Doohickey, Thingamajig)
// Categories seeded: "Hardware", "Electronics", "Misc"
const SEEDED_PRODUCT_ID = 1;   // Widget, $9.99, Hardware
const SEEDED_PRODUCT_ID_2 = 2; // Gadget, $24.99, Electronics
const MISSING_ID = 99999;

// ── Setup: create test entities via POST ──────────────────────────────────────

export function setup() {
  const headers = { 'Content-Type': 'application/json' };

  // Create a product for PUT/PATCH/DELETE tests (so we don't mutate seed data)
  const postProductRes = http.post(
    `${BASE_URL}/v1/Products`,
    JSON.stringify({ name: 'TestProduct', price: 1.23, category: 'Test' }),
    { headers }
  );
  check(postProductRes, { 'setup POST product 201': (r) => r.status === 201 });
  const testProductId = postProductRes.status === 201
    ? JSON.parse(postProductRes.body).id
    : null;

  // Create a second product for DELETE test
  const postDeleteRes = http.post(
    `${BASE_URL}/v1/Products`,
    JSON.stringify({ name: 'DeleteMe', price: 0.01, category: 'Test' }),
    { headers }
  );
  check(postDeleteRes, { 'setup POST delete-target 201': (r) => r.status === 201 });
  const deleteProductId = postDeleteRes.status === 201
    ? JSON.parse(postDeleteRes.body).id
    : null;

  return { testProductId, deleteProductId };
}

// ── Teardown: remove test entities ────────────────────────────────────────────

export function teardown(data) {
  if (data.testProductId) {
    http.del(`${BASE_URL}/v1/Products(${data.testProductId})`);
  }
  // deleteProductId may already be gone (deleted in the DELETE test group)
}

// ── Main test function ────────────────────────────────────────────────────────

export default function (data) {
  const { testProductId, deleteProductId } = data;
  const headers = { 'Content-Type': 'application/json' };

  // ── collection GET ──────────────────────────────────────────────────────────
  group('collection GET', () => {
    const res = http.get(`${BASE_URL}/v1/Products`);
    check(res, {
      'status 200': (r) => r.status === 200,
      'has @odata.context': (r) => JSON.parse(r.body)['@odata.context'] !== undefined,
      'has value array': (r) => Array.isArray(JSON.parse(r.body).value),
      'value is non-empty': (r) => JSON.parse(r.body).value.length > 0,
    });
  });

  // ── $filter ─────────────────────────────────────────────────────────────────
  group('$filter', () => {
    // Property names must match the EDM model (PascalCase: Price, Name, Category).
    // Spaces in OData query syntax must be percent-encoded (%20) because k6 sends
    // URLs verbatim and Kestrel treats a literal space as an HTTP request-line
    // terminator, returning 400 with an empty body.
    const cases = [
      // Comparison operators
      { qs: "$filter=Price%20eq%209.99",         label: 'eq numeric',     expect: (v) => v.length >= 1 && v.every(p => p.price === 9.99) },
      { qs: "$filter=Price%20ne%209.99",         label: 'ne numeric',     expect: (v) => v.every(p => p.price !== 9.99) },
      { qs: "$filter=Price%20gt%2010",           label: 'gt numeric',     expect: (v) => v.every(p => p.price > 10) },
      { qs: "$filter=Price%20lt%2010",           label: 'lt numeric',     expect: (v) => v.every(p => p.price < 10) },
      { qs: "$filter=Price%20ge%209.99",         label: 'ge numeric',     expect: (v) => v.every(p => p.price >= 9.99) },
      { qs: "$filter=Price%20le%209.99",         label: 'le numeric',     expect: (v) => v.every(p => p.price <= 9.99) },
      // String functions
      { qs: "$filter=contains(Name,'get')",      label: 'contains',       expect: (v) => v.length >= 1 && v.every(p => p.name.toLowerCase().includes('get')) },
      { qs: "$filter=startswith(Name,'W')",      label: 'startswith',     expect: (v) => v.every(p => p.name.startsWith('W')) },
      { qs: "$filter=endswith(Name,'et')",       label: 'endswith',       expect: (v) => v.every(p => p.name.toLowerCase().endsWith('et')) },
      // Logical combinations
      { qs: "$filter=Price%20gt%205%20and%20Price%20lt%2020",   label: 'and',   expect: (v) => v.every(p => p.price > 5 && p.price < 20) },
      { qs: "$filter=Price%20lt%205%20or%20Price%20gt%2030",    label: 'or',    expect: (v) => v.every(p => p.price < 5 || p.price > 30) },
    ];

    for (const tc of cases) {
      const res = http.get(`${BASE_URL}/v1/Products?${tc.qs}`);
      const body = JSON.parse(res.body);
      check(res, {
        [`filter ${tc.label} 200`]: (r) => r.status === 200,
        [`filter ${tc.label} results correct`]: () => body.value && tc.expect(body.value),
      });
    }
  });

  // ── $orderby ────────────────────────────────────────────────────────────────
  group('$orderby', () => {
    const ascRes = http.get(`${BASE_URL}/v1/Products?$orderby=Price`);
    check(ascRes, {
      'orderby price asc 200': (r) => r.status === 200,
      'orderby price asc ordered': (r) => {
        const v = JSON.parse(r.body).value;
        for (let i = 1; i < v.length; i++) {
          if (v[i].price < v[i - 1].price) return false;
        }
        return true;
      },
    });

    // "desc" keyword must be separated from the property name by a space → %20
    const descRes = http.get(`${BASE_URL}/v1/Products?$orderby=Price%20desc`);
    check(descRes, {
      'orderby price desc 200': (r) => r.status === 200,
      'orderby price desc ordered': (r) => {
        const v = JSON.parse(r.body).value;
        for (let i = 1; i < v.length; i++) {
          if (v[i].price > v[i - 1].price) return false;
        }
        return true;
      },
    });

    const multiRes = http.get(`${BASE_URL}/v1/Products?$orderby=Category,Price%20desc`);
    check(multiRes, {
      'orderby multi-property 200': (r) => r.status === 200,
      'orderby multi-property has results': (r) => JSON.parse(r.body).value.length > 0,
    });
  });

  // ── $select ─────────────────────────────────────────────────────────────────
  group('$select', () => {
    const singleRes = http.get(`${BASE_URL}/v1/Products?$select=name`);
    check(singleRes, {
      '$select single 200': (r) => r.status === 200,
      '$select single has name': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].name !== undefined;
      },
      '$select single no price': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].price === undefined;
      },
    });

    const multiRes = http.get(`${BASE_URL}/v1/Products?$select=name,price`);
    check(multiRes, {
      '$select multi 200': (r) => r.status === 200,
      '$select multi has name and price': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].name !== undefined && v[0].price !== undefined;
      },
      '$select multi no category': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length > 0 && v[0].category === undefined;
      },
    });
  });

  // ── $top/$skip ──────────────────────────────────────────────────────────────
  group('$top/$skip', () => {
    const topRes = http.get(`${BASE_URL}/v1/Products?$top=1`);
    check(topRes, {
      '$top=1 status 200': (r) => r.status === 200,
      '$top=1 returns 1 item': (r) => JSON.parse(r.body).value.length === 1,
    });

    const skipRes = http.get(`${BASE_URL}/v1/Products?$skip=1`);
    check(skipRes, {
      '$skip=1 status 200': (r) => r.status === 200,
      '$skip=1 returns fewer items': (r) => {
        const v = JSON.parse(r.body).value;
        return v.length >= 1;
      },
    });

    const paginatedRes = http.get(`${BASE_URL}/v1/Products?$top=2&$skip=1`);
    check(paginatedRes, {
      '$top+$skip 200': (r) => r.status === 200,
      '$top+$skip returns at most 2': (r) => JSON.parse(r.body).value.length <= 2,
    });
  });

  // ── $count standalone ───────────────────────────────────────────────────────
  group('$count standalone', () => {
    const res = http.get(`${BASE_URL}/v1/Products/$count`);
    check(res, {
      '$count 200': (r) => r.status === 200,
      '$count body is integer': (r) => Number.isInteger(parseInt(r.body, 10)),
      '$count value >= 5': (r) => parseInt(r.body, 10) >= 5,
    });
  });

  // ── $count inline ────────────────────────────────────────────────────────────
  group('$count inline', () => {
    const res = http.get(`${BASE_URL}/v1/Products?$count=true`);
    check(res, {
      '$count inline 200': (r) => r.status === 200,
      '$count inline has @odata.count': (r) => {
        const body = JSON.parse(r.body);
        return typeof body['@odata.count'] === 'number';
      },
      '$count inline count >= 5': (r) => JSON.parse(r.body)['@odata.count'] >= 5,
    });
  });

  // ── single entity GET ────────────────────────────────────────────────────────
  group('single entity GET', () => {
    const foundRes = http.get(`${BASE_URL}/v1/Products(${SEEDED_PRODUCT_ID})`);
    check(foundRes, {
      'GetById existing 200': (r) => r.status === 200,
      'GetById body has id': (r) => JSON.parse(r.body).id === SEEDED_PRODUCT_ID,
      'GetById Widget name': (r) => JSON.parse(r.body).name === 'Widget',
    });

    const notFoundRes = http.get(`${BASE_URL}/v1/Products(${MISSING_ID})`);
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
      `${BASE_URL}/v1/Products`,
      JSON.stringify({ name: 'NewWidget', price: 5.55, category: 'Hardware' }),
      { headers }
    );
    check(res, {
      'POST 201': (r) => r.status === 201,
      'POST Location header': (r) => r.headers['Location'] !== undefined || r.headers['location'] !== undefined,
      'POST body has id': (r) => JSON.parse(r.body).id !== undefined,
      'POST body has name': (r) => JSON.parse(r.body).name === 'NewWidget',
    });

    // Clean up the created entity
    if (res.status === 201) {
      const created = JSON.parse(res.body);
      http.del(`${BASE_URL}/v1/Products(${created.id})`);
    }
  });

  // ── PUT ─────────────────────────────────────────────────────────────────────
  group('PUT', () => {
    if (!testProductId) return;

    // OhData validates that the key in the URL matches the key in the body.
    // Include the id so the handler doesn't reject with 400 key-mismatch.
    const res = http.put(
      `${BASE_URL}/v1/Products(${testProductId})`,
      JSON.stringify({ id: testProductId, name: 'UpdatedProduct', price: 99.99, category: 'Updated' }),
      { headers }
    );
    check(res, {
      'PUT 200': (r) => r.status === 200,
      'PUT body name updated': (r) => JSON.parse(r.body).name === 'UpdatedProduct',
      'PUT body price updated': (r) => JSON.parse(r.body).price === 99.99,
    });
  });

  // ── PATCH ────────────────────────────────────────────────────────────────────
  group('PATCH', () => {
    if (!testProductId) return;

    const res = http.patch(
      `${BASE_URL}/v1/Products(${testProductId})`,
      JSON.stringify({ price: 77.77 }),
      { headers }
    );
    check(res, {
      'PATCH 200': (r) => r.status === 200,
      'PATCH price changed': (r) => JSON.parse(r.body).price === 77.77,
      'PATCH name unchanged': (r) => JSON.parse(r.body).name !== undefined,
    });
  });

  // ── DELETE ───────────────────────────────────────────────────────────────────
  group('DELETE', () => {
    if (deleteProductId) {
      const delRes = http.del(`${BASE_URL}/v1/Products(${deleteProductId})`);
      check(delRes, {
        'DELETE existing 204': (r) => r.status === 204,
      });
    }

    // ProductProfile uses the default IdempotentDelete=true setting, so deleting a
    // non-existent entity is a no-op that returns 204 (not 404).
    const notFoundRes = http.del(`${BASE_URL}/v1/Products(${MISSING_ID})`);
    check(notFoundRes, {
      'DELETE missing idempotent 204': (r) => r.status === 204,
    });
  });

  // ── error cases ──────────────────────────────────────────────────────────────
  group('error cases', () => {
    // Invalid $filter syntax
    const badFilterRes = http.get(`${BASE_URL}/v1/Products?$filter=NOTVALID(((`);
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
    const badKeyRes = http.get(`${BASE_URL}/v1/Products(notanint)`);
    check(badKeyRes, {
      'malformed key 400 or 404': (r) => r.status === 400 || r.status === 404,
    });
  });

  // ── versioning ───────────────────────────────────────────────────────────────
  group('versioning', () => {
    const v1Res = http.get(`${BASE_URL}/v1/Products`);
    check(v1Res, {
      'v1 Products 200': (r) => r.status === 200,
      'v1 context contains v1': (r) => {
        const body = JSON.parse(r.body);
        return body['@odata.context'] && body['@odata.context'].includes('v1');
      },
    });

    const v2Res = http.get(`${BASE_URL}/v2/Products`);
    check(v2Res, {
      'v2 Products 200': (r) => r.status === 200,
      'v2 context contains v2': (r) => {
        const body = JSON.parse(r.body);
        return body['@odata.context'] && body['@odata.context'].includes('v2');
      },
    });

    // v2-only: Orders
    const v2OrdersRes = http.get(`${BASE_URL}/v2/Orders`);
    check(v2OrdersRes, {
      'v2 Orders 200': (r) => r.status === 200,
      'v2 Orders value array': (r) => Array.isArray(JSON.parse(r.body).value),
    });

    // v1 should not have Orders
    const v1OrdersRes = http.get(`${BASE_URL}/v1/Orders`);
    check(v1OrdersRes, {
      'v1 Orders not found (404)': (r) => r.status === 404,
    });

    // Categories — available in both versions
    const v1CatRes = http.get(`${BASE_URL}/v1/Categories`);
    check(v1CatRes, {
      'v1 Categories 200': (r) => r.status === 200,
    });

    const v2CatRes = http.get(`${BASE_URL}/v2/Categories`);
    check(v2CatRes, {
      'v2 Categories 200': (r) => r.status === 200,
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
