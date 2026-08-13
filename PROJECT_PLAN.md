# Supplier Integration API — Project Plan

## 1. Project Purpose

Build a portfolio-ready **ASP.NET Core Supplier Integration & Webhook API** that demonstrates practical backend integration skills beyond CRUD.

The project should showcase:

- ASP.NET Core REST API design
- JWT authentication and Admin authorization
- External REST API consumption
- Typed `HttpClient`
- Provider configuration
- Pagination handling
- Timeout and transient-failure handling
- Rate-limit (`429`) handling
- Cancellation-token propagation
- Product/inventory synchronization
- Auditable synchronization history
- Background synchronization
- Secure HMAC webhook verification
- Webhook idempotency
- Concurrent duplicate-event protection
- SQL Server persistence with Entity Framework Core
- Health checks
- Centralized Problem Details
- Safe logging
- OpenAPI / Scalar documentation
- Automated integration tests
- Professional GitHub portfolio presentation

The application domain will be a **Supplier Integration Service** that synchronizes supplier product/inventory data into a local SQL Server database and receives supplier webhook events.

The goal is not to build an ERP, marketplace, or message-bus platform. The project should remain focused, understandable, and directly relevant to freelance backend integration work.

---

# 2. Portfolio Positioning

Suggested portfolio title:

> ASP.NET Core Supplier Integration & Webhook API

Suggested description:

> Backend integration service built with ASP.NET Core for synchronizing external supplier data, processing secure idempotent webhooks, handling resilient HTTP communication, and maintaining auditable synchronization history.

Primary differentiator:

```text
Third-party REST API integration
+
Resilient HTTP communication
+
Secure webhook verification
+
Idempotent event processing
+
Auditable synchronization
```

This project should complement the existing portfolio:

```text
Task Management API
→ secure REST/JWT fundamentals

E-Commerce Order API
→ transactions, concurrency, inventory, business workflows

Supplier Integration API
→ third-party integrations, resilience, webhooks, idempotency
```

---

# 3. Technology Stack

Use:

- .NET 10
- ASP.NET Core 10 Web API
- C#
- Entity Framework Core 10
- SQL Server
- JWT Bearer Authentication
- Typed `HttpClient`
- `Microsoft.Extensions.Http.Resilience` or framework-supported HTTP resilience
- FluentValidation
- ASP.NET Core Health Checks
- ASP.NET Core built-in OpenAPI
- Scalar
- xUnit
- `WebApplicationFactory`
- SQLite relational integration tests where provider-realistic behavior is required

Prefer framework-native functionality where practical.

Do not introduce libraries or architectural patterns unless they provide clear value.

---

# 4. Repository

Repository name:

```text
SupplierIntegrationApi
```

Target structure:

```text
SupplierIntegrationApi/
├── src/
│   └── SupplierIntegrationApi/
│       ├── Configuration/
│       ├── Controllers/
│       ├── Data/
│       │   ├── Configurations/
│       │   └── Migrations/
│       ├── DTOs/
│       ├── Entities/
│       ├── Enums/
│       ├── Errors/
│       ├── Integrations/
│       │   └── Supplier/
│       ├── Interfaces/
│       ├── Services/
│       ├── Validation/
│       └── Program.cs
├── tests/
│   └── SupplierIntegrationApi.Tests/
├── screenshots/
├── README.md
├── PROJECT_PLAN.md
├── .gitignore
├── global.json
└── SupplierIntegrationApi.slnx
```

Keep the solution simple.

Do not split the application into multiple class-library projects without a concrete need.

---

# 5. Scope Boundaries

Version 1 intentionally excludes:

```text
Frontend UI
Payments
Shipping
Orders
Customer carts
Supplier purchasing
EDI
Multiple suppliers
Kafka
RabbitMQ
MassTransit
Redis
CQRS
MediatR
Event sourcing
Distributed transactions
Kubernetes
Docker orchestration
Elasticsearch
GraphQL
Email/SMS
Multi-tenancy
Complex ERP workflows
```

Do not add these unless explicitly requested later.

The project models one external supplier integration.

---

# 6. Roles

Use one application-management role:

```text
Admin
```

No public self-registration is required for production scope.

For portfolio/demo purposes, Admin credentials may be created through:

- safe local-development seeding, or
- development database administration

Do not create an insecure public Admin-registration endpoint.

---

# 7. Authentication

Endpoint:

```http
POST /api/auth/login
```

Requirements:

- authenticate an Admin account
- password stored only as a secure hash
- issue JWT access token
- validate issuer, audience, signing key, signature, and lifetime
- use UTC timestamps
- keep JWT key outside source control
- no refresh-token subsystem is required for this project

Reason:

Refresh-token rotation is already demonstrated in other portfolio work. This project should focus on integration engineering.

---

# 8. Core Domain

Use a deliberately small model:

```text
User
Product
SyncRun
WebhookEvent
```

Optional only if genuinely useful:

```text
SupplierProductMapping
```

Prefer avoiding a mapping table if `Product.ExternalId` is sufficient.

---

# 9. User Entity

Suggested:

```text
User
- Id
- Email
- NormalizedEmail
- PasswordHash
- Role
- IsActive
- CreatedAtUtc
```

Rules:

- normalized email unique
- password never stored plaintext
- Admin-only role
- inactive users cannot authenticate

---

# 10. Product Entity

Suggested:

```text
Product
- Id
- ExternalId
- Sku
- Name
- Price
- StockQuantity
- IsActive
- LastSyncedAtUtc
- CreatedAtUtc
- UpdatedAtUtc
```

Rules:

- `ExternalId` unique
- `Sku` unique if supplier contract guarantees stable uniqueness
- Price uses `decimal(18,2)`
- StockQuantity cannot be negative
- current local values represent the latest successfully synchronized supplier state
- synchronization updates existing rows by stable supplier identity
- synchronization creates rows that do not yet exist locally

Do not expose supplier credentials through Product responses.

---

# 11. SyncRun Entity

Suggested:

```text
SyncRun
- Id
- TriggerType
- Status
- StartedAtUtc
- CompletedAtUtc
- ItemsRead
- ItemsCreated
- ItemsUpdated
- ItemsUnchanged
- FailureCode
- FailureMessage
```

Recommended enums:

```text
SyncTriggerType
- Manual
- Scheduled
```

```text
SyncRunStatus
- Running
- Succeeded
- Failed
- Cancelled
```

Rules:

- every synchronization attempt creates one auditable SyncRun
- no raw API keys or Authorization headers stored
- failure messages must be safe and operational, not raw provider exception dumps
- completed sync runs are immutable except for final status/result fields

---

# 12. WebhookEvent Entity

Suggested:

```text
WebhookEvent
- Id
- ExternalEventId
- EventType
- Status
- ReceivedAtUtc
- ProcessedAtUtc
- ProductExternalId
- FailureCode
```

Recommended status enum:

```text
Received
Processed
Ignored
Failed
```

Critical constraint:

```text
WebhookEvent.ExternalEventId UNIQUE
```

This is the database-level foundation for webhook idempotency.

Do not persist the supplier signature itself unless there is a documented reason.

Do not persist secrets.

---

# 13. Supplier Provider Configuration

Suggested configuration:

```json
{
  "Supplier": {
    "BaseUrl": "https://supplier.example/",
    "ApiKey": "",
    "PageSize": 100,
    "RequestTimeoutSeconds": 10,
    "WebhookSecret": "",
    "ScheduledSyncIntervalMinutes": 30
  }
}
```

Requirements:

- `ApiKey` empty in committed configuration
- `WebhookSecret` empty in committed configuration
- secrets supplied through User Secrets/environment variables
- validate required options on startup
- validate sensible timeout/page-size values
- never log secrets

---

# 14. Supplier REST Contract

Model a realistic external API conceptually:

```http
GET /api/products?page=1&pageSize=100
```

Example response:

```json
{
  "items": [
    {
      "id": "SUP-1001",
      "sku": "KB-001",
      "name": "Mechanical Keyboard",
      "price": 99.95,
      "stockQuantity": 24,
      "isActive": true
    }
  ],
  "page": 1,
  "pageSize": 100,
  "totalPages": 3
}
```

Do not bind the architecture to a fragile public demo API.

Production code should depend on a typed Supplier client abstraction.

Tests should use controlled HTTP responses.

---

# 15. Typed Supplier Client

Recommended:

```text
ISupplierClient
SupplierClient
```

The client is responsible for:

- constructing supplier HTTP requests
- authentication headers
- pagination requests
- response status handling
- JSON deserialization
- provider contract validation
- propagating CancellationToken

The client should not perform EF Core persistence.

Keep external transport logic separate from synchronization business logic.

---

# 16. HTTP Client Authentication

Use provider configuration to send a supplier credential, for example:

```http
Authorization: Bearer <supplier-api-key>
```

or:

```http
X-Api-Key: <supplier-api-key>
```

Choose one documented provider contract for the portfolio implementation.

Never:

- log the credential
- return it from an endpoint
- store it in the database
- commit it to Git

---

# 17. Supplier Pagination

Synchronization must support multiple supplier pages.

Conceptual flow:

```text
Request page 1
    ↓
validate response
    ↓
process items
    ↓
has another page?
    ↓ yes
request next page
    ↓
repeat
```

Requirements:

- stop when provider reports final page
- protect against invalid pagination metadata
- avoid infinite loops
- propagate cancellation
- count processed items accurately

Tests must prove multi-page synchronization.

---

# 18. Synchronization Workflow

Admin endpoint:

```http
POST /api/admin/integrations/supplier/sync
```

Flow:

```text
Authenticate Admin
      ↓
Create SyncRun = Running
      ↓
Fetch supplier page(s)
      ↓
Validate external data
      ↓
Map external items
      ↓
Upsert Products
      ↓
Record counts
      ↓
Mark SyncRun Succeeded
```

Failure:

```text
provider/validation/persistence failure
      ↓
mark SyncRun Failed safely
      ↓
return appropriate Problem Details
```

Cancellation:

```text
request cancelled
      ↓
stop work
      ↓
mark SyncRun Cancelled where practical
```

---

# 19. Synchronization Concurrency

Do not allow uncontrolled overlapping full synchronization runs.

Preferred simple rule:

```text
Only one supplier sync may be Running at a time.
```

A second manual sync while another active sync exists should return:

```text
409 Conflict
```

The rule should be protected at a level that is meaningful under concurrent requests.

Avoid relying only on an in-memory boolean if the project is designed to demonstrate database-backed correctness.

A database-backed guard/constraint or transaction-safe claim is preferred.

Keep implementation practical.

---

# 20. Product Upsert Rules

Use stable:

```text
ExternalId
```

to identify supplier records.

For each supplier item:

If no local Product exists:

```text
Create
```

If Product exists and values differ:

```text
Update
```

If values are identical:

```text
No-op / count unchanged
```

Always update:

```text
LastSyncedAtUtc
```

according to the chosen rule.

Use UTC timestamps.

Avoid destructive deletes.

If a supplier product is inactive:

```text
IsActive = false
```

Do not delete local historical integration records.

---

# 21. Transaction Boundaries

Do not wrap the entire remote HTTP download in one SQL Server transaction.

Preferred pattern:

1. fetch/validate external page data outside a DB transaction
2. use short database transactions for local persistence where needed
3. avoid holding DB locks while waiting on external HTTP calls

If the synchronization uses one transaction for a validated complete dataset, only do so after external fetching is complete and memory size is reasonable.

Document the chosen tradeoff.

---

# 22. Resilient HTTP Communication

Use framework-supported HTTP resilience.

Handle at minimum:

- connection failures
- request timeout
- transient 5xx responses
- `429 Too Many Requests`

Requirements:

- finite retry count
- bounded delays
- cancellation respected
- no infinite retries
- no retry storm
- provider errors mapped to safe application results

Do not implement manual `while(true)` retry loops.

---

# 23. Retry Semantics

Safe automatic retries are appropriate for supplier `GET` requests because they are read-only.

Do not generalize automatic retry to arbitrary side-effecting HTTP operations.

For `429`:

- honor `Retry-After` when supported by the selected framework mechanism
- otherwise use bounded backoff
- still cap the total retry behavior

Tests should verify the observable resilience behavior without sleeping for long real-time delays.

---

# 24. Timeout Handling

Supplier calls require a bounded timeout.

A provider timeout should not produce an unhandled exception or leak transport details.

Return a safe application error such as:

```text
502 Bad Gateway
```

or:

```text
503 Service Unavailable
```

Choose and document one consistent mapping.

The associated SyncRun must be finalized safely.

---

# 25. Provider Error Mapping

Recommended external-integration errors:

```text
Supplier unavailable          → 503
Supplier timeout              → 504 or documented 503
Supplier rate limit exhausted → 503
Invalid supplier payload      → 502
Concurrent sync already active → 409
```

Use Problem Details.

Do not return raw provider response bodies if they may contain sensitive/internal information.

---

# 26. Manual Sync Response

Suggested successful response:

```json
{
  "syncRunId": 42,
  "status": "Succeeded",
  "itemsRead": 250,
  "itemsCreated": 10,
  "itemsUpdated": 35,
  "itemsUnchanged": 205,
  "startedAtUtc": "2026-08-13T12:00:00Z",
  "completedAtUtc": "2026-08-13T12:00:02Z"
}
```

Use actual enum JSON behavior consistently.

---

# 27. Sync History Endpoints

Admin-only:

```http
GET /api/admin/integrations/supplier/runs
GET /api/admin/integrations/supplier/runs/{id}
```

List supports:

```text
pageNumber
pageSize
status
triggerType
fromDate
toDate
```

Suggested sorting:

```text
StartedAtUtc DESC
Id DESC
```

Maximum page size:

```text
100
```

Do not return credentials, provider headers, or unsafe exception detail.

---

# 28. Product Read Endpoints

Public or Admin-readable endpoints:

```http
GET /api/products
GET /api/products/{id}
```

Recommended query parameters:

```text
pageNumber
pageSize
search
isActive
minStock
maxStock
```

Keep product querying small.

Do not rebuild the E-Commerce catalog project.

Purpose:

Allow reviewers to observe synchronized local state.

---

# 29. Webhook Endpoint

Endpoint:

```http
POST /api/webhooks/supplier
```

No JWT authentication.

Security comes from the supplier webhook signature.

Expected headers:

```http
X-Supplier-Event-Id: evt_123
X-Supplier-Signature: <signature>
```

Example payload:

```json
{
  "eventType": "inventory.updated",
  "productId": "SUP-1001",
  "stockQuantity": 18
}
```

---

# 30. Webhook Signature Verification

Use:

```text
HMAC-SHA256
```

over the exact raw request body using the configured webhook secret.

Requirements:

- read raw request body correctly
- compute expected HMAC
- decode/normalize supplied signature using a documented format
- compare using constant-time comparison
- reject missing signature
- reject malformed signature
- reject invalid signature
- do not log signature or secret

Recommended response:

```text
401 Unauthorized
```

for invalid signature.

Do not deserialize and then reserialize before computing the signature.

Signature must use the exact received bytes.

---

# 31. Webhook Idempotency

Critical portfolio feature.

Use:

```text
X-Supplier-Event-Id
```

as stable external event identity.

The database must enforce uniqueness.

First delivery:

```text
evt_123
→ process
→ persist WebhookEvent
→ update Product
```

Repeated delivery:

```text
evt_123
→ detect duplicate
→ do not apply side effect again
```

A duplicate valid webhook should return a stable success response such as:

```text
200 OK
```

or:

```text
204 No Content
```

Choose and document one behavior.

Do not turn normal duplicate delivery into a server error.

---

# 32. Concurrent Duplicate Webhooks

Test this explicitly.

Example:

```text
20 concurrent deliveries of evt_123
```

Expected:

```text
one effective product update
one stored unique WebhookEvent
remaining deliveries safely treated as duplicates
```

Do not depend only on:

```text
SELECT exists
then INSERT
```

without database uniqueness, because concurrent requests can race.

Database uniqueness is authoritative.

---

# 33. Webhook Event Types

Keep webhook scope intentionally small.

Support:

```text
inventory.updated
price.updated
product.updated
```

Optional simplification:

Support only:

```text
inventory.updated
product.updated
```

if that produces a cleaner project.

Unknown validly signed event types should be safely ignored/recorded according to a documented rule.

Do not implement dozens of event types.

---

# 34. Webhook Update Rules

`inventory.updated`:

```text
locate Product by ExternalId
update StockQuantity
update UpdatedAtUtc
```

`price.updated`:

```text
locate Product by ExternalId
update Price
update UpdatedAtUtc
```

`product.updated` may update:

```text
Name
Price
StockQuantity
IsActive
```

Validate:

- price > 0 where relevant
- stock >= 0
- external product ID required

Do not allow negative stock from webhook payloads.

---

# 35. Unknown Product Webhooks

Choose a clear rule.

Recommended:

Validly signed webhook for unknown Product:

- record WebhookEvent as `Ignored`
- do not create an incomplete Product from a partial inventory-only event
- return success

A future full synchronization can create the Product from authoritative supplier data.

This avoids creating incomplete local rows.

Document this behavior.

---

# 36. Background Synchronization

Implement a small scheduled synchronization mechanism.

Use:

```text
BackgroundService
```

with configurable interval.

Requirements:

- no Hangfire required
- no message broker
- cancellation respected
- reuse the same synchronization service as manual sync
- do not duplicate synchronization business logic
- prevent overlap with manual/other scheduled runs
- safe logging
- failure of one scheduled run must not kill the host

Default schedule can be disabled or use a conservative local interval.

Recommended configuration:

```text
Supplier:ScheduledSyncEnabled
Supplier:ScheduledSyncIntervalMinutes
```

---

# 37. Background Service Scope Handling

`BackgroundService` is singleton-hosted.

When accessing scoped services:

- create an `IServiceScope`
- resolve synchronization service inside the scope
- dispose scope each iteration

Do not inject scoped DbContext directly into singleton BackgroundService.

---

# 38. Health Checks

Expose:

```http
GET /health
```

At minimum verify application liveness.

Optionally include SQL Server readiness.

Do not make every `/health` request call the external supplier unless there is a strong reason; that can create coupling/rate-limit noise.

If supplier health is exposed, make it a separate detailed readiness check or keep it out of v1.

---

# 39. Health Response

Keep default/compact health output unless a custom response adds clear portfolio value.

Do not expose:

- connection strings
- supplier URL credentials
- exception details

---

# 40. OpenAPI / Scalar

Use built-in OpenAPI + Scalar in Development.

Requirements:

- JWT Bearer scheme
- Admin-protected endpoints marked with security requirement
- webhook endpoint documented as signature-authenticated, not JWT-authenticated
- useful endpoint summaries
- request/response contracts
- representative error responses where practical

Expected demo flows:

Manual sync:

```text
Admin login
   ↓
Authorize
   ↓
Run supplier sync
   ↓
View synced products
   ↓
View SyncRun history
```

Webhook:

```text
Signed supplier event
   ↓
Webhook verification
   ↓
Idempotency
   ↓
Local Product update
```

---

# 41. Problem Details

Use centralized ASP.NET Core Problem Details.

Production-safe unexpected `500` responses must not expose:

- exception message
- stack trace
- SQL/provider detail
- connection string
- API keys
- file paths

Include a trace identifier where useful.

Integration-specific known failures should be mapped deliberately instead of becoming generic 500s.

---

# 42. Logging

Use:

```text
ILogger<T>
```

Useful events:

- sync started/completed
- sync counts
- scheduled sync failure
- supplier unavailable
- webhook accepted
- duplicate webhook detected
- invalid webhook signature
- unknown product event ignored

Never log:

- JWT tokens
- supplier API key
- webhook secret
- raw signature
- Authorization headers
- connection strings
- passwords
- full raw webhook body

Use IDs and safe metadata.

---

# 43. Validation

Use FluentValidation.

Validate at minimum:

Login:

- email required/valid
- password required

Sync query/history:

- page number >= 1
- page size 1..100
- valid date range
- valid enums

Supplier DTOs:

- ExternalId required
- SKU required where contract requires
- Name required
- Price > 0
- StockQuantity >= 0

Webhook:

- external event ID required
- event type required
- Product ID required for supported product events
- StockQuantity >= 0
- Price > 0 where supplied

Remember:

Signature verification must occur against raw bytes before trusting/deserializing the body for business processing.

---

# 44. Database Constraints

Important constraints:

```text
User.NormalizedEmail UNIQUE
Product.ExternalId UNIQUE
Product.Sku UNIQUE (if chosen)
WebhookEvent.ExternalEventId UNIQUE
```

Use:

```text
decimal(18,2)
```

for money.

Use deliberate delete behavior.

Avoid cascades that erase audit history.

SyncRun and WebhookEvent are audit/history records and should not disappear because a Product changes.

---

# 45. Architecture Rules

- controllers remain thin
- integration transport belongs in Supplier client
- synchronization business logic belongs in a service
- EF Core DbContext may be used directly by services
- no generic repository
- no Unit of Work wrapper
- no CQRS/MediatR
- no service locator
- no static global HttpClient
- use typed HttpClient
- resilience configuration centralized
- webhook signature verification separated from controller plumbing where useful
- comments explain why
- readable code over architecture ceremony

---

# 46. DTO Rules

Never expose EF Core entities directly.

Suggested DTOs:

```text
LoginRequest
AuthResponse

ProductResponse
ProductQueryParameters

StartSupplierSyncResponse
SyncRunResponse
SyncRunListItemResponse
SyncRunQueryParameters

SupplierProductDto
SupplierProductPageDto

SupplierWebhookPayload
WebhookResponse
```

Use focused models.

Do not expose:

```text
PasswordHash
ApiKey
WebhookSecret
internal provider headers
unsafe failure detail
```

---

# 47. Cancellation Tokens

Propagate request cancellation through:

- controllers
- validation
- EF Core queries
- SaveChangesAsync
- transactions
- supplier HttpClient calls
- sync service
- background service cancellation

Do not swallow `OperationCanceledException` as a generic provider failure.

Distinguish actual timeout from caller cancellation where practical.

---

# 48. Test Strategy

Test important behavior.

Do not chase an arbitrary coverage percentage.

Target:

```text
approximately 35–50 focused tests
```

More is acceptable if each protects meaningful behavior.

Use relational SQLite for database uniqueness/concurrency behavior where practical.

Use controlled fake HTTP transport for supplier API behavior.

Do not depend on internet access for automated tests.

---

# 49. Supplier Client Tests

Cover:

- successful single-page response
- multi-page response
- expected authentication header
- cancellation propagation
- timeout behavior
- transient 5xx handling
- 429 handling
- malformed JSON
- invalid pagination metadata
- unexpected status
- no secret leakage in application responses/logs

Use deterministic delays/no-delay resilience configuration in tests where necessary.

---

# 50. Synchronization Tests

Cover:

- Admin required
- new products created
- existing products updated
- unchanged products counted correctly
- inactive supplier product reflected locally
- price/stock mapping
- multi-page aggregate counts
- SyncRun success history
- SyncRun failure history
- concurrent manual sync conflict
- cancellation handling
- external failures do not leave a Running audit record indefinitely

---

# 51. Webhook Tests

Cover:

- valid signature accepted
- missing signature rejected
- malformed signature rejected
- invalid signature rejected
- valid inventory update
- valid price update
- negative stock rejected
- duplicate event is idempotent
- concurrent duplicate event processed once
- unknown Product safely ignored
- unknown event type safe behavior
- secret/signature not leaked

At least one integration test must calculate the real HMAC over the exact HTTP body bytes.

Do not duplicate production HMAC implementation in a way that makes tests tautological; use standard cryptographic primitives independently in tests.

---

# 52. Authentication Tests

Cover:

- valid Admin login
- invalid credentials
- inactive Admin rejected
- protected sync endpoint returns 401 anonymously
- valid Admin JWT allows access

No refresh-token tests required.

---

# 53. Quality Tests

Cover:

- validation → 400 Problem Details
- conflict → 409
- provider availability mapping
- unexpected exception → safe 500
- OpenAPI protected/anonymous metadata
- health endpoint
- no sensitive values in HTTP responses

---

# 54. External HTTP Test Infrastructure

Use one controlled approach, for example:

- custom `HttpMessageHandler`, or
- a lightweight local test HTTP server if genuinely needed

Prefer the simplest approach that exercises the typed Supplier client realistically.

Required capabilities:

- inspect outgoing URL/query/header
- queue responses
- simulate pagination
- simulate 429/5xx
- simulate timeout/cancellation
- return malformed payload

Do not add a heavy mocking framework just for HTTP.

---

# 55. Security Checklist

Before project completion:

- [ ] passwords hashed
- [ ] JWT key not committed
- [ ] supplier API key not committed
- [ ] webhook secret not committed
- [ ] JWT issuer validated
- [ ] JWT audience validated
- [ ] JWT signature validated
- [ ] JWT lifetime validated
- [ ] Admin authorization enforced
- [ ] typed HttpClient used
- [ ] supplier credential never logged
- [ ] finite HTTP retries
- [ ] timeouts configured
- [ ] cancellation propagated
- [ ] webhook signature uses exact raw body
- [ ] constant-time signature comparison
- [ ] invalid webhook signatures rejected
- [ ] ExternalEventId unique
- [ ] duplicate webhook side effects prevented
- [ ] concurrent duplicates tested
- [ ] unexpected errors use safe Problem Details
- [ ] logs do not contain secrets
- [ ] health endpoints do not expose internals
- [ ] HTTPS redirection enabled

---

# 56. Portfolio Screenshots

Final project should include genuine screenshots such as:

```text
screenshots/scalar-overview.png
screenshots/scalar-manual-sync.png
screenshots/scalar-sync-history.png
screenshots/scalar-webhook.png
screenshots/scalar-products.png
```

Recommended visual story:

1. Scalar API overview
2. successful Admin manual sync
3. sync history/audit result
4. signed webhook success/duplicate behavior
5. synchronized product state

Do not fabricate UI screenshots.

Redact:

- JWT tokens
- supplier API key
- webhook secret
- signatures if sensitive
- passwords
- authorization headers

---

# 57. README Requirements

Final root README should contain:

1. title
2. project description
3. portfolio highlights
4. technology stack
5. architecture
6. supplier sync workflow
7. HTTP resilience design
8. webhook security
9. idempotency design
10. synchronization concurrency rule
11. background synchronization
12. endpoint overview
13. configuration/User Secrets
14. database setup
15. running the API
16. Scalar/OpenAPI
17. demo flow
18. testing
19. security notes
20. screenshots
21. scope boundaries
22. freelance relevance

Important portfolio highlight:

```text
The system safely handles unreliable external HTTP communication and repeated webhook delivery without duplicating business side effects.
```

Do not exaggerate production scale.

---

# 58. Definition of Done

Project is complete only when:

- [ ] solution builds
- [ ] migration applies
- [ ] Admin login works
- [ ] JWT authorization works
- [ ] supplier typed client works
- [ ] pagination works
- [ ] manual sync works
- [ ] Product upsert works
- [ ] SyncRun audit works
- [ ] overlapping sync protected
- [ ] timeout behavior works
- [ ] transient 5xx resilience works
- [ ] 429 behavior works
- [ ] webhook HMAC verification works
- [ ] invalid signature rejected
- [ ] duplicate webhook is idempotent
- [ ] concurrent duplicates process once
- [ ] background sync works
- [ ] health endpoint works
- [ ] Problem Details safe
- [ ] no secrets committed
- [ ] tests pass
- [ ] vulnerability audit clean
- [ ] no pending model changes
- [ ] README complete
- [ ] genuine screenshots included
- [ ] repository portfolio-ready

---

# 59. Implementation Workflow

Each phase gets:

```text
Dedicated branch
Focused implementation
Automated tests
Manual verification where valuable
Pull request
Merge into master
Delete phase branch
```

Never implement the full project directly on `master`.

---

# 60. Phase 1 — Foundation

Branch:

```text
phase/01-foundation
```

Implement:

- solution/repository structure
- production project
- test project
- .NET SDK pin
- local dotnet-ef tool
- EF Core SQL Server
- core entities:
  - User
  - Product
  - SyncRun
  - WebhookEvent
- enums
- entity configurations
- database constraints
- initial migration
- OpenAPI
- Scalar
- Problem Details baseline
- configuration models
- health endpoint baseline
- test infrastructure

Important constraints:

```text
User.NormalizedEmail UNIQUE
Product.ExternalId UNIQUE
WebhookEvent.ExternalEventId UNIQUE
```

No external supplier HTTP integration yet.

No JWT yet.

Verification:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-restore -m:1
dotnet package list --vulnerable --include-transitive
dotnet ef migrations has-pending-model-changes \
  --project ./src/SupplierIntegrationApi/SupplierIntegrationApi.csproj \
  --startup-project ./src/SupplierIntegrationApi/SupplierIntegrationApi.csproj
git diff --check
```

---

# 61. Phase 2 — Admin Authentication

Branch:

```text
phase/02-admin-auth
```

Implement:

- Admin login
- email normalization
- password hashing
- JWT generation
- JWT validation
- Admin authorization
- current user service only if needed
- OpenAPI Bearer security
- authentication tests

No refresh tokens.

No public Admin registration endpoint.

Manual smoke test:

```text
Admin login
Anonymous protected endpoint → 401
Valid Admin protected endpoint → success
```

---

# 62. Phase 3 — Supplier REST Integration

Branch:

```text
phase/03-supplier-sync
```

Implement:

- Supplier options
- typed `ISupplierClient`
- supplier auth header
- supplier DTOs
- pagination
- external response validation
- Product upsert service
- manual Admin sync endpoint
- one-active-sync rule
- SyncRun audit history
- SyncRun list/detail endpoints
- Product read endpoints
- tests using controlled HTTP responses

Primary proof:

```text
External pages
→ typed HttpClient
→ validation/mapping
→ SQL Server upsert
→ auditable SyncRun
```

No retry/resilience work beyond basic safe handling yet unless required by typed-client foundation.

---

# 63. Phase 4 — HTTP Resilience

Branch:

```text
phase/04-http-resilience
```

Implement/refine:

- request timeout
- transient 5xx retry
- 429 handling
- Retry-After behavior where supported
- bounded retry policy
- cancellation behavior
- safe provider error mapping
- logging audit
- resilience tests

Do not retry unsafe side-effecting external operations.

Manual smoke test should use a controlled local provider/test fixture if practical.

---

# 64. Phase 5 — Secure Webhooks

Branch:

```text
phase/05-secure-webhooks
```

Implement:

- webhook endpoint
- raw-body HMAC-SHA256 verification
- constant-time comparison
- event ID header
- supported event types
- WebhookEvent persistence
- database-backed idempotency
- concurrent duplicate protection
- Product inventory/price updates
- safe unknown Product behavior
- safe unknown event behavior
- webhook tests

Critical proof:

```text
same ExternalEventId delivered concurrently
→ one effective side effect
```

---

# 65. Phase 6 — Background Sync & Portfolio Polish

Branch:

```text
phase/06-portfolio-polish
```

Implement/refine:

- scheduled `BackgroundService`
- scope handling
- no-overlap reuse
- scheduled SyncRun trigger type
- health checks finalization
- centralized exception handling/security audit
- FluentValidation audit
- CancellationToken audit
- OpenAPI summaries/descriptions
- README
- architecture diagram
- setup instructions
- demo flow
- testing instructions
- genuine Scalar screenshots
- repository cleanup
- Freelancer positioning

No new business feature beyond scheduled sync/health required by this phase.

Final verification:

```bash
dotnet restore
dotnet build --no-restore -m:1
dotnet test --no-restore -m:1
dotnet package list --vulnerable --include-transitive
dotnet ef migrations has-pending-model-changes \
  --project ./src/SupplierIntegrationApi/SupplierIntegrationApi.csproj \
  --startup-project ./src/SupplierIntegrationApi/SupplierIntegrationApi.csproj
git diff --check
git status
```

---

# 66. Git Workflow

Expected history:

```text
master
  ↓
phase/01-foundation
  ↓ PR
master
  ↓
phase/02-admin-auth
  ↓ PR
master
  ↓
phase/03-supplier-sync
  ↓ PR
master
  ↓
phase/04-http-resilience
  ↓ PR
master
  ↓
phase/05-secure-webhooks
  ↓ PR
master
  ↓
phase/06-portfolio-polish
  ↓ PR
master
```

Each PR description should include:

- summary
- behavior
- security considerations
- tests
- commands actually run
- build/test results
- migration/package notes
- explicit scope boundary

Do not claim verification that was not executed.

---

# 67. Code Quality Rules

- nullable reference types enabled
- implicit usings acceptable
- async APIs
- CancellationToken where meaningful
- TimeProvider for testable timestamps
- UTC timestamps
- decimal for money
- parameterized SQL if raw SQL is ever needed
- typed HttpClient
- no static HttpClient
- no generic repository
- no CQRS/MediatR
- no unnecessary inheritance
- no raw secrets in logs
- no dynamic unsafe URLs from user input
- explicit provider URL from trusted configuration
- clear method naming
- focused services
- thin controllers
- build with zero errors
- resolve meaningful warnings
- tests protect critical integration behavior

---

# 68. Final Portfolio Story

The finished repository should communicate the following clearly:

> This service integrates with an external supplier API, synchronizes product and inventory data into SQL Server, tolerates transient HTTP failures with bounded resilience policies, securely validates signed webhooks, prevents duplicate webhook side effects using database-backed idempotency, records synchronization history, and supports both manual and scheduled synchronization.

The main freelance-relevant skills demonstrated are:

```text
ASP.NET Core
C#
REST API Integration
Typed HttpClient
Third-Party APIs
Webhooks
HMAC
Idempotency
HTTP Resilience
Rate Limits
Background Services
Entity Framework Core
SQL Server
JWT
OpenAPI
Integration Testing
Backend Development
```
