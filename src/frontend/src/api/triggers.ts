import client from './client'
import type { TriggerTypeSchema } from '../types'

export const triggersApi = {
  getTypes: () => client.get<TriggerTypeSchema[]>('/triggers/types').then(r => r.data),
  getType: (typeId: string) => client.get<TriggerTypeSchema>(`/triggers/types/${typeId}`).then(r => r.data),
  validateConfig: (typeId: string, configJson: string) =>
    client.post(`/triggers/types/${typeId}/validate-config`, { configJson }).then(r => r.data),
}
