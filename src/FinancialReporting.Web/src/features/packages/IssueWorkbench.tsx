/**
 * IssueWorkbench — extracted from App.tsx.
 *
 * TODO: Once the App.tsx type registry is extracted into a shared types module,
 * remove the inline type re-declarations below (marked with TODO-DEDUPE) and
 * import them from that shared location.
 */

import { postJson } from '../../api/client'
import { Button, SeverityBadge } from '../../components/primitives'
import { Check } from 'lucide-react'

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
type SlideBlockDto = {
  id: string
  sortOrder: number
  kind: string
  contentJson: string
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

// ---------------------------------------------------------------------------

export function IssueWorkbench({ packageData, refreshPackages }: { packageData: PackageDto; refreshPackages: () => Promise<void> }) {
  const openIssues = packageData.issues.filter((issue) => issue.status === 'Open')
  const applyFix = async (issueId: string) => {
    await postJson(`/api/packages/${packageData.id}/issues/${issueId}/apply-fix`, {
      reason: 'Approved from issue workbench',
      comment: 'User accepted AI recommendation',
    })
    await refreshPackages()
  }
  const ignoreIssue = async (issueId: string) => {
    await postJson(`/api/packages/${packageData.id}/issues/${issueId}/ignore`, {
      reason: 'Ignored from issue workbench',
      comment: 'User reviewed and chose to ignore',
    })
    await refreshPackages()
  }

  return (
    <section className="workbench">
      <div className="section-title tight">
        <h2>Final review issues</h2>
        <span className="muted">{openIssues.length} open · rerunnable until clean</span>
      </div>
      {openIssues.length === 0 ? (
        <div className="empty-state">
          <Check size={18} /> No material issues are open.
        </div>
      ) : (
        <div className="issue-list">
          {openIssues.map((issue) => (
            <div className="issue-row" key={issue.id}>
              <SeverityBadge severity={issue.severity} />
              <div className="issue-body">
                <strong>{issue.title}</strong>
                <p>{issue.description}</p>
                <small>
                  {issue.category} · confidence {(issue.confidence * 100).toFixed(0)}%
                </small>
              </div>
              <Button variant="primary" icon={<Check size={14} />} onClick={() => applyFix(issue.id)}>
                Apply fix
              </Button>
              <Button variant="ghost" onClick={() => ignoreIssue(issue.id)}>Ignore</Button>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
