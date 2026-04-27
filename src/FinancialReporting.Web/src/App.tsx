import { useCallback, useEffect, useState } from 'react'
import { API_BASE, fetchJson, postJson } from './api/client'
import { Button } from './components/primitives'
import { CommandPalette } from './components/CommandPalette'
import { FluxReviewPanel } from './features/flux/FluxReviewPanel'
import { AccountPanel, MappingView } from './features/mapping/MappingView'
import { SlideEditor } from './features/packages/SlideEditor'
import { BenchmarkingView } from './pages/BenchmarkingPage'
import { BrandingView } from './pages/BrandingPage'
import { CompetitiveParityView } from './pages/CompetitiveParityPage'
import { Dashboard } from './pages/DashboardPage'
import { EliminationsView } from './pages/EliminationsPage'
import { ExecutiveLibrary } from './pages/ExecutiveLibraryPage'
import { FsLibraryView } from './pages/FsLibraryPage'
import { KpiLibrary } from './pages/KpisPage'
import { LayoutsView } from './pages/LayoutsPage'
import { LiveDashboard } from './pages/LiveDashboardPage'
import { OutputView } from './pages/OutputPage'
import { PlanningView } from './pages/PlanningPage'
import { ReportingStudioView } from './pages/ReportingStudioPage'
import { AiSettings } from './pages/AiSettingsPage'
import { StatementsView } from './pages/StatementsPage'
import { XeroSettings } from './pages/XeroSettingsPage'
import {
  AlertTriangle,
  Bot,
  CheckCircle2,
  Clock3,
  Database,
  FileText,
  History,
  LayoutGrid,
  LineChart,
  Map as MapIcon,
  MessageSquare,
  Paintbrush,
  PlugZap,
  Plus,
  Search,
  Send,
  Settings,
  Share2,
  X,
} from 'lucide-react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import './App.css'

// API_BASE moved to api/client.ts and imported above.

type View =
  | 'dashboard'
  | 'slide'
  | 'planning'
  | 'benchmarks'
  | 'mapping'
  | 'eliminations'
  | 'fslibrary'
  | 'flux'
  | 'report-studio'
  | 'statements'
  | 'library'
  | 'kpis'
  | 'branding'
  | 'layouts'
  | 'output'
  | 'livedash'
  | 'settings'
  | 'ai-settings'
  | 'xero-settings'
  | 'parity'

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

type OrganizationOption = {
  id: string
  key: string
  name: string
  abbreviation: string
  isConsolidated: boolean
  isXeroMapped: boolean
}

type PeriodOption = {
  id: string
  key: string
  label: string
  periodStart: string
  periodEnd: string
  isClosed: boolean
  packageCount: number
  ledgerActivityCount: number
}

type PackageOption = {
  id: string
  organizationKey: string
  organizationName: string
  periodKey: string
  periodLabel: string
  status: string
}

type ReportingCoverage = {
  organizationKey: string
  periodKey: string
  packageId: string | null
  packageStatus: string | null
  ledgerActivityCount: number
}

type ReportingContext = {
  organizations: OrganizationOption[]
  periods: PeriodOption[]
  packages: PackageOption[]
  coverage: ReportingCoverage[]
}

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

type SlideBlockDto = {
  id: string
  sortOrder: number
  kind: string
  contentJson: string
}

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

type PackageVersion = {
  id: string
  versionLabel: string
  createdBy: string
  changeSummary: string
  createdAt: string
}









type Panel =
  | { type: 'chat'; slideId: string; blockId?: string }
  | { type: 'history'; slideId: string }
  | { type: 'account'; accountId: string }
  | null


const AI_PROGRESS_STEPS = [
  { progress: 0, label: 'Queued' },
  { progress: 12, label: 'Preparing request' },
  { progress: 35, label: 'Running analysis' },
  { progress: 70, label: 'Checking response' },
  { progress: 85, label: 'Applying results' },
  { progress: 100, label: 'Done' },
]

const emptyReportingContext: ReportingContext = {
  organizations: [],
  periods: [],
  packages: [],
  coverage: [],
}

function emptyPackageForSelection(context: ReportingContext, organizationKey: string, periodKey: string): PackageDto {
  const organization = context.organizations.find((item) => item.key === organizationKey) ?? context.organizations[0]
  const period = context.periods.find((item) => item.key === periodKey) ?? context.periods[0]
  return {
    id: '',
    organizationId: organization?.id ?? '',
    reportingPeriodId: period?.id ?? '',
    organizationKey: organization?.key ?? '',
    organizationName: organization?.name ?? 'No entity selected',
    organizationAbbreviation: organization?.abbreviation ?? '',
    periodKey: period?.key ?? '',
    period: period?.label ?? 'No Xero period',
    status: 'Not started',
    versionLabel: 'No package yet',
    baseFrom: '—',
    lastXeroSyncAt: null,
    isSourceDataStale: false,
    sourceDataStaleReason: null,
    sourceDataChangedAt: null,
    blockReason: null,
    themeJson: JSON.stringify({ primary: '#0F2A4A', accent: '#6B4FA8' }),
    slides: [],
    issues: [],
  }
}

function App() {
  const [packages, setPackages] = useState<PackageDto[]>([])
  const [reportingContext, setReportingContext] = useState<ReportingContext>(emptyReportingContext)
  const [selectedOrganizationKey, setSelectedOrganizationKey] = useState('')
  const [selectedPeriodKey, setSelectedPeriodKey] = useState('')
  const [selectedPackageId, setSelectedPackageId] = useState<string>('')
  const [view, setView] = useState<View>('dashboard')
  const [selectedSlideId, setSelectedSlideId] = useState<string>('')
  const [panel, setPanel] = useState<Panel>(null)
  const [aiRuns, setAiRuns] = useState<AiRun[]>([])
  const [toast, setToast] = useState<string | null>(null)
  const [mappingRefreshKey, setMappingRefreshKey] = useState(0)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [paletteOpen, setPaletteOpen] = useState(false)

  const selectedPackageByContext = packages.find((p) => p.organizationKey === selectedOrganizationKey && p.periodKey === selectedPeriodKey)
  const selectedPackageById = packages.find((p) => p.id === selectedPackageId)
  const selectedPackage =
    selectedPackageByContext
    ?? (!selectedOrganizationKey && !selectedPeriodKey ? selectedPackageById : undefined)
    ?? emptyPackageForSelection(reportingContext, selectedOrganizationKey, selectedPeriodKey)
  const hasSelectedPackage = selectedPackage.id.length > 0
  const selectedSlide = selectedPackage.slides.find((s) => s.id === selectedSlideId) ?? selectedPackage.slides[0]
  const theme = parseJson<{ primary?: string; accent?: string }>(selectedPackage.themeJson, {})

  const refreshPackages = useCallback(async () => {
    const [data, context] = await Promise.all([
      fetchJson<PackageDto[]>('/api/packages'),
      fetchJson<ReportingContext>(selectedOrganizationKey ? `/api/reporting-context?organizationKey=${encodeURIComponent(selectedOrganizationKey)}` : '/api/reporting-context'),
    ])
    setPackages(data)
    setReportingContext(context)
    setLoadError(null)
    const currentOrg = context.organizations.find((item) => item.key === selectedOrganizationKey)
    const nextOrg = currentOrg ?? context.organizations.find((item) => item.isXeroMapped && !item.isConsolidated) ?? context.organizations[0]
    const currentPeriod = context.periods.find((item) => item.key === selectedPeriodKey)
    const nextPeriod = currentPeriod ?? context.periods[0]
    if (!selectedOrganizationKey && nextOrg) setSelectedOrganizationKey(nextOrg.key)
    if (!selectedPeriodKey && nextPeriod) setSelectedPeriodKey(nextPeriod.key)
    setSelectedPackageId((current) => {
      if (data.some((item) => item.id === current)) return current
      const matching = data.find((item) => item.organizationKey === (nextOrg?.key ?? selectedOrganizationKey) && item.periodKey === (nextPeriod?.key ?? selectedPeriodKey))
      return matching?.id ?? ''
    })
  }, [selectedOrganizationKey, selectedPeriodKey])

  useEffect(() => {
    let cancelled = false
    async function loadInitialContext() {
      const [data, initialContext] = await Promise.all([fetchJson<PackageDto[]>('/api/packages'), fetchJson<ReportingContext>('/api/reporting-context')])
      const firstOrg = initialContext.organizations.find((item) => item.isXeroMapped && !item.isConsolidated) ?? initialContext.organizations[0]
      const context = firstOrg
        ? await fetchJson<ReportingContext>(`/api/reporting-context?organizationKey=${encodeURIComponent(firstOrg.key)}`)
        : initialContext
      if (cancelled) return
      const firstPackage = data.find((item) => item.organizationKey === firstOrg?.key) ?? data[0]
      const firstPeriod = context.periods.find((item) => item.key === firstPackage?.periodKey) ?? context.periods[0]
      setReportingContext(context)
        setPackages(data)
      setSelectedOrganizationKey(firstPackage?.organizationKey ?? firstOrg?.key ?? '')
      setSelectedPeriodKey(firstPackage?.periodKey ?? firstPeriod?.key ?? '')
      setSelectedPackageId(firstPackage?.id ?? '')
      setSelectedSlideId(firstPackage?.slides[0]?.id ?? '')
      setLoadError(null)
    }

    loadInitialContext()
      .catch(() => setLoadError('Could not reach the API. Start the backend to load live Xero reporting data.'))
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      const isModifier = event.metaKey || event.ctrlKey
      if (isModifier && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        setPaletteOpen((current) => !current)
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [])

  useEffect(() => {
    const connection = new HubConnectionBuilder().withUrl(`${API_BASE}/hubs/ai`).withAutomaticReconnect().build()
    connection.on('aiRunUpdated', (run: AiRun) => {
      setAiRuns((current) => [run, ...current.filter((x) => x.id !== run.id)].slice(0, 8))
      if (run.status === 'Completed') {
        refreshPackages().catch(() => undefined)
      }
    })
    connection.start().catch(() => undefined)
    return () => {
      connection.stop().catch(() => undefined)
    }
  }, [refreshPackages])

  const openSlide = (id: string) => {
    setSelectedSlideId(id)
    setView('slide')
    setPanel(null)
  }

  const selectReportingContext = (organizationKey: string, periodKey: string) => {
    const match = packages.find((item) => item.organizationKey === organizationKey && item.periodKey === periodKey)
    setSelectedOrganizationKey(organizationKey)
    setSelectedPeriodKey(periodKey)
    setSelectedPackageId(match?.id ?? '')
    setSelectedSlideId(match?.slides[0]?.id ?? '')
    if (!match && view === 'slide') {
      setView('dashboard')
    }
    setPanel(null)
    fetchJson<ReportingContext>(`/api/reporting-context?organizationKey=${encodeURIComponent(organizationKey)}`)
      .then((context) => {
        setReportingContext(context)
        if (!context.periods.some((period) => period.key === periodKey) && context.periods[0]) {
          const nextPeriod = context.periods[0].key
          const nextMatch = packages.find((item) => item.organizationKey === organizationKey && item.periodKey === nextPeriod)
          setSelectedPeriodKey(nextPeriod)
          setSelectedPackageId(nextMatch?.id ?? '')
          setSelectedSlideId(nextMatch?.slides[0]?.id ?? '')
        }
      })
      .catch(() => undefined)
  }

  const createSelectedPackage = async () => {
    const next = await postJson<PackageDto>('/api/packages/ensure', {
      organizationKey: selectedOrganizationKey,
      periodKey: selectedPeriodKey,
      baseFrom: null,
    })
    setPackages((current) => [next, ...current.filter((item) => item.id !== next.id)])
    setSelectedPackageId(next.id)
    setSelectedSlideId(next.slides[0]?.id ?? '')
    await refreshPackages()
    setToast(`Created ${next.organizationName} package for ${next.period}.`)
  }

  const runFinalReview = async () => {
    if (!hasSelectedPackage) return
    const run = await postJson<AiRun>(`/api/packages/${selectedPackage.id}/final-review`, {})
    setAiRuns((current) => [run, ...current])
    setToast('Final AI review queued.')
  }

  const recompile = async () => {
    if (!hasSelectedPackage) return
    await postJson(`/api/packages/${selectedPackage.id}/recompile`, {})
    await refreshPackages()
    setToast('Package recompiled from Xero data.')
  }

  return (
    <div
      className="app"
      style={
        {
          '--primary': theme.primary ?? '#0F2A4A',
          '--accent': theme.accent ?? '#6B4FA8',
        } as React.CSSProperties
      }
    >
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <TopBar
        key={`${selectedOrganizationKey}:${selectedPeriodKey}`}
        reportingContext={reportingContext}
        selectedOrganizationKey={selectedOrganizationKey}
        selectedPeriodKey={selectedPeriodKey}
        onSelect={selectReportingContext}
        selectedPackage={selectedPackage}
        onOpenPalette={() => setPaletteOpen(true)}
      />
      <main className="workspace" id="main-content" aria-label="Main workspace">
        <Sidebar
          packageData={selectedPackage}
          view={view}
          selectedSlideId={selectedSlideId}
          openSlide={openSlide}
          setView={setView}
          hasPackage={hasSelectedPackage}
        />
        <section className={panel ? 'content with-panel' : 'content'}>
          {loadError && <ApiErrorState message={loadError} />}
          {!loadError && view === 'settings' && <SettingsHome setView={setView} />}
          {!loadError && view === 'ai-settings' && <AiSettings />}
          {!loadError && view === 'xero-settings' && <XeroSettings notify={setToast} />}
          {!loadError && !hasSelectedPackage && ['dashboard', 'slide', 'planning', 'benchmarks', 'flux', 'report-studio', 'library', 'kpis', 'branding', 'layouts', 'output', 'livedash'].includes(view) && (
            <PackagePlaceholder packageData={selectedPackage} createPackage={createSelectedPackage} />
          )}
          {!loadError && hasSelectedPackage && view === 'dashboard' && (
            <Dashboard
              packageData={selectedPackage}
              aiRuns={aiRuns}
              openSlide={openSlide}
              recompile={recompile}
              runFinalReview={runFinalReview}
              refreshPackages={refreshPackages}
            />
          )}
          {!loadError && hasSelectedPackage && view === 'slide' && selectedSlide && (
            <SlideEditor
              key={selectedSlide.id}
              packageData={selectedPackage}
              slide={selectedSlide}
              issues={selectedPackage.issues.filter((x) => x.packageSlideId === selectedSlide.id)}
              refreshPackages={refreshPackages}
              notify={setToast}
              openChat={(blockId) => setPanel({ type: 'chat', slideId: selectedSlide.id, blockId })}
              openHistory={() => setPanel({ type: 'history', slideId: selectedSlide.id })}
            />
          )}
          {!loadError && view === 'mapping' && <MappingView organizationKey={selectedOrganizationKey} periodKey={selectedPeriodKey} refreshKey={mappingRefreshKey} openAccount={(accountId) => setPanel({ type: 'account', accountId })} />}
          {!loadError && view === 'fslibrary' && <FsLibraryView organizationKey={selectedOrganizationKey} />}
          {!loadError && view === 'eliminations' && <EliminationsView organizationKey={selectedOrganizationKey} periodKey={selectedPeriodKey} />}
          {!loadError && hasSelectedPackage && view === 'planning' && <PlanningView packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'benchmarks' && <BenchmarkingView packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'flux' && <FluxReviewPanel packageData={selectedPackage} refreshPackages={refreshPackages} standalone onAiRunQueued={(run) => setAiRuns((current) => [run, ...current.filter((x) => x.id !== run.id)].slice(0, 8))} />}
          {!loadError && hasSelectedPackage && view === 'report-studio' && <ReportingStudioView packageData={selectedPackage} refreshPackages={refreshPackages} notify={setToast} />}
          {!loadError && view === 'statements' && <StatementsView organizationKey={selectedOrganizationKey} periodKey={selectedPeriodKey} />}
          {!loadError && hasSelectedPackage && view === 'library' && <ExecutiveLibrary packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'kpis' && <KpiLibrary packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'branding' && <BrandingView packageData={selectedPackage} refreshPackages={refreshPackages} notify={setToast} />}
          {!loadError && hasSelectedPackage && view === 'layouts' && <LayoutsView packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'output' && <OutputView packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'livedash' && <LiveDashboard packageData={selectedPackage} openSlide={openSlide} />}
          {!loadError && view === 'parity' && <CompetitiveParityView packageData={hasSelectedPackage ? selectedPackage : null} refreshPackages={refreshPackages} />}
        </section>
      </main>

      <CommandPalette
        open={paletteOpen}
        onClose={() => setPaletteOpen(false)}
        organizations={reportingContext.organizations.map((organization) => ({
          key: organization.key,
          name: organization.name,
          abbreviation: organization.abbreviation,
        }))}
        periods={reportingContext.periods.map((period) => ({ key: period.key, label: period.label }))}
        slides={selectedPackage.slides.map((slide) => ({
          id: slide.id,
          sortOrder: slide.sortOrder,
          subject: slide.subject,
          kpiLabel: slide.kpiLabel,
        }))}
        hasPackage={hasSelectedPackage}
        selectedOrganizationKey={selectedOrganizationKey}
        onView={(nextView) => setView(nextView as View)}
        onSelectOrganization={(organizationKey) => selectReportingContext(organizationKey, selectedPeriodKey)}
        onSelectPeriod={(periodKey) => selectReportingContext(selectedOrganizationKey, periodKey)}
        onOpenSlide={(slideId) => openSlide(slideId)}
      />
      {hasSelectedPackage && panel?.type === 'chat' && selectedSlide && <ChatPanel slide={selectedSlide} onClose={() => setPanel(null)} />}
      {hasSelectedPackage && panel?.type === 'history' && selectedSlide && <HistoryPanel packageData={selectedPackage} slide={selectedSlide} onClose={() => setPanel(null)} refreshPackages={refreshPackages} />}
      {panel?.type === 'account' && <AccountPanel accountId={panel.accountId} organizationKey={selectedOrganizationKey} onClose={() => setPanel(null)} onChanged={() => { setToast('Mapping updated.'); setMappingRefreshKey((key) => key + 1); refreshPackages().catch(() => undefined) }} />}
      <AiRunProgress runs={aiRuns} />
      {toast && <Toast message={toast} onDone={() => setToast(null)} />}
    </div>
  )
}

/**
 * CS Redesign v3 TopBar — 48px tall, IBM Plex Sans.
 * Brand square + name, vertical divider, entity/month/year split-pill selector,
 * sync + issues status pills, search button (⌘K hint), avatar.
 */
function TopBar({
  reportingContext,
  selectedOrganizationKey,
  selectedPeriodKey,
  onSelect,
  selectedPackage,
  onOpenPalette,
}: {
  reportingContext: ReportingContext
  selectedOrganizationKey: string
  selectedPeriodKey: string
  onSelect: (organizationKey: string, periodKey: string) => void
  selectedPackage: PackageDto
  onOpenPalette: () => void
}) {
  const selectedPeriod = reportingContext.periods.find((item) => item.key === selectedPeriodKey)
  const hasMappedXeroOrganizations = reportingContext.organizations.some((organization) => organization.isXeroMapped)
  const organizations = reportingContext.organizations.filter((organization) => {
    if (organization.key === selectedOrganizationKey) return true
    if (organization.isConsolidated) return false
    return hasMappedXeroOrganizations ? organization.isXeroMapped : true
  })
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
  const periodMonth = selectedPeriod ? months[(selectedPeriod.periodStart ? new Date(selectedPeriod.periodStart).getUTCMonth() : 0)] : ''
  const periodYear = selectedPeriod ? new Date(selectedPeriod.periodStart).getUTCFullYear() : new Date().getUTCFullYear()

  const monthOptions = reportingContext.periods.filter((p) => new Date(p.periodStart).getUTCFullYear() === periodYear)
  const yearOptions = Array.from(new Set(reportingContext.periods.map((p) => new Date(p.periodStart).getUTCFullYear()))).sort()

  const openIssues = (selectedPackage.issues ?? []).filter((i) => i.status === 'Open').length
  const lastSync = formatRelative(selectedPackage.lastXeroSyncAt)
  const isSyncFresh = isWithinLastHour(selectedPackage.lastXeroSyncAt)

  return (
    <header className="cs-topbar" role="banner">
      <div className="cs-brand">
        <div className="cs-brand-mark">C</div>
        <span className="cs-brand-name">CloseSoftware</span>
      </div>

      <div className="cs-topbar-divider" />

      <div className="cs-context-pill" aria-label="Reporting context">
        <div className="cs-context-field">
          <span className="cs-context-label">Entity</span>
          <select
            value={selectedOrganizationKey}
            onChange={(event) => onSelect(event.target.value, selectedPeriodKey)}
            disabled={organizations.length === 0}
          >
            {organizations.length === 0 && <option value="">No entities</option>}
            {organizations.map((organization) => (
              <option key={organization.key} value={organization.key}>{organization.name}</option>
            ))}
          </select>
        </div>
        <div className="cs-context-divider" />
        <div className="cs-context-field">
          <span className="cs-context-label">Month</span>
          <select
            value={selectedPeriodKey}
            onChange={(event) => onSelect(selectedOrganizationKey, event.target.value)}
            disabled={monthOptions.length === 0}
          >
            {monthOptions.length === 0 && <option value="">—</option>}
            {monthOptions.map((period) => (
              <option key={period.key} value={period.key}>{period.label.split(' ')[0]}</option>
            ))}
          </select>
        </div>
        <div className="cs-context-divider" />
        <div className="cs-context-field">
          <span className="cs-context-label">Year</span>
          <select
            value={String(periodYear)}
            onChange={(event) => {
              // Find any period in the chosen year and switch to it (default: same month if available, else first).
              const targetYear = Number(event.target.value)
              const sameMonth = reportingContext.periods.find((p) => new Date(p.periodStart).getUTCFullYear() === targetYear && months[new Date(p.periodStart).getUTCMonth()] === periodMonth)
              const fallback = reportingContext.periods.find((p) => new Date(p.periodStart).getUTCFullYear() === targetYear)
              const next = sameMonth ?? fallback
              if (next) onSelect(selectedOrganizationKey, next.key)
            }}
          >
            {yearOptions.map((y) => <option key={y} value={String(y)}>{y}</option>)}
          </select>
        </div>
      </div>

      <div className="cs-status-row">
        <span className={`cs-status-pill ${isSyncFresh ? 'good' : 'warn'}`} title={`Last Xero sync ${lastSync}`}>
          <span className="cs-status-dot" />
          <span className="mono">{lastSync}</span>
        </span>
        {openIssues > 0 && (
          <span className="cs-status-pill warn" title={`${openIssues} open issue${openIssues === 1 ? '' : 's'}`}>
            <span className="cs-status-dot" />
            <span className="mono">{openIssues} {openIssues === 1 ? 'issue' : 'issues'}</span>
          </span>
        )}
      </div>

      <div className="spacer" />

      <button
        className="cs-search-button"
        type="button"
        onClick={onOpenPalette}
        title="Search (⌘K)"
        aria-label="Open command palette"
      >
        <Search size={13} aria-hidden />
        <span>Search…</span>
        <span className="cs-kbd mono">⌘K</span>
      </button>

      <div className="cs-avatar" aria-label="Account">MP</div>
    </header>
  )
}

/**
 * CS Redesign v3 Sidebar — 224px wide, IBM Plex Sans.
 * Entity context card, Workflow group, Mapping & Eliminations accordion,
 * Financial Package accordion (with numbered slide list), Package tools footer.
 */

const MAPPING_SUB_VIEWS: View[] = ['mapping', 'fslibrary', 'eliminations']
const PACKAGE_SUB_VIEWS: View[] = ['dashboard', 'slide']

function CsSidebarItem({
  active,
  icon,
  label,
  onClick,
  hasIssue,
}: {
  active: boolean
  icon: React.ReactNode
  label: string
  onClick: () => void
  hasIssue?: boolean
}) {
  return (
    <button
      type="button"
      className={`cs-side-item ${active ? 'active' : ''}`}
      onClick={onClick}
      aria-current={active ? 'page' : undefined}
    >
      <span className="cs-side-icon" aria-hidden>{icon}</span>
      <span className="cs-side-label">{label}</span>
      {hasIssue && <span className="cs-side-issue-dot" aria-label="Issue" />}
    </button>
  )
}

function CsSidebarLabel({ children }: { children: React.ReactNode }) {
  return <div className="cs-side-eyebrow">{children}</div>
}

function CsSidebarAccordion({
  label,
  icon,
  isOpen,
  onToggle,
  children,
  active,
}: {
  label: string
  icon: React.ReactNode
  isOpen: boolean
  onToggle: () => void
  children: React.ReactNode
  active: boolean
}) {
  return (
    <div className="cs-side-accordion">
      <button
        type="button"
        className={`cs-side-item cs-side-accordion-toggle ${active ? 'active' : ''}`}
        onClick={onToggle}
        aria-expanded={isOpen}
      >
        <span className="cs-side-icon" aria-hidden>{icon}</span>
        <span className="cs-side-label">{label}</span>
        <span className="cs-side-chev" aria-hidden>{isOpen ? '▾' : '▸'}</span>
      </button>
      {isOpen && <div className="cs-side-accordion-body">{children}</div>}
    </div>
  )
}

function CsSidebarSubItem({
  active,
  label,
  icon,
  onClick,
  index,
  hasIssue,
}: {
  active: boolean
  label: string
  icon?: React.ReactNode
  onClick: () => void
  index?: number
  hasIssue?: boolean
}) {
  return (
    <button
      type="button"
      className={`cs-side-sub ${active ? 'active' : ''}`}
      onClick={onClick}
      aria-current={active ? 'page' : undefined}
    >
      {icon && <span className="cs-side-icon" aria-hidden>{icon}</span>}
      {index !== undefined && <span className="cs-side-sub-index mono">{String(index).padStart(2, '0')}</span>}
      <span className="cs-side-label">{label}</span>
      {hasIssue && <span className="cs-side-issue-dot small" aria-label="Issue" />}
    </button>
  )
}

function Sidebar({
  packageData,
  view,
  selectedSlideId,
  openSlide,
  setView,
  hasPackage,
}: {
  packageData: PackageDto
  view: View
  selectedSlideId: string
  openSlide: (id: string) => void
  setView: (view: View) => void
  hasPackage: boolean
}) {
  const [mappingOpen, setMappingOpen] = useState(false)
  const [pkgOpen, setPkgOpen] = useState(false)
  const issuesBySlide = new Map<string, IssueDto>()
  for (const issue of (packageData.issues ?? [])) {
    if (issue.packageSlideId && issue.status === 'Open') issuesBySlide.set(issue.packageSlideId, issue)
  }
  const isMappingActive = MAPPING_SUB_VIEWS.includes(view)
  const isPackageActive = PACKAGE_SUB_VIEWS.includes(view)

  // Auto-open accordions when their views are active so navigation feels coherent.
  useEffect(() => { if (isMappingActive) setMappingOpen(true) }, [isMappingActive])
  useEffect(() => { if (isPackageActive) setPkgOpen(true) }, [isPackageActive])

  const fresh = isWithinLastHour(packageData.lastXeroSyncAt)

  return (
    <aside className="cs-sidebar" role="navigation" aria-label="Primary">
      <div className="cs-entity-card">
        <div className="cs-eyebrow">Selected entity</div>
        <div className="cs-entity-name">{packageData.organizationName || '—'}</div>
        <div className="cs-entity-period">{packageData.period}</div>
        <div className="cs-entity-meta">
          <span className="cs-version-chip">{packageData.versionLabel || 'v1'} {packageData.id ? '' : 'Data only'}</span>
          <span className="cs-sync-indicator">
            <span className={`cs-sync-dot ${fresh ? 'good' : 'idle'}`} />
            <span>{formatRelative(packageData.lastXeroSyncAt)}</span>
          </span>
        </div>
      </div>

      <div className="cs-side-scroll">
        <CsSidebarLabel>Workflow</CsSidebarLabel>
        <CsSidebarItem active={view === 'dashboard'} icon={<LayoutGrid size={14} />} label="Entity Dashboard" onClick={() => setView('dashboard')} />
        <CsSidebarItem active={view === 'flux'} icon={<MessageSquare size={14} />} label="Flux Review" onClick={() => setView('flux')} hasIssue={(packageData.issues ?? []).some((i) => i.status === 'Open' && i.category?.toLowerCase().includes('flux'))} />
        <CsSidebarItem active={view === 'statements'} icon={<Database size={14} />} label="Statements & Transactions" onClick={() => setView('statements')} />
        <CsSidebarItem active={view === 'library'} icon={<History size={14} />} label="Reporting Library" onClick={() => setView('library')} />
        <CsSidebarItem active={view === 'livedash'} icon={<LineChart size={14} />} label="Live Dashboard" onClick={() => setView('livedash')} />

        <CsSidebarAccordion
          label="Mapping & Eliminations"
          icon={<MapIcon size={14} />}
          isOpen={mappingOpen}
          onToggle={() => { setMappingOpen((o) => !o); if (!mappingOpen) setView('mapping') }}
          active={isMappingActive}
        >
          <CsSidebarSubItem active={view === 'mapping'} icon={<MapIcon size={12} />} label="Account Mapping" onClick={() => setView('mapping')} />
          <CsSidebarSubItem active={view === 'fslibrary'} icon={<FileText size={12} />} label="FS Line Library" onClick={() => setView('fslibrary')} />
          <CsSidebarSubItem active={view === 'eliminations'} icon={<X size={12} />} label="Eliminations" onClick={() => setView('eliminations')} />
        </CsSidebarAccordion>

        <CsSidebarAccordion
          label="Financial Package"
          icon={<LayoutGrid size={14} />}
          isOpen={pkgOpen}
          onToggle={() => { setPkgOpen((o) => !o); if (!pkgOpen) setView('dashboard') }}
          active={isPackageActive}
        >
          <CsSidebarSubItem active={view === 'dashboard'} icon={<LayoutGrid size={12} />} label="Package Overview" onClick={() => setView('dashboard')} />
          {!hasPackage && <div className="cs-side-empty">No package for this entity & period.</div>}
          {packageData.slides.map((slide) => (
            <CsSidebarSubItem
              key={slide.id}
              active={view === 'slide' && selectedSlideId === slide.id}
              index={slide.sortOrder}
              label={slide.subject}
              onClick={() => openSlide(slide.id)}
              hasIssue={issuesBySlide.has(slide.id)}
            />
          ))}
        </CsSidebarAccordion>
      </div>

      <div className="cs-side-footer">
        <CsSidebarLabel>Package tools</CsSidebarLabel>
        <CsSidebarItem active={view === 'output'} icon={<Share2 size={14} />} label="Share & Export" onClick={() => setView('output')} />
        <CsSidebarItem
          active={view === 'settings' || view === 'ai-settings' || view === 'xero-settings' || view === 'branding' || view === 'layouts'}
          icon={<Settings size={14} />}
          label="Settings"
          onClick={() => setView('settings')}
        />
      </div>
    </aside>
  )
}

function PackagePlaceholder({ packageData, createPackage }: { packageData: PackageDto; createPackage: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const create = async () => {
    setBusy(true)
    try {
      await createPackage()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page">
      <div className="empty-package">
        <div>
          <div className="eyebrow">Reporting period ready</div>
          <h1>{packageData.organizationName} · {packageData.period}</h1>
          <p>This entity and period exist, but no board package has been started yet. Xero ledger sync can create periods automatically when transaction activity appears.</p>
        </div>
        <Button variant="primary" icon={<Plus size={15} />} disabled={busy} onClick={create}>
          Create package
        </Button>
      </div>
    </div>
  )
}

function ApiErrorState({ message }: { message: string }) {
  return (
    <div className="page">
      <div className="empty-package danger">
        <div>
          <div className="eyebrow">API unavailable</div>
          <h1>Live data could not be loaded</h1>
          <p>{message}</p>
        </div>
      </div>
    </div>
  )
}

function SettingsHome({ setView }: { setView: (view: View) => void }) {
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">System settings</div>
          <h1>Settings</h1>
          <p>Connection, AI runtime, role, branding, and diagnostic controls live here.</p>
        </div>
      </div>
      <div className="settings-home-grid">
        <ActionTile icon={<PlugZap size={20} />} title="Xero Settings" text="Tenant status, OAuth reconnects, data coverage, and historical backfill." onClick={() => setView('xero-settings')} />
        <ActionTile icon={<Bot size={20} />} title="AI Settings" text="Codex CLI model, reasoning effort, and module prompt profiles." onClick={() => setView('ai-settings')} />
        <ActionTile icon={<Paintbrush size={20} />} title="Branding" text="Entity package colors, cover styling, and export presentation settings." onClick={() => setView('branding')} />
        <ActionTile icon={<LayoutGrid size={20} />} title="Layouts" text="Board package page order, headers, footers, and slide layouts." onClick={() => setView('layouts')} />
      </div>
    </div>
  )
}



function ChatPanel({ slide, onClose }: { slide: SlideDto; onClose: () => void }) {
  const [messages, setMessages] = useState([{ from: 'ai', text: `I'm scoped to ${slide.subject}. I can rewrite, explain drivers, check QA rules, or recommend a fix.` }])
  const [input, setInput] = useState('')
  const send = () => {
    if (!input.trim()) return
    const reply = `Suggested rewrite: ${slide.kpiLabel} totaled ${fmtMoney(slide.currentValue)}, ${slide.varianceAmount >= 0 ? 'up' : 'down'} ${Math.abs(slide.variancePercent).toFixed(1)}% versus prior year, driven by the highest-confidence GL accounts.`
    setMessages((current) => [...current, { from: 'user', text: input }, { from: 'ai', text: reply }])
    setInput('')
  }
  return (
    <SidePanel title="AI Assistant" subtitle={slide.subject} onClose={onClose}>
      <div className="chat-list">
        {messages.map((message, index) => (
          <div key={index} className={message.from === 'ai' ? 'chat ai' : 'chat user'}>
            {message.text}
          </div>
        ))}
      </div>
      <div className="quick-actions">
        {['Rewrite for board clarity', 'Check Rule 5/6', 'Add business driver'].map((action) => (
          <Button key={action} variant="ghost" onClick={() => setInput(action)}>{action}</Button>
        ))}
      </div>
      <div className="chat-input">
        <input value={input} onChange={(event) => setInput(event.target.value)} onKeyDown={(event) => event.key === 'Enter' && send()} placeholder="Ask about this slide..." />
        <Button variant="primary" icon={<Send size={14} />} onClick={send}>Send</Button>
      </div>
    </SidePanel>
  )
}

function HistoryPanel({ packageData, slide, onClose, refreshPackages }: { packageData: PackageDto; slide: SlideDto; onClose: () => void; refreshPackages: () => Promise<void> }) {
  const [versions, setVersions] = useState<PackageVersion[]>([])

  useEffect(() => {
    fetchJson<PackageVersion[]>(`/api/packages/${packageData.id}/versions`).then(setVersions).catch(() => setVersions([]))
  }, [packageData.id])

  const restore = async (versionId: string) => {
    await postJson(`/api/packages/${packageData.id}/versions/${versionId}/restore`, {})
    await refreshPackages()
    onClose()
  }

  return (
    <SidePanel title="Version history" subtitle={slide.subject} onClose={onClose}>
      {versions.length === 0 && <div className="empty-state">No versions yet.</div>}
      {versions.map((version, index) => (
        <div className="history-row" key={version.id}>
          <span className="mono">{version.versionLabel}</span>
          <strong>{version.changeSummary}</strong>
          <small>{version.createdBy} · {formatRelative(version.createdAt)}</small>
          {index > 0 && <Button variant="ghost" onClick={() => restore(version.id)}>Restore</Button>}
        </div>
      ))}
    </SidePanel>
  )
}

function SidePanel({ title, subtitle, onClose, children }: { title: string; subtitle: string; onClose: () => void; children: React.ReactNode }) {
  // P3.37 — close on Escape so keyboard users can dismiss without locating the X button.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [onClose]);
  return (
    <aside className="side-panel" role="dialog" aria-modal="true" aria-label={title}>
      <div className="panel-header">
        <div>
          <strong>{title}</strong>
          <span>{subtitle}</span>
        </div>
        <button onClick={onClose}><X size={18} /></button>
      </div>
      <div className="panel-body">{children}</div>
    </aside>
  )
}

function ActionTile({ icon, title, text, onClick }: { icon: React.ReactNode; title: string; text: string; onClick: () => void }) {
  return (
    <button className="action-tile" onClick={onClick}>
      <span>{icon}</span>
      <strong>{title}</strong>
      <p>{text}</p>
    </button>
  )
}

// Card / Button / SegmentButton / RailItem / SeverityBadge / Sparkline moved to
// components/primitives.tsx. Imported at the top of this file.

function AiRunProgress({ runs }: { runs: AiRun[] }) {
  const [dismissedRunIds, setDismissedRunIds] = useState<string[]>([])
  const visibleRuns = runs.filter((run) => !dismissedRunIds.includes(run.id))
  const activeRun = visibleRuns.find((run) => run.status === 'Queued' || run.status === 'Running') ?? visibleRuns[0]

  if (!activeRun) {
    return null
  }

  const progress = normalizeAiProgress(activeRun)
  const statusClass = activeRun.status.toLowerCase()

  return (
    <aside className={`ai-run-popover ${statusClass}`} role="status" aria-live="polite">
      <div className="ai-run-header">
        <div className="ai-run-title">
          <span className="ai-run-icon"><Bot size={16} /></span>
          <div>
            <strong>{formatAiModule(activeRun.module)}</strong>
            <span>{aiRunStatusLabel(activeRun)}</span>
          </div>
        </div>
        <button
          className="ai-run-close"
          aria-label="Dismiss AI progress"
          onClick={() => setDismissedRunIds((current) => [...current, activeRun.id])}
        >
          <X size={16} />
        </button>
      </div>
      <div className="ai-progress-row">
        <div className="ai-progress-track" aria-hidden="true">
          <span style={{ width: `${progress}%` }} />
        </div>
        <span className="mono">{progress}%</span>
      </div>
      <div className="ai-step-list">
        {AI_PROGRESS_STEPS.map((step, index) => {
          const state = aiStepState(activeRun, step.progress, index)
          return (
            <div className={`ai-step ${state}`} key={step.label}>
              {state === 'complete'
                ? <CheckCircle2 size={14} />
                : state === 'failed'
                  ? <AlertTriangle size={14} />
                  : <Clock3 size={14} />}
              <span>{step.label}</span>
            </div>
          )
        })}
      </div>
      <p>{aiRunDetail(activeRun)}</p>
    </aside>
  )
}

function Toast({ message, onDone }: { message: string; onDone: () => void }) {
  useEffect(() => {
    const timeout = window.setTimeout(onDone, 2600)
    return () => window.clearTimeout(timeout)
  }, [onDone])
  return <div className="toast">{message}</div>
}

function normalizeAiProgress(run: AiRun) {
  if (run.status === 'Completed') return 100
  if (run.status === 'Queued') return Math.max(4, run.progress)
  return Math.max(0, Math.min(100, run.progress))
}

function aiRunStatusLabel(run: AiRun) {
  if (run.status === 'Completed') return 'Completed'
  if (run.status === 'Failed') return 'Needs attention'
  if (run.status === 'Cancelled') return 'Cancelled'
  const index = currentAiStepIndex(normalizeAiProgress(run))
  return AI_PROGRESS_STEPS[index]?.label ?? run.status
}

function aiRunDetail(run: AiRun) {
  if (run.status === 'Completed') return 'Results are ready and the package is refreshing with the latest AI output.'
  if (run.status === 'Cancelled') return 'This AI run was stopped before completion.'
  if (run.status === 'Failed') {
    const lines = run.logs.split('\n').map((line) => line.trim()).filter(Boolean)
    return lines[lines.length - 1] ?? 'The AI run could not finish.'
  }
  if (run.status === 'Queued') return 'Waiting for the next available AI worker.'
  return 'You can keep working while the AI run continues in the background.'
}

function aiStepState(run: AiRun, stepProgress: number, index: number) {
  const progress = normalizeAiProgress(run)
  const currentIndex = currentAiStepIndex(progress)
  if (run.status === 'Completed') return 'complete'
  if (run.status === 'Failed' && index === currentIndex) return 'failed'
  if (run.status === 'Cancelled' && index === currentIndex) return 'cancelled'
  if (stepProgress < progress) return 'complete'
  if (index === currentIndex) return 'current'
  return 'pending'
}

function currentAiStepIndex(progress: number) {
  let currentIndex = 0
  AI_PROGRESS_STEPS.forEach((step, index) => {
    if (progress >= step.progress) currentIndex = index
  })
  return currentIndex
}

function formatAiModule(value: string) {
  return value
    .split('-')
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}

function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

function formatRelative(value: string | null) {
  if (!value) return '—'
  const minutes = Math.max(1, Math.round((Date.now() - new Date(value).getTime()) / 60000))
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} hr ago`
}

function isWithinLastHour(value: string | null) {
  if (!value) return false
  return (Date.now() - new Date(value).getTime()) < 1000 * 60 * 60
}


function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

// fetchJson / postJson / putJson / deleteJson moved to api/client.ts.

export default App
