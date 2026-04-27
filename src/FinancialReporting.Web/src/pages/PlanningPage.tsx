/**
 * PlanningPage — extracted from App.tsx.
 *
 * TODO: Once the App.tsx type registry is extracted into a shared types module,
 * remove the inline type re-declarations below (marked with TODO-DEDUPE) and
 * import them from that shared location.
 */

import { useCallback, useEffect, useState } from 'react'
import { fetchJson, postJson, putJson } from '../api/client'
import { Button, Card, SegmentButton } from '../components/primitives'
import {
  Check,
  Plus,
  Target,
} from 'lucide-react'

// ---------------------------------------------------------------------------
// TODO: dedupe with App.tsx type registry
// ---------------------------------------------------------------------------

/** TODO: dedupe with App.tsx type registry */
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

/** TODO: dedupe with App.tsx type registry */
type SlideBlockDto = {
  id: string
  sortOrder: number
  kind: string
  contentJson: string
}

/** TODO: dedupe with App.tsx type registry */
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

/** TODO: dedupe with App.tsx type registry */
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

/** TODO: dedupe with App.tsx type registry */
type NonFinancialMetricDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  name: string
  category: string
  unit: string
  currentValue: number
  priorValue: number
  targetValue: number
  valuesJson: string
  source: string
  isPinned: boolean
}

/** TODO: dedupe with App.tsx type registry */
type BudgetVarianceRowDto = {
  fsLine: string
  actualAmount: number
  budgetAmount: number
  varianceAmount: number
  variancePercent: number
}

/** TODO: dedupe with App.tsx type registry */
type ForecastEventDto = {
  id: string
  monthOffset: number
  name: string
  category: string
  revenueImpact: number
  expenseImpact: number
  cashImpact: number
  isRecurring: boolean
  notes: string
}

/** TODO: dedupe with App.tsx type registry */
type ForecastProjectionRowDto = {
  monthKey: string
  revenue: number
  grossProfit: number
  operatingExpense: number
  netIncome: number
  cashInflow: number
  cashOutflow: number
  netCashFlow: number
  endingCash: number
  accountsReceivable: number
  accountsPayable: number
  equity: number
  cashThresholdBreached: boolean
}

/** TODO: dedupe with App.tsx type registry */
type ForecastScenarioDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  name: string
  description: string
  scenarioType: string
  horizonMonths: number
  revenueGrowthPercent: number
  grossMarginPercent: number
  opexGrowthPercent: number
  cashConversionPercent: number
  startingCash: number
  cashThreshold: number
  assumptionsJson: string
  isBase: boolean
  events: ForecastEventDto[]
  rows: ForecastProjectionRowDto[]
}

/** TODO: dedupe with App.tsx type registry */
type PlanningOverviewDto = {
  reportPackageId: string
  organizationId: string
  organizationName: string
  periodKey: string
  monthlyRevenueActual: number
  monthlyOperatingExpenseActual: number
  forecastStartMonth: string
  scenarios: ForecastScenarioDto[]
  budgetRows: BudgetVarianceRowDto[]
  nonFinancialMetrics: NonFinancialMetricDto[]
}

/** TODO: dedupe with App.tsx type registry */
type CashTimingRowDto = {
  label: string
  periodStart: string
  periodEnd: string
  cashInflow: number
  cashOutflow: number
  netCashFlow: number
  endingCash: number
  cashThresholdBreached: boolean
}

/** TODO: dedupe with App.tsx type registry */
type CashTimingDto = {
  reportPackageId: string
  scenarioId: string
  granularity: string
  rows: CashTimingRowDto[]
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

/** TODO: dedupe with App.tsx helper */
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

// ---------------------------------------------------------------------------
// ColumnChart — re-declared inline (copy from App.tsx ~line 3215)
// TODO: Extract to a shared charting primitives module
// ---------------------------------------------------------------------------

function ColumnChart({ current, prior }: { current: number[]; prior: number[] }) {
  const count = Math.max(current.length, prior.length)
  const values = Array.from({ length: count }, (_, index) => ({
    current: Number(current[index] ?? 0),
    prior: Number(prior[index] ?? 0),
  }))
  const max = Math.max(...values.flatMap((value) => [Math.abs(value.current), Math.abs(value.prior)]), 1)
  const monthLabels = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

  if (count === 0) {
    return <div className="empty-state compact">No monthly chart data is attached to this slide yet.</div>
  }

  return (
    <div className="column-chart" style={{ gridTemplateColumns: `repeat(${count}, minmax(34px, 1fr))` }}>
      {values.map((value, index) => (
        <div className="bar-column" key={`${index}-${value.current}-${value.prior}`}>
          <div className="bar-pair">
            <span className={value.prior < 0 ? 'bar prior negative' : 'bar prior'} title={`Prior ${fmtMoney(value.prior)}`} style={{ height: `${Math.max(2, (Math.abs(value.prior) / max) * 100)}%` }} />
            <span className={value.current < 0 ? 'bar current negative' : 'bar current'} title={`Current ${fmtMoney(value.current)}`} style={{ height: `${Math.max(2, (Math.abs(value.current) / max) * 100)}%` }} />
          </div>
          <small>{monthLabels[index] ?? `P${index + 1}`}</small>
        </div>
      ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// StatCard — private to this file
// ---------------------------------------------------------------------------

function StatCard({ label, value, sub, tone }: { label: string; value: string; sub: string; tone?: 'good' | 'warn' }) {
  return (
    <Card className={tone ? `stat-card ${tone}` : 'stat-card'}>
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{sub}</small>
    </Card>
  )
}

// ---------------------------------------------------------------------------

export function PlanningView({ packageData }: { packageData: PackageDto }) {
  const [overview, setOverview] = useState<PlanningOverviewDto | null>(null)
  const [selectedScenarioId, setSelectedScenarioId] = useState('')
  const [draft, setDraft] = useState<Partial<ForecastScenarioDto>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [cashGranularity, setCashGranularity] = useState<'Weekly' | 'Daily'>('Weekly')
  const [cashTiming, setCashTiming] = useState<CashTimingDto | null>(null)

  const load = useCallback(async () => {
    const data = await fetchJson<PlanningOverviewDto>(`/api/planning/${packageData.id}/overview`)
    setOverview(data)
    setSelectedScenarioId((current) => current || data.scenarios[0]?.id || '')
  }, [packageData.id])

  useEffect(() => {
    load().catch(() => setOverview(null))
  }, [load])

  const active = overview?.scenarios.find((scenario) => scenario.id === selectedScenarioId) ?? overview?.scenarios[0]
  const activeId = active?.id
  const editable = active ? { ...active, ...draft } : null
  const rows = editable?.rows ?? []
  const firstBreach = rows.find((row) => row.cashThresholdBreached)
  const lastRow = rows[rows.length - 1]

  useEffect(() => {
    if (!activeId) return
    fetchJson<CashTimingDto>(`/api/planning/${packageData.id}/cash-timing?scenarioId=${activeId}&granularity=${cashGranularity}&months=3`)
      .then(setCashTiming)
      .catch(() => setCashTiming(null))
  }, [activeId, cashGranularity, packageData.id])

  const updateDraft = (patch: Partial<ForecastScenarioDto>) => setDraft((current) => ({ ...current, ...patch }))
  const scenarioPayload = (scenario: ForecastScenarioDto) => ({
    organizationId: scenario.organizationId,
    reportingPeriodId: scenario.reportingPeriodId,
    name: scenario.name,
    description: scenario.description,
    scenarioType: scenario.scenarioType,
    horizonMonths: scenario.horizonMonths,
    revenueGrowthPercent: scenario.revenueGrowthPercent,
    grossMarginPercent: scenario.grossMarginPercent,
    opexGrowthPercent: scenario.opexGrowthPercent,
    cashConversionPercent: scenario.cashConversionPercent,
    startingCash: scenario.startingCash,
    cashThreshold: scenario.cashThreshold,
    assumptionsJson: scenario.assumptionsJson,
    isBase: scenario.isBase,
  })

  const saveScenario = async () => {
    if (!editable) return
    setBusy('save')
    try {
      await putJson(`/api/planning/scenarios/${editable.id}`, scenarioPayload(editable))
      setDraft({})
      await load()
    } finally {
      setBusy(null)
    }
  }

  const cloneScenario = async () => {
    if (!active) return
    setBusy('clone')
    try {
      await postJson('/api/planning/scenarios', {
        ...scenarioPayload(active),
        name: `${active.name} copy`,
        scenarioType: 'Custom',
        isBase: false,
        revenueGrowthPercent: active.revenueGrowthPercent + 2,
      })
      await load()
    } finally {
      setBusy(null)
    }
  }

  const addEvent = async () => {
    if (!active) return
    setBusy('event')
    try {
      await postJson(`/api/planning/scenarios/${active.id}/events`, {
        monthOffset: 3,
        name: 'New hire',
        category: 'People',
        revenueImpact: 0,
        expenseImpact: 12000,
        cashImpact: -12000,
        isRecurring: true,
        notes: 'Recurring microforecast added from planning view.',
      })
      await load()
    } finally {
      setBusy(null)
    }
  }

  if (!overview || !editable) {
    return (
      <div className="page">
        <div className="empty-state">Planning data is loading.</div>
      </div>
    )
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Planning</div>
          <h1>3-way forecasting · {overview.organizationName}</h1>
          <p>Scenario planning, budget variance, cash runway, and operational drivers for {overview.periodKey}.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<Plus size={15} />} disabled={busy === 'clone'} onClick={cloneScenario}>Clone scenario</Button>
          <Button variant="accent" icon={<Target size={15} />} disabled={busy === 'event'} onClick={addEvent}>Add microforecast</Button>
          <Button variant="primary" icon={<Check size={15} />} disabled={busy === 'save'} onClick={saveScenario}>Save scenario</Button>
        </div>
      </div>

      <div className="stat-grid">
        <StatCard label="Revenue base" value={fmtMoney(overview.monthlyRevenueActual)} sub="Latest monthly actual" />
        <StatCard label="Opex base" value={fmtMoney(overview.monthlyOperatingExpenseActual)} sub="Latest monthly actual" />
        <StatCard label="Runway alert" value={firstBreach?.monthKey ?? 'Clear'} sub={`Threshold ${fmtMoney(editable.cashThreshold)}`} tone={firstBreach ? 'warn' : 'good'} />
        <StatCard label="Ending cash" value={fmtMoney(lastRow?.endingCash ?? 0)} sub={`${editable.horizonMonths} month horizon`} />
      </div>

      <div className="planning-grid">
        <Card className="scenario-card">
          <div className="table-header">
            <strong>Scenarios</strong>
            <span>{overview.scenarios.length} active</span>
          </div>
          <div className="scenario-list">
            {overview.scenarios.map((scenario) => (
              <button key={scenario.id} className={scenario.id === editable.id ? 'scenario-option active' : 'scenario-option'} onClick={() => { setSelectedScenarioId(scenario.id); setDraft({}) }}>
                <span>{scenario.scenarioType}</span>
                <strong>{scenario.name}</strong>
                <small>{scenario.revenueGrowthPercent.toFixed(1)}% growth · {scenario.grossMarginPercent.toFixed(1)}% margin</small>
              </button>
            ))}
          </div>
          <div className="field-row">
            <label className="field">
              <span>Revenue growth %</span>
              <input type="number" value={editable.revenueGrowthPercent} onChange={(event) => updateDraft({ revenueGrowthPercent: Number(event.target.value) })} />
            </label>
            <label className="field">
              <span>Gross margin %</span>
              <input type="number" value={editable.grossMarginPercent} onChange={(event) => updateDraft({ grossMarginPercent: Number(event.target.value) })} />
            </label>
          </div>
          <div className="field-row">
            <label className="field">
              <span>Opex growth %</span>
              <input type="number" value={editable.opexGrowthPercent} onChange={(event) => updateDraft({ opexGrowthPercent: Number(event.target.value) })} />
            </label>
            <label className="field">
              <span>Cash threshold</span>
              <input type="number" value={editable.cashThreshold} onChange={(event) => updateDraft({ cashThreshold: Number(event.target.value) })} />
            </label>
          </div>
          <div className="event-list">
            {editable.events.map((event) => (
              <div key={event.id}>
                <strong>{event.name}</strong>
                <span>Month {event.monthOffset} · {event.isRecurring ? 'recurring' : 'one-time'} · {fmtMoney(event.cashImpact)}</span>
              </div>
            ))}
          </div>
        </Card>

        <Card className="forecast-card">
          <div className="table-header">
            <strong>Forecast curve</strong>
            <span>P&L · cash flow · balance sheet</span>
          </div>
          <ColumnChart current={rows.slice(0, 12).map((row) => row.revenue)} prior={rows.slice(0, 12).map((row) => row.netIncome)} />
          <div className="forecast-table-wrap">
            <table className="mini-table full">
              <thead>
                <tr>
                  <th>Month</th>
                  <th>Revenue</th>
                  <th>Net income</th>
                  <th>Cash</th>
                  <th>AR</th>
                  <th>AP</th>
                </tr>
              </thead>
              <tbody>
                {rows.slice(0, 12).map((row) => (
                  <tr key={row.monthKey}>
                    <td className="mono">{row.monthKey}</td>
                    <td>{fmtMoney(row.revenue)}</td>
                    <td className={row.netIncome >= 0 ? 'good-text' : 'bad-text'}>{fmtMoney(row.netIncome)}</td>
                    <td className={row.cashThresholdBreached ? 'bad-text' : ''}>{fmtMoney(row.endingCash)}</td>
                    <td>{fmtMoney(row.accountsReceivable)}</td>
                    <td>{fmtMoney(row.accountsPayable)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      </div>

      <div className="planning-grid two">
        <Card>
          <div className="table-header">
            <strong>Budget variance</strong>
            <span>{overview.budgetRows.length} FS lines</span>
          </div>
          <table className="mini-table full">
            <tbody>
              {overview.budgetRows.map((row) => (
                <tr key={row.fsLine}>
                  <td>{row.fsLine}</td>
                  <td>{fmtMoney(row.actualAmount)}</td>
                  <td>{fmtMoney(row.budgetAmount)}</td>
                  <td className={row.varianceAmount >= 0 ? 'good-text' : 'bad-text'}>{row.variancePercent.toFixed(1)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <Card>
          <div className="table-header">
            <strong>Non-financial drivers</strong>
            <span>{overview.nonFinancialMetrics.length} metrics</span>
          </div>
          <div className="driver-list">
            {overview.nonFinancialMetrics.map((metric) => (
              <div key={metric.id}>
                <span>{metric.category}</span>
                <strong>{metric.name}</strong>
                <small>{metric.currentValue.toLocaleString()} {metric.unit} · target {metric.targetValue.toLocaleString()}</small>
              </div>
            ))}
          </div>
        </Card>
      </div>

      <Card>
        <div className="table-header">
          <strong>Cash timing</strong>
          <div className="segmented compact">
            <SegmentButton active={cashGranularity === 'Weekly'} onClick={() => setCashGranularity('Weekly')}>Weekly</SegmentButton>
            <SegmentButton active={cashGranularity === 'Daily'} onClick={() => setCashGranularity('Daily')}>Daily</SegmentButton>
          </div>
        </div>
        <div className="forecast-table-wrap">
          <table className="mini-table full">
            <thead>
              <tr>
                <th>Period</th>
                <th>Inflow</th>
                <th>Outflow</th>
                <th>Net</th>
                <th>Ending cash</th>
              </tr>
            </thead>
            <tbody>
              {(cashTiming?.rows ?? []).slice(0, cashGranularity === 'Daily' ? 21 : 14).map((row) => (
                <tr key={row.label}>
                  <td className="mono">{row.label}</td>
                  <td>{fmtMoney(row.cashInflow)}</td>
                  <td>{fmtMoney(row.cashOutflow)}</td>
                  <td className={row.netCashFlow >= 0 ? 'good-text' : 'bad-text'}>{fmtMoney(row.netCashFlow)}</td>
                  <td className={row.cashThresholdBreached ? 'bad-text' : ''}>{fmtMoney(row.endingCash)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {!cashTiming?.rows.length && <div className="empty-state compact">Cash timing rows are loading.</div>}
        </div>
      </Card>
    </div>
  )
}
