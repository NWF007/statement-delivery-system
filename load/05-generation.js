// Batch generation throughput: request a run for last month, poll its counters, report
// items/sec sustained. Not a VU ramp - the generation fleet paces itself off the claim queue;
// this script only OBSERVES it and prints the extrapolation input for SCALE.md B7:
//   workers_needed = 30,000,000 / (items_per_sec_per_worker x 6h x 3600)
import http from 'k6/http';
import { check, sleep } from 'k6';
import { API } from './lib.js';

export const options = { vus: 1, iterations: 1, duration: '2h' };

export default function () {
  const staffToken = http.post(
    `${API}/v1/dev/tokens?customerId=00000000-0000-0000-0000-000000000001&staff=true`)
    .json('accessToken');
  const auth = { headers: { Authorization: `Bearer ${staffToken}` } };

  const now = new Date();
  const start = new Date(now.getFullYear(), now.getMonth() - 1, 1).toISOString().slice(0, 10);
  const end = new Date(now.getFullYear(), now.getMonth(), 0).toISOString().slice(0, 10);

  const created = http.post(`${API}/v1/statement-runs`,
    JSON.stringify({ periodStart: start, periodEnd: end }),
    Object.assign({ headers: { 'Content-Type': 'application/json', ...auth.headers } }));
  check(created, { 'run accepted': (r) => r.status === 201 || r.status === 200 });
  const runId = created.json('runId') || created.json('id');

  let last = { done: 0, at: Date.now() };
  for (;;) {
    sleep(15);
    const run = http.get(`${API}/v1/statement-runs/${runId}`, auth).json();
    const done = run.completedItems ?? run.completed_items ?? 0;
    const total = run.totalItems ?? run.total_items ?? 0;
    const rate = (done - last.done) / ((Date.now() - last.at) / 1000);
    console.log(`items ${done}/${total}  window rate ${rate.toFixed(1)}/s  status ${run.status}`);
    last = { done, at: Date.now() };
    if (run.status === 'COMPLETED' || run.status === 'FAILED' || done >= total && total > 0) {
      break;
    }
  }
}
