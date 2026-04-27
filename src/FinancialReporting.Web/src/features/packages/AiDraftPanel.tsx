/**
 * AiDraftPanel — extracted from App.tsx.
 *
 * TODO: Once the App.tsx type registry is extracted into a shared types module,
 * remove the inline type re-declarations below (marked with TODO-DEDUPE) and
 * import them from that shared location.
 */

import { useCallback, useEffect, useState } from 'react'
import { fetchJson, postJson } from '../../api/client'
import { Button, SeverityBadge } from '../../components/primitives'
import { Wand2 } from 'lucide-react'

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

/** TODO: dedupe with App.tsx type registry */
type AiPackageDraft = {
  id: string
  reportPackageId: string
  status: string
  kind: string
  title: string
  description: string
  createdAt: string
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

/** TODO: dedupe with App.tsx helper */
function formatRelative(value: string | null) {
  if (!value) return '—'
  const minutes = Math.max(1, Math.round((Date.now() - new Date(value).getTime()) / 60000))
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} hr ago`
}

// ---------------------------------------------------------------------------

export function AiDraftPanel({ packageData, refreshPackages }: { packageData: PackageDto; refreshPackages: () => Promise<void> }) {
  const [drafts, setDrafts] = useState<AiPackageDraft[]>([])
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    const next = await fetchJson<AiPackageDraft[]>(`/api/packages/${packageData.id}/ai-package-drafts`)
    setDrafts(next)
  }, [packageData.id])

  useEffect(() => {
    let active = true
    fetchJson<AiPackageDraft[]>(`/api/packages/${packageData.id}/ai-package-drafts`)
      .then((next) => {
        if (active) setDrafts(next)
      })
      .catch(() => {
        if (active) setDrafts([])
      })
    return () => {
      active = false
    }
  }, [packageData.id])

  const createDrafts = async () => {
    setBusy('create')
    try {
      const next = await postJson<AiPackageDraft[]>(`/api/packages/${packageData.id}/ai-package-draft`, {})
      setDrafts(next)
    } finally {
      setBusy(null)
    }
  }

  const accept = async (id: string) => {
    setBusy(id)
    try {
      await postJson(`/api/ai-package-drafts/${id}/accept`, {})
      await Promise.all([load(), refreshPackages()])
    } finally {
      setBusy(null)
    }
  }

  const reject = async (id: string) => {
    setBusy(id)
    try {
      await postJson(`/api/ai-package-drafts/${id}/reject`, { reason: 'Rejected from staged draft review' })
      await load()
    } finally {
      setBusy(null)
    }
  }

  const staged = drafts.filter((draft) => draft.status === 'Staged')
  return (
    <section className="workbench">
      <div className="section-title tight">
        <div>
          <h2>AI package drafts</h2>
          <span className="muted">Staged suggestions only · existing slides stay untouched until accepted</span>
        </div>
        <Button variant="accent" icon={<Wand2 size={15} />} disabled={busy === 'create'} onClick={createDrafts}>Draft from flux</Button>
      </div>
      {staged.length === 0 ? (
        <div className="empty-state">No staged AI package suggestions yet.</div>
      ) : (
        <div className="issue-list">
          {staged.slice(0, 5).map((draft) => (
            <div className="issue-row" key={draft.id}>
              <SeverityBadge severity="Low" />
              <div className="issue-body">
                <strong>{draft.title}</strong>
                <p>{draft.description}</p>
                <small>{draft.kind} · {formatRelative(draft.createdAt)}</small>
              </div>
              <Button variant="primary" disabled={busy === draft.id} onClick={() => accept(draft.id)}>Accept</Button>
              <Button variant="ghost" disabled={busy === draft.id} onClick={() => reject(draft.id)}>Reject</Button>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
