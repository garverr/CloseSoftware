# Security Review: Auth, Secrets & Encryption at Rest

**Categories:** 41, 42, 43
**Reviewer:** Agent 18 of 20
**Date:** 2026-04-27
**Pillars covered:** Xero import, Flux + AI GL investigation, AI Board Package

---

## Category 41 — Auth Model: Header-Based Auth + Admin Bypass

| Finding | Severity | File:Line | Detail |
|---------|----------|-----------|--------|
| No `AddAuthentication` / `AddAuthorization` middleware registered | **BLOCKER** | `Program.cs:1–100` | Zero calls to `AddAuthentication`, `AddAuthorization`, `UseAuthentication`, or `UseAuthorization`. The entire auth model is a hand-rolled helper. |
| `Can()` defaults to `["Admin"]` when header absent | **BLOCKER** | `Program.cs:3634–3639` | `var activeRoles = string.IsNullOrWhiteSpace(roleHeader) ? ["Admin"] : …` — any unauthenticated caller is silently granted Admin rights. |
| `Actor()` defaults to `"dev-admin"` | **BLOCKER** | `Program.cs:3643` | Audit trail actor is `"dev-admin"` when `X-FR-User` is not supplied. SOC 2 CC6.1 and SOX requires non-repudiation; spoofed actor names corrupt the trail entirely. |
| `Role()` defaults to `"Admin"` | **BLOCKER** | `Program.cs:3646` | Same bypass as `Can()` — role recorded in audit records is fabricated. |
| No environment guard on bypass | **BLOCKER** | `Program.cs:3632–3646` | The `IsDevelopment()` guard at line 75 applies only to OpenAPI docs. The Admin bypass is active in every environment including production. |
| `X-FR-Role` / `X-FR-User` are plain HTTP headers | **Major** | `Program.cs:3634,3643` | Any client can send arbitrary values; no signature, no cryptographic binding. Impersonation of any role or user is trivial from inside the network. |
| No OIDC/JWT/session token in any service registration | **BLOCKER** | `Program.cs:1–100` | No Bearer token validation, no cookie auth, no OpenID Connect. All three pillars (Xero data import, Flux AI, Board Package) are protected only by these headers. |

**Sub-verdict (41):** Production-catastrophic. An attacker who can reach the API with no headers has full Admin access to all financial data, Xero OAuth flows, and AI operations.

---

## Category 42 — Secret Management

| Finding | Severity | File:Line | Detail |
|---------|----------|-----------|--------|
| `Xero:ClientId` hardcoded in `appsettings.json` | **Major** | `appsettings.json:18` | `CC60149FD029424D80EC6A83DC1BD0FF` committed in plaintext. Should be in user-secrets (dev) or Key Vault / AWS Secrets Manager (prod). |
| No `Xero:ClientSecret` found anywhere | **BLOCKER** | All service files | `FinancialServices.cs:1649,2900`, `XeroLedgerServices.cs:590`, `XeroBackfillServices.cs:1142` — token refresh calls send only `client_id` and `refresh_token`. Xero's PKCE flow for public clients may be intentional, but if the app is registered as a confidential client the secret is simply absent and token refresh will silently fail or succeed insecurely. No documentation or comment clarifies the registration type. |
| Developer filesystem path exposed in config | **Major** | `appsettings.json:25` | `"FinanceAppV2DbPath": "/Users/rickygarver/Projects/Finance App V2/…"` — absolute developer path committed; leaks internal project structure and username. |
| No `UserSecretsId` in any `.csproj` | **Major** | All `.csproj` files | No user-secrets integration; developers will continue embedding secrets in config files. |
| DataProtection keys stored on local filesystem | **Major** | `Program.cs:22–31` | `PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))` defaults to `%LOCALAPPDATA%/FinanceApp/DataProtection-Keys`. No key ring encryption at rest (no `ProtectKeysWithAzureKeyVault`, `ProtectKeysWith*`). Keys are unprotected XML files; compromise of the host gives full key ring access, decrypting all Xero tokens. |
| `DataProtection:KeysPath` not set in either appsettings | **Major** | `appsettings.json`, `appsettings.Development.json` | Falls through to environment variable, then OS default — no production override path specified. |
| Codex CLI runs as the OS user logged into Codex | **Major** | `Program.cs:60–62` | `CodexWorker` is a hosted service. It inherits process-level OS credentials; no sandboxing or least-privilege service account. AI inputs containing financial data are fed to this process. |

**Sub-verdict (42):** DataProtection key ring is unencrypted on disk; Xero access/refresh tokens rely on it. ClientId is hardcoded. ClientSecret presence/absence is ambiguous. Codex runs with full OS user credentials.

---

## Category 43 — Encryption at Rest for Financial Data & PII

| Finding | Severity | File:Line | Detail |
|---------|----------|-----------|--------|
| SQLite database has no encryption | **BLOCKER** | `Program.cs:43–46`, `appsettings.json:7` | `Data Source=financial-reporting-dev.db` — plain SQLite with no `SQLCipher` or transparent encryption. All journals, trial balances, GL transactions, and contact-level data are stored in cleartext. |
| `XeroJournalLine.Description` stored unencrypted | **Major** | `Domain/Entities.cs:396` | Journal line descriptions may contain vendor names, employee references, and payment memos — PII under GDPR / SOC 2 PI criteria. No column-level encryption. |
| `XeroJournal.Reference` stored unencrypted | **Major** | `Domain/Entities.cs:380` | Same concern. |
| `XeroJournal.PayloadJson` stores full Xero payload | **Major** | `Domain/Entities.cs:381` | Raw JSON blob from Xero; may include contact names and other PII. No encryption or field-level masking. |
| DataProtection key ring itself is unencrypted on disk | **BLOCKER** | `Program.cs:29–31` | Keys protect Xero OAuth tokens but the keys themselves are stored as plaintext XML. If the host filesystem is compromised, all encrypted tokens can be decrypted. |
| `AuditRecord.BeforeJson` / `AfterJson` contain financial diffs | **Major** | `Program.cs:3660–3672` | `RedactSensitive` (line 4778–4792) only replaces the string key names (e.g. `"access_token"`) but not the values or financial line amounts. Audit records hold financial statement snapshots in plaintext. |
| AI prompt contains full package snapshot including PII-adjacent fields | **Major** | `FinancialServices.cs:956–978` | `snapshotJson` passed directly to Codex worker includes `ContentJson` from blocks (line 615) and `Description` fields. No PII-scrubbing layer before prompt construction. |
| `RedactSensitive` key-name redaction is brittle | **Major** | `Program.cs:4786` | Replaces field names, not values. A payload `{"access_token":"eyJ..."}` becomes `{"[redacted]":"eyJ..."}` — the token value is still present. |

**Sub-verdict (43):** Financial data and PII sit in a cleartext SQLite file. DataProtection keys are unencrypted at rest. The AI prompt pipeline has no PII scrub before forwarding snapshot data to Codex.

---

## Cross-Cutting Verdict

All three pillars — Xero import, Flux AI investigation, and AI Board Package — share a single authorization surface that provides **zero cryptographic identity guarantees** in production. The same Admin bypass that makes local dev convenient gives any network-reachable caller unrestricted read/write/delete access to multi-entity financial data. Combined with cleartext SQLite storage and unencrypted DataProtection keys, the blast radius of a single host-level compromise is total.

---

## Top 3 Fixes (Priority Order)

**Fix 1 — Eliminate the Admin bypass and add real authentication (Cat 41)**
Register ASP.NET Core JWT Bearer or OIDC authentication middleware. Gate the bypass strictly with `if (app.Environment.IsDevelopment())` or remove it entirely and use `dotnet user-secrets` with a dev identity provider. Replace the `Can()` / `Actor()` / `Role()` helpers with `ClaimsPrincipal` claims. All 70+ `Can(http, …)` call sites must gate on the authenticated identity, not a forged header.
*Evidence:* `Program.cs:3632–3646`, `Program.cs:75`.

**Fix 2 — Encrypt the DataProtection key ring and move secrets out of config (Cat 42)**
Add `ProtectKeysWithAzureKeyVault` (or equivalent) so the key ring is never stored as plaintext XML. Move `Xero:ClientId` to user-secrets (dev) and Key Vault (prod). Add `UserSecretsId` to the `.csproj`. Clarify whether Xero app is public (PKCE only) or confidential (requires `ClientSecret`); if confidential, store the secret in the vault.
*Evidence:* `Program.cs:29–31`, `appsettings.json:18`.

**Fix 3 — Encrypt SQLite at rest and add a PII-scrub layer before AI prompts (Cat 43)**
Replace plain SQLite with SQLCipher (or migrate to a server DB with TDE) for the production connection string. Before `BuildPrompt` serialises the snapshot, strip or hash `Description`, `Reference`, and `PayloadJson` fields that contain transaction-level PII. Fix `RedactSensitive` to redact field values, not just key names.
*Evidence:* `appsettings.json:7`, `FinancialServices.cs:956–978`, `Domain/Entities.cs:380,381,396`, `Program.cs:4786`.
