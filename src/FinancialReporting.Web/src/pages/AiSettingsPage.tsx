import { useEffect, useState } from 'react'
import { fetchJson, putJson } from '../api/client'
import { Button, Card } from '../components/primitives'
import { Check } from 'lucide-react'

// TODO: dedupe — mirrors AiModel in App.tsx
type AiModel = {
  id: string
  displayName: string
  reasoningEfforts: string[]
  isDefault: boolean
}

// TODO: dedupe — mirrors AiSetting in App.tsx
type AiSetting = {
  id?: string
  module: string
  model: string
  reasoningEffort: string
  profile: string
  enabled: boolean
}

// Private constant — mirrors AI_SETTING_MODULES in App.tsx
const AI_SETTING_MODULES = ['slide-chat', 'narrative-rewrite', 'mapping-suggestions', 'flux-explain', 'final-review', 'export-qa']

export function AiSettings() {
  const [models, setModels] = useState<AiModel[]>([])
  const [settings, setSettings] = useState<AiSetting[]>([])

  useEffect(() => {
    fetchJson<AiModel[]>('/api/ai/models').then(setModels).catch(() => setModels([{ id: 'gpt-5.5', displayName: 'gpt-5.5', reasoningEfforts: ['low', 'medium', 'high', 'xhigh'], isDefault: true }]))
    fetchJson<AiSetting[]>('/api/settings/ai-runtime').then(setSettings).catch(() => setSettings(AI_SETTING_MODULES.map((module) => ({ module, model: 'gpt-5.5', reasoningEffort: 'high', profile: module, enabled: true }))))
  }, [])

  const normalized = AI_SETTING_MODULES.map((module) => settings.find((s) => s.module === module) ?? { module, model: models[0]?.id ?? 'gpt-5.5', reasoningEffort: 'high', profile: module, enabled: true })
  const update = (module: string, patch: Partial<AiSetting>) => setSettings(normalized.map((setting) => (setting.module === module ? { ...setting, ...patch } : setting)))
  const save = async () => putJson('/api/settings/ai-runtime', normalized)

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
