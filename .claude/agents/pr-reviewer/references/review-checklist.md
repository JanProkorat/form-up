# pr-reviewer hard-rule gate checklist

Walk this list **top-to-bottom on every pass**. Don't skip even if the
diff looks small. Each violation becomes a finding entry with the
strict 4-line shape:

```
[SEVERITY] file:line — <rule citation>
Found:
    <offending code excerpt>
Fix:
    <suggested replacement>
```

Severity ladder:
- **BLOCKING** — merge is impossible until fixed.
- **MAJOR** — must fix before merge but doesn't block reviewing other findings.
- **MINOR** — author should address but reviewer can sign off conditionally.

---

## 1. Generated files write-locked

**Citation:** [`rules/code-style.md#generated-files-are-write-locked-if-the-repo-has-one`](../../../rules/code-style.md#generated-files-are-write-locked-if-the-repo-has-one)

Search the diff for:

```bash
git diff --name-only origin/<base>...HEAD | grep -E '(web|mobile)/src/api/generated\.ts'
```

Any hit → **BLOCKING**. Author must regenerate via `regen-api`.

---

## 2. Hardcoded colors / spacing / fonts

**Citation:** [`rules/code-style.md#design-tokens-over-hardcoded-values`](../../../rules/code-style.md#design-tokens-over-hardcoded-values)

In `/web` diff:
```bash
git diff origin/<base>...HEAD -- web/src | grep -E '(#[0-9a-fA-F]{3,8}|color: |background:|fontSize:)'
```

In `/mobile` diff:
```bash
git diff origin/<base>...HEAD -- mobile/src | grep -E '(#[0-9a-fA-F]{3,8}|color:.+["\x27])'
```

Each match that's not a token reference → **BLOCKING**. Brand accent
`#c9a84c` is the most common offender — never inline it.

---

## 3. TypeScript `any` / `as any` / `@ts-ignore`

**Citation:** [`rules/code-style.md#no-any-in-typescript`](../../../rules/code-style.md#no-any-in-typescript)

```bash
git diff origin/<base>...HEAD -- '*.ts' '*.tsx' | grep -E '(\\bany\\b|as any|@ts-ignore|@ts-expect-error)'
```

Any new occurrence without a comment explaining unavoidable interop +
follow-up issue → **BLOCKING**.

---

## 4. Hardcoded API URLs

**Citation:** [`rules/code-style.md#no-hardcoded-api-base-urls`](../../../rules/code-style.md#no-hardcoded-api-base-urls)

```bash
git diff origin/<base>...HEAD | grep -E 'https?://(localhost|[0-9.]+|api\.|form-up)'
```

Anything new in TS/JS/JSX/TSX → **BLOCKING**. Web reads `import.meta.env`,
mobile reads `EXPO_PUBLIC_API_BASE_URL`.

---

## 5. i18n keys present in cs/en/de

**Citation:** [`rules/i18n.md#when-new-copy-lands`](../../../rules/i18n.md#when-new-copy-lands) (supported locales listed in `.claude/CLAUDE.md` → "Locales")

For each new key added to one locale file, confirm it exists in the
other two:

```bash
# Web
diff <(jq -r 'paths | join(".")' web/src/i18n/locales/cs.json | sort) \
     <(jq -r 'paths | join(".")' web/src/i18n/locales/en.json | sort)
diff <(jq -r 'paths | join(".")' web/src/i18n/locales/cs.json | sort) \
     <(jq -r 'paths | join(".")' web/src/i18n/locales/de.json | sort)
```

Same for mobile. Missing key → **BLOCKING** (qa-tester would have
caught it; if it didn't, surface here).

---

## 6. Branch name format

**Citation:** [`rules/branch-and-pr.md#format-rules`](../../../rules/branch-and-pr.md#format-rules)

```bash
gh pr view <N> --json headRefName --jq .headRefName
```

Must match `^(feature|fix|refactor|docs|chore)/[0-9]+-[a-z0-9-]+$`.
Mismatch → **BLOCKING**, route back to dev for branch rename.

---

## 7. Base branch correctness

**Citation:** [`rules/branch-and-pr.md#validation-by-pr-reviewer`](../../../rules/branch-and-pr.md#validation-by-pr-reviewer)

```bash
gh pr view <N> --json baseRefName --jq .baseRefName
```

- Sub-issue of an epic → must be `feature/<epic>-<short>`, not `develop`.
- Standalone or epic-level → `develop` (or `main` for rare release rolls).

Wrong base → **BLOCKING**, route back to fix base before review runs.

---

## 8. One-branch-per-PR

**Citation:** [`rules/branch-and-pr.md#one-branch-per-pr-enforcement`](../../../rules/branch-and-pr.md#one-branch-per-pr-enforcement)

```bash
gh pr view <N> --json commits --jq '.commits[].messageHeadline' | \
    grep -vE '^(feat|fix|refactor|docs|chore)\\(.*#<N>|#<N>'
```

Commits not referencing the PR's issue number → **BLOCKING** with
"branch contains unrelated commits".

---

## 9. Vertical-slice anti-patterns (backend)

**Citation:** [`rules/architecture.md#banned-patterns`](../../../rules/architecture.md#banned-patterns)

```bash
git diff origin/<base>...HEAD -- backend | grep -E '(IRepository<|MediatR\\.|IRequest<|Application\\s*Services)'
```

Hits → **BLOCKING**. Path back is to inline the logic into the slice.

---

## 10. Swallowed exceptions (backend)

**Citation:** [`rules/error-handling.md#core-principle`](../../../rules/error-handling.md#core-principle)

```bash
git diff origin/<base>...HEAD -- backend | grep -E 'catch \\(Exception.*\\) \\{[^}]*Ok\\(\\)'
```

Hits → **BLOCKING**. Use ProblemDetails / `Send.NotFoundAsync` / etc.

---

## 11. Merge exclusion list

**Citation:** [`rules/merge-strategy.md#exclusion-list`](../../../rules/merge-strategy.md#exclusion-list)

```bash
gh pr view <N> --json files --jq '.files[].path' | grep -E 'backend/.*Scripts/|backend/.*DataMigrations/'
```

Hits → **BLOCKED** verdict (not a finding — terminate review with
`blocked_reason`). User merges manually. Also check for one-shot data
commands per the rule — start with
`gh pr view <N> --json files --jq '.files[].path' | grep -E 'backend/.*Infrastructure/Cli/'`,
then read any new startup or CLI routine that writes, renames or drops data. EF migrations (`backend/**/Migrations/**`) are
**not** excluded — list them in the verdict so the user sees the schema
change, and continue.

Same for any diff containing `db\\.\\w+\\.update`, `bulkWrite`,
`deleteMany` calls in MongoContext / Services.

Base branch = `main` → **BLOCKED** unconditionally.

---

## 12. Kind-label set

**Citation:** [`rules/merge-strategy.md#strategy-mapping`](../../../rules/merge-strategy.md#strategy-mapping)

```bash
gh pr view <N> --json labels --jq '[.labels[].name] | map(select(. == "Epic" or . == "Task" or . == "Bug" or . == "Chore")) | length'
```

Must be exactly `1`. Zero → **BLOCKED**, route to `github-issues` for
labelling. Two+ conflicting → **BLOCKED**, route to `github-issues`
for cleanup.

---

## 13. Wiki screen inventory current

**Citation:** `.claude/skills/notion-docs/SKILL.md` → Fixed facts

```bash
git diff --name-status -M --diff-filter=ADR origin/<base>...HEAD -- 'web/src/pages/**' 'mobile/app/**' 'mobile/src/app/**'
git diff origin/<base>...HEAD -- web/src/App.tsx | grep -E '^[+-].*(<Route|path=)'
git diff origin/<base>...HEAD -- docs/wiki/screens.json
```

`<base>` is always `develop` (only PRs into `develop` are reviewed); for an
epic PR the diff covers all its tasks. A page or route **added, deleted or renamed** (first two commands
print anything) with no matching `screens.json` change (third command empty)
→ **BLOCKING**. For each changed screen, compare the API calls its diff adds
(`useQuery`/`useMutation` callers, `src/api/*` functions) against its entry's
`endpoints`; a new call that is not listed → **BLOCKING**. CI's "Wiki screen
inventory check" step catches stale entries (a listed file or endpoint that
no longer exists), not missing ones — that is this item's job.

---

## 14. Bar-lowering moves

**Citation:** [`rules/testing.md#time-budget`](../../../rules/testing.md#time-budget), root `CLAUDE.md` → Working Principles

A change can make the gate pass by lowering the gate. Look for four moves:

1. **Threshold moved** — a budget, timeout, retry count or warning limit raised.
2. **Test made easier** — an assertion deleted or loosened, or an expected value changed to match the new output.
3. **Checker silenced** — a skip, suppression or disable added.
4. **Stub left in** — a placeholder body shipped as if done.

Each move is a finding only when neither the PR body nor the issue gives a reason for it.

```bash
git diff origin/<base>...HEAD -- .github 'backend/**/*.csproj' 'web/playwright.config.*' 'web/vitest.config.*' 'web/src/test/setup.ts' | grep -iE '^\+([^+].*)?(BUDGET|timeout|retries|NoWarn|TreatWarningsAsErrors|continue-on-error)'
git diff origin/<base>...HEAD -- '*Tests*' '*.spec.ts' '*.test.ts' '*.test.tsx' | grep -E '^-([^-].*)?(Assert\.|Should|expect\()'
git diff origin/<base>...HEAD | grep -E '^\+([^+].*)?(#pragma warning disable|SuppressMessage|Skip ?=|\.skip\(|eslint-disable|--no-verify)'
git diff origin/<base>...HEAD | grep -E '^\+([^+].*)?(NotImplementedException|TODO|FIXME)'
```

Read each hit; a hit is a finding only if it lowers the bar. Legitimate
changes (behaviour changed on purpose, test moved) need a sentence in the
PR body. Threshold raised with no reason → **BLOCKING**, per the
time-budget rule. Any other move with no reason → **MAJOR**.

A test moved from E2E to Vitest shows up in the second command as deleted
`expect(` lines. It is not a finding when the PR adds a test of the same name
under `web/src/` that asserts the same outcome (item 15 checks that).

---

## 15. Web test layer and double coverage

**Citation:** [`rules/web-testing.md#which-layer-proves-what`](../../../rules/web-testing.md#which-layer-proves-what), [`#no-double-coverage`](../../../rules/web-testing.md#no-double-coverage)

```bash
git diff --name-status origin/<base>...HEAD -- 'web/tests/e2e/**/*.spec.ts' 'web/src/**/*.test.ts' 'web/src/**/*.test.tsx'
git diff origin/<base>...HEAD -- 'web/tests/e2e/**/*.spec.ts' | grep -E '^\+\s*test\('
git diff origin/<base>...HEAD -- 'web/src/**/*.test.ts' 'web/src/**/*.test.tsx' | grep -E '^\+\s*it(\.each\(.*\))?\('
```

For each **new Playwright test** (second command), check it against the E2E
list in the rule. A test whose point is a UI state, validation, copy, a
request body or URL state, with nothing a real browser or the real backend
adds → **MAJOR**, fix: write it as a Vitest component test.

For each **new Vitest test** (third command) that repeats a Playwright test
still in the tree (same title or the same assertions) → **MAJOR**, fix:
delete the E2E copy in this PR. A moved test whose assertion is narrower
than the E2E original, with no sentence in the PR body → **MAJOR**.

Two tests in the diff, or a new one and an existing one, proving the same
behaviour at the same layer → **MINOR**, fix: merge them or delete one.

---

## Done when

- All 15 items walked.
- Findings emitted in the strict 4-line shape.
- `passes_complete` recorded as `self-only` after first pass; updated
  to `both` after fresh-eyes sub-reviewer agrees.
- Verdict set: READY-FOR-MERGE only when both passes are clean AND
  no BLOCKING findings AND no BLOCKED triggers (items 11, 12) fired.
