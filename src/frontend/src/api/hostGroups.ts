import client from './client'
import type { HostGroup, WorkflowHostInfo, CreateHostGroupRequest, CreateHostRequest, GenerateTokenResponse } from '../types'

export const hostGroupsApi = {
  list: () => client.get<HostGroup[]>('/host-groups').then(r => r.data),
  get: (id: string) => client.get<HostGroup>(`/host-groups/${id}`).then(r => r.data),
  create: (data: CreateHostGroupRequest) => client.post<HostGroup>('/host-groups', data).then(r => r.data),
  getHosts: (id: string) => client.get<WorkflowHostInfo[]>(`/host-groups/${id}/hosts`).then(r => r.data),
  createHost: (groupId: string, data: CreateHostRequest) =>
    client.post<WorkflowHostInfo>(`/host-groups/${groupId}/hosts`, data).then(r => r.data),
  removeHost: (groupId: string, hostId: string) =>
    client.delete(`/host-groups/${groupId}/hosts/${hostId}`).then(r => r.data),
  generateToken: (id: string) =>
    client.post<GenerateTokenResponse>(`/host-groups/${id}/registration-token`).then(r => r.data),
  revokeToken: (id: string) =>
    client.delete(`/host-groups/${id}/registration-token`).then(r => r.data),
}
