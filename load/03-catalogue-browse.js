// Read-path throughput under the MANDATORY bounded date range - the query that must show
// partition pruning in its plan (SCALE.md, EXPLAIN section). Replica-eligible by design.
import http from 'k6/http';
import { check } from 'k6';
import { API, rampOptions, pick, authHeaders, monthRange } from './lib.js';

export const options = rampOptions;

export default function () {
  const s = pick();
  const { from, to } = monthRange(s.period_start);
  const res = http.get(
    `${API}/v1/customers/${s.customer_id}/statements?from=${from}&to=${to}`,
    Object.assign({ tags: { name: 'browse' } }, authHeaders(s.customer_id)));

  check(res, { 'listed (200)': (r) => r.status === 200 });
}
