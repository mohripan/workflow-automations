import client from './client'
import type { JobResponse, PagedResult } from '../types'

export const jobsApi = {
  list: (connectionId: string, params?: { automationId?: string; status?: string }) =>
    client.get<PagedResult<JobResponse>>(`/${connectionId}/jobs`, { params }).then(r => r.data.items),
  get: (connectionId: string, id: string) =>
    client.get<JobResponse>(`/${connectionId}/jobs/${id}`).then(r => r.data),
  cancel: (connectionId: string, id: string) =>
    client.post(`/${connectionId}/jobs/${id}/cancel`),
  delete: (connectionId: string, id: string) =>
    client.delete(`/${connectionId}/jobs/${id}`),
}

