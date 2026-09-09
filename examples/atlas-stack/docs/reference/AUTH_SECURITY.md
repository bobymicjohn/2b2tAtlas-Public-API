# Authentication, RBAC, Security, And Trust Boundaries

## Scope

This file covers user identity, authorization, worker credentials, and system trust boundaries. WDL archive defenses are summarized in `WDL_INGESTION.md` and detailed in `docs/INGESTION_SECURITY.md`.

## JWT Authentication

**Production:** `2b2tAtlas.Server/Program.cs` registers `Services/AtlasAuthentication.cs`, which validates JWT issuer, audience, lifetime, signing key, and uses zero clock skew. `2b2tAtlas.Server/Services/AuthService.cs` emits identity, role, `superadmin=true`, and one `perm` claim per effective permission.

`JwtSettings__SecretKey` must be host-managed. Authentication proves identity; write access still requires the endpoint's named permission policy. The authoritative client refresh path is `GET /api/auth/profile`, not editable browser-local user JSON.

Login permits 10 attempts per minute per forwarded/client IP; registration permits 5 per 10 minutes. `AuthService` also owns persisted failed-login and lockout behavior.

## Canonical Roles

| Stored role | Display name | Default intent |
| --- | --- | --- |
| `SuperAdmin` | Founder | Every permission and role-profile control |
| `Admin` | Archivist | Content/user administration, moderation, renders, audit |
| `Cartographer` | Cartographer | Mapping, attachments, moderation, renders, groups |
| `HighwayArchitect` | Highway Architect | Highway create/edit |
| `Chronicler` | Chronicler | Historical location create |
| `User` | Member | Public/read-only |

Canonical IDs, display names, rank checks, permission constants, and default bundles are in `2b2tAtlas.Shared/Models/Auth/Rbac.cs`. Unknown role IDs normalize to no role and receive no default permissions.

## Permission Resolution

One ASP.NET authorization policy is registered for each `Permissions.All` value. A policy requires an authenticated principal with `superadmin=true` or the matching `perm` claim.

For non-SuperAdmin users, `RolePermissions` database rows replace the role's code default when rows exist. A reset deletes override rows and restores the code bundle. Every authenticated request revalidates the account and rebuilds permissions from current database state. Disabling an account or changing its password invalidates its existing sessions on the next request.

`AtlasSessionValidator` recognizes only the configured owner identity (account 1 / atlas-owner on the reference instance). `AtlasWriteProtection` keeps every DELETE, account/role change, global render change and human ingestion mutation owner-only, independently of role overrides. `AtlasRecoveryStore` requires a verified pre-edit database snapshot and enforces durable per-account/shared quotas. See [highway contributor access and recovery](../HIGHWAY_CONTRIBUTORS.md) for editor validation, conflict checks, damage warnings and targeted restore.

## Public, User, And Worker Identities

`Services/AtlasOpenApi.cs` publishes only explicitly anonymous GET operations in
`/openapi/v1.json`. Conflicting authorization metadata and the retired legacy
warp-write route are excluded. `/openapi/internal.json` requires `users.manage`
and declares each protected operation's Bearer or worker-key security requirement.
The public document-name route is constrained to v1; it cannot select the internal
schema. OpenAPI responses use `Cache-Control: private, no-store`. Worker-key
attributes describe the controller's existing fixed-time key checks; they are
documentation metadata, not a substitute for those checks.

`OpenApiSecurityTests` runs an isolated HTTP host with the production authentication
registration and controller metadata. It checks every protected controller route
for anonymous denial, permission-policy routes for viewer denial, schema access,
public schema exclusions, and invalid/expired/incorrectly signed token denial.
Separate ingestion tests reject missing/incorrect keys on all four worker actions.

Public GET endpoints use `[AllowAnonymous]`. User writes use bearer JWTs and permission policies. The ingestion worker is a machine identity and does not use a user JWT for queue claim/status.

The worker sends raw `X-Atlas-Worker-Key`; the API stores/configures only its SHA-256 and compares hashes in fixed time. A successful claim returns a random token; only its SHA-256 is persisted. Each status update must present both worker key and active claim token.

## WDL Upload Boundary

The preferred upload path in `2b2tAtlas.Server/Controllers/IngestionJobsController.cs` is an authenticated, owner-only session additionally requiring `renders.manage`. The API accepts ordered 32 MiB chunks with exact offsets, caps the declared archive at 32 GiB, expires incomplete sessions after 24 hours, and rejects completion until declared length and ZIP signature match. The legacy 1 GiB multipart endpoint remains for compatible clients.

Upload metadata is bounded JSON validated by `IngestionJobValidator`; optional `worldRoot` is a safe archive-relative selector, not a host path. Partial files use random names and are deleted on rejection or expiry. Completion SHA-verifies the ZIP into the configured content-addressed archive (E: on the reference instance; X: holds backups) before queueing. The API stores and archives the bytes but never extracts or renders them.

## Data Trust Boundaries

- Location, highway, group, role, render, and attachment writes are untrusted API input.
- WDL ZIP contents, NBT, chunks, filenames, and renderer output are hostile.
- Wiki pages and BlackBrain retrieval text are untrusted data, never instructions.
- Published tiles are immutable output, not an input channel.
- Coordinates require source/WDL evidence; AI suggestions cannot move them.

## Secrets And Isolation

Do not commit JWT keys, worker keys or hashes, generated seed credentials, production databases, WDLs, renderer profiles containing local secrets, or tunnel tokens. Run API and worker as non-administrator accounts. The renderer must not access Atlas DB/source, browser profiles, SSH keys, cloud credentials, or API/user tokens.

**Optional:** BlackBrain receives only public/source-attributed artifacts. **Future:** a dedicated Atlas integration must not reuse generic `/api/tools/{name}/invoke` and must never grant mutation or publication authority.
