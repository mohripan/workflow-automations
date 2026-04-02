import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { Plus, Search, ToggleLeft, ToggleRight } from 'lucide-react'
import { automationsApi } from '../api/automations'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'
import { TriggerTypeBadge } from '../components/ui/StatusBadge'
import { formatDistanceToNow } from 'date-fns'
import { useAuth } from '../hooks/useAuth'
import toast from 'react-hot-toast'

export default function AutomationsList() {
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const canWrite = hasRole('admin') || hasRole('operator')

  const [search, setSearch] = useState('')

  const { data, isLoading, error } = useQuery({
    queryKey: ['automations'],
    queryFn: automationsApi.list,
    refetchInterval: 30_000,
  })

  const toggleMutation = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      enabled ? automationsApi.disable(id) : automationsApi.enable(id),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['automations'] }); toast.success('Updated') },
    onError: () => toast.error('Failed to update'),
  })

  if (isLoading) return <PageLoader />
  if (error) return <ErrorState message="Failed to load automations" />

  const filtered = (data ?? []).filter(a =>
    a.name.toLowerCase().includes(search.toLowerCase())
  )

  return (
    <div className="p-6 space-y-5">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-xl font-bold text-white">Automations</h1>
        {canWrite && (
          <button onClick={() => navigate('/automations/new')} className="btn-primary">
            <Plus size={15} /> New
          </button>
        )}
      </div>

      <div className="relative">
        <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" />
        <input
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="Search automations…"
          className="input pl-9"
        />
      </div>

      {filtered.length === 0 ? (
        <EmptyState message={search ? 'No automations match your search.' : 'No automations yet. Create one to get started.'} />
      ) : (
        <div className="card overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-700">
                <th className="text-left px-5 py-3 text-xs text-slate-400 font-medium">Name</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium">Status</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium hidden md:table-cell">Task</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium hidden lg:table-cell">Triggers</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium hidden lg:table-cell">Updated</th>
                <th className="px-3 py-3" />
              </tr>
            </thead>
            <tbody>
              {filtered.map(auto => (
                <tr
                  key={auto.id}
                  className="border-b border-slate-700/50 hover:bg-slate-700/30 cursor-pointer transition-colors"
                  onClick={() => navigate(`/automations/${auto.id}`)}
                >
                  <td className="px-5 py-3">
                    <div className="flex items-center gap-2">
                      <div className={`w-2 h-2 rounded-full flex-shrink-0 ${auto.isEnabled ? 'bg-emerald-400' : 'bg-slate-600'}`} />
                      <span className="text-slate-100 font-medium">{auto.name}</span>
                    </div>
                    {auto.description && <p className="text-xs text-slate-500 ml-4 mt-0.5">{auto.description}</p>}
                  </td>
                  <td className="px-3 py-3">
                    <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${auto.isEnabled ? 'text-emerald-400' : 'text-slate-500'}`}>
                      {auto.isEnabled ? 'Enabled' : 'Disabled'}
                    </span>
                  </td>
                  <td className="px-3 py-3 hidden md:table-cell">
                    <code className="text-xs text-indigo-400 bg-indigo-500/10 px-1.5 py-0.5 rounded">{auto.taskId}</code>
                  </td>
                  <td className="px-3 py-3 hidden lg:table-cell">
                    <div className="flex flex-wrap gap-1">
                      {auto.triggers.slice(0, 3).map(t => (
                        <TriggerTypeBadge key={t.id} typeId={t.typeId} />
                      ))}
                      {auto.triggers.length > 3 && (
                        <span className="text-xs text-slate-500">+{auto.triggers.length - 3}</span>
                      )}
                    </div>
                  </td>
                  <td className="px-3 py-3 text-slate-500 text-xs hidden lg:table-cell">
                    {formatDistanceToNow(new Date(auto.updatedAt), { addSuffix: true })}
                  </td>
                  <td className="px-3 py-3" onClick={e => e.stopPropagation()}>
                    {canWrite && (
                      <button
                        onClick={() => toggleMutation.mutate({ id: auto.id, enabled: auto.isEnabled })}
                        className="text-slate-400 hover:text-white transition-colors"
                        title={auto.isEnabled ? 'Disable' : 'Enable'}
                      >
                        {auto.isEnabled ? <ToggleRight size={20} className="text-emerald-400" /> : <ToggleLeft size={20} />}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
