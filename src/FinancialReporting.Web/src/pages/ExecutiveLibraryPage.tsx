// TODO dedupe: PackageDto, IssueDto, SlideDto, SlideBlockDto types are also declared in App.tsx
import { Card } from '../components/primitives'

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

export function ExecutiveLibrary({ packageData }: { packageData: PackageDto }) {
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Executive reporting library</div>
          <h1>{packageData.organizationName}</h1>
          <p>Entity-level packages, statements, KPI history, flux explanations, exports, and QA summaries.</p>
        </div>
      </div>
      <div className="settings-home-grid">
        <Card><div className="eyebrow">Monthly package</div><h3>{packageData.period}</h3><p>{packageData.status} · {packageData.slides.length} slides</p></Card>
        <Card><div className="eyebrow">Final review</div><h3>{packageData.issues.filter((issue) => issue.status === 'Open').length} open</h3><p>AI issue workbench and approved fixes.</p></Card>
        <Card><div className="eyebrow">Flux</div><h3>Variance explanations</h3><p>Completed explanations feed financial package drafting.</p></Card>
        <Card><div className="eyebrow">Exports</div><h3>PDF / Excel</h3><p>Latest artifacts are available under Share & export.</p></Card>
      </div>
    </div>
  )
}
