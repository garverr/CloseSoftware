# Frontend & Workflow Review — Financial Reporting Software

**Scope:** `src/FinancialReporting.Web` (React 19 + Vite + TS, single-file `App.tsx` 4,078 LOC, `App.css` 2,263 LOC).
**Reviewed:** 2026-04-26.
**Method:** 20 independent lenses — engineering, design system, workflow, accessibility, data presentation. All findings cite `App.tsx`/`App.css` line numbers so you can jump straight in.

---

## Executive Summary

**The good** — this is *not* an AI-generated template. Typography (Source Serif 4 + Inter + JetBrains Mono), 8px spacing grid, custom focus rings, restrained accent purple, semantic component variants, and the slide editor's UX (autosave + undo/redo + drag-drop) all show real craft. Compares favorably to mid-tier B2B finance tools (Sage Intacct, early Mosaic).

**The risk** — engineering debt and workflow gaps are accumulating fast. The entire frontend lives in **one 4,078-line file**, there is **no URL routing** (back button is broken, no deep-links), there is **no test safety net**, accessibility is **not at WCAG AA**, and the highest-volume workflows (mapping inbox, version rollback, eliminations discovery) are **the lowest-polish ones**.

**Verdict:** ~7/10 visual, ~5/10 engineering, ~6/10 UX. To be "best in class" needs three concrete pushes: (1) modularise + route, (2) finish the mapping/audit/eliminations workflows to slide-editor quality, (3) accessibility + motion polish pass.

**Priority key:** **P0** = blocking best-in-class • **P1** = serious gap • **P2** = polish.

---

## Part 1 — Engineering & Architecture (Views 1–8)

### View 1 — Code architecture: God-component **[P0]**

`App.tsx` is one 4,078-line file with a single `App` function (lines 709–931) dispatching 17 views via 34 inline conditionals (882–921). 40+ sub-components live in the same module. Largest internal blocks: `XeroSettings` ~390 lines (3237–3624), `FluxReviewPanel` ~250 lines (1424–1673), `SlideEditor` ~200 lines (1787–1993).

**Why this matters:** every view edit reloads the whole module in dev, code review diff-noise is permanent, and onboarding a second engineer is painful.

**Action:** split into `src/views/*`, `src/components/*`, `src/api/*`, `src/types/*`. Extract `<ViewRouter>` to replace the conditional cascade.

### View 2 — State management: root bloat + prop drilling **[P1]**

11 `useState` calls in the root (710–721); `panel` (717) is a 3-mode union forced through every view; `mappingRefreshKey` (720) is a hack to bust a child's stale fetch; `onChanged` callbacks fire 3 sequential `setState`s inline (927). No context, no reducer, no selectors.

**Action:** lift package + context into a `PackageContext`, colocate panel state into the components that own it, replace refresh-key hack with proper invalidation. Consider `zustand` or context-per-domain — not Redux.

### View 3 — Routing: no URL state **[P0]**

`view` / `selectedSlideId` / `panel` are pure `useState` (715–717). **F5 dumps the user back to the dashboard. The browser back button does nothing. Slides cannot be linked or shared.** For an internal collaboration tool this is a hard blocker.

**Action:** adopt `react-router` v6 (or TanStack Router). URL paths: `/org/:orgKey/period/:periodKey/package/:id/slide/:slideId`. Persist panel + filters in search params.

### View 4 — Data fetching: no retry, cache, or dedup **[P1]**

`fetchJson/postJson/putJson/deleteJson` (4046–4076) are 4-line wrappers with `if (!response.ok) throw`. Every view switch re-fetches `/api/packages` (731–750). `FluxReviewPanel` fetches twice — once in a `useCallback` (1447), once in an effect (1454). Only `loadInitialContext` (752–777) cancels in-flight requests; the other 14 effects can race on rapid view switching.

**Action:** introduce TanStack Query (or SWR). Buys you cache, dedup, retry, mutation rollback, devtools — replaces ~60% of the manual `useState`/`useEffect` plumbing.

### View 5 — Realtime / SignalR: silent disconnect **[P1]**

Hub bootstraps with `.withAutomaticReconnect()` but `.start().catch(() => undefined)` (787) and `.stop().catch(() => undefined)` (789) swallow every error. Only one server event (`aiRunUpdated`, 781) is wired. The hub is recreated every time `refreshPackages` changes (791) — risk of reconnect thrashing on org/period switches. **There is no UI indicator of connection state**, so users believe they have realtime when they don't.

**Action:** subscribe to `connection.onclose / onreconnecting / onreconnected`, render a connection chip in the topbar (like Linear's), surface failures via toast.

### View 6 — Performance: no memo, no lazy load, lucide all-in **[P2 turning P1]**

Zero `React.memo`, `useMemo`, or `useCallback` for child components. `TopBar` and `Sidebar` (934, 1029) re-render on every root state change. 30 lucide icons imported eagerly (3–39). All 17 views are bundled into the initial JS chunk (882–921). Today the app is small enough to feel snappy; this rots fast.

**Action:** `React.lazy` each view, wrap children of `App` in `memo`, switch to `lucide-react/dist/esm/icons/<name>` deep imports if Vite tree-shaking under-performs (verify with `vite build --mode production`).

### View 7 — Type safety: DTOs duplicated, JSON blobs unchecked **[P2]**

60+ DTO interfaces hand-mirrored from the .NET API (45–656). String-typed JSON fields (`themeJson`, `monthlyJson`, `contentJson`, `chartConfigJson`) are parsed ad-hoc (4038–4043) with no schema validation. One backend rename = silent runtime breakage.

**Action:** generate types from the OpenAPI spec (`openapi-typescript`) or use NSwag at build time. Add `zod` schemas for the JSON blob fields and parse at the API boundary.

### View 8 — Tests: only a smoke script **[P0]**

`scripts/frontend-smoke.mjs` (94 lines) checks API reachability and asserts a marker exists in built `App.tsx` source. **No unit tests, no component tests, no Playwright.** ESLint + react-hooks plugin is the only safety net. Any refactor — and you need several — will break things you can't see.

**Action:** Vitest + React Testing Library for components, Playwright for the 6 critical workflows (create package → map → eliminate → edit slide → export → version restore).

---

## Part 2 — Visual Design System (Views 9–13)

### View 9 — Typography: **Strong** **[Keep]**

Three intentional faces, each with a job: Source Serif 4 (brand, h1), Inter (UI body), JetBrains Mono (financial figures, line 577 / 852). Hierarchy is clear (28 / 22 / 20 / 16 / 12 / 9–10), eyebrow labels at 9–10px / 800 weight / .08em tracking are an editorial touch most B2B apps miss.

**Polish:** add `font-feature-settings: "tnum" 1, "ss01" 1` on `.metric` / `.kpi-value` so columns of numbers actually align. JetBrains Mono won't tabular by default in proportional contexts.

### View 10 — Color & semantics: **OK** **[P1 contrast]**

Palette (CSS 7–18) is restrained and corporate-appropriate: navy primary `#0f2a4a`, contained purple accent `#6b4fa8`, semantic good/warn/bad. **Two contrast problems:**

1. `--muted: #6b6b70` on `--bg: #f7f6f4` ≈ 4.0:1, on `--soft: #f2f0ec` ≈ 3.8:1. WCAG AA requires 4.5:1 for body text. The `.muted` class is used widely on small text (e.g. `confidence {n}%`, App.tsx:1409) — **fails AA**.
2. Variance is conveyed by colour alone (`.good-text`/`.bad-text`). Add an icon or `▲/▼` glyph everywhere variance is shown (already done at 1357, missed at 2082).

No dark mode. For a tool finance teams stare at for hours, that's a real omission, but P2.

### View 11 — Spacing, layout, density: **Strong** **[Keep]**

Strict 4/8 px grid, no magic numbers. Three-column shell (topbar 56 / sidebar 240 / content / right panel 420) with sticky context-switcher is ideal for finance pros. Density is appropriate — closer to Linear than to Salesforce.

**One nit:** `.context-switcher` 6-column grid (CSS 94–106) is information-dense and beautifully arranged on desktop, but its 1120px and 780px breakpoints are the only responsive thinking in the file. Below 1024px the app degrades.

### View 12 — Motion & micro-interactions: **Weak** **[P1]**

Only three transitions in 2,263 CSS lines: `.content` margin (370), `.side-panel` slide (1132), `.ai-progress-track` fill (2003). No hover scale, no button press feedback, no toast slide-in, no focus-ring fade, no skeleton shimmer, no row-add/remove animation in the slide editor. The product feels static and "documented" rather than alive.

**Action:** `prefers-reduced-motion` aware micro-interactions on buttons, rows, toasts, and AI status. ~150 lines of CSS would lift the whole feel.

### View 13 — Component coherence: **Strong** **[Keep]**

Button variants (default / primary / accent / ghost / disabled, CSS 430–467), unified inputs (905–927), segmented controls (198–229), severity chips, mini-tables, side-panel modals — all share radii (5/6/7/8 px), border treatment, and focus colour-mix. No half-baked one-off styles.

**Polish:** extract these into a `<Button variant>`, `<Chip>`, `<SegmentedControl>` component so they stop being CSS-class incantations.

---

## Part 3 — Accessibility & Data Presentation (Views 14–17)

### View 14 — Accessibility (WCAG 2.2 AA): **Weak** **[P0 for any external/regulated user]**

Top issues with line refs:

- Form fields lack `htmlFor` association — `.context-field` (974–995, 2229–2253) wraps a `<span>` label and a `<select>`, so screen readers don't announce the label.
- Segmented controls (1582–1587, CSS 198–229) are buttons with `.active` styling but no `role="radiogroup"` / `role="radio"` / `aria-checked` / arrow-key handling.
- No `:focus-visible` rules in `App.css`. Only `.context-field select:focus` exists (181). Tab navigation is invisible across most of the app.
- Sidebar (1047) has no `<nav>` / `role="navigation"`; side panel (`.side-panel`) is not declared as a dialog and has no Escape-to-close.
- Lucide icons are imported without an `aria-hidden`/labeling convention (4–38). Icon-only buttons use `title` not `aria-label` (e.g. 1021).
- `.muted` small-text colour fails AA (see View 10).
- AI popover at 3884 sets `aria-live="polite"` — good — but no other live regions exist; SignalR-driven changes happen silently.

**Action:** dedicated a11y sprint. The cheapest big wins are `:focus-visible`, `<label htmlFor>`, fix `--muted`, and add `aria-label` on icon-only controls.

### View 15 — Empty / loading / error states: **OK** **[P1]**

Empty states exist (1082, 1226, 1250, 1591, 2523) but most are pure prose with no CTA, illustration, or next-step link. No skeletons anywhere — only "Loading…" text. Toast (929 / 3932) auto-dismisses with no close button, no severity variants, no persistent log; if a user looks away they miss errors. The `isSourceDataStale` flag (1004) is shown only as a tooltip-coloured word in the topbar — **this is the single most important freshness signal in the product and it's nearly invisible.** No optimistic-update rollback in the slide editor (1811–1920) — on save failure the textarea keeps its value with no retry CTA.

**Action:** introduce a `<Banner severity>`, `<Skeleton>`, and richer toast (success/warn/error/persistent). Promote stale-source to a top-of-page banner on `dashboard` / `slide` / `output` views.

### View 16 — Number, currency, variance presentation: **Weak** **[P1]**

This is a financial reporting product; numbers are the product. Today:

- `fmtMoney` (4001) uses `toLocaleString('en-US', { maximumFractionDigits: 0 })` — locale is hard-coded, and 0-decimal hides cents on small accounts.
- No `font-feature-settings: "tnum"` on `.metric`/`.kpi-value` (CSS 577, 852) — columns don't align.
- Number columns aren't right-aligned (`.mini-table`, `.xero-table` lack `text-align: right` on numeric `<td>`s).
- Variance display mixes formats: "↑ $245,000 (12.3%)" at 1358 vs colour-only at 2082.
- Negative number convention is inconsistent: U+2212 minus, parens, and color are all used in different views.
- Confidence (e.g. AI mapping suggestions, 1409) is rendered as muted small text — the most important trust signal in the product is the lowest-contrast element.

**Action:** add `formatCurrency`, `formatPercent`, `formatVariance`, `formatRelativeDate` helpers with org-level locale + currency, apply tabular-nums globally, right-align numeric columns, pick one negative convention (parens for accounting, minus for KPIs).

### View 17 — Tables & data grids: **Weak** **[P1]**

`.xero-table` and similar (CSS 1744–1802) force horizontal scroll on narrow screens, have no sticky `<thead>`, no sticky first column, no sortable headers, no column resize/reorder/hide, no zebra, no row-density toggle. Account mapping table (2185–2217) — the most-used surface in the app — has none of these. The 780px breakpoint hides `<thead>` entirely (CSS 2228–2230) and relies on pseudo-element labels (2242–2252), which is fragile.

**Action:** standardise on TanStack Table for the 4 heavy grids (mapping inbox, statements, flux, planning). Sticky header + sticky first column + right-aligned numeric columns is table-stakes.

---

## Part 4 — Workflow Review (Views 18–20)

Workflows traced against the user journey. Each line cite is `App.tsx`.

### View 18 — Mapping inbox (highest-volume workflow): **Weak** **[P0]**

`MappingView` (2094) → `AccountPanel` (2287). For each unmapped account a user must: (1) click row → (2) scan suggested FS line → (3) pick from dropdown (2368) → (4) click "Accept" (2381) → (5) close panel. **Five clicks per account, no bulk select, no keyboard shortcuts, no undo, no filter-by-confidence, hard-coded 70/30 split (2324), and the AI confidence is shown but you cannot inspect *why* it's low.** A real customer with 200 first-seen accounts will hate this.

**Action:** redesign to a keyboard-first inbox (j/k to move, A to accept suggestion, S to split, R to reject, U to undo). Add multi-select, "Accept all >85% confidence" bulk action, configurable split, confidence-evidence hover-card.

### View 19 — Slide editor: **Strong** **[Keep, lightly polish]**

`SlideEditor` (1787) is the standout in the codebase. 30-step undo/redo (1804–1847), HTML5 drag-drop block reorder (1875–1884), implicit autosave with idle/saving/saved/error status (1848–1859, 1920), inline editing (2059–2070), block-level chat (1967–1989). Add a side-by-side preview-vs-PDF parity view (currently you can't trust the export matches the editor) and animate row insert/delete and you have a top-tier surface.

### View 20 — Versioning, audit, eliminations discovery: **Weak** **[P0 for compliance]**

Three related gaps that together fail the "trust" requirement of finance work:

1. **History (`HistoryPanel`, 3721–3746)** — whole-package snapshots only. No slide-level history, no field-level diff, no user attribution in the row (3741), no rollback confirmation, no preview of what restoring will overwrite. A user cannot revert "the block I just edited" without losing every other change since.
2. **Issues (`IssueWorkbench`, 1373–1422)** — flagged issues have title + description + confidence, with "Apply fix" / "Ignore" actions only (1412–1415). No drill-down into *which accounts triggered the rule* or *what threshold was checked*. No way to author manual rules.
3. **Eliminations (`AccountPanel` actions, 2341–2350)** — you can fire eliminate / intercompany / exclude per account, but **there is no UI listing active rules**. To see why account 8200 was eliminated last month you'd have to git-blame the audit log.

**Action:** field-level audit log with diff viewer; dedicated `/rules` view listing every active elimination + intercompany + recurring rule with last-fired info; per-issue evidence drill-down (which accounts, which thresholds, which prior period comparison).

---

## Bonus context (not counted in the 20)

These three were noted but didn't warrant a full lens:

- **First-run experience** — users land on `PackagePlaceholder` (1122) with one button. Add a 30-second guided tour (Xero connect → first sync → first mapping → first slide → first export). Drives time-to-value, demo-able.
- **Context switcher (TopBar 960)** — actually one of the *strongest* surfaces. Stale signaling, sync freshness, TB count, package status all in one place. Keep this; treat it as the design language for every other status surface.
- **Xero connect/sync (3237)** — solid OAuth popup, status cards, reconnect-per-tenant, backfill preview. Use this as the reference for how *every* external integration should look.

---

## Recommended Sequence

If you do this in waves rather than all at once:

**Wave 1 (foundation, ~2 weeks):**
- Split `App.tsx` into `views/`, `components/`, `api/`, `types/`.
- Add `react-router` with URL state.
- Add Vitest + Playwright skeleton with one test per critical workflow.
- Fix `--muted` contrast and add `:focus-visible` globally.

**Wave 2 (workflow finishers, ~3 weeks):**
- Mapping inbox redesign (bulk + keyboard + undo).
- Field-level audit log + diff viewer.
- Rules registry view.
- Number-formatting helpers + tabular-nums + right-aligned numeric columns.

**Wave 3 (polish, ~1 week):**
- TanStack Query for the data layer.
- Motion pass (hover, toast, skeleton, AI status).
- Stale-source banner + SignalR connection chip.
- Toast variants + persistent error log.

After Wave 1+2, this product is genuinely best-in-class for the segment.
