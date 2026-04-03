import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Plus, Server, ChevronDown, ChevronRight, Circle, Key, Trash2,
  Copy, Terminal, Monitor, Shield, Activity, AlertTriangle, Users, ExternalLink,
} from 'lucide-react'
import { formatDistanceToNow } from 'date-fns'
import { hostGroupsApi } from '../api/hostGroups'
import { Modal } from '../components/ui/Modal'
import { PageLoader, ErrorState, EmptyState, Spinner } from '../components/ui/States'
import { useAuth } from '../hooks/useAuth'
import toast from 'react-hot-toast'
import type {
  HostGroup, WorkflowHostInfo, GenerateTokenResponse,
} from '../types'

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const TTL_OPTIONS = [
  { label: '1 hour', hours: 1 },
  { label: '6 hours', hours: 6 },
  { label: '24 hours', hours: 24 },
  { label: '7 days', hours: 168 },
  { label: '30 days', hours: 720 },
] as const

type Tab = 'hosts' | 'tokens' | 'activity'

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function relativeTime(iso: string) {
  try {
    return formatDistanceToNow(new Date(iso), { addSuffix: true })
  } catch {
    return iso
  }
}

function copyToClipboard(text: string, label = 'Copied to clipboard') {
  navigator.clipboard.writeText(text)
  toast.success(label)
}

function actionIcon(action: string) {
  if (action.includes('create') || action.includes('add')) return <Plus size={14} className="text-emerald-400" />
  if (action.includes('delete') || action.includes('remove') || action.includes('revoke')) return <Trash2 size={14} className="text-red-400" />
  if (action.includes('token') || action.includes('generate')) return <Key size={14} className="text-amber-400" />
  return <Activity size={14} className="text-indigo-400" />
}

// ---------------------------------------------------------------------------
// Setup Instructions (shown after token generation)
// ---------------------------------------------------------------------------

function SetupInstructions({ token, connectionId }: { token: string; connectionId: string }) {
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
          <button onClick={() => copyToClipboard(dockerCmd)} className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-400 hover:text-white">
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
          <button onClick={() => copyToClipboard(systemdCmd)} className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-400 hover:text-white">
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

// ---------------------------------------------------------------------------
// Hosts Tab
// ---------------------------------------------------------------------------

function HostsTab({ groupId, isAdmin }: { groupId: string; isAdmin: boolean }) {
  const qc = useQueryClient()
  const [showAddHost, setShowAddHost] = useState(false)
  const [newHostName, setNewHostName] = useState('')
  const [confirmRemove, setConfirmRemove] = useState<WorkflowHostInfo | null>(null)

  const { data: hosts, isLoading } = useQuery({
    queryKey: ['hosts', groupId],
    queryFn: () => hostGroupsApi.getHosts(groupId),
    refetchInterval: 15_000,
  })

  const addHostMut = useMutation({
    mutationFn: () => hostGroupsApi.createHost(groupId, { name: newHostName }),
    onSuccess: () => {
      toast.success('Host added')
      setShowAddHost(false)
      setNewHostName('')
      qc.invalidateQueries({ queryKey: ['hosts', groupId] })
    },
    onError: () => toast.error('Failed to add host (name may already exist)'),
  })

  const removeHostMut = useMutation({
    mutationFn: (hostId: string) => hostGroupsApi.removeHost(groupId, hostId),
    onSuccess: () => {
      toast.success('Host removed')
      setConfirmRemove(null)
      qc.invalidateQueries({ queryKey: ['hosts', groupId] })
    },
    onError: () => toast.error('Failed to remove host'),
  })

  if (isLoading) return <div className="flex justify-center py-8"><Spinner /></div>

  return (
    <div>
      {isAdmin && (
        <div className="px-5 py-3 border-b border-slate-700/50 flex items-center justify-between">
          {showAddHost ? (
            <form
              className="flex items-center gap-2 flex-1"
              onSubmit={e => { e.preventDefault(); if (newHostName.trim()) addHostMut.mutate() }}
            >
              <input
                value={newHostName}
                onChange={e => setNewHostName(e.target.value)}
                className="input max-w-xs"
                placeholder="e.g. worker-01"
                autoFocus
              />
              <button type="submit" disabled={!newHostName.trim() || addHostMut.isPending} className="btn-primary text-xs">
                {addHostMut.isPending ? 'Adding…' : 'Add'}
              </button>
              <button type="button" onClick={() => { setShowAddHost(false); setNewHostName('') }} className="btn-ghost text-xs">
                Cancel
              </button>
            </form>
          ) : (
            <button onClick={() => setShowAddHost(true)} className="btn-secondary text-xs">
              <Plus size={13} /> Add Host
            </button>
          )}
        </div>
      )}

      <div className="divide-y divide-slate-700/50">
        {(!hosts || hosts.length === 0) && (
          <p className="text-slate-500 text-sm px-5 py-6 text-center">
            No hosts in this group. Generate a registration token and deploy an agent.
          </p>
        )}
        {hosts?.map(host => (
          <div key={host.id} className="flex items-center gap-3 px-5 py-3 hover:bg-slate-700/20 transition-colors">
            <Circle
              size={8}
              className={`flex-shrink-0 fill-current ${host.isOnline ? 'text-emerald-400' : 'text-slate-600'}`}
            />
            <div className="flex-1 min-w-0">
              <p className="text-sm text-slate-200">{host.name}</p>
              {host.lastHeartbeat && (
                <p className="text-xs text-slate-500">
                  Last heartbeat {relativeTime(host.lastHeartbeat)}
                </p>
              )}
            </div>
            <span className={`text-xs px-2 py-0.5 rounded-full ${host.isOnline ? 'text-emerald-400 bg-emerald-500/10' : 'text-slate-500 bg-slate-700/30'}`}>
              {host.isOnline ? 'Online' : 'Offline'}
            </span>
            {isAdmin && (
              <button
                onClick={() => setConfirmRemove(host)}
                className="text-slate-500 hover:text-red-400 transition-colors p-1"
                title="Remove host"
              >
                <Trash2 size={14} />
              </button>
            )}
          </div>
        ))}
      </div>

      {/* Remove host confirmation */}
      <Modal open={!!confirmRemove} onClose={() => setConfirmRemove(null)} title="Remove Host" size="sm">
        <div className="space-y-4">
          <p className="text-sm text-slate-400">
            Remove host <strong className="text-white">{confirmRemove?.name}</strong> from this group?
            The agent process will not be stopped, but it will no longer receive jobs.
          </p>
          <div className="flex justify-end gap-2">
            <button onClick={() => setConfirmRemove(null)} className="btn-secondary">Cancel</button>
            <button
              onClick={() => confirmRemove && removeHostMut.mutate(confirmRemove.id)}
              disabled={removeHostMut.isPending}
              className="btn-danger"
            >
              {removeHostMut.isPending ? 'Removing…' : 'Remove'}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Tokens Tab
// ---------------------------------------------------------------------------

function TokensTab({ group, isAdmin }: { group: HostGroup; isAdmin: boolean }) {
  const qc = useQueryClient()
  const [showGenerate, setShowGenerate] = useState(false)
  const [tokenLabel, setTokenLabel] = useState('')
  const [tokenTtl, setTokenTtl] = useState(24)
  const [generatedToken, setGeneratedToken] = useState<GenerateTokenResponse | null>(null)

  const { data: tokens, isLoading } = useQuery({
    queryKey: ['tokens', group.id],
    queryFn: () => hostGroupsApi.getTokens(group.id),
  })

  const generateMut = useMutation({
    mutationFn: () => hostGroupsApi.generateToken(group.id, {
      label: tokenLabel.trim() || undefined,
      expiresInHours: tokenTtl,
    }),
    onSuccess: (data) => {
      setGeneratedToken(data)
      setShowGenerate(false)
      setTokenLabel('')
      setTokenTtl(24)
      qc.invalidateQueries({ queryKey: ['tokens', group.id] })
      qc.invalidateQueries({ queryKey: ['host-groups'] })
    },
    onError: () => toast.error('Failed to generate token'),
  })

  const revokeMut = useMutation({
    mutationFn: (tokenId: string) => hostGroupsApi.revokeToken(group.id, tokenId),
    onSuccess: () => {
      toast.success('Token revoked')
      qc.invalidateQueries({ queryKey: ['tokens', group.id] })
      qc.invalidateQueries({ queryKey: ['host-groups'] })
    },
    onError: () => toast.error('Failed to revoke token'),
  })

  if (isLoading) return <div className="flex justify-center py-8"><Spinner /></div>

  return (
    <div>
      {isAdmin && (
        <div className="px-5 py-3 border-b border-slate-700/50">
          <button onClick={() => setShowGenerate(true)} className="btn-secondary text-xs">
            <Key size={13} /> Generate Token
          </button>
        </div>
      )}

      <div className="divide-y divide-slate-700/50">
        {(!tokens || tokens.length === 0) && (
          <p className="text-slate-500 text-sm px-5 py-6 text-center">
            No registration tokens. Generate one to allow agents to register with this group.
          </p>
        )}
        {tokens?.map(t => (
          <div key={t.id} className="flex items-center gap-3 px-5 py-3 hover:bg-slate-700/20 transition-colors">
            <Key size={14} className={t.isExpired ? 'text-slate-600' : 'text-amber-400'} />
            <div className="flex-1 min-w-0">
              <p className="text-sm text-slate-200">{t.label || <span className="text-slate-500 italic">No label</span>}</p>
              <p className="text-xs text-slate-500">
                {t.isExpired ? 'Expired' : `Expires ${relativeTime(t.expiresAt)}`}
                {' · '}Created {relativeTime(t.createdAt)}
              </p>
            </div>
            <span className={`text-xs px-2 py-0.5 rounded-full ${t.isExpired ? 'text-slate-500 bg-slate-700/30' : 'text-emerald-400 bg-emerald-500/10'}`}>
              {t.isExpired ? 'Expired' : 'Active'}
            </span>
            {isAdmin && !t.isExpired && (
              <button
                onClick={() => revokeMut.mutate(t.id)}
                disabled={revokeMut.isPending}
                className="text-slate-500 hover:text-red-400 transition-colors p-1"
                title="Revoke token"
              >
                <Trash2 size={14} />
              </button>
            )}
          </div>
        ))}
      </div>

      {/* Generate Token Form Modal */}
      <Modal open={showGenerate} onClose={() => setShowGenerate(false)} title="Generate Registration Token" size="sm">
        <div className="space-y-4">
          <div>
            <label className="label">Label <span className="text-slate-500">(optional)</span></label>
            <input
              value={tokenLabel}
              onChange={e => setTokenLabel(e.target.value)}
              className="input"
              placeholder="e.g. production-deploy-2025"
            />
          </div>
          <div>
            <label className="label">Expires in</label>
            <select
              value={tokenTtl}
              onChange={e => setTokenTtl(Number(e.target.value))}
              className="input"
            >
              {TTL_OPTIONS.map(o => (
                <option key={o.hours} value={o.hours}>{o.label}</option>
              ))}
            </select>
          </div>
          <div className="flex justify-end gap-2">
            <button onClick={() => setShowGenerate(false)} className="btn-secondary">Cancel</button>
            <button onClick={() => generateMut.mutate()} disabled={generateMut.isPending} className="btn-primary">
              {generateMut.isPending ? 'Generating…' : 'Generate'}
            </button>
          </div>
        </div>
      </Modal>

      {/* Show Generated Token Modal */}
      <Modal open={!!generatedToken} onClose={() => setGeneratedToken(null)} title="Registration Token" size="lg">
        {generatedToken && (
          <div className="space-y-4">
            <div className="bg-amber-500/10 border border-amber-500/20 rounded-lg p-3">
              <p className="text-amber-400 text-sm font-medium flex items-center gap-2">
                <AlertTriangle size={16} /> This token will only be shown once. Copy it now.
              </p>
            </div>

            {generatedToken.label && (
              <p className="text-xs text-slate-400">Label: <span className="text-slate-200">{generatedToken.label}</span></p>
            )}
            <p className="text-xs text-slate-400">Expires: <span className="text-slate-200">{relativeTime(generatedToken.expiresAt)}</span></p>

            <div className="relative group">
              <pre className="text-sm bg-slate-950 text-emerald-400 p-3 rounded-lg font-mono break-all border border-slate-700">
                {generatedToken.token}
              </pre>
              <button
                onClick={() => copyToClipboard(generatedToken.token, 'Token copied')}
                className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-400 hover:text-white"
              >
                <Copy size={14} />
              </button>
            </div>

            <h3 className="text-sm font-semibold text-white">Deploy an Agent</h3>
            <SetupInstructions token={generatedToken.token} connectionId={group.connectionId} />
          </div>
        )}
      </Modal>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Activity Tab
// ---------------------------------------------------------------------------

function ActivityTab({ groupId }: { groupId: string }) {
  const { data: entries, isLoading } = useQuery({
    queryKey: ['activity', groupId],
    queryFn: () => hostGroupsApi.getActivity(groupId),
  })

  if (isLoading) return <div className="flex justify-center py-8"><Spinner /></div>

  const visible = entries?.slice(0, 50) ?? []

  return (
    <div className="divide-y divide-slate-700/50">
      {visible.length === 0 && (
        <p className="text-slate-500 text-sm px-5 py-6 text-center">No activity recorded for this group.</p>
      )}
      {visible.map(entry => (
        <div key={entry.id} className="flex items-start gap-3 px-5 py-3 hover:bg-slate-700/20 transition-colors">
          <div className="mt-0.5">{actionIcon(entry.action)}</div>
          <div className="flex-1 min-w-0">
            <p className="text-sm text-slate-200">{entry.detail || entry.action}</p>
            <p className="text-xs text-slate-500">
              {entry.username && <span>{entry.username} · </span>}
              {relativeTime(entry.occurredAt)}
            </p>
          </div>
        </div>
      ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Delete Host Group Modal
// ---------------------------------------------------------------------------

function DeleteGroupModal({
  group,
  open,
  onClose,
}: {
  group: HostGroup | null
  open: boolean
  onClose: () => void
}) {
  const qc = useQueryClient()
  const [confirmName, setConfirmName] = useState('')

  const deleteMut = useMutation({
    mutationFn: () => hostGroupsApi.delete(group!.id, { confirmName }),
    onSuccess: () => {
      toast.success(`Host group "${group!.name}" deleted`)
      onClose()
      qc.invalidateQueries({ queryKey: ['host-groups'] })
    },
    onError: () => toast.error('Failed to delete host group'),
  })

  const nameMatches = confirmName === group?.name

  const handleClose = () => { setConfirmName(''); onClose() }

  return (
    <Modal open={open} onClose={handleClose} title="Delete Host Group" size="sm">
      {group && (
        <div className="space-y-4">
          <div className="bg-red-500/10 border border-red-500/20 rounded-lg p-3">
            <p className="text-red-400 text-sm font-medium flex items-center gap-2">
              <AlertTriangle size={16} /> This action is irreversible.
            </p>
            <p className="text-red-400/80 text-xs mt-1">
              All hosts and tokens associated with this group will be permanently removed.
            </p>
          </div>
          <div>
            <label className="label">
              Type <strong className="text-white">{group.name}</strong> to confirm
            </label>
            <input
              value={confirmName}
              onChange={e => setConfirmName(e.target.value)}
              className="input"
              placeholder={group.name}
              autoFocus
            />
            {confirmName.length > 0 && !nameMatches && (
              <p className="text-xs text-red-400 mt-1">Name does not match.</p>
            )}
          </div>
          <div className="flex justify-end gap-2">
            <button onClick={handleClose} className="btn-secondary">Cancel</button>
            <button
              onClick={() => deleteMut.mutate()}
              disabled={!nameMatches || deleteMut.isPending}
              className="btn-danger"
            >
              {deleteMut.isPending ? 'Deleting…' : 'Delete Group'}
            </button>
          </div>
        </div>
      )}
    </Modal>
  )
}

// ---------------------------------------------------------------------------
// Host Group Card (collapsible with tabs)
// ---------------------------------------------------------------------------

function HostGroupCard({
  group,
  isAdmin,
  onDelete,
}: {
  group: HostGroup
  isAdmin: boolean
  onDelete: (g: HostGroup) => void
}) {
  const [open, setOpen] = useState(false)
  const [activeTab, setActiveTab] = useState<Tab>('hosts')
  const navigate = useNavigate()

  const { data: hosts } = useQuery({
    queryKey: ['hosts', group.id],
    queryFn: () => hostGroupsApi.getHosts(group.id),
    enabled: open,
    refetchInterval: open ? 15_000 : false,
  })

  const hostCount = hosts?.length ?? 0
  const onlineCount = hosts?.filter(h => h.isOnline).length ?? 0

  const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
    { id: 'hosts', label: 'Hosts', icon: <Server size={14} /> },
    { id: 'tokens', label: 'Tokens', icon: <Key size={14} /> },
    { id: 'activity', label: 'Activity', icon: <Activity size={14} /> },
  ]

  return (
    <div className="card overflow-hidden">
      {/* Header row */}
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

        {/* Summary badges */}
        <div className="hidden sm:flex items-center gap-3 mr-2">
          {open && hosts ? (
            <span className="text-xs text-slate-400 flex items-center gap-1">
              <Users size={12} /> {onlineCount}/{hostCount} online
            </span>
          ) : (
            <span className="text-xs text-slate-400 flex items-center gap-1">
              <Key size={12} /> {group.activeTokenCount} token{group.activeTokenCount !== 1 ? 's' : ''}
            </span>
          )}
        </div>

        {group.hasActiveTokens && (
          <span title="Has active registration tokens">
            <Shield size={13} className="text-amber-400 flex-shrink-0" />
          </span>
        )}

        <button
          onClick={(e) => { e.stopPropagation(); navigate(`/host-groups/${group.id}`) }}
          className="text-slate-500 hover:text-indigo-400 transition-colors p-1 flex-shrink-0"
          title="View details"
        >
          <ExternalLink size={14} />
        </button>

        {open
          ? <ChevronDown size={16} className="text-slate-500 flex-shrink-0" />
          : <ChevronRight size={16} className="text-slate-500 flex-shrink-0" />
        }
      </button>

      {/* Expanded: tab bar + content */}
      {open && (
        <div className="border-t border-slate-700">
          {/* Tab bar */}
          <div className="flex items-center border-b border-slate-700/50 px-5">
            {tabs.map(tab => (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`flex items-center gap-1.5 px-3 py-2.5 text-xs font-medium border-b-2 transition-colors ${
                  activeTab === tab.id
                    ? 'border-indigo-500 text-indigo-400'
                    : 'border-transparent text-slate-500 hover:text-slate-300'
                }`}
              >
                {tab.icon} {tab.label}
              </button>
            ))}

            {/* Delete button — far right */}
            {isAdmin && (
              <button
                onClick={e => { e.stopPropagation(); onDelete(group) }}
                className="ml-auto text-slate-500 hover:text-red-400 transition-colors p-1.5"
                title="Delete host group"
              >
                <Trash2 size={14} />
              </button>
            )}
          </div>

          {/* Tab content */}
          {activeTab === 'hosts' && <HostsTab groupId={group.id} isAdmin={isAdmin} />}
          {activeTab === 'tokens' && <TokensTab group={group} isAdmin={isAdmin} />}
          {activeTab === 'activity' && <ActivityTab groupId={group.id} />}
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Page Root
// ---------------------------------------------------------------------------

export default function HostGroupsPage() {
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const isAdmin = hasRole('admin')

  const [showCreate, setShowCreate] = useState(false)
  const [newName, setNewName] = useState('')
  const [newConnId, setNewConnId] = useState('')
  const [deleteTarget, setDeleteTarget] = useState<HostGroup | null>(null)

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
      {/* Page header */}
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

      {/* Group list */}
      {(!data || data.length === 0) ? (
        <EmptyState message="No host groups configured. Create one and deploy an agent to start running jobs." />
      ) : (
        <div className="space-y-3">
          {data.map(g => (
            <HostGroupCard
              key={g.id}
              group={g}
              isAdmin={isAdmin}
              onDelete={setDeleteTarget}
            />
          ))}
        </div>
      )}

      {/* Create Host Group Modal */}
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

      {/* Delete Host Group Modal */}
      <DeleteGroupModal
        group={deleteTarget}
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
      />
    </div>
  )
}
