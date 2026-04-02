import { useState } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, X, ChevronDown } from 'lucide-react'
import { jobsApi } from '../api/jobs'
import { StatusBadge } from '../components/ui/StatusBadge'
import { JobStatusTimeline } from '../components/jobs/JobStatusTimeline'
import { CodeEditor } from '../components/ui/CodeEditor'
import { PageLoader, ErrorState } from '../components/ui/States'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { useJobSignalR } from '../hooks/useSignalR'
import { useAuth } from '../hooks/useAuth'
import { ACTIVE_STATUSES } from '../types'
import type { JobStatusUpdate } from '../types'
import { formatDistanceToNow } from 'date-fns'
import toast from 'react-hot-toast'

function Accordion({ title, children, defaultOpen = false }: {
  title: string; children: React.ReactNode; defaultOpen?: boolean
}) {
  const [open, setOpen] = useState(defaultOpen)
  return (
    <div className="card overflow-hidden">
      <button
        onClick={() => setOpen(o => !o)}
        className="w-full flex items-center justify-between px-4 py-3 hover:bg-slate-700/30 transition-colors"
      >
        <span className="text-sm font-semibold text-white">{title}</span>
        <ChevronDown size={16} className={`text-slate-400 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && <div className="border-t border-slate-700 p-4">{children}</div>}
    </div>
  )
}

export default function JobDetail() {
  const { connectionId, id } = useParams<{ connectionId: string; id: string }>()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const canCancel = hasRole('admin') || hasRole('operator')
  const [cancelConfirm, setCancelConfirm] = useState(false)

  const { data: job, isLoading, error } = useQuery({
    queryKey: ['job', connectionId, id],
    queryFn: () => jobsApi.get(connectionId!, id!),
    enabled: !!connectionId && !!id,
    refetchInterval: (q) =>
      q.state.data && ACTIVE_STATUSES.includes(q.state.data.status) ? 5000 : false,
  })

  // Real-time SignalR updates
  useJobSignalR(id!, (update) => {
    const u = update as JobStatusUpdate
    qc.setQueryData(['job', connectionId, id], (old: typeof job) =>
      old ? { ...old, status: u.status, message: u.message, outputJson: u.outputJson ?? old.outputJson } : old
    )
  })

  const cancelMutation = useMutation({
    mutationFn: () => jobsApi.cancel(connectionId!, id!),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['job', connectionId, id] }); toast.success('Cancellation requested'); setCancelConfirm(false) },
    onError: () => toast.error('Cancel failed'),
  })

  if (isLoading) return <PageLoader />
  if (error || !job) return <ErrorState message="Job not found" />

  const isActive = ACTIVE_STATUSES.includes(job.status)

  return (
    <div className="p-6 space-y-5 max-w-3xl">
      {/* Header */}
      <div className="flex items-start gap-4">
        <button onClick={() => navigate('/jobs')} className="btn-ghost mt-1">
          <ArrowLeft size={15} />
        </button>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-3 flex-wrap">
            <StatusBadge status={job.status} />
            {isActive && (
              <span className="flex items-center gap-1 text-xs text-blue-400">
                <span className="w-1.5 h-1.5 bg-blue-400 rounded-full animate-pulse" />
                Live updates
              </span>
            )}
          </div>
          <Link to={`/automations/${job.automationId}`} className="text-sm text-indigo-400 hover:text-indigo-300 mt-1 block">
            {job.automationName} →
          </Link>
        </div>
        {canCancel && isActive && (
          <button onClick={() => setCancelConfirm(true)} className="btn-danger flex-shrink-0">
            <X size={14} /> Cancel
          </button>
        )}
      </div>

      {/* Timeline */}
      <div className="card p-5">
        <JobStatusTimeline status={job.status} />
        {job.message && (
          <div className={`mt-4 p-3 rounded-lg text-sm ${job.status === 'Error' ? 'bg-red-500/10 text-red-300 border border-red-500/20' : 'bg-slate-700/50 text-slate-300'}`}>
            {job.message}
          </div>
        )}
      </div>

      {/* Meta */}
      <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
        {[
          { label: 'Job ID', value: <span className="text-xs text-slate-300 font-mono truncate">{job.id.slice(0, 8)}…</span> },
          { label: 'Host Group', value: <span className="text-xs text-slate-300 font-mono truncate">{job.hostGroupId.slice(0, 8)}…</span> },
          { label: 'Created', value: <span className="text-xs text-slate-300">{formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}</span> },
        ].map(({ label, value }) => (
          <div key={label} className="card p-3">
            <p className="text-xs text-slate-500 mb-1">{label}</p>
            {value}
          </div>
        ))}
      </div>

      {/* Output */}
      {job.outputJson && (
        <Accordion title="Output" defaultOpen>
          <CodeEditor
            value={JSON.stringify(JSON.parse(job.outputJson), null, 2)}
            readOnly height="200px"
          />
        </Accordion>
      )}

      <ConfirmDialog
        open={cancelConfirm}
        onClose={() => setCancelConfirm(false)}
        onConfirm={() => cancelMutation.mutate()}
        title="Cancel Job"
        message="Send a cancellation request for this job?"
        confirmLabel="Cancel Job"
        danger
        loading={cancelMutation.isPending}
      />
    </div>
  )
}
