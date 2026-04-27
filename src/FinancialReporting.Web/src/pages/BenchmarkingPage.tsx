/**
 * BenchmarkingPage — extracted from App.tsx.
 *
 * TODO: Once the App.tsx type registry is extracted into a shared types module,
 * remove the inline type re-declarations below (marked with TODO-DEDUPE) and
 * import them from that shared location.
 */

import { useEffect, useState } from 'react'
import { fetchJson, postJson, putJson } from '../api/client'
import { Button, Card, Sparkline } from '../components/primitives'
import { Plus } from 'lucide-react'

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
type KpiDto = {
  id: string
  organizationId: string
  name: string
  category: string
  formula: string
  unit: string
  currentValue: number
  targetValue: number
  isPinned: boolean
  status: string
}

/** TODO: dedupe with App.tsx type registry */
type FxRateDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  currencyCode: string
  rateToPresentation: number
  source: string
  updatedAt: string
}

/** TODO: dedupe with App.tsx type registry */
type BenchmarkRowDto = {
  organizationId: string
  organizationName: string
  abbreviation: string
  isConsolidated: boolean
  reportPackageId: string | null
  packageStatus: string
  revenue: number
  expense: number
  netIncome: number
  grossMarginPercent: number
  openIssueCount: number
  kpis: KpiDto[]
  rank: number
}

/** TODO: dedupe with App.tsx type registry */
type BenchmarkingDto = {
  periodKey: string
  rows: BenchmarkRowDto[]
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

/** TODO: dedupe with App.tsx helper */
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

/** TODO: dedupe with App.tsx helper */
function formatKpiValue(kpi: KpiDto) {
  if (kpi.unit === '%') return `${kpi.currentValue.toFixed(1)}%`
  if (kpi.unit.toLowerCase().includes('day')) return `${kpi.currentValue.toFixed(0)} days`
  if (kpi.unit.toLowerCase().includes('month')) return `${kpi.currentValue.toFixed(1)} mo`
  if (kpi.unit === '$') return fmtMoney(kpi.currentValue)
  return `${kpi.currentValue.toLocaleString('en-US', { maximumFractionDigits: 1 })} ${kpi.unit}`
}

export function BenchmarkingView({ packageData }: { packageData: PackageDto }) {
  const [benchmarking, setBenchmarking] = useState<BenchmarkingDto | null>(null)
  const [fxRates, setFxRates] = useState<FxRateDto[]>([])

  useEffect(() => {
    Promise.all([
      fetchJson<BenchmarkingDto>(`/api/benchmarking?periodKey=${encodeURIComponent(packageData.periodKey)}`),
      fetchJson<FxRateDto[]>(`/api/fx-rates?organizationId=${packageData.organizationId}&periodKey=${encodeURIComponent(packageData.periodKey)}`),
    ])
      .then(([benchmarks, rates]) => {
        setBenchmarking(benchmarks)
        setFxRates(rates)
      })
      .catch(() => {
        setBenchmarking(null)
        setFxRates([])
      })
  }, [packageData.organizationId, packageData.periodKey])

  const saveFxRate = async (rate: FxRateDto, value: number) => {
    const saved = await putJson<FxRateDto>(`/api/fx-rates/${rate.id}`, {
      organizationId: rate.organizationId,
      reportingPeriodId: rate.reportingPeriodId,
      currencyCode: rate.currencyCode,
      rateToPresentation: value,
      source: 'Manual benchmark table',
    })
    setFxRates((current) => current.map((item) => (item.id === saved.id ? saved : item)))
  }

  const addFxRate = async () => {
    const code = ['EUR', 'GBP', 'CAD', 'AUD', 'MXN'].find((candidate) => !fxRates.some((rate) => rate.currencyCode === candidate)) ?? 'EUR'
    const saved = await postJson<FxRateDto>('/api/fx-rates', {
      organizationId: packageData.organizationId,
      reportingPeriodId: packageData.reportingPeriodId,
      currencyCode: code,
      rateToPresentation: 1,
      source: 'Manual benchmark table',
    }).catch(() => null)
    if (saved) setFxRates((current) => [...current, saved])
  }

  const rows = benchmarking?.rows ?? []
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Benchmarks</div>
          <h1>Entity performance comparison</h1>
          <p>Side-by-side financials, package status, KPI scorecards, and open-review load for {benchmarking?.periodKey ?? packageData.periodKey}.</p>
        </div>
      </div>
      <div className="benchmark-grid">
        {rows.map((row) => (
          <Card key={row.organizationId} className="benchmark-card">
            <div className="benchmark-rank">{row.rank}</div>
            <div>
              <span className="eyebrow">{row.isConsolidated ? 'Consolidated' : row.abbreviation}</span>
              <h3>{row.organizationName}</h3>
              <p>{row.packageStatus} · {row.openIssueCount} open issues</p>
            </div>
            <div className="metric-row">
              <span className="metric">{fmtMoney(row.netIncome)}</span>
              <span className={row.grossMarginPercent >= 0 ? 'good-text mono' : 'bad-text mono'}>{row.grossMarginPercent.toFixed(1)}% margin</span>
            </div>
            <Sparkline current={[row.revenue, row.expense, row.netIncome].map((value) => Math.abs(value))} />
            <div className="benchmark-kpis">
              {row.kpis.map((kpi) => (
                <span key={kpi.id}>{kpi.name}: {formatKpiValue(kpi)}</span>
              ))}
            </div>
          </Card>
        ))}
      </div>
      <Card>
        <div className="table-header">
          <strong>FX rate table</strong>
          <Button variant="ghost" icon={<Plus size={13} />} onClick={addFxRate}>Add rate</Button>
        </div>
        <div className="fx-rate-grid">
          {fxRates.map((rate) => (
            <label key={rate.id} className="field compact-field">
              <span>{rate.currencyCode}</span>
              <input type="number" step="0.0001" value={rate.rateToPresentation} onChange={(event) => saveFxRate(rate, Number(event.target.value))} />
            </label>
          ))}
        </div>
      </Card>
      {rows.length === 0 && <div className="empty-state">No benchmark rows are available for this period yet.</div>}
    </div>
  )
}
