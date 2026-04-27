import { useState } from 'react'
import { API_BASE, postJson } from '../api/client'
import { Clock3, FileSpreadsheet, FileText, Share2 } from 'lucide-react'
import { Button } from '../components/primitives'

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

// TODO: dedupe — mirrors ExportArtifact in App.tsx
type ExportArtifact = {
  id: string
  type: string
  status: string
  fileName: string
  downloadUrl: string
}

// TODO: dedupe — mirrors ActionTile in App.tsx
function ActionTile({ icon, title, text, onClick }: { icon: React.ReactNode; title: string; text: string; onClick: () => void }) {
  return (
    <button className="action-tile" onClick={onClick}>
      <span>{icon}</span>
      <strong>{title}</strong>
      <p>{text}</p>
    </button>
  )
}

export function OutputView({ packageData }: { packageData: PackageDto }) {
  const [result, setResult] = useState<string | null>(null)
  const [artifacts, setArtifacts] = useState<ExportArtifact[]>([])
  const call = async (path: string, body: unknown) => {
    const response = await postJson<ExportArtifact & { token?: string }>(path, body)
    if (response.downloadUrl) {
      setArtifacts((current) => [response, ...current].slice(0, 5))
    }
    setResult(response.token ? `/share/${response.token}` : response.downloadUrl ?? response.id ?? 'Queued')
  }
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Output</div>
          <h1>Share & export</h1>
          <p>Generate PDF, Excel, shared dashboard links, and scheduled distribution packages.</p>
        </div>
      </div>
      <div className="output-grid">
        <ActionTile icon={<FileText />} title="Board PDF" text="Package pages, issue summary, appendix." onClick={() => call('/api/exports/pdf', { reportPackageId: packageData.id, includeIssues: true, includeAppendix: true })} />
        <ActionTile icon={<FileSpreadsheet />} title="Excel workbook" text="Trial balance, mappings, KPI data, QA tabs." onClick={() => call('/api/exports/excel', { reportPackageId: packageData.id, includeIssues: true, includeAppendix: true })} />
        <ActionTile icon={<Share2 />} title="Share link" text="Secure browser dashboard with optional downloads." onClick={() => call('/api/share-links', { reportPackageId: packageData.id, requirePassword: true, allowDownload: true })} />
        <ActionTile icon={<Clock3 />} title="Schedule" text="Monthly delivery after package finalization." onClick={() => call('/api/distribution-schedules', { reportPackageId: packageData.id, recipients: ['finance@example.com'], cadence: 'Monthly', includePdf: true, includeExcel: true })} />
      </div>
      {result && <div className="empty-state">Ready: <span className="mono">{result}</span></div>}
      {artifacts.length > 0 && (
        <div className="issue-list">
          {artifacts.map((artifact) => (
            <div className="issue-row" key={artifact.id}>
              <FileText size={16} />
              <div className="issue-body">
                <strong>{artifact.fileName}</strong>
                <p>{artifact.type} · {artifact.status}</p>
              </div>
              <Button variant="primary" onClick={() => window.open(`${API_BASE}${artifact.downloadUrl}`, '_blank')}>Open</Button>
              <Button variant="ghost" onClick={() => postJson(`/api/exports/${artifact.id}/qa`, { reportPackageId: packageData.id }).then(() => setResult(`QA complete for ${artifact.fileName}`))}>QA</Button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
