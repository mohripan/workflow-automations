import client from './client'
import type { HostGroup, WorkflowHostInfo, CreateHostGroupRequest } from '../types'

export const hostGroupsApi = {
  list: () => client.get<HostGroup[]>('/host-groups').then(r => r.data),
  create: (data: CreateHostGroupRequest) => client.post<HostGroup>('/host-groups', data).then(r => r.data),
  getHosts: (id: string) => client.get<WorkflowHostInfo[]>(`/host-groups/${id}/hosts`).then(r => r.data),
}
