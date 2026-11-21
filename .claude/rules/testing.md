---
paths:
  - "backend/**"
---

# Testing Rules (backend)

> **Descriptive.** Written in #1104 after the suite was cut from 15 min to
> ~1.5 min locally on the same 2528 tests, then trimmed to 2381. Applies to
> `backend/FormUp.Tests`. Every rule below exists because breaking it
> cost real minutes or hid a real bug.

## Time budget

CI fails the backend job when the three test chunks together take longer than
`TEST_TIME_BUDGET_SECONDS` (`.github/workflows/backend.yml`, step "Test suite
size and time budget"). The step also writes the test count and time to the
job summary. Count is reported, never limited — a useful test is always
welcome; a slow one is not.

Raising the budget needs a sentence in the PR saying what got slower and why
it can't be made cheaper. "The suite grew" is not a reason.

### Chunk layout

`build-and-test` runs the suite in three steps, one after another in the same
job. The budget covers all three together, so moving a class between chunks
saves nothing; only cheaper tests do.

| Chunk | Namespaces | Tests | CI time |
|---|---|---|---|
| 1 Endpoints | `Endpoints*` | 4360 | 115 s |
| 2 Services, Validators, Infrastructure | `Services*`, `Validators*`, `Infrastructure*` | 1952 | 24 s |
| 3 Remaining | everything else (complement of 1 and 2) | 725 | 35 s |

Measured on PR #2327 (#1805, run 44936814443): 174 s against the 300 s
budget. Before #1805, collections ran one at a time and the same 7037 tests
took 224 s, 243 s and 262 s on `develop` (runs 44831547640, 44917018413,
44800842430). Runner variance has been ±30 s or more, almost all of it in
chunk 1. Since #1805 collections run in parallel (#parallel-collections);
the longest single collection now sets the floor of chunk 1.

Where the time goes (local full run, #1563): chunk 1 has no slow class; its
1489 shared-database tests (`[Collection(TestCollection.Name)]`) take a
median of ~43 ms each. The big wins so far were whole-database work repeated
per test: `QaSeedRunnerTests` re-seeding (#1741) and `AutomationSchedulerTests`
sweeping every earlier test's rules (#1768). Look for that pattern first.

## Parallel collections

Since #1805, xUnit runs up to four collections at once
(`TestAssemblyConfig.cs` attribute, mirrored in `xunit.runner.json`). Tests
inside one collection still run one after another.

| Collection | Runs | Why |
|---|---|---|
| `TestCollection.Name`, `Shard2`, `Shard3`, `Shard4` (`Infrastructure/TestCollection.cs`) | in parallel | each shard has its own `FitnessApiFactory`, so its own host, Postgres database and Mongo database |
| the other named collections | in parallel | each fixture creates its own databases inside the shared containers |
| classes with no `[Collection]` (validators, `Factory.Create` unit tests) | in parallel, one collection per class | no database; `Factory.Create` keeps each test's `HttpContext` in an `AsyncLocal` and disposes nothing (FastEndpoints 8.0.1) |
| `SeedTests`, `ResetTests` | alone (`DisableParallelization = true`) | they set process-wide environment variables (`QA_SEED_*`) that `QaSeedRunner` reads |
| `ConsoleCapture` (`Infrastructure/ConsoleCaptureCollection.cs`) | alone (`DisableParallelization = true`) | `ConsoleOutputCapture` swaps the process-wide `Console.Out` |

Rules that keep this safe:

- **A new integration class uses `[Collection(TestCollection.Name)]`.** The
  shards were balanced once by test count; don't move classes between them
  without a reason.
- **Classes that share a static host derived from a factory stay in one
  shard.** `MealPhotoTestHost` caches the host built from the first factory
  that asks; a class in another shard would call a host bound to a different
  database (the four users all sit in `TestCollection.Name`).
- **A test that changes process-wide state** — environment variables,
  `Console.Out`, static fields — gets its own
  `[CollectionDefinition(..., DisableParallelization = true)]`.
  `CultureInfo.CurrentCulture` is per async flow and does not count.
- **A new `WebApplicationFactory<Program>` skips `base.DisposeAsync()`**, like
  `FitnessApiFactory` (#296). Disposing it can dispose the provider behind
  FastEndpoints' global `ServiceResolver` while other collections still use it.
- **Every host boot goes through `Infrastructure/FastEndpointsBootGate.cs`.**
  A factory calls `FastEndpointsBootGate.Start(this)` first in
  `InitializeAsync`; a `WithWebHostBuilder` host is wrapped in
  `FastEndpointsBootGate.Booted(...)`. FastEndpoints keeps its serializer
  options in a process-wide static and replaces them on every boot, so a
  request served by another collection mid-boot locks them and the boot throws
  "JsonSerializerOptions instance is read-only" (CI run 44938750732). The gate
  boots one host at a time and retries that exact failure up to three times.
  Nothing enforces this for a new host — reviewers check it.

## What each layer is for

| Layer | Tool | Use for |
|---|---|---|
| Validator | `new XValidator().TestValidate(...)` | every input rule and boundary |
| Endpoint unit | `Factory.Create<TEndpoint>(...)` + mocks (`Builders/MockDbBuilder.cs`) | handler logic with dependencies stubbed |
| Integration | `[Collection(TestCollection.Name)]` + `FitnessApiFactory` | real DB, real pipeline, auth, ownership |
| Architecture | `Architecture/EndpointAuthorizationTests.cs` | rules that hold for every endpoint at once |

Test a behaviour at the cheapest layer that can prove it, and once.

## Authorization

- **Every endpoint declares `Roles(...)` or `AllowAnonymous()`** — enforced by
  `Architecture/EndpointAuthorizationTests.cs`. Its allow-list names the
  endpoints that deliberately require only a login; adding to it is a reviewed
  exception.
- **Don't add a per-endpoint "no auth → 401" or "wrong role → 403" test.** The
  architecture test covers every endpoint's declaration, including that
  allow-listed routes still require a login. The real pipeline is proven once
  by canaries: `DismissRequestEndpointTests.Dismiss_Unauthenticated_Returns401`
  and `GetTrainerCheckInsEndpointTests.GetList_Unauthenticated_Returns401` for 401,
  `SessionTemplateRoleGateIntegrationTests` and
  `SubscriptionPlanRoleGateIntegrationTests` for wrong-role 403.
- Allow-listed endpoints keep their unit `HandleAsync_NoClaims_Returns401`
  tests — those test the endpoint's own missing-claim guard.
- **Do add a 403/404 test for every ownership, link or capability check** —
  that is app logic, not framework (`rules/api-design.md#authorization`).

## Validation

Rule-by-rule coverage lives in the validator's own tests. The endpoint gets
**one** 400 test proving validation is wired, not one per rule.

## Test data: small builders, not big setup

- Build actors with `Builders/TestActors.cs` (`TestActors.Trainer(factory).CreateAsync()`,
  `.Client(...)`, `TestActors.Link(factory, trainer, client)`) and entities with
  `Builders/EntityBuilders.cs`. They write straight to the database and mint a
  token (`Infrastructure/TestTokenFactory.cs`, kept honest by
  `TestTokenFactoryContractTests`).
- Real `/auth/register` and `/auth/login` belong in the Auth tests only.
- Create only what the test asserts on. No shared seed, no private per-file
  `Setup*Async` that builds a world.
- Don't copy a helper into another file — extend a builder.

## Containers and isolation

- Never start a container or a `WebApplicationFactory` per `[Fact]` — an
  instance field on the test class does exactly that, because xUnit builds a
  new class instance for each test. Use `ICollectionFixture<T>`.
- There is one Postgres and one Mongo container per run
  (`Infrastructure/SharedTestContainers.cs`, an assembly fixture). A fixture
  gets its own database inside them (`CreatePostgresDatabaseAsync`,
  `CreateMongoDatabaseName`). Collections run in parallel, so this is what
  keeps them apart (#parallel-collections).
- Isolate with unique data (fresh GUIDs, unique emails and names). A test that
  asserts an exact count or "contains a single" over a shared collection must
  clear that collection first (or `Infrastructure/DatabaseResetFixtureExtensions.cs`
  for a whole database) — otherwise it passes or fails by test order, which
  differs between machines (#1104's first CI run failed this way after three
  green local runs).
- **A list or search assertion must filter to the test's own data.** Hundreds
  of rows from other tests share the database; "my item is on page 1" passes
  or fails depending on test order (found in #1104,
  `ProfessionalAvatarIntegrationTests.SearchProfessionals_TrainerWithAvatar_ResponseContainsCorrectUrl`).

## Theories

One `[InlineData]` row per distinct branch or boundary. Five rows that hit the
same `Contains` check are one test written five times.

## Assertions

Assert the outcome. A test whose only assertion is `NotBeNull()` on the
response proves the call didn't throw — say that with `NotThrowAsync()`, or
check what actually changed.

## Waiting

No `Thread.Sleep` or fixed `Task.Delay` to wait for async work (background
services, SignalR, schedulers). Poll the observable outcome with a deadline
and fail with the last value seen.

## Cancellation token

Pass `TestContext.Current.CancellationToken` (xUnit v3) to every async call
in a test — the shipped norm, in 285 test files. It lets the runner cancel a
hung test instead of stalling the whole run.

## Running tests

- Scoped runs: `dotnet test backend/FormUp.Tests/FormUp.Tests.csproj -- --filter-class "<FQN>"`.
  Plain `--filter` is silently ignored and runs everything.
- Timing a full run: block sleep (`caffeinate -i`) and make sure no other test
  suite is running — both have produced numbers that were off by 10×.
- Per-test timings: add `--report-xunit --report-xunit-filename timing.xml`
  after the `--`.
