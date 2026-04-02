import client from './client'
import type {
  AutomationResponse, CreateAutomationRequest, UpdateAutomationRequest,
  CreateTriggerRequest, PagedResult,
} from '../types'

// Build an UpdateAutomationRequest from an existing response — preserves all fields.
function toUpdateRequest(auto: AutomationResponse): UpdateAutomationRequest {
  return {
    name: auto.name,
    description: auto.description,
    hostGroupId: auto.hostGroupId,
    taskId: auto.taskId,
    triggers: auto.triggers.map(t => ({ name: t.name, typeId: t.typeId, configJson: t.configJson })),
    triggerCondition: auto.triggerCondition,
    timeoutSeconds: auto.timeoutSeconds,
    maxRetries: auto.maxRetries,
    taskConfig: auto.taskConfig,
  }
}

export const automationsApi = {
  list: () => client.get<PagedResult<AutomationResponse>>('/automations').then(r => r.data.items),
  get: (id: string) => client.get<AutomationResponse>(`/automations/${id}`).then(r => r.data),
  create: (data: CreateAutomationRequest) => client.post<AutomationResponse>('/automations', data).then(r => r.data),
  update: (id: string, data: UpdateAutomationRequest) => client.put<AutomationResponse>(`/automations/${id}`, data).then(r => r.data),
  delete: (id: string) => client.delete(`/automations/${id}`),
  enable: (id: string) => client.put(`/automations/${id}/enable`),
  disable: (id: string) => client.put(`/automations/${id}/disable`),

  // Trigger CRUD — no dedicated endpoints; implemented via full automation PUT.
  addTrigger: async (automationId: string, trigger: CreateTriggerRequest) => {
    const auto = await automationsApi.get(automationId)
    return automationsApi.update(automationId, {
      ...toUpdateRequest(auto),
      triggers: [...auto.triggers.map(t => ({ name: t.name, typeId: t.typeId, configJson: t.configJson })), trigger],
    })
  },
  updateTrigger: async (automationId: string, triggerId: string, trigger: CreateTriggerRequest) => {
    const auto = await automationsApi.get(automationId)
    return automationsApi.update(automationId, {
      ...toUpdateRequest(auto),
      triggers: auto.triggers.map(t =>
        t.id === triggerId ? trigger : { name: t.name, typeId: t.typeId, configJson: t.configJson }
      ),
    })
  },
  deleteTrigger: async (automationId: string, triggerId: string) => {
    const auto = await automationsApi.get(automationId)
    return automationsApi.update(automationId, {
      ...toUpdateRequest(auto),
      triggers: auto.triggers
        .filter(t => t.id !== triggerId)
        .map(t => ({ name: t.name, typeId: t.typeId, configJson: t.configJson })),
    })
  },

  // Webhook: POST /automations/{id}/webhook with optional HMAC signature header.
  fireWebhook: (id: string, signature?: string) =>
    client.post(`/automations/${id}/webhook`, {}, {
      headers: signature ? { 'X-FlowForge-Signature': signature } : {},
    }),
}

