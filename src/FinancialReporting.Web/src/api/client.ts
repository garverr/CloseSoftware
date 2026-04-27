/**
 * Typed fetch helpers for talking to FinancialReporting.Api. Cat 39.
 *
 * Single source of truth for the API base URL, the JSON content type, and the
 * (eventual) JWT bearer attachment. Future feature pages should import these
 * helpers rather than rolling their own fetches.
 *
 * Auth: if `localStorage["fr.token"]` is set, it's sent as `Authorization: Bearer …`.
 * The dev-token endpoint at `/api/auth/dev-token` returns a token in the right shape.
 */

export const API_BASE: string = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? ''

const TOKEN_STORAGE_KEY = 'fr.token'

function buildHeaders(extra?: Record<string, string>): HeadersInit {
  const headers: Record<string, string> = { ...(extra ?? {}) }
  try {
    const token = typeof localStorage !== 'undefined' ? localStorage.getItem(TOKEN_STORAGE_KEY) : null
    if (token) {
      headers.Authorization = `Bearer ${token}`
    }
  } catch {
    // localStorage can throw in private mode / ITP contexts; ignore.
  }
  return headers
}

async function parseError(path: string, method: string, response: Response): Promise<never> {
  let body: unknown
  try {
    body = await response.json()
  } catch {
    body = await response.text().catch(() => '')
  }
  const detail = (body && typeof body === 'object' && 'detail' in body && typeof (body as { detail: unknown }).detail === 'string')
    ? (body as { detail: string }).detail
    : `${method} ${path} failed (${response.status})`
  throw new ApiError(method, path, response.status, detail, body)
}

export class ApiError extends Error {
  readonly method: string
  readonly path: string
  readonly status: number
  readonly body: unknown
  constructor(method: string, path: string, status: number, message: string, body: unknown) {
    super(message)
    this.name = 'ApiError'
    this.method = method
    this.path = path
    this.status = status
    this.body = body
  }
}

export async function fetchJson<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, { headers: buildHeaders() })
  if (!response.ok) await parseError(path, 'GET', response)
  return response.json() as Promise<T>
}

export async function postJson<T = unknown>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    method: 'POST',
    headers: buildHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(body),
  })
  if (!response.ok) await parseError(path, 'POST', response)
  return response.status === 204 ? ({} as T) : ((await response.json()) as T)
}

export async function putJson<T = unknown>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    method: 'PUT',
    headers: buildHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(body),
  })
  if (!response.ok) await parseError(path, 'PUT', response)
  return response.status === 204 ? ({} as T) : ((await response.json()) as T)
}

export async function deleteJson<T = unknown>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, { method: 'DELETE', headers: buildHeaders() })
  if (!response.ok) await parseError(path, 'DELETE', response)
  return response.status === 204 ? ({} as T) : ((await response.json()) as T)
}

export function setBearerToken(token: string | null): void {
  try {
    if (token) {
      localStorage.setItem(TOKEN_STORAGE_KEY, token)
    } else {
      localStorage.removeItem(TOKEN_STORAGE_KEY)
    }
  } catch {
    // ignore
  }
}

export function getBearerToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_STORAGE_KEY)
  } catch {
    return null
  }
}
