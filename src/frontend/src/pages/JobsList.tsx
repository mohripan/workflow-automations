import { useState } from 'react'
import { useQueries, useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { Search, X } from 'lucide-react'
import { jobsApi } from '../api/jobs'
import { hostGroupsApi } from '../api/hostGroups'
import { StatusBadge } from '../components/ui/StatusBadge'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { useAuth } from '../hooks/useAuth'
import { formatDistanceToNow } from 'date-fns'
import { ACTIVE_STATUSES } from '../types'
import type { HostGroup, JobResponse } from '../types'
import toast from 'react-hot-toast'

const STATUS_FILTERS: { label: string; value: string }[] = [
  { label: 'All', value: '' },
  { label: 'Pending', value: 'Pending' },
  { label: 'Running', value: 'running' },
  { label: 'Completed', value: 'Completed' },
  { label: 'Error', value: 'Error' },
  { label: 'Cancelled', value: 'Cancelled' },
]

export default function JobsList() {
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const canCancel = hasRole('admin') || hasRole('operator')

  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [cancelTarget, setCancelTarget] = useState<{ connectionId: string; id: string } | null>(null)

  const { data: hostGroups, isLoading: hgLoading, error: hgError } = useQuery({
    queryKey: ['host-groups'],
    queryFn: hostGroupsApi.list,
  })

  const jobsQueries = useQueries({
    queries: (hostGroups ?? []).map((g: HostGroup) => ({
      queryKey: ['jobs', g.connectionId],
      queryFn: () => jobsApi.list(g.connectionId),
      refetchInterval: 10_000,
    })),
  })

  const connectionIdByHostGroup = Object.fromEntries(
    (hostGroups ?? []).map((g: HostGroup) => [g.id, g.connectionId])
  )

  const cancelMutation = useMutation({
    mutationFn: ({ connectionId, id }: { connectionId: string; id: string }) =>
      jobsApi.cancel(connectionId, id),
    onSuccess: (_, { connectionId }) => {
      qc.invalidateQueries({ queryKey: ['jobs', connectionId] })
      toast.success('Job cancelled')
      setCancelTarget(null)
    },
    onError: () => toast.error('Cancel failed'),
  })

  const isLoading = hgLoading || jobsQueries.some(q => q.isLoading)
  if (isLoading) return <PageLoader />
  if (hgError) return <ErrorState message="Failed to load jobs" />

  const allJobs: JobResponse[] = jobsQueries.flatMap(q => q.data ?? [])

  const filtered = allJobs
    .filter(j => {
      if (search && !j.automationName.toLowerCase().includes(search.toLowerCase())) return false
      if (!statusFilter) return true
      if (statusFilter === 'running') return ACTIVE_STATUSES.includes(j.status)
      return j.status === statusFilter
    })
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())

  return (
    <div className="p-6 space-y-5">
      <h1 className="text-xl font-bold text-white">Jobs</h1>

      <div className="flex flex-col sm:flex-row gap-3">
        <div className="relative flex-1">
          <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" />
          <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search by automation…" className="input pl-9" />
        </div>
        <div className="flex gap-1 flex-wrap">
          {STATUS_FILTERS.map(f => (
            <button
              key={f.value}
              onClick={() => setStatusFilter(f.value)}
              className={`text-xs px-3 py-1.5 rounded-lg font-medium transition-all ${
                statusFilter === f.value
                  ? 'bg-indigo-600 text-white'
                  : 'bg-slate-700 text-slate-400 hover:text-white'
              }`}
            >
              {f.label}
            </button>
          ))}
        </div>
      </div>

      {filtered.length === 0 ? (
        <EmptyState message="No jobs match your filters." />
      ) : (
        <div className="card overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-700">
                <th className="text-left px-5 py-3 text-xs text-slate-400 font-medium">Automation</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium">Status</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium hidden lg:table-cell">Created</th>
                <th className="px-3 py-3" />
              </tr>
            </thead>
            <tbody>
              {filtered.map(job => {
                const cid = connectionIdByHostGroup[job.hostGroupId] ?? ''
                return (
                  <tr
                    key={job.id}
                    className="border-b border-slate-700/50 hover:bg-slate-700/30 cursor-pointer transition-colors"
                    onClick={() => cid && navigate(`/jobs/${cid}/${job.id}`)}
                  >
                    <td className="px-5 py-3 text-slate-200 font-medium">{job.automationName}</td>
                    <td className="px-3 py-3"><StatusBadge status={job.status} /></td>
                    <td className="px-3 py-3 text-xs text-slate-500 hidden lg:table-cell">
                      {formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}
                    </td>
                    <td className="px-3 py-3" onClick={e => e.stopPropagation()}>
                      {canCancel && ACTIVE_STATUSES.includes(job.status) && cid && (
                        <button onClick={() => setCancelTarget({ connectionId: cid, id: job.id })} className="btn-ghost text-xs py-1">
                          <X size={13} /> Cancel
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={!!cancelTarget}
        onClose={() => setCancelTarget(null)}
        onConfirm={() => cancelMutation.mutate(cancelTarget!)}
        title="Cancel Job"
        message="Cancel this job? It will be stopped if currently running."
        confirmLabel="Cancel Job"
        danger
        loading={cancelMutation.isPending}
      />
    </div>
  )
}
