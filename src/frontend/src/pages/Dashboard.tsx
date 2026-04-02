import { useQueries, useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { Zap, Briefcase, AlertCircle, Play, Plus } from 'lucide-react'
import { automationsApi } from '../api/automations'
import { jobsApi } from '../api/jobs'
import { hostGroupsApi } from '../api/hostGroups'
import { StatusBadge } from '../components/ui/StatusBadge'
import { PageLoader, ErrorState } from '../components/ui/States'
import { formatDistanceToNow } from 'date-fns'
import { ACTIVE_STATUSES } from '../types'
import type { HostGroup, JobResponse } from '../types'

function StatCard({ icon: Icon, label, value, color }: {
  icon: React.ElementType; label: string; value: number | string; color: string
}) {
  return (
    <div className="card p-5 flex items-center gap-4">
      <div className={`w-10 h-10 rounded-xl flex items-center justify-center ${color}`}>
        <Icon size={18} className="text-white" />
      </div>
      <div>
        <p className="text-2xl font-bold text-white">{value}</p>
        <p className="text-xs text-slate-400">{label}</p>
      </div>
    </div>
  )
}

export default function Dashboard() {
  const navigate = useNavigate()

  const { data: automations, isLoading: aLoading, error: aError } = useQuery({
    queryKey: ['automations'],
    queryFn: automationsApi.list,
    refetchInterval: 30_000,
  })

  const { data: hostGroups, isLoading: hgLoading } = useQuery({
    queryKey: ['host-groups'],
    queryFn: hostGroupsApi.list,
  })

  // Fetch jobs from every host group connection in parallel.
  const jobsQueries = useQueries({
    queries: (hostGroups ?? []).map((g: HostGroup) => ({
      queryKey: ['jobs', g.connectionId],
      queryFn: () => jobsApi.list(g.connectionId),
      refetchInterval: 10_000,
    })),
  })

  const isLoading = aLoading || hgLoading
  const allJobs: JobResponse[] = jobsQueries.flatMap(q => q.data ?? [])

  if (isLoading) return <PageLoader />
  if (aError) return <ErrorState message="Failed to load dashboard" />

  const connectionIdByHostGroup = Object.fromEntries(
    (hostGroups ?? []).map((g: HostGroup) => [g.id, g.connectionId])
  )

  const enabled = automations?.filter(a => a.isEnabled).length ?? 0
  const running = allJobs.filter(j => ACTIVE_STATUSES.includes(j.status)).length
  const errors = allJobs.filter(j => j.status === 'Error').length
  const recent = [...allJobs]
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
    .slice(0, 20)

  return (
    <div className="p-6 space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-bold text-white">Dashboard</h1>
          <p className="text-slate-400 text-sm mt-0.5">FlowForge workflow overview</p>
        </div>
        <button onClick={() => navigate('/automations/new')} className="btn-primary">
          <Plus size={15} /> New Automation
        </button>
      </div>

      <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <StatCard icon={Zap} label="Total Automations" value={automations?.length ?? 0} color="bg-indigo-600" />
        <StatCard icon={Play} label="Enabled" value={enabled} color="bg-emerald-600" />
        <StatCard icon={Briefcase} label="Running Jobs" value={running} color="bg-blue-600" />
        <StatCard icon={AlertCircle} label="Errored Jobs" value={errors} color="bg-red-600" />
      </div>

      <div className="card overflow-hidden">
        <div className="px-5 py-4 border-b border-slate-700 flex items-center justify-between">
          <h2 className="font-semibold text-white text-sm">Recent Jobs</h2>
          <button onClick={() => navigate('/jobs')} className="text-xs text-indigo-400 hover:text-indigo-300 transition-colors">
            View all →
          </button>
        </div>
        {recent.length === 0 ? (
          <p className="text-slate-500 text-sm text-center py-10">No jobs yet. Create an automation to get started.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-700">
                <th className="text-left px-5 py-2.5 text-xs text-slate-400 font-medium">Automation</th>
                <th className="text-left px-3 py-2.5 text-xs text-slate-400 font-medium">Status</th>
                <th className="text-left px-3 py-2.5 text-xs text-slate-400 font-medium hidden sm:table-cell">Created</th>
              </tr>
            </thead>
            <tbody>
              {recent.map(job => (
                <tr
                  key={job.id}
                  className="border-b border-slate-700/50 hover:bg-slate-700/30 cursor-pointer transition-colors"
                  onClick={() => {
                    const cid = connectionIdByHostGroup[job.hostGroupId]
                    if (cid) navigate(`/jobs/${cid}/${job.id}`)
                  }}
                >
                  <td className="px-5 py-2.5 text-slate-200">{job.automationName}</td>
                  <td className="px-3 py-2.5"><StatusBadge status={job.status} /></td>
                  <td className="px-3 py-2.5 text-slate-500 text-xs hidden sm:table-cell">
                    {formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
