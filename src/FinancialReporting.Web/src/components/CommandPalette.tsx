import { useEffect, useMemo, useRef, useState } from 'react'
import { fetchJson } from '../api/client'

export type PaletteOrganization = {
  key: string
  name: string
  abbreviation: string
}

export type PalettePeriod = {
  key: string
  label: string
}

export type PaletteSlide = {
  id: string
  sortOrder: number
  subject: string
  kpiLabel: string
}

type FsLineDefinitionDto = {
  id: string
  organizationId: string
  statementType: string
  section: string
  name: string
}

type AccountDto = {
  id: string
  code: string
  name: string
  type: string
  fsLine: string
}

export type CommandKind = 'view' | 'organization' | 'period' | 'slide' | 'fsline' | 'account'

type ViewName =
  | 'dashboard'
  | 'flux'
  | 'statements'
  | 'library'
  | 'kpis'
  | 'mapping'
  | 'fslibrary'
  | 'eliminations'
  | 'planning'
  | 'benchmarks'
  | 'parity'
  | 'output'
  | 'settings'
  | 'ai-settings'
  | 'xero-settings'
  | 'branding'
  | 'layouts'
  | 'livedash'

type ViewCommand = {
  kind: 'view'
  id: string
  view: ViewName
  label: string
  hint: string
  keywords: string
}

type OrgCommand = {
  kind: 'organization'
  id: string
  organizationKey: string
  label: string
  hint: string
  keywords: string
}

type PeriodCommand = {
  kind: 'period'
  id: string
  periodKey: string
  label: string
  hint: string
  keywords: string
}

type SlideCommand = {
  kind: 'slide'
  id: string
  slideId: string
  label: string
  hint: string
  keywords: string
}

type FsLineCommand = {
  kind: 'fsline'
  id: string
  label: string
  hint: string
  keywords: string
}

type AccountCommand = {
  kind: 'account'
  id: string
  accountCode: string
  label: string
  hint: string
  keywords: string
}

export type PaletteCommand =
  | ViewCommand
  | OrgCommand
  | PeriodCommand
  | SlideCommand
  | FsLineCommand
  | AccountCommand

const VIEW_COMMANDS: ViewCommand[] = [
  { kind: 'view', id: 'view:dashboard', view: 'dashboard', label: 'Entity Dashboard', hint: 'Workflow', keywords: 'dashboard overview entity' },
  { kind: 'view', id: 'view:flux', view: 'flux', label: 'Flux Review', hint: 'Workflow', keywords: 'flux variance review' },
  { kind: 'view', id: 'view:statements', view: 'statements', label: 'Statements & Transactions', hint: 'Workflow', keywords: 'statements transactions ledger journal' },
  { kind: 'view', id: 'view:library', view: 'library', label: 'Reporting Library', hint: 'Workflow', keywords: 'library reporting templates archive' },
  { kind: 'view', id: 'view:livedash', view: 'livedash', label: 'Live Dashboard', hint: 'Workflow', keywords: 'live dashboard kpi metrics' },
  { kind: 'view', id: 'view:mapping', view: 'mapping', label: 'Account Mapping', hint: 'Mapping', keywords: 'mapping accounts gl chart' },
  { kind: 'view', id: 'view:fslibrary', view: 'fslibrary', label: 'FS Line Library', hint: 'Mapping', keywords: 'fs line library statements consolidation' },
  { kind: 'view', id: 'view:eliminations', view: 'eliminations', label: 'Eliminations', hint: 'Mapping', keywords: 'eliminations intercompany consolidation' },
  { kind: 'view', id: 'view:planning', view: 'planning', label: 'Planning', hint: 'Package', keywords: 'planning budget forecast' },
  { kind: 'view', id: 'view:benchmarks', view: 'benchmarks', label: 'Benchmarking', hint: 'Package', keywords: 'benchmark peer industry' },
  { kind: 'view', id: 'view:kpis', view: 'kpis', label: 'KPIs', hint: 'Package', keywords: 'kpi key performance indicators metrics' },
  { kind: 'view', id: 'view:output', view: 'output', label: 'Share & Export', hint: 'Package tools', keywords: 'output share export pdf email distribution' },
  { kind: 'view', id: 'view:settings', view: 'settings', label: 'Settings', hint: 'Package tools', keywords: 'settings configuration' },
  { kind: 'view', id: 'view:ai-settings', view: 'ai-settings', label: 'AI Settings', hint: 'Settings', keywords: 'ai codex claude model reasoning' },
  { kind: 'view', id: 'view:xero-settings', view: 'xero-settings', label: 'Xero Settings', hint: 'Settings', keywords: 'xero tenant connection oauth sync' },
  { kind: 'view', id: 'view:branding', view: 'branding', label: 'Branding', hint: 'Settings', keywords: 'branding theme colors logo' },
  { kind: 'view', id: 'view:layouts', view: 'layouts', label: 'Layouts', hint: 'Settings', keywords: 'layouts page order header footer' },
  { kind: 'view', id: 'view:parity', view: 'parity', label: 'Competitive Parity', hint: 'Workflow', keywords: 'competitive parity comparison features' },
]

function matchesQuery(haystack: string, terms: string[]): boolean {
  if (terms.length === 0) return true
  const lower = haystack.toLowerCase()
  return terms.every((term) => lower.includes(term))
}

export function CommandPalette({
  open,
  onClose,
  organizations,
  periods,
  slides,
  hasPackage,
  selectedOrganizationKey,
  onView,
  onSelectOrganization,
  onSelectPeriod,
  onOpenSlide,
}: {
  open: boolean
  onClose: () => void
  organizations: PaletteOrganization[]
  periods: PalettePeriod[]
  slides: PaletteSlide[]
  hasPackage: boolean
  selectedOrganizationKey: string
  onView: (view: ViewName) => void
  onSelectOrganization: (organizationKey: string) => void
  onSelectPeriod: (periodKey: string) => void
  onOpenSlide: (slideId: string) => void
}) {
  const [query, setQuery] = useState('')
  const [activeIndex, setActiveIndex] = useState(0)
  const [fsLines, setFsLines] = useState<FsLineDefinitionDto[]>([])
  const [accounts, setAccounts] = useState<AccountDto[]>([])
  const inputRef = useRef<HTMLInputElement | null>(null)

  useEffect(() => {
    if (open) {
      setQuery('')
      setActiveIndex(0)
      const handle = window.setTimeout(() => inputRef.current?.focus(), 0)
      return () => window.clearTimeout(handle)
    }
    return undefined
  }, [open])

  useEffect(() => {
    if (!open || !selectedOrganizationKey) return
    let cancelled = false
    const orgParam = encodeURIComponent(selectedOrganizationKey)
    Promise.all([
      fetchJson<FsLineDefinitionDto[]>(`/api/mapping/fs-lines?organizationKey=${orgParam}`),
      fetchJson<AccountDto[]>(`/api/mapping/accounts?organizationKey=${orgParam}`),
    ])
      .then(([nextLines, nextAccounts]) => {
        if (cancelled) return
        setFsLines(Array.isArray(nextLines) ? nextLines : [])
        setAccounts(Array.isArray(nextAccounts) ? nextAccounts : [])
      })
      .catch(() => {
        if (cancelled) return
        setFsLines([])
        setAccounts([])
      })
    return () => {
      cancelled = true
    }
  }, [open, selectedOrganizationKey])

  const allCommands = useMemo<PaletteCommand[]>(() => {
    const orgCommands: OrgCommand[] = organizations.map((organization) => ({
      kind: 'organization',
      id: `org:${organization.key}`,
      organizationKey: organization.key,
      label: organization.name,
      hint: organization.abbreviation ? `Entity · ${organization.abbreviation}` : 'Entity',
      keywords: `entity organization ${organization.name} ${organization.abbreviation} ${organization.key}`,
    }))
    const periodCommands: PeriodCommand[] = periods.map((period) => ({
      kind: 'period',
      id: `period:${period.key}`,
      periodKey: period.key,
      label: period.label,
      hint: 'Reporting period',
      keywords: `period month ${period.label} ${period.key}`,
    }))
    const slideCommands: SlideCommand[] = hasPackage
      ? slides.map((slide) => ({
          kind: 'slide',
          id: `slide:${slide.id}`,
          slideId: slide.id,
          label: slide.subject || `Slide ${slide.sortOrder}`,
          hint: `Slide ${String(slide.sortOrder).padStart(2, '0')} · ${slide.kpiLabel || 'package'}`,
          keywords: `slide ${slide.subject} ${slide.kpiLabel}`,
        }))
      : []
    const fsLineCommands: FsLineCommand[] = fsLines.map((line) => ({
      kind: 'fsline',
      id: `fsline:${line.id}`,
      label: line.name,
      hint: `${line.statementType} · ${line.section}`,
      keywords: `fs line ${line.name} ${line.section} ${line.statementType}`,
    }))
    const accountCommands: AccountCommand[] = accounts.map((account) => ({
      kind: 'account',
      id: `account:${account.id}`,
      accountCode: account.code,
      label: `${account.code} · ${account.name}`,
      hint: account.fsLine ? `Account · ${account.fsLine}` : `Account · ${account.type}`,
      keywords: `account gl ${account.code} ${account.name} ${account.fsLine}`,
    }))
    return [
      ...VIEW_COMMANDS,
      ...orgCommands,
      ...periodCommands,
      ...slideCommands,
      ...fsLineCommands,
      ...accountCommands,
    ]
  }, [organizations, periods, slides, hasPackage, fsLines, accounts])

  const filtered = useMemo(() => {
    const terms = query
      .trim()
      .toLowerCase()
      .split(/\s+/)
      .filter(Boolean)
    if (terms.length === 0) return allCommands.slice(0, 60)
    return allCommands.filter((command) => matchesQuery(`${command.label} ${command.hint} ${command.keywords}`, terms)).slice(0, 60)
  }, [allCommands, query])

  useEffect(() => {
    if (activeIndex >= filtered.length) {
      setActiveIndex(filtered.length === 0 ? 0 : filtered.length - 1)
    }
  }, [filtered.length, activeIndex])

  if (!open) return null

  const groups: Array<{ key: string; label: string; items: PaletteCommand[] }> = [
    { key: 'view', label: 'Navigate', items: filtered.filter((c) => c.kind === 'view') },
    { key: 'organization', label: 'Entities', items: filtered.filter((c) => c.kind === 'organization') },
    { key: 'period', label: 'Periods', items: filtered.filter((c) => c.kind === 'period') },
    { key: 'slide', label: 'Package slides', items: filtered.filter((c) => c.kind === 'slide') },
    { key: 'fsline', label: 'FS lines', items: filtered.filter((c) => c.kind === 'fsline') },
    { key: 'account', label: 'GL accounts', items: filtered.filter((c) => c.kind === 'account') },
  ].filter((group) => group.items.length > 0)

  const orderedFlat: PaletteCommand[] = groups.flatMap((group) => group.items)

  const activate = (command: PaletteCommand) => {
    switch (command.kind) {
      case 'view':
        onView(command.view)
        break
      case 'organization':
        onSelectOrganization(command.organizationKey)
        break
      case 'period':
        onSelectPeriod(command.periodKey)
        break
      case 'slide':
        onOpenSlide(command.slideId)
        break
      case 'fsline':
        onView('fslibrary')
        break
      case 'account':
        onView('mapping')
        break
    }
    onClose()
  }

  const handleKey = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setActiveIndex((index) => Math.min(orderedFlat.length - 1, index + 1))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((index) => Math.max(0, index - 1))
    } else if (event.key === 'Enter') {
      event.preventDefault()
      const target = orderedFlat[activeIndex]
      if (target) activate(target)
    } else if (event.key === 'Escape') {
      event.preventDefault()
      onClose()
    }
  }

  let runningIndex = -1

  return (
    <div className="cs-palette-backdrop" role="dialog" aria-modal="true" aria-label="Command palette" onClick={onClose}>
      <div className="cs-palette" onClick={(event) => event.stopPropagation()}>
        <div className="cs-palette-input-row">
          <input
            ref={inputRef}
            type="text"
            value={query}
            onChange={(event) => {
              setQuery(event.target.value)
              setActiveIndex(0)
            }}
            onKeyDown={handleKey}
            placeholder="Search entities, periods, slides, FS lines, accounts, navigation…"
            aria-label="Command palette search"
          />
          <span className="cs-kbd mono">esc</span>
        </div>

        <div className="cs-palette-results" role="listbox">
          {orderedFlat.length === 0 && (
            <div className="cs-palette-empty">No matches for &quot;{query}&quot;</div>
          )}
          {groups.map((group) => (
            <div className="cs-palette-group" key={group.key}>
              <div className="cs-palette-group-label">{group.label}</div>
              {group.items.map((command) => {
                runningIndex += 1
                const isActive = runningIndex === activeIndex
                return (
                  <button
                    key={command.id}
                    type="button"
                    role="option"
                    aria-selected={isActive}
                    className={isActive ? 'cs-palette-item active' : 'cs-palette-item'}
                    onMouseEnter={() => setActiveIndex(runningIndex)}
                    onClick={() => activate(command)}
                  >
                    <span className="cs-palette-item-label">{command.label}</span>
                    <span className="cs-palette-item-hint">{command.hint}</span>
                  </button>
                )
              })}
            </div>
          ))}
        </div>

        <div className="cs-palette-footer">
          <span><span className="cs-kbd mono">↑↓</span> navigate</span>
          <span><span className="cs-kbd mono">↵</span> open</span>
          <span><span className="cs-kbd mono">esc</span> close</span>
        </div>
      </div>
    </div>
  )
}
