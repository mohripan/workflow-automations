import type { JobStatus } from '../../types'
import { ACTIVE_STATUSES } from '../../types'

const steps: { key: JobStatus; label: string }[] = [
  { key: 'Pending', label: 'Pending' },
  { key: 'Started', label: 'Started' },
  { key: 'InProgress', label: 'In Progress' },
  { key: 'Completed', label: 'Completed' },
]

const terminalColors: Partial<Record<JobStatus, string>> = {
  Completed: 'bg-emerald-500',
  Error: 'bg-red-500',
  CompletedUnsuccessfully: 'bg-orange-500',
  Cancelled: 'bg-slate-500',
}

export function JobStatusTimeline({ status }: { status: JobStatus }) {
  const isActive = ACTIVE_STATUSES.includes(status)
  const terminalLabel: Partial<Record<JobStatus, string>> = {
    Error: 'Error', CompletedUnsuccessfully: 'Unsuccessful', Cancelled: 'Cancelled',
  }
  const finalStep = terminalLabel[status]
    ? { key: status, label: terminalLabel[status]! }
    : steps[3]

  const displaySteps = [...steps.slice(0, 3), finalStep]

  const activeIdx = isActive
    ? displaySteps.findIndex(s => s.key === status)
    : displaySteps.length - 1

  return (
    <div className="flex items-center gap-0">
      {displaySteps.map((step, idx) => {
        const done = idx <= activeIdx
        const isCurrent = idx === activeIdx
        const color = terminalColors[step.key as JobStatus] ?? 'bg-indigo-500'

        return (
          <div key={step.key} className="flex items-center flex-1">
            <div className="flex flex-col items-center gap-1">
              <div className={`w-3 h-3 rounded-full border-2 transition-all ${
                done
                  ? `${color} border-transparent`
                  : 'bg-slate-800 border-slate-600'
              } ${isCurrent && isActive ? 'ring-2 ring-indigo-400 ring-offset-1 ring-offset-slate-800 animate-pulse' : ''}`} />
              <span className={`text-xs whitespace-nowrap ${done ? 'text-slate-200' : 'text-slate-600'}`}>
                {step.label}
              </span>
            </div>
            {idx < displaySteps.length - 1 && (
              <div className={`flex-1 h-0.5 mx-1 mb-5 ${idx < activeIdx ? (terminalColors[status] ?? 'bg-indigo-500') : 'bg-slate-700'}`} />
            )}
          </div>
        )
      })}
    </div>
  )
}
