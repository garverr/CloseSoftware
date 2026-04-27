// TODO dedupe: PackageDto, ReportingStudioDto, ReportingStudioSettings, CompetitiveFeatureGroupDto,
//              SlideDto, SlideBlockDto, IssueDto types are also declared in App.tsx
import { useCallback, useEffect, useState } from 'react'
import { fetchJson, postJson, putJson } from '../api/client'
import { Button, Card } from '../components/primitives'
import { AlertTriangle, Check, CheckCircle2, Wand2, X } from 'lucide-react'

// TODO dedupe: also in App.tsx
type SlideBlockDto = {
  id: string
  sortOrder: number
  kind: string
  contentJson: string
}

// TODO dedupe: also in App.tsx
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

// TODO dedupe: also in App.tsx
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

// TODO dedupe: also in App.tsx
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

// TODO dedupe: also in App.tsx
type CompetitiveFeatureGroupDto = {
  category: string
  competitorPattern: string
  features: Array<{
    name: string
    status: string
    ourImplementation: string
  }>
}

// TODO dedupe: also in App.tsx
type ReportingStudioSettings = {
  reportStyle: string
  numberFormat: string
  rounding: string
  statementLayout: string
  commentaryTone: string
  reportSections: string[]
  showPriorMonth: boolean
  showPriorYear: boolean
  showBudget: boolean
  showForecast: boolean
  showYtd: boolean
  showRollingTwelve: boolean
  showVarianceDollar: boolean
  showVariancePercent: boolean
  showZeroRows: boolean
  landscapeForWideTables: boolean
  includeFluxNarratives: boolean
  includeLedgerEvidence: boolean
  includeFinalReview: boolean
  includeActionPlan: boolean
}

// TODO dedupe: also in App.tsx
type ReportingStudioDto = {
  reportPackageId: string
  organizationName: string
  periodKey: string
  settings: ReportingStudioSettings
  contentLibrary: Array<{
    name: string
    description: string
    items: Array<{ name: string; kind: string; description: string }>
  }>
  fsLineSections: Array<{ statementType: string; section: string; lineCount: number }>
  statementSections: Array<{ statementType: string; section: string; lineCount: number }>
  qualityChecks: Array<{ name: string; status: string; detail: string; recommendation: string }>
  qualityScore: number
  marketCapabilities: CompetitiveFeatureGroupDto[]
}

export function ReportingStudioView({ packageData, refreshPackages, notify }: { packageData: PackageDto; refreshPackages: () => Promise<void>; notify: (message: string) => void }) {
  const [studio, setStudio] = useState<ReportingStudioDto | null>(null)
  const [settings, setSettings] = useState<ReportingStudioSettings | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    const data = await fetchJson<ReportingStudioDto>(`/api/packages/${packageData.id}/reporting-studio`)
    setStudio(data)
    setSettings(data.settings)
  }, [packageData.id])

  useEffect(() => {
    load().catch(() => {
      setStudio(null)
      setSettings(null)
    })
  }, [load])

  const update = <K extends keyof ReportingStudioSettings>(key: K, value: ReportingStudioSettings[K]) => {
    setSettings((current) => current ? { ...current, [key]: value } : current)
  }

  const toggleSection = (section: string) => {
    setSettings((current) => {
      if (!current) return current
      const exists = current.reportSections.some((item) => item.toLowerCase() === section.toLowerCase())
      return {
        ...current,
        reportSections: exists
          ? current.reportSections.filter((item) => item.toLowerCase() !== section.toLowerCase())
          : [...current.reportSections, section],
      }
    })
  }

  const save = async () => {
    if (!settings) return
    setBusy('save')
    try {
      const saved = await putJson<ReportingStudioSettings>(`/api/packages/${packageData.id}/reporting-studio`, settings)
      setSettings(saved)
      await load()
      notify('Reporting studio settings saved.')
    } finally {
      setBusy(null)
    }
  }

  const apply = async () => {
    if (!settings) return
    setBusy('apply')
    try {
      const result = await postJson<{ created: number }>(`/api/packages/${packageData.id}/reporting-studio/apply`, { sections: settings.reportSections })
      await Promise.all([refreshPackages(), load()])
      notify(result.created === 0 ? 'Reporting package already has those sections.' : `Added ${result.created} reporting sections.`)
    } finally {
      setBusy(null)
    }
  }

  if (!studio || !settings) {
    return (
      <div className="page">
        <div className="empty-state">Loading reporting studio...</div>
      </div>
    )
  }

  const selected = new Set(settings.reportSections.map((item) => item.toLowerCase()))
  const passed = studio.qualityChecks.filter((check) => check.status === 'Pass').length
  const statementLineCount = studio.statementSections.reduce((sum, section) => sum + section.lineCount, 0)
  const booleanOptions: Array<[keyof Pick<ReportingStudioSettings,
    | 'showPriorMonth'
    | 'showPriorYear'
    | 'showBudget'
    | 'showForecast'
    | 'showYtd'
    | 'showRollingTwelve'
    | 'showVarianceDollar'
    | 'showVariancePercent'
    | 'showZeroRows'
    | 'landscapeForWideTables'
    | 'includeFluxNarratives'
    | 'includeLedgerEvidence'
    | 'includeFinalReview'
    | 'includeActionPlan'>, string]> = [
    ['showPriorMonth', 'Prior month'],
    ['showPriorYear', 'Prior year'],
    ['showBudget', 'Budget'],
    ['showForecast', 'Forecast'],
    ['showYtd', 'YTD'],
    ['showRollingTwelve', 'Rolling 12'],
    ['showVarianceDollar', '$ variance'],
    ['showVariancePercent', '% variance'],
    ['showZeroRows', 'Show zero rows'],
    ['landscapeForWideTables', 'Landscape wide tables'],
    ['includeFluxNarratives', 'Flux narratives'],
    ['includeLedgerEvidence', 'Ledger evidence'],
    ['includeFinalReview', 'Final review'],
    ['includeActionPlan', 'Action plan'],
  ]

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Reporting studio</div>
          <h1>{packageData.organizationName} · {packageData.period}</h1>
          <p>Custom report structure, statement behavior, commentary rules, visuals, QA, and package assembly without spreadsheet-style editing.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<Check size={15} />} disabled={busy === 'save'} onClick={save}>
            {busy === 'save' ? 'Saving...' : 'Save settings'}
          </Button>
          <Button variant="primary" icon={<Wand2 size={15} />} disabled={busy === 'apply'} onClick={apply}>
            {busy === 'apply' ? 'Applying...' : 'Apply to package'}
          </Button>
        </div>
      </div>

      <div className="studio-scoreboard">
        <Card>
          <span className="eyebrow">Report readiness</span>
          <h3>{studio.qualityScore}%</h3>
          <p>{passed} of {studio.qualityChecks.length} checks passing</p>
        </Card>
        <Card>
          <span className="eyebrow">Selected sections</span>
          <h3>{settings.reportSections.length}</h3>
          <p>{settings.reportSections.slice(0, 4).join(' · ')}</p>
        </Card>
        <Card>
          <span className="eyebrow">Statement evidence</span>
          <h3>{statementLineCount}</h3>
          <p>{studio.statementSections.length} imported statement sections</p>
        </Card>
        <Card>
          <span className="eyebrow">FS line library</span>
          <h3>{studio.fsLineSections.reduce((sum, section) => sum + section.lineCount, 0)}</h3>
          <p>{studio.fsLineSections.length} active grouped sections</p>
        </Card>
      </div>

      <div className="reporting-studio-grid">
        <Card className="control-card">
          <div className="section-title tight">
            <h2>Report behavior</h2>
            <span className="muted">Saved per package</span>
          </div>
          <div className="field-row">
            <label className="field">
              <span>Style</span>
              <select value={settings.reportStyle} onChange={(event) => update('reportStyle', event.target.value)}>
                <option>Board-ready</option>
                <option>Investor update</option>
                <option>Operating review</option>
                <option>Bank package</option>
              </select>
            </label>
            <label className="field">
              <span>Rounding</span>
              <select value={settings.rounding} onChange={(event) => update('rounding', event.target.value)}>
                <option>Whole dollars</option>
                <option>Nearest thousand</option>
                <option>One decimal</option>
              </select>
            </label>
          </div>
          <label className="field">
            <span>Statement layout</span>
            <select value={settings.statementLayout} onChange={(event) => update('statementLayout', event.target.value)}>
              <option>Grouped financial statements</option>
              <option>Condensed executive statements</option>
              <option>Detailed management statements</option>
              <option>Custom FS line table</option>
            </select>
          </label>
          <label className="field">
            <span>Commentary tone</span>
            <select value={settings.commentaryTone} onChange={(event) => update('commentaryTone', event.target.value)}>
              <option>Direct CFO narrative</option>
              <option>Board concise</option>
              <option>Operational detail</option>
              <option>Lender formal</option>
            </select>
          </label>
          <div className="toggle-grid">
            {booleanOptions.map(([key, label]) => (
              <label key={key} className="check-row">
                <input type="checkbox" checked={settings[key]} onChange={(event) => update(key, event.target.checked)} />
                <span>{label}</span>
              </label>
            ))}
          </div>
          <div className="selected-sections">
            {settings.reportSections.map((section) => (
              <button key={section} className="section-chip" onClick={() => toggleSection(section)}>
                {section}
                <X size={12} />
              </button>
            ))}
          </div>
        </Card>

        <Card className="content-library-card">
          <div className="section-title tight">
            <h2>Content library</h2>
            <span className="muted">Fathom/Syft-style blocks</span>
          </div>
          <div className="content-library-list">
            {studio.contentLibrary.map((group) => (
              <div key={group.name} className="content-library-group">
                <strong>{group.name}</strong>
                <p>{group.description}</p>
                <div className="content-item-grid">
                  {group.items.map((item) => (
                    <button key={`${group.name}-${item.name}`} className={selected.has(item.name.toLowerCase()) ? 'content-item selected' : 'content-item'} onClick={() => toggleSection(item.name)}>
                      <span>{item.kind}</span>
                      <strong>{item.name}</strong>
                      <small>{item.description}</small>
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </Card>
      </div>

      <div className="studio-bottom-grid">
        <Card>
          <div className="section-title tight">
            <h2>Quality checks</h2>
            <span className="muted">{studio.qualityScore}% ready</span>
          </div>
          <div className="quality-list">
            {studio.qualityChecks.map((check) => (
              <div key={check.name} className={`quality-row ${check.status.toLowerCase()}`}>
                {check.status === 'Pass' ? <CheckCircle2 size={16} /> : <AlertTriangle size={16} />}
                <div>
                  <strong>{check.name}</strong>
                  <p>{check.detail}</p>
                  <small>{check.recommendation}</small>
                </div>
              </div>
            ))}
          </div>
        </Card>
        <Card>
          <div className="section-title tight">
            <h2>Market coverage</h2>
            <span className="muted">Competitor parity map</span>
          </div>
          <div className="feature-list">
            {studio.marketCapabilities.flatMap((group) => group.features.slice(0, 2).map((feature) => (
              <div key={`${group.category}-${feature.name}`}>
                <CheckCircle2 size={15} />
                <div>
                  <strong>{feature.name}</strong>
                  <small>{group.category} · {feature.status}</small>
                </div>
              </div>
            )))}
          </div>
        </Card>
      </div>
    </div>
  )
}
