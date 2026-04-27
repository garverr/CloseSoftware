# Board Package Visuals Review — Agent 12

**Categories:** 22 (Chart / visualization auto-generation appropriateness), 24 (Slide layout templating, branding, consistency)

**Pillar:** AI Board Package

---

## Category 22 — Chart / Visualization Auto-Generation Appropriateness

### Evidence Summary

| Observation | Severity | File:Line |
|---|---|---|
| All "chart" blocks route to a single `ColumnChart` component regardless of `componentVariant`; variants `rolling-12`, `year-over-year`, `budget-projection`, `scenario-chart` are library entries only — no distinct rendering exists | Blocker | App.tsx:2746–2753, 5050–5097 |
| No waterfall / bridge chart exists; "Waterfall Bridge" appears only as a named section in the "Best-in-class reporting pack" template but has zero visual implementation | Blocker | Program.cs:4703, App.tsx (no match) |
| No stacked-bar, combo (line+bar), donut, area, or scatter chart types exist anywhere in the codebase | Major | App.tsx:4859–4884 (ColumnChart is the only chart function) |
| `defaultContent('chart', ...)` hard-codes `type: 'clustered'` for every chart block; chart type is never read back to switch rendering | Major | App.tsx:5011 |
| `Sparkline` is SVG-native and renders correctly as a trend line with optional prior-year overlay — appropriate for KPI tiles | Pass | App.tsx:4800–4811 |
| `ColumnChart` renders paired prior/current bars with month labels and negative-value coloring — correct for monthly trend, but visually thin (`width: 10px` fixed bars) against a 260 px min-height canvas | Minor | App.tsx:4873–4884, App.css:1307–1328 |
| BlockInspector chart inspector exposes only `showLegend`, `showDataLabels`, and `frequency` — no chart-type selector, so users cannot switch from bar to line or waterfall even if rendering existed | Major | App.tsx:2851–2869 |
| The "Best-in-class reporting pack" template names "Waterfall Bridge", "Common-size Analysis", "Driver Tree" — sections that imply distinct chart types with no rendering behind them; board recipients would see empty text blocks | Blocker | Program.cs:4703, Program.cs:3123–3124 |

### Analysis

The component library catalogues six chart variants (`monthly-trend`, `rolling-12`, `year-over-year`, `budget-projection`, `scenario-chart`, `waterfall-bridge` implied by template) but collapses all rendering to one clustered bar function. Best-in-class peers (Fathom, Syft) auto-select chart type by context: waterfalls for variance bridge, stacked bars for revenue-by-segment, combo charts for actuals + forecast. This system has the data model (`monthlyJson`, `priorMonthlyJson`, `varianceAmount`) to support those types but no rendering logic distinguishes them.

---

## Category 24 — Slide Layout Templating, Branding, Consistency

### Evidence Summary

| Observation | Severity | File:Line |
|---|---|---|
| `BrandingView` correctly stores primary color, accent, font family, cover style (3 options), header text, footer text, and logo filename per package — solid multi-tenant foundation | Pass | App.tsx:3946–4027 |
| CSS injects `--primary` and `--accent` as CSS custom properties at the root package shell, so chart bars, buttons, and borders respond to tenant color without code changes | Pass | App.tsx:952–953, App.css:8–9 |
| Logo upload button exists in UI but calls `putJson` with a hardcoded `logoFileName` string; no file upload endpoint or `<input type="file">` is wired — the `cover-mark` renders only an abbreviation text square, not an actual logo image | Major | App.tsx:3959, 4013–4015, 4017–4024 |
| Cover preview in `BrandingView` does not respond to `coverStyle` selection at runtime — the three options ("Modern", "Classic", "Executive") are stored in themeJson but the preview `div.cover-preview` is a single static layout with no conditional styling | Major | App.tsx:3999–4024 |
| `LayoutsView` is read-only: the slide list is rendered but there are no drag-to-reorder, show/hide toggle, or per-slide layout (portrait/landscape, column count) controls | Major | App.tsx:4030–4051 |
| `fontFamily` is persisted and returned in themeJson, but is never applied to any CSS property in App.tsx or App.css; `font-family` in App.css is hard-coded to `Inter` at `:root` and never reads the CSS variable | Major | App.tsx:3950, 3995, App.css:21 |
| Slide header is consistent — every `SlideBlock` renders inside a `.report-canvas` with `.slide-header` showing subject, KPI label, variance badge, and action buttons; the 12-column grid system (`span-4/6/8/12`) enforces layout uniformity | Pass | App.tsx:2488–2608, App.css:1064–1082 |
| Block-level branding tokens (`--primary` on `.bar.current`, `.button.primary`) are consistent but chart bars use a fixed gray (`#b8b8bf`) for prior-year instead of a lighter tint of `--primary`, so the prior bar color breaks from theme when primary changes | Minor | App.css:1313–1315 |
| No per-slide or per-section background color, column-count override, or page-break control exists; all slides use identical single-canvas layout | Major | App.tsx:2346–2345 (SlideEditor — no layout variation props) |
| Template application creates slides with only two generic blocks (a text placeholder and a table stub) regardless of section name; chart type from `ChartConfigJson` is serialized as `type: "template"` which is never rendered | Major | Program.cs:3120–3124 |

### Analysis

The branding plumbing (color tokens, themeJson persistence, cover-style options, header/footer text) is architecturally correct and multi-tenant ready. However, four stored settings — font family, cover style variants, logo file, and page-order reordering — are disconnected from any rendering path. A board package exported today would look identical for every tenant except for primary/accent color. Fathom and Syft give each client a visually distinct cover page with their logo, chosen font, and a layout that differs between "Modern" and "Executive" modes.

---

## Pillar Verdict — AI Board Package

The board package editor has a solid structural skeleton (block grid, slide ordering, branding API, template library) but is pre-functional on visualization and branding output. The single `ColumnChart` rendering all chart variants and the absence of any waterfall, stacked, or combo chart means the "Best-in-class reporting pack" template — the flagship upsell item — produces a package whose named sections ("Waterfall Bridge", "Driver Tree") contain only placeholder text. Similarly, branding settings are persisted but four of six stored fields are never applied to the rendered output. The package cannot currently go into a board deck without substantial visual uplift.

---

## Top 3 Fixes

1. **[Blocker] Implement chart-type routing in `SlideBlock`** — Add a `switch(componentVariant)` inside the `block.kind === 'chart'` branch (App.tsx:2746) so `monthly-trend` renders `ColumnChart`, `rolling-12` renders the same with a 12-period window, and `year-over-year` renders a side-by-side version. Add a true waterfall/bridge chart (SVG or CSS) for the `waterfall-bridge` variant using `varianceAmount` data already on `SlideDto`. Add a chart-type selector to `BlockInspector` (App.tsx:2851) so users can switch types without removing and re-adding the block.

2. **[Major] Connect font family and cover style to rendered output** — In App.tsx apply `theme.fontFamily` to `--font-family` CSS variable at the package shell level (App.tsx:952–953 block) and update App.css `:root` to use `font-family: var(--font-family, Inter)`. Add conditional CSS classes to `.cover-preview` based on `coverStyle` (e.g., `cover-preview--executive`, `cover-preview--classic`) with distinct layout, typography scale, and accent treatment in App.css. Wire the logo upload `<Button>` to a real `<input type="file">` posting to `/api/packages/{id}/logo`, and replace the `cover-mark` abbreviation square with an `<img>` when `logoFileName` resolves.

3. **[Major] Make `LayoutsView` interactive and make `apply-template` produce real blocks** — Add drag-to-reorder in `LayoutsView` that calls `PUT /api/packages/{id}/theme` with the updated `pageOrder` array (the endpoint already accepts it, App.tsx:3962). In `BuildReportingStudioSlide` / template apply (Program.cs:3120–3124), populate `ChartConfigJson` with a meaningful type derived from the section name (e.g., "Waterfall Bridge" → `{ type: "waterfall" }`) and emit a real `chart` block alongside the text block so the slide canvas is non-empty when a template is first applied.
