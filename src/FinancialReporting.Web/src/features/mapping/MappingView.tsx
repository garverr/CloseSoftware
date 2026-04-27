/**
 * MappingView — Account Mapping & Eliminations screen.
 *
 * Extracted from App.tsx (lines 3213–3568). AccountPanel is co-located here
 * because it is used exclusively by MappingView and is not referenced elsewhere.
 *
 * TODO: dedupe — the type aliases below are declared inline to keep this file
 * self-contained until a shared types module is introduced. When that module
 * lands, remove these declarations and import from there.
 */

import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, Check, PanelRightOpen, Plus, Sparkles, Wand2, X } from 'lucide-react'
import { Button, Card, SegmentButton, Sparkline } from '../../components/primitives'
import { deleteJson, fetchJson, postJson } from '../../api/client'

// ---------------------------------------------------------------------------
// Type aliases (TODO: dedupe with App.tsx once a shared types module exists)
// ---------------------------------------------------------------------------

// TODO: dedupe — AccountDto
type AccountDto = {
  id: string
  tenantId: string
  code: string
  name: string
  type: string
  fsLine: string
  aiSuggestedFsLine: string
  mappingConfidence: number
  isFirstSeen: boolean
  reviewStatus: string
  consolidationTreatment: string
  monthlyBalancesJson: string
  transactionCount: number
}

// TODO: dedupe — AccountDetailDto
type AccountDetailDto = {
  account: AccountDto
  transactions: Array<{
    id: string
    transactionDate: string
    description: string
    debit: number
    credit: number
    source: string
  }>
}

// TODO: dedupe — FsLineDefinitionDto
type FsLineDefinitionDto = {
  id: string
  organizationId: string
  statementType: string
  section: string
  name: string
  normalBalance: string
  aiGuidance: string
  sortOrder: number
  isActive: boolean
}

// TODO: dedupe — FinancialStatementGroupingResult
type FinancialStatementGroupingResult = {
  fsLinesCreated: number
  fsLinesReactivated: number
  accountsUpdated: number
  statementMatched: number
  fallbackMatched: number
  unmatched: number
}

// ---------------------------------------------------------------------------
// Private helpers (file-local, not exported)
// ---------------------------------------------------------------------------

function formatStatementType(value: string) {
  if (value === 'BalanceSheet') return 'Balance sheet'
  if (value === 'TrialBalance') return 'Trial balance'
  return 'Income statement'
}

function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

// ---------------------------------------------------------------------------
// SidePanel (file-local helper used by AccountPanel)
// ---------------------------------------------------------------------------

function SidePanel({
  title,
  subtitle,
  onClose,
  children,
}: {
  title: string
  subtitle: string
  onClose: () => void
  children: React.ReactNode
}) {
  // P3.37 — close on Escape so keyboard users can dismiss without locating the X button.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [onClose])

  return (
    <aside className="side-panel" role="dialog" aria-modal="true" aria-label={title}>
      <div className="panel-header">
        <div>
          <strong>{title}</strong>
          <span>{subtitle}</span>
        </div>
        <button onClick={onClose}>
          <X size={18} />
        </button>
      </div>
      <div className="panel-body">{children}</div>
    </aside>
  )
}

// ---------------------------------------------------------------------------
// AccountPanel — account drilldown side-panel (used only by MappingView)
// ---------------------------------------------------------------------------

export function AccountPanel({
  accountId,
  organizationKey,
  onClose,
  onChanged,
}: {
  accountId: string
  organizationKey: string
  onClose: () => void
  onChanged: () => void
}) {
  const [detail, setDetail] = useState<AccountDetailDto | null>(null)
  const [fsLines, setFsLines] = useState<FsLineDefinitionDto[]>([])
  const [loadFailed, setLoadFailed] = useState(false)
  const [reason, setReason] = useState('Reviewed from mapping inbox')
  const [fsLine, setFsLine] = useState('')

  useEffect(() => {
    Promise.all([
      fetchJson<AccountDetailDto>(`/api/mapping/accounts/${accountId}`),
      fetchJson<FsLineDefinitionDto[]>(`/api/mapping/fs-lines?organizationKey=${encodeURIComponent(organizationKey)}`),
    ])
      .then(([data, lines]) => {
        const active = lines.filter((line) => line.isActive)
        const preferredLine = data.account.aiSuggestedFsLine || data.account.fsLine
        setDetail(data)
        setFsLines(active)
        setFsLine(active.some((line) => line.name === preferredLine) ? preferredLine : (active[0]?.name ?? ''))
        setLoadFailed(false)
      })
      .catch(() => {
        setDetail(null)
        setFsLines([])
        setLoadFailed(true)
      })
  }, [accountId, organizationKey])

  const account = detail?.account

  const acceptMapping = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/map`, { fsLine, reason })
    onChanged()
    onClose()
  }

  const splitMapping = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/split`, {
      reason,
      lines: [
        { fsLine, percent: 70 },
        { fsLine: `${fsLine} — Other`, percent: 30 },
      ],
    })
    onChanged()
    onClose()
  }

  const markReviewed = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/mark-reviewed`, { reason })
    onChanged()
    onClose()
  }

  const rejectMapping = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/reject`, { reason })
    onChanged()
    onClose()
  }

  const eliminate = async (type: string) => {
    await postJson(`/api/mapping/accounts/${accountId}/eliminate`, {
      type,
      description: `${type} ${account?.code}`,
      reason,
      createRecurringRule: type === 'intercompany',
    })
    onChanged()
    onClose()
  }

  return (
    <SidePanel title="Account drilldown" subtitle={account ? `${account.code} · ${account.name}` : 'Loading'} onClose={onClose}>
      {loadFailed && (
        <div className="alert-strip warn">
          <AlertTriangle size={15} /> Account detail or FS lines could not be loaded.
        </div>
      )}
      {account && (
        <>
          <div className="panel-card">
            <div className="account-title">
              <span className="mono">{account.code}</span>
              {account.isFirstSeen && <span className="new-badge">New</span>}
            </div>
            <strong>{account.name}</strong>
            <p>
              {account.type} · {account.tenantId}
            </p>
            <Sparkline current={parseJson<number[]>(account.monthlyBalancesJson, [])} />
          </div>
          <label className="field">
            <span>FS line</span>
            <select value={fsLine} onChange={(event) => setFsLine(event.target.value)}>
              <option value="">Select an FS line</option>
              {fsLines.map((line) => (
                <option value={line.name} key={line.id}>
                  {formatStatementType(line.statementType)} · {line.section} · {line.name}
                </option>
              ))}
            </select>
          </label>
          {fsLines.length === 0 && (
            <div className="alert-strip warn">
              <AlertTriangle size={15} /> Create FS lines in the mapping library before accepting mappings.
            </div>
          )}
          <label className="field">
            <span>Audit reason</span>
            <textarea value={reason} onChange={(event) => setReason(event.target.value)} rows={3} />
          </label>
          <div className="panel-actions">
            <Button variant="primary" icon={<Check size={14} />} disabled={!fsLine} onClick={acceptMapping}>
              Accept mapping
            </Button>
            <Button variant="secondary" disabled={!fsLine} onClick={splitMapping}>
              Split
            </Button>
            <Button variant="secondary" onClick={markReviewed}>
              Reviewed
            </Button>
            <Button variant="ghost" onClick={rejectMapping}>
              Reject
            </Button>
            <Button variant="secondary" onClick={() => eliminate('eliminate')}>
              Eliminate
            </Button>
            <Button variant="secondary" onClick={() => eliminate('intercompany')}>
              Intercompany
            </Button>
            <Button variant="ghost" onClick={() => eliminate('exclude')}>
              Exclude
            </Button>
          </div>
          <div className="table-header panel-table-title">
            <strong>Transactions</strong>
            <span>{detail.transactions.length}</span>
          </div>
          <div className="transaction-list">
            {detail.transactions.map((tx) => (
              <div key={tx.id}>
                <span className="mono">{tx.transactionDate}</span>
                <strong>{tx.description}</strong>
                <small>{fmtMoney(tx.credit - tx.debit)}</small>
              </div>
            ))}
          </div>
        </>
      )}
    </SidePanel>
  )
}

// ---------------------------------------------------------------------------
// MappingView — main export
// ---------------------------------------------------------------------------

export function MappingView({
  organizationKey,
  periodKey,
  refreshKey,
  openAccount,
}: {
  organizationKey: string
  periodKey: string
  refreshKey: number
  openAccount: (id: string) => void
}) {
  const [accounts, setAccounts] = useState<AccountDto[]>([])
  const [fsLines, setFsLines] = useState<FsLineDefinitionDto[]>([])
  const [filter, setFilter] = useState<'all' | 'new' | 'review'>('all')
  const [loadFailed, setLoadFailed] = useState(false)
  const [groupingBusy, setGroupingBusy] = useState(false)
  const [groupingResult, setGroupingResult] = useState<FinancialStatementGroupingResult | null>(null)
  const [lineDraft, setLineDraft] = useState({
    statementType: 'IncomeStatement',
    section: 'Revenue',
    name: '',
    normalBalance: 'Credit',
    aiGuidance: '',
  })

  const loadMapping = useCallback(async () => {
    if (!organizationKey || !periodKey) return
    try {
      const [nextAccounts, nextLines] = await Promise.all([
        fetchJson<AccountDto[]>(`/api/mapping/accounts?organizationKey=${encodeURIComponent(organizationKey)}&periodKey=${encodeURIComponent(periodKey)}`),
        fetchJson<FsLineDefinitionDto[]>(`/api/mapping/fs-lines?organizationKey=${encodeURIComponent(organizationKey)}`),
      ])
      setAccounts(nextAccounts)
      setFsLines(nextLines)
      setLoadFailed(false)
    } catch {
      setAccounts([])
      setFsLines([])
      setLoadFailed(true)
    }
  }, [organizationKey, periodKey])

  useEffect(() => {
    void loadMapping()
  }, [loadMapping, refreshKey])

  const activeLines = fsLines.filter((line) => line.isActive)
  const groupedLines = activeLines.reduce<Record<string, FsLineDefinitionDto[]>>((groups, line) => {
    const key = `${formatStatementType(line.statementType)} · ${line.section}`
    groups[key] = groups[key] ? [...groups[key], line] : [line]
    return groups
  }, {})

  const filtered = accounts.filter((account) => {
    if (filter === 'new') return account.isFirstSeen
    if (filter === 'review') return account.reviewStatus !== 'Reviewed'
    return true
  })

  const createFsLine = async () => {
    if (!lineDraft.name.trim()) return
    const created = await postJson<FsLineDefinitionDto>('/api/mapping/fs-lines', {
      organizationKey,
      ...lineDraft,
      reason: 'Created from mapping inbox',
    })
    setFsLines((lines) => [...lines, created])
    setLineDraft((draft) => ({ ...draft, name: '', aiGuidance: '' }))
  }

  const deactivateFsLine = async (lineId: string) => {
    const updated = await deleteJson<FsLineDefinitionDto>(`/api/mapping/fs-lines/${lineId}`)
    setFsLines((lines) => lines.map((line) => (line.id === updated.id ? updated : line)))
  }

  const groupFromFinancials = async () => {
    setGroupingBusy(true)
    try {
      const result = await postJson<FinancialStatementGroupingResult>('/api/mapping/group-from-financials', {
        organizationKey,
        includeReviewed: false,
      })
      setGroupingResult(result)
      await loadMapping()
    } finally {
      setGroupingBusy(false)
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Consolidation</div>
          <h1>Account Mapping &amp; Eliminations</h1>
          <p>Accounts are scoped to the selected entity and period. New Xero accounts surface here after the rolling sync creates the period.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<Wand2 size={14} />} disabled={groupingBusy} onClick={groupFromFinancials}>
            {groupingBusy ? 'Grouping...' : 'Group from financials'}
          </Button>
          <div className="segmented compact">
            <SegmentButton active={filter === 'all'} onClick={() => setFilter('all')}>
              All
            </SegmentButton>
            <SegmentButton active={filter === 'new'} onClick={() => setFilter('new')}>
              New
            </SegmentButton>
            <SegmentButton active={filter === 'review'} onClick={() => setFilter('review')}>
              Review
            </SegmentButton>
          </div>
        </div>
      </div>
      {groupingResult && (
        <div className="alert-strip">
          <Sparkles size={15} /> Grouped {groupingResult.accountsUpdated} accounts from imported statements ({groupingResult.statementMatched} direct matches,{' '}
          {groupingResult.fallbackMatched} fallback; {groupingResult.fsLinesCreated} FS lines created).
        </div>
      )}
      <div className="mapping-grid">
        <Card className="mapping-inbox">
          <div className="table-header">
            <strong>Mapping inbox</strong>
            <span>{filtered.length} accounts</span>
          </div>
          {loadFailed && (
            <div className="alert-strip warn">
              <AlertTriangle size={15} /> Mapping data could not be loaded for this entity-period.
            </div>
          )}
          <table>
            <thead>
              <tr>
                <th>Account</th>
                <th>FS Line</th>
                <th>AI</th>
                <th>Treatment</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((account) => (
                <tr key={account.id}>
                  <td>
                    <button className="account-link" onClick={() => openAccount(account.id)}>
                      <span className="mono">{account.code}</span>
                      <strong>{account.name}</strong>
                      {account.isFirstSeen && <span className="new-badge">New</span>}
                    </button>
                  </td>
                  <td>{account.fsLine}</td>
                  <td>
                    <span className="confidence">{Math.round(account.mappingConfidence * 100)}%</span>
                    {account.aiSuggestedFsLine}
                  </td>
                  <td>{account.consolidationTreatment}</td>
                  <td>
                    <PanelRightOpen size={15} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {!loadFailed && filtered.length === 0 && <div className="empty-state">No accounts have been imported for this entity-period yet.</div>}
        </Card>
        <Card className="fs-line-card">
          <div className="table-header">
            <strong>FS line library</strong>
            <span>{activeLines.length} active</span>
          </div>
          <div className="fs-line-form">
            <div className="field-row">
              <label className="field">
                <span>Statement</span>
                <select value={lineDraft.statementType} onChange={(event) => setLineDraft((draft) => ({ ...draft, statementType: event.target.value }))}>
                  <option value="IncomeStatement">Income statement</option>
                  <option value="BalanceSheet">Balance sheet</option>
                  <option value="TrialBalance">Trial balance</option>
                </select>
              </label>
              <label className="field">
                <span>Normal</span>
                <select value={lineDraft.normalBalance} onChange={(event) => setLineDraft((draft) => ({ ...draft, normalBalance: event.target.value }))}>
                  <option value="Credit">Credit</option>
                  <option value="Debit">Debit</option>
                </select>
              </label>
            </div>
            <label className="field">
              <span>Group</span>
              <input
                value={lineDraft.section}
                onChange={(event) => setLineDraft((draft) => ({ ...draft, section: event.target.value }))}
                placeholder="Revenue, COGS, Assets..."
              />
            </label>
            <label className="field">
              <span>FS line</span>
              <input
                value={lineDraft.name}
                onChange={(event) => setLineDraft((draft) => ({ ...draft, name: event.target.value }))}
                placeholder="Revenue - Implementation"
              />
            </label>
            <label className="field">
              <span>AI guidance</span>
              <textarea
                value={lineDraft.aiGuidance}
                onChange={(event) => setLineDraft((draft) => ({ ...draft, aiGuidance: event.target.value }))}
                rows={3}
                placeholder="Tell AI what kinds of accounts belong here."
              />
            </label>
            <Button variant="primary" icon={<Plus size={14} />} onClick={createFsLine}>
              Add FS line
            </Button>
          </div>
          <div className="fs-line-list">
            {Object.entries(groupedLines).map(([group, lines]) => (
              <div className="fs-line-group" key={group}>
                <span>{group}</span>
                {lines.map((line) => (
                  <div className="fs-line-row" key={line.id}>
                    <strong>{line.name}</strong>
                    <small>{line.normalBalance}</small>
                    <button title="Deactivate FS line" onClick={() => deactivateFsLine(line.id)}>
                      <X size={13} />
                    </button>
                  </div>
                ))}
              </div>
            ))}
            {activeLines.length === 0 && <div className="empty-state">Create FS lines before mapping accounts.</div>}
          </div>
          <div className="elim-divider" />
          <div className="elim-option">
            <strong>Eliminations stay here too</strong>
            <p>Open an account drilldown to eliminate, exclude, split, or create an intercompany rule with audit metadata.</p>
          </div>
        </Card>
      </div>
    </div>
  )
}
