import { useEffect, useMemo, useState } from 'react'
import { fetchJson } from '../api/client'
import { Card } from '../components/primitives'

type RecurringEliminationRule = {
  id: string
  organizationId: string
  reportingPeriodId: string | null
  glAccountId: string | null
  type: string
  description: string
  criteriaJson: string
  amount: number
  reason: string
  isActive: boolean
  createdAt: string
}

type ElimType = 'all' | 'Intercompany' | 'Elimination'
type ElimStatus = 'all' | 'Active' | 'Pending'

function fmtMoney(value: number) {
  if (!Number.isFinite(value)) return '$0'
  const sign = value < 0 ? '−' : ''
  const abs = Math.abs(value)
  if (abs >= 1_000_000) return `${sign}$${(abs / 1_000_000).toFixed(2)}M`
  if (abs >= 1_000) return `${sign}$${(abs / 1_000).toFixed(0)}K`
  return `${sign}$${abs.toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

function classifyType(type: string): 'Intercompany' | 'Elimination' {
  return /interco/i.test(type) ? 'Intercompany' : 'Elimination'
}

function classifyStatus(rule: RecurringEliminationRule): 'Active' | 'Pending' {
  return rule.isActive ? 'Active' : 'Pending'
}

export function EliminationsView({
  organizationKey,
  periodKey,
}: {
  organizationKey: string
  periodKey: string
}) {
  const [rules, setRules] = useState<RecurringEliminationRule[]>([])
  const [typeFilter, setTypeFilter] = useState<ElimType>('all')
  const [statusFilter, setStatusFilter] = useState<ElimStatus>('all')
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    const params = new URLSearchParams()
    if (organizationKey) params.set('organizationKey', organizationKey)
    if (periodKey) params.set('periodKey', periodKey)
    const qs = params.toString()
    const url = qs ? `/api/mapping/recurring-eliminations?${qs}` : '/api/mapping/recurring-eliminations'
    fetchJson<RecurringEliminationRule[]>(url)
      .then((data) => {
        if (cancelled) return
        setRules(Array.isArray(data) ? data : [])
        setLoadError(null)
      })
      .catch(() => {
        if (cancelled) return
        setRules([])
        setLoadError('Could not load elimination rules.')
      })
    return () => {
      cancelled = true
    }
  }, [organizationKey, periodKey])

  const visible = useMemo(() => {
    return rules.filter((rule) => {
      const t = classifyType(rule.type)
      const s = classifyStatus(rule)
      if (typeFilter !== 'all' && t !== typeFilter) return false
      if (statusFilter !== 'all' && s !== statusFilter) return false
      return true
    })
  }, [rules, typeFilter, statusFilter])

  const totals = useMemo(() => {
    const active = rules.filter((r) => r.isActive)
    const pending = rules.filter((r) => !r.isActive)
    const interco = rules.filter((r) => classifyType(r.type) === 'Intercompany')
    const elim = rules.filter((r) => classifyType(r.type) === 'Elimination')
    const totalEliminated = active.reduce((sum, r) => sum + Math.abs(Number(r.amount) || 0), 0)
    const netAmount = active.reduce((sum, r) => sum + (Number(r.amount) || 0), 0)
    return { active, pending, interco, elim, totalEliminated, netAmount }
  }, [rules])

  return (
    <div className="page cs-page-narrow">
      <div className="page-header">
        <div>
          <div className="eyebrow">Consolidation</div>
          <h1>Eliminations</h1>
          <p>
            {organizationKey || 'No entity'} · {periodKey || 'No period'} · Intercompany and elimination rules
            applied at consolidation.
          </p>
        </div>
      </div>

      {loadError && (
        <div className="cs-alert warn" role="alert">
          <span className="cs-alert-dot" /> {loadError}
        </div>
      )}

      <div className="cs-summary-grid">
        <SummaryTile label="Total eliminated" value={fmtMoney(totals.totalEliminated)} sub="Gross active rules" />
        <SummaryTile label="Intercompany rules" value={String(totals.interco.length)} sub="Income/expense pairs" />
        <SummaryTile label="Balance sheet elims" value={String(totals.elim.length)} sub="Investment & equity" />
        <SummaryTile
          label="Pending confirmation"
          value={String(totals.pending.length)}
          sub="Inactive — require review"
          tone={totals.pending.length > 0 ? 'warn' : 'good'}
        />
      </div>

      {totals.pending.length > 0 && (
        <div className="cs-alert warn" role="status">
          <span className="cs-alert-dot" />
          <span>
            <strong>
              {totals.pending.length} elimination rule{totals.pending.length > 1 ? 's' : ''}
            </strong>{' '}
            inactive — consolidated financials may be incomplete until reactivated.
          </span>
        </div>
      )}

      <Card className="cs-table-card">
        <div className="cs-table-toolbar">
          <div className="cs-seg-group">
            {(['all', 'Intercompany', 'Elimination'] as ElimType[]).map((value) => (
              <button
                key={value}
                type="button"
                className={typeFilter === value ? 'cs-seg active' : 'cs-seg'}
                onClick={() => setTypeFilter(value)}
              >
                {value === 'all' ? 'All' : value}
              </button>
            ))}
          </div>
          <div className="cs-seg-group">
            {([
              { value: 'all' as const, label: 'All' },
              { value: 'Active' as const, label: `Active (${totals.active.length})` },
              { value: 'Pending' as const, label: `Pending (${totals.pending.length})` },
            ] satisfies Array<{ value: ElimStatus; label: string }>).map((option) => (
              <button
                key={option.value}
                type="button"
                className={statusFilter === option.value ? 'cs-seg active' : 'cs-seg'}
                onClick={() => setStatusFilter(option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>
        </div>

        <div className="cs-table-head">
          <span>Rule / description</span>
          <span>Type</span>
          <span className="num">Amount</span>
          <span>Status</span>
          <span>Created</span>
        </div>

        {visible.length === 0 && (
          <div className="cs-table-empty">No elimination rules match this filter.</div>
        )}

        {visible.map((rule) => {
          const t = classifyType(rule.type)
          const s = classifyStatus(rule)
          const amount = Number(rule.amount) || 0
          return (
            <div className="cs-table-row" key={rule.id}>
              <div className="cs-table-cell-main">
                <strong>{rule.description || rule.type}</strong>
                <small>{rule.reason || '—'}</small>
              </div>
              <span className={`cs-tag ${t === 'Intercompany' ? 'tone-blue' : 'tone-purple'}`}>{t}</span>
              <span className="cs-table-cell-num mono">{fmtMoney(amount)}</span>
              <span className={`cs-pill ${s === 'Active' ? 'good' : 'warn'}`}>
                <span className="cs-status-dot" /> {s}
              </span>
              <span className="cs-table-cell-meta mono">
                {new Date(rule.createdAt).toLocaleDateString('en-US', {
                  month: 'short',
                  day: 'numeric',
                  year: 'numeric',
                })}
              </span>
            </div>
          )
        })}

        <div className="cs-table-footer">
          <span>
            {visible.length} rules shown · {totals.active.length} active
          </span>
          <span className="mono">Net elimination: {fmtMoney(totals.netAmount)}</span>
        </div>
      </Card>
    </div>
  )
}

function SummaryTile({
  label,
  value,
  sub,
  tone,
}: {
  label: string
  value: string
  sub: string
  tone?: 'warn' | 'good'
}) {
  return (
    <div className={`cs-summary-tile ${tone ?? ''}`}>
      <div className="eyebrow">{label}</div>
      <div className="cs-summary-value mono">{value}</div>
      <div className="cs-summary-sub">{sub}</div>
    </div>
  )
}
