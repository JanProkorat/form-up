// Run independent test collections in parallel; tests inside one collection stay serial.
//
// Layout: every [Collection(...)] and every class without the attribute (xUnit's default is one
// collection per class) is its own collection. Collections run concurrently, capped by
// MaxParallelThreads. A collection that shares mutable state with the rest of the process opts out
// with [CollectionDefinition(..., DisableParallelization = true)] — such collections run alone,
// after the parallel ones have finished.
//
// Why parallel is safe for the FastEndpoints static resolver:
//   Factory.Create<TEndpoint>() (FastEndpoints 8.0.1, Src/Library/Testing/Factory.cs) builds the
//   endpoint over a fresh HttpContext whose RequestServices come from a new ServiceCollection. It
//   calls AddTestServices, which sets the process-global ServiceResolver.Instance only if no one
//   has set it yet, and it points that resolver's IHttpContextAccessor at the new context. There is
//   no per-call WebApplicationFactory and nothing is disposed. ServiceResolver
//   (Src/Core/ServiceResolver/ServiceResolver.cs) resolves through
//   ctxAccessor?.HttpContext?.RequestServices ?? provider, and HttpContextAccessor is AsyncLocal
//   backed, so concurrent tests do not see each other's context.
//   The one real hazard is a WebApplicationFactory<Program> whose DisposeAsync disposes the root
//   provider that is currently the global resolver's fallback. Every factory in this assembly
//   (FitnessApiFactory, ResetEndpointFactoryBase, QaSeedRunnerFactory, CatalogSeedingFactory,
//   RateLimitEnabledFactory, RefreshTokenReuse and WorkoutLogCompletion factories) skips
//   base.DisposeAsync() for exactly that reason. A new factory must do the same.
//
// Isolation: each collection fixture creates its own Postgres database and Mongo database inside the
// shared containers (SharedTestContainers), so collections never share rows.
//
// Precedence: the xUnit docs say a runner may override code or configuration
// (https://xunit.net/docs/running-tests-in-parallel, "Parallelism in Runners"), and
// xunit.runner.json is read by the runner. To avoid depending on that, xunit.runner.json states the
// same values as this attribute.
// UNVERIFIED: which of the attribute and xunit.runner.json wins when they differ in xunit.v3 3.2.2 —
// https://xunit.net/docs/config-xunit-runner-json
//
// CI runs the test binary directly (./FormUp.Tests/bin/Release/net10.0/FormUp.Tests), which honours
// this attribute; dotnet test also reads xunit.runner.json.
//
// See also: FitnessApiFactory.cs (skips base.DisposeAsync, #296) and TestHostedServiceExtensions.cs
// (background schedulers never start in a test host, #726).
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
