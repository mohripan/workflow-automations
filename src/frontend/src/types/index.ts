// All shared TypeScript interfaces matching backend API DTOs

export type JobStatus =
  | 'Pending' | 'Started' | 'InProgress'
  | 'Completed' | 'Error' | 'CompletedUnsuccessfully'
  | 'Cancelled' | 'Removed'

export const TERMINAL_STATUSES: JobStatus[] = [
  'Completed', 'Error', 'CompletedUnsuccessfully', 'Cancelled', 'Removed',
]
export const ACTIVE_STATUSES: JobStatus[] = ['Pending', 'Started', 'InProgress']

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
}

export interface TriggerConditionNode {
  operator: 'And' | 'Or' | null
  triggerName: string | null
  nodes: TriggerConditionNode[] | null
}

export interface TriggerResponse {
  id: string
  name: string
  typeId: string
  configJson: string
}

export interface AutomationResponse {
  id: string
  name: string
  description?: string
  taskId: string
  hostGroupId: string
  isEnabled: boolean
  timeoutSeconds?: number
  maxRetries: number
  taskConfig?: string
  triggers: TriggerResponse[]
  triggerCondition: TriggerConditionNode
  createdAt: string
  updatedAt: string
}

// Triggers and condition are inline — no separate trigger endpoints exist.
// Add/edit/delete triggers by sending the full trigger list in PUT /automations/{id}.
export interface CreateAutomationRequest {
  name: string
  description?: string
  hostGroupId: string
  taskId: string
  triggers: CreateTriggerRequest[]
  triggerCondition: TriggerConditionNode
  timeoutSeconds?: number
  maxRetries?: number
  taskConfig?: string
}

export type UpdateAutomationRequest = CreateAutomationRequest

export interface CreateTriggerRequest {
  name: string
  typeId: string
  configJson: string
}

export interface JobResponse {
  id: string
  automationId: string
  automationName: string
  hostGroupId: string
  hostId?: string
  status: JobStatus
  message?: string
  outputJson?: string
  createdAt: string
  updatedAt: string
}

export interface JobStatusUpdate {
  jobId: string
  status: JobStatus
  message?: string
  outputJson?: string
  updatedAt: string
}

export type ConfigFieldType =
  | 'String' | 'Int' | 'Bool' | 'CronExpression'
  | 'MultilineString' | 'Script' | 'Enum'

export interface ConfigField {
  name: string
  label: string
  dataType: ConfigFieldType
  required: boolean
  description?: string
  defaultValue?: string
  enumValues?: string[]
}

export interface TriggerTypeSchema {
  typeId: string
  displayName: string
  description?: string
  fields: ConfigField[]
}

export interface TaskParameterField {
  name: string
  label: string
  type: string
  required: boolean
  defaultValue?: string
  helpText?: string
}

export interface TaskTypeDescriptor {
  taskId: string
  displayName: string
  description?: string
  parameters: TaskParameterField[]
}

export interface HostGroup {
  id: string
  name: string
  connectionId: string
  hasRegistrationToken: boolean
  createdAt: string
  updatedAt: string
}

export interface WorkflowHostInfo {
  id: string
  name: string
  isOnline: boolean
  lastHeartbeat?: string
}

export interface DlqEntry {
  id: string
  sourceStream: string
  messageId: string
  error: string
  payload: string
  createdAt: string
}

export interface CreateHostGroupRequest {
  name: string
  connectionId: string
}

export interface CreateHostRequest {
  name: string
}

export interface GenerateTokenResponse {
  token: string
}

