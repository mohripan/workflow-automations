import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Plus, Server, ChevronDown, ChevronRight, Circle } from 'lucide-react'
import { hostGroupsApi } from '../api/hostGroups'
import { Modal } from '../components/ui/Modal'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'
import { useAuth } from '../hooks/useAuth'
import toast from 'react-hot-toast'

function HostGroupCard({ group }: { group: { id: string; name: string; connectionId: string } }) {
  const [open, setOpen] = useState(false)
  const { data: hosts, isLoading } = useQuery({
    queryKey: ['hosts', group.id],
    queryFn: () => hostGroupsApi.getHosts(group.id),
    enabled: open,
  })

  return (
    <div className="card overflow-hidden">
      <button
        onClick={() => setOpen(o => !o)}
        className="w-full flex items-center gap-4 px-5 py-4 hover:bg-slate-700/30 transition-colors"
      >
        <div className="w-9 h-9 bg-slate-700 rounded-lg flex items-center justify-center flex-shrink-0">
          <Server size={16} className="text-indigo-400" />
        </div>
        <div className="flex-1 text-left min-w-0">
          <p className="text-sm font-semibold text-white">{group.name}</p>
          <p className="text-xs text-slate-500 font-mono truncate">{group.connectionId}</p>
        </div>
        {open ? <ChevronDown size={16} className="text-slate-500" /> : <ChevronRight size={16} className="text-slate-500" />}
      </button>

      {open && (
        <div className="border-t border-slate-700 divide-y divide-slate-700/50">
          {isLoading && <p className="text-slate-500 text-sm px-5 py-3">Loading hosts…</p>}
          {!isLoading && (!hosts || hosts.length === 0) && (
            <p className="text-slate-500 text-sm px-5 py-3">No hosts in this group.</p>
          )}
          {hosts?.map(host => (
            <div key={host.id} className="flex items-center gap-3 px-5 py-3">
              <Circle
                size={8}
                className={`flex-shrink-0 fill-current ${host.isOnline ? 'text-emerald-400' : 'text-slate-600'}`}
              />
              <div>
                <p className="text-sm text-slate-200">{host.name}</p>
                {host.lastHeartbeat && (
                  <p className="text-xs text-slate-500">
                    Last seen: {new Date(host.lastHeartbeat).toLocaleTimeString()}
                  </p>
                )}
              </div>
              <span className={`ml-auto text-xs px-2 py-0.5 rounded-full ${host.isOnline ? 'text-emerald-400 bg-emerald-500/10' : 'text-slate-500 bg-slate-700/30'}`}>
                {host.isOnline ? 'Online' : 'Offline'}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export default function HostGroupsPage() {
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const isAdmin = hasRole('admin')
  const [showCreate, setShowCreate] = useState(false)
  const [newName, setNewName] = useState('')
  const [newConnId, setNewConnId] = useState('')

  const { data, isLoading, error } = useQuery({
    queryKey: ['host-groups'],
    queryFn: hostGroupsApi.list,
    refetchInterval: 30_000,
  })

  const createMutation = useMutation({
    mutationFn: () => hostGroupsApi.create({ name: newName, connectionId: newConnId }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['host-groups'] })
      toast.success('Host group created')
      setShowCreate(false)
      setNewName('')
      setNewConnId('')
    },
    onError: () => toast.error('Failed to create host group'),
  })

  if (isLoading) return <PageLoader />
  if (error) return <ErrorState message="Failed to load host groups" />

  return (
    <div className="p-6 space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-bold text-white">Host Groups</h1>
          <p className="text-slate-400 text-sm mt-0.5">Pools of WorkflowHost instances</p>
        </div>
        {isAdmin && (
          <button onClick={() => setShowCreate(true)} className="btn-primary">
            <Plus size={15} /> New Group
          </button>
        )}
      </div>

      {(!data || data.length === 0) ? (
        <EmptyState message="No host groups configured." />
      ) : (
        <div className="space-y-3">
          {data.map(g => <HostGroupCard key={g.id} group={g} />)}
        </div>
      )}

      <Modal open={showCreate} onClose={() => setShowCreate(false)} title="Create Host Group" size="sm">
        <div className="space-y-4">
          <div>
            <label className="label">Name <span className="text-red-400">*</span></label>
            <input value={newName} onChange={e => setNewName(e.target.value)} className="input" placeholder="e.g. production" />
          </div>
          <div>
            <label className="label">Connection ID <span className="text-red-400">*</span></label>
            <input value={newConnId} onChange={e => setNewConnId(e.target.value)} className="input" placeholder="e.g. wf-jobs-minion" />
            <p className="text-xs text-slate-500 mt-1">Must match a key in <code>JobConnections</code> config.</p>
          </div>
          <div className="flex justify-end gap-2">
            <button onClick={() => setShowCreate(false)} className="btn-secondary">Cancel</button>
            <button
              onClick={() => createMutation.mutate()}
              disabled={!newName || !newConnId || createMutation.isPending}
              className="btn-primary"
            >
              {createMutation.isPending ? 'Creating…' : 'Create'}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  )
}
