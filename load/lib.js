// Shared plumbing for every scenario: data loading, dev-token minting, the common ramp.
import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';
import papaparse from 'https://jslib.k6.io/papaparse/5.1.1/index.js';

export const API = __ENV.API_URL || 'http://localhost:8081';
export const GATEWAY = __ENV.GATEWAY_URL || 'http://localhost:8082';

// Ramp to the breaking point, not to a comfortable number. Aborts on the thresholds.
export const rampOptions = {
  stages: [
    { duration: '2m', target: 50 },
    { duration: '2m', target: 200 },
    { duration: '2m', target: 500 },
    { duration: '2m', target: 1000 },
    { duration: '2m', target: 2000 },
  ],
  thresholds: {
    http_req_failed: [{ threshold: 'rate<0.01', abortOnFail: true }],
    http_req_duration: [{ threshold: 'p(99)<2000', abortOnFail: true }],
  },
};

// A bounded random sample exported by load/README.md's \copy command.
export const statements = new SharedArray('statements', () =>
  papaparse.parse(open('./data/statements.csv'), { header: true }).data
    .filter((r) => r.customer_id));

export function pick() {
  return statements[Math.floor(Math.random() * statements.length)];
}

// Dev-only token mint; cached per VU so the mint does not dominate the measurement.
const tokenCache = {};
export function tokenFor(customerId, staff = false) {
  const key = customerId + (staff ? ':staff' : '');
  if (!tokenCache[key]) {
    const res = http.post(
      `${API}/v1/dev/tokens?customerId=${customerId}&staff=${staff}`, null,
      { tags: { name: 'dev-token' } });
    check(res, { 'token minted': (r) => r.status === 200 });
    tokenCache[key] = res.json('accessToken');
  }
  return tokenCache[key];
}

export function authHeaders(customerId, staff = false) {
  return { headers: { Authorization: `Bearer ${tokenFor(customerId, staff)}` } };
}

export function monthRange(periodStart) {
  // The mandatory bounded date range around the statement's own month.
  const from = periodStart;
  const d = new Date(periodStart);
  const to = new Date(d.getFullYear(), d.getMonth() + 1, 1).toISOString().slice(0, 10);
  return { from, to };
}

// POSTs in this suite send a JSON body. k6 defaults an unlabelled string body to text/plain,
// which the API answers 415 for — so every issue request failed before this existed. Separate
// from authHeaders because the GET paths must NOT advertise a request body content type.
export function jsonAuthHeaders(customerId, staff = false) {
  return {
    headers: {
      Authorization: `Bearer ${tokenFor(customerId, staff)}`,
      'Content-Type': 'application/json',
    },
  };
}
