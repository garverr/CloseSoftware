import type { CSSProperties, MouseEventHandler, ReactNode } from 'react'

/**
 * Shared UI primitives extracted from App.tsx as the first step of the page-level
 * decomposition. Cat 30. New screens import from here so they don't recreate the same
 * Card/Button surface.
 */

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`card ${className}`}>{children}</div>
}

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'accent'

export function Button({
  children,
  icon,
  variant = 'secondary',
  disabled,
  onClick,
}: {
  children: ReactNode
  icon?: ReactNode
  variant?: ButtonVariant
  disabled?: boolean
  onClick?: MouseEventHandler<HTMLButtonElement>
}) {
  return (
    <button className={`button ${variant}`} disabled={disabled} onClick={onClick}>
      {icon}
      {children}
    </button>
  )
}

export function SegmentButton({
  active,
  children,
  onClick,
  icon,
}: {
  active: boolean
  children: ReactNode
  onClick: () => void
  icon?: ReactNode
}) {
  return (
    <button className={active ? 'seg active' : 'seg'} onClick={onClick}>
      {icon}
      {children}
    </button>
  )
}

export function RailItem({
  active,
  icon,
  badge,
  disabled,
  children,
  onClick,
}: {
  active?: boolean
  icon: ReactNode
  badge?: string
  disabled?: boolean
  children: ReactNode
  onClick: () => void
}) {
  return (
    <button className={active ? 'rail-item active' : 'rail-item'} disabled={disabled} onClick={onClick}>
      <span className="rail-icon">{icon}</span>
      <span>{children}</span>
      {badge && <span className="rail-dot" />}
    </button>
  )
}

export function SeverityBadge({ severity }: { severity: string }) {
  return <span className={`severity ${severity.toLowerCase()}`}>{severity}</span>
}

/** Minimal-data SVG sparkline. Used for KPI tiles, flux trend strips, and account snapshots. */
export function Sparkline({
  current,
  prior = [],
  style,
}: {
  current: number[]
  prior?: number[]
  style?: CSSProperties
}) {
  const values = [...current, ...prior].filter((x) => Number.isFinite(x))
  const min = Math.min(...values, 0)
  const max = Math.max(...values, 1)
  const points = current
    .map((value, index) => `${(index / Math.max(current.length - 1, 1)) * 130},${48 - ((value - min) / (max - min || 1)) * 44}`)
    .join(' ')
  const priorPoints = prior
    .map((value, index) => `${(index / Math.max(prior.length - 1, 1)) * 130},${48 - ((value - min) / (max - min || 1)) * 44}`)
    .join(' ')
  return (
    <svg className="sparkline" viewBox="0 0 132 52" role="img" aria-label="Trend" style={style}>
      {prior.length > 0 && (
        <polyline points={priorPoints} fill="none" stroke="currentColor" strokeDasharray="3 3" opacity=".35" strokeWidth="2" />
      )}
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="2.5" />
    </svg>
  )
}
