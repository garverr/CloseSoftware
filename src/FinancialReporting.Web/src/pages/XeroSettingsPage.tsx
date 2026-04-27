import { useCallback, useEffect, useState } from 'react'
import { fetchJson, postJson } from '../api/client'
import { Button, Card } from '../components/primitives'
import {
  AlertTriangle,
  ArrowUpRight,
  CheckCircle2,
  Database,
  Link,
  PlugZap,
  RefreshCw,
} from 'lucide-react'

// TODO: dedupe — mirrors ReportingContext in App.tsx
type ReportingContext = {
  organizations: OrganizationOption[]
  periods: PeriodOption[]
  packages: PackageOption[]
  coverage: ReportingCoverage[]
}

// TODO: dedupe — mirrors OrganizationOption in App.tsx
type OrganizationOption = {
  id: string
  key: string
  name: string
  abbreviation: string
  isConsolidated: boolean
  isXeroMapped: boolean
}

// TODO: dedupe — mirrors PeriodOption in App.tsx
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

// TODO: dedupe — mirrors PackageOption in App.tsx
type PackageOption = {
  id: string
  organizationKey: string
  organizationName: string
  periodKey: string
  periodLabel: string
  status: string
}

// TODO: dedupe — mirrors ReportingCoverage in App.tsx
type ReportingCoverage = {
  organizationKey: string
  periodKey: string
  packageId: string | null
  packageStatus: string | null
  ledgerActivityCount: number
}

// TODO: dedupe — mirrors XeroConnectionStatus in App.tsx
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

// TODO: dedupe — mirrors XeroTenantStatus in App.tsx
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

// TODO: dedupe — mirrors XeroLedgerSyncStatus in App.tsx
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

// TODO: dedupe — mirrors XeroStatus in App.tsx
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

// TODO: dedupe — mirrors XeroConnectResponse in App.tsx
type XeroConnectResponse = {
  authUrl: string | null
  state: string | null
  error: string | null
}

// TODO: dedupe — mirrors XeroBackfillPreview in App.tsx
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

// TODO: dedupe — mirrors XeroBackfillRun in App.tsx
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

// TODO: dedupe — mirrors XeroDataCoverage in App.tsx
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

const emptyReportingContext: ReportingContext = {
  organizations: [],
  periods: [],
  packages: [],
  coverage: [],
}

// Private file-local helper — mirrors fmtMoney in App.tsx
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

// Private file-local helper — mirrors formatRelative in App.tsx
function formatRelative(value: string | null) {
  if (!value) return '—'
  const minutes = Math.max(1, Math.round((Date.now() - new Date(value).getTime()) / 60000))
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} hr ago`
}

// Private file-local helper — mirrors formatDateTime in App.tsx
function formatDateTime(value: string | null) {
  if (!value) return 'Never'
  return new Date(value).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
}

// Private file-local helper — mirrors shortId in App.tsx
function shortId(value: string) {
  return value.length <= 12 ? value : `${value.slice(0, 8)}...${value.slice(-4)}`
}

export function XeroSettings({ notify }: { notify: (message: string) => void }) {
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
