import { useCallback, useEffect, useState } from 'react'
import {
  AlertTriangle,
  ArrowUpRight,
  BarChart3,
  Bell,
  Bot,
  Boxes,
  Building2,
  CalendarDays,
  Check,
  CheckCircle2,
  Clock3,
  Database,
  Download,
  FileSpreadsheet,
  FileText,
  Gauge,
  History,
  LayoutGrid,
  LineChart,
  Link,
  ListChecks,
  MessageSquare,
  Paintbrush,
  PanelRightOpen,
  PlugZap,
  Plus,
  RefreshCw,
  Send,
  Settings,
  Share2,
  Sparkles,
  Target,
  TrendingUp,
  Undo2,
  Wand2,
  X,
} from 'lucide-react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import './App.css'

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? ''

type View =
  | 'dashboard'
  | 'slide'
  | 'planning'
  | 'benchmarks'
  | 'mapping'
  | 'flux'
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

type AccountDto = {
  id: string
  tenantId: string
  code: string
  name: string
  type: string
  fsLine: string
  aiSuggestedFsLine: string
  mappingConfidence: number
  isFirstSeen: boolean
  reviewStatus: string
  consolidationTreatment: string
  monthlyBalancesJson: string
  transactionCount: number
}

type AccountDetailDto = {
  account: AccountDto
  transactions: Array<{
    id: string
    transactionDate: string
    description: string
    debit: number
    credit: number
    source: string
  }>
}

type FsLineDefinitionDto = {
  id: string
  organizationId: string
  statementType: string
  section: string
  name: string
  normalBalance: string
  aiGuidance: string
  sortOrder: number
  isActive: boolean
}

type AiModel = {
  id: string
  displayName: string
  reasoningEfforts: string[]
  isDefault: boolean
}

type AiSetting = {
  id?: string
  module: string
  model: string
  reasoningEffort: string
  profile: string
  enabled: boolean
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

type KpiDto = {
  id: string
  organizationId: string
  name: string
  category: string
  formula: string
  unit: string
  currentValue: number
  targetValue: number
  isPinned: boolean
  status: string
}

type KpiAlertDto = {
  id: string
  kpiDefinitionId: string
  kpiName: string
  direction: string
  thresholdValue: number
  severity: string
  message: string
  isActive: boolean
  isTriggered: boolean
  lastTriggeredAt: string | null
}

type NonFinancialMetricDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  name: string
  category: string
  unit: string
  currentValue: number
  priorValue: number
  targetValue: number
  valuesJson: string
  source: string
  isPinned: boolean
}

type FormulaEvaluationDto = {
  formula: string
  normalizedExpression: string
  value: number
  dependencies: string[]
}

type FxRateDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  currencyCode: string
  rateToPresentation: number
  source: string
  updatedAt: string
}

type PackageCommentDto = {
  id: string
  reportPackageId: string
  packageSlideId: string | null
  slideBlockId: string | null
  body: string
  status: string
  author: string
  createdAt: string
  updatedAt: string
  resolvedAt: string | null
}

type ForecastProjectionRowDto = {
  monthKey: string
  revenue: number
  grossProfit: number
  operatingExpense: number
  netIncome: number
  cashInflow: number
  cashOutflow: number
  netCashFlow: number
  endingCash: number
  accountsReceivable: number
  accountsPayable: number
  equity: number
  cashThresholdBreached: boolean
}

type ForecastEventDto = {
  id: string
  monthOffset: number
  name: string
  category: string
  revenueImpact: number
  expenseImpact: number
  cashImpact: number
  isRecurring: boolean
  notes: string
}

type ForecastScenarioDto = {
  id: string
  organizationId: string
  reportingPeriodId: string
  name: string
  description: string
  scenarioType: string
  horizonMonths: number
  revenueGrowthPercent: number
  grossMarginPercent: number
  opexGrowthPercent: number
  cashConversionPercent: number
  startingCash: number
  cashThreshold: number
  assumptionsJson: string
  isBase: boolean
  events: ForecastEventDto[]
  rows: ForecastProjectionRowDto[]
}

type BudgetVarianceRowDto = {
  fsLine: string
  actualAmount: number
  budgetAmount: number
  varianceAmount: number
  variancePercent: number
}

type PlanningOverviewDto = {
  reportPackageId: string
  organizationId: string
  organizationName: string
  periodKey: string
  monthlyRevenueActual: number
  monthlyOperatingExpenseActual: number
  forecastStartMonth: string
  scenarios: ForecastScenarioDto[]
  budgetRows: BudgetVarianceRowDto[]
  nonFinancialMetrics: NonFinancialMetricDto[]
}

type CashTimingRowDto = {
  label: string
  periodStart: string
  periodEnd: string
  cashInflow: number
  cashOutflow: number
  netCashFlow: number
  endingCash: number
  cashThresholdBreached: boolean
}

type CashTimingDto = {
  reportPackageId: string
  scenarioId: string
  granularity: string
  rows: CashTimingRowDto[]
}

type BenchmarkingDto = {
  periodKey: string
  rows: BenchmarkRowDto[]
}

type BenchmarkRowDto = {
  organizationId: string
  organizationName: string
  abbreviation: string
  isConsolidated: boolean
  reportPackageId: string | null
  packageStatus: string
  revenue: number
  expense: number
  netIncome: number
  grossMarginPercent: number
  openIssueCount: number
  kpis: KpiDto[]
  rank: number
}

type ReportTemplateDto = {
  id: string
  name: string
  category: string
  description: string
  sections: string[]
  isBuiltIn: boolean
}

type CompetitiveFeatureGroupDto = {
  category: string
  competitorPattern: string
  features: Array<{
    name: string
    status: string
    ourImplementation: string
  }>
}

type ExportArtifact = {
  id: string
  type: string
  status: string
  fileName: string
  downloadUrl: string
}

type XeroConnectionStatus = {
  id: string
  organizationId: string
  tenantId: string
  tenantName: string
  tenantType: string
  connectionStatus: string
  lastConnectedAt: string | null
  lastError: string | null
  tokenExpiresAt: string
  scopes: string
  isTokenExpired: boolean
  lastSyncAt: string | null
  lastSyncStatus: string | null
}

type XeroTenantStatus = {
  id: string
  tenantId: string
  tenantName: string
  tenantType: string
  connectionStatus: string
  lastConnectedAt: string | null
  lastError: string | null
  tokenExpiresAt: string
  scopes: string
  requiresReconnectForLedger: boolean
  source: string
  isTokenExpired: boolean
  mappedOrganizationId: string | null
  isIgnored: boolean
}

type XeroLedgerSyncStatus = {
  enabled: boolean
  syncEveryMinutes: number
  dailyTrialBalanceHourUtc: number
  retentionYears: number
  tenants: Array<{
    tenantId: string
    tenantName: string
    connectionStatus: string
    requiresReconnectForLedger: boolean
    lastJournalNumber: number | null
    lastSuccessfulSyncAt: string | null
    status: string
    lastError: string | null
  }>
}

type XeroStatus = {
  clientConfigured: boolean
  redirectUri: string
  scopes: string
  environment: string
  allowLocalStubReports: boolean
  connections: XeroConnectionStatus[]
  tenants: XeroTenantStatus[]
  ledgerSync: XeroLedgerSyncStatus
  tokenImport: {
    supported: boolean
    requiresMatchingDataProtectionKeyRing: boolean
    source: string
  }
}

type XeroConnectResponse = {
  authUrl: string | null
  state: string | null
  error: string | null
}

type XeroBackfillPreview = {
  fromPeriodKey: string
  toPeriodKey: string
  monthCount: number
  estimatedCalls: number
  softMinuteLimit: number
  softDailyLimit: number
  hydrateLedger: boolean
  tenants: Array<{ tenantId: string; tenantName: string; organizationName: string; estimatedCalls: number; risk: string }>
}

type XeroBackfillRun = {
  id: string
  fromPeriodKey: string
  toPeriodKey: string
  status: string
  estimatedCalls: number
  actualCalls: number
  summaryJson: string
  rateLimitJson: string
  error: string | null
  tasks: Array<{ tenantId: string; tenantName: string; status: string; estimatedCalls: number; actualCalls: number; journalsImported: number; journalLinesImported: number; statementsImported: number; error: string | null }>
}

type XeroDataCoverage = {
  fromPeriodKey: string
  toPeriodKey: string
  rows: Array<{
    tenantId: string
    tenantName: string
    organizationKey: string
    organizationName: string
    periodKey: string
    status: string
    journalCount: number
    journalLineCount: number
    rawSnapshotTypes: string[]
    statementLineCount: number
    reconciliationStatus: string | null
    reconciliationDifference: number
  }>
}

type EntityStatements = {
  organizationKey: string
  periodKey: string
  lines: Array<{ statementType: string; section: string; rowPath: string; lineName: string; accountCode: string; currentAmount: number; priorAmount: number; amountsJson: string }>
}

type EntityLedgerSummary = {
  organizationKey: string
  periodKey: string
  journalLineCount: number
  lines: Array<{ accountCode: string; accountName: string; netAmount: number; transactionCount: number }>
}

type FluxReview = {
  reportPackageId: string
  isSourceDataStale: boolean
  sourceDataStaleReason: string | null
  progress: FluxReviewProgress
  groups: FluxReviewGroup[]
}

type FluxReviewProgress = {
  totalGroups: number
  requiredExplanations: number
  openExplanations: number
  autoSignedOff: number
  prepared: number
  reviewed: number
}

type FluxReviewGroup = {
  id: string
  fluxType: string
  statementType: string
  groupName: string
  currentPeriodKey: string
  priorPeriodKey: string
  currentAmount: number
  priorAmount: number
  runningThreeMonthAmount: number
  varianceAmount: number
  variancePercent: number
  dollarThreshold: number
  percentThreshold: number
  thresholdLogic: string
  requiresExplanation: boolean
  requiresLedgerDetail: boolean
  ledgerDetailStatus: string
  ledgerDetailPulledAt: string | null
  status: string
  assignee: string
  reviewer: string
  dueDate: string | null
  explanationTemplate: string
  priorExplanation: string
  tags: string
  trendJson: string
  driverSummaryJson: string
  riskFlagsJson: string
  autoSignedOff: boolean
  explanation: string
  preparedBy: string
  preparedAt: string | null
  reviewedBy: string
  reviewedAt: string | null
  evidenceJson: string
}

type FluxReviewDrilldown = {
  groupId: string
  fluxType: string
  statementType: string
  groupName: string
  currentPeriodKey: string
  priorPeriodKey: string
  currentAmount: number
  priorAmount: number
  runningThreeMonthAmount: number
  varianceAmount: number
  variancePercent: number
  accounts: FluxReviewAccount[]
}

type FluxReviewAccount = {
  accountCode: string
  accountName: string
  accountType: string
  fsLine: string
  currentAmount: number
  priorAmount: number
  varianceAmount: number
  variancePercent: number
  currentTransactions: FluxLedgerTransaction[]
  priorTransactions: FluxLedgerTransaction[]
}

type FluxLedgerTransaction = {
  date: string
  journalNumber: number
  sourceType: string
  reference: string
  description: string
  netAmount: number
  grossAmount: number
  taxAmount: number
}

type AiPackageDraft = {
  id: string
  reportPackageId: string
  status: string
  kind: string
  title: string
  description: string
  createdAt: string
}

type Panel =
  | { type: 'chat'; slideId: string; blockId?: string }
  | { type: 'history'; slideId: string }
  | { type: 'account'; accountId: string }
  | null

const AI_SETTING_MODULES = ['slide-chat', 'narrative-rewrite', 'mapping-suggestions', 'flux-explain', 'final-review', 'export-qa']

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

function block(kind: string, content: unknown): SlideBlockDto {
  return { id: crypto.randomUUID(), sortOrder: 1, kind, contentJson: JSON.stringify(content) }
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

  const selectedPackage =
    packages.find((p) => p.organizationKey === selectedOrganizationKey && p.periodKey === selectedPeriodKey)
    ?? packages.find((p) => p.id === selectedPackageId)
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
      <TopBar
        key={`${selectedOrganizationKey}:${selectedPeriodKey}`}
        reportingContext={reportingContext}
        selectedOrganizationKey={selectedOrganizationKey}
        selectedPeriodKey={selectedPeriodKey}
        onSelect={selectReportingContext}
        view={view}
          setView={setView}
          selectedPackage={selectedPackage}
        />
      <main className="workspace">
        <Sidebar
          packageData={selectedPackage}
          view={view}
          selectedSlideId={selectedSlideId}
          openSlide={openSlide}
          setView={setView}
          recompile={recompile}
          hasPackage={hasSelectedPackage}
        />
        <section className={panel ? 'content with-panel' : 'content'}>
          {loadError && <ApiErrorState message={loadError} />}
          {!loadError && view === 'settings' && <SettingsHome setView={setView} />}
          {!loadError && view === 'ai-settings' && <AiSettings />}
          {!loadError && view === 'xero-settings' && <XeroSettings notify={setToast} />}
          {!loadError && !hasSelectedPackage && ['dashboard', 'slide', 'planning', 'benchmarks', 'flux', 'library', 'kpis', 'branding', 'layouts', 'output', 'livedash'].includes(view) && (
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
              packageId={selectedPackage.id}
              slide={selectedSlide}
              issues={selectedPackage.issues.filter((x) => x.packageSlideId === selectedSlide.id)}
              refreshPackages={refreshPackages}
              notify={setToast}
              openChat={(blockId) => setPanel({ type: 'chat', slideId: selectedSlide.id, blockId })}
              openHistory={() => setPanel({ type: 'history', slideId: selectedSlide.id })}
            />
          )}
          {!loadError && view === 'mapping' && <MappingView organizationKey={selectedOrganizationKey} periodKey={selectedPeriodKey} refreshKey={mappingRefreshKey} openAccount={(accountId) => setPanel({ type: 'account', accountId })} />}
          {!loadError && hasSelectedPackage && view === 'planning' && <PlanningView packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'benchmarks' && <BenchmarkingView packageData={selectedPackage} />}
          {!loadError && hasSelectedPackage && view === 'flux' && <FluxReviewPanel packageData={selectedPackage} refreshPackages={refreshPackages} standalone onAiRunQueued={(run) => setAiRuns((current) => [run, ...current.filter((x) => x.id !== run.id)].slice(0, 8))} />}
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

      {hasSelectedPackage && panel?.type === 'chat' && selectedSlide && <ChatPanel slide={selectedSlide} onClose={() => setPanel(null)} />}
      {hasSelectedPackage && panel?.type === 'history' && selectedSlide && <HistoryPanel packageData={selectedPackage} slide={selectedSlide} onClose={() => setPanel(null)} refreshPackages={refreshPackages} />}
      {panel?.type === 'account' && <AccountPanel accountId={panel.accountId} organizationKey={selectedOrganizationKey} onClose={() => setPanel(null)} onChanged={() => { setToast('Mapping updated.'); setMappingRefreshKey((key) => key + 1); refreshPackages().catch(() => undefined) }} />}
      <AiRunProgress runs={aiRuns} />
      {toast && <Toast message={toast} onDone={() => setToast(null)} />}
    </div>
  )
}

function TopBar({
  reportingContext,
  selectedOrganizationKey,
  selectedPeriodKey,
  onSelect,
  view,
  setView,
  selectedPackage,
}: {
  reportingContext: ReportingContext
  selectedOrganizationKey: string
  selectedPeriodKey: string
  onSelect: (organizationKey: string, periodKey: string) => void
  view: View
  setView: (view: View) => void
  selectedPackage: PackageDto
}) {
  const selectedPackageOption = reportingContext.packages.find((item) => item.organizationKey === selectedOrganizationKey && item.periodKey === selectedPeriodKey)
  const selectedPeriod = reportingContext.periods.find((item) => item.key === selectedPeriodKey)
  const hasMappedXeroOrganizations = reportingContext.organizations.some((organization) => organization.isXeroMapped)
  const organizations = reportingContext.organizations.filter((organization) => {
    if (organization.key === selectedOrganizationKey) return true
    if (organization.isConsolidated) return false
    return hasMappedXeroOrganizations ? organization.isXeroMapped : true
  })

  return (
    <header className="topbar">
      <div className="brand">
        <div className="brand-mark">L</div>
        <div>
          <div className="brand-name">Ledgerline</div>
          <div className="brand-tag">BOARD PACKAGES</div>
        </div>
      </div>
      <div className="context-switcher" aria-label="Reporting context">
        <div className="context-heading">
          <span>Entity period</span>
          <strong>{selectedPackageOption ? selectedPackageOption.status : 'Data only'}</strong>
        </div>
        <label className="context-field entity-field">
          <span><Building2 size={13} /> Entity</span>
          <select value={selectedOrganizationKey} onChange={(event) => onSelect(event.target.value, selectedPeriodKey)} disabled={organizations.length === 0}>
            {organizations.length === 0 && <option value="">No Xero entities</option>}
            {organizations.map((organization) => (
              <option key={organization.key} value={organization.key}>
                {organization.name}
              </option>
            ))}
          </select>
        </label>
        <label className="context-field">
          <span><CalendarDays size={13} /> Period</span>
          <select value={selectedPeriodKey} onChange={(event) => onSelect(selectedOrganizationKey, event.target.value)} disabled={reportingContext.periods.length === 0}>
            {reportingContext.periods.length === 0 && <option value="">No data-backed periods</option>}
            {reportingContext.periods.map((period) => (
              <option key={period.key} value={period.key}>
                {period.label}
              </option>
            ))}
          </select>
        </label>
        <div className="context-meta" title="Reporting periods are created by Xero when source activity exists.">
          <span>Xero freshness</span>
          <strong>{formatRelative(selectedPackage.lastXeroSyncAt)}</strong>
        </div>
        <div className="context-meta" title="Trial Balance reconciliation status is checked by the Xero coverage job.">
          <span>TB</span>
          <strong>{selectedPeriod?.ledgerActivityCount ? `${selectedPeriod.ledgerActivityCount} journals` : 'No journals'}</strong>
        </div>
        <div className={selectedPackage.isSourceDataStale ? 'context-meta stale' : 'context-meta'} title={selectedPackage.sourceDataStaleReason ?? 'Package is current with known source data.'}>
          <span>Package</span>
          <strong>{selectedPackage.isSourceDataStale ? 'Stale' : selectedPackageOption ? 'Current' : 'Not started'}</strong>
        </div>
      </div>
      <div className="spacer" />
      <div className="segmented">
        <SegmentButton active={view === 'dashboard' || view === 'slide'} onClick={() => setView('dashboard')} icon={<Boxes size={15} />}>
          Editor
        </SegmentButton>
        <SegmentButton active={view === 'livedash'} onClick={() => setView('livedash')} icon={<Gauge size={15} />}>
          Live Dashboard
        </SegmentButton>
        <SegmentButton active={view === 'output'} onClick={() => setView('output')} icon={<Share2 size={15} />}>
          Share
        </SegmentButton>
      </div>
      <button className="icon-button" onClick={() => setView('settings')} title="Settings">
        <Settings size={17} />
      </button>
      <div className="avatar">MP</div>
    </header>
  )
}

function Sidebar({
  packageData,
  view,
  selectedSlideId,
  openSlide,
  setView,
  recompile,
  hasPackage,
}: {
  packageData: PackageDto
  view: View
  selectedSlideId: string
  openSlide: (id: string) => void
  setView: (view: View) => void
  recompile: () => void
  hasPackage: boolean
}) {
  return (
    <aside className="sidebar">
      <div className="period-card">
        <span>Selected context</span>
        <strong>{packageData.organizationName}</strong>
        <small>{packageData.period} · {packageData.id ? packageData.versionLabel : 'data only'}</small>
      </div>
      <div className="rail-label">Workflow</div>
      <RailItem active={view === 'dashboard' || view === 'livedash'} icon={<Gauge size={15} />} onClick={() => setView('dashboard')}>
        Entity dashboard
      </RailItem>
      <RailItem active={view === 'mapping'} icon={<ListChecks size={15} />} onClick={() => setView('mapping')}>
        Mapping & eliminations
      </RailItem>
      <RailItem active={view === 'planning'} icon={<LineChart size={15} />} onClick={() => setView('planning')}>
        Planning & forecast
      </RailItem>
      <RailItem active={view === 'benchmarks'} icon={<BarChart3 size={15} />} onClick={() => setView('benchmarks')}>
        Benchmarks
      </RailItem>
      <RailItem active={view === 'flux'} icon={<MessageSquare size={15} />} onClick={() => setView('flux')}>
        Flux review
      </RailItem>
      <RailItem active={view === 'dashboard' || view === 'slide'} icon={<Boxes size={15} />} onClick={() => setView('dashboard')}>
        Financial package
      </RailItem>
      <RailItem active={view === 'statements'} icon={<FileSpreadsheet size={15} />} onClick={() => setView('statements')}>
        Statements & transactions
      </RailItem>
      <RailItem active={view === 'library'} icon={<History size={15} />} onClick={() => setView('library')}>
        Reporting library
      </RailItem>
      <RailItem active={view === 'parity'} icon={<CheckCircle2 size={15} />} onClick={() => setView('parity')}>
        Competitor parity
      </RailItem>
      <div className="rail-label">Package slides</div>
      {!hasPackage && <div className="rail-empty">No package has been created for this entity and period.</div>}
      {hasPackage && (
        <RailItem active={view === 'dashboard'} icon={<Boxes size={15} />} onClick={() => setView('dashboard')}>
          Package overview
        </RailItem>
      )}
      {packageData.slides.map((slide) => {
        const issue = packageData.issues.find((x) => x.packageSlideId === slide.id && x.status === 'Open')
        return (
          <RailItem key={slide.id} active={view === 'slide' && selectedSlideId === slide.id} badge={issue?.severity} icon={<span className="mono">0{slide.sortOrder}</span>} onClick={() => openSlide(slide.id)}>
            {slide.subject}
          </RailItem>
        )
      })}
      <div className="rail-label">Package tools</div>
      <RailItem disabled={!hasPackage} icon={<RefreshCw size={15} />} onClick={recompile}>
        Recompile package
      </RailItem>
      <RailItem active={view === 'output'} icon={<Link size={15} />} onClick={() => setView('output')}>
        Share & export
      </RailItem>
      <RailItem active={view === 'settings' || view === 'ai-settings' || view === 'xero-settings' || view === 'branding' || view === 'layouts'} icon={<Settings size={15} />} onClick={() => setView('settings')}>
        Settings
      </RailItem>
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

function StatementsView({ organizationKey, periodKey }: { organizationKey: string; periodKey: string }) {
  const [statements, setStatements] = useState<EntityStatements | null>(null)
  const [ledger, setLedger] = useState<EntityLedgerSummary | null>(null)

  useEffect(() => {
    if (!organizationKey || !periodKey) return
    Promise.all([
      fetchJson<EntityStatements>(`/api/entities/${organizationKey}/periods/${periodKey}/statements`),
      fetchJson<EntityLedgerSummary>(`/api/entities/${organizationKey}/periods/${periodKey}/ledger-summary`),
    ])
      .then(([nextStatements, nextLedger]) => {
        setStatements(nextStatements)
        setLedger(nextLedger)
      })
      .catch(() => {
        setStatements(null)
        setLedger(null)
      })
  }, [organizationKey, periodKey])

  const lines = statements?.lines ?? []
  const ledgerLines = ledger?.lines ?? []

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Statements & transactions</div>
          <h1>{organizationKey || 'Entity'} · {periodKey || 'Period'}</h1>
          <p>Statement rows and ledger summaries are sourced from Xero backfill and the rolling 15-minute sync.</p>
        </div>
      </div>
      <div className="statement-grid">
        <Card className="statement-card">
          <div className="section-title tight">
            <h2>Financial statements</h2>
            <span className="muted">{lines.length} normalized rows</span>
          </div>
          <div className="statement-table-wrap">
            <table className="xero-table">
              <thead>
                <tr><th>Statement</th><th>Line</th><th>Account</th><th>Amount</th><th>Prior</th></tr>
              </thead>
              <tbody>
                {lines.slice(0, 80).map((line, index) => (
                  <tr key={`${line.statementType}-${line.rowPath}-${index}`}>
                    <td>{line.statementType}</td>
                    <td><strong>{line.lineName}</strong><small>{line.section}</small></td>
                    <td>{line.accountCode || '—'}</td>
                    <td>{fmtMoney(line.currentAmount)}</td>
                    <td>{fmtMoney(line.priorAmount)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {lines.length === 0 && <div className="empty-state">No statement data has been imported for this entity-period yet.</div>}
          </div>
        </Card>
        <Card className="statement-card">
          <div className="section-title tight">
            <h2>Ledger summary</h2>
            <span className="muted">{ledger?.journalLineCount ?? 0} journal lines</span>
          </div>
          <div className="statement-table-wrap">
            <table className="xero-table">
              <thead>
                <tr><th>Account</th><th>Name</th><th>Net</th><th>Lines</th></tr>
              </thead>
              <tbody>
                {ledgerLines.slice(0, 80).map((line) => (
                  <tr key={line.accountCode}>
                    <td>{line.accountCode}</td>
                    <td>{line.accountName}</td>
                    <td>{fmtMoney(line.netAmount)}</td>
                    <td>{line.transactionCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {ledgerLines.length === 0 && <div className="empty-state">No ledger activity has been imported for this period.</div>}
          </div>
        </Card>
      </div>
    </div>
  )
}

function ExecutiveLibrary({ packageData }: { packageData: PackageDto }) {
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

function Dashboard({
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

      {packageData.isSourceDataStale && (
        <div className="alert-strip warn">
          <RefreshCw size={16} /> {packageData.sourceDataStaleReason ?? 'New Xero ledger activity was imported after this package was built.'}
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
              <small>{slide.blocks.length} blocks · {slide.accountCodesCsv.split(',').length} GL accts</small>
            </div>
            <Sparkline current={parseJson<number[]>(slide.monthlyJson, [])} prior={parseJson<number[]>(slide.priorMonthlyJson, [])} />
          </button>
        ))}
      </div>

      <IssueWorkbench packageData={packageData} refreshPackages={refreshPackages} />
    </div>
  )
}

function IssueWorkbench({ packageData, refreshPackages }: { packageData: PackageDto; refreshPackages: () => Promise<void> }) {
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

function FluxReviewPanel({
  packageData,
  refreshPackages,
  standalone = false,
  onAiRunQueued,
}: {
  packageData: PackageDto
  refreshPackages: () => Promise<void>
  standalone?: boolean
  onAiRunQueued?: (run: AiRun) => void
}) {
  const [review, setReview] = useState<FluxReview | null>(null)
  const [busy, setBusy] = useState(false)
  const [editing, setEditing] = useState<string | null>(null)
  const [explanation, setExplanation] = useState('')
  const [fluxType, setFluxType] = useState<'MonthOverMonth' | 'YearOverYear'>('MonthOverMonth')
  const [statementType, setStatementType] = useState<'ProfitAndLoss' | 'BalanceSheet'>('ProfitAndLoss')
  const [selectedGroupId, setSelectedGroupId] = useState('')
  const [drilldown, setDrilldown] = useState<FluxReviewDrilldown | null>(null)
  const [selectedAccountCode, setSelectedAccountCode] = useState('')
  const [aiRun, setAiRun] = useState<AiRun | null>(null)
  const [aiSuggestion, setAiSuggestion] = useState('')
  const [statusFilter, setStatusFilter] = useState<'all' | 'required' | 'open' | 'prepared' | 'reviewed' | 'assigned' | 'late' | 'ungrouped'>('required')
  const [search, setSearch] = useState('')
  const [settingsForm, setSettingsForm] = useState({
    dollarThreshold: '0',
    percentThreshold: '10',
    thresholdLogic: 'OR',
    assignee: '',
    reviewer: '',
    dueDate: '',
    tags: '',
    applyScope: 'period',
    explanationTemplate: '',
  })

  const load = useCallback(async () => {
    const next = await fetchJson<FluxReview>(`/api/packages/${packageData.id}/flux-review`)
    setReview(next)
  }, [packageData.id])

  useEffect(() => {
    let active = true
    fetchJson<FluxReview>(`/api/packages/${packageData.id}/flux-review`)
      .then((next) => {
        if (active) setReview(next)
      })
      .catch(() => {
        if (active) setReview(null)
      })
    return () => {
      active = false
    }
  }, [packageData.id])

  const refreshFlux = async () => {
    setBusy(true)
    try {
      const next = await postJson<FluxReview>(`/api/packages/${packageData.id}/refresh-flux`, {})
      setReview(next)
      await refreshPackages()
    } finally {
      setBusy(false)
    }
  }

  const pullLedgerDetail = async () => {
    setBusy(true)
    try {
      const next = await postJson<FluxReview>(`/api/packages/${packageData.id}/pull-ledger-detail`, {})
      setReview(next)
      await refreshPackages()
    } finally {
      setBusy(false)
    }
  }

  const saveExplanation = async (groupId: string) => {
    await putJson(`/api/flux-review/groups/${groupId}/explanation`, {
      explanation,
      reason: 'Flux review explanation',
    })
    setEditing(null)
    setExplanation('')
    await load()
  }

  const approve = async (groupId: string) => {
    await postJson(`/api/flux-review/groups/${groupId}/approve`, {})
    await load()
  }

  const signOff = async (groupId: string, action: 'prepare' | 'review') => {
    await postJson(`/api/flux-review/groups/${groupId}/sign-off`, {
      action,
      reason: action === 'review' ? 'Flux group reviewed' : 'Flux group prepared',
    })
    await load()
  }

  const saveSettings = async () => {
    if (!selectedGroup) return
    await putJson(`/api/flux-review/groups/${selectedGroup.id}/settings`, {
      dollarThreshold: Number(settingsForm.dollarThreshold || 0),
      percentThreshold: Number(settingsForm.percentThreshold || 0),
      thresholdLogic: settingsForm.thresholdLogic,
      assignee: settingsForm.assignee,
      reviewer: settingsForm.reviewer,
      dueDate: settingsForm.dueDate || null,
      explanationTemplate: settingsForm.explanationTemplate,
      tags: settingsForm.tags,
      applyScope: settingsForm.applyScope,
      reason: 'Updated flux workflow settings',
    })
    await load()
  }

  const rollForwardExplanation = async () => {
    if (!selectedGroup) return
    await postJson(`/api/flux-review/groups/${selectedGroup.id}/roll-forward-explanation`, {})
    await load()
    await loadDrilldown(selectedGroup.id)
  }

  const exportFluxCsv = () => {
    window.open(`${API_BASE}/api/packages/${packageData.id}/flux-review/export.csv`, '_blank', 'noopener,noreferrer')
  }

  const loadDrilldown = useCallback(async (groupId: string) => {
    setDrilldown(null)
    setSelectedAccountCode('')
    const next = await fetchJson<FluxReviewDrilldown>(`/api/flux-review/groups/${groupId}/drilldown`)
    setDrilldown(next)
    setSelectedAccountCode(next.accounts[0]?.accountCode ?? '')
  }, [])

  const queueAiExplanation = async () => {
    if (!selectedGroupId) return
    setBusy(true)
    setAiSuggestion('')
    try {
      const run = await postJson<AiRun>(`/api/flux-review/groups/${selectedGroupId}/ai-explain`, {})
      setAiRun(run)
      onAiRunQueued?.(run)
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    if (!aiRun || !['Queued', 'Running'].includes(aiRun.status)) return
    const interval = window.setInterval(async () => {
      const next = await fetchJson<AiRun>(`/api/ai/runs/${aiRun.id}`)
      setAiRun(next)
      if (next.status === 'Completed') {
        const parsed = parseJson<{ suggestedExplanation?: string }>(next.outputJson, {})
        setAiSuggestion(parsed.suggestedExplanation ?? '')
      }
    }, 1600)
    return () => window.clearInterval(interval)
  }, [aiRun])

  const useAiSuggestion = async () => {
    if (!selectedGroupId || !aiSuggestion) return
    await putJson(`/api/flux-review/groups/${selectedGroupId}/explanation`, {
      explanation: aiSuggestion,
      reason: 'Accepted AI variance explanation',
    })
    setAiSuggestion('')
    setEditing(null)
    setExplanation('')
    await load()
    await refreshPackages()
  }

  const allGroups = (review?.groups ?? []).filter((group) => group.fluxType === fluxType && group.statementType === statementType)
  const normalizedSearch = search.trim().toLowerCase()
  const groups = allGroups.filter((group) => {
    const matchesSearch = !normalizedSearch ||
      group.groupName.toLowerCase().includes(normalizedSearch) ||
      group.status.toLowerCase().includes(normalizedSearch) ||
      group.assignee.toLowerCase().includes(normalizedSearch) ||
      group.reviewer.toLowerCase().includes(normalizedSearch) ||
      group.tags.toLowerCase().includes(normalizedSearch)
    const isLate = !!group.dueDate && new Date(`${group.dueDate}T23:59:59`).getTime() < Date.now() && group.status !== 'Approved'
    const matchesStatus =
      statusFilter === 'all' ||
      (statusFilter === 'required' && group.requiresExplanation) ||
      (statusFilter === 'open' && group.requiresExplanation && !group.explanation && group.status !== 'Approved') ||
      (statusFilter === 'prepared' && (group.status === 'Prepared' || !!group.preparedBy)) ||
      (statusFilter === 'reviewed' && (group.status === 'Approved' || !!group.reviewedBy)) ||
      (statusFilter === 'assigned' && (!!group.assignee || !!group.reviewer)) ||
      (statusFilter === 'late' && isLate) ||
      (statusFilter === 'ungrouped' && group.groupName.toLowerCase().includes('ungrouped'))
    return matchesSearch && matchesStatus
  })
  const required = groups.filter((group) => group.requiresExplanation)
  const openRequired = required.filter((group) => !group.explanation && group.status !== 'Approved')
  const ledgerNeeded = groups.filter((group) => group.requiresLedgerDetail && !['Available', 'Pulled'].includes(group.ledgerDetailStatus)).length
  const selectedGroup = groups.find((group) => group.id === selectedGroupId) ?? groups[0]
  const selectedAccount = drilldown?.accounts.find((account) => account.accountCode === selectedAccountCode) ?? drilldown?.accounts[0]
  const selectedRiskFlags = parseJson<string[]>(selectedGroup?.riskFlagsJson ?? '[]', [])
  const selectedTrend = parseJson<Array<{ periodKey: string; amount: number }>>(selectedGroup?.trendJson ?? '[]', [])

  useEffect(() => {
    if (!selectedGroup) {
      setSelectedGroupId('')
      setDrilldown(null)
      return
    }
    if (selectedGroup.id !== selectedGroupId) {
      setSelectedGroupId(selectedGroup.id)
      loadDrilldown(selectedGroup.id).catch(() => setDrilldown(null))
    }
  }, [selectedGroup, selectedGroupId, loadDrilldown])

  useEffect(() => {
    if (!selectedGroup) return
    setSettingsForm({
      dollarThreshold: String(selectedGroup.dollarThreshold ?? 0),
      percentThreshold: String(selectedGroup.percentThreshold ?? 10),
      thresholdLogic: selectedGroup.thresholdLogic || 'OR',
      assignee: selectedGroup.assignee ?? '',
      reviewer: selectedGroup.reviewer ?? '',
      dueDate: selectedGroup.dueDate ?? '',
      tags: selectedGroup.tags ?? '',
      applyScope: 'period',
      explanationTemplate: selectedGroup.explanationTemplate ?? '',
    })
  }, [selectedGroup?.id])

  return (
    <section className="workbench">
      <div className="section-title tight">
        <div>
          <h2>Flux review</h2>
          <span className="muted">{openRequired.length} required explanations open · {allGroups.length} {statementLabel(statementType)} groups · thresholds can be tuned by FS line</span>
        </div>
        <div className="button-row">
          <Button variant="ghost" icon={<Download size={15} />} onClick={exportFluxCsv}>Export CSV</Button>
          <Button variant="secondary" icon={<RefreshCw size={15} />} disabled={busy} onClick={refreshFlux}>Refresh flux</Button>
          <Button variant="secondary" icon={<Database size={15} />} disabled={busy} onClick={pullLedgerDetail}>Pull ledger detail</Button>
        </div>
      </div>
      {review?.isSourceDataStale && <div className="alert-strip warn compact">{review.sourceDataStaleReason || 'Source data changed since this flux was built.'}</div>}
      {review?.progress && (
        <div className="flux-progress-grid">
          <div><span>Required</span><strong>{review.progress.requiredExplanations}</strong></div>
          <div><span>Open</span><strong>{review.progress.openExplanations}</strong></div>
          <div><span>Auto signed-off</span><strong>{review.progress.autoSignedOff}</strong></div>
          <div><span>Prepared</span><strong>{review.progress.prepared}</strong></div>
          <div><span>Reviewed</span><strong>{review.progress.reviewed}</strong></div>
        </div>
      )}
      <div className="segmented compact">
        <SegmentButton active={fluxType === 'MonthOverMonth'} onClick={() => setFluxType('MonthOverMonth')}>Current month vs prior month</SegmentButton>
        <SegmentButton active={fluxType === 'YearOverYear'} onClick={() => setFluxType('YearOverYear')}>Current year vs prior year</SegmentButton>
      </div>
      <div className="segmented compact">
        <SegmentButton active={statementType === 'ProfitAndLoss'} onClick={() => setStatementType('ProfitAndLoss')}>P&amp;L flux</SegmentButton>
        <SegmentButton active={statementType === 'BalanceSheet'} onClick={() => setStatementType('BalanceSheet')}>Balance sheet flux</SegmentButton>
      </div>
      <div className="flux-toolbar">
        <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search groups, assignee, reviewer..." />
        <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as typeof statusFilter)}>
          <option value="required">Requires explanation</option>
          <option value="open">Open explanations</option>
          <option value="prepared">Prepared</option>
          <option value="reviewed">Reviewed</option>
          <option value="ungrouped">Ungrouped accounts</option>
          <option value="all">All groups</option>
        </select>
      </div>
      {ledgerNeeded > 0 && <div className="alert-strip warn compact">{ledgerNeeded} material groups need ledger detail. The app will pull detail automatically when TB/source data changed; use the button for an on-demand pull.</div>}
      {groups.length === 0 ? (
        <div className="empty-state">No {statementLabel(statementType)} groups match this filter. Sync Xero, refresh flux, or widen the filter.</div>
      ) : (
        <div className="flux-review-grid">
          <div className="issue-list">
            {groups.slice(0, standalone ? 80 : 8).map((group) => (
              <div className={`issue-row flux-row ${selectedGroup?.id === group.id ? 'selected' : ''}`} key={group.id} onClick={() => { setSelectedGroupId(group.id); loadDrilldown(group.id).catch(() => setDrilldown(null)) }}>
                <SeverityBadge severity={group.requiresExplanation ? 'Medium' : 'Low'} />
                <div className="issue-body">
                  <strong>{group.groupName}</strong>
                  <p>
                    Prior {fmtMoney(group.priorAmount)} · Current {fmtMoney(group.currentAmount)} · Change {fmtMoney(group.varianceAmount)} · {group.variancePercent.toFixed(1)}%
                    {group.fluxType === 'MonthOverMonth' ? ` · 3 mo ${fmtMoney(group.runningThreeMonthAmount)}` : ''}
                  </p>
                  <small>{group.currentPeriodKey} vs {group.priorPeriodKey} · {statementLabel(group.statementType)} · Ledger {group.ledgerDetailStatus}</small>
                  <div className="flux-row-meta">
                    <span className={`status-pill ${group.status === 'Approved' ? 'good' : group.requiresExplanation ? 'warn' : ''}`}>{group.status}</span>
                    {group.assignee && <span>Owner {group.assignee}</span>}
                    {group.dueDate && <span>Due {group.dueDate}</span>}
                    {group.autoSignedOff && <span>Auto signed-off</span>}
                  </div>
                  {editing === group.id ? (
                    <div className="inline-editor" onClick={(event) => event.stopPropagation()}>
                      <textarea value={explanation} onChange={(event) => setExplanation(event.target.value)} placeholder="Explain the variance using GL detail..." />
                      <Button variant="primary" onClick={() => saveExplanation(group.id)}>Save</Button>
                    </div>
                  ) : (
                    <small>{group.explanation || group.status}</small>
                  )}
                </div>
                <Button variant="ghost" onClick={(event) => { event.stopPropagation(); setEditing(group.id); setExplanation(group.explanation) }}>Explain</Button>
                <Button variant="secondary" onClick={(event) => { event.stopPropagation(); approve(group.id) }}>Review</Button>
              </div>
            ))}
          </div>
          <Card className="flux-detail-card">
            {!drilldown ? (
              <div className="empty-state">Select a flux line to see accounts and ledger detail.</div>
            ) : (
              <>
                <div className="section-title tight">
                  <div>
                    <div className="eyebrow">Flux drilldown</div>
                    <h3>{drilldown.groupName}</h3>
                    <span className="muted">{drilldown.currentPeriodKey} vs {drilldown.priorPeriodKey} · {drilldown.accounts.length} accounts</span>
                  </div>
                  <div className="button-row">
                    <Button variant="secondary" icon={<Check size={15} />} disabled={!selectedGroup} onClick={() => selectedGroup && signOff(selectedGroup.id, 'prepare')}>Prepare</Button>
                    <Button variant="secondary" icon={<CheckCircle2 size={15} />} disabled={!selectedGroup} onClick={() => selectedGroup && signOff(selectedGroup.id, 'review')}>Review</Button>
                    <Button variant="accent" icon={<Sparkles size={15} />} disabled={busy} onClick={queueAiExplanation}>AI analysis</Button>
                  </div>
                </div>
                {selectedGroup && (
                  <div className="flux-settings-panel">
                    <div>
                      <label>Dollar threshold</label>
                      <input value={settingsForm.dollarThreshold} onChange={(event) => setSettingsForm((current) => ({ ...current, dollarThreshold: event.target.value }))} />
                    </div>
                    <div>
                      <label>Percent threshold</label>
                      <input value={settingsForm.percentThreshold} onChange={(event) => setSettingsForm((current) => ({ ...current, percentThreshold: event.target.value }))} />
                    </div>
                    <div>
                      <label>Assignee</label>
                      <input value={settingsForm.assignee} onChange={(event) => setSettingsForm((current) => ({ ...current, assignee: event.target.value }))} placeholder="Finance owner" />
                    </div>
                    <div>
                      <label>Reviewer</label>
                      <input value={settingsForm.reviewer} onChange={(event) => setSettingsForm((current) => ({ ...current, reviewer: event.target.value }))} placeholder="Reviewer" />
                    </div>
                    <div>
                      <label>Due date</label>
                      <input value={settingsForm.dueDate} onChange={(event) => setSettingsForm((current) => ({ ...current, dueDate: event.target.value }))} placeholder="YYYY-MM-DD" />
                    </div>
                    <Button variant="secondary" onClick={saveSettings}>Save settings</Button>
                    <label className="wide">Explanation template</label>
                    <textarea className="wide" value={settingsForm.explanationTemplate} onChange={(event) => setSettingsForm((current) => ({ ...current, explanationTemplate: event.target.value }))} />
                  </div>
                )}
                {selectedRiskFlags.length > 0 && (
                  <div className="flux-chip-row">
                    {selectedRiskFlags.map((flag) => <span className="status-pill warn" key={flag}>{flag}</span>)}
                  </div>
                )}
                {selectedTrend.length > 0 && (
                  <div className="flux-trend-strip">
                    {selectedTrend.map((point) => (
                      <div key={point.periodKey}>
                        <span>{point.periodKey}</span>
                        <strong>{fmtMoney(point.amount)}</strong>
                      </div>
                    ))}
                  </div>
                )}
                {selectedGroup?.priorExplanation && (
                  <div className="ai-suggestion-box">
                    <div className="eyebrow">Prior explanation</div>
                    <p>{selectedGroup.priorExplanation}</p>
                    <Button variant="secondary" onClick={rollForwardExplanation}>Roll forward</Button>
                  </div>
                )}
                {drilldown.accounts.length === 0 ? (
                  <div className="empty-state">No accounts are attached to this flux line yet. Check mapping for ungrouped accounts.</div>
                ) : (
                  <div className="flux-account-list">
                    {drilldown.accounts.map((account) => (
                      <button className={`flux-account-row ${selectedAccount?.accountCode === account.accountCode ? 'selected' : ''}`} key={account.accountCode} onClick={() => setSelectedAccountCode(account.accountCode)}>
                        <span>
                          <strong>{account.accountCode} · {account.accountName}</strong>
                          <small>{account.fsLine || 'Ungrouped'} · {account.currentTransactions.length + account.priorTransactions.length} GL lines</small>
                        </span>
                        <span>{fmtMoney(account.varianceAmount)} · {account.variancePercent.toFixed(1)}%</span>
                      </button>
                    ))}
                  </div>
                )}
                {selectedAccount && (
                  <div className="ledger-detail">
                    <div className="ledger-detail-header">
                      <strong>{selectedAccount.accountCode} · {selectedAccount.accountName}</strong>
                      <span>Prior {fmtMoney(selectedAccount.priorAmount)} · Current {fmtMoney(selectedAccount.currentAmount)}</span>
                    </div>
                    <TransactionTable title={`Current period GL · ${drilldown.currentPeriodKey}`} rows={selectedAccount.currentTransactions} />
                    <TransactionTable title={`Prior period GL · ${drilldown.priorPeriodKey}`} rows={selectedAccount.priorTransactions} />
                  </div>
                )}
                {aiRun && (
                  <div className="ai-suggestion-box">
                    <div className="eyebrow">AI explanation</div>
                    <strong>{aiRun.status === 'Completed' ? 'Suggested explanation ready' : `${aiRun.status} · ${aiRun.progress}%`}</strong>
                    {aiSuggestion ? <p>{aiSuggestion}</p> : <p className="muted">Codex is reviewing the account and journal detail for this flux line.</p>}
                    {aiSuggestion && <Button variant="primary" onClick={useAiSuggestion}>Use explanation</Button>}
                  </div>
                )}
              </>
            )}
          </Card>
        </div>
      )}
    </section>
  )
}

function TransactionTable({ title, rows }: { title: string; rows: FluxLedgerTransaction[] }) {
  return (
    <div className="transaction-detail-table">
      <div className="table-header"><span>{title}</span><strong>{rows.length}</strong></div>
      {rows.length === 0 ? (
        <div className="empty-state compact">No journal lines loaded for this account and period.</div>
      ) : (
        <table className="mini-table full">
          <thead>
            <tr><th>Date</th><th>Journal</th><th>Description</th><th>Source</th><th>Amount</th></tr>
          </thead>
          <tbody>
            {rows.slice(0, 50).map((row, index) => (
              <tr key={`${row.journalNumber}-${index}`}>
                <td>{row.date}</td>
                <td>{row.journalNumber}</td>
                <td>{row.description || row.reference}</td>
                <td>{row.sourceType}</td>
                <td>{fmtMoney(row.netAmount)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

function AiDraftPanel({ packageData, refreshPackages }: { packageData: PackageDto; refreshPackages: () => Promise<void> }) {
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

function SlideEditor({
  packageId,
  slide,
  issues,
  refreshPackages,
  notify,
  openChat,
  openHistory,
}: {
  packageId: string
  slide: SlideDto
  issues: IssueDto[]
  refreshPackages: () => Promise<void>
  notify: (message: string) => void
  openChat: (blockId?: string) => void
  openHistory: () => void
}) {
  const [history, setHistory] = useState<{ past: SlideBlockDto[][]; present: SlideBlockDto[]; future: SlideBlockDto[][] }>({
    past: [],
    present: slide.blocks,
    future: [],
  })
  const [selectedBlock, setSelectedBlock] = useState<string | null>(null)
  const [dragging, setDragging] = useState<string | null>(null)
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'error'>('idle')
  const [comments, setComments] = useState<PackageCommentDto[]>([])
  const [commentBody, setCommentBody] = useState('')

  const blocks = history.present
  const loadComments = useCallback(async () => {
    const next = await fetchJson<PackageCommentDto[]>(`/api/packages/${packageId}/comments?slideId=${slide.id}`)
    setComments(next)
  }, [packageId, slide.id])

  useEffect(() => {
    loadComments().catch(() => setComments([]))
  }, [loadComments])

  const setBlocks = (next: SlideBlockDto[]) => {
    setHistory((current) => ({ past: [...current.past, current.present].slice(-30), present: next, future: [] }))
  }
  const undo = () =>
    setHistory((current) =>
      current.past.length === 0
        ? current
        : {
            past: current.past.slice(0, -1),
            present: current.past[current.past.length - 1],
            future: [current.present, ...current.future].slice(0, 30),
          },
    )
  const redo = () =>
    setHistory((current) =>
      current.future.length === 0
        ? current
        : {
            past: [...current.past, current.present].slice(-30),
            present: current.future[0],
            future: current.future.slice(1),
          },
    )
  const persist = async (action: () => Promise<unknown>, success?: string) => {
    setSaveState('saving')
    try {
      await action()
      setSaveState('idle')
      if (success) notify(success)
      await refreshPackages()
    } catch {
      setSaveState('error')
      notify('Could not save editor change.')
    }
  }
  const addBlock = (kind: string) => {
    const local = block(kind, defaultContent(kind, slide))
    const next = [...blocks, { ...local, sortOrder: blocks.length + 1 }]
    setBlocks(next)
    void persist(async () => postJson(`/api/slides/${slide.id}/blocks`, { kind, contentJson: local.contentJson, sortOrder: next.length }))
  }
  const updateBlock = (id: string, content: unknown) => {
    const contentJson = JSON.stringify(content)
    setBlocks(blocks.map((b) => (b.id === id ? { ...b, contentJson } : b)))
    void persist(async () => putJson(`/api/blocks/${id}`, { kind: blocks.find((b) => b.id === id)?.kind ?? 'text', contentJson }))
  }
  const removeBlock = (id: string) => {
    setBlocks(blocks.filter((b) => b.id !== id))
    void persist(async () => deleteJson(`/api/blocks/${id}`), 'Block removed.')
  }
  const moveDrag = (targetId: string) => {
    if (!dragging || dragging === targetId) return
    const moving = blocks.find((b) => b.id === dragging)
    if (!moving) return
    const without = blocks.filter((b) => b.id !== dragging)
    const targetIndex = without.findIndex((b) => b.id === targetId)
    const next = [...without.slice(0, targetIndex), moving, ...without.slice(targetIndex)].map((block, index) => ({ ...block, sortOrder: index + 1 }))
    setBlocks(next)
    setDragging(null)
    void persist(async () => postJson(`/api/slides/${slide.id}/reorder-blocks`, { blockIds: next.map((block) => block.id) }))
  }
  const addComment = async () => {
    if (!commentBody.trim()) return
    const saved = await postJson<PackageCommentDto>(`/api/packages/${packageId}/comments`, {
      packageSlideId: slide.id,
      slideBlockId: selectedBlock,
      body: commentBody,
      status: 'Open',
      author: 'Finance reviewer',
    })
    setComments((current) => [saved, ...current])
    setCommentBody('')
    notify('Comment added.')
  }
  const resolveComment = async (comment: PackageCommentDto) => {
    const saved = await putJson<PackageCommentDto>(`/api/comments/${comment.id}`, {
      packageSlideId: comment.packageSlideId,
      slideBlockId: comment.slideBlockId,
      body: comment.body,
      status: 'Resolved',
      author: comment.author,
    })
    setComments((current) => current.map((item) => (item.id === saved.id ? saved : item)))
  }

  return (
    <div className="page narrow">
      <div className="slide-header">
        <div>
          <div className="eyebrow">
            {String(slide.sortOrder).padStart(2, '0')} · {slide.accountCodesCsv.split(',').length} GL accounts · YTD through Nov 2025
          </div>
          <h1>{slide.subject}</h1>
        </div>
        <div className="actions">
          <span className={saveState === 'error' ? 'bad-text mono' : 'muted mono'}>{saveState === 'saving' ? 'Saving...' : saveState === 'error' ? 'Save failed' : 'Saved'}</span>
          <Button variant="ghost" icon={<Undo2 size={15} />} disabled={history.past.length === 0} onClick={undo}>
            Undo
          </Button>
          <Button variant="ghost" icon={<RefreshCw size={15} />} disabled={history.future.length === 0} onClick={redo}>
            Redo
          </Button>
          <Button variant="ghost" icon={<History size={15} />} onClick={openHistory}>
            History
          </Button>
          <Button variant="accent" icon={<Sparkles size={15} />} onClick={() => openChat()}>
            AI on slide
          </Button>
        </div>
      </div>
      <InsertBar onAdd={addBlock} />
      <div className="block-list">
        {blocks.map((b) => (
          <SlideBlock
            key={b.id}
            block={b}
            slide={slide}
            selected={selectedBlock === b.id}
            setSelected={() => setSelectedBlock(b.id)}
            onUpdate={(content) => updateBlock(b.id, content)}
            onRemove={() => removeBlock(b.id)}
            onChat={() => openChat(b.id)}
            onDragStart={() => setDragging(b.id)}
            onDragOver={(event) => event.preventDefault()}
            onDrop={() => moveDrag(b.id)}
          />
        ))}
      </div>
      {issues.length > 0 && (
        <div className="issue-card">
          <div className="eyebrow">AI-Flagged Issues ({issues.length})</div>
          {issues.map((issue) => (
            <div className="inline-issue" key={issue.id}>
              <SeverityBadge severity={issue.severity} />
              <div>
                <strong>{issue.title}</strong>
                <p>{issue.description}</p>
              </div>
            </div>
          ))}
        </div>
      )}
      <Card className="comment-panel">
        <div className="table-header">
          <strong>Stakeholder comments</strong>
          <span>{comments.filter((comment) => comment.status !== 'Resolved').length} open</span>
        </div>
        <div className="comment-compose">
          <textarea value={commentBody} onChange={(event) => setCommentBody(event.target.value)} placeholder="Add a review note" />
          <Button variant="primary" icon={<MessageSquare size={14} />} onClick={addComment}>Comment</Button>
        </div>
        <div className="comment-list">
          {comments.map((comment) => (
            <div key={comment.id} className={comment.status === 'Resolved' ? 'comment-item resolved' : 'comment-item'}>
              <div>
                <strong>{comment.author}</strong>
                <p>{comment.body}</p>
                <small>{comment.slideBlockId ? 'Block note' : 'Slide note'} · {new Date(comment.createdAt).toLocaleDateString()}</small>
              </div>
              {comment.status !== 'Resolved' && <Button variant="ghost" icon={<Check size={13} />} onClick={() => resolveComment(comment)}>Resolve</Button>}
            </div>
          ))}
          {comments.length === 0 && <div className="empty-state compact">No comments on this slide yet.</div>}
        </div>
      </Card>
    </div>
  )
}

function SlideBlock({
  block,
  slide,
  selected,
  setSelected,
  onUpdate,
  onRemove,
  onChat,
  onDragStart,
  onDragOver,
  onDrop,
}: {
  block: SlideBlockDto
  slide: SlideDto
  selected: boolean
  setSelected: () => void
  onUpdate: (content: unknown) => void
  onRemove: () => void
  onChat: () => void
  onDragStart: () => void
  onDragOver: (event: React.DragEvent) => void
  onDrop: () => void
}) {
  const content = parseJson<Record<string, unknown>>(block.contentJson, {})
  return (
    <article
      className={selected ? 'editor-block selected' : 'editor-block'}
      draggable
      onDragStart={onDragStart}
      onDragOver={onDragOver}
      onDrop={onDrop}
      onClick={setSelected}
    >
      <div className="block-toolbar">
        <span className="block-kind">{block.kind}</span>
        <button title="Ask AI about this block" onClick={onChat}>
          <MessageSquare size={14} />
        </button>
        <button title="Delete block" onClick={onRemove}>
          <X size={14} />
        </button>
      </div>
      {block.kind === 'kpi' && (
        <div className="kpi-block">
          <div>
            <span>{String(content.label ?? slide.kpiLabel)}</span>
            <strong>{fmtMoney(Number(content.current ?? slide.currentValue))}</strong>
          </div>
          <div className={slide.varianceAmount >= 0 ? 'good-text mono' : 'bad-text mono'}>
            {slide.varianceAmount >= 0 ? '▲' : '▼'} {fmtMoney(Math.abs(slide.varianceAmount))} · {Math.abs(slide.variancePercent).toFixed(1)}%
          </div>
        </div>
      )}
      {block.kind === 'chart' && <ColumnChart current={parseJson<number[]>(slide.monthlyJson, [])} prior={parseJson<number[]>(slide.priorMonthlyJson, [])} />}
      {block.kind === 'drivers' && (
        <div className="driver-list">
          {slide.accountCodesCsv.split(',').map((account, index) => (
            <div key={account}>
              <span className="mono">{account}</span>
              <strong>{['Customer acquisition', 'Vendor partner mix', 'Payroll timing'][index] ?? 'Variance driver'}</strong>
              <small>{fmtMoney(Math.abs(slide.varianceAmount) / (index + 2))}</small>
            </div>
          ))}
        </div>
      )}
      {block.kind === 'text' && (
        <textarea
          value={String(content.text ?? '')}
          onChange={(event) => onUpdate({ ...content, text: event.target.value })}
          rows={4}
        />
      )}
      {block.kind === 'callout' && (
        <div className="callout-block">
          <Sparkles size={16} />
          <textarea value={String(content.text ?? 'Highlighted insight')} onChange={(event) => onUpdate({ ...content, text: event.target.value })} rows={2} />
        </div>
      )}
      {block.kind === 'table' && (
        <table className="mini-table">
          <tbody>
            <tr>
              <th>Metric</th>
              <th>Current</th>
              <th>Prior</th>
            </tr>
            <tr>
              <td>{slide.kpiLabel}</td>
              <td>{fmtMoney(slide.currentValue)}</td>
              <td>{fmtMoney(slide.priorValue)}</td>
            </tr>
          </tbody>
        </table>
      )}
      {block.kind === 'divider' && <hr />}
      {block.kind === 'image' && <div className="image-placeholder">Image / upload block</div>}
    </article>
  )
}

function MappingView({
  organizationKey,
  periodKey,
  refreshKey,
  openAccount,
}: {
  organizationKey: string
  periodKey: string
  refreshKey: number
  openAccount: (id: string) => void
}) {
  const [accounts, setAccounts] = useState<AccountDto[]>([])
  const [fsLines, setFsLines] = useState<FsLineDefinitionDto[]>([])
  const [filter, setFilter] = useState<'all' | 'new' | 'review'>('all')
  const [loadFailed, setLoadFailed] = useState(false)
  const [lineDraft, setLineDraft] = useState({
    statementType: 'IncomeStatement',
    section: 'Revenue',
    name: '',
    normalBalance: 'Credit',
    aiGuidance: '',
  })

  useEffect(() => {
    if (!organizationKey || !periodKey) return
    Promise.all([
      fetchJson<AccountDto[]>(`/api/mapping/accounts?organizationKey=${encodeURIComponent(organizationKey)}&periodKey=${encodeURIComponent(periodKey)}`),
      fetchJson<FsLineDefinitionDto[]>(`/api/mapping/fs-lines?organizationKey=${encodeURIComponent(organizationKey)}`),
    ])
      .then(([nextAccounts, nextLines]) => {
        setAccounts(nextAccounts)
        setFsLines(nextLines)
        setLoadFailed(false)
      })
      .catch(() => {
        setAccounts([])
        setFsLines([])
        setLoadFailed(true)
      })
  }, [organizationKey, periodKey, refreshKey])

  const activeLines = fsLines.filter((line) => line.isActive)
  const groupedLines = activeLines.reduce<Record<string, FsLineDefinitionDto[]>>((groups, line) => {
    const key = `${formatStatementType(line.statementType)} · ${line.section}`
    groups[key] = groups[key] ? [...groups[key], line] : [line]
    return groups
  }, {})

  const filtered = accounts.filter((account) => {
    if (filter === 'new') return account.isFirstSeen
    if (filter === 'review') return account.reviewStatus !== 'Reviewed'
    return true
  })

  const createFsLine = async () => {
    if (!lineDraft.name.trim()) return
    const created = await postJson<FsLineDefinitionDto>('/api/mapping/fs-lines', {
      organizationKey,
      ...lineDraft,
      reason: 'Created from mapping inbox',
    })
    setFsLines((lines) => [...lines, created])
    setLineDraft((draft) => ({ ...draft, name: '', aiGuidance: '' }))
  }

  const deactivateFsLine = async (lineId: string) => {
    const updated = await deleteJson<FsLineDefinitionDto>(`/api/mapping/fs-lines/${lineId}`)
    setFsLines((lines) => lines.map((line) => (line.id === updated.id ? updated : line)))
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Consolidation</div>
          <h1>Account Mapping & Eliminations</h1>
          <p>Accounts are scoped to the selected entity and period. New Xero accounts surface here after the rolling sync creates the period.</p>
        </div>
        <div className="segmented compact">
          <SegmentButton active={filter === 'all'} onClick={() => setFilter('all')}>All</SegmentButton>
          <SegmentButton active={filter === 'new'} onClick={() => setFilter('new')}>New</SegmentButton>
          <SegmentButton active={filter === 'review'} onClick={() => setFilter('review')}>Review</SegmentButton>
        </div>
      </div>
      <div className="mapping-grid">
        <Card className="mapping-inbox">
          <div className="table-header">
            <strong>Mapping inbox</strong>
            <span>{filtered.length} accounts</span>
          </div>
          {loadFailed && <div className="alert-strip warn"><AlertTriangle size={15} /> Mapping data could not be loaded for this entity-period.</div>}
          <table>
            <thead>
              <tr>
                <th>Account</th>
                <th>FS Line</th>
                <th>AI</th>
                <th>Treatment</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((account) => (
                <tr key={account.id}>
                  <td>
                    <button className="account-link" onClick={() => openAccount(account.id)}>
                      <span className="mono">{account.code}</span>
                      <strong>{account.name}</strong>
                      {account.isFirstSeen && <span className="new-badge">New</span>}
                    </button>
                  </td>
                  <td>{account.fsLine}</td>
                  <td>
                    <span className="confidence">{Math.round(account.mappingConfidence * 100)}%</span>
                    {account.aiSuggestedFsLine}
                  </td>
                  <td>{account.consolidationTreatment}</td>
                  <td>
                    <PanelRightOpen size={15} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {!loadFailed && filtered.length === 0 && <div className="empty-state">No accounts have been imported for this entity-period yet.</div>}
        </Card>
        <Card className="fs-line-card">
          <div className="table-header">
            <strong>FS line library</strong>
            <span>{activeLines.length} active</span>
          </div>
          <div className="fs-line-form">
            <div className="field-row">
              <label className="field">
                <span>Statement</span>
                <select value={lineDraft.statementType} onChange={(event) => setLineDraft((draft) => ({ ...draft, statementType: event.target.value }))}>
                  <option value="IncomeStatement">Income statement</option>
                  <option value="BalanceSheet">Balance sheet</option>
                  <option value="TrialBalance">Trial balance</option>
                </select>
              </label>
              <label className="field">
                <span>Normal</span>
                <select value={lineDraft.normalBalance} onChange={(event) => setLineDraft((draft) => ({ ...draft, normalBalance: event.target.value }))}>
                  <option value="Credit">Credit</option>
                  <option value="Debit">Debit</option>
                </select>
              </label>
            </div>
            <label className="field">
              <span>Group</span>
              <input value={lineDraft.section} onChange={(event) => setLineDraft((draft) => ({ ...draft, section: event.target.value }))} placeholder="Revenue, COGS, Assets..." />
            </label>
            <label className="field">
              <span>FS line</span>
              <input value={lineDraft.name} onChange={(event) => setLineDraft((draft) => ({ ...draft, name: event.target.value }))} placeholder="Revenue - Implementation" />
            </label>
            <label className="field">
              <span>AI guidance</span>
              <textarea value={lineDraft.aiGuidance} onChange={(event) => setLineDraft((draft) => ({ ...draft, aiGuidance: event.target.value }))} rows={3} placeholder="Tell AI what kinds of accounts belong here." />
            </label>
            <Button variant="primary" icon={<Plus size={14} />} onClick={createFsLine}>
              Add FS line
            </Button>
          </div>
          <div className="fs-line-list">
            {Object.entries(groupedLines).map(([group, lines]) => (
              <div className="fs-line-group" key={group}>
                <span>{group}</span>
                {lines.map((line) => (
                  <div className="fs-line-row" key={line.id}>
                    <strong>{line.name}</strong>
                    <small>{line.normalBalance}</small>
                    <button title="Deactivate FS line" onClick={() => deactivateFsLine(line.id)}>
                      <X size={13} />
                    </button>
                  </div>
                ))}
              </div>
            ))}
            {activeLines.length === 0 && <div className="empty-state">Create FS lines before mapping accounts.</div>}
          </div>
          <div className="elim-divider" />
          <div className="elim-option">
            <strong>Eliminations stay here too</strong>
            <p>Open an account drilldown to eliminate, exclude, split, or create an intercompany rule with audit metadata.</p>
          </div>
        </Card>
      </div>
    </div>
  )
}

function AccountPanel({ accountId, organizationKey, onClose, onChanged }: { accountId: string; organizationKey: string; onClose: () => void; onChanged: () => void }) {
  const [detail, setDetail] = useState<AccountDetailDto | null>(null)
  const [fsLines, setFsLines] = useState<FsLineDefinitionDto[]>([])
  const [loadFailed, setLoadFailed] = useState(false)
  const [reason, setReason] = useState('Reviewed from mapping inbox')
  const [fsLine, setFsLine] = useState('')

  useEffect(() => {
    Promise.all([
      fetchJson<AccountDetailDto>(`/api/mapping/accounts/${accountId}`),
      fetchJson<FsLineDefinitionDto[]>(`/api/mapping/fs-lines?organizationKey=${encodeURIComponent(organizationKey)}`),
    ])
      .then(([data, lines]) => {
        const active = lines.filter((line) => line.isActive)
        const preferredLine = data.account.aiSuggestedFsLine || data.account.fsLine
        setDetail(data)
        setFsLines(active)
        setFsLine(active.some((line) => line.name === preferredLine) ? preferredLine : (active[0]?.name ?? ''))
        setLoadFailed(false)
      })
      .catch(() => {
        setDetail(null)
        setFsLines([])
        setLoadFailed(true)
      })
  }, [accountId, organizationKey])

  const account = detail?.account
  const acceptMapping = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/map`, { fsLine, reason })
    onChanged()
    onClose()
  }
  const splitMapping = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/split`, {
      reason,
      lines: [
        { fsLine, percent: 70 },
        { fsLine: `${fsLine} — Other`, percent: 30 },
      ],
    })
    onChanged()
    onClose()
  }
  const markReviewed = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/mark-reviewed`, { reason })
    onChanged()
    onClose()
  }
  const rejectMapping = async () => {
    await postJson(`/api/mapping/accounts/${accountId}/reject`, { reason })
    onChanged()
    onClose()
  }
  const eliminate = async (type: string) => {
    await postJson(`/api/mapping/accounts/${accountId}/eliminate`, {
      type,
      description: `${type} ${account?.code}`,
      reason,
      createRecurringRule: type === 'intercompany',
    })
    onChanged()
    onClose()
  }

  return (
    <SidePanel title="Account drilldown" subtitle={account ? `${account.code} · ${account.name}` : 'Loading'} onClose={onClose}>
      {loadFailed && <div className="alert-strip warn"><AlertTriangle size={15} /> Account detail or FS lines could not be loaded.</div>}
      {account && (
        <>
          <div className="panel-card">
            <div className="account-title">
              <span className="mono">{account.code}</span>
              {account.isFirstSeen && <span className="new-badge">New</span>}
            </div>
            <strong>{account.name}</strong>
            <p>{account.type} · {account.tenantId}</p>
            <Sparkline current={parseJson<number[]>(account.monthlyBalancesJson, [])} />
          </div>
          <label className="field">
            <span>FS line</span>
            <select value={fsLine} onChange={(event) => setFsLine(event.target.value)}>
              <option value="">Select an FS line</option>
              {fsLines.map((line) => (
                <option value={line.name} key={line.id}>{formatStatementType(line.statementType)} · {line.section} · {line.name}</option>
              ))}
            </select>
          </label>
          {fsLines.length === 0 && <div className="alert-strip warn"><AlertTriangle size={15} /> Create FS lines in the mapping library before accepting mappings.</div>}
          <label className="field">
            <span>Audit reason</span>
            <textarea value={reason} onChange={(event) => setReason(event.target.value)} rows={3} />
          </label>
          <div className="panel-actions">
            <Button variant="primary" icon={<Check size={14} />} disabled={!fsLine} onClick={acceptMapping}>
              Accept mapping
            </Button>
            <Button variant="secondary" disabled={!fsLine} onClick={splitMapping}>
              Split
            </Button>
            <Button variant="secondary" onClick={markReviewed}>
              Reviewed
            </Button>
            <Button variant="ghost" onClick={rejectMapping}>
              Reject
            </Button>
            <Button variant="secondary" onClick={() => eliminate('eliminate')}>
              Eliminate
            </Button>
            <Button variant="secondary" onClick={() => eliminate('intercompany')}>
              Intercompany
            </Button>
            <Button variant="ghost" onClick={() => eliminate('exclude')}>
              Exclude
            </Button>
          </div>
          <div className="table-header panel-table-title">
            <strong>Transactions</strong>
            <span>{detail.transactions.length}</span>
          </div>
          <div className="transaction-list">
            {detail.transactions.map((tx) => (
              <div key={tx.id}>
                <span className="mono">{tx.transactionDate}</span>
                <strong>{tx.description}</strong>
                <small>{fmtMoney(tx.credit - tx.debit)}</small>
              </div>
            ))}
          </div>
        </>
      )}
    </SidePanel>
  )
}

function PlanningView({ packageData }: { packageData: PackageDto }) {
  const [overview, setOverview] = useState<PlanningOverviewDto | null>(null)
  const [selectedScenarioId, setSelectedScenarioId] = useState('')
  const [draft, setDraft] = useState<Partial<ForecastScenarioDto>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [cashGranularity, setCashGranularity] = useState<'Weekly' | 'Daily'>('Weekly')
  const [cashTiming, setCashTiming] = useState<CashTimingDto | null>(null)

  const load = useCallback(async () => {
    const data = await fetchJson<PlanningOverviewDto>(`/api/planning/${packageData.id}/overview`)
    setOverview(data)
    setSelectedScenarioId((current) => current || data.scenarios[0]?.id || '')
  }, [packageData.id])

  useEffect(() => {
    load().catch(() => setOverview(null))
  }, [load])

  const active = overview?.scenarios.find((scenario) => scenario.id === selectedScenarioId) ?? overview?.scenarios[0]
  const editable = active ? { ...active, ...draft } : null
  const rows = editable?.rows ?? []
  const firstBreach = rows.find((row) => row.cashThresholdBreached)
  const lastRow = rows[rows.length - 1]

  useEffect(() => {
    if (!active) return
    fetchJson<CashTimingDto>(`/api/planning/${packageData.id}/cash-timing?scenarioId=${active.id}&granularity=${cashGranularity}&months=3`)
      .then(setCashTiming)
      .catch(() => setCashTiming(null))
  }, [active?.id, cashGranularity, packageData.id])

  const updateDraft = (patch: Partial<ForecastScenarioDto>) => setDraft((current) => ({ ...current, ...patch }))
  const scenarioPayload = (scenario: ForecastScenarioDto) => ({
    organizationId: scenario.organizationId,
    reportingPeriodId: scenario.reportingPeriodId,
    name: scenario.name,
    description: scenario.description,
    scenarioType: scenario.scenarioType,
    horizonMonths: scenario.horizonMonths,
    revenueGrowthPercent: scenario.revenueGrowthPercent,
    grossMarginPercent: scenario.grossMarginPercent,
    opexGrowthPercent: scenario.opexGrowthPercent,
    cashConversionPercent: scenario.cashConversionPercent,
    startingCash: scenario.startingCash,
    cashThreshold: scenario.cashThreshold,
    assumptionsJson: scenario.assumptionsJson,
    isBase: scenario.isBase,
  })

  const saveScenario = async () => {
    if (!editable) return
    setBusy('save')
    try {
      await putJson(`/api/planning/scenarios/${editable.id}`, scenarioPayload(editable))
      setDraft({})
      await load()
    } finally {
      setBusy(null)
    }
  }

  const cloneScenario = async () => {
    if (!active) return
    setBusy('clone')
    try {
      await postJson('/api/planning/scenarios', {
        ...scenarioPayload(active),
        name: `${active.name} copy`,
        scenarioType: 'Custom',
        isBase: false,
        revenueGrowthPercent: active.revenueGrowthPercent + 2,
      })
      await load()
    } finally {
      setBusy(null)
    }
  }

  const addEvent = async () => {
    if (!active) return
    setBusy('event')
    try {
      await postJson(`/api/planning/scenarios/${active.id}/events`, {
        monthOffset: 3,
        name: 'New hire',
        category: 'People',
        revenueImpact: 0,
        expenseImpact: 12000,
        cashImpact: -12000,
        isRecurring: true,
        notes: 'Recurring microforecast added from planning view.',
      })
      await load()
    } finally {
      setBusy(null)
    }
  }

  if (!overview || !editable) {
    return (
      <div className="page">
        <div className="empty-state">Planning data is loading.</div>
      </div>
    )
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Planning</div>
          <h1>3-way forecasting · {overview.organizationName}</h1>
          <p>Scenario planning, budget variance, cash runway, and operational drivers for {overview.periodKey}.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<Plus size={15} />} disabled={busy === 'clone'} onClick={cloneScenario}>Clone scenario</Button>
          <Button variant="accent" icon={<Target size={15} />} disabled={busy === 'event'} onClick={addEvent}>Add microforecast</Button>
          <Button variant="primary" icon={<Check size={15} />} disabled={busy === 'save'} onClick={saveScenario}>Save scenario</Button>
        </div>
      </div>

      <div className="stat-grid">
        <StatCard label="Revenue base" value={fmtMoney(overview.monthlyRevenueActual)} sub="Latest monthly actual" />
        <StatCard label="Opex base" value={fmtMoney(overview.monthlyOperatingExpenseActual)} sub="Latest monthly actual" />
        <StatCard label="Runway alert" value={firstBreach?.monthKey ?? 'Clear'} sub={`Threshold ${fmtMoney(editable.cashThreshold)}`} tone={firstBreach ? 'warn' : 'good'} />
        <StatCard label="Ending cash" value={fmtMoney(lastRow?.endingCash ?? 0)} sub={`${editable.horizonMonths} month horizon`} />
      </div>

      <div className="planning-grid">
        <Card className="scenario-card">
          <div className="table-header">
            <strong>Scenarios</strong>
            <span>{overview.scenarios.length} active</span>
          </div>
          <div className="scenario-list">
            {overview.scenarios.map((scenario) => (
              <button key={scenario.id} className={scenario.id === editable.id ? 'scenario-option active' : 'scenario-option'} onClick={() => { setSelectedScenarioId(scenario.id); setDraft({}) }}>
                <span>{scenario.scenarioType}</span>
                <strong>{scenario.name}</strong>
                <small>{scenario.revenueGrowthPercent.toFixed(1)}% growth · {scenario.grossMarginPercent.toFixed(1)}% margin</small>
              </button>
            ))}
          </div>
          <div className="field-row">
            <label className="field">
              <span>Revenue growth %</span>
              <input type="number" value={editable.revenueGrowthPercent} onChange={(event) => updateDraft({ revenueGrowthPercent: Number(event.target.value) })} />
            </label>
            <label className="field">
              <span>Gross margin %</span>
              <input type="number" value={editable.grossMarginPercent} onChange={(event) => updateDraft({ grossMarginPercent: Number(event.target.value) })} />
            </label>
          </div>
          <div className="field-row">
            <label className="field">
              <span>Opex growth %</span>
              <input type="number" value={editable.opexGrowthPercent} onChange={(event) => updateDraft({ opexGrowthPercent: Number(event.target.value) })} />
            </label>
            <label className="field">
              <span>Cash threshold</span>
              <input type="number" value={editable.cashThreshold} onChange={(event) => updateDraft({ cashThreshold: Number(event.target.value) })} />
            </label>
          </div>
          <div className="event-list">
            {editable.events.map((event) => (
              <div key={event.id}>
                <strong>{event.name}</strong>
                <span>Month {event.monthOffset} · {event.isRecurring ? 'recurring' : 'one-time'} · {fmtMoney(event.cashImpact)}</span>
              </div>
            ))}
          </div>
        </Card>

        <Card className="forecast-card">
          <div className="table-header">
            <strong>Forecast curve</strong>
            <span>P&L · cash flow · balance sheet</span>
          </div>
          <ColumnChart current={rows.slice(0, 12).map((row) => row.revenue)} prior={rows.slice(0, 12).map((row) => row.netIncome)} />
          <div className="forecast-table-wrap">
            <table className="mini-table full">
              <thead>
                <tr>
                  <th>Month</th>
                  <th>Revenue</th>
                  <th>Net income</th>
                  <th>Cash</th>
                  <th>AR</th>
                  <th>AP</th>
                </tr>
              </thead>
              <tbody>
                {rows.slice(0, 12).map((row) => (
                  <tr key={row.monthKey}>
                    <td className="mono">{row.monthKey}</td>
                    <td>{fmtMoney(row.revenue)}</td>
                    <td className={row.netIncome >= 0 ? 'good-text' : 'bad-text'}>{fmtMoney(row.netIncome)}</td>
                    <td className={row.cashThresholdBreached ? 'bad-text' : ''}>{fmtMoney(row.endingCash)}</td>
                    <td>{fmtMoney(row.accountsReceivable)}</td>
                    <td>{fmtMoney(row.accountsPayable)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      </div>

      <div className="planning-grid two">
        <Card>
          <div className="table-header">
            <strong>Budget variance</strong>
            <span>{overview.budgetRows.length} FS lines</span>
          </div>
          <table className="mini-table full">
            <tbody>
              {overview.budgetRows.map((row) => (
                <tr key={row.fsLine}>
                  <td>{row.fsLine}</td>
                  <td>{fmtMoney(row.actualAmount)}</td>
                  <td>{fmtMoney(row.budgetAmount)}</td>
                  <td className={row.varianceAmount >= 0 ? 'good-text' : 'bad-text'}>{row.variancePercent.toFixed(1)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
        <Card>
          <div className="table-header">
            <strong>Non-financial drivers</strong>
            <span>{overview.nonFinancialMetrics.length} metrics</span>
          </div>
          <div className="driver-list">
            {overview.nonFinancialMetrics.map((metric) => (
              <div key={metric.id}>
                <span>{metric.category}</span>
                <strong>{metric.name}</strong>
                <small>{metric.currentValue.toLocaleString()} {metric.unit} · target {metric.targetValue.toLocaleString()}</small>
              </div>
            ))}
          </div>
        </Card>
      </div>

      <Card>
        <div className="table-header">
          <strong>Cash timing</strong>
          <div className="segmented compact">
            <SegmentButton active={cashGranularity === 'Weekly'} onClick={() => setCashGranularity('Weekly')}>Weekly</SegmentButton>
            <SegmentButton active={cashGranularity === 'Daily'} onClick={() => setCashGranularity('Daily')}>Daily</SegmentButton>
          </div>
        </div>
        <div className="forecast-table-wrap">
          <table className="mini-table full">
            <thead>
              <tr>
                <th>Period</th>
                <th>Inflow</th>
                <th>Outflow</th>
                <th>Net</th>
                <th>Ending cash</th>
              </tr>
            </thead>
            <tbody>
              {(cashTiming?.rows ?? []).slice(0, cashGranularity === 'Daily' ? 21 : 14).map((row) => (
                <tr key={row.label}>
                  <td className="mono">{row.label}</td>
                  <td>{fmtMoney(row.cashInflow)}</td>
                  <td>{fmtMoney(row.cashOutflow)}</td>
                  <td className={row.netCashFlow >= 0 ? 'good-text' : 'bad-text'}>{fmtMoney(row.netCashFlow)}</td>
                  <td className={row.cashThresholdBreached ? 'bad-text' : ''}>{fmtMoney(row.endingCash)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {!cashTiming?.rows.length && <div className="empty-state compact">Cash timing rows are loading.</div>}
        </div>
      </Card>
    </div>
  )
}

function BenchmarkingView({ packageData }: { packageData: PackageDto }) {
  const [benchmarking, setBenchmarking] = useState<BenchmarkingDto | null>(null)
  const [fxRates, setFxRates] = useState<FxRateDto[]>([])

  useEffect(() => {
    Promise.all([
      fetchJson<BenchmarkingDto>(`/api/benchmarking?periodKey=${encodeURIComponent(packageData.periodKey)}`),
      fetchJson<FxRateDto[]>(`/api/fx-rates?organizationId=${packageData.organizationId}&periodKey=${encodeURIComponent(packageData.periodKey)}`),
    ])
      .then(([benchmarks, rates]) => {
        setBenchmarking(benchmarks)
        setFxRates(rates)
      })
      .catch(() => {
        setBenchmarking(null)
        setFxRates([])
      })
  }, [packageData.organizationId, packageData.periodKey])

  const saveFxRate = async (rate: FxRateDto, value: number) => {
    const saved = await putJson<FxRateDto>(`/api/fx-rates/${rate.id}`, {
      organizationId: rate.organizationId,
      reportingPeriodId: rate.reportingPeriodId,
      currencyCode: rate.currencyCode,
      rateToPresentation: value,
      source: 'Manual benchmark table',
    })
    setFxRates((current) => current.map((item) => (item.id === saved.id ? saved : item)))
  }

  const addFxRate = async () => {
    const code = ['EUR', 'GBP', 'CAD', 'AUD', 'MXN'].find((candidate) => !fxRates.some((rate) => rate.currencyCode === candidate)) ?? 'EUR'
    const saved = await postJson<FxRateDto>('/api/fx-rates', {
      organizationId: packageData.organizationId,
      reportingPeriodId: packageData.reportingPeriodId,
      currencyCode: code,
      rateToPresentation: 1,
      source: 'Manual benchmark table',
    }).catch(() => null)
    if (saved) setFxRates((current) => [...current, saved])
  }

  const rows = benchmarking?.rows ?? []
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Benchmarks</div>
          <h1>Entity performance comparison</h1>
          <p>Side-by-side financials, package status, KPI scorecards, and open-review load for {benchmarking?.periodKey ?? packageData.periodKey}.</p>
        </div>
      </div>
      <div className="benchmark-grid">
        {rows.map((row) => (
          <Card key={row.organizationId} className="benchmark-card">
            <div className="benchmark-rank">{row.rank}</div>
            <div>
              <span className="eyebrow">{row.isConsolidated ? 'Consolidated' : row.abbreviation}</span>
              <h3>{row.organizationName}</h3>
              <p>{row.packageStatus} · {row.openIssueCount} open issues</p>
            </div>
            <div className="metric-row">
              <span className="metric">{fmtMoney(row.netIncome)}</span>
              <span className={row.grossMarginPercent >= 0 ? 'good-text mono' : 'bad-text mono'}>{row.grossMarginPercent.toFixed(1)}% margin</span>
            </div>
            <Sparkline current={[row.revenue, row.expense, row.netIncome].map((value) => Math.abs(value))} />
            <div className="benchmark-kpis">
              {row.kpis.map((kpi) => (
                <span key={kpi.id}>{kpi.name}: {formatKpiValue(kpi)}</span>
              ))}
            </div>
          </Card>
        ))}
      </div>
      <Card>
        <div className="table-header">
          <strong>FX rate table</strong>
          <Button variant="ghost" icon={<Plus size={13} />} onClick={addFxRate}>Add rate</Button>
        </div>
        <div className="fx-rate-grid">
          {fxRates.map((rate) => (
            <label key={rate.id} className="field compact-field">
              <span>{rate.currencyCode}</span>
              <input type="number" step="0.0001" value={rate.rateToPresentation} onChange={(event) => saveFxRate(rate, Number(event.target.value))} />
            </label>
          ))}
        </div>
      </Card>
      {rows.length === 0 && <div className="empty-state">No benchmark rows are available for this period yet.</div>}
    </div>
  )
}

function KpiLibrary({ packageData }: { packageData: PackageDto }) {
  const [kpis, setKpis] = useState<KpiDto[]>([])
  const [alerts, setAlerts] = useState<KpiAlertDto[]>([])
  const [metrics, setMetrics] = useState<NonFinancialMetricDto[]>([])
  const [draftTarget, setDraftTarget] = useState<Record<string, number>>({})
  const [formulaDraft, setFormulaDraft] = useState('kpi("Cash Runway") * 30')
  const [formulaResult, setFormulaResult] = useState<FormulaEvaluationDto | null>(null)

  const load = useCallback(async () => {
    const [nextKpis, nextAlerts, nextMetrics] = await Promise.all([
      fetchJson<KpiDto[]>(`/api/kpis?organizationId=${packageData.organizationId}`),
      fetchJson<KpiAlertDto[]>(`/api/kpi-alerts?organizationId=${packageData.organizationId}`),
      fetchJson<NonFinancialMetricDto[]>(`/api/non-financial-metrics?organizationId=${packageData.organizationId}&periodKey=${encodeURIComponent(packageData.periodKey)}`),
    ])
    setKpis(nextKpis)
    setAlerts(nextAlerts)
    setMetrics(nextMetrics)
    setDraftTarget(Object.fromEntries(nextKpis.map((kpi) => [kpi.id, kpi.targetValue])))
  }, [packageData.organizationId, packageData.periodKey])

  useEffect(() => {
    load()
      .catch(() => {
        setKpis([])
        setAlerts([])
        setMetrics([])
      })
  }, [load])

  const saveKpi = async (kpi: KpiDto, patch: Partial<KpiDto>) => {
    const next = { ...kpi, ...patch }
    const saved = await putJson<KpiDto>(`/api/kpis/${kpi.id}`, {
      organizationId: next.organizationId,
      name: next.name,
      category: next.category,
      formula: next.formula,
      unit: next.unit,
      currentValue: next.currentValue,
      targetValue: next.targetValue,
      isPinned: next.isPinned,
      higherIsBetter: next.name.toLowerCase() !== 'dso',
    })
    setKpis((current) => current.map((item) => (item.id === saved.id ? saved : item)))
  }

  const enableAlert = async (kpi: KpiDto) => {
    const direction = kpi.name.toLowerCase() === 'dso' ? 'Above' : 'Below'
    const saved = await postJson<KpiAlertDto>('/api/kpi-alerts', {
      kpiDefinitionId: kpi.id,
      direction,
      thresholdValue: kpi.targetValue,
      severity: kpi.status === 'bad' ? 'High' : 'Medium',
      message: `${kpi.name} crossed the board target.`,
      isActive: true,
    })
    setAlerts((current) => [saved, ...current])
  }

  const addKpi = async () => {
    const saved = await postJson<KpiDto>('/api/kpis', {
      organizationId: packageData.organizationId,
      name: 'Custom KPI',
      category: 'Custom',
      formula: 'Manual input',
      unit: '$',
      currentValue: 0,
      targetValue: 1,
      isPinned: false,
      higherIsBetter: true,
    }).catch(() => null)
    if (saved) setKpis((current) => [saved, ...current])
  }

  const addMetric = async () => {
    const saved = await postJson<NonFinancialMetricDto>('/api/non-financial-metrics', {
      organizationId: packageData.organizationId,
      reportingPeriodId: packageData.reportingPeriodId,
      name: 'Custom Driver',
      category: 'Operations',
      unit: 'count',
      currentValue: 0,
      priorValue: 0,
      targetValue: 1,
      valuesJson: '[]',
      source: 'Manual datasheet',
      isPinned: false,
    }).catch(() => null)
    if (saved) setMetrics((current) => [saved, ...current])
  }

  const evaluateFormula = async () => {
    const result = await postJson<FormulaEvaluationDto>('/api/formulas/evaluate', {
      organizationId: packageData.organizationId,
      reportingPeriodId: packageData.reportingPeriodId,
      formula: formulaDraft,
    }).catch(() => null)
    setFormulaResult(result)
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Performance Library</div>
          <h1>KPI Scorecards</h1>
          <p>Pinned KPI cards, alerts, and non-financial drivers feed package slides, shared dashboards, and export QA.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<TrendingUp size={15} />} onClick={addMetric}>Add driver</Button>
          <Button variant="primary" icon={<Gauge size={15} />} onClick={addKpi}>Set goals</Button>
        </div>
      </div>
      <Card className="formula-card">
        <div className="table-header">
          <strong>Formula builder</strong>
          <span>{formulaResult ? fmtMoney(formulaResult.value) : 'Ready'}</span>
        </div>
        <div className="formula-row">
          <input value={formulaDraft} onChange={(event) => setFormulaDraft(event.target.value)} />
          <Button variant="secondary" icon={<FileSpreadsheet size={14} />} onClick={evaluateFormula}>Evaluate</Button>
        </div>
        <small className="mono">{formulaResult?.normalizedExpression ?? 'Use fs("Revenue"), kpi("Cash Runway"), or metric("Customer Count").'}</small>
      </Card>
      <div className="kpi-grid">
        {kpis.map((kpi) => (
          <Card key={kpi.id} className="kpi-card">
            <span className="eyebrow">{kpi.category}</span>
            <strong>{kpi.name}</strong>
            <div className="kpi-value">{formatKpiValue(kpi)}</div>
            <small className="mono">{kpi.formula}</small>
            <div className="target-bar">
              <span style={{ width: kpi.status === 'good' ? '112%' : kpi.status === 'warn' ? '88%' : '72%' }} />
            </div>
            <label className="field compact-field">
              <span>Target</span>
              <input
                type="number"
                value={draftTarget[kpi.id] ?? kpi.targetValue}
                onChange={(event) => setDraftTarget((current) => ({ ...current, [kpi.id]: Number(event.target.value) }))}
                onBlur={() => saveKpi(kpi, { targetValue: draftTarget[kpi.id] ?? kpi.targetValue })}
              />
            </label>
            <label className="check-row">
              <input type="checkbox" checked={kpi.isPinned} onChange={(event) => saveKpi(kpi, { isPinned: event.target.checked })} />
              <span>Pinned to slides</span>
            </label>
            <div className="alert-chip-row">
              {alerts.filter((alert) => alert.kpiDefinitionId === kpi.id).map((alert) => (
                <span key={alert.id} className={alert.isTriggered ? 'alert-chip triggered' : 'alert-chip'}>
                  <Bell size={12} /> {alert.direction} {alert.thresholdValue.toLocaleString()}
                </span>
              ))}
              {!alerts.some((alert) => alert.kpiDefinitionId === kpi.id) && (
                <Button variant="ghost" icon={<Bell size={13} />} onClick={() => enableAlert(kpi)}>Alert</Button>
              )}
            </div>
          </Card>
        ))}
      </div>
      <div className="section-title">
        <h2>Non-financial drivers</h2>
        <span className="muted">{metrics.length} manual or integrated metrics</span>
      </div>
      <div className="benchmark-grid compact">
        {metrics.map((metric) => (
          <Card key={metric.id} className="benchmark-card">
            <span className="eyebrow">{metric.category}</span>
            <h3>{metric.name}</h3>
            <div className="metric-row">
              <span className="metric">{metric.currentValue.toLocaleString()} {metric.unit}</span>
              <span className={metric.currentValue >= metric.targetValue ? 'good-text mono' : 'bad-text mono'}>target {metric.targetValue.toLocaleString()}</span>
            </div>
            <Sparkline current={parseJson<number[]>(metric.valuesJson, [])} />
            <small>{metric.source}</small>
          </Card>
        ))}
      </div>
    </div>
  )
}

function BrandingView({ packageData, refreshPackages, notify }: { packageData: PackageDto; refreshPackages: () => Promise<void>; notify: (message: string) => void }) {
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

function LayoutsView({ packageData }: { packageData: PackageDto }) {
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

function OutputView({ packageData }: { packageData: PackageDto }) {
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

function LiveDashboard({ packageData, openSlide }: { packageData: PackageDto; openSlide: (id: string) => void }) {
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

function CompetitiveParityView({ packageData, refreshPackages }: { packageData: PackageDto | null; refreshPackages: () => Promise<void> }) {
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

function XeroSettings({ notify }: { notify: (message: string) => void }) {
  const [status, setStatus] = useState<XeroStatus | null>(null)
  const [reportingContext, setReportingContext] = useState<ReportingContext>(emptyReportingContext)
  const [coverage, setCoverage] = useState<XeroDataCoverage | null>(null)
  const [backfillPreview, setBackfillPreview] = useState<XeroBackfillPreview | null>(null)
  const [backfillRun, setBackfillRun] = useState<XeroBackfillRun | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    const [next, context, nextCoverage] = await Promise.all([
      fetchJson<XeroStatus>('/api/xero/status'),
      fetchJson<ReportingContext>('/api/reporting-context'),
      fetchJson<XeroDataCoverage>('/api/xero/data-coverage?from=2025-01&to=current'),
    ])
    setStatus(next)
    setReportingContext(context)
    setCoverage(nextCoverage)
  }, [])

  useEffect(() => {
    let active = true
    Promise.all([
      fetchJson<XeroStatus>('/api/xero/status'),
      fetchJson<ReportingContext>('/api/reporting-context'),
      fetchJson<XeroDataCoverage>('/api/xero/data-coverage?from=2025-01&to=current'),
    ])
      .then(([next, context, nextCoverage]) => {
        if (active) {
          setStatus(next)
          setReportingContext(context)
          setCoverage(nextCoverage)
        }
      })
      .catch(() => notify('Could not load Xero status.'))
    return () => {
      active = false
    }
  }, [notify])

  useEffect(() => {
    const onMessage = (event: MessageEvent) => {
      if (event.data?.type === 'xero-connected') {
        load().catch(() => undefined)
        notify('Xero connection completed.')
      } else if (event.data?.type === 'xero-error') {
        notify(event.data.message ?? 'Xero authorization failed.')
      }
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [load, notify])

  const openConnect = async (connectionId?: string, tenantId?: string) => {
    const busyKey = connectionId ? `reconnect-${connectionId}` : tenantId ? `reconnect-${tenantId}` : 'connect'
    setBusy(busyKey)
    const popup = window.open('', 'xero-oauth', 'width=1040,height=820')
    if (popup) {
      popup.document.title = 'Connecting to Xero'
      popup.document.body.innerHTML = '<div style="font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;padding:32px;"><h1>Preparing Xero authorization...</h1><p>You will be redirected to Xero in a moment.</p></div>'
    }
    try {
      const response = tenantId
        ? await postJson<XeroConnectResponse>(`/api/xero/tenants/${tenantId}/reconnect`, {})
        : connectionId
        ? await postJson<XeroConnectResponse>(`/api/xero/connections/${connectionId}/reconnect`, {})
        : await fetchJson<XeroConnectResponse>('/api/xero/connect')
      if (response.authUrl) {
        if (popup) {
          popup.location.href = response.authUrl
        } else {
          window.location.href = response.authUrl
        }
        notify('Xero authorization opened.')
      } else {
        popup?.close()
        notify(response.error ?? 'Xero authorization could not start.')
      }
    } catch {
      popup?.close()
      notify('Could not start Xero authorization.')
    } finally {
      setBusy(null)
    }
  }

  const importTokens = async () => {
    setBusy('import')
    try {
      const result = await postJson<{ importedConnections: number; message: string }>('/api/xero/import-v2-tokens', {})
      notify(result.message)
      await load()
    } catch {
      notify('Finance App V2 token import could not run.')
    } finally {
      setBusy(null)
    }
  }

  const previewImport = async () => {
    setBusy('preview')
    try {
      const result = await postJson<{ tenantCount: number; message: string }>('/api/xero/import-v2-tokens/preview', {})
      notify(`${result.message} ${result.tenantCount} tenant(s) ready.`)
    } catch {
      notify('Could not preview Finance App V2 token import.')
    } finally {
      setBusy(null)
    }
  }

  const testConnection = async () => {
    setBusy('test')
    try {
      const result = await postJson<XeroStatus>('/api/xero/test', {})
      setStatus(result)
      notify('Xero connection status refreshed.')
    } catch {
      notify('Xero test failed.')
    } finally {
      setBusy(null)
    }
  }

  const previewBackfill = async () => {
    setBusy('backfill-preview')
    try {
      const result = await postJson<XeroBackfillPreview>('/api/xero/backfill/preview', { fromPeriodKey: '2025-01', toPeriodKey: 'current', hydrateLedger: false })
      setBackfillPreview(result)
      notify(`Backfill preview: ${result.estimatedCalls} estimated calls across ${result.tenants.length} tenant(s).`)
    } catch {
      notify('Could not preview historical Xero backfill.')
    } finally {
      setBusy(null)
    }
  }

  const queueBackfill = async () => {
    setBusy('backfill')
    try {
      const result = await postJson<XeroBackfillRun>('/api/xero/backfill', { fromPeriodKey: '2025-01', toPeriodKey: 'current', hydrateLedger: false })
      setBackfillRun(result)
      notify('Historical Xero backfill queued.')
      await load()
    } catch {
      notify('Could not queue historical Xero backfill.')
    } finally {
      setBusy(null)
    }
  }

  const tenants = status?.tenants ?? []
  const ledger = status?.ledgerSync
  const readyTenants = tenants.filter((tenant) => tenant.connectionStatus === 'Connected' && !tenant.isTokenExpired && !tenant.requiresReconnectForLedger)
  const reconnectTenants = tenants.filter((tenant) => tenant.isTokenExpired || tenant.requiresReconnectForLedger || tenant.connectionStatus !== 'Connected')
  const importedTenantCount = tenants.filter((tenant) => tenant.source === 'Finance App V2').length
  const redirectLooksRegistered = status?.redirectUri.includes('localhost:5264') ?? false
  const coverageRows = coverage?.rows ?? []
  const completeMonths = coverageRows.filter((row) => row.status === 'Complete').length
  const reviewMonths = coverageRows.filter((row) => row.status === 'Needs retry').length
  const isCoverageHealthy = (coverageStatus: string) => coverageStatus === 'Complete'

  return (
    <div className="page xero-page">
      <div className="page-header xero-header">
        <div>
          <div className="eyebrow">Xero integration</div>
          <h1>Xero Control Center</h1>
          <p>Global tenant connection, automatic 15-minute ledger sync, historical data backfill, and Xero data coverage.</p>
        </div>
        <div className="actions">
          <Button variant="secondary" icon={<RefreshCw size={15} />} disabled={busy === 'refresh'} onClick={() => { setBusy('refresh'); load().finally(() => setBusy(null)) }}>
            Refresh Status
          </Button>
          <Button variant="primary" icon={<ArrowUpRight size={15} />} disabled={busy === 'connect'} onClick={() => openConnect()}>
            Connect Xero
          </Button>
        </div>
      </div>

      {status?.allowLocalStubReports && (
        <div className="alert-strip warn">
          <AlertTriangle size={16} /> Test fixture reports are enabled. Live Xero data should not be trusted until fixture mode is off.
        </div>
      )}
      {reconnectTenants.length > 0 && (
        <div className="alert-strip warn">
          <PlugZap size={16} /> {reconnectTenants.length} tenant(s) need reconnect before rolling GL sync can read journals.
        </div>
      )}

      <div className="xero-overview-grid">
        <Card className={status?.clientConfigured && redirectLooksRegistered ? 'xero-status-card good' : 'xero-status-card warn'}>
          <div className="xero-card-top">
            <div>
              <div className="eyebrow">OAuth app</div>
              <h3>{status?.clientConfigured ? 'Configured' : 'Missing client id'}</h3>
            </div>
            {status?.clientConfigured && redirectLooksRegistered ? <CheckCircle2 size={20} /> : <AlertTriangle size={20} />}
          </div>
          <dl className="compact-metrics">
            <div><dt>Callback</dt><dd>{status?.redirectUri ?? 'Loading...'}</dd></div>
            <div><dt>Environment</dt><dd>{status?.environment ?? 'Loading...'}</dd></div>
          </dl>
        </Card>
        <Card className={readyTenants.length > 0 ? 'xero-status-card good' : 'xero-status-card warn'}>
          <div className="xero-card-top">
            <div>
              <div className="eyebrow">Tenant coverage</div>
              <h3>{readyTenants.length} ready / {tenants.length || 0} connected</h3>
            </div>
            <Link size={20} />
          </div>
          <dl className="compact-metrics">
            <div><dt>Imported</dt><dd>{importedTenantCount} from Finance App V2</dd></div>
            <div><dt>Reconnect</dt><dd>{reconnectTenants.length} tenant(s)</dd></div>
          </dl>
        </Card>
        <Card className="xero-status-card">
          <div className="xero-card-top">
            <div>
              <div className="eyebrow">Rolling GL sync</div>
              <h3>{ledger?.enabled ? `Every ${ledger.syncEveryMinutes} minutes` : 'Paused'}</h3>
            </div>
            <Database size={20} />
          </div>
          <dl className="compact-metrics">
            <div><dt>Retention</dt><dd>{ledger?.retentionYears ?? 3} years detail</dd></div>
            <div><dt>TB basis</dt><dd>Month-end on refresh</dd></div>
          </dl>
        </Card>
        <Card className={reviewMonths === 0 ? 'xero-status-card good' : 'xero-status-card warn'}>
          <div className="xero-card-top">
            <div>
              <div className="eyebrow">Data coverage</div>
              <h3>{completeMonths} complete / {coverageRows.length || 0} checks</h3>
            </div>
            <CheckCircle2 size={20} />
          </div>
          <dl className="compact-metrics">
            <div><dt>Needs review</dt><dd>{reviewMonths} month(s)</dd></div>
            <div><dt>Periods</dt><dd>{reportingContext.periods.length} data-backed</dd></div>
          </dl>
        </Card>
      </div>

      <div className="xero-layout">
        <div className="xero-main">
          <div className="page-header compact">
            <div>
              <div className="eyebrow">Tenant registry</div>
              <h2>Connections</h2>
            </div>
            <div className="actions">
              <Button variant="ghost" disabled={busy === 'test'} onClick={testConnection}>Test Connection</Button>
              <Button variant="ghost" disabled={busy === 'preview'} onClick={previewImport}>Preview V2</Button>
            </div>
          </div>

          <Card className="xero-table-card">
            <div className="xero-table-wrap">
              <table className="xero-table">
                <thead>
                  <tr>
                    <th>Tenant</th>
                    <th>Status</th>
                    <th>GL scope</th>
                    <th>Ledger sync</th>
                    <th>Mapped entity</th>
                    <th>Token</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {tenants.map((tenant) => {
                    const ledgerStatus = ledger?.tenants.find((item) => item.tenantId === tenant.tenantId)
                    const isReady = tenant.connectionStatus === 'Connected' && !tenant.isTokenExpired && !tenant.requiresReconnectForLedger
                    return (
                      <tr key={tenant.id} className={isReady ? 'ready' : 'needs-work'}>
                        <td>
                          <strong>{tenant.tenantName}</strong>
                          <small>{tenant.tenantType} / {shortId(tenant.tenantId)}</small>
                          {(tenant.lastError || ledgerStatus?.lastError) && <span className="row-error">{tenant.lastError ?? ledgerStatus?.lastError}</span>}
                        </td>
                        <td><span className={isReady ? 'status-pill good' : 'status-pill warn'}>{tenant.connectionStatus}{tenant.isTokenExpired ? ' expired' : ''}</span></td>
                        <td>{tenant.requiresReconnectForLedger ? 'Reconnect required' : 'Ready'}</td>
                        <td>{ledgerStatus?.lastSuccessfulSyncAt ? `${formatRelative(ledgerStatus.lastSuccessfulSyncAt)} / journal ${ledgerStatus.lastJournalNumber ?? 0}` : ledgerStatus?.status ?? 'Not synced'}</td>
                        <td>{tenant.mappedOrganizationId ? shortId(tenant.mappedOrganizationId) : 'Unmapped'}</td>
                        <td>{formatDateTime(tenant.tokenExpiresAt)}</td>
                        <td>
                          <Button variant={isReady ? 'ghost' : 'secondary'} disabled={busy === `reconnect-${tenant.tenantId}`} onClick={() => openConnect(undefined, tenant.tenantId)}>
                            Reconnect
                          </Button>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
            {status && tenants.length === 0 && <div className="empty-state">No Xero tenants are connected yet.</div>}
          </Card>

          <Card className="xero-config-card">
            <div>
              <div className="eyebrow">Requested scopes</div>
              <p>{status?.scopes ?? 'Loading...'}</p>
            </div>
          </Card>

          <Card className="xero-table-card">
            <div className="section-title tight">
              <div>
                <h2>2025-current data coverage</h2>
                <span className="muted">Month-end reports, Trial Balance snapshots, normalized lines, and optional GL evidence</span>
              </div>
              <Button variant="ghost" disabled={busy === 'refresh'} onClick={() => { setBusy('refresh'); load().finally(() => setBusy(null)) }}>Refresh</Button>
            </div>
            <div className="xero-table-wrap coverage-table">
              <table className="xero-table">
                <thead>
                  <tr><th>Entity</th><th>Period</th><th>Status</th><th>Journals</th><th>Statements</th><th>TB</th></tr>
                </thead>
                <tbody>
                  {coverageRows.slice(0, 120).map((row) => (
                    <tr key={`${row.tenantId}-${row.periodKey}`}>
                      <td><strong>{row.organizationName}</strong><small>{row.tenantName}</small></td>
                      <td>{row.periodKey}</td>
                      <td><span className={isCoverageHealthy(row.status) ? 'status-pill good' : row.status === 'No activity' ? 'status-pill' : 'status-pill warn'}>{row.status}</span></td>
                      <td>{row.journalCount} / {row.journalLineCount} lines</td>
                      <td>{row.rawSnapshotTypes.join(', ') || '—'} · {row.statementLineCount}</td>
                      <td>{row.reconciliationStatus ?? 'Pending'}{row.reconciliationDifference ? ` · ${fmtMoney(row.reconciliationDifference)}` : ''}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        </div>

        <div className="xero-side">
          <Card className="xero-action-panel">
            <div className="xero-card-top">
              <div>
                <div className="eyebrow">Finance App V2 import</div>
                <h3>{status?.tokenImport.source ?? 'Loading...'}</h3>
              </div>
              <PlugZap size={20} />
            </div>
            <p>Imports all Finance App V2 tenants, then reconnect expands Xero grants for journal-based GL sync.</p>
            <div className="stacked-actions">
              <Button variant="secondary" disabled={busy === 'import'} onClick={importTokens}>Import tokens</Button>
              <Button variant="ghost" disabled={busy === 'preview'} onClick={previewImport}>Preview import</Button>
            </div>
          </Card>

          <Card className="xero-action-panel">
            <div className="xero-card-top">
              <div>
                <div className="eyebrow">Historical backfill</div>
                <h3>2025 to current</h3>
              </div>
              <RefreshCw size={20} />
            </div>
            <p>Queues statement and Trial Balance import with conservative throttling. GL detail is hydrated later for material review evidence.</p>
            <div className="stacked-actions">
              <Button variant="secondary" icon={<Database size={15} />} disabled={busy === 'backfill-preview'} onClick={previewBackfill}>Preview calls</Button>
              <Button variant="accent" icon={<RefreshCw size={15} />} disabled={busy === 'backfill'} onClick={queueBackfill}>Queue backfill</Button>
            </div>
            {backfillPreview && <p className="muted">{backfillPreview.estimatedCalls} estimated calls · {backfillPreview.softMinuteLimit}/min soft cap · {backfillPreview.softDailyLimit}/day soft cap · GL {backfillPreview.hydrateLedger ? 'included' : 'deferred'}.</p>}
            {backfillRun && <p className="muted">Run {shortId(backfillRun.id)} is {backfillRun.status}; {backfillRun.actualCalls} calls used.</p>}
          </Card>

          <Card className="xero-action-panel">
            <div className="xero-card-top">
              <div>
                <div className="eyebrow">Diagnostics</div>
                <h3>Manual sync is admin-only</h3>
              </div>
              <Database size={20} />
            </div>
            <p>Xero updates automatically every 15 minutes. Manual sync controls stay here only for troubleshooting.</p>
          </Card>
        </div>
      </div>
    </div>
  )
}

function AiSettings() {
  const [models, setModels] = useState<AiModel[]>([])
  const [settings, setSettings] = useState<AiSetting[]>([])

  useEffect(() => {
    fetchJson<AiModel[]>('/api/ai/models').then(setModels).catch(() => setModels([{ id: 'gpt-5.5', displayName: 'gpt-5.5', reasoningEfforts: ['low', 'medium', 'high', 'xhigh'], isDefault: true }]))
    fetchJson<AiSetting[]>('/api/settings/ai-runtime').then(setSettings).catch(() => setSettings(AI_SETTING_MODULES.map((module) => ({ module, model: 'gpt-5.5', reasoningEffort: 'high', profile: module, enabled: true }))))
  }, [])

  const normalized = AI_SETTING_MODULES.map((module) => settings.find((s) => s.module === module) ?? { module, model: models[0]?.id ?? 'gpt-5.5', reasoningEffort: 'high', profile: module, enabled: true })
  const update = (module: string, patch: Partial<AiSetting>) => setSettings(normalized.map((setting) => (setting.module === module ? { ...setting, ...patch } : setting)))
  const save = async () => postJson('/api/settings/ai-runtime', normalized)

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <div className="eyebrow">Codex CLI runtime</div>
          <h1>AI Settings</h1>
          <p>Choose the local Codex model, reasoning effort, and prompt profile per module.</p>
        </div>
        <Button variant="primary" icon={<Check size={15} />} onClick={save}>
          Save settings
        </Button>
      </div>
      <Card className="settings-table">
        <table>
          <thead>
            <tr>
              <th>Module</th>
              <th>Model</th>
              <th>Reasoning</th>
              <th>Profile</th>
              <th>Enabled</th>
            </tr>
          </thead>
          <tbody>
            {normalized.map((setting) => (
              <tr key={setting.module}>
                <td><strong>{setting.module}</strong></td>
                <td>
                  <select value={setting.model} onChange={(event) => update(setting.module, { model: event.target.value })}>
                    {models.map((model) => (
                      <option key={model.id} value={model.id}>{model.displayName}</option>
                    ))}
                  </select>
                </td>
                <td>
                  <select value={setting.reasoningEffort} onChange={(event) => update(setting.module, { reasoningEffort: event.target.value })}>
                    {(models.find((model) => model.id === setting.model)?.reasoningEfforts ?? ['low', 'medium', 'high', 'xhigh']).map((effort) => (
                      <option key={effort} value={effort}>{effort}</option>
                    ))}
                  </select>
                </td>
                <td><input value={setting.profile} onChange={(event) => update(setting.module, { profile: event.target.value })} /></td>
                <td><input type="checkbox" checked={setting.enabled} onChange={(event) => update(setting.module, { enabled: event.target.checked })} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
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
  return (
    <aside className="side-panel">
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

function InsertBar({ onAdd }: { onAdd: (kind: string) => void }) {
  const kinds = ['text', 'kpi', 'chart', 'drivers', 'table', 'callout', 'divider', 'image']
  return (
    <div className="insert-bar">
      {kinds.map((kind) => (
        <button key={kind} onClick={() => onAdd(kind)}>{kind}</button>
      ))}
    </div>
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

function StatCard({ label, value, sub, tone }: { label: string; value: string; sub: string; tone?: 'good' | 'warn' }) {
  return (
    <Card className={tone ? `stat-card ${tone}` : 'stat-card'}>
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{sub}</small>
    </Card>
  )
}

function Card({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`card ${className}`}>{children}</div>
}

function Button({
  children,
  icon,
  variant = 'secondary',
  disabled,
  onClick,
}: {
  children: React.ReactNode
  icon?: React.ReactNode
  variant?: 'primary' | 'secondary' | 'ghost' | 'accent'
  disabled?: boolean
  onClick?: React.MouseEventHandler<HTMLButtonElement>
}) {
  return (
    <button className={`button ${variant}`} disabled={disabled} onClick={onClick}>
      {icon}
      {children}
    </button>
  )
}

function SegmentButton({ active, children, onClick, icon }: { active: boolean; children: React.ReactNode; onClick: () => void; icon?: React.ReactNode }) {
  return (
    <button className={active ? 'seg active' : 'seg'} onClick={onClick}>
      {icon}
      {children}
    </button>
  )
}

function RailItem({ active, icon, badge, disabled, children, onClick }: { active?: boolean; icon: React.ReactNode; badge?: string; disabled?: boolean; children: React.ReactNode; onClick: () => void }) {
  return (
    <button className={active ? 'rail-item active' : 'rail-item'} disabled={disabled} onClick={onClick}>
      <span className="rail-icon">{icon}</span>
      <span>{children}</span>
      {badge && <span className="rail-dot" />}
    </button>
  )
}

function SeverityBadge({ severity }: { severity: string }) {
  return <span className={`severity ${severity.toLowerCase()}`}>{severity}</span>
}

function Sparkline({ current, prior = [] }: { current: number[]; prior?: number[] }) {
  const values = [...current, ...prior].filter((x) => Number.isFinite(x))
  const min = Math.min(...values, 0)
  const max = Math.max(...values, 1)
  const points = current.map((value, index) => `${(index / Math.max(current.length - 1, 1)) * 130},${48 - ((value - min) / (max - min || 1)) * 44}`).join(' ')
  const priorPoints = prior.map((value, index) => `${(index / Math.max(prior.length - 1, 1)) * 130},${48 - ((value - min) / (max - min || 1)) * 44}`).join(' ')
  return (
    <svg className="sparkline" viewBox="0 0 132 52" role="img" aria-label="Trend">
      {prior.length > 0 && <polyline points={priorPoints} fill="none" stroke="currentColor" strokeDasharray="3 3" opacity=".35" strokeWidth="2" />}
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="2.5" />
    </svg>
  )
}

function ColumnChart({ current, prior }: { current: number[]; prior: number[] }) {
  const max = Math.max(...current, ...prior, 1)
  return (
    <div className="column-chart">
      {current.map((value, index) => (
        <div className="bar-pair" key={index}>
          <span className="bar prior" style={{ height: `${(Number(prior[index] ?? 0) / max) * 100}%` }} />
          <span className="bar current" style={{ height: `${(value / max) * 100}%` }} />
        </div>
      ))}
    </div>
  )
}

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

function countSeverity(issues: IssueDto[], severity: string) {
  return issues.filter((issue) => issue.severity === severity).length
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

function defaultContent(kind: string, slide: SlideDto) {
  if (kind === 'kpi') return { label: slide.kpiLabel, current: slide.currentValue, prior: slide.priorValue }
  if (kind === 'text') return { text: 'Add board-ready commentary here.' }
  if (kind === 'callout') return { text: 'Highlighted insight.' }
  if (kind === 'chart') return { type: 'clustered', showPY: true }
  return {}
}

function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

function formatKpiValue(kpi: KpiDto) {
  if (kpi.unit === '%') return `${kpi.currentValue.toFixed(1)}%`
  if (kpi.unit.toLowerCase().includes('day')) return `${kpi.currentValue.toFixed(0)} days`
  if (kpi.unit.toLowerCase().includes('month')) return `${kpi.currentValue.toFixed(1)} mo`
  if (kpi.unit === '$') return fmtMoney(kpi.currentValue)
  return `${kpi.currentValue.toLocaleString('en-US', { maximumFractionDigits: 1 })} ${kpi.unit}`
}

function formatRelative(value: string | null) {
  if (!value) return '—'
  const minutes = Math.max(1, Math.round((Date.now() - new Date(value).getTime()) / 60000))
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} hr ago`
}

function formatDateTime(value: string | null) {
  if (!value) return 'Never'
  return new Date(value).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
}

function formatStatementType(value: string) {
  if (value === 'BalanceSheet') return 'Balance sheet'
  if (value === 'TrialBalance') return 'Trial balance'
  return 'Income statement'
}

function statementLabel(value: string) {
  if (value === 'ProfitAndLoss') return 'P&L'
  return formatStatementType(value)
}

function shortId(value: string) {
  return value.length <= 12 ? value : `${value.slice(0, 8)}...${value.slice(-4)}`
}

function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

async function fetchJson<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`)
  if (!response.ok) throw new Error(`GET ${path} failed`)
  return response.json() as Promise<T>
}

async function postJson<T = unknown>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!response.ok) throw new Error(`POST ${path} failed`)
  return response.status === 204 ? ({} as T) : ((await response.json()) as T)
}

async function putJson<T = unknown>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!response.ok) throw new Error(`PUT ${path} failed`)
  return response.status === 204 ? ({} as T) : ((await response.json()) as T)
}

async function deleteJson<T = unknown>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(`DELETE ${path} failed`)
  return response.status === 204 ? ({} as T) : ((await response.json()) as T)
}

export default App
