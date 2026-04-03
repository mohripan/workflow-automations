import { useState } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  ArrowLeft, Trash2, Plus, Key, Clock, Activity, Server,
  Monitor, Copy, Check, ShieldAlert, Cpu, HardDrive,
} from 'lucide-react'
import { hostGroupsApi } from '../api/hostGroups'
import { jobsApi } from '../api/jobs'
import { StatusBadge } from '../components/ui/StatusBadge'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'
import { Modal } from '../components/ui/Modal'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { useAuth } from '../hooks/useAuth'
import { formatDistanceToNow, format } from 'date-fns'
import toast from 'react-hot-toast'
import type { WorkflowHostInfo, RegistrationTokenInfo, AuditLogEntry, JobResponse } from '../types'

type Tab = 'hosts' | 'tokens' | 'jobs' | 'activity'

export default function HostGroupDetail() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const isAdmin = hasRole('admin')
  const canWrite = isAdmin || hasRole('operator')

  const [tab, setTab] = useState<Tab>('hosts')
  const [showGenerateToken, setShowGenerateToken] = useState(false)
  const [showAddHost, setShowAddHost] = useState(false)
  const [deleteConfirm, setDeleteConfirm] = useState(false)
  const [deleteConfirmName, setDeleteConfirmName] = useState('')
  const [deleteHostId, setDeleteHostId] = useState<string | null>(null)
  const [revokeTokenId, setRevokeTokenId] = useState<string | null>(null)
  const [generatedToken, setGeneratedToken] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [selectedHost, setSelectedHost] = useState<WorkflowHostInfo | null>(null)

  // --- Queries ---
  const { data: group, isLoading, error } = useQuery({
    queryKey: ['host-group', id],
    queryFn: () => hostGroupsApi.get(id!),
    enabled: !!id,
  })

  const { data: hosts } = useQuery({
    queryKey: ['host-group-hosts', id],
    queryFn: () => hostGroupsApi.getHosts(id!),
    enabled: !!id,
    refetchInterval: 15_000,
  })

  const { data: tokens } = useQuery({
    queryKey: ['host-group-tokens', id],
    queryFn: () => hostGroupsApi.getTokens(id!),
    enabled: !!id && tab === 'tokens',
  })

  const { data: activity } = useQuery({
    queryKey: ['host-group-activity', id],
    queryFn: () => hostGroupsApi.getActivity(id!),
    enabled: !!id && tab === 'activity',
  })

  const { data: jobs } = useQuery({
    queryKey: ['host-group-jobs', group?.connectionId],
    queryFn: () => jobsApi.list(group!.connectionId),
    enabled: !!group?.connectionId && tab === 'jobs',
  })

  // --- Mutations ---
  const deleteMutation = useMutation({
    mutationFn: () => hostGroupsApi.delete(id!, { confirmName: deleteConfirmName }),
    onSuccess: () => {
      toast.success('Host group deleted')
      navigate('/host-groups')
    },
    onError: () => toast.error('Delete failed — check confirmation name'),
  })

  const removeHostMutation = useMutation({
    mutationFn: (hostId: string) => hostGroupsApi.removeHost(id!, hostId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['host-group-hosts', id] })
      toast.success('Host removed')
      setDeleteHostId(null)
    },
    onError: () => toast.error('Failed to remove host'),
  })

  const revokeTokenMutation = useMutation({
    mutationFn: (tokenId: string) => hostGroupsApi.revokeToken(id!, tokenId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['host-group-tokens', id] })
      qc.invalidateQueries({ queryKey: ['host-group', id] })
      toast.success('Token revoked')
      setRevokeTokenId(null)
    },
    onError: () => toast.error('Failed to revoke token'),
  })

  if (isLoading) return <PageLoader />
  if (error || !group) return <ErrorState message="Host group not found" />

  const onlineCount = hosts?.filter(h => h.isOnline).length ?? 0
  const totalHosts = hosts?.length ?? 0

  const tabs: { key: Tab; label: string; icon: typeof Server }[] = [
    { key: 'hosts', label: `Hosts (${totalHosts})`, icon: Monitor },
    { key: 'tokens', label: `Tokens (${group.activeTokenCount})`, icon: Key },
    { key: 'jobs', label: 'Jobs', icon: Cpu },
    { key: 'activity', label: 'Activity', icon: Activity },
  ]

  const handleCopyToken = async (token: string) => {
    await navigator.clipboard.writeText(token)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <div className="p-6 space-y-6 max-w-6xl">
      {/* Header */}
      <div className="flex items-start gap-4">
        <button onClick={() => navigate('/host-groups')} className="btn-ghost mt-1">
          <ArrowLeft size={15} />
        </button>
        <div className="flex-1 min-w-0">
          <h1 className="text-xl font-bold text-white truncate">{group.name}</h1>
          <p className="text-slate-400 text-sm mt-1">
            Connection: <code className="text-indigo-400 text-xs">{group.connectionId}</code>
          </p>
        </div>
        {isAdmin && (
          <button
            onClick={() => { setDeleteConfirm(true); setDeleteConfirmName('') }}
            className="btn-danger flex-shrink-0"
          >
            <Trash2 size={14} /> Delete Group
          </button>
        )}
      </div>

      {/* Summary Cards */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <SummaryCard label="Total Hosts" value={totalHosts.toString()} />
        <SummaryCard
          label="Online"
          value={onlineCount.toString()}
          accent={onlineCount > 0 ? 'emerald' : 'slate'}
        />
        <SummaryCard
          label="Active Tokens"
          value={group.activeTokenCount.toString()}
          accent={group.hasActiveTokens ? 'indigo' : 'slate'}
        />
        <SummaryCard
          label="Created"
          value={formatDistanceToNow(new Date(group.createdAt), { addSuffix: true })}
        />
      </div>

      {/* Tabs */}
      <div className="border-b border-slate-700">
        <nav className="flex gap-1">
          {tabs.map(({ key, label, icon: Icon }) => (
            <button
              key={key}
              onClick={() => setTab(key)}
              className={`flex items-center gap-1.5 px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
                tab === key
                  ? 'border-indigo-500 text-indigo-400'
                  : 'border-transparent text-slate-400 hover:text-slate-300'
              }`}
            >
              <Icon size={14} /> {label}
            </button>
          ))}
        </nav>
      </div>

      {/* Tab Content */}
      {tab === 'hosts' && (
        <HostsTab
          hosts={hosts ?? []}
          canWrite={canWrite}
          onAdd={() => setShowAddHost(true)}
          onRemove={setDeleteHostId}
          onSelect={setSelectedHost}
          selectedHost={selectedHost}
          groupConnectionId={group.connectionId}
          jobs={jobs}
        />
      )}

      {tab === 'tokens' && (
        <TokensTab
          tokens={tokens ?? []}
          canWrite={canWrite}
          onGenerate={() => setShowGenerateToken(true)}
          onRevoke={setRevokeTokenId}
        />
      )}

      {tab === 'jobs' && (
        <JobsTab
          jobs={jobs ?? []}
          connectionId={group.connectionId}
        />
      )}

      {tab === 'activity' && <ActivityTab activity={activity ?? []} />}

      {/* --- Modals --- */}

      {/* Generate Token Modal */}
      <GenerateTokenModal
        open={showGenerateToken}
        groupId={id!}
        onClose={() => { setShowGenerateToken(false); setGeneratedToken(null) }}
        onGenerated={(token) => setGeneratedToken(token)}
        generatedToken={generatedToken}
        copied={copied}
        onCopy={handleCopyToken}
      />

      {/* Add Host Modal */}
      <AddHostModal
        open={showAddHost}
        groupId={id!}
        onClose={() => setShowAddHost(false)}
      />

      {/* Delete Group Confirm */}
      <Modal
        open={deleteConfirm}
        onClose={() => setDeleteConfirm(false)}
        title="Delete Host Group"
        size="sm"
      >
        <div className="space-y-4">
          <div className="flex gap-3">
            <ShieldAlert className="text-red-400 flex-shrink-0" size={18} />
            <div className="text-sm text-slate-300 space-y-2">
              <p>
                This will permanently delete <strong className="text-white">{group.name}</strong> and
                all {totalHosts} host(s) in it. Active jobs will be disrupted.
              </p>
              <p>Type the group name below to confirm:</p>
            </div>
          </div>
          <input
            value={deleteConfirmName}
            onChange={e => setDeleteConfirmName(e.target.value)}
            className="input"
            placeholder={group.name}
            autoFocus
          />
          <div className="flex justify-end gap-2">
            <button onClick={() => setDeleteConfirm(false)} className="btn-secondary">Cancel</button>
            <button
              onClick={() => deleteMutation.mutate()}
              disabled={deleteConfirmName !== group.name || deleteMutation.isPending}
              className="btn-danger"
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Delete Host Group'}
            </button>
          </div>
        </div>
      </Modal>

      {/* Remove Host Confirm */}
      <ConfirmDialog
        open={!!deleteHostId}
        onClose={() => setDeleteHostId(null)}
        onConfirm={() => deleteHostId && removeHostMutation.mutate(deleteHostId)}
        title="Remove Host"
        message="Remove this host from the group? It will no longer receive jobs."
        confirmLabel="Remove"
        danger
        loading={removeHostMutation.isPending}
      />

      {/* Revoke Token Confirm */}
      <ConfirmDialog
        open={!!revokeTokenId}
        onClose={() => setRevokeTokenId(null)}
        onConfirm={() => revokeTokenId && revokeTokenMutation.mutate(revokeTokenId)}
        title="Revoke Token"
        message="Revoke this registration token? Agents using it will no longer be able to register."
        confirmLabel="Revoke"
        danger
        loading={revokeTokenMutation.isPending}
      />
    </div>
  )
}

// --- Sub-components ---

function SummaryCard({ label, value, accent = 'slate' }: { label: string; value: string; accent?: string }) {
  const accentColors: Record<string, string> = {
    emerald: 'text-emerald-400',
    indigo: 'text-indigo-400',
    slate: 'text-slate-200',
  }
  return (
    <div className="card p-3">
      <p className="text-xs text-slate-500 mb-1">{label}</p>
      <p className={`text-lg font-semibold ${accentColors[accent] ?? accentColors.slate}`}>{value}</p>
    </div>
  )
}

function HostsTab({
  hosts, canWrite, onAdd, onRemove, onSelect, selectedHost, groupConnectionId, jobs,
}: {
  hosts: WorkflowHostInfo[]
  canWrite: boolean
  onAdd: () => void
  onRemove: (id: string) => void
  onSelect: (host: WorkflowHostInfo | null) => void
  selectedHost: WorkflowHostInfo | null
  groupConnectionId: string
  jobs?: JobResponse[]
}) {
  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-400">
          {hosts.filter(h => h.isOnline).length} of {hosts.length} host(s) online
        </p>
        {canWrite && (
          <button onClick={onAdd} className="btn-primary text-sm">
            <Plus size={14} /> Add Host
          </button>
        )}
      </div>

      {hosts.length === 0 ? (
        <EmptyState message="No hosts registered. Generate a token and deploy an agent." />
      ) : (
        <div className="grid gap-3">
          {hosts.map(host => (
            <div
              key={host.id}
              className={`card p-4 cursor-pointer transition-all ${
                selectedHost?.id === host.id ? 'ring-1 ring-indigo-500' : 'hover:bg-slate-700/30'
              }`}
              onClick={() => onSelect(selectedHost?.id === host.id ? null : host)}
            >
              <div className="flex items-center gap-3">
                <div className={`w-2.5 h-2.5 rounded-full flex-shrink-0 ${
                  host.isOnline ? 'bg-emerald-400 shadow-lg shadow-emerald-400/20' : 'bg-slate-600'
                }`} />
                <div className="flex-1 min-w-0">
                  <p className="text-sm font-medium text-white truncate">{host.name}</p>
                  <p className="text-xs text-slate-500 mt-0.5">
                    {host.isOnline ? 'Online' : 'Offline'}
                    {host.lastHeartbeat && (
                      <> · Last seen {formatDistanceToNow(new Date(host.lastHeartbeat), { addSuffix: true })}</>
                    )}
                  </p>
                </div>
                {canWrite && (
                  <button
                    onClick={e => { e.stopPropagation(); onRemove(host.id) }}
                    className="btn-ghost text-xs p-1 hover:text-red-400"
                    title="Remove host"
                  >
                    <Trash2 size={13} />
                  </button>
                )}
              </div>

              {/* Expanded Host Detail */}
              {selectedHost?.id === host.id && (
                <HostDetail host={host} connectionId={groupConnectionId} jobs={jobs} />
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function HostDetail({ host, connectionId, jobs }: {
  host: WorkflowHostInfo
  connectionId: string
  jobs?: JobResponse[]
}) {
  const hostJobs = (jobs ?? [])
    .filter(j => j.hostId === host.id)
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
    .slice(0, 10)

  return (
    <div className="mt-4 pt-4 border-t border-slate-700 space-y-4" onClick={e => e.stopPropagation()}>
      {/* Host Info Grid */}
      <div className="grid grid-cols-2 gap-3">
        <div>
          <p className="text-xs text-slate-500">Host ID</p>
          <p className="text-xs text-slate-300 font-mono mt-0.5">{host.id}</p>
        </div>
        <div>
          <p className="text-xs text-slate-500">Status</p>
          <p className={`text-xs mt-0.5 ${host.isOnline ? 'text-emerald-400' : 'text-slate-500'}`}>
            {host.isOnline ? '● Online' : '○ Offline'}
          </p>
        </div>
        <div>
          <p className="text-xs text-slate-500">Last Heartbeat</p>
          <p className="text-xs text-slate-300 mt-0.5">
            {host.lastHeartbeat
              ? format(new Date(host.lastHeartbeat), 'PPpp')
              : 'Never'}
          </p>
        </div>
        <div>
          <p className="text-xs text-slate-500">Resource Usage</p>
          <p className="text-xs text-slate-500 italic mt-0.5">
            <HardDrive size={11} className="inline mr-1" />
            Agent telemetry coming soon
          </p>
        </div>
      </div>

      {/* Job History */}
      <div>
        <p className="text-xs text-slate-400 font-medium mb-2">Recent Jobs ({hostJobs.length})</p>
        {hostJobs.length === 0 ? (
          <p className="text-xs text-slate-500">No jobs assigned to this host yet.</p>
        ) : (
          <div className="space-y-1">
            {hostJobs.map(job => (
              <Link
                key={job.id}
                to={`/jobs/${connectionId}/${job.id}`}
                className="flex items-center gap-2 p-2 rounded-lg hover:bg-slate-700/30 transition-colors"
              >
                <StatusBadge status={job.status} />
                <span className="text-xs text-slate-300 truncate flex-1">{job.automationName}</span>
                <span className="text-xs text-slate-500">
                  {formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}
                </span>
              </Link>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

function TokensTab({ tokens, canWrite, onGenerate, onRevoke }: {
  tokens: RegistrationTokenInfo[]
  canWrite: boolean
  onGenerate: () => void
  onRevoke: (id: string) => void
}) {
  const active = tokens.filter(t => !t.isExpired)
  const expired = tokens.filter(t => t.isExpired)

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-400">{active.length} active, {expired.length} expired</p>
        {canWrite && (
          <button onClick={onGenerate} className="btn-primary text-sm">
            <Plus size={14} /> Generate Token
          </button>
        )}
      </div>

      {tokens.length === 0 ? (
        <EmptyState message="No tokens generated yet." />
      ) : (
        <div className="space-y-2">
          {[...active, ...expired].map(token => (
            <div key={token.id} className="card p-4 flex items-center gap-3">
              <Key size={14} className={token.isExpired ? 'text-slate-600' : 'text-indigo-400'} />
              <div className="flex-1 min-w-0">
                <p className={`text-sm font-medium ${token.isExpired ? 'text-slate-500 line-through' : 'text-white'}`}>
                  {token.label || 'Unnamed token'}
                </p>
                <p className="text-xs text-slate-500 mt-0.5">
                  <Clock size={10} className="inline mr-1" />
                  {token.isExpired
                    ? `Expired ${formatDistanceToNow(new Date(token.expiresAt), { addSuffix: true })}`
                    : `Expires ${formatDistanceToNow(new Date(token.expiresAt), { addSuffix: true })}`}
                  {' · Created '}
                  {formatDistanceToNow(new Date(token.createdAt), { addSuffix: true })}
                </p>
              </div>
              {canWrite && !token.isExpired && (
                <button
                  onClick={() => onRevoke(token.id)}
                  className="btn-ghost text-xs hover:text-red-400"
                >
                  Revoke
                </button>
              )}
              {token.isExpired && (
                <span className="text-xs text-slate-600 px-2 py-0.5 bg-slate-800 rounded-full">Expired</span>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function JobsTab({ jobs, connectionId }: { jobs: JobResponse[]; connectionId: string }) {
  const sorted = [...jobs].sort(
    (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
  )

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-400">{sorted.length} total job(s) in this host group</p>
      {sorted.length === 0 ? (
        <EmptyState message="No jobs have run in this host group yet." />
      ) : (
        <div className="card overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-700">
                <th className="text-left px-4 py-2.5 text-xs text-slate-400 font-medium">Status</th>
                <th className="text-left px-3 py-2.5 text-xs text-slate-400 font-medium">Automation</th>
                <th className="text-left px-3 py-2.5 text-xs text-slate-400 font-medium">Host</th>
                <th className="text-left px-3 py-2.5 text-xs text-slate-400 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {sorted.slice(0, 50).map(job => (
                <tr
                  key={job.id}
                  className="border-b border-slate-700/50 hover:bg-slate-700/30 cursor-pointer"
                  onClick={() => window.location.href = `/jobs/${connectionId}/${job.id}`}
                >
                  <td className="px-4 py-2.5"><StatusBadge status={job.status} /></td>
                  <td className="px-3 py-2.5 text-xs text-slate-300 truncate max-w-48">{job.automationName}</td>
                  <td className="px-3 py-2.5 text-xs text-slate-500 font-mono truncate max-w-32">
                    {job.hostId?.slice(0, 8) ?? '—'}
                  </td>
                  <td className="px-3 py-2.5 text-xs text-slate-500">
                    {formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}
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

function ActivityTab({ activity }: { activity: AuditLogEntry[] }) {
  const actionIcons: Record<string, string> = {
    'HostGroup.Created': '🏗️',
    'HostGroup.Deleted': '🗑️',
    'RegistrationToken.Generated': '🔑',
    'RegistrationToken.Revoked': '🚫',
    'Host.Added': '➕',
    'Host.Removed': '➖',
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-400">{activity.length} event(s)</p>
      {activity.length === 0 ? (
        <EmptyState message="No activity recorded yet." />
      ) : (
        <div className="space-y-2">
          {activity.map(entry => (
            <div key={entry.id} className="card p-3 flex gap-3">
              <span className="text-base flex-shrink-0 mt-0.5">
                {actionIcons[entry.action] ?? '📋'}
              </span>
              <div className="flex-1 min-w-0">
                <p className="text-sm text-slate-200">{entry.detail}</p>
                <p className="text-xs text-slate-500 mt-0.5">
                  {entry.username && <span className="text-slate-400">{entry.username}</span>}
                  {entry.username && ' · '}
                  {formatDistanceToNow(new Date(entry.occurredAt), { addSuffix: true })}
                </p>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function GenerateTokenModal({ open, groupId, onClose, onGenerated, generatedToken, copied, onCopy }: {
  open: boolean
  groupId: string
  onClose: () => void
  onGenerated: (token: string) => void
  generatedToken: string | null
  copied: boolean
  onCopy: (token: string) => void
}) {
  const qc = useQueryClient()
  const [label, setLabel] = useState('')
  const [hours, setHours] = useState('24')

  const generateMutation = useMutation({
    mutationFn: () => hostGroupsApi.generateToken(groupId, {
      label: label || undefined,
      expiresInHours: parseFloat(hours) || 24,
    }),
    onSuccess: (data) => {
      onGenerated(data.token)
      qc.invalidateQueries({ queryKey: ['host-group-tokens', groupId] })
      qc.invalidateQueries({ queryKey: ['host-group', groupId] })
      toast.success('Token generated')
    },
    onError: () => toast.error('Failed to generate token'),
  })

  const handleClose = () => {
    setLabel('')
    setHours('24')
    onClose()
  }

  return (
    <Modal open={open} onClose={handleClose} title="Generate Registration Token" size="md">
      {generatedToken ? (
        <div className="space-y-4">
          <div className="bg-emerald-500/10 border border-emerald-500/20 rounded-lg p-4">
            <p className="text-sm text-emerald-300 font-medium mb-2">Token generated successfully!</p>
            <p className="text-xs text-slate-400 mb-3">
              Copy this token now — it won't be shown again.
            </p>
            <div className="flex items-center gap-2">
              <code className="flex-1 text-xs bg-slate-900 p-2 rounded border border-slate-700 text-slate-200 font-mono break-all select-all">
                {generatedToken}
              </code>
              <button
                onClick={() => onCopy(generatedToken)}
                className="btn-ghost flex-shrink-0"
                title="Copy to clipboard"
              >
                {copied ? <Check size={14} className="text-emerald-400" /> : <Copy size={14} />}
              </button>
            </div>
          </div>
          <div className="bg-slate-700/50 rounded-lg p-4">
            <p className="text-xs text-slate-400 mb-2">Use this token to register an agent:</p>
            <code className="text-xs text-indigo-300 block">
              REGISTRATION_TOKEN={generatedToken} dotnet run --project FlowForge.WorkflowHost
            </code>
          </div>
          <div className="flex justify-end">
            <button onClick={handleClose} className="btn-primary">Done</button>
          </div>
        </div>
      ) : (
        <div className="space-y-4">
          <div>
            <label className="label">Label (optional)</label>
            <input
              value={label}
              onChange={e => setLabel(e.target.value)}
              className="input"
              placeholder="e.g. production-ec2, dev-laptop"
            />
          </div>
          <div>
            <label className="label">Expires In (hours)</label>
            <input
              type="number"
              value={hours}
              onChange={e => setHours(e.target.value)}
              className="input"
              min="1"
              max="8760"
            />
            <p className="text-xs text-slate-500 mt-1">Default: 24 hours. Max: 8760 hours (1 year).</p>
          </div>
          <div className="flex justify-end gap-2">
            <button onClick={handleClose} className="btn-secondary">Cancel</button>
            <button
              onClick={() => generateMutation.mutate()}
              disabled={generateMutation.isPending}
              className="btn-primary"
            >
              {generateMutation.isPending ? 'Generating…' : 'Generate Token'}
            </button>
          </div>
        </div>
      )}
    </Modal>
  )
}

function AddHostModal({ open, groupId, onClose }: {
  open: boolean
  groupId: string
  onClose: () => void
}) {
  const qc = useQueryClient()
  const [name, setName] = useState('')

  const createMutation = useMutation({
    mutationFn: () => hostGroupsApi.createHost(groupId, { name }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['host-group-hosts', groupId] })
      toast.success('Host added')
      setName('')
      onClose()
    },
    onError: () => toast.error('Failed to add host'),
  })

  return (
    <Modal open={open} onClose={() => { setName(''); onClose() }} title="Add Host Manually" size="sm">
      <div className="space-y-4">
        <div>
          <label className="label">Host Name <span className="text-red-400">*</span></label>
          <input
            value={name}
            onChange={e => setName(e.target.value)}
            className="input"
            placeholder="e.g. worker-01"
            autoFocus
          />
          <p className="text-xs text-slate-500 mt-1">
            Must be unique. For remote agents, use token-based registration instead.
          </p>
        </div>
        <div className="flex justify-end gap-2">
          <button onClick={() => { setName(''); onClose() }} className="btn-secondary">Cancel</button>
          <button
            onClick={() => createMutation.mutate()}
            disabled={!name.trim() || createMutation.isPending}
            className="btn-primary"
          >
            {createMutation.isPending ? 'Adding…' : 'Add Host'}
          </button>
        </div>
      </div>
    </Modal>
  )
}
