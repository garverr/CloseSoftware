import { Card } from '../components/primitives'

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

export function LayoutsView({ packageData }: { packageData: PackageDto }) {
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Package assembly</div>
          <h1>Layouts & page order</h1>
          <p>Arrange cover, contents, slide sections, appendix, issue summary, and export footers.</p>
        </div>
      </div>
      <div className="layout-list">
        {['Cover', 'Executive Summary', ...packageData.slides.map((s) => s.subject), 'QA Issues', 'Appendix'].map((name, index) => (
          <Card key={name} className="layout-row">
            <span className="mono">{String(index + 1).padStart(2, '0')}</span>
            <strong>{name}</strong>
            <span>{index === 0 ? 'Cover template' : 'Board package page'}</span>
          </Card>
        ))}
      </div>
    </div>
  )
}
