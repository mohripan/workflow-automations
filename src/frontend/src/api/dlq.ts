import client from './client'
import type { DlqEntry } from '../types'

export const dlqApi = {
  list: (limit = 50) => client.get<DlqEntry[]>('/dlq', { params: { limit } }).then(r => r.data),
  delete: (id: string) => client.delete(`/dlq/${id}`),
  replay: (id: string) => client.post(`/dlq/${id}/replay`),
}
