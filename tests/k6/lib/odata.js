// Small assertion helpers shared by smoke.js and conformance.js. Helpers only -- no framework,
// no runner, no indirection that hides which request produced a failure.
//
// The one thing this file really buys is the OData-Version assertion. Protocol §8.1.5 requires
// OData-Version on EVERY response, success and error alike, and OhData writes it from the
// OUTERMOST group filter specifically so a response an inner filter short-circuits (the #203
// Content-Length 413, the $format 400, the OData-MaxVersion 400) still carries it -- #496 finding
// 2 is exactly that header having previously been written fourth of five filters and going
// missing on the 413. A header that must be universal has to be asserted universally or the
// assertion proves nothing, so every request in both scripts goes through `req` below and pays
// one check for it automatically.

import http from 'k6/http';
import { check } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { BASE_URL } from './seed.js';

// Collapses a URL to a stable route shape: strips the origin, the query string, and the key
// inside (...). Used only as a check LABEL, so the k6 metric keeps one series per route family
// rather than one per distinct query string, while still naming the route that failed.
export function routeLabel(url) {
  return String(url)
    .replace(BASE_URL, '')
    .split('?')[0]
    .replace(/\([^)]*\)/g, '({key})');
}

/**
 * Every request either script makes. Asserts OData-Version: 4.0 on the response.
 *
 * opts.odataVersion === false exempts a request that is expected NEVER TO REACH the OData
 * endpoint group -- an unmapped path answered by ASP.NET Core routing itself. That case asserts
 * the header is ABSENT instead, so the exemption is pinned rather than merely granted.
 */
export function req(method, url, body, opts) {
  opts = opts || {};
  const params = opts.params || {};
  const res = http.request(method, url, body, params);
  const where = `${method} ${routeLabel(url)}`;

  if (opts.odataVersion === false) {
    check(res, {
      [`no OData-Version off the OData group [${where}]`]: (r) => header(r, 'OData-Version') === undefined,
    });
  } else {
    check(res, {
      [`OData-Version: 4.0 [${where}]`]: (r) => header(r, 'OData-Version') === '4.0',
    });
  }
  return res;
}

export const get = (url, opts) => req('GET', url, null, opts);
export const post = (url, body, opts) => req('POST', url, body, opts);
export const put = (url, body, opts) => req('PUT', url, body, opts);
export const patch = (url, body, opts) => req('PATCH', url, body, opts);
export const del = (url, body, opts) => req('DELETE', url, body, opts);

export const JSON_HEADERS = { 'Content-Type': 'application/json' };

/** Request params carrying a JSON content type plus any extra headers. */
export function jsonParams(extraHeaders) {
  return { headers: Object.assign({}, JSON_HEADERS, extraHeaders || {}) };
}

/** Case-insensitive header read: k6 normalises most header names, but not reliably all. */
export function header(res, name) {
  const lower = name.toLowerCase();
  for (const k of Object.keys(res.headers)) {
    if (k.toLowerCase() === lower) return res.headers[k];
  }
  return undefined;
}

/** JSON.parse that never throws; returns null when the body is not JSON. */
export function body(res) {
  try {
    return JSON.parse(res.body);
  } catch (e) {
    return null;
  }
}

/** Asserts the status code alone. Use only where the status IS the whole behaviour. */
export function expectStatus(res, expected, label) {
  return check(res, {
    [`${label} -> ${expected}`]: (r) => r.status === expected,
  });
}

/**
 * Asserts an OData error response: status, the {"error":{"code","message"}} envelope shape, and
 * the error code. Status alone is not enough -- §9.4 fixes the envelope, and the #495 defects
 * (a host DictionaryKeyPolicy rewriting `error`/`code`, a throwing converter emptying the body)
 * both ship the right STATUS with an unreadable body.
 */
export function expectError(res, status, code, label) {
  const b = body(res);
  return check(res, {
    [`${label} -> ${status}`]: (r) => r.status === status,
    [`${label} -> error envelope`]: () =>
      b !== null && b.error !== undefined &&
      typeof b.error.code === 'string' && b.error.code.length > 0 &&
      typeof b.error.message === 'string' && b.error.message.length > 0,
    [`${label} -> code ${code}`]: () => b !== null && b.error !== undefined && b.error.code === code,
  });
}

/** §11.2.5 + §9.3.1 refusal of a $-option the addressed route does not implement. */
export function expectUnsupportedOption(res, label) {
  return expectError(res, 501, 'UnsupportedQueryOption', label);
}

/** Percent-encodes a query-option VALUE. k6 sends URLs verbatim and Kestrel treats a literal
 *  space in the request line as a terminator, so every value with a space must be encoded. */
export function q(value) {
  return encodeURIComponent(value);
}

// ── Reporting ────────────────────────────────────────────────────────────────
// Both scripts run against ONE container start (see tests/docker-compose.k6.yml), so their
// reports must not collide on one filename -- the second run would overwrite the first and CI
// would upload half the evidence. `stdout` is included deliberately: defining handleSummary
// REPLACES k6's default end-of-test summary, and the previous smoke.js returned only file keys,
// so the CI console printed no summary at all.
export function reports(name, data) {
  const text = textSummary(data, { indent: ' ', enableColors: false });
  const out = { stdout: text };
  out[`/reports/${name}-summary.md`] = text;
  out[`/reports/${name}-results.json`] = JSON.stringify(data);
  return out;
}
