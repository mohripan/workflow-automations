import client from './client'
import type { TaskTypeDescriptor } from '../types'

export const taskTypesApi = {
  list: () => client.get<TaskTypeDescriptor[]>('/task-types').then(r => r.data),
  get: (taskId: string) => client.get<TaskTypeDescriptor>(`/task-types/${taskId}`).then(r => r.data),
}
