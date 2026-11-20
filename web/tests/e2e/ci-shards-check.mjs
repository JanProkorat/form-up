// Fails when the CI shard groups in ci-shards.json miss a test or run one twice.
// Usage (from web/): node tests/e2e/ci-shards-check.mjs
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const groups = JSON.parse(readFileSync(new URL('./ci-shards.json', import.meta.url), 'utf8'));

// `setup` runs on every machine by design, so it is left out of the comparison.
function listTests(args) {
  const out = execFileSync('npx', ['playwright', 'test', '--list', ...args], {
    encoding: 'utf8',
    env: { ...process.env, CI: 'true' },
  });
  return out
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.startsWith('[') && !line.startsWith('[setup]'));
}

const split = (s) => (s ? s.split(/\s+/).filter(Boolean) : []);

const expected = new Set(listTests([]));
const seen = new Map();
for (const g of groups) {
  for (const args of [g.args, g.after].filter(Boolean)) {
    const tests = listTests(split(args));
    console.log(`${g.group} (${args}): ${tests.length} tests`);
    for (const t of tests) seen.set(t, [...(seen.get(t) ?? []), g.group]);
  }
}

const missing = [...expected].filter((t) => !seen.has(t));
const twice = [...seen].filter(([, gs]) => gs.length > 1);
const unknown = [...seen.keys()].filter((t) => !expected.has(t));

console.log(`Full suite: ${expected.size} tests; covered by groups: ${seen.size}`);
for (const t of missing) console.error(`MISSING from every group: ${t}`);
for (const [t, gs] of twice) console.error(`IN MORE THAN ONE GROUP (${gs.join(', ')}): ${t}`);
for (const t of unknown) console.error(`NOT IN THE FULL SUITE: ${t}`);

if (missing.length || twice.length || unknown.length) {
  console.error('Fix web/tests/e2e/ci-shards.json so every test runs in exactly one group.');
  process.exit(1);
}
console.log('OK: every test runs in exactly one CI group.');
