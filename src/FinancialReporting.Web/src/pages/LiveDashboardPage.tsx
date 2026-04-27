import { Sparkles } from 'lucide-react'
import { Sparkline } from '../components/primitives'

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

// TODO: dedupe — mirrors fmtMoney in App.tsx
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

// TODO: dedupe — mirrors parseJson in App.tsx
function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

export function LiveDashboard({ packageData, openSlide }: { packageData: PackageDto; openSlide: (id: string) => void }) {
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Live dashboard</div>
          <h1>{packageData.organizationName}</h1>
          <p>Live KPI strip, alerts, and board-package drill-throughs.</p>
        </div>
      </div>
      <div className="alert-strip">
        <Sparkles size={16} /> {packageData.issues.filter((x) => x.status === 'Open').length} active alerts from the current package review.
      </div>
      <div className="live-grid">
        {packageData.slides.map((slide) => (
          <button key={slide.id} className="live-card" onClick={() => openSlide(slide.id)}>
            <span>{slide.subject}</span>
            <strong>{fmtMoney(slide.currentValue)}</strong>
            <Sparkline current={parseJson<number[]>(slide.monthlyJson, [])} />
          </button>
        ))}
      </div>
    </div>
  )
}
