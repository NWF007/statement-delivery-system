// The full redeem: atomic consume -> decrypt -> stream. Tokens are single-use, so every
// iteration must issue a fresh link - redemption RPS is inherently bounded by issue RPS, and
// that coupling is part of the honest result, not an artefact to hide.
//
// The same script measures the DENIAL path separately (a deliberate replay): the 50 ms
// uniform-timing floor from Prompt 3 puts a hard per-connection ceiling on denial throughput -
// SCALE.md hypothesis #3 - so denials get their own trend metric rather than polluting p99.
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';
import { API, GATEWAY, rampOptions, pick, jsonAuthHeaders } from './lib.js';

export const options = rampOptions;

const redeemMs = new Trend('redeem_duration', true);
const denialMs = new Trend('denial_duration', true);

export default function () {
  const s = pick();
  const issued = http.post(
    `${API}/v1/statements/${s.statement_id}/download-links?period=${s.period_start}`,
    JSON.stringify({}),
    Object.assign({ tags: { name: 'issue' } }, jsonAuthHeaders(s.customer_id)));
  if (issued.status !== 201) {
    return;
  }

  const url = issued.json('url').replace(/^https?:\/\/[^/]+/, GATEWAY);

  const first = http.get(url, { tags: { name: 'redeem' } });
  check(first, { 'redeemed (200)': (r) => r.status === 200 });
  redeemMs.add(first.timings.duration);

  // One deliberate replay in ten: single-use enforcement under load, on the padded path.
  if (Math.random() < 0.1) {
    const replay = http.get(url, { tags: { name: 'replay-denied' } });
    check(replay, { 'replay denied (404)': (r) => r.status === 404 });
    denialMs.add(replay.timings.duration);
  }
}
