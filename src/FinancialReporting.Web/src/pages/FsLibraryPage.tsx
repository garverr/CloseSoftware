import { useEffect, useMemo, useState } from 'react'
import { fetchJson } from '../api/client'
import { Card } from '../components/primitives'

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

type StatementTab = 'Income Statement' | 'Balance Sheet'

const STATEMENT_TABS: StatementTab[] = ['Income Statement', 'Balance Sheet']

export function FsLibraryView({ organizationKey }: { organizationKey: string }) {
  const [lines, setLines] = useState<FsLineDefinitionDto[]>([])
  const [accounts, setAccounts] = useState<AccountDto[]>([])
  const [stmtTab, setStmtTab] = useState<StatementTab>('Income Statement')
  const [openSections, setOpenSections] = useState<Record<string, boolean>>({})
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    if (!organizationKey) return
    let cancelled = false
    Promise.all([
      fetchJson<FsLineDefinitionDto[]>(
        `/api/mapping/fs-lines?organizationKey=${encodeURIComponent(organizationKey)}&includeInactive=true`,
      ),
      fetchJson<AccountDto[]>(`/api/mapping/accounts?organizationKey=${encodeURIComponent(organizationKey)}`),
    ])
      .then(([nextLines, nextAccounts]) => {
        if (cancelled) return
        setLines(Array.isArray(nextLines) ? nextLines : [])
        setAccounts(Array.isArray(nextAccounts) ? nextAccounts : [])
        setLoadError(null)
      })
      .catch(() => {
        if (cancelled) return
        setLines([])
        setAccounts([])
        setLoadError('Could not load FS line library.')
      })
    return () => {
      cancelled = true
    }
  }, [organizationKey])

  const accountsByLine = useMemo(() => {
    const map = new Map<string, AccountDto[]>()
    for (const account of accounts) {
      const key = (account.fsLine ?? '').trim()
      if (!key) continue
      const list = map.get(key.toLowerCase()) ?? []
      list.push(account)
      map.set(key.toLowerCase(), list)
    }
    return map
  }, [accounts])

  const grouped = useMemo(() => {
    const filter = (line: FsLineDefinitionDto) => {
      if (!line.statementType) return false
      const t = line.statementType.toLowerCase()
      if (stmtTab === 'Income Statement') return t.includes('income') || t.includes('p&l') || t.includes('p l') || t.includes('profit')
      return t.includes('balance')
    }
    const within = lines.filter(filter)
    const sectionMap = new Map<string, FsLineDefinitionDto[]>()
    for (const line of within) {
      const key = line.section || 'Other'
      const list = sectionMap.get(key) ?? []
      list.push(line)
      sectionMap.set(key, list)
    }
    return Array.from(sectionMap.entries()).map(([section, items]) => ({
      section,
      lines: items.sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)),
    }))
  }, [lines, stmtTab])

  const totals = useMemo(() => {
    const totalLines = grouped.reduce((sum, g) => sum + g.lines.length, 0)
    const mapped = grouped.reduce(
      (sum, g) => sum + g.lines.filter((l) => (accountsByLine.get(l.name.toLowerCase()) ?? []).length > 0).length,
      0,
    )
    return { totalLines, mapped, unmapped: totalLines - mapped }
  }, [grouped, accountsByLine])

  const toggleSection = (section: string) =>
    setOpenSections((prev) => ({ ...prev, [section]: prev[section] === false ? true : false }))

  const isOpen = (section: string) => openSections[section] !== false

  return (
    <div className="page cs-page-narrow">
      <div className="page-header">
        <div>
          <div className="eyebrow">Consolidation structure</div>
          <h1>FS Line Library</h1>
          <p>
            Define the financial-statement lines that Xero accounts roll up into ·{' '}
            {organizationKey || 'No entity selected'}
          </p>
        </div>
      </div>

      {loadError && (
        <div className="cs-alert warn" role="alert">
          <span className="cs-alert-dot" /> {loadError}
        </div>
      )}

      {totals.unmapped > 0 && (
        <div className="cs-alert warn" role="status">
          <span className="cs-alert-dot" />
          <span>
            <strong>{totals.unmapped} FS lines</strong> have no Xero accounts mapped — these will appear empty in
            the statements.
          </span>
        </div>
      )}

      <div className="cs-fs-toolbar">
        <div className="cs-seg-group">
          {STATEMENT_TABS.map((tab) => (
            <button
              key={tab}
              type="button"
              className={stmtTab === tab ? 'cs-seg active' : 'cs-seg'}
              onClick={() => setStmtTab(tab)}
            >
              {tab}
            </button>
          ))}
        </div>
        <span className="cs-fs-counts">
          {totals.totalLines} lines · {totals.mapped} mapped
        </span>
      </div>

      <Card className="cs-table-card">
        <div className="cs-fs-head">
          <span>FS Line name</span>
          <span>Mapped accounts</span>
          <span className="num">Status</span>
        </div>

        {grouped.length === 0 && (
          <div className="cs-table-empty">
            No FS lines defined for {stmtTab}. Lines are seeded automatically from defaults the first time the
            mapping endpoint runs.
          </div>
        )}

        {grouped.map((group) => (
          <div className="cs-fs-section" key={group.section}>
            <button
              type="button"
              className="cs-fs-section-head"
              onClick={() => toggleSection(group.section)}
              aria-expanded={isOpen(group.section)}
            >
              <span className="cs-fs-chev" aria-hidden>
                {isOpen(group.section) ? '▾' : '▸'}
              </span>
              <span className="cs-fs-section-label">{group.section}</span>
              <span className="cs-fs-section-count">{group.lines.length} lines</span>
            </button>

            {isOpen(group.section) &&
              group.lines.map((line) => {
                const mapped = accountsByLine.get(line.name.toLowerCase()) ?? []
                return (
                  <div className="cs-fs-row" key={line.id}>
                    <div className="cs-fs-row-name">
                      <span>{line.name}</span>
                      {!line.isActive && <span className="cs-tag tone-muted">Unused</span>}
                    </div>
                    <div className="cs-fs-row-accounts">
                      {mapped.length === 0 && <span className="cs-fs-row-empty">No accounts mapped</span>}
                      {mapped.map((account) => (
                        <span key={account.id} className="cs-fs-account-chip mono" title={account.name}>
                          {account.code}
                        </span>
                      ))}
                    </div>
                    <div className="cs-fs-row-status">
                      {mapped.length > 0 ? (
                        <span className="cs-pill good">
                          <span className="cs-status-dot" /> {mapped.length} acct
                        </span>
                      ) : (
                        <span className="cs-pill warn">
                          <span className="cs-status-dot" /> Empty
                        </span>
                      )}
                    </div>
                  </div>
                )
              })}
          </div>
        ))}
      </Card>
    </div>
  )
}
