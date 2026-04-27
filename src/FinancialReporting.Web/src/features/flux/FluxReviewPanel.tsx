/**
 * FluxReviewPanel — extracted from App.tsx.
 *
 * TODO: Once the App.tsx type registry is extracted into a shared types module,
 * remove the inline type re-declarations below (marked with TODO-DEDUPE) and
 * import them from that shared location.
 */

import { useCallback, useEffect, useState } from 'react'
import { API_BASE, fetchJson, postJson, putJson } from '../../api/client'
import { Button, Card, SegmentButton, SeverityBadge, Sparkline } from '../../components/primitives'
import {
  Check,
  CheckCircle2,
  Database,
  Download,
  RefreshCw,
  Sparkles,
} from 'lucide-react'

// ---------------------------------------------------------------------------
// TODO-DEDUPE: Types below are re-declared from App.tsx pending a shared types
// extraction. When `src/types/index.ts` (or equivalent) is created, import
// from there and delete these declarations.
// ---------------------------------------------------------------------------

/** TODO-DEDUPE: matches PackageDto in App.tsx */
type PackageDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  organizationKey: string
  organizationName: string
  organizationAbbreviation: string
  periodKey: string
  period: string
  status: string
  versionLabel: string
  baseFrom: string
  lastXeroSyncAt: string | null
  isSourceDataStale: boolean
  sourceDataStaleReason: string | null
  sourceDataChangedAt: string | null
  blockReason: string | null
  themeJson: string
  slides: SlideDto[]
  issues: IssueDto[]
}

/** TODO-DEDUPE: minimal SlideDto shape required by PackageDto */
type SlideDto = {
  id: string
  sortOrder: number
  subject: string
  kpiLabel: string
  currentValue: number
  priorValue: number
  varianceAmount: number
  variancePercent: number
  accountCodesCsv: string
  monthlyJson: string
  priorMonthlyJson: string
  chartConfigJson: string
  blocks: SlideBlockDto[]
}

/** TODO-DEDUPE: minimal SlideBlockDto shape required by SlideDto */
type SlideBlockDto = {
  id: string
  sortOrder: number
  kind: string
  contentJson: string
}

/** TODO-DEDUPE: minimal IssueDto shape required by PackageDto */
type IssueDto = {
  id: string
  packageSlideId: string | null
  severity: string
  status: string
  category: string
  title: string
  description: string
  evidenceJson: string
  recommendedFixJson: string
  confidence: number
}

/** TODO-DEDUPE: matches AiRun in App.tsx */
type AiRun = {
  id: string
  reportPackageId: string | null
  module: string
  model: string
  reasoningEffort: string
  status: string
  progress: number
  outputJson: string
  logs: string
  createdAt: string
  completedAt: string | null
}

/** TODO-DEDUPE: matches FluxReview in App.tsx */
type FluxReview = {
  reportPackageId: string
  isSourceDataStale: boolean
  sourceDataStaleReason: string | null
  progress: FluxReviewProgress
  groups: FluxReviewGroup[]
}

/** TODO-DEDUPE: matches FluxReviewProgress in App.tsx */
type FluxReviewProgress = {
  totalGroups: number
  requiredExplanations: number
  openExplanations: number
  autoSignedOff: number
  prepared: number
  reviewed: number
}

/** TODO-DEDUPE: matches FluxReviewGroup in App.tsx */
type FluxReviewGroup = {
  id: string
  fluxType: string
  statementType: string
  groupName: string
  currentPeriodKey: string
  priorPeriodKey: string
  currentAmount: number
  priorAmount: number
  runningThreeMonthAmount: number
  varianceAmount: number
  variancePercent: number
  dollarThreshold: number
  percentThreshold: number
  thresholdLogic: string
  requiresExplanation: boolean
  requiresLedgerDetail: boolean
  ledgerDetailStatus: string
  ledgerDetailPulledAt: string | null
  status: string
  assignee: string
  reviewer: string
  dueDate: string | null
  explanationTemplate: string
  priorExplanation: string
  tags: string
  trendJson: string
  driverSummaryJson: string
  riskFlagsJson: string
  autoSignedOff: boolean
  explanation: string
  preparedBy: string
  preparedAt: string | null
  reviewedBy: string
  reviewedAt: string | null
  evidenceJson: string
}

/** TODO-DEDUPE: matches FluxReviewDrilldown in App.tsx */
type FluxReviewDrilldown = {
  groupId: string
  fluxType: string
  statementType: string
  groupName: string
  currentPeriodKey: string
  priorPeriodKey: string
  currentAmount: number
  priorAmount: number
  runningThreeMonthAmount: number
  varianceAmount: number
  variancePercent: number
  accounts: FluxReviewAccount[]
}

/** TODO-DEDUPE: matches FluxReviewAccount in App.tsx */
type FluxReviewAccount = {
  accountCode: string
  accountName: string
  accountType: string
  fsLine: string
  currentAmount: number
  priorAmount: number
  varianceAmount: number
  variancePercent: number
  currentTransactions: FluxLedgerTransaction[]
  priorTransactions: FluxLedgerTransaction[]
}

/** TODO-DEDUPE: matches FluxLedgerTransaction in App.tsx */
type FluxLedgerTransaction = {
  date: string
  journalNumber: number
  sourceType: string
  reference: string
  description: string
  netAmount: number
  grossAmount: number
  taxAmount: number
}

// ---------------------------------------------------------------------------
// Local helpers (also live in App.tsx — will dedupe once helpers are extracted)
// ---------------------------------------------------------------------------

function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

function formatStatementType(value: string) {
  if (value === 'BalanceSheet') return 'Balance sheet'
  if (value === 'TrialBalance') return 'Trial balance'
  return 'Income statement'
}

function statementLabel(value: string) {
  if (value === 'ProfitAndLoss') return 'P&L'
  return formatStatementType(value)
}

function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

// ---------------------------------------------------------------------------
// TransactionTable — sub-component, only used by FluxReviewPanel
// ---------------------------------------------------------------------------

function TransactionTable({ title, rows }: { title: string; rows: FluxLedgerTransaction[] }) {
  // P2.25 — replace the silent 50-row hard cap with a description filter and an explicit
  // show-all toggle. Adds a Xero deep-link icon when the row carries a SourceID. Cat 11.
  const [filter, setFilter] = useState('')
  const [showAll, setShowAll] = useState(false)
  const filtered = filter.trim().length === 0
    ? rows
    : rows.filter((row) => {
        const needle = filter.toLowerCase()
        return (row.description || '').toLowerCase().includes(needle)
          || (row.reference || '').toLowerCase().includes(needle)
          || (row.sourceType || '').toLowerCase().includes(needle)
          || String(row.journalNumber || '').includes(needle)
      })
  const visible = showAll ? filtered : filtered.slice(0, 50)
  const hidden = filtered.length - visible.length

  return (
    <div className="transaction-detail-table">
      <div className="table-header">
        <span>{title}</span>
        <strong>{filtered.length}{filter.trim().length > 0 && rows.length !== filtered.length ? ` of ${rows.length}` : ''}</strong>
      </div>
      <div className="table-toolbar">
        <input
          type="search"
          placeholder="Filter by description, reference, source, or journal #"
          value={filter}
          onChange={(event) => setFilter(event.target.value)}
          aria-label="Filter GL rows"
        />
      </div>
      {filtered.length === 0 ? (
        <div className="empty-state compact">
          {rows.length === 0 ? 'No journal lines loaded for this account and period.' : 'No rows match the filter.'}
        </div>
      ) : (
        <>
          <table className="mini-table full">
            <thead>
              <tr><th>Date</th><th>Journal</th><th>Description</th><th>Source</th><th>Amount</th><th aria-label="Open in Xero" /></tr>
            </thead>
            <tbody>
              {visible.map((row, index) => {
                // P2.25 — deep-link to original Xero document when a SourceID is present.
                const sourceId = (row as { sourceId?: string }).sourceId ?? ''
                const xeroUrl = sourceId ? `https://go.xero.com/Reports/JournalReport.aspx?journalNumber=${row.journalNumber}` : ''
                return (
                  <tr key={`${row.journalNumber}-${index}`}>
                    <td>{row.date}</td>
                    <td>{row.journalNumber}</td>
                    <td>{row.description || row.reference}</td>
                    <td>{row.sourceType}</td>
                    <td>{fmtMoney(row.netAmount)}</td>
                    <td>
                      {xeroUrl && (
                        <a href={xeroUrl} target="_blank" rel="noopener noreferrer" aria-label={`Open journal ${row.journalNumber} in Xero`}>↗</a>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {hidden > 0 && (
            <button className="link-button" type="button" onClick={() => setShowAll(true)}>
              Show {hidden} more row{hidden === 1 ? '' : 's'}
            </button>
          )}
        </>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// FluxReviewPanel — named export
// ---------------------------------------------------------------------------

export function FluxReviewPanel({
  packageData,
  refreshPackages,
  standalone = false,
  onAiRunQueued,
}: {
  packageData: PackageDto
  refreshPackages: () => Promise<void>
  standalone?: boolean
  onAiRunQueued?: (run: AiRun) => void
}) {
  const [review, setReview] = useState<FluxReview | null>(null)
  const [busy, setBusy] = useState(false)
  const [editing, setEditing] = useState<string | null>(null)
  const [explanation, setExplanation] = useState('')
  const [fluxType, setFluxType] = useState<'MonthOverMonth' | 'YearOverYear'>('MonthOverMonth')
  const [statementType, setStatementType] = useState<'ProfitAndLoss' | 'BalanceSheet'>('ProfitAndLoss')
  const [selectedGroupId, setSelectedGroupId] = useState('')
  const [drilldown, setDrilldown] = useState<FluxReviewDrilldown | null>(null)
  const [selectedAccountCode, setSelectedAccountCode] = useState('')
  const [aiRun, setAiRun] = useState<AiRun | null>(null)
  const [aiSuggestion, setAiSuggestion] = useState('')
  const [statusFilter, setStatusFilter] = useState<'all' | 'required' | 'open' | 'prepared' | 'reviewed' | 'assigned' | 'late' | 'ungrouped'>('required')
  const [search, setSearch] = useState('')
  const [settingsForm, setSettingsForm] = useState({
    dollarThreshold: '0',
    percentThreshold: '10',
    thresholdLogic: 'OR',
    assignee: '',
    reviewer: '',
    dueDate: '',
    tags: '',
    applyScope: 'period',
    explanationTemplate: '',
  })

  const load = useCallback(async () => {
    const next = await fetchJson<FluxReview>(`/api/packages/${packageData.id}/flux-review`)
    setReview(next)
  }, [packageData.id])

  useEffect(() => {
    let active = true
    fetchJson<FluxReview>(`/api/packages/${packageData.id}/flux-review`)
      .then((next) => {
        if (active) setReview(next)
      })
      .catch(() => {
        if (active) setReview(null)
      })
    return () => {
      active = false
    }
  }, [packageData.id])

  const refreshFlux = async () => {
    setBusy(true)
    try {
      const next = await postJson<FluxReview>(`/api/packages/${packageData.id}/refresh-flux`, {})
      setReview(next)
      await refreshPackages()
    } finally {
      setBusy(false)
    }
  }

  const pullLedgerDetail = async () => {
    setBusy(true)
    try {
      const next = await postJson<FluxReview>(`/api/packages/${packageData.id}/pull-ledger-detail`, {})
      setReview(next)
      await refreshPackages()
    } finally {
      setBusy(false)
    }
  }

  const saveExplanation = async (groupId: string) => {
    await putJson(`/api/flux-review/groups/${groupId}/explanation`, {
      explanation,
      reason: 'Flux review explanation',
    })
    setEditing(null)
    setExplanation('')
    await load()
  }

  const approve = async (groupId: string) => {
    await postJson(`/api/flux-review/groups/${groupId}/approve`, {})
    await load()
  }

  const signOff = async (groupId: string, action: 'prepare' | 'review') => {
    await postJson(`/api/flux-review/groups/${groupId}/sign-off`, {
      action,
      reason: action === 'review' ? 'Flux group reviewed' : 'Flux group prepared',
    })
    await load()
  }

  const saveSettings = async () => {
    if (!selectedGroup) return
    await putJson(`/api/flux-review/groups/${selectedGroup.id}/settings`, {
      dollarThreshold: Number(settingsForm.dollarThreshold || 0),
      percentThreshold: Number(settingsForm.percentThreshold || 0),
      thresholdLogic: settingsForm.thresholdLogic,
      assignee: settingsForm.assignee,
      reviewer: settingsForm.reviewer,
      dueDate: settingsForm.dueDate || null,
      explanationTemplate: settingsForm.explanationTemplate,
      tags: settingsForm.tags,
      applyScope: settingsForm.applyScope,
      reason: 'Updated flux workflow settings',
    })
    await load()
  }

  const rollForwardExplanation = async () => {
    if (!selectedGroup) return
    await postJson(`/api/flux-review/groups/${selectedGroup.id}/roll-forward-explanation`, {})
    await load()
    await loadDrilldown(selectedGroup.id)
  }

  const exportFluxCsv = () => {
    window.open(`${API_BASE}/api/packages/${packageData.id}/flux-review/export.csv`, '_blank', 'noopener,noreferrer')
  }

  const loadDrilldown = useCallback(async (groupId: string) => {
    setDrilldown(null)
    setSelectedAccountCode('')
    const next = await fetchJson<FluxReviewDrilldown>(`/api/flux-review/groups/${groupId}/drilldown`)
    setDrilldown(next)
    setSelectedAccountCode(next.accounts[0]?.accountCode ?? '')
  }, [])

  const queueAiExplanation = async () => {
    if (!selectedGroupId) return
    setBusy(true)
    setAiSuggestion('')
    try {
      const run = await postJson<AiRun>(`/api/flux-review/groups/${selectedGroupId}/ai-explain`, {})
      setAiRun(run)
      onAiRunQueued?.(run)
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    if (!aiRun || !['Queued', 'Running'].includes(aiRun.status)) return
    const interval = window.setInterval(async () => {
      const next = await fetchJson<AiRun>(`/api/ai/runs/${aiRun.id}`)
      setAiRun(next)
      if (next.status === 'Completed') {
        const parsed = parseJson<{ suggestedExplanation?: string }>(next.outputJson, {})
        setAiSuggestion(parsed.suggestedExplanation ?? '')
      }
    }, 1600)
    return () => window.clearInterval(interval)
  }, [aiRun])

  const useAiSuggestion = async () => {
    if (!selectedGroupId || !aiSuggestion) return
    await putJson(`/api/flux-review/groups/${selectedGroupId}/explanation`, {
      explanation: aiSuggestion,
      reason: 'Accepted AI variance explanation',
    })
    setAiSuggestion('')
    setEditing(null)
    setExplanation('')
    await load()
    await refreshPackages()
  }

  const allGroups = (review?.groups ?? []).filter((group) => group.fluxType === fluxType && group.statementType === statementType)
  const normalizedSearch = search.trim().toLowerCase()
  const groups = allGroups.filter((group) => {
    const matchesSearch = !normalizedSearch ||
      group.groupName.toLowerCase().includes(normalizedSearch) ||
      group.status.toLowerCase().includes(normalizedSearch) ||
      group.assignee.toLowerCase().includes(normalizedSearch) ||
      group.reviewer.toLowerCase().includes(normalizedSearch) ||
      group.tags.toLowerCase().includes(normalizedSearch)
    const isLate = !!group.dueDate && new Date(`${group.dueDate}T23:59:59`).getTime() < Date.now() && group.status !== 'Approved'
    const matchesStatus =
      statusFilter === 'all' ||
      (statusFilter === 'required' && group.requiresExplanation) ||
      (statusFilter === 'open' && group.requiresExplanation && !group.explanation && group.status !== 'Approved') ||
      (statusFilter === 'prepared' && (group.status === 'Prepared' || !!group.preparedBy)) ||
      (statusFilter === 'reviewed' && (group.status === 'Approved' || !!group.reviewedBy)) ||
      (statusFilter === 'assigned' && (!!group.assignee || !!group.reviewer)) ||
      (statusFilter === 'late' && isLate) ||
      (statusFilter === 'ungrouped' && group.groupName.toLowerCase().includes('ungrouped'))
    return matchesSearch && matchesStatus
  })
  const required = groups.filter((group) => group.requiresExplanation)
  const openRequired = required.filter((group) => !group.explanation && group.status !== 'Approved')
  const ledgerNeeded = groups.filter((group) => group.requiresLedgerDetail && !['Available', 'Pulled'].includes(group.ledgerDetailStatus)).length
  const selectedGroup = groups.find((group) => group.id === selectedGroupId) ?? groups[0]
  const selectedAccount = drilldown?.accounts.find((account) => account.accountCode === selectedAccountCode) ?? drilldown?.accounts[0]
  const selectedRiskFlags = parseJson<string[]>(selectedGroup?.riskFlagsJson ?? '[]', [])
  const selectedTrend = parseJson<Array<{ periodKey: string; amount: number }>>(selectedGroup?.trendJson ?? '[]', [])

  useEffect(() => {
    if (!selectedGroup) {
      setSelectedGroupId('')
      setDrilldown(null)
      return
    }
    if (selectedGroup.id !== selectedGroupId) {
      setSelectedGroupId(selectedGroup.id)
      loadDrilldown(selectedGroup.id).catch(() => setDrilldown(null))
    }
  }, [selectedGroup, selectedGroupId, loadDrilldown])

  useEffect(() => {
    if (!selectedGroup) return
    setSettingsForm({
      dollarThreshold: String(selectedGroup.dollarThreshold ?? 0),
      percentThreshold: String(selectedGroup.percentThreshold ?? 10),
      thresholdLogic: selectedGroup.thresholdLogic || 'OR',
      assignee: selectedGroup.assignee ?? '',
      reviewer: selectedGroup.reviewer ?? '',
      dueDate: selectedGroup.dueDate ?? '',
      tags: selectedGroup.tags ?? '',
      applyScope: 'period',
      explanationTemplate: selectedGroup.explanationTemplate ?? '',
    })
  }, [selectedGroup?.id])

  return (
    <section className="workbench">
      <div className="section-title tight">
        <div>
          <h2>Flux review</h2>
          <span className="muted">{openRequired.length} required explanations open · {allGroups.length} {statementLabel(statementType)} groups · thresholds can be tuned by FS line</span>
        </div>
        <div className="button-row">
          <Button variant="ghost" icon={<Download size={15} />} onClick={exportFluxCsv}>Export CSV</Button>
          <Button variant="secondary" icon={<RefreshCw size={15} />} disabled={busy} onClick={refreshFlux}>Refresh flux</Button>
          <Button variant="secondary" icon={<Database size={15} />} disabled={busy} onClick={pullLedgerDetail}>Pull ledger detail</Button>
        </div>
      </div>
      {review?.isSourceDataStale && <div className="alert-strip warn compact">{review.sourceDataStaleReason || 'Source data changed since this flux was built.'}</div>}
      {review?.progress && (
        <div className="flux-progress-grid">
          <div><span>Required</span><strong>{review.progress.requiredExplanations}</strong></div>
          <div><span>Open</span><strong>{review.progress.openExplanations}</strong></div>
          <div><span>Auto signed-off</span><strong>{review.progress.autoSignedOff}</strong></div>
          <div><span>Prepared</span><strong>{review.progress.prepared}</strong></div>
          <div><span>Reviewed</span><strong>{review.progress.reviewed}</strong></div>
        </div>
      )}
      <div className="segmented compact">
        <SegmentButton active={fluxType === 'MonthOverMonth'} onClick={() => setFluxType('MonthOverMonth')}>Current month vs prior month</SegmentButton>
        <SegmentButton active={fluxType === 'YearOverYear'} onClick={() => setFluxType('YearOverYear')}>Current year vs prior year</SegmentButton>
      </div>
      <div className="segmented compact">
        <SegmentButton active={statementType === 'ProfitAndLoss'} onClick={() => setStatementType('ProfitAndLoss')}>P&amp;L flux</SegmentButton>
        <SegmentButton active={statementType === 'BalanceSheet'} onClick={() => setStatementType('BalanceSheet')}>Balance sheet flux</SegmentButton>
      </div>
      <div className="flux-toolbar">
        <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search groups, assignee, reviewer..." />
        <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
          <option value="required">Requires explanation</option>
          <option value="open">Open explanations</option>
          <option value="prepared">Prepared</option>
          <option value="reviewed">Reviewed</option>
          <option value="assigned">Assigned</option>
          <option value="late">Late</option>
          <option value="ungrouped">Ungrouped accounts</option>
          <option value="all">All groups</option>
        </select>
      </div>
      {ledgerNeeded > 0 && <div className="alert-strip warn compact">{ledgerNeeded} material groups need ledger detail. The app will pull detail automatically when TB/source data changed; use the button for an on-demand pull.</div>}
      {groups.length === 0 ? (
        <div className="empty-state">No {statementLabel(statementType)} groups match this filter. Sync Xero, refresh flux, or widen the filter.</div>
      ) : (
        <div className="flux-review-grid">
          <div className="issue-list">
            {groups.slice(0, standalone ? 80 : 8).map((group) => (
              <div className={`issue-row flux-row ${selectedGroup?.id === group.id ? 'selected' : ''}`} key={group.id} onClick={() => { setSelectedGroupId(group.id); loadDrilldown(group.id).catch(() => setDrilldown(null)) }}>
                <SeverityBadge severity={group.requiresExplanation ? 'Medium' : 'Low'} />
                <div className="issue-body">
                  <strong>{group.groupName}</strong>
                  <p>
                    Prior {fmtMoney(group.priorAmount)} · Current {fmtMoney(group.currentAmount)} · Change {fmtMoney(group.varianceAmount)} · {group.variancePercent.toFixed(1)}%
                    {group.fluxType === 'MonthOverMonth' ? ` · 3 mo ${fmtMoney(group.runningThreeMonthAmount)}` : ''}
                  </p>
                  <small>{group.currentPeriodKey} vs {group.priorPeriodKey} · {statementLabel(group.statementType)} · Ledger {group.ledgerDetailStatus}</small>
                  <div className="flux-row-meta">
                    <span className={`status-pill ${group.status === 'Approved' ? 'good' : group.requiresExplanation ? 'warn' : ''}`}>{group.status}</span>
                    {group.assignee && <span>Owner {group.assignee}</span>}
                    {group.dueDate && <span>Due {group.dueDate}</span>}
                    {group.tags && <span>{group.tags}</span>}
                    {group.autoSignedOff && <span>Auto signed-off</span>}
                  </div>
                  {editing === group.id ? (
                    <div className="inline-editor" onClick={(event) => event.stopPropagation()}>
                      <textarea value={explanation} onChange={(event) => setExplanation(event.target.value)} placeholder="Explain the variance using GL detail..." />
                      <Button variant="primary" onClick={() => saveExplanation(group.id)}>Save</Button>
                    </div>
                  ) : (
                    <small>{group.explanation || group.status}</small>
                  )}
                </div>
                <Button variant="ghost" onClick={(event) => { event.stopPropagation(); setEditing(group.id); setExplanation(group.explanation) }}>Explain</Button>
                <Button variant="secondary" onClick={(event) => { event.stopPropagation(); approve(group.id) }}>Review</Button>
              </div>
            ))}
          </div>
          <Card className="flux-detail-card">
            {!drilldown ? (
              <div className="empty-state">Select a flux line to see accounts and ledger detail.</div>
            ) : (
              <>
                <div className="section-title tight">
                  <div>
                    <div className="eyebrow">Flux drilldown</div>
                    <h3>{drilldown.groupName}</h3>
                    <span className="muted">{drilldown.currentPeriodKey} vs {drilldown.priorPeriodKey} · {drilldown.accounts.length} accounts</span>
                  </div>
                  <div className="button-row">
                    <Button variant="secondary" icon={<Check size={15} />} disabled={!selectedGroup} onClick={() => selectedGroup && signOff(selectedGroup.id, 'prepare')}>Prepare</Button>
                    <Button variant="secondary" icon={<CheckCircle2 size={15} />} disabled={!selectedGroup} onClick={() => selectedGroup && signOff(selectedGroup.id, 'review')}>Review</Button>
                    <Button variant="accent" icon={<Sparkles size={15} />} disabled={busy} onClick={queueAiExplanation}>AI analysis</Button>
                  </div>
                </div>
                {selectedGroup && (
                  <div className="flux-settings-panel">
                    <div>
                      <label>Dollar threshold</label>
                      <input value={settingsForm.dollarThreshold} onChange={(event) => setSettingsForm((current) => ({ ...current, dollarThreshold: event.target.value }))} />
                    </div>
                    <div>
                      <label>Percent threshold</label>
                      <input value={settingsForm.percentThreshold} onChange={(event) => setSettingsForm((current) => ({ ...current, percentThreshold: event.target.value }))} />
                    </div>
                    <div>
                      <label>Threshold logic</label>
                      <select value={settingsForm.thresholdLogic} onChange={(event) => setSettingsForm((current) => ({ ...current, thresholdLogic: event.target.value }))}>
                        <option value="OR">Amount or percent</option>
                        <option value="AND">Amount and percent</option>
                      </select>
                    </div>
                    <div>
                      <label>Assignee</label>
                      <input value={settingsForm.assignee} onChange={(event) => setSettingsForm((current) => ({ ...current, assignee: event.target.value }))} placeholder="Finance owner" />
                    </div>
                    <div>
                      <label>Reviewer</label>
                      <input value={settingsForm.reviewer} onChange={(event) => setSettingsForm((current) => ({ ...current, reviewer: event.target.value }))} placeholder="Reviewer" />
                    </div>
                    <div>
                      <label>Due date</label>
                      <input value={settingsForm.dueDate} onChange={(event) => setSettingsForm((current) => ({ ...current, dueDate: event.target.value }))} placeholder="YYYY-MM-DD" />
                    </div>
                    <div>
                      <label>Tags</label>
                      <input value={settingsForm.tags} onChange={(event) => setSettingsForm((current) => ({ ...current, tags: event.target.value }))} placeholder="close, audit, board" />
                    </div>
                    <div>
                      <label>Apply to</label>
                      <select value={settingsForm.applyScope} onChange={(event) => setSettingsForm((current) => ({ ...current, applyScope: event.target.value }))}>
                        <option value="period">This period only</option>
                        <option value="future">This period and going forward</option>
                      </select>
                    </div>
                    <Button variant="secondary" onClick={saveSettings}>Save settings</Button>
                    <label className="wide">Explanation template</label>
                    <textarea className="wide" value={settingsForm.explanationTemplate} onChange={(event) => setSettingsForm((current) => ({ ...current, explanationTemplate: event.target.value }))} />
                  </div>
                )}
                {selectedRiskFlags.length > 0 && (
                  <div className="flux-chip-row">
                    {selectedRiskFlags.map((flag) => <span className="status-pill warn" key={flag}>{flag}</span>)}
                  </div>
                )}
                {selectedTrend.length > 0 && (
                  <>
                    {/* P2.25 — Sparkline alongside the labelled values so reviewers can
                        spot trend direction at a glance instead of reading 6 numbers. Cat 16. */}
                    <div className="flux-trend-strip-chart">
                      <Sparkline current={selectedTrend.map((point) => point.amount)} />
                    </div>
                    <div className="flux-trend-strip">
                      {selectedTrend.map((point) => (
                        <div key={point.periodKey}>
                          <span>{point.periodKey}</span>
                          <strong>{fmtMoney(point.amount)}</strong>
                        </div>
                      ))}
                    </div>
                  </>
                )}
                {selectedGroup?.priorExplanation && (
                  <div className="ai-suggestion-box">
                    <div className="eyebrow">Prior explanation</div>
                    <p>{selectedGroup.priorExplanation}</p>
                    <Button variant="secondary" onClick={rollForwardExplanation}>Roll forward</Button>
                  </div>
                )}
                {drilldown.accounts.length === 0 ? (
                  <div className="empty-state">No accounts are attached to this flux line yet. Check mapping for ungrouped accounts.</div>
                ) : (
                  <div className="flux-account-list">
                    {drilldown.accounts.map((account) => (
                      <button className={`flux-account-row ${selectedAccount?.accountCode === account.accountCode ? 'selected' : ''}`} key={account.accountCode} onClick={() => setSelectedAccountCode(account.accountCode)}>
                        <span>
                          <strong>{account.accountCode} · {account.accountName}</strong>
                          <small>{account.fsLine || 'Ungrouped'} · {account.currentTransactions.length + account.priorTransactions.length} GL lines</small>
                        </span>
                        <span>{fmtMoney(account.varianceAmount)} · {account.variancePercent.toFixed(1)}%</span>
                      </button>
                    ))}
                  </div>
                )}
                {selectedAccount && (
                  <div className="ledger-detail">
                    <div className="ledger-detail-header">
                      <strong>{selectedAccount.accountCode} · {selectedAccount.accountName}</strong>
                      <span>Prior {fmtMoney(selectedAccount.priorAmount)} · Current {fmtMoney(selectedAccount.currentAmount)}</span>
                    </div>
                    <TransactionTable title={`Current period GL · ${drilldown.currentPeriodKey}`} rows={selectedAccount.currentTransactions} />
                    <TransactionTable title={`Prior period GL · ${drilldown.priorPeriodKey}`} rows={selectedAccount.priorTransactions} />
                  </div>
                )}
                {aiRun && (
                  <div className="ai-suggestion-box">
                    <div className="eyebrow">AI explanation</div>
                    <strong>{aiRun.status === 'Completed' ? 'Suggested explanation ready' : `${aiRun.status} · ${aiRun.progress}%`}</strong>
                    {aiSuggestion ? <p>{aiSuggestion}</p> : <p className="muted">Codex is reviewing the account and journal detail for this flux line.</p>}
                    {aiSuggestion && <Button variant="primary" onClick={useAiSuggestion}>Use explanation</Button>}
                  </div>
                )}
              </>
            )}
          </Card>
        </div>
      )}
    </section>
  )
}
