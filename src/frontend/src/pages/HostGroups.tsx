import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Plus, Server, ChevronDown, ChevronRight, Circle, Key, Trash2, Copy, Terminal, Monitor } from 'lucide-react'
import { hostGroupsApi } from '../api/hostGroups'
import { Modal } from '../components/ui/Modal'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'
import { useAuth } from '../hooks/useAuth'
import toast from 'react-hot-toast'
import type { HostGroup } from '../types'

function SetupInstructions({ token, connectionId }: { token: string; connectionId: string }) {
  const copyCmd = (cmd: string) => { navigator.clipboard.writeText(cmd); toast.success('Copied to clipboard') }

  const dockerCmd = `docker run -d \\
  -e NODE_NAME="my-host" \\
  -e REGISTRATION_TOKEN="${token}" \\
  -e "ConnectionStrings__DefaultConnection=Host=<PLATFORM_DB_HOST>;Database=flowforge_platform;Username=postgres;Password=postgres" \\
  -e "JobConnections__${connectionId}__ConnectionString=Host=<JOB_DB_HOST>;Database=<JOB_DB_NAME>;Username=postgres;Password=postgres" \\
  -e "JobConnections__${connectionId}__Provider=PostgreSQL" \\
  -e "Redis__ConnectionString=<REDIS_HOST>:6379" \\
  flowforge-workflowhost:latest`

  const systemdCmd = `# Install .NET 10 runtime, then:
export NODE_NAME="my-host"
export REGISTRATION_TOKEN="${token}"
dotnet FlowForge.WorkflowHost.dll`

  return (
    <div className="space-y-4 mt-3">
      <div>
        <div className="flex items-center gap-2 mb-1.5">
          <Terminal size={14} className="text-indigo-400" />
          <span className="text-xs font-semibold text-slate-300 uppercase tracking-wide">Docker</span>
        </div>
        <div className="relative group">
          <pre className="text-xs bg-slate-950 text-slate-300 p-3 rounded-lg overflow-x-auto border border-slate-700">{dockerCmd}</pre>
          <button onClick={() => copyCmd(dockerCmd)} className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-400 hover:text-white">
            <Copy size={14} />
          </button>
        </div>
      </div>
      <div>
        <div className="flex items-center gap-2 mb-1.5">
          <Monitor size={14} className="text-indigo-400" />
          <span className="text-xs font-semibold text-slate-300 uppercase tracking-wide">Linux / Windows</span>
        </div>
        <div className="relative group">
          <pre className="text-xs bg-slate-950 text-slate-300 p-3 rounded-lg overflow-x-auto border border-slate-700">{systemdCmd}</pre>
          <button onClick={() => copyCmd(systemdCmd)} className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-400 hover:text-white">
            <Copy size={14} />
          </button>
        </div>
      </div>
      <p className="text-xs text-slate-500">
        Replace <code>&lt;PLATFORM_DB_HOST&gt;</code>, <code>&lt;JOB_DB_HOST&gt;</code>, and <code>&lt;REDIS_HOST&gt;</code> with your infrastructure addresses.
        See <code>docs/HOSTGROUPS.md</code> for full deployment guide.
      </p>
    </div>
  )
}

function HostGroupCard({ group, isAdmin }: { group: HostGroup; isAdmin: boolean }) {
  const qc = useQueryClient()
  const [open, setOpen] = useState(false)
  const [showToken, setShowToken] = useState(false)
  const [token, setToken] = useState<string | null>(null)
  const [showAddHost, setShowAddHost] = useState(false)
  const [newHostName, setNewHostName] = useState('')

  const { data: hosts, isLoading } = useQuery({
    queryKey: ['hosts', group.id],
    queryFn: () => hostGroupsApi.getHosts(group.id),
    enabled: open,
    refetchInterval: open ? 15_000 : false,
  })

  const genTokenMut = useMutation({
    mutationFn: () => hostGroupsApi.generateToken(group.id),
    onSuccess: (data) => {
      setToken(data.token)
      setShowToken(true)
      qc.invalidateQueries({ queryKey: ['host-groups'] })
    },
    onError: () => toast.error('Failed to generate token'),
  })

  const revokeTokenMut = useMutation({
    mutationFn: () => hostGroupsApi.revokeToken(group.id),
    onSuccess: () => {
      toast.success('Registration token revoked')
      setToken(null)
      qc.invalidateQueries({ queryKey: ['host-groups'] })
    },
    onError: () => toast.error('Failed to revoke token'),
  })

  const addHostMut = useMutation({
    mutationFn: () => hostGroupsApi.createHost(group.id, { name: newHostName }),
    onSuccess: () => {
      toast.success('Host added')
      setShowAddHost(false)
      setNewHostName('')
      qc.invalidateQueries({ queryKey: ['hosts', group.id] })
    },
    onError: () => toast.error('Failed to add host (name may already exist)'),
  })

  const removeHostMut = useMutation({
    mutationFn: (hostId: string) => hostGroupsApi.removeHost(group.id, hostId),
    onSuccess: () => {
      toast.success('Host removed')
      qc.invalidateQueries({ queryKey: ['hosts', group.id] })
    },
    onError: () => toast.error('Failed to remove host'),
  })

  const hostCount = hosts?.length ?? 0
  const onlineCount = hosts?.filter(h => h.isOnline).length ?? 0

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
        {open && hosts && (
          <span className="text-xs text-slate-400 mr-2">
            {onlineCount}/{hostCount} online
          </span>
        )}
        {group.hasRegistrationToken && (
          <span title="Has registration token">
            <Key size={13} className="text-amber-400 flex-shrink-0" />
          </span>
        )}
        {open ? <ChevronDown size={16} className="text-slate-500" /> : <ChevronRight size={16} className="text-slate-500" />}
      </button>

      {open && (
        <div className="border-t border-slate-700">
          {/* Host list */}
          <div className="divide-y divide-slate-700/50">
            {isLoading && <p className="text-slate-500 text-sm px-5 py-3">Loading hosts…</p>}
            {!isLoading && hostCount === 0 && (
              <p className="text-slate-500 text-sm px-5 py-3">No hosts in this group. Generate a registration token and deploy an agent.</p>
            )}
            {hosts?.map(host => (
              <div key={host.id} className="flex items-center gap-3 px-5 py-3">
                <Circle
                  size={8}
                  className={`flex-shrink-0 fill-current ${host.isOnline ? 'text-emerald-400' : 'text-slate-600'}`}
                />
                <div className="flex-1 min-w-0">
                  <p className="text-sm text-slate-200">{host.name}</p>
                  {host.lastHeartbeat && (
                    <p className="text-xs text-slate-500">
                      Last seen: {new Date(host.lastHeartbeat).toLocaleTimeString()}
                    </p>
                  )}
                </div>
                <span className={`text-xs px-2 py-0.5 rounded-full ${host.isOnline ? 'text-emerald-400 bg-emerald-500/10' : 'text-slate-500 bg-slate-700/30'}`}>
                  {host.isOnline ? 'Online' : 'Offline'}
                </span>
                {isAdmin && (
                  <button
                    onClick={() => { if (confirm(`Remove host "${host.name}"?`)) removeHostMut.mutate(host.id) }}
                    className="text-slate-500 hover:text-red-400 transition-colors p-1"
                    title="Remove host"
                  >
                    <Trash2 size={14} />
                  </button>
                )}
              </div>
            ))}
          </div>

          {/* Admin actions */}
          {isAdmin && (
            <div className="border-t border-slate-700 px-5 py-3 flex flex-wrap gap-2">
              <button onClick={() => setShowAddHost(true)} className="btn-secondary text-xs">
                <Plus size={13} /> Add Host
              </button>
              <button onClick={() => genTokenMut.mutate()} disabled={genTokenMut.isPending} className="btn-secondary text-xs">
                <Key size={13} /> {genTokenMut.isPending ? 'Generating…' : 'Generate Token'}
              </button>
              {group.hasRegistrationToken && (
                <button onClick={() => { if (confirm('Revoke the registration token? Existing agents are unaffected, but new agents can\'t register.')) revokeTokenMut.mutate() }}
                  disabled={revokeTokenMut.isPending}
                  className="btn-secondary text-xs text-amber-400 hover:text-amber-300">
                  Revoke Token
                </button>
              )}
            </div>
          )}
        </div>
      )}

      {/* Token modal */}
      <Modal open={showToken} onClose={() => setShowToken(false)} title="Registration Token" size="lg">
        <div className="space-y-4">
          <div className="bg-amber-500/10 border border-amber-500/20 rounded-lg p-3">
            <p className="text-amber-400 text-sm font-medium">⚠️ Copy this token now — it won't be shown again.</p>
          </div>
          <div className="relative group">
            <pre className="text-sm bg-slate-950 text-emerald-400 p-3 rounded-lg font-mono break-all border border-slate-700">{token}</pre>
            <button onClick={() => { navigator.clipboard.writeText(token ?? ''); toast.success('Token copied') }}
              className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-400 hover:text-white">
              <Copy size={14} />
            </button>
          </div>
          <h3 className="text-sm font-semibold text-white">Deploy an Agent</h3>
          {token && <SetupInstructions token={token} connectionId={group.connectionId} />}
        </div>
      </Modal>

      {/* Add Host modal */}
      <Modal open={showAddHost} onClose={() => setShowAddHost(false)} title="Add Host Manually" size="sm">
        <div className="space-y-4">
          <p className="text-sm text-slate-400">
            Pre-register a host entry. The host will appear as offline until the agent starts sending heartbeats.
          </p>
          <div>
            <label className="label">Host Name <span className="text-red-400">*</span></label>
            <input value={newHostName} onChange={e => setNewHostName(e.target.value)} className="input" placeholder="e.g. worker-01" />
            <p className="text-xs text-slate-500 mt-1">Must match the agent's <code>NODE_NAME</code> environment variable.</p>
          </div>
          <div className="flex justify-end gap-2">
            <button onClick={() => setShowAddHost(false)} className="btn-secondary">Cancel</button>
            <button
              onClick={() => addHostMut.mutate()}
              disabled={!newHostName || addHostMut.isPending}
              className="btn-primary"
            >
              {addHostMut.isPending ? 'Adding…' : 'Add Host'}
            </button>
          </div>
        </div>
      </Modal>
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
          <p className="text-slate-400 text-sm mt-0.5">Manage agent pools and deploy runners to any machine</p>
        </div>
        {isAdmin && (
          <button onClick={() => setShowCreate(true)} className="btn-primary">
            <Plus size={15} /> New Group
          </button>
        )}
      </div>

      {(!data || data.length === 0) ? (
        <EmptyState message="No host groups configured. Create one and deploy an agent to start running jobs." />
      ) : (
        <div className="space-y-3">
          {data.map(g => <HostGroupCard key={g.id} group={g} isAdmin={isAdmin} />)}
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
