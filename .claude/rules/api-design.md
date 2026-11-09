---
paths:
  - "backend/**"
description: FastEndpoints REPR rules for HTTP endpoints in the FormUp backend
---

# API Design Rules

> **Descriptive unless marked otherwise.** Counts below were measured against
> `backend/FormUp.Application/Features` on `develop` at `dc990021`
> (issue #937). **[ASPIRATIONAL]** marks a direction for new code that most
> existing code does not follow — never a description, and never a finding
> against existing code.

All HTTP endpoints use FastEndpoints 8.0.1 (REPR). 233 endpoint files under
`Features/`: 190 inherit `Endpoint<TRequest, TResponse>`, 43 inherit
`EndpointWithoutRequest<TResponse>`. Every one declares its dependencies via
a **primary constructor** (233 of 233).

## Endpoint pattern

Business logic in `HandleAsync`. No service classes for feature logic, no
MediatR. `IApplicationDbContext` / `IMongoContext` and other deps injected
via the primary constructor. If `HandleAsync` grows past ~50 lines, extract
`private` methods on the same class, or reuse an endpoint extension from
`Domain/Extensions/`.

```csharp
/// <summary>
/// Retrieves a single nutrition plan with full detail.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="linkAuthorizationService">Resolves link capabilities.</param>
public class GetPlanEndpoint(IMongoContext mongo, IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<GetPlanRequest, GetPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/nutrition/plans/{PlanId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get a nutrition plan";
            s.Description = "Returns the full nutrition plan with all weeks, days, meals, and foods.";
            s.Response<GetPlanResponse>(StatusCodes.Status200OK, "Nutrition plan detail");
            s.Response(StatusCodes.Status404NotFound, "Plan not found, or not readable by the caller");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetPlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var plan = await this.LoadOwnedNutritionPlanIfAllowedAsync(
            mongo, linkAuthorizationService, req.PlanId, Guid.Parse(userId), ct);

        if (plan is null)
        {
            return;
        }

        await Send.OkAsync(GetPlanResponse.FromDocument(plan), ct);
    }
}
```

### Class accessibility

`public class {Name}Endpoint(...)` is the shipped norm — **219 of 233**.
The 14 `internal sealed` endpoints are the `MealTemplates` and
`SessionTemplates` slices, written later against the pre-#937 rule text.

**[ASPIRATIONAL]** `internal sealed` is preferred for new endpoints — FastEndpoints
discovers them by assembly scan, so `public` buys nothing. Matching the
surrounding slice is equally acceptable, and converting existing endpoints
is not in scope for a feature PR.

## Configure structure

In order:

1. **HTTP verb + route.** `Get`/`Post`/`Put`/`Patch`/`Delete` with a
   leading-slash absolute route (233 of 233 start with `/`).
2. **Authorization.** `Roles(AppRoles.X)` (209 call sites) or
   `AllowAnonymous()` (11) — see #authorization.
3. **`DontCatchExceptions()`** where the slice already uses it — see
   #dont-catch-exceptions.
4. **`Description(...)`** only when overriding the auto-generated name —
   see #endpoint-names.
5. **`Summary(...)`** — mandatory and universal (233 of 233). Set
   `s.Summary`, `s.Description`, and declare every status code the endpoint
   can return with `s.Response(code, desc)` — `s.Response<TResponse>(...)`
   for a typed success, `s.Response<ProblemDetails>(400, ...)` for a 400
   with a body. `s.Responses[code] = "..."` only adds a description to a
   code that is already declared; on its own the code never reaches Swagger
   or the generated clients (#1225). Declaring a success code with
   `s.Response` replaces the default one, so declare the code the handler
   actually sends.
   `Architecture/StatusCodeContractTests` pins the exact set per operation.

Not used anywhere in this backend (0 occurrences each): `Group<T>()`,
`Version()`, `PreProcessor`/`PostProcessor`. Don't introduce one without
discussing it — the flat, per-endpoint `Configure()` is deliberate.

## Endpoint names

`Program.cs` sets `c.Endpoints.ShortNames = true`, so an endpoint's route
name defaults to its **class name** and that name is **global across the
whole assembly**. Two endpoints named the same class name crash the app at
boot — `dotnet build` and scoped unit runs stay green, so only a real boot
(the compose harness, or the Playwright CI job) catches it. Prefix names
by domain: `SearchSessionTemplatesEndpoint`, not `SearchEndpoint`.

`Description(b => b.WithName(nameof(TheEndpoint)))` appears on 14 endpoints
(the `MealTemplates` / `SessionTemplates` slices) and is redundant with
`ShortNames = true` — harmless, not required, don't add it to new code and
don't flag it on existing code. `Description(b => b.ExcludeFromDescription())`
hides an endpoint from Swagger (`Features/Testing/Reset`).

There is no `.WithTag(...)` call anywhere in this backend, and no
`{Feature}FeatureConfiguration` type for one to reference. Swagger grouping
is route-derived. (The rule requiring both was removed in #937 — it had 0
occurrences.)

## Empty request DTOs crash at boot

`Endpoint<TRequest, TResponse>` with a `TRequest` that has **zero
properties** throws `NotSupportedException` during startup. Use
`EndpointWithoutRequest<TResponse>` instead (43 endpoints do). Like the
short-name collision above, `dotnet test` misses this — only a real boot
catches it.

For a bodyless mutation whose only inputs are route parameters, keep
`Endpoint<TRequest>` with a route-bound-properties-only DTO; clients post an
empty JSON body `{}` (established: `ArchiveConversationEndpoint`).

## Handleasync body

1. Input binding (FastEndpoints populates `req`).
2. Resolve the caller — `User.FindFirstValue(AppClaims.UserId)`, guard
   `null` with `Send.UnauthorizedAsync(ct)`.
3. Load data (`AsNoTracking()` for EF reads — `rules/ef-core.md#asnotracking`).
4. Guards for expected errors → `Send.XAsync(ct); return;`
   (`rules/csharp-style.md#guard-clauses`).
5. Business logic.
6. Success via `Send.OkAsync(...)` / `Send.CreatedAtAsync(...)` /
   `Send.NoContentAsync(...)`.

## Extract guards when many

When `HandleAsync` has **3 or more guard clauses** before the business
logic, extract the load-and-validate sequence into a helper named
`Load{Entity}OrRespondAsync` / `Load{Entity}IfAllowedAsync`. The helper
returns `{Entity}?` — `null` signals a response has already been written.

Where the same sequence is shared by several slices it goes into
`Domain/Extensions/` as an endpoint extension method and is called as
`this.Load…Async(...)`. Shipped examples:
`LoadOwnedNutritionPlanIfAllowedAsync`,
`LoadLibraryEntryForReadOrRespondAsync`.

```csharp
public override async Task HandleAsync(GetSessionTemplateRequest req, CancellationToken ct)
{
    var userId = User.FindFirstValue(AppClaims.UserId);

    if (userId is null)
    {
        await Send.UnauthorizedAsync(ct);
        return;
    }

    var template = await this.LoadLibraryEntryForReadOrRespondAsync(
        mongo.SessionTemplates, req.TemplateId, Guid.Parse(userId), SessionTemplateErrors.Denial, ct);

    if (template is null)
    {
        return;
    }

    await Send.OkAsync(SessionTemplateDetailResponse.FromDocument(template), ct);
}
```

Anti-pattern: 4+ inline `if (...) { Send.X; return; }` blocks in
`HandleAsync` followed by the real work. The reader cannot tell what the
endpoint *does* without scrolling past the validation.

## Send pattern

The `Send.*` property API is universal — 715 call sites, 0 legacy
`SendOkAsync(...)`-style calls. Measured breakdown:

| Call | Sites |
|---|---|
| `await Send.NotFoundAsync(ct)` | 222 |
| `await Send.UnauthorizedAsync(ct)` | 217 |
| `await Send.OkAsync(response, ct)` | 191 |
| `await Send.NoContentAsync(ct)` | 49 |
| `await Send.ForbiddenAsync(ct)` | 22 |
| `await Send.ResponseAsync(...)` | 9 |
| `await Send.CreatedAtAsync<TEndpoint>(routeValues, response, cancellation: ct)` | 9 |

Note it is `Send.ForbiddenAsync`, **not** `Send.ForbidAsync`. And
`Send.ErrorsAsync(...)` is not used anywhere (0 sites) — for a coded error
payload use `this.SendProblemAsync(status, ErrorCodes.X, detail, ct)`
instead, per `rules/error-handling.md#send-for-expected-errors`.

Anti-patterns: `return TypedResults.Ok(...)`, throwing for expected errors,
hand-rolling a `ProblemDetails` object.

Always `return;` after `Send.XAsync(ct)` in a guard branch — the response is
already written.

`Send.CreatedAtAsync` requires a registered `LinkGenerator`, which the
lightweight `Factory.Create<TEndpoint>()` test host does **not** provide.
Test 201 paths through the `FitnessApiFactory` (Testcontainers) host
instead.

## Authorization

Gating is role-based: `Roles(AppRoles.X)`, never `Policies(...)`. There is
no `AuthorizationPolicies` class and `Policies(` has 0 call sites — a rule
requiring it was removed in #937.

Measured forms:

| Form | Sites |
|---|---|
| `Roles(AppRoles.Client)` | 71 |
| `Roles(AppRoles.Trainer)` | 41 |
| `Roles(AppRoles.Trainer, AppRoles.Nutritionist)` | 38 |
| `Roles(AppRoles.Nutritionist)` | 37 |
| `Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Client)` | 8 |
| `Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Admin)` | 7 |
| `Roles(AppRoles.TrainerOrNutritionist)` | 3 |
| `Roles(AppRoles.Client, AppRoles.Trainer, AppRoles.Nutritionist)` | 3 |
| `Roles("Client")` — string literal | 1 |

**Always the `AppRoles` constants, never a string literal.** The single
`Roles("Client")` in `Features/Client/SubmitOnboarding/SubmitOnboardingEndpoint.cs:26`
is a known drift, not a permitted form; fix it opportunistically if you are
already editing that file.

**Prefer the varargs form** `Roles(AppRoles.Trainer, AppRoles.Nutritionist)`
(38 sites) over `Roles(AppRoles.TrainerOrNutritionist)` (3 sites). The two
are behaviourally identical — `AppRoles.TrainerOrNutritionist` is just the
constant `"Trainer,Nutritionist"`, and FastEndpoints comma-joins the varargs
into the same `AuthorizeAttribute.Roles` value — but the varargs form reads
as the OR it is and composes with a third role. Don't churn the 3 existing
sites; don't add a fourth.

Role gating is coarse. **A role check is not an ownership check** — an
endpoint addressing a specific client's data must also verify the caller's
live link and per-domain capability (`IClientLinkAuthorizationService`'s
`GetCapabilitiesBy…Async`, which returns `null` when there is no live link).
`IsActive` alone is not sufficient.

No endpoint is anonymous unless intentionally public — then call
`AllowAnonymous()` and say why in `Summary`. The 11 anonymous endpoints are
the `Auth` slice (login, register, refresh, social login, nonce, password
reset, email verification) plus `Features/Testing/Reset`, which is
additionally excluded from Swagger and gated on a test-only environment.

## Routes

- **Absolute, leading slash**: `Get("/nutrition/plans/{PlanId}")`. All 233
  routes start with `/`.
- **Domain prefix first**: `/nutrition/...`, `/training/...`,
  `/client/...`, `/trainer/...`, `/professionals/...`, `/users/...`.
- Lowercase + hyphens for multi-word segments: `/training/session-templates`,
  `/trainer/pending-invites`, `/client/weekly-check-ins`.
- **Route parameters are PascalCase and match the request DTO property name
  exactly** — `{PlanId}` binds `GetPlanRequest.PlanId`. This is the binding
  contract; a case mismatch silently binds nothing.
- **Type constraints are not the convention here** — only 2 of 209 route
  parameters carry one. Validate the value in the validator instead
  (`rules/validation.md`).
- RESTful verbs: `GET` reads (84), `POST` creates (96), `PUT` full updates
  (26), `PATCH` partial (3), `DELETE` removal (24).

## Json serialization

Configured globally in `Program.cs` (`JsonStringEnumConverter`,
ProblemDetails error shape with `IndicateErrorCode = true`). Never configure
it per endpoint.

## Dont catch exceptions

**[ASPIRATIONAL]** — `DontCatchExceptions()` is present on **14 of 233**
endpoints (the `MealTemplates` and `SessionTemplates` slices). It is *not*
mandatory here and its absence is inherited debt, never a finding against a
feature PR that merely followed the surrounding slice.

Add it to new endpoints where you want unhandled exceptions to reach the
global handler in `Middleware/` unwrapped. Do not open a sweep to add it to
the other 219 without an issue of its own.
