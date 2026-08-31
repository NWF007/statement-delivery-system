// Write-path RPS: token mint + SHA-256 hash insert + transactional audit append.
// Hypothesis under test (SCALE.md #1): the 16 audit chain heads become the serialisation
// ceiling before anything else does.
import http from 'k6/http';
import { check } from 'k6';
import { API, rampOptions, pick, authHeaders } from './lib.js';

export const options = rampOptions;

export default function () {
  const s = pick();
  const res = http.post(
    `${API}/v1/statements/${s.statement_id}/download-links?period=${s.period_start}`,
    JSON.stringify({}),
    Object.assign({ tags: { name: 'issue' } }, authHeaders(s.customer_id)));

  check(res, { 'link issued (201)': (r) => r.status === 201 });
}
