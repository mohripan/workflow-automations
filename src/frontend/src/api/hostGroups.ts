import client from './client'
import type {
  HostGroup, WorkflowHostInfo, RegistrationTokenInfo,
  CreateHostGroupRequest, DeleteHostGroupRequest, CreateHostRequest,
  GenerateTokenRequest, GenerateTokenResponse, AuditLogEntry,
} from '../types'

export const hostGroupsApi = {
  list: () => client.get<HostGroup[]>('/host-groups').then(r => r.data),
  get: (id: string) => client.get<HostGroup>(`/host-groups/${id}`).then(r => r.data),
  create: (data: CreateHostGroupRequest) => client.post<HostGroup>('/host-groups', data).then(r => r.data),
  delete: (id: string, data: DeleteHostGroupRequest) =>
    client.delete(`/host-groups/${id}`, { data }).then(r => r.data),
  getHosts: (id: string) => client.get<WorkflowHostInfo[]>(`/host-groups/${id}/hosts`).then(r => r.data),
  createHost: (groupId: string, data: CreateHostRequest) =>
    client.post<WorkflowHostInfo>(`/host-groups/${groupId}/hosts`, data).then(r => r.data),
  removeHost: (groupId: string, hostId: string) =>
    client.delete(`/host-groups/${groupId}/hosts/${hostId}`).then(r => r.data),
  getTokens: (id: string) =>
    client.get<RegistrationTokenInfo[]>(`/host-groups/${id}/tokens`).then(r => r.data),
  generateToken: (id: string, data?: GenerateTokenRequest) =>
    client.post<GenerateTokenResponse>(`/host-groups/${id}/registration-token`, data ?? {}).then(r => r.data),
  revokeToken: (groupId: string, tokenId: string) =>
    client.delete(`/host-groups/${groupId}/registration-token/${tokenId}`).then(r => r.data),
  getActivity: (id: string) =>
    client.get<AuditLogEntry[]>(`/host-groups/${id}/activity`).then(r => r.data),
}
