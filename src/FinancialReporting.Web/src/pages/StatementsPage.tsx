// TODO dedupe: EntityStatements, EntityLedgerSummary types are also declared in App.tsx
import { useEffect, useState } from 'react'
import { fetchJson } from '../api/client'
import { Card } from '../components/primitives'

// TODO dedupe: also in App.tsx
type EntityStatements = {
  organizationKey: string
  periodKey: string
  lines: Array<{ statementType: string; section: string; rowPath: string; lineName: string; accountCode: string; currentAmount: number; priorAmount: number; amountsJson: string }>
}

// TODO dedupe: also in App.tsx
type EntityLedgerSummary = {
  organizationKey: string
  periodKey: string
  journalLineCount: number
  lines: Array<{ accountCode: string; accountName: string; netAmount: number; transactionCount: number }>
}

// TODO dedupe: also in App.tsx
function fmtMoney(value: number) {
  const sign = value < 0 ? '−' : ''
  return `${sign}$${Math.abs(value).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}

export function StatementsView({ organizationKey, periodKey }: { organizationKey: string; periodKey: string }) {
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
