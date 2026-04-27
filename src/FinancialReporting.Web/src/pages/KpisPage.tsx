import { useCallback, useEffect, useState } from 'react'
import { fetchJson, postJson, putJson } from '../api/client'
import { Bell, FileSpreadsheet, Gauge, TrendingUp } from 'lucide-react'
import { Button, Card, Sparkline } from '../components/primitives'

// TODO: dedupe — mirrors PackageDto in App.tsx
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

// TODO: dedupe — mirrors SlideDto in App.tsx
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

// TODO: dedupe — mirrors SlideBlockDto in App.tsx
type SlideBlockDto = {
  id: string
  sortOrder: number
  kind: string
  contentJson: string
}

// TODO: dedupe — mirrors IssueDto in App.tsx
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

// TODO: dedupe — mirrors KpiDto in App.tsx
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

// TODO: dedupe — mirrors KpiAlertDto in App.tsx
type KpiAlertDto = {
  id: string
  kpiDefinitionId: string
  kpiName: string
  direction: string
  thresholdValue: number
  severity: string
  message: string
  isActive: boolean
  isTriggered: boolean
  lastTriggeredAt: string | null
}

// TODO: dedupe — mirrors NonFinancialMetricDto in App.tsx
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

// TODO: dedupe — mirrors FormulaEvaluationDto in App.tsx
type FormulaEvaluationDto = {
  formula: string
  normalizedExpression: string
  value: number
  dependencies: string[]
}

// TODO: dedupe — mirrors fmtMoney in App.tsx
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

// TODO: dedupe — mirrors formatKpiValue in App.tsx
function formatKpiValue(kpi: KpiDto) {
  if (kpi.unit === '%') return `${kpi.currentValue.toFixed(1)}%`
  if (kpi.unit.toLowerCase().includes('day')) return `${kpi.currentValue.toFixed(0)} days`
  if (kpi.unit.toLowerCase().includes('month')) return `${kpi.currentValue.toFixed(1)} mo`
  if (kpi.unit === '$') return fmtMoney(kpi.currentValue)
  return `${kpi.currentValue.toLocaleString('en-US', { maximumFractionDigits: 1 })} ${kpi.unit}`
}

// TODO: dedupe — mirrors parseJson in App.tsx
function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

export function KpiLibrary({ packageData }: { packageData: PackageDto }) {
  const [kpis, setKpis] = useState<KpiDto[]>([])
  const [alerts, setAlerts] = useState<KpiAlertDto[]>([])
  const [metrics, setMetrics] = useState<NonFinancialMetricDto[]>([])
  const [draftTarget, setDraftTarget] = useState<Record<string, number>>({})
  const [formulaDraft, setFormulaDraft] = useState('kpi("Cash Runway") * 30')
  const [formulaResult, setFormulaResult] = useState<FormulaEvaluationDto | null>(null)

  const load = useCallback(async () => {
    const [nextKpis, nextAlerts, nextMetrics] = await Promise.all([
      fetchJson<KpiDto[]>(`/api/kpis?organizationId=${packageData.organizationId}`),
      fetchJson<KpiAlertDto[]>(`/api/kpi-alerts?organizationId=${packageData.organizationId}`),
      fetchJson<NonFinancialMetricDto[]>(`/api/non-financial-metrics?organizationId=${packageData.organizationId}&periodKey=${encodeURIComponent(packageData.periodKey)}`),
    ])
    setKpis(nextKpis)
    setAlerts(nextAlerts)
    setMetrics(nextMetrics)
    setDraftTarget(Object.fromEntries(nextKpis.map((kpi) => [kpi.id, kpi.targetValue])))
  }, [packageData.organizationId, packageData.periodKey])

  useEffect(() => {
    load()
      .catch(() => {
        setKpis([])
        setAlerts([])
        setMetrics([])
      })
  }, [load])

  const saveKpi = async (kpi: KpiDto, patch: Partial<KpiDto>) => {
    const next = { ...kpi, ...patch }
    const saved = await putJson<KpiDto>(`/api/kpis/${kpi.id}`, {
      organizationId: next.organizationId,
      name: next.name,
      category: next.category,
      formula: next.formula,
      unit: next.unit,
      currentValue: next.currentValue,
      targetValue: next.targetValue,
      isPinned: next.isPinned,
      higherIsBetter: next.name.toLowerCase() !== 'dso',
    })
    setKpis((current) => current.map((item) => (item.id === saved.id ? saved : item)))
  }

  const enableAlert = async (kpi: KpiDto) => {
    const direction = kpi.name.toLowerCase() === 'dso' ? 'Above' : 'Below'
    const saved = await postJson<KpiAlertDto>('/api/kpi-alerts', {
      kpiDefinitionId: kpi.id,
      direction,
      thresholdValue: kpi.targetValue,
      severity: kpi.status === 'bad' ? 'High' : 'Medium',
      message: `${kpi.name} crossed the board target.`,
      isActive: true,
    })
    setAlerts((current) => [saved, ...current])
  }

  const addKpi = async () => {
    const saved = await postJson<KpiDto>('/api/kpis', {
      organizationId: packageData.organizationId,
      name: 'Custom KPI',
      category: 'Custom',
      formula: 'Manual input',
      unit: '$',
      currentValue: 0,
      targetValue: 1,
      isPinned: false,
      higherIsBetter: true,
    }).catch(() => null)
    if (saved) setKpis((current) => [saved, ...current])
  }

  const addMetric = async () => {
    const saved = await postJson<NonFinancialMetricDto>('/api/non-financial-metrics', {
      organizationId: packageData.organizationId,
      reportingPeriodId: packageData.reportingPeriodId,
      name: 'Custom Driver',
      category: 'Operations',
      unit: 'count',
      currentValue: 0,
      priorValue: 0,
      targetValue: 1,
      valuesJson: '[]',
      source: 'Manual datasheet',
      isPinned: false,
    }).catch(() => null)
    if (saved) setMetrics((current) => [saved, ...current])
  }

  const evaluateFormula = async () => {
    const result = await postJson<FormulaEvaluationDto>('/api/formulas/evaluate', {
      organizationId: packageData.organizationId,
      reportingPeriodId: packageData.reportingPeriodId,
      formula: formulaDraft,
    }).catch(() => null)
    setFormulaResult(result)
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Performance Library</div>
          <h1>KPI Scorecards</h1>
          <p>Pinned KPI cards, alerts, and non-financial drivers feed package slides, shared dashboards, and export QA.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<TrendingUp size={15} />} onClick={addMetric}>Add driver</Button>
          <Button variant="primary" icon={<Gauge size={15} />} onClick={addKpi}>Set goals</Button>
        </div>
      </div>
      <Card className="formula-card">
        <div className="table-header">
          <strong>Formula builder</strong>
          <span>{formulaResult ? fmtMoney(formulaResult.value) : 'Ready'}</span>
        </div>
        <div className="formula-row">
          <input value={formulaDraft} onChange={(event) => setFormulaDraft(event.target.value)} />
          <Button variant="secondary" icon={<FileSpreadsheet size={14} />} onClick={evaluateFormula}>Evaluate</Button>
        </div>
        <small className="mono">{formulaResult?.normalizedExpression ?? 'Use fs("Revenue"), kpi("Cash Runway"), or metric("Customer Count").'}</small>
      </Card>
      <div className="kpi-grid">
        {kpis.map((kpi) => (
          <Card key={kpi.id} className="kpi-card">
            <span className="eyebrow">{kpi.category}</span>
            <strong>{kpi.name}</strong>
            <div className="kpi-value">{formatKpiValue(kpi)}</div>
            <small className="mono">{kpi.formula}</small>
            <div className="target-bar">
              <span style={{ width: kpi.status === 'good' ? '112%' : kpi.status === 'warn' ? '88%' : '72%' }} />
            </div>
            <label className="field compact-field">
              <span>Target</span>
              <input
                type="number"
                value={draftTarget[kpi.id] ?? kpi.targetValue}
                onChange={(event) => setDraftTarget((current) => ({ ...current, [kpi.id]: Number(event.target.value) }))}
                onBlur={() => saveKpi(kpi, { targetValue: draftTarget[kpi.id] ?? kpi.targetValue })}
              />
            </label>
            <label className="check-row">
              <input type="checkbox" checked={kpi.isPinned} onChange={(event) => saveKpi(kpi, { isPinned: event.target.checked })} />
              <span>Pinned to slides</span>
            </label>
            <div className="alert-chip-row">
              {alerts.filter((alert) => alert.kpiDefinitionId === kpi.id).map((alert) => (
                <span key={alert.id} className={alert.isTriggered ? 'alert-chip triggered' : 'alert-chip'}>
                  <Bell size={12} /> {alert.direction} {alert.thresholdValue.toLocaleString()}
                </span>
              ))}
              {!alerts.some((alert) => alert.kpiDefinitionId === kpi.id) && (
                <Button variant="ghost" icon={<Bell size={13} />} onClick={() => enableAlert(kpi)}>Alert</Button>
              )}
            </div>
          </Card>
        ))}
      </div>
      <div className="section-title">
        <h2>Non-financial drivers</h2>
        <span className="muted">{metrics.length} manual or integrated metrics</span>
      </div>
      <div className="benchmark-grid compact">
        {metrics.map((metric) => (
          <Card key={metric.id} className="benchmark-card">
            <span className="eyebrow">{metric.category}</span>
            <h3>{metric.name}</h3>
            <div className="metric-row">
              <span className="metric">{metric.currentValue.toLocaleString()} {metric.unit}</span>
              <span className={metric.currentValue >= metric.targetValue ? 'good-text mono' : 'bad-text mono'}>target {metric.targetValue.toLocaleString()}</span>
            </div>
            <Sparkline current={parseJson<number[]>(metric.valuesJson, [])} />
            <small>{metric.source}</small>
          </Card>
        ))}
      </div>
    </div>
  )
}
