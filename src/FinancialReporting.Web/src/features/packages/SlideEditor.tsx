/**
 * Slide editor surface — extracted from App.tsx.
 *
 * Contains: SlideEditor, SlideBlock, BlockInspector, FinancialTableBlock,
 * DriverEvidence, ColumnChart, BreakdownChart, CompositionChart, WaterfallChart,
 * plus the supporting pure helpers (reportComponentLibrary, defaultContent, etc.)
 * and local type aliases (marked with TODO dedupe comments).
 *
 * App.tsx still defines its own copies of these types and helpers. Once the full
 * decomposition is complete, the shared types/utils should live in a central
 * registry and both files should import from there.
 */

import { useCallback, useEffect, useState } from 'react'
import {
  AlertTriangle,
  BarChart3,
  Check,
  FileSpreadsheet,
  FileText,
  Gauge,
  History,
  LayoutGrid,
  ListChecks,
  MessageSquare,
  PanelRightOpen,
  RefreshCw,
  Settings,
  Sparkles,
  Undo2,
  X,
} from 'lucide-react'
import { Button, Card, SeverityBadge, Sparkline } from '../../components/primitives'
import { deleteJson, fetchJson, postJson, putJson } from '../../api/client'

// ---------------------------------------------------------------------------
// TODO: dedupe — these type aliases duplicate the ones in App.tsx.
//       Move to a shared types/reporting.ts once the full decomposition lands.
// ---------------------------------------------------------------------------

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

type ReportBlockWidth = 'third' | 'half' | 'twoThirds' | 'full'

type ReportComponentDefinition = {
  id: string
  category: string
  kind: string
  label: string
  description: string
  variant: string
  width: ReportBlockWidth
  content: Record<string, unknown>
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

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const REPORT_BLOCK_WIDTHS: Array<{ width: ReportBlockWidth; label: string; columns: number }> = [
  { width: 'third', label: 'One third', columns: 4 },
  { width: 'half', label: 'Half page', columns: 6 },
  { width: 'twoThirds', label: 'Two thirds', columns: 8 },
  { width: 'full', label: 'Full width', columns: 12 },
]

// ---------------------------------------------------------------------------
// Local helper copies from App.tsx; will dedupe once App.tsx type/util
// registry is extracted.
// ---------------------------------------------------------------------------

function parseJson<T>(value: string, fallback: T): T {
  try {
    return JSON.parse(value) as T
  } catch {
    return fallback
  }
}

function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

function fmtVariancePercent(value: number, prior: number, current: number) {
  if (Math.abs(prior) < 0.01 && Math.abs(current) >= 0.01) return 'n/m'
  return `${Math.abs(value).toFixed(1)}%`
}

function splitAccountCodes(value: string) {
  return value
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)
}

function pluralize(count: number, noun: string) {
  return `${count} ${noun}${count === 1 ? '' : 's'}`
}

function shortId(value: string) {
  return value.length <= 12 ? value : `${value.slice(0, 8)}...${value.slice(-4)}`
}

function formatRelative(value: string | null) {
  if (!value) return '—'
  const minutes = Math.max(1, Math.round((Date.now() - new Date(value).getTime()) / 60000))
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} hr ago`
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value)
}

function readStringField(record: Record<string, unknown>, key: string) {
  const value = record[key]
  return value === null || value === undefined ? '' : String(value)
}

function readNumberField(record: Record<string, unknown>, key: string) {
  const value = record[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : Number(value || 0)
}

function readReportAccounts(content: Record<string, unknown>) {
  const raw = Array.isArray(content.topAccounts) ? content.topAccounts : []
  return raw
    .map((item) => {
      const record = isRecord(item) ? item : {}
      return {
        code: readStringField(record, 'code'),
        name: readStringField(record, 'name'),
        type: readStringField(record, 'type'),
        fsLine: readStringField(record, 'fsLine'),
        aiSuggestedFsLine: readStringField(record, 'aiSuggestedFsLine'),
        current: readNumberField(record, 'current'),
        transactionCount: readNumberField(record, 'transactionCount'),
      }
    })
    .filter((account) => account.code || account.name)
}

function readReportTransactions(content: Record<string, unknown>) {
  const raw = Array.isArray(content.transactions) ? content.transactions : []
  return raw
    .map((item) => {
      const record = isRecord(item) ? item : {}
      return {
        code: readStringField(record, 'code'),
        name: readStringField(record, 'name'),
        date: readStringField(record, 'date'),
        description: readStringField(record, 'description'),
        amount: readNumberField(record, 'amount'),
        source: readStringField(record, 'source') || 'Xero',
      }
    })
    .filter((transaction) => transaction.code || transaction.description)
}

// ---------------------------------------------------------------------------
// Private block helpers (not exported; consumed only within this file)
// ---------------------------------------------------------------------------

/** Construct a local (unsaved) SlideBlockDto from kind + content. */
function makeBlock(kind: string, content: unknown): SlideBlockDto {
  return { id: crypto.randomUUID(), sortOrder: 1, kind, contentJson: JSON.stringify(content) }
}

function blockWidth(kind: string, content: Record<string, unknown>): ReportBlockWidth {
  const raw = String(content.width ?? '')
  if (raw === 'third' || raw === 'half' || raw === 'twoThirds' || raw === 'full') return raw
  if (kind === 'kpi' || kind === 'callout' || kind === 'image') return 'half'
  return 'full'
}

function widthToColumns(width: ReportBlockWidth) {
  return REPORT_BLOCK_WIDTHS.find((option) => option.width === width)?.columns ?? 12
}

function nearestReportWidth(columns: number): ReportBlockWidth {
  const normalized = Math.max(4, Math.min(12, columns))
  return REPORT_BLOCK_WIDTHS.reduce(
    (best, option) =>
      Math.abs(option.columns - normalized) < Math.abs(best.columns - normalized) ? option : best,
    REPORT_BLOCK_WIDTHS[0],
  ).width
}

function blockWidthClass(kind: string, content: Record<string, unknown>) {
  return `span-${widthToColumns(blockWidth(kind, content))}`
}

function componentLabel(kind: string, variant: string) {
  const labels: Record<string, string> = {
    'key-number': 'Key number',
    'sparkline-metric': 'Sparkline',
    'variance-tile': 'Variance',
    'monthly-trend': 'Chart',
    'rolling-12': 'Rolling chart',
    'year-over-year': 'YoY chart',
    'budget-projection': 'Budget chart',
    'scenario-chart': 'Scenario chart',
    'breakdown-chart': 'Breakdown chart',
    'composition-pie': 'Composition',
    'variance-waterfall': 'Waterfall',
    'financial-summary': 'Financial table',
    'trended-pl': 'Trended table',
    'variance-table': 'Variance table',
    'transaction-drilldown': 'GL drilldown',
    'account-composition': 'Account table',
    'kpi-comparatives': 'KPI table',
    'cash-flow-summary': 'Cash bridge',
    'flux-summary': 'Flux table',
    heading: 'Heading',
    commentary: 'Commentary',
    note: 'Note',
    'driver-evidence': 'Evidence',
    callout: 'Callout',
    divider: 'Divider',
    image: 'Image',
  }
  return labels[variant] ?? kind.replace(/[-_]/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase())
}

function componentIcon(kind: string, variant = '') {
  if (kind === 'chart') return <BarChart3 size={17} />
  if (kind === 'table') return <FileSpreadsheet size={17} />
  if (kind === 'kpi') return <Gauge size={17} />
  if (kind === 'drivers') return <ListChecks size={17} />
  if (kind === 'callout') return <Sparkles size={17} />
  if (kind === 'image') return <FileText size={17} />
  if (variant === 'heading') return <LayoutGrid size={17} />
  return <FileText size={17} />
}

function statementRowsForSlide(slide: SlideDto) {
  const monthly = parseJson<number[]>(slide.monthlyJson, []).map((value) => Number(value || 0))
  const threeMonth = monthly.slice(0, 3)
  const threeMonthAverage =
    threeMonth.length > 0
      ? threeMonth.reduce((sum, value) => sum + value, 0) / threeMonth.length
      : slide.currentValue
  const prior = slide.priorValue
  const current = slide.currentValue
  const percent = prior === 0 ? 0 : ((current - prior) / Math.abs(prior)) * 100
  return [
    { label: slide.kpiLabel, current, prior, change: current - prior, percent },
    {
      label: 'Current month run rate',
      current: threeMonthAverage,
      prior,
      change: threeMonthAverage - prior,
      percent: prior === 0 ? 0 : ((threeMonthAverage - prior) / Math.abs(prior)) * 100,
    },
    {
      label: 'Package variance',
      current: slide.varianceAmount,
      prior: 0,
      change: slide.varianceAmount,
      percent: slide.variancePercent,
    },
  ]
}

function evidenceAccountsForSlide(slide: SlideDto) {
  return slide.blocks
    .flatMap((b) => readReportAccounts(parseJson<Record<string, unknown>>(b.contentJson, {})))
    .filter((account, index, all) => {
      const key = (account.code || account.name).toLowerCase()
      return all.findIndex((candidate) => (candidate.code || candidate.name).toLowerCase() === key) === index
    })
}

function evidenceTransactionsForSlide(slide: SlideDto) {
  return slide.blocks
    .flatMap((b) => readReportTransactions(parseJson<Record<string, unknown>>(b.contentJson, {})))
    .slice(0, 24)
}

function defaultContent(kind: string, slide: SlideDto) {
  if (kind === 'kpi')
    return { label: slide.kpiLabel, componentTitle: slide.kpiLabel, current: slide.currentValue, prior: slide.priorValue, width: 'half' }
  if (kind === 'text')
    return { text: 'Add board-ready commentary here.', componentTitle: slide.subject, width: 'full' }
  if (kind === 'callout')
    return { text: 'Highlighted insight.', componentTitle: 'Callout', width: 'half' }
  if (kind === 'chart')
    return { type: 'clustered', showPY: true, showLegend: true, showDataLabels: true, componentTitle: slide.kpiLabel, width: 'full' }
  if (kind === 'table')
    return { componentVariant: 'financial-summary', componentTitle: slide.kpiLabel, width: 'full' }
  if (kind === 'drivers')
    return { componentTitle: 'Driver evidence', section: slide.kpiLabel, width: 'full' }
  return {}
}

function reportComponentLibrary(slide: SlideDto): ReportComponentDefinition[] {
  const evidenceAccounts = evidenceAccountsForSlide(slide)
  const evidenceTransactions = evidenceTransactionsForSlide(slide)
  return [
    {
      id: 'key-number',
      category: 'Key numbers',
      kind: 'kpi',
      label: 'Key number',
      description: 'Single metric with comparison and variance.',
      variant: 'key-number',
      width: 'half',
      content: { ...defaultContent('kpi', slide), componentVariant: 'key-number', width: 'half' },
    },
    {
      id: 'sparkline-metric',
      category: 'Key numbers',
      kind: 'kpi',
      label: 'Sparkline metric',
      description: 'Metric with month trend and PY comparison.',
      variant: 'sparkline-metric',
      width: 'half',
      content: { ...defaultContent('kpi', slide), componentVariant: 'sparkline-metric', width: 'half' },
    },
    {
      id: 'variance-tile',
      category: 'Key numbers',
      kind: 'kpi',
      label: 'Variance tile',
      description: 'Current, prior, dollar change, and percent change.',
      variant: 'variance-tile',
      width: 'third',
      content: { ...defaultContent('kpi', slide), componentVariant: 'variance-tile', width: 'third' },
    },
    {
      id: 'monthly-trend',
      category: 'Charts',
      kind: 'chart',
      label: 'Monthly trend',
      description: 'Current year bars with prior-year comparison.',
      variant: 'monthly-trend',
      width: 'full',
      content: { ...defaultContent('chart', slide), componentVariant: 'monthly-trend', width: 'full' },
    },
    {
      id: 'rolling-12',
      category: 'Charts',
      kind: 'chart',
      label: 'Rolling 12 months',
      description: 'Rolling view for board trends and seasonality.',
      variant: 'rolling-12',
      width: 'full',
      content: {
        ...defaultContent('chart', slide),
        componentVariant: 'rolling-12',
        componentTitle: `${slide.kpiLabel} rolling trend`,
        width: 'full',
      },
    },
    {
      id: 'year-over-year',
      category: 'Charts',
      kind: 'chart',
      label: 'This year vs last year',
      description: 'Side-by-side monthly current vs prior-year bars.',
      variant: 'year-over-year',
      width: 'twoThirds',
      content: { ...defaultContent('chart', slide), componentVariant: 'year-over-year', width: 'twoThirds' },
    },
    {
      id: 'budget-projection',
      category: 'Charts',
      kind: 'chart',
      label: 'Budget projection',
      description: 'Budget-ready placeholder for forecast/budget series.',
      variant: 'budget-projection',
      width: 'twoThirds',
      content: {
        ...defaultContent('chart', slide),
        componentVariant: 'budget-projection',
        componentTitle: `${slide.kpiLabel} budget projection`,
        width: 'twoThirds',
      },
    },
    {
      id: 'scenario-chart',
      category: 'Charts',
      kind: 'chart',
      label: 'Scenario chart',
      description: 'Scenario comparison module for forecast packages.',
      variant: 'scenario-chart',
      width: 'twoThirds',
      content: {
        ...defaultContent('chart', slide),
        componentVariant: 'scenario-chart',
        componentTitle: `${slide.kpiLabel} scenarios`,
        width: 'twoThirds',
      },
    },
    {
      id: 'breakdown-chart',
      category: 'Charts',
      kind: 'chart',
      label: 'Account breakdown',
      description: 'Ranked bar view of the accounts driving this line.',
      variant: 'breakdown-chart',
      width: 'full',
      content: {
        ...defaultContent('chart', slide),
        componentVariant: 'breakdown-chart',
        componentTitle: `${slide.kpiLabel} account breakdown`,
        width: 'full',
        topAccounts: evidenceAccounts,
      },
    },
    {
      id: 'composition-pie',
      category: 'Charts',
      kind: 'chart',
      label: 'Composition chart',
      description: 'Donut-style mix of accounts or statement components.',
      variant: 'composition-pie',
      width: 'half',
      content: {
        ...defaultContent('chart', slide),
        componentVariant: 'composition-pie',
        componentTitle: `${slide.kpiLabel} mix`,
        width: 'half',
        topAccounts: evidenceAccounts,
      },
    },
    {
      id: 'variance-waterfall',
      category: 'Charts',
      kind: 'chart',
      label: 'Variance waterfall',
      description: 'Prior-to-current bridge for board variance stories.',
      variant: 'variance-waterfall',
      width: 'twoThirds',
      content: {
        ...defaultContent('chart', slide),
        componentVariant: 'variance-waterfall',
        componentTitle: `${slide.kpiLabel} variance bridge`,
        width: 'twoThirds',
      },
    },
    {
      id: 'income-statement',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Income statement',
      description: 'Current month summary with variance columns.',
      variant: 'financial-summary',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'financial-summary',
        componentTitle: 'Income statement',
        width: 'full',
      },
    },
    {
      id: 'trended-pl',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Trended P&L',
      description: 'Month-by-month financial table with total column.',
      variant: 'trended-pl',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'trended-pl',
        componentTitle: 'Trended P&L',
        width: 'full',
      },
    },
    {
      id: 'balance-sheet-variance',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Balance sheet variance',
      description: 'Prior month and current month variance view.',
      variant: 'variance-table',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'variance-table',
        componentTitle: 'Balance sheet variance',
        width: 'full',
      },
    },
    {
      id: 'transaction-drilldown',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Transaction drilldown',
      description: 'Linked accounts and GL evidence for the narrative.',
      variant: 'transaction-drilldown',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'transaction-drilldown',
        componentTitle: 'Transaction drilldown',
        width: 'full',
        topAccounts: evidenceAccounts,
        transactions: evidenceTransactions,
      },
    },
    {
      id: 'account-composition',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Account composition',
      description: 'Account mix, mapping, balance, and transaction count.',
      variant: 'account-composition',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'account-composition',
        componentTitle: 'Account composition',
        width: 'full',
        topAccounts: evidenceAccounts,
      },
    },
    {
      id: 'kpi-comparatives',
      category: 'Tables & financials',
      kind: 'table',
      label: 'KPI comparatives',
      description: 'Prior, current, dollar change, and percent change rows.',
      variant: 'kpi-comparatives',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'kpi-comparatives',
        componentTitle: 'KPI comparatives',
        width: 'full',
      },
    },
    {
      id: 'flux-summary',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Flux summary',
      description: 'Board-ready variance table tied to flux review logic.',
      variant: 'flux-summary',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'flux-summary',
        componentTitle: 'Flux summary',
        width: 'full',
      },
    },
    {
      id: 'cash-flow-summary',
      category: 'Tables & financials',
      kind: 'table',
      label: 'Cash flow bridge',
      description: 'Draft cash impact bridge for executive packages.',
      variant: 'cash-flow-summary',
      width: 'full',
      content: {
        ...defaultContent('table', slide),
        componentVariant: 'cash-flow-summary',
        componentTitle: 'Cash flow bridge',
        width: 'full',
      },
    },
    {
      id: 'heading',
      category: 'Text & narrative',
      kind: 'text',
      label: 'Section heading',
      description: 'Large page or section title.',
      variant: 'heading',
      width: 'full',
      content: {
        ...defaultContent('text', slide),
        componentVariant: 'heading',
        componentTitle: slide.subject,
        width: 'full',
      },
    },
    {
      id: 'commentary',
      category: 'Text & narrative',
      kind: 'text',
      label: 'Commentary',
      description: 'Board-ready narrative paragraph.',
      variant: 'commentary',
      width: 'full',
      content: { ...defaultContent('text', slide), componentVariant: 'commentary', width: 'full' },
    },
    {
      id: 'note',
      category: 'Text & narrative',
      kind: 'text',
      label: 'Note',
      description: 'Short internal or board note.',
      variant: 'note',
      width: 'half',
      content: {
        ...defaultContent('text', slide),
        componentVariant: 'note',
        text: 'Add a concise note.',
        width: 'half',
      },
    },
    {
      id: 'driver-evidence',
      category: 'AI & evidence',
      kind: 'drivers',
      label: 'Driver evidence',
      description: 'Statement line, accounts, and variance evidence.',
      variant: 'driver-evidence',
      width: 'full',
      content: { ...defaultContent('drivers', slide), componentVariant: 'driver-evidence', width: 'full' },
    },
    {
      id: 'callout',
      category: 'AI & evidence',
      kind: 'callout',
      label: 'Insight callout',
      description: 'Highlighted conclusion or AI recommendation.',
      variant: 'callout',
      width: 'half',
      content: { ...defaultContent('callout', slide), componentVariant: 'callout', width: 'half' },
    },
    {
      id: 'divider',
      category: 'Layout & media',
      kind: 'divider',
      label: 'Divider',
      description: 'Visual separation between report sections.',
      variant: 'divider',
      width: 'full',
      content: { componentVariant: 'divider', width: 'full' },
    },
    {
      id: 'image',
      category: 'Layout & media',
      kind: 'image',
      label: 'Image or logo',
      description: 'Brand, appendix, or supporting image placeholder.',
      variant: 'image',
      width: 'half',
      content: { componentVariant: 'image', componentTitle: 'Image', width: 'half' },
    },
  ]
}

// ---------------------------------------------------------------------------
// Chart sub-components — copied from App.tsx; travel with SlideBlock.
// ---------------------------------------------------------------------------

export function ColumnChart({ current, prior }: { current: number[]; prior: number[] }) {
  const count = Math.max(current.length, prior.length)
  const values = Array.from({ length: count }, (_, index) => ({
    current: Number(current[index] ?? 0),
    prior: Number(prior[index] ?? 0),
  }))
  const max = Math.max(...values.flatMap((value) => [Math.abs(value.current), Math.abs(value.prior)]), 1)
  const monthLabels = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

  if (count === 0) {
    return <div className="empty-state compact">No monthly chart data is attached to this slide yet.</div>
  }

  return (
    <div className="column-chart" style={{ gridTemplateColumns: `repeat(${count}, minmax(34px, 1fr))` }}>
      {values.map((value, index) => (
        <div className="bar-column" key={`${index}-${value.current}-${value.prior}`}>
          <div className="bar-pair">
            <span
              className={value.prior < 0 ? 'bar prior negative' : 'bar prior'}
              title={`Prior ${fmtMoney(value.prior)}`}
              style={{ height: `${Math.max(2, (Math.abs(value.prior) / max) * 100)}%` }}
            />
            <span
              className={value.current < 0 ? 'bar current negative' : 'bar current'}
              title={`Current ${fmtMoney(value.current)}`}
              style={{ height: `${Math.max(2, (Math.abs(value.current) / max) * 100)}%` }}
            />
          </div>
          <small>{monthLabels[index] ?? `P${index + 1}`}</small>
        </div>
      ))}
    </div>
  )
}

export function BreakdownChart({
  accounts,
  slide,
}: {
  accounts: ReturnType<typeof readReportAccounts>
  slide: SlideDto
}) {
  const rows =
    accounts.length > 0
      ? accounts.slice(0, 8)
      : [{ code: '', name: slide.kpiLabel, fsLine: slide.kpiLabel, aiSuggestedFsLine: '', current: slide.currentValue, transactionCount: 0 }]
  const max = Math.max(...rows.map((account) => Math.abs(account.current)), Math.abs(slide.currentValue), 1)
  return (
    <div className="breakdown-chart">
      {rows.map((account) => (
        <div key={`${account.code}-${account.name}`} className="breakdown-row">
          <div>
            <strong>{account.name || slide.kpiLabel}</strong>
            <small>
              {account.code || 'Statement rollup'} · {account.fsLine || account.aiSuggestedFsLine || 'Unmapped'}
            </small>
          </div>
          <div className="breakdown-track">
            <span
              className={account.current < 0 ? 'negative' : ''}
              style={{ width: `${Math.max(4, (Math.abs(account.current) / max) * 100)}%` }}
            />
          </div>
          <strong className="mono">{fmtMoney(account.current || slide.currentValue)}</strong>
        </div>
      ))}
    </div>
  )
}

export function CompositionChart({
  accounts,
  slide,
}: {
  accounts: ReturnType<typeof readReportAccounts>
  slide: SlideDto
}) {
  const rows =
    accounts.length > 0
      ? accounts.slice(0, 5)
      : statementRowsForSlide(slide)
          .slice(0, 3)
          .map((row) => ({
            code: row.label,
            name: row.label,
            fsLine: row.label,
            aiSuggestedFsLine: '',
            current: row.current,
            transactionCount: 0,
          }))
  const total = Math.max(
    rows.reduce((sum, account) => sum + Math.abs(account.current), 0),
    1,
  )
  let offset = 0
  const stops = rows
    .map((account, index) => {
      const share = (Math.abs(account.current) / total) * 100
      const start = offset
      offset += share
      return `var(--chart-${(index % 5) + 1}) ${start}% ${offset}%`
    })
    .join(', ')
  return (
    <div className="composition-chart">
      <div className="composition-donut" style={{ background: `conic-gradient(${stops})` }}>
        <span>{rows.length}</span>
      </div>
      <div className="composition-legend">
        {rows.map((account, index) => (
          <div key={`${account.code}-${account.name}`}>
            <i style={{ background: `var(--chart-${(index % 5) + 1})` }} />
            <span>{account.name || account.code}</span>
            <strong>{((Math.abs(account.current) / total) * 100).toFixed(1)}%</strong>
          </div>
        ))}
      </div>
    </div>
  )
}

export function WaterfallChart({ slide }: { slide: SlideDto }) {
  const rows = [
    { label: 'Prior', amount: slide.priorValue, type: 'base' },
    { label: 'Change', amount: slide.varianceAmount, type: slide.varianceAmount >= 0 ? 'up' : 'down' },
    { label: 'Current', amount: slide.currentValue, type: 'base' },
  ]
  const max = Math.max(...rows.map((row) => Math.abs(row.amount)), 1)
  return (
    <div className="waterfall-chart">
      {rows.map((row) => (
        <div className={`waterfall-step ${row.type}`} key={row.label}>
          <div className="waterfall-bar" style={{ height: `${Math.max(8, (Math.abs(row.amount) / max) * 100)}%` }} />
          <strong>{fmtMoney(row.amount)}</strong>
          <small>{row.label}</small>
        </div>
      ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// DriverEvidence — copied from App.tsx; tightly coupled to SlideBlock.
// ---------------------------------------------------------------------------

export function DriverEvidence({ content, slide }: { content: Record<string, unknown>; slide: SlideDto }) {
  const evidenceAccounts = readReportAccounts(content)
  const evidenceTransactions = readReportTransactions(content)
  const linkedAccounts = [
    ...splitAccountCodes(slide.accountCodesCsv),
    ...((Array.isArray(content.accounts) ? content.accounts : []) as unknown[])
      .map((item) => String(item).trim())
      .filter(Boolean),
    ...evidenceAccounts.map((account) => account.code),
  ].filter(
    (item, index, all) =>
      all.findIndex((candidate) => candidate.toLowerCase() === item.toLowerCase()) === index,
  )
  const section = String(content.section ?? slide.kpiLabel ?? slide.subject).trim()

  return (
    <div className="driver-list">
      <div>
        <span className="mono">Statement section</span>
        <strong>{section || 'Package rollup'}</strong>
        <small>{slide.subject}</small>
      </div>
      <div>
        <span className="mono">Current vs prior</span>
        <strong>
          {fmtMoney(slide.currentValue)} vs {fmtMoney(slide.priorValue)}
        </strong>
        <small>{slide.variancePercent.toFixed(1)}% change</small>
      </div>
      <div>
        <span className="mono">Variance</span>
        <strong>{fmtMoney(slide.varianceAmount)}</strong>
        <small>
          {linkedAccounts.length > 0
            ? `${pluralize(linkedAccounts.length, 'linked account')} · ${pluralize(evidenceTransactions.length, 'transaction')}`
            : 'No account-level source attached to this rollup'}
        </small>
      </div>
      {evidenceAccounts.slice(0, 9).map((account) => (
        <div key={account.code}>
          <span className="mono">{account.code}</span>
          <strong>{account.name}</strong>
          <small>
            {fmtMoney(account.current)} · {account.transactionCount} tx ·{' '}
            {account.fsLine || account.aiSuggestedFsLine || 'Unmapped'}
          </small>
        </div>
      ))}
      {evidenceAccounts.length === 0 &&
        linkedAccounts.slice(0, 9).map((account) => (
          <div key={account}>
            <span className="mono">Linked account</span>
            <strong>{shortId(account)}</strong>
            <small>Use Statements & transactions or Flux drilldown for transaction detail.</small>
          </div>
        ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// FinancialTableBlock
// ---------------------------------------------------------------------------

export function FinancialTableBlock({
  content,
  slide,
}: {
  content: Record<string, unknown>
  slide: SlideDto
}) {
  const variant = String(content.componentVariant ?? 'financial-summary')
  const currentMonthly = parseJson<number[]>(slide.monthlyJson, [])
  const priorMonthly = parseJson<number[]>(slide.priorMonthlyJson, [])
  const accounts = splitAccountCodes(slide.accountCodesCsv)
  const evidenceAccounts = readReportAccounts(content)
  const evidenceTransactions = readReportTransactions(content)

  if (variant === 'account-composition') {
    const rows =
      evidenceAccounts.length > 0
        ? evidenceAccounts.slice(0, 10)
        : accounts
            .slice(0, 10)
            .map((code) => ({ code, name: 'Linked account', fsLine: slide.kpiLabel, aiSuggestedFsLine: '', current: 0, transactionCount: 0 }))
    return (
      <table className="mini-table financial-table">
        <thead>
          <tr>
            <th>Account</th>
            <th>FS line</th>
            <th>Current</th>
            <th>Mix</th>
            <th>Tx</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((account) => {
            const mix =
              Math.abs(slide.currentValue) < 0.01
                ? 0
                : (Math.abs(account.current) / Math.abs(slide.currentValue)) * 100
            return (
              <tr key={account.code || account.name}>
                <td>
                  {account.code} · {account.name}
                </td>
                <td>{account.fsLine || account.aiSuggestedFsLine || 'Unmapped'}</td>
                <td>{account.current ? fmtMoney(account.current) : 'See GL'}</td>
                <td>{mix.toFixed(1)}%</td>
                <td>{account.transactionCount || '—'}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    )
  }

  if (variant === 'kpi-comparatives' || variant === 'flux-summary') {
    const rows = statementRowsForSlide(slide)
    return (
      <table className="mini-table financial-table">
        <thead>
          <tr>
            <th>{variant === 'flux-summary' ? 'Flux line' : 'Metric'}</th>
            <th>Prior</th>
            <th>Current</th>
            <th>$ Change</th>
            <th>% Change</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.label}>
              <td>{row.label}</td>
              <td>{fmtMoney(row.prior)}</td>
              <td>{fmtMoney(row.current)}</td>
              <td>{fmtMoney(row.change)}</td>
              <td>{fmtVariancePercent(row.percent, row.prior, row.current)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    )
  }

  if (variant === 'cash-flow-summary') {
    const netIncome = slide.currentValue
    const workingCapital = slide.varianceAmount * -0.35
    const other = slide.varianceAmount * 0.15
    const endingCashImpact = netIncome + workingCapital + other
    return (
      <table className="mini-table financial-table">
        <thead>
          <tr>
            <th>Cash flow bridge</th>
            <th>{slide.subject}</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Net income / statement contribution</td>
            <td>{fmtMoney(netIncome)}</td>
          </tr>
          <tr>
            <td>Working capital movement</td>
            <td>{fmtMoney(workingCapital)}</td>
          </tr>
          <tr>
            <td>Other non-cash / timing items</td>
            <td>{fmtMoney(other)}</td>
          </tr>
          <tr>
            <td>
              <strong>Estimated cash impact</strong>
            </td>
            <td>
              <strong>{fmtMoney(endingCashImpact)}</strong>
            </td>
          </tr>
        </tbody>
      </table>
    )
  }

  if (variant === 'trended-pl') {
    const labels = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
    return (
      <table className="mini-table financial-table trended">
        <thead>
          <tr>
            <th>{slide.kpiLabel}</th>
            {labels.map((label) => (
              <th key={label}>{label}</th>
            ))}
            <th>Total</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Current year</td>
            {labels.map((label, index) => (
              <td key={label}>{fmtMoney(Number(currentMonthly[index] ?? 0))}</td>
            ))}
            <td>{fmtMoney(currentMonthly.reduce((sum, value) => sum + Number(value || 0), 0))}</td>
          </tr>
          <tr>
            <td>Prior year</td>
            {labels.map((label, index) => (
              <td key={label}>{fmtMoney(Number(priorMonthly[index] ?? 0))}</td>
            ))}
            <td>{fmtMoney(priorMonthly.reduce((sum, value) => sum + Number(value || 0), 0))}</td>
          </tr>
        </tbody>
      </table>
    )
  }

  if (variant === 'transaction-drilldown') {
    if (evidenceTransactions.length > 0) {
      return (
        <table className="mini-table financial-table transaction-preview-table">
          <thead>
            <tr>
              <th>Date</th>
              <th>Account</th>
              <th>Description</th>
              <th>Amount</th>
              <th>Source</th>
            </tr>
          </thead>
          <tbody>
            {evidenceTransactions.slice(0, 12).map((transaction, index) => (
              <tr key={`${transaction.date}-${transaction.code}-${index}`}>
                <td>{transaction.date}</td>
                <td>
                  {transaction.code} · {transaction.name}
                </td>
                <td>{transaction.description}</td>
                <td>{fmtMoney(transaction.amount)}</td>
                <td>{transaction.source}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )
    }

    if (evidenceAccounts.length > 0) {
      return (
        <table className="mini-table financial-table">
          <thead>
            <tr>
              <th>Linked account</th>
              <th>FS line</th>
              <th>Current</th>
              <th>Transactions</th>
            </tr>
          </thead>
          <tbody>
            {evidenceAccounts.slice(0, 10).map((account) => (
              <tr key={account.code}>
                <td>
                  {account.code} · {account.name}
                </td>
                <td>{account.fsLine || account.aiSuggestedFsLine || 'Unmapped'}</td>
                <td>{fmtMoney(account.current)}</td>
                <td>{account.transactionCount}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )
    }

    return (
      <table className="mini-table financial-table">
        <thead>
          <tr>
            <th>Linked account</th>
            <th>Current</th>
            <th>Prior</th>
            <th>Evidence</th>
          </tr>
        </thead>
        <tbody>
          {(accounts.length ? accounts : ['Ungrouped rollup']).slice(0, 8).map((account, index) => (
            <tr key={account}>
              <td>{account}</td>
              <td>{index === 0 ? fmtMoney(slide.currentValue) : 'See GL'}</td>
              <td>{index === 0 ? fmtMoney(slide.priorValue) : 'See GL'}</td>
              <td>Open Statements & transactions for journal detail</td>
            </tr>
          ))}
        </tbody>
      </table>
    )
  }

  // Default: financial-summary / variance-table
  return (
    <table className="mini-table financial-table">
      <thead>
        <tr>
          <th>Financial line</th>
          <th>Current</th>
          <th>Prior</th>
          <th>$ change</th>
          <th>% change</th>
        </tr>
      </thead>
      <tbody>
        {statementRowsForSlide(slide).map((row) => (
          <tr key={row.label}>
            <td>{row.label}</td>
            <td>{fmtMoney(row.current)}</td>
            <td>{fmtMoney(row.prior)}</td>
            <td>{fmtMoney(row.change)}</td>
            <td>{row.percent.toFixed(1)}%</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

// ---------------------------------------------------------------------------
// BlockInspector
// ---------------------------------------------------------------------------

export function BlockInspector({
  block,
  slide,
  onUpdate,
  onResize,
  onChat,
  onRemove,
}: {
  block: SlideBlockDto | null
  slide: SlideDto
  onUpdate: (content: unknown) => void
  onResize: (width: ReportBlockWidth) => void
  onChat: () => void
  onRemove: () => void
}) {
  if (!block) {
    return (
      <aside className="component-inspector empty">
        <div className="library-header">
          <div>
            <strong>Component settings</strong>
            <span>Select a report module to edit sizing, labels, and display options.</span>
          </div>
          <Settings size={17} />
        </div>
      </aside>
    )
  }

  const content = parseJson<Record<string, unknown>>(block.contentJson, {})
  const variant = String(content.componentVariant ?? content.variant ?? block.kind)
  const title = String(content.componentTitle ?? content.label ?? content.title ?? slide.kpiLabel)
  const width = blockWidth(block.kind, content)
  const update = (patch: Record<string, unknown>) => onUpdate({ ...content, ...patch })

  return (
    <aside className="component-inspector">
      <div className="inspector-heading">
        <div>
          <span className="component-icon">{componentIcon(block.kind, variant)}</span>
          <div>
            <strong>{componentLabel(block.kind, variant)}</strong>
            <small>{REPORT_BLOCK_WIDTHS.find((option) => option.width === width)?.label ?? 'Custom width'}</small>
          </div>
        </div>
        <button type="button" title="Ask AI about this component" onClick={onChat}>
          <MessageSquare size={15} />
        </button>
      </div>
      <label className="inspector-field">
        <span>Title</span>
        <input
          value={title}
          onChange={(event) =>
            update({ componentTitle: event.target.value, label: event.target.value, title: event.target.value })
          }
        />
      </label>
      <div className="inspector-field">
        <span>Horizontal size</span>
        <div className="resize-control">
          {REPORT_BLOCK_WIDTHS.map((option) => (
            <button
              key={option.width}
              className={option.width === width ? 'active' : ''}
              type="button"
              onClick={() => onResize(option.width)}
            >
              {option.columns}/12
            </button>
          ))}
        </div>
      </div>
      {block.kind === 'chart' && (
        <>
          <label className="check-row">
            <input
              type="checkbox"
              checked={content.showLegend !== false}
              onChange={(event) => update({ showLegend: event.target.checked })}
            />
            Show legend
          </label>
          <label className="check-row">
            <input
              type="checkbox"
              checked={content.showDataLabels !== false}
              onChange={(event) => update({ showDataLabels: event.target.checked })}
            />
            Show data labels
          </label>
          <label className="inspector-field">
            <span>Frequency</span>
            <select
              value={String(content.frequency ?? 'Month')}
              onChange={(event) => update({ frequency: event.target.value })}
            >
              <option>Month</option>
              <option>Quarter</option>
              <option>Year</option>
            </select>
          </label>
        </>
      )}
      {block.kind === 'table' && (
        <label className="inspector-field">
          <span>Table style</span>
          <select
            value={variant}
            onChange={(event) => update({ componentVariant: event.target.value })}
          >
            <option value="financial-summary">This month summary</option>
            <option value="trended-pl">Trended financials</option>
            <option value="variance-table">Variance table</option>
            <option value="transaction-drilldown">Transaction drilldown</option>
            <option value="account-composition">Account composition</option>
            <option value="kpi-comparatives">KPI comparatives</option>
            <option value="cash-flow-summary">Cash flow summary</option>
            <option value="flux-summary">Flux summary</option>
          </select>
        </label>
      )}
      {block.kind === 'text' && variant !== 'heading' && (
        <label className="inspector-field">
          <span>Commentary</span>
          <textarea
            value={String(content.text ?? '')}
            onChange={(event) => update({ text: event.target.value })}
            rows={4}
          />
        </label>
      )}
      <div className="inspector-actions">
        <Button variant="ghost" icon={<X size={14} />} onClick={onRemove}>
          Remove
        </Button>
      </div>
    </aside>
  )
}

// ---------------------------------------------------------------------------
// SlideBlock
// ---------------------------------------------------------------------------

export function SlideBlock({
  block,
  slide,
  selected,
  setSelected,
  onUpdate,
  onResize,
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
  onResize: (width: ReportBlockWidth) => void
  onRemove: () => void
  onChat: () => void
  onDragStart: () => void
  onDragOver: (event: React.DragEvent) => void
  onDrop: () => void
}) {
  const content = parseJson<Record<string, unknown>>(block.contentJson, {})
  const width = blockWidth(block.kind, content)
  const variant = String(content.componentVariant ?? content.variant ?? block.kind)
  const title = String(content.componentTitle ?? content.label ?? content.title ?? slide.kpiLabel)

  const startResize = (event: React.PointerEvent<HTMLButtonElement>) => {
    event.preventDefault()
    event.stopPropagation()
    const startX = event.clientX
    const startColumns = widthToColumns(width)
    const grid = event.currentTarget.closest('.report-grid')
    const gridWidth = grid?.getBoundingClientRect().width ?? 720
    const columnWidth = Math.max(48, gridWidth / 12)
    const finish = (moveEvent: PointerEvent) => {
      const nextColumns = startColumns + Math.round((moveEvent.clientX - startX) / columnWidth)
      onResize(nearestReportWidth(nextColumns))
      window.removeEventListener('pointerup', finish)
      window.removeEventListener('pointercancel', finish)
    }
    window.addEventListener('pointerup', finish)
    window.addEventListener('pointercancel', finish)
  }

  return (
    <article
      className={`editor-block kind-${block.kind} ${blockWidthClass(block.kind, content)}${selected ? ' selected' : ''}`}
      draggable
      onDragStart={onDragStart}
      onDragOver={onDragOver}
      onDrop={onDrop}
      onClick={setSelected}
    >
      <div className="block-toolbar">
        <span className="block-kind">{componentLabel(block.kind, variant)}</span>
        <div className="block-width-pills" aria-label="Component width">
          {REPORT_BLOCK_WIDTHS.map((option) => (
            <button
              key={option.width}
              className={option.width === width ? 'active' : ''}
              title={`Resize to ${option.label}`}
              type="button"
              onClick={(event) => {
                event.stopPropagation()
                onResize(option.width)
              }}
            >
              {option.columns}
            </button>
          ))}
        </div>
        <button
          className="resize-handle"
          title="Drag to resize horizontally"
          type="button"
          onPointerDown={startResize}
        >
          ↔
        </button>
        <button title="Ask AI about this block" type="button" onClick={onChat}>
          <MessageSquare size={14} />
        </button>
        <button title="Delete block" type="button" onClick={onRemove}>
          <X size={14} />
        </button>
      </div>

      {block.kind === 'kpi' && (
        <div className={variant === 'sparkline-metric' ? 'kpi-block sparkline-metric' : 'kpi-block'}>
          <div>
            <span>{title}</span>
            <strong>{fmtMoney(Number(content.current ?? slide.currentValue))}</strong>
            {variant === 'sparkline-metric' && (
              <Sparkline
                current={parseJson<number[]>(slide.monthlyJson, [])}
                prior={parseJson<number[]>(slide.priorMonthlyJson, [])}
              />
            )}
          </div>
          <div className={slide.varianceAmount >= 0 ? 'good-text mono' : 'bad-text mono'}>
            {slide.varianceAmount >= 0 ? '▲' : '▼'} {fmtMoney(Math.abs(slide.varianceAmount))} ·{' '}
            {fmtVariancePercent(Math.abs(slide.variancePercent), slide.priorValue, slide.currentValue)}
          </div>
        </div>
      )}

      {block.kind === 'chart' && (
        <div className="chart-block">
          <div className="chart-title-row">
            <strong>{title}</strong>
            {content.showLegend !== false && (
              <span>
                <i /> Current <i className="prior" /> Prior year
              </span>
            )}
          </div>
          {/* P1.15 — chart-type routing per componentVariant. Cat 22.
              The audit found six declared variants all collapsed to ColumnChart. */}
          {variant === 'composition-pie' ? (
            <CompositionChart accounts={readReportAccounts(content)} slide={slide} />
          ) : variant === 'breakdown-chart' ? (
            <BreakdownChart accounts={readReportAccounts(content)} slide={slide} />
          ) : variant === 'variance-waterfall' || variant === 'waterfall-bridge' ? (
            <WaterfallChart slide={slide} />
          ) : variant === 'rolling-12' ? (
            <ColumnChart
              current={parseJson<number[]>(slide.monthlyJson, []).slice(-12)}
              prior={parseJson<number[]>(slide.priorMonthlyJson, []).slice(-12)}
            />
          ) : variant === 'year-over-year' ? (
            <ColumnChart
              current={parseJson<number[]>(slide.monthlyJson, []).slice(-12)}
              prior={parseJson<number[]>(slide.priorMonthlyJson, []).slice(-12)}
            />
          ) : (
            // Default = monthly-trend / budget-projection / scenario-chart all use the
            // standard column rendering until those gain dedicated visuals.
            <ColumnChart
              current={parseJson<number[]>(slide.monthlyJson, [])}
              prior={parseJson<number[]>(slide.priorMonthlyJson, [])}
            />
          )}
        </div>
      )}

      {block.kind === 'drivers' && <DriverEvidence content={content} slide={slide} />}

      {block.kind === 'text' && (
        <div className={variant === 'heading' ? 'text-block heading' : 'text-block'}>
          {variant === 'heading' && (
            <input
              value={title}
              onChange={(event) =>
                onUpdate({ ...content, componentTitle: event.target.value, label: event.target.value })
              }
            />
          )}
          {variant !== 'heading' && (
            <textarea
              value={String(content.text ?? '')}
              onChange={(event) => onUpdate({ ...content, text: event.target.value })}
              rows={variant === 'note' ? 2 : 4}
            />
          )}
        </div>
      )}

      {block.kind === 'callout' && (
        <div className="callout-block">
          <Sparkles size={16} />
          <textarea
            value={String(content.text ?? 'Highlighted insight')}
            onChange={(event) => onUpdate({ ...content, text: event.target.value })}
            rows={2}
          />
        </div>
      )}

      {block.kind === 'table' && <FinancialTableBlock content={content} slide={slide} />}

      {block.kind === 'divider' && <hr />}

      {block.kind === 'image' && <div className="image-placeholder">Image / upload block</div>}
    </article>
  )
}

// ---------------------------------------------------------------------------
// SlideEditor — primary export
// ---------------------------------------------------------------------------

export function SlideEditor({
  packageData,
  slide,
  issues,
  refreshPackages,
  notify,
  openChat,
  openHistory,
}: {
  packageData: PackageDto
  slide: SlideDto
  issues: IssueDto[]
  refreshPackages: () => Promise<void>
  notify: (message: string) => void
  openChat: (blockId?: string) => void
  openHistory: () => void
}) {
  const [history, setHistory] = useState<{
    past: SlideBlockDto[][]
    present: SlideBlockDto[]
    future: SlideBlockDto[][]
  }>({
    past: [],
    present: slide.blocks,
    future: [],
  })
  const [selectedBlock, setSelectedBlock] = useState<string | null>(slide.blocks[0]?.id ?? null)
  const [dragging, setDragging] = useState<string | null>(null)
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'error'>('idle')
  const [comments, setComments] = useState<PackageCommentDto[]>([])
  const [commentBody, setCommentBody] = useState('')
  const [libraryCategory, setLibraryCategory] = useState('Charts')
  const accountCodes = splitAccountCodes(slide.accountCodesCsv)

  const blocks = history.present
  const library = reportComponentLibrary(slide)
  const libraryCategories = Array.from(new Set(library.map((component) => component.category)))
  const activeLibraryCategory = libraryCategories.includes(libraryCategory)
    ? libraryCategory
    : libraryCategories[0]
  const visibleComponents = library.filter((component) => component.category === activeLibraryCategory)
  const selectedBlockRecord = selectedBlock
    ? blocks.find((b) => b.id === selectedBlock) ?? null
    : null

  useEffect(() => {
    window.scrollTo(0, 0)
  }, [slide.id])

  const loadComments = useCallback(async () => {
    const packageId = packageData.id
    const next = await fetchJson<PackageCommentDto[]>(
      `/api/packages/${packageId}/comments?slideId=${slide.id}`,
    )
    setComments(next)
  }, [packageData.id, slide.id])

  useEffect(() => {
    loadComments().catch(() => setComments([]))
  }, [loadComments])

  const setBlocks = (next: SlideBlockDto[]) => {
    setHistory((current) => ({
      past: [...current.past, current.present].slice(-30),
      present: next,
      future: [],
    }))
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

  const addBlock = (kind: string, contentOverrides: Record<string, unknown> = {}) => {
    const local = makeBlock(kind, { ...defaultContent(kind, slide), ...contentOverrides })
    const next = [...blocks, { ...local, sortOrder: blocks.length + 1 }]
    setBlocks(next)
    setSelectedBlock(local.id)
    void persist(async () =>
      postJson(`/api/slides/${slide.id}/blocks`, {
        kind,
        contentJson: local.contentJson,
        sortOrder: next.length,
      }),
    )
  }

  const updateBlock = (id: string, content: unknown) => {
    const contentJson = JSON.stringify(content)
    setBlocks(blocks.map((b) => (b.id === id ? { ...b, contentJson } : b)))
    void persist(async () =>
      putJson(`/api/blocks/${id}`, {
        kind: blocks.find((b) => b.id === id)?.kind ?? 'text',
        contentJson,
      }),
    )
  }

  const resizeBlock = (id: string, width: ReportBlockWidth) => {
    const target = blocks.find((b) => b.id === id)
    if (!target) return
    const content = parseJson<Record<string, unknown>>(target.contentJson, {})
    updateBlock(id, { ...content, width })
  }

  const removeBlock = (id: string) => {
    if (selectedBlock === id) setSelectedBlock(null)
    setBlocks(blocks.filter((b) => b.id !== id))
    void persist(async () => deleteJson(`/api/blocks/${id}`), 'Block removed.')
  }

  const moveDrag = (targetId: string) => {
    if (!dragging || dragging === targetId) return
    const moving = blocks.find((b) => b.id === dragging)
    if (!moving) return
    const without = blocks.filter((b) => b.id !== dragging)
    const targetIndex = without.findIndex((b) => b.id === targetId)
    const next = [...without.slice(0, targetIndex), moving, ...without.slice(targetIndex)].map(
      (b, index) => ({ ...b, sortOrder: index + 1 }),
    )
    setBlocks(next)
    setDragging(null)
    void persist(async () =>
      postJson(`/api/slides/${slide.id}/reorder-blocks`, { blockIds: next.map((b) => b.id) }),
    )
  }

  const addComment = async () => {
    if (!commentBody.trim()) return
    const packageId = packageData.id
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
    <div className="page report-page">
      <div className="slide-header">
        <div>
          <div className="eyebrow">
            {String(slide.sortOrder).padStart(2, '0')} ·{' '}
            {accountCodes.length > 0 ? pluralize(accountCodes.length, 'GL account') : 'Rollup line'} ·{' '}
            {packageData.period} · Xero {formatRelative(packageData.lastXeroSyncAt)}
          </div>
          <h1>{slide.subject}</h1>
        </div>
        <div className="actions">
          <span className={saveState === 'error' ? 'bad-text mono' : 'muted mono'}>
            {saveState === 'saving' ? 'Saving...' : saveState === 'error' ? 'Save failed' : 'Saved'}
          </span>
          <Button
            variant="ghost"
            icon={<Undo2 size={15} />}
            disabled={history.past.length === 0}
            onClick={undo}
          >
            Undo
          </Button>
          <Button
            variant="ghost"
            icon={<RefreshCw size={15} />}
            disabled={history.future.length === 0}
            onClick={redo}
          >
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

      <div className="slide-context-strip">
        {(packageData.blockReason || packageData.isSourceDataStale) && (
          <div className="slide-context-warning">
            <AlertTriangle size={14} />
            <span>
              {packageData.blockReason ??
                packageData.sourceDataStaleReason ??
                'Source data changed after this slide was generated.'}
            </span>
          </div>
        )}
        <div>
          <span>Current</span>
          <strong>{fmtMoney(slide.currentValue)}</strong>
        </div>
        <div>
          <span>Prior</span>
          <strong>{fmtMoney(slide.priorValue)}</strong>
        </div>
        <div>
          <span>Change</span>
          <strong className={slide.varianceAmount >= 0 ? 'good-text' : 'bad-text'}>
            {fmtMoney(slide.varianceAmount)}
          </strong>
        </div>
        <div>
          <span>Variance</span>
          <strong className={slide.varianceAmount >= 0 ? 'good-text' : 'bad-text'}>
            {fmtVariancePercent(slide.variancePercent, slide.priorValue, slide.currentValue)}
          </strong>
        </div>
        <div>
          <span>Package</span>
          <strong>{packageData.versionLabel}</strong>
        </div>
      </div>

      <div className="report-builder">
        <aside className="component-library-panel">
          <div className="library-header">
            <div>
              <strong>Modules</strong>
              <span>Drag-ready report components</span>
            </div>
            <PanelRightOpen size={17} />
          </div>
          <div className="library-category-list" role="tablist" aria-label="Report module categories">
            {libraryCategories.map((category) => (
              <button
                key={category}
                className={category === activeLibraryCategory ? 'active' : ''}
                type="button"
                onClick={() => setLibraryCategory(category)}
              >
                {category}
              </button>
            ))}
          </div>
          <div className="component-list">
            {visibleComponents.map((component) => (
              <button
                key={component.id}
                className="component-choice"
                type="button"
                onClick={() => addBlock(component.kind, component.content)}
              >
                <span className="component-icon">{componentIcon(component.kind, component.variant)}</span>
                <span>
                  <strong>{component.label}</strong>
                  <small>{component.description}</small>
                </span>
              </button>
            ))}
          </div>
        </aside>

        <section className="report-canvas-shell">
          <div className="report-canvas-toolbar">
            <div>
              <strong>{packageData.organizationName}</strong>
              <span>
                {packageData.period} · {slide.subject}
              </span>
            </div>
            <span className="muted mono">{pluralize(blocks.length, 'module')}</span>
          </div>
          <div className="report-canvas">
            <div className="report-grid">
              {blocks.map((b) => (
                <SlideBlock
                  key={b.id}
                  block={b}
                  slide={slide}
                  selected={selectedBlock === b.id}
                  setSelected={() => setSelectedBlock(b.id)}
                  onUpdate={(content) => updateBlock(b.id, content)}
                  onResize={(width) => resizeBlock(b.id, width)}
                  onRemove={() => removeBlock(b.id)}
                  onChat={() => openChat(b.id)}
                  onDragStart={() => setDragging(b.id)}
                  onDragOver={(event) => event.preventDefault()}
                  onDrop={() => moveDrag(b.id)}
                />
              ))}
              {blocks.length === 0 && (
                <div className="empty-state report-empty">
                  Choose a module from the library to start this report page.
                </div>
              )}
            </div>
          </div>
        </section>

        <BlockInspector
          block={selectedBlockRecord}
          slide={slide}
          onUpdate={(content) => selectedBlockRecord && updateBlock(selectedBlockRecord.id, content)}
          onResize={(width) => selectedBlockRecord && resizeBlock(selectedBlockRecord.id, width)}
          onChat={() => selectedBlockRecord && openChat(selectedBlockRecord.id)}
          onRemove={() => selectedBlockRecord && removeBlock(selectedBlockRecord.id)}
        />
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
          <textarea
            value={commentBody}
            onChange={(event) => setCommentBody(event.target.value)}
            placeholder="Add a review note"
          />
          <Button variant="primary" icon={<MessageSquare size={14} />} onClick={addComment}>
            Comment
          </Button>
        </div>
        <div className="comment-list">
          {comments.map((comment) => (
            <div
              key={comment.id}
              className={comment.status === 'Resolved' ? 'comment-item resolved' : 'comment-item'}
            >
              <div>
                <strong>{comment.author}</strong>
                <p>{comment.body}</p>
                <small>
                  {comment.slideBlockId ? 'Block note' : 'Slide note'} ·{' '}
                  {new Date(comment.createdAt).toLocaleDateString()}
                </small>
              </div>
              {comment.status !== 'Resolved' && (
                <Button variant="ghost" icon={<Check size={13} />} onClick={() => resolveComment(comment)}>
                  Resolve
                </Button>
              )}
            </div>
          ))}
          {comments.length === 0 && (
            <div className="empty-state compact">No comments on this slide yet.</div>
          )}
        </div>
      </Card>
    </div>
  )
}
