/**
 * DashboardPage — extracted from App.tsx.
 *
 * TODO: Once the App.tsx type registry is extracted into a shared types module,
 * remove the inline type re-declarations below (marked with TODO-DEDUPE) and
 * import them from that shared location.
 */

import { Button, Card, SeverityBadge, Sparkline } from '../components/primitives'
import { FluxReviewPanel } from '../features/flux/FluxReviewPanel'
import { AiDraftPanel } from '../features/packages/AiDraftPanel'
import { IssueWorkbench } from '../features/packages/IssueWorkbench'
import {
  Download,
  FileText,
  RefreshCw,
  Sparkles,
  Wand2,
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

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

/** TODO: dedupe with App.tsx helper */
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

/** TODO: dedupe with App.tsx helper */
function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

/** TODO: dedupe with App.tsx helper */
function pluralize(count: number, noun: string) {
  return `${count} ${noun}${count === 1 ? '' : 's'}`
}

/** TODO: dedupe with App.tsx helper */
function splitAccountCodes(value: string) {
  return value
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)
}

/** TODO: dedupe with App.tsx helper */
function formatRelative(value: string | null) {
  if (!value) return '—'
  const minutes = Math.max(1, Math.round((Date.now() - new Date(value).getTime()) / 60000))
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} hr ago`
}

/** TODO: dedupe with App.tsx helper */
function countSeverity(issues: IssueDto[], severity: string) {
  return issues.filter((issue) => issue.severity === severity).length
}

// ---------------------------------------------------------------------------
// Sub-component (private to this file)
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

export function Dashboard({
  packageData,
  aiRuns,
  openSlide,
  recompile,
  runFinalReview,
  refreshPackages,
}: {
  packageData: PackageDto
  aiRuns: AiRun[]
  openSlide: (id: string) => void
  recompile: () => void
  runFinalReview: () => void
  refreshPackages: () => Promise<void>
}) {
  const openIssues = packageData.issues.filter((issue) => issue.status === 'Open')
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Live Board Package</div>
          <h1>
            {packageData.organizationName} · {packageData.period}
          </h1>
          <p>
            Based on {packageData.baseFrom} · Xero last synced {formatRelative(packageData.lastXeroSyncAt)} · {packageData.versionLabel}
          </p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<RefreshCw size={15} />} onClick={recompile}>
            Recompile from Xero
          </Button>
          <Button variant="accent" icon={<Sparkles size={15} />} onClick={runFinalReview}>
            Final AI Review
          </Button>
          <Button variant="primary" icon={<Download size={15} />}>
            Export Board PDF
          </Button>
        </div>
      </div>

      {(packageData.isSourceDataStale || packageData.blockReason) && (
        <div className="alert-strip warn">
          <RefreshCw size={16} /> {packageData.blockReason ?? packageData.sourceDataStaleReason ?? 'New Xero ledger activity was imported after this package was built.'}
        </div>
      )}

      <div className="stat-grid">
        <StatCard label="Slides" value={packageData.slides.length.toString()} sub="Board package content" />
        <StatCard label="Open Issues" value={openIssues.length === 0 ? 'All clear' : openIssues.length.toString()} sub={`${countSeverity(openIssues, 'Critical')} crit · ${countSeverity(openIssues, 'High')} high · ${countSeverity(openIssues, 'Medium')} med`} tone={openIssues.length === 0 ? 'good' : 'warn'} />
        <StatCard label="Status" value={packageData.status} sub={packageData.versionLabel} />
        <StatCard label="Codex Jobs" value={aiRuns[0]?.status ?? 'Idle'} sub={aiRuns[0] ? `${aiRuns[0].module} · ${aiRuns[0].progress}%` : 'No active run'} />
      </div>

      <FluxReviewPanel packageData={packageData} refreshPackages={refreshPackages} />
      <AiDraftPanel packageData={packageData} refreshPackages={refreshPackages} />

      <div className="section-title">
        <h2>Package slides</h2>
        <div className="actions">
          <Button variant="ghost" icon={<FileText size={15} />}>
            Add blank slide
          </Button>
          <Button variant="accent" icon={<Wand2 size={15} />}>
            Ask AI to add a slide
          </Button>
        </div>
      </div>
      <div className="slide-grid">
        {packageData.slides.map((slide) => (
          <button key={slide.id} className="slide-card" onClick={() => openSlide(slide.id)}>
            <div>
              <div className="slide-card-heading">
                <span className="mono">0{slide.sortOrder}</span>
                <strong>{slide.subject}</strong>
                {packageData.issues.some((issue) => issue.packageSlideId === slide.id && issue.status === 'Open') && <SeverityBadge severity="Medium" />}
              </div>
              <div className="metric-row">
                <span className="metric">{fmtMoney(slide.currentValue)}</span>
                <span className={slide.varianceAmount >= 0 ? 'good-text mono' : 'bad-text mono'}>
                  {slide.varianceAmount >= 0 ? '▲' : '▼'} {fmtMoney(Math.abs(slide.varianceAmount))} ({Math.abs(slide.variancePercent).toFixed(1)}%)
                </span>
              </div>
              <small>{slide.blocks.length} blocks · {pluralize(splitAccountCodes(slide.accountCodesCsv).length, 'GL acct')}</small>
            </div>
            <Sparkline current={parseJson<number[]>(slide.monthlyJson, [])} prior={parseJson<number[]>(slide.priorMonthlyJson, [])} />
          </button>
        ))}
      </div>

      <IssueWorkbench packageData={packageData} refreshPackages={refreshPackages} />
    </div>
  )
}
