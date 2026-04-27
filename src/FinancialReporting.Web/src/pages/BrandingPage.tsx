import { useState } from 'react'
import { putJson } from '../api/client'
import { Check, Download } from 'lucide-react'
import { Button, Card } from '../components/primitives'

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

// TODO: dedupe — mirrors parseJson in App.tsx
function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

export function BrandingView({ packageData, refreshPackages, notify }: { packageData: PackageDto; refreshPackages: () => Promise<void>; notify: (message: string) => void }) {
  const theme = parseJson<{ primary?: string; accent?: string; fontFamily?: string; coverStyle?: string; headerText?: string; footerText?: string }>(packageData.themeJson, {})
  const [primary, setPrimary] = useState(theme.primary ?? '#0F2A4A')
  const [accent, setAccent] = useState(theme.accent ?? '#6B4FA8')
  const [fontFamily, setFontFamily] = useState(theme.fontFamily ?? 'Inter')
  const [coverStyle, setCoverStyle] = useState(theme.coverStyle ?? 'modern')
  const [headerText, setHeaderText] = useState(theme.headerText ?? packageData.organizationName)
  const [footerText, setFooterText] = useState(theme.footerText ?? 'Confidential financial reporting')

  const save = async () => {
    await putJson(`/api/packages/${packageData.id}/theme`, {
      primary,
      accent,
      logoFileName: `${packageData.organizationAbbreviation.toLowerCase()}-logo.svg`,
      fontFamily,
      coverStyle,
      pageOrder: ['Cover', 'Executive Summary', ...packageData.slides.map((slide) => slide.subject), 'QA Issues', 'Appendix'],
      headerText,
      footerText,
      exportSettings: { includeIssues: true, includeAppendix: true },
    })
    await refreshPackages()
    notify('Branding and layout settings saved.')
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Brand & theme</div>
          <h1>Branding · {packageData.organizationName}</h1>
          <p>Logo, colors, fonts, and cover-page configuration for exports and shared links.</p>
        </div>
        <Button variant="primary" icon={<Check size={15} />} onClick={save}>
          Save brand
        </Button>
      </div>
      <div className="brand-layout">
        <Card className="control-card">
          <label className="field">
            <span>Primary</span>
            <input type="color" value={primary} onChange={(event) => setPrimary(event.target.value)} />
          </label>
          <label className="field">
            <span>Accent</span>
            <input type="color" value={accent} onChange={(event) => setAccent(event.target.value)} />
          </label>
          <label className="field">
            <span>Font</span>
            <input value={fontFamily} onChange={(event) => setFontFamily(event.target.value)} />
          </label>
          <label className="field">
            <span>Cover style</span>
            <select value={coverStyle} onChange={(event) => setCoverStyle(event.target.value)}>
              <option value="modern">Modern</option>
              <option value="classic">Classic</option>
              <option value="executive">Executive</option>
            </select>
          </label>
          <label className="field">
            <span>Header</span>
            <input value={headerText} onChange={(event) => setHeaderText(event.target.value)} />
          </label>
          <label className="field">
            <span>Footer</span>
            <input value={footerText} onChange={(event) => setFooterText(event.target.value)} />
          </label>
          <Button variant="secondary" icon={<Download size={15} />}>
            Upload logo
          </Button>
        </Card>
        <Card className="cover-preview">
          <div className="cover-mark">{packageData.organizationAbbreviation}</div>
          <div>
            <span>{packageData.period}</span>
            <h2>{packageData.organizationName}</h2>
            <p>Confidential financial reporting · Internal use only</p>
          </div>
        </Card>
      </div>
    </div>
  )
}
