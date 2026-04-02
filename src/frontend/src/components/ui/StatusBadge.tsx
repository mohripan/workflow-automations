import type { JobStatus } from '../../types'
import clsx from 'clsx'

const labels: Record<JobStatus, string> = {
  Pending: 'Pending', Started: 'Started', InProgress: 'In Progress',
  Completed: 'Completed', Error: 'Error',
  CompletedUnsuccessfully: 'Unsuccessful', Cancelled: 'Cancelled', Removed: 'Removed',
}

const styles: Record<JobStatus, string> = {
  Pending: 'bg-yellow-500/20 text-yellow-300 border border-yellow-500/30',
  Started: 'bg-blue-500/20 text-blue-300 border border-blue-500/30',
  InProgress: 'bg-blue-500/20 text-blue-300 border border-blue-500/30',
  Completed: 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30',
  Error: 'bg-red-500/20 text-red-300 border border-red-500/30',
  CompletedUnsuccessfully: 'bg-orange-500/20 text-orange-300 border border-orange-500/30',
  Cancelled: 'bg-slate-500/20 text-slate-400 border border-slate-500/30',
  Removed: 'bg-slate-600/20 text-slate-500 border border-slate-600/30',
}

export function StatusBadge({ status, className }: { status: JobStatus; className?: string }) {
  return (
    <span className={clsx('inline-flex items-center px-2 py-0.5 rounded-md text-xs font-medium', styles[status], className)}>
      {labels[status]}
    </span>
  )
}

const triggerColors: Record<string, string> = {
  schedule: 'bg-purple-500/20 text-purple-300 border border-purple-500/30',
  sql: 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/30',
  webhook: 'bg-orange-500/20 text-orange-300 border border-orange-500/30',
  'job-completed': 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30',
  'custom-script': 'bg-pink-500/20 text-pink-300 border border-pink-500/30',
}

export function TriggerTypeBadge({ typeId }: { typeId: string }) {
  return (
    <span className={clsx(
      'inline-flex items-center px-2 py-0.5 rounded-md text-xs font-medium',
      triggerColors[typeId] ?? 'bg-slate-500/20 text-slate-300 border border-slate-500/30',
    )}>
      {typeId}
    </span>
  )
}
