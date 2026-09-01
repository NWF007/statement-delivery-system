// The composite that resembles production: 70% browse, 25% issue, 5% full redeem.
import http from 'k6/http';
import { check } from 'k6';
import { API, GATEWAY, rampOptions, pick, authHeaders, jsonAuthHeaders, monthRange } from './lib.js';

export const options = rampOptions;

export default function () {
  const s = pick();
  const roll = Math.random();

  if (roll < 0.70) {
    const { from, to } = monthRange(s.period_start);
    const res = http.get(
      `${API}/v1/customers/${s.customer_id}/statements?from=${from}&to=${to}`,
      Object.assign({ tags: { name: 'browse' } }, authHeaders(s.customer_id)));
    check(res, { browse: (r) => r.status === 200 });
    return;
  }

  const issued = http.post(
    `${API}/v1/statements/${s.statement_id}/download-links?period=${s.period_start}`,
    JSON.stringify({}),
    Object.assign({ tags: { name: 'issue' } }, jsonAuthHeaders(s.customer_id)));

  if (roll < 0.95 || issued.status !== 201) {
    check(issued, { issue: (r) => r.status === 201 });
    return;
  }

  const url = issued.json('url').replace(/^https?:\/\/[^/]+/, GATEWAY);
  const res = http.get(url, { tags: { name: 'redeem' } });
  check(res, { redeem: (r) => r.status === 200 });
}
