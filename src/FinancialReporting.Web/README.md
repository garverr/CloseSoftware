# FinancialReporting.Web

React 19 + Vite + TypeScript frontend for the Financial Reporting platform.

## Run

```bash
npm install
npm run dev -- --host 127.0.0.1
```

Open <http://127.0.0.1:5173>. The dev server proxies API calls to <http://localhost:5264>.

## Layout (target)

The app is mid-migration out of a single ~4,700-line `App.tsx`. New screens should land as their own files; `App.tsx` is being incrementally drained.

```
src/
  App.tsx                # router/shell + remaining unmigrated screens
  pages/                 # route-level pages (target home for screens)
  features/              # feature folders (data hooks + UI for one feature)
    flux/
    packages/
    xero/
    ai/
  components/            # shared primitives (segmented controls, Sparkline, ...)
  hooks/                 # shared cross-feature hooks
  api/                   # typed fetch wrappers per feature
  tokens.css             # design tokens (only file allowed to define :root vars)
```

See `CONTRIBUTING.md` at the repo root for the contribution rules.

## Smoke test

```bash
npm run test:smoke
```

Confirms the build output exists and the bundle isn't pathologically large.

## Backend handoff

This frontend talks to `FinancialReporting.Api`. The API contract:

- All endpoints prefixed `/api/`. Errors are RFC 7807 ProblemDetails (P3.32).
- Real-time AI run status via SignalR at `/hubs/ai`. Subscribe to a run via `connection.invoke('SubscribeToRun', runId)` (P3.35) — don't listen to broadcasts.
- The CFO approval gate at `POST /api/packages/{id}/approve` is required before `DistributionSchedule` can send (P1.16).
- The marquee diff at `GET /api/packages/{id}/diff` returns typed Keep / Modify / Add / Remove decisions you can render directly (P1.1).

## Accessibility

Target is WCAG AA. The audit identified specific gaps:

- Add `aria-label` to every icon-only button.
- Use `:focus-visible` for keyboard-focus styling (already global in `App.css`).
- The `--muted` token has been adjusted to ≥ 4.5:1 contrast on light backgrounds.
- Sidebar uses `role="navigation"`; modal panels declare `role="dialog"` + `aria-modal`.
