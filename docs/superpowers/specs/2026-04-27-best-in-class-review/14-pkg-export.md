# Best-in-Class Audit — Agent 14: Export Fidelity & Re-Generation Cost

**Categories:** 27 (Export Fidelity — PDF / XLSX / PPTX-equivalent), 28 (Re-generation Cost, Idempotency, Deterministic Seeds)

**Pillar:** AI Board Package

---

## Category 27 — Export Fidelity (PDF / XLSX / PPTX-equivalent)

### Evidence

| Finding | File:Line | Severity |
|---|---|---|
| PDF is hand-rolled raw PDF 1.4 bytes via `StringBuilder` — no vector graphics engine, no font embedding beyond Helvetica Type1 stub, no headers/footers, no page breaks, hard-capped at 46 lines total | `FinancialServices.cs:1384–1418` | **Blocker** |
| PDF has a single page (`/Count 1`) regardless of slide count; `pageCount` in metadata is calculated but the document itself is always 1 page | `FinancialServices.cs:1217`, `1395–1397` | **Blocker** |
| No rendering of charts, tables, or KPI visuals — only plain-text `slide.Subject` + `slide.KpiLabel` + variance numerics; board charts are silently dropped | `FinancialServices.cs:1346–1382` | **Blocker** |
| XLSX is hand-rolled OOXML ZIP via `ZipArchive`; no `ClosedXML` / `EPPlus` / `DocumentFormat.OpenXml`; zero cell styles, no header freeze, no column widths, no number formatting cells (amounts emitted as `inlineStr` strings, not numeric cells with format codes) | `FinancialServices.cs:1436–1519` | **Major** |
| XLSX emits amounts as string type (`t="inlineStr"`) — Excel cannot sum, sort, or chart them; this breaks any downstream formula work | `FinancialServices.cs:1499–1500` | **Major** |
| No PPTX / presentation export format exists; the slide-object model in `PackageSlide` / `SlideBlock` is complete but never rendered to a shareable deck format | `Program.cs:4702–4703` (template definitions list slides; no `/api/exports/pptx` route anywhere in 5 118 lines) | **Major** |
| No NuGet reference to any PDF or XLSX library (QuestPDF, ClosedXML, iText, Aspose, Syncfusion, FastReport, Telerik, NPOI) | `FinancialReporting.Api.csproj:1–18` | **Blocker** |
| QA check (`BuildExportQaAsync`) only verifies file exists and byte size > 128; does not check page count, chart presence, font embedding, or content fidelity | `FinancialServices.cs:1309–1325` | **Major** |
| Branding inputs (`FontFamily`, `HeaderText`, `FooterText`, `LogoFileName`, `CoverStyle`, `PageOrder`) are stored in `ThemeJson` and wired through `UpdatePackageThemeRequest` but are completely ignored by `BuildSimplePdf` and `WriteXlsxAsync` | `Program.cs:4844`; `FinancialServices.cs:1199–1220` | **Blocker** |

### Summary

The PDF output is a proof-of-concept stub that writes a fixed-font, single-page ASCII text dump. It is not board-ready by any definition: no branding, no vector charts, no page breaks, no headers/footers, no font embedding beyond a Helvetica stub, and a 46-line hard ceiling that will silently truncate any real package. The XLSX is similarly skeletal — valid ZIP structure but no styling, numeric types, freeze panes, or formulas. PPTX does not exist. Fathom, Syft, and even basic browser-print-to-PDF outputs would be meaningfully superior.

---

## Category 28 — Re-generation Cost, Idempotency, Deterministic Seeds

### Evidence

| Finding | File:Line | Severity |
|---|---|---|
| Every call to `POST /api/exports/pdf` or `/api/exports/excel` unconditionally generates a new artifact with a new GUID — no cache lookup by `(packageId, includeIssues, includeAppendix)` key, no content hash, no idempotency token | `Program.cs:2143–2178`; `FinancialServices.cs:1199–1307` | **Major** |
| No `InputHash` or `ContentFingerprint` field on `ExportArtifact` entity; CFO re-run produces a new file with a new ID even when no package data changed | `FinancialServices.cs:1208–1219` (artifact construction fields) | **Major** |
| AI narrative seeds (`BuildPackageNarrative`) are deterministic given same inputs (pure string interpolation) — correct behavior — but AI runs via Codex CLI carry no `seed` / `temperature=0` flag; output can vary between runs for the same input JSON | `FinancialServices.cs:2537–2544`; `FinancialServices.cs:897–954` (no seed arg in `CodexExecutionRequest`) | **Major** |
| No deduplication check before queuing an `AiRun`: duplicate `POST /api/ai/runs` with identical `InputJson` / `Module` creates a new row and triggers a new Codex CLI process | `Program.cs:847–868` | **Major** |
| Export filenames are deterministic (`{slug}-{period}-board-package.pdf`), meaning a second export silently overwrites the first file on disk; a requester holding the prior download URL now gets corrupted data until their request completes | `FinancialServices.cs:1203–1206` | **Major** |
| `BuildExportQaAsync` does not record a content hash of the output file, so there is no mechanism to detect whether two artifacts are identical | `FinancialServices.cs:1309–1325` | **Minor** |
| `explanationSeed` (slide-level narrative placeholder text) is deterministic by design — correct | `FinancialServices.cs:2449`, `2537–2544` | Pass |

### Summary

Re-generation is cheap in the wrong way: the system re-runs unconditionally on every request without checking whether inputs changed, writes over the previous file, and produces a new `ExportArtifact` row per call. There is no caching layer, no content fingerprint, and no idempotency key. The Codex CLI path carries no seed, so AI narrative can drift between runs. A CFO regenerating a package they already approved may receive a slightly different narrative and a file that silently replaced the one they had reviewed.

---

## Pillar Verdict — AI Board Package (Export Pillar)

The Board Package export pillar is **not production-ready**. Both the PDF and XLSX artifacts are hand-rolled stubs that do not deliver board-grade output: PDF is a single-page, 46-line ASCII text dump; XLSX stores all amounts as strings with no styling; PPTX does not exist. The full branding system (logo, fonts, headers, footers, page order) is stored and modelled but completely disconnected from the rendering layer. Re-generation has no cache or idempotency mechanism. These are pre-revenue blockers, not polish items.

---

## Top 3 Fixes

1. **[Blocker] Replace `BuildSimplePdf` with a real PDF library.** Adopt QuestPDF (MIT) or iText (AGPL/commercial). Render one page per `PackageSlide`, embed charts as SVG/vector, apply `ThemeJson` branding (logo, fonts, header/footer), add page numbers, and honour `LandscapeForWideTables`. This is the single highest-impact change — nothing downstream of PDF export is credible without it. (`FinancialServices.cs:1384–1418`)

2. **[Major] Replace `WriteXlsxAsync` with ClosedXML or EPPlus; add PPTX export.** Emit numeric cells with Excel format codes (not `inlineStr`), freeze header rows, auto-size columns, and add an `=SUM()` totals row per section. Separately, add `POST /api/exports/pptx` that renders `PackageSlide` objects into OpenXML PresentationML — this is the distribution format boards actually use. (`FinancialServices.cs:1436–1519`)

3. **[Major] Add export idempotency and AI determinism.** Compute a `SHA-256` hash of `(packageId, versionLabel, includeIssues, includeAppendix)` before generating; return the existing `ExportArtifact` if the hash matches. Store `InputHash` on the entity. Pass a fixed `--seed` / `temperature=0` flag to the Codex CLI invocation so re-runs on unchanged inputs produce identical narrative. (`Program.cs:2143–2178`; `FinancialServices.cs:897–954`)
