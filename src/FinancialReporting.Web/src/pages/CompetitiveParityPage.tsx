// TODO dedupe: PackageDto, CompetitiveFeatureGroupDto, ReportTemplateDto, SlideDto, SlideBlockDto,
//              IssueDto types are also declared in App.tsx
import { useEffect, useState } from 'react'
import { fetchJson, postJson } from '../api/client'
import { Button, Card } from '../components/primitives'
import { CheckCircle2, LayoutGrid } from 'lucide-react'

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
type ReportTemplateDto = {
  id: string
  name: string
  category: string
  description: string
  sections: string[]
  isBuiltIn: boolean
}

export function CompetitiveParityView({ packageData, refreshPackages }: { packageData: PackageDto | null; refreshPackages: () => Promise<void> }) {
  const [groups, setGroups] = useState<CompetitiveFeatureGroupDto[]>([])
  const [templates, setTemplates] = useState<ReportTemplateDto[]>([])
  const [busy, setBusy] = useState<string | null>(null)

  useEffect(() => {
    Promise.all([
      fetchJson<CompetitiveFeatureGroupDto[]>('/api/competitive-gaps'),
      fetchJson<ReportTemplateDto[]>('/api/report-templates'),
    ])
      .then(([nextGroups, nextTemplates]) => {
        setGroups(nextGroups)
        setTemplates(nextTemplates)
      })
      .catch(() => {
        setGroups([])
        setTemplates([])
      })
  }, [])

  const applyTemplate = async (templateId: string) => {
    if (!packageData) return
    setBusy(templateId)
    try {
      await postJson(`/api/packages/${packageData.id}/apply-template`, { templateId })
      await refreshPackages()
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Competitive parity</div>
          <h1>Fathom · Reach · competitor gap matrix</h1>
          <p>Feature coverage now tracks reporting, analysis, planning, and consolidation parity against the market.</p>
        </div>
      </div>
      <div className="parity-grid">
        {groups.map((group) => (
          <Card key={group.category} className="parity-card">
            <span className="eyebrow">{group.category}</span>
            <h3>{group.competitorPattern}</h3>
            <div className="feature-list">
              {group.features.map((feature) => (
                <div key={feature.name}>
                  <CheckCircle2 size={15} />
                  <strong>{feature.name}</strong>
                  <span className={feature.status === 'Implemented' ? 'good-text' : 'warn-text'}>{feature.status}</span>
                  <small>{feature.ourImplementation}</small>
                </div>
              ))}
            </div>
          </Card>
        ))}
      </div>
      <div className="section-title">
        <h2>Template library</h2>
        <span className="muted">{templates.length} built-in packs</span>
      </div>
      <div className="template-grid">
        {templates.map((template) => (
          <Card key={template.id} className="template-card">
            <span className="eyebrow">{template.category}</span>
            <h3>{template.name}</h3>
            <p>{template.description}</p>
            <div className="template-sections">
              {template.sections.slice(0, 5).map((section) => <span key={section}>{section}</span>)}
            </div>
            <Button variant="primary" icon={<LayoutGrid size={15} />} disabled={!packageData || busy === template.id} onClick={() => applyTemplate(template.id)}>
              Apply
            </Button>
          </Card>
        ))}
      </div>
    </div>
  )
}
