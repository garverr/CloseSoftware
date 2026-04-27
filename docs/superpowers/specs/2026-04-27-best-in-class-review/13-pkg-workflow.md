# AI Board Package Workflow Audit — Agent 13

**Categories:** 25 (Human-in-the-loop editing of AI suggestions), 26 (Versioning & approval workflow — CFO sign-off → Board)

**Pillar:** AI Board Package

---

## Category 25 — Human-in-the-loop Editing of AI Suggestions

### What Exists

| Feature | Status | Evidence |
|---|---|---|
| AI draft suggestions (staged, accept/reject per-item) | Partial | `Program.cs:2104-2138`; `App.tsx:2294-2311` |
| Issue workbench: apply-fix or ignore per issue | Partial | `Program.cs:509-586`; `App.tsx:1746-1786` |
| Block-level inline editing (SlideEditor, PUT /api/blocks/{id}) | Partial | `Program.cs:711-736`; `App.tsx:2094` |
| Comment on slides and blocks (PackageComment entity) | Partial | `Entities.cs:229-244`; `Program.cs:2401-2473` |
| Reject reason captured on draft reject | Minimal | `App.tsx:2307` — hardcoded string, not user-entered |
| AI provenance on SlideBlock (which AI run generated this text) | Missing | `Entities.cs:121-129` — no AiRunId, no generatedAt, no originatingModule |
| Sentence-level editing / partial accept of AI suggestion | Missing | Accept operates on full AiPackageDraftSuggestion, no sub-suggestion editing |
| Inline text diff (before/after view before accepting) | Missing | No diff surface in AiDraftPanel; `App.tsx:2328-2340` shows only title + description |
| CFO can add custom free-text reason when accepting a fix | Missing | `App.tsx:1747-1749` — reason is hardcoded "Approved from issue workbench" |
| Confidence score surfaced for draft suggestions | Missing | `AiPackageDraftSuggestion` entity has no Confidence field; IssueDto has it but AiDraftPanel does not display it |

### Gaps (Category 25)

- **BLOCKER — No provenance metadata on SlideBlock.** After an AI suggestion is accepted and written to a `SlideBlock` (via `ApplyOperationAsync` at `Program.cs:3471-3497`), the block carries no `AiRunId`, no `GeneratedAt`, no `IsAiAuthored` flag. A CFO cannot tell which text was AI-generated versus human-written.
- **Major — Reject reason is hardcoded, not user-entered.** `App.tsx:2307` passes `'Rejected from staged draft review'` — the CFO cannot explain why they rejected a suggestion. The `RejectDraftRequest` record (`Program.cs:4827`) accepts a `string? Reason` but the UI ignores it.
- **Major — No inline diff view.** The AiDraftPanel (`App.tsx:2314-2343`) shows only title and description of the suggestion, not a before/after comparison of what the slide content will look like. A CFO is accepting blind.
- **Minor — Sentence-granularity.** AI suggestions are accepted at the full `AiPackageDraftSuggestion` level. There is no mechanism to accept part of an AI narrative block.

---

## Category 26 — Versioning & Approval Workflow (CFO Sign-off → Board)

### What Exists

| Feature | Status | Evidence |
|---|---|---|
| PackageVersion snapshots (SnapshotJson) created on every edit | Implemented | `Entities.cs:277-286`; `Program.cs:3688-3698` |
| Version list endpoint (GET /api/packages/{id}/versions) | Implemented | `Program.cs:589-596` |
| Version restore endpoint | Implemented | `Program.cs:599-634` |
| AuditRecord with Actor, Role, Action, Before/After JSON | Implemented | `Entities.cs:778-791`; `Program.cs:3642-3673` |
| GET /api/audit filterable by packageId | Implemented | `Program.cs:3332-3342` |
| PackageStatus enum (Draft, Review, Syncing, Blocked, Final) | Exists | `Entities.cs:3-10` |
| Formal CFO-approval transition endpoint (e.g. POST /approve-package) | Missing | No such endpoint in `Program.cs` |
| ApprovedBy / ApprovedAt fields on ReportPackage or PackageVersion | Missing | `Entities.cs:79-100` — absent |
| Board-distribution gated on CFO approval | Missing | `DistributionSchedule` (`Entities.cs:763-776`) has no approval prerequisite check |
| Immutability protection after CFO approval (prevent further edits) | Missing | No IsLocked / LockedAt on ReportPackage or PackageVersion |
| PackageVersion marked as "CFO-Approved" snapshot | Missing | PackageVersion has no ApprovalStatus or IsApproved field |
| Actor identity via authenticated principal (JWT/session) | Missing | `Program.cs:3642-3643` — Actor reads `X-FR-User` header, trivially spoofable; no real auth |
| Audit trail surfaced in UI (audit log screen) | Missing | GET /api/audit exists but no UI component consumes it in App.tsx |

### Gaps (Category 26)

- **BLOCKER — No formal CFO approval gate.** `PackageStatus.Final` exists in the enum (`Entities.cs:9`) but there is no endpoint to transition to it with an ApprovedBy/ApprovedAt stamp. A Board package can be distributed via `DistributionSchedule` regardless of package status — there is no enforcement preventing distribution of a Draft.
- **BLOCKER — Snapshots are not immutable post-approval.** `PackageVersion.SnapshotJson` is never write-protected. The restore endpoint (`Program.cs:619`) replaces live data from a snapshot, but nothing prevents overwriting a version that a CFO "signed off on." The Board may receive content that has since been mutated.
- **Major — Actor identity is not authenticated.** `Program.cs:3642-3643` derives the actor from the `X-FR-User` HTTP header with a fallback of `"dev-admin"`. Any caller can forge the actor name. Audit records are therefore not legally defensible.
- **Minor — Audit log has no UI.** The API endpoint exists (`Program.cs:3332`) but there is no view in `App.tsx` that renders audit history to a CFO.

---

## Pillar Verdict — AI Board Package

**Red.** The versioning infrastructure (snapshot-per-edit, audit records, restore) is well-built. However the two features that make a Board package trustworthy — a gated CFO approval that locks the approved snapshot and prevents further edits, and AI provenance on accepted suggestions — are entirely absent. A CFO cannot currently prove that the Board received exactly what they approved.

---

## Top 3 Fixes (Priority Order)

1. **Add CFO approval gate (Blocker / Cat 26).** Add `ApprovedBy`, `ApprovedAt`, `IsApproved` to `ReportPackage` (`Entities.cs:79`). Implement `POST /api/packages/{id}/approve` that stamps these fields, creates a `PackageVersion` tagged as the approved snapshot, and sets `PackageStatus.Final`. Block `DistributionSchedule` send unless `IsApproved` is true.

2. **Add AI provenance to SlideBlock (Blocker / Cat 25).** Add `IsAiAuthored`, `OriginatingAiRunId`, `AiAuthoredAt` to the `SlideBlock` entity (`Entities.cs:121`). Set these in `ApplyOperationAsync` (`Program.cs:3476-3497`) when writing AI-generated content. Surface them in the slide editor tooltip ("AI suggested this on [date] via [module]").

3. **Require user-entered reject reason + show diff before accept (Major / Cat 25).** Wire the `Reason` field in `RejectDraftRequest` to a text input in `AiDraftPanel` (`App.tsx:2304-2311`). Add a before/after diff panel to the draft review modal so CFOs can see exactly what accepting will change in slide content before committing.
