# Financial Reporting Software

Standalone board-package reporting app built from the supplied Ledgerline-style frontend prototype.

## Stack

- Frontend: React + Vite + TypeScript
- Backend: ASP.NET Core 10 + EF Core
- Development database: SQLite
- Completion phase database: SQLite only (SQL Server deployment/migrations intentionally excluded here)
- AI runtime: server-side Codex CLI only; no OpenAI API key is used by the app

## Run Locally

Start the API:

```bash
dotnet run --project src/FinancialReporting.Api --urls http://localhost:5198
```

Start the frontend:

```bash
cd src/FinancialReporting.Web
npm run dev -- --host 127.0.0.1
```

Open `http://127.0.0.1:5173`.

## Configuration

`src/FinancialReporting.Api/appsettings.Development.json` uses SQLite and a mock Codex runner so local UI flows can be tested without launching Codex jobs.

For the controlled server in this phase:

- Keep `UseSqlite=true`.
- Set `Ai:UseMockRunner=false`.
- Run the service under the OS account that is logged into Codex CLI.
- Configure Xero with the same OAuth app settings used by Finance App V2.
- Use the same DataProtection app name/key ring when attempting V2 Xero token import; otherwise reconnect tenants in this app.

## Implemented Workflows

- Package overview, slide editor, mapping inbox, eliminations, KPI library, branding, layouts, output/share, live dashboard, and AI settings screens.
- Durable API records for packages, slides, blocks, issues, versions, Xero OAuth sessions/connections/syncs, mappings, recurring elimination rules, eliminations, exports, share links, schedules, audit records, and AI runs.
- Persistent editor APIs for slide/block update, create, delete, and reorder.
- Codex CLI job queue with model discovery from local Codex configuration/cache, SignalR status updates, strict JSON validation, retry-on-invalid-output, redacted logs, and validated user-approved fix operations.
- First-seen account handling with mapping review status, split/reject/review actions, recurring elimination rules, and consolidation treatments.
- Real local PDF/XLSX artifact generation with download endpoints and export QA metadata.
- Dev role enforcement through `X-FR-Role` / `X-FR-User` headers, defaulting to an Admin dev-login bypass when the headers are not supplied.
