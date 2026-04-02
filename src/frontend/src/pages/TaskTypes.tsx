import { useQuery } from '@tanstack/react-query'
import { taskTypesApi } from '../api/taskTypes'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'

export default function TaskTypesPage() {
  const { data, isLoading, error } = useQuery({
    queryKey: ['task-types'],
    queryFn: taskTypesApi.list,
  })

  if (isLoading) return <PageLoader />
  if (error) return <ErrorState message="Failed to load task types" />

  return (
    <div className="p-6 space-y-5">
      <div>
        <h1 className="text-xl font-bold text-white">Task Types</h1>
        <p className="text-slate-400 text-sm mt-0.5">Available workflow task handlers registered in the system</p>
      </div>

      {(!data || data.length === 0) ? (
        <EmptyState message="No task types registered." />
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          {data.map(task => (
            <div key={task.taskId} className="card p-5 space-y-4">
              <div>
                <div className="flex items-center gap-3">
                  <code className="text-sm font-bold text-indigo-400 bg-indigo-500/10 px-2 py-0.5 rounded">
                    {task.taskId}
                  </code>
                </div>
                <p className="text-base font-semibold text-white mt-1">{task.displayName}</p>
                {task.description && <p className="text-xs text-slate-400 mt-1">{task.description}</p>}
              </div>

              {task.parameters.length > 0 && (
                <div>
                  <p className="text-xs font-medium text-slate-400 mb-2">Parameters</p>
                  <div className="space-y-1.5">
                    {task.parameters.map(p => (
                      <div key={p.name} className="flex items-start gap-2">
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center gap-2">
                            <code className="text-xs text-slate-300 font-mono">{p.name}</code>
                            <span className={`text-xs px-1.5 py-0.5 rounded ${
                              p.required
                                ? 'bg-red-500/15 text-red-400'
                                : 'bg-slate-700 text-slate-500'
                            }`}>
                              {p.required ? 'required' : 'optional'}
                            </span>
                            <span className="text-xs text-slate-600">{p.type}</span>
                          </div>
                          {p.helpText && <p className="text-xs text-slate-500 mt-0.5">{p.helpText}</p>}
                          {p.defaultValue && (
                            <p className="text-xs text-slate-600 mt-0.5">default: <code>{p.defaultValue}</code></p>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
