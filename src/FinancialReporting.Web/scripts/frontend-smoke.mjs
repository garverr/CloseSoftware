import { readFile } from 'node:fs/promises'

const frontendUrl = process.env.FRONTEND_URL ?? 'http://127.0.0.1:5173/'
const apiUrl = process.env.API_URL ?? 'http://localhost:5264'

const checks = []

function ok(condition, label, detail = '') {
  if (!condition) {
    throw new Error(`${label}${detail ? `: ${detail}` : ''}`)
  }

  checks.push(label)
}

async function readJson(url) {
  const response = await fetch(url, { headers: { 'x-fr-role': 'Admin', 'x-fr-user': 'smoke-test' } })
  ok(response.ok, `GET ${url}`, `${response.status} ${response.statusText}`)
  return response.json()
}

async function readText(url) {
  const response = await fetch(url)
  ok(response.ok, `GET ${url}`, `${response.status} ${response.statusText}`)
  return response.text()
}

const html = await readText(frontendUrl)
ok(html.includes('id="root"'), 'frontend shell exposes React root')

const appSource = await readFile(new URL('../src/App.tsx', import.meta.url), 'utf8')
const requiredUiWiring = [
  '/api/packages',
  '/api/packages/${selectedPackage.id}/final-review',
  '/api/slides/${slide.id}/reorder-blocks',
  '/api/mapping/accounts',
  '/api/mapping/accounts/${accountId}/split',
  '/api/mapping/accounts/${accountId}/eliminate',
  '/api/mapping/fs-lines',
  '/api/kpis',
  '/api/packages/${packageData.id}/theme',
  '/api/exports/pdf',
  '/api/exports/excel',
  '/api/share-links',
  '/api/settings/ai-runtime',
  '/api/packages/${packageData.id}/versions',
  '/api/xero/status',
  '/api/xero/connect',
  '/api/xero/import-v2-tokens',
  '/api/xero/backfill/preview',
  '/api/xero/backfill',
  '/api/xero/connections/${connectionId}/reconnect',
]

for (const marker of requiredUiWiring) {
  ok(appSource.includes(marker), `frontend wired to ${marker}`)
}

const health = await readJson(`${apiUrl}/api/health`)
ok(health.database === 'SQLite', 'API health reports SQLite')

const packages = await readJson(`${apiUrl}/api/packages`)
ok(Array.isArray(packages) && packages.length > 0, 'packages load')
const currentPackage = packages[0]
ok(Array.isArray(currentPackage.slides) && currentPackage.slides.length > 0, 'package slides load')
ok(currentPackage.slides.some((slide) => Array.isArray(slide.blocks) && slide.blocks.length > 0), 'slide blocks load')
ok(Array.isArray(currentPackage.issues), 'package issues load')

const accounts = await readJson(`${apiUrl}/api/mapping/accounts`)
ok(Array.isArray(accounts), 'mapping accounts load')
ok(accounts.length === 0 || accounts.every((account) => 'isFirstSeen' in account), 'mapping accounts expose first-seen flag')

const context = await readJson(`${apiUrl}/api/reporting-context`)
ok(Array.isArray(context.organizations) && context.organizations.length > 0, 'reporting context organizations load')
const orgKey = context.organizations[0].key
const fsLines = await readJson(`${apiUrl}/api/mapping/fs-lines?organizationKey=${encodeURIComponent(orgKey)}`)
ok(Array.isArray(fsLines), 'FS line library loads')
ok(fsLines.every((line) => line.isActive), 'FS line library defaults to active definitions')

const kpis = await readJson(`${apiUrl}/api/kpis`)
ok(Array.isArray(kpis), 'KPI library loads')

const models = await readJson(`${apiUrl}/api/ai/models`)
ok(Array.isArray(models) && models.length > 0, 'AI models load')

const xero = await readJson(`${apiUrl}/api/xero/status`)
ok(typeof xero.clientConfigured === 'boolean', 'Xero status loads')
ok(Array.isArray(xero.connections), 'Xero connections expose tenant list')

const versions = await readJson(`${apiUrl}/api/packages/${currentPackage.id}/versions`)
ok(Array.isArray(versions), 'package versions load')

console.log(`Frontend smoke passed (${checks.length} checks).`)
