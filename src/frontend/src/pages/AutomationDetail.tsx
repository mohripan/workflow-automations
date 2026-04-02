import { useState } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  ArrowLeft, Edit, Trash2, ToggleLeft, ToggleRight,
  Plus, Webhook, Pencil, X
} from 'lucide-react'
import { automationsApi } from '../api/automations'
import { jobsApi } from '../api/jobs'
import { hostGroupsApi } from '../api/hostGroups'
import { triggersApi } from '../api/triggers'
import { PageLoader, ErrorState } from '../components/ui/States'
import { StatusBadge, TriggerTypeBadge } from '../components/ui/StatusBadge'
import { CodeEditor } from '../components/ui/CodeEditor'
import { Modal } from '../components/ui/Modal'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { TriggerConfigForm } from '../components/automation/TriggerConfigForm'
import { ConditionTreeDisplay } from '../components/automation/ConditionTreeBuilder'
import { useAuth } from '../hooks/useAuth'
import { formatDistanceToNow } from 'date-fns'
import toast from 'react-hot-toast'
import type { CreateTriggerRequest, TriggerResponse, TriggerConditionNode } from '../types'

function TriggerModal({
  automationId, trigger, onClose,
}: { automationId: string; trigger?: TriggerResponse; onClose: () => void }) {
  const qc = useQueryClient()
  const { data: types } = useQuery({ queryKey: ['trigger-types'], queryFn: triggersApi.getTypes })
  const [name, setName] = useState(trigger?.name ?? '')
  const [typeId, setTypeId] = useState(trigger?.typeId ?? '')
  const [config, setConfig] = useState<Record<string, string>>(() => {
    try { return trigger ? JSON.parse(trigger.configJson) : {} } catch { return {} }
  })

  const save = useMutation({
    mutationFn: async () => {
      const req: CreateTriggerRequest = { name, typeId, configJson: JSON.stringify(config) }
      if (trigger) return automationsApi.updateTrigger(automationId, trigger.id, req)
      return automationsApi.addTrigger(automationId, req)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['automation', automationId] })
      toast.success(trigger ? 'Trigger updated' : 'Trigger added')
      onClose()
    },
    onError: (e: Error) => toast.error(e.message),
  })

  return (
    <Modal open onClose={onClose} title={trigger ? 'Edit Trigger' : 'Add Trigger'} size="lg">
      <div className="space-y-4">
        <div>
          <label className="label">Trigger Name <span className="text-red-400">*</span></label>
          <input value={name} onChange={e => setName(e.target.value)} className="input" placeholder="e.g. daily-schedule" />
          <p className="text-xs text-slate-500 mt-1">Used in the condition tree. Must be unique within this automation.</p>
        </div>
        <div>
          <label className="label">Type <span className="text-red-400">*</span></label>
          <select value={typeId} onChange={e => { setTypeId(e.target.value); setConfig({}) }} className="input">
            <option value="">Select trigger type…</option>
            {types?.map(t => <option key={t.typeId} value={t.typeId}>{t.displayName}</option>)}
          </select>
        </div>
        {typeId && (
          <div>
            <label className="label">Configuration</label>
            <div className="bg-slate-900/50 rounded-lg p-4 border border-slate-700">
              <TriggerConfigForm typeId={typeId} value={config} onChange={setConfig} />
            </div>
          </div>
        )}
        <div className="flex justify-end gap-2 pt-2">
          <button onClick={onClose} className="btn-secondary">Cancel</button>
          <button
            onClick={() => save.mutate()}
            disabled={!name || !typeId || save.isPending}
            className="btn-primary"
          >
            {save.isPending ? 'Saving…' : 'Save Trigger'}
          </button>
        </div>
      </div>
    </Modal>
  )
}

export default function AutomationDetail() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { hasRole } = useAuth()
  const canWrite = hasRole('admin') || hasRole('operator')
  const isAdmin = hasRole('admin')

  const [triggerModal, setTriggerModal] = useState<{ open: boolean; trigger?: TriggerResponse }>({ open: false })
  const [deleteConfirm, setDeleteConfirm] = useState(false)
  const [deleteTriggerConfirm, setDeleteTriggerConfirm] = useState<string | null>(null)
  const [webhookModal, setWebhookModal] = useState<string | null>(null)
  const [webhookSecret, setWebhookSecret] = useState('')

  const { data: auto, isLoading, error } = useQuery({
    queryKey: ['automation', id],
    queryFn: () => automationsApi.get(id!),
    enabled: !!id,
  })

  const { data: hostGroups } = useQuery({
    queryKey: ['host-groups'],
    queryFn: hostGroupsApi.list,
  })

  const connectionId = hostGroups?.find(g => g.id === auto?.hostGroupId)?.connectionId

  const { data: jobs } = useQuery({
    queryKey: ['jobs', connectionId, { automationId: id }],
    queryFn: () => jobsApi.list(connectionId!, { automationId: id }),
    enabled: !!connectionId && !!id,
  })

  const toggleMutation = useMutation({
    mutationFn: () => auto!.isEnabled ? automationsApi.disable(id!) : automationsApi.enable(id!),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['automation', id] }); toast.success('Updated') },
  })

  const deleteMutation = useMutation({
    mutationFn: () => automationsApi.delete(id!),
    onSuccess: () => { navigate('/automations'); toast.success('Automation deleted') },
    onError: () => toast.error('Failed to delete'),
  })

  const deleteTriggerMutation = useMutation({
    mutationFn: (triggerId: string) => automationsApi.deleteTrigger(id!, triggerId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['automation', id] }); toast.success('Trigger removed') },
    onError: () => toast.error('Failed to remove trigger'),
  })

  const fireWebhookMutation = useMutation({
    mutationFn: (signature: string) => automationsApi.fireWebhook(id!, signature || undefined),
    onSuccess: () => { toast.success('Webhook fired'); setWebhookModal(null); setWebhookSecret('') },
    onError: () => toast.error('Webhook failed — check signature'),
  })

  if (isLoading) return <PageLoader />
  if (error || !auto) return <ErrorState message="Automation not found" />

  const conditionNode: TriggerConditionNode = typeof auto.triggerCondition === 'string'
    ? JSON.parse(auto.triggerCondition)
    : auto.triggerCondition

  const recentJobs = [...(jobs ?? [])].sort((a, b) =>
    new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
  ).slice(0, 5)

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      {/* Header */}
      <div className="flex items-start gap-4">
        <button onClick={() => navigate('/automations')} className="btn-ghost mt-1">
          <ArrowLeft size={15} />
        </button>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-3 flex-wrap">
            <h1 className="text-xl font-bold text-white truncate">{auto.name}</h1>
            <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${auto.isEnabled ? 'text-emerald-400 bg-emerald-500/10' : 'text-slate-500 bg-slate-700/30'}`}>
              {auto.isEnabled ? 'Enabled' : 'Disabled'}
            </span>
          </div>
          {auto.description && <p className="text-slate-400 text-sm mt-1">{auto.description}</p>}
        </div>
        <div className="flex items-center gap-2 flex-shrink-0">
          {canWrite && (
            <>
              <button onClick={() => toggleMutation.mutate()} className="btn-secondary" title={auto.isEnabled ? 'Disable' : 'Enable'}>
                {auto.isEnabled ? <ToggleRight size={16} className="text-emerald-400" /> : <ToggleLeft size={16} />}
                {auto.isEnabled ? 'Disable' : 'Enable'}
              </button>
              <button onClick={() => navigate(`/automations/${id}/edit`)} className="btn-secondary">
                <Edit size={15} /> Edit
              </button>
            </>
          )}
          {isAdmin && (
            <button onClick={() => setDeleteConfirm(true)} className="btn-danger">
              <Trash2 size={15} />
            </button>
          )}
        </div>
      </div>

      {/* Info grid */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        {[
          { label: 'Task ID', value: <code className="text-indigo-400 text-xs">{auto.taskId}</code> },
          { label: 'Host Group', value: <span className="text-xs text-slate-300">{auto.hostGroupId}</span> },
          { label: 'Timeout', value: <span className="text-xs text-slate-300">{auto.timeoutSeconds ? `${auto.timeoutSeconds}s` : 'None'}</span> },
          { label: 'Max Retries', value: <span className="text-xs text-slate-300">{auto.maxRetries}</span> },
        ].map(({ label, value }) => (
          <div key={label} className="card p-3">
            <p className="text-xs text-slate-500 mb-1">{label}</p>
            {value}
          </div>
        ))}
      </div>

      <div className="grid lg:grid-cols-2 gap-6">
        {/* Triggers */}
        <div className="card overflow-hidden">
          <div className="px-4 py-3 border-b border-slate-700 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-white">Triggers</h2>
            {canWrite && (
              <button onClick={() => setTriggerModal({ open: true })} className="btn-ghost text-xs">
                <Plus size={13} /> Add
              </button>
            )}
          </div>
          {auto.triggers.length === 0 ? (
            <p className="text-slate-500 text-sm text-center py-8">No triggers yet.</p>
          ) : (
            <div className="divide-y divide-slate-700/50">
              {auto.triggers.map(trigger => (
                <div key={trigger.id} className="flex items-center gap-3 px-4 py-3">
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="text-sm text-slate-200 font-medium">{trigger.name}</span>
                      <TriggerTypeBadge typeId={trigger.typeId} />
                    </div>
                  </div>
                  <div className="flex items-center gap-1 flex-shrink-0">
                    {trigger.typeId === 'webhook' && canWrite && (
                      <button
                        onClick={() => { setWebhookModal(trigger.name); setWebhookSecret('') }}
                        className="btn-ghost text-xs p-1"
                        title="Fire webhook"
                      >
                        <Webhook size={13} />
                      </button>
                    )}
                    {canWrite && (
                      <>
                        <button onClick={() => setTriggerModal({ open: true, trigger })} className="btn-ghost text-xs p-1">
                          <Pencil size={13} />
                        </button>
                        <button onClick={() => setDeleteTriggerConfirm(trigger.id)} className="btn-ghost text-xs p-1 hover:text-red-400">
                          <X size={13} />
                        </button>
                      </>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Condition Tree */}
        <div className="card overflow-hidden">
          <div className="px-4 py-3 border-b border-slate-700 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-white">Condition Tree</h2>
            {canWrite && (
              <button onClick={() => navigate(`/automations/${id}/edit`)} className="btn-ghost text-xs">
                <Edit size={13} /> Edit
              </button>
            )}
          </div>
          <div className="p-4">
            {conditionNode ? (
              <ConditionTreeDisplay node={conditionNode} />
            ) : (
              <p className="text-slate-500 text-sm">No condition configured.</p>
            )}
          </div>
        </div>
      </div>

      {/* Task Config */}
      {auto.taskConfig && (
        <div className="card overflow-hidden">
          <div className="px-4 py-3 border-b border-slate-700">
            <h2 className="text-sm font-semibold text-white">Task Configuration</h2>
          </div>
          <div className="p-4">
            <CodeEditor
              value={JSON.stringify(JSON.parse(auto.taskConfig), null, 2)}
              readOnly height="180px"
            />
          </div>
        </div>
      )}

      {/* Recent Jobs */}
      <div className="card overflow-hidden">
        <div className="px-4 py-3 border-b border-slate-700 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-white">Recent Jobs</h2>
          <Link to={`/jobs`} className="text-xs text-indigo-400 hover:text-indigo-300 transition-colors">View all →</Link>
        </div>
        {recentJobs.length === 0 ? (
          <p className="text-slate-500 text-sm text-center py-8">No jobs yet.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-700">
                <th className="text-left px-4 py-2.5 text-xs text-slate-400 font-medium">Status</th>
                <th className="text-left px-3 py-2.5 text-xs text-slate-400 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {recentJobs.map(job => (
                <tr key={job.id} className="border-b border-slate-700/50 hover:bg-slate-700/30 cursor-pointer" onClick={() => connectionId && navigate(`/jobs/${connectionId}/${job.id}`)}>
                  <td className="px-4 py-2.5"><StatusBadge status={job.status} /></td>
                  <td className="px-3 py-2.5 text-xs text-slate-500">
                    {formatDistanceToNow(new Date(job.createdAt), { addSuffix: true })}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Modals */}
      {triggerModal.open && (
        <TriggerModal
          automationId={id!}
          trigger={triggerModal.trigger}
          onClose={() => setTriggerModal({ open: false })}
        />
      )}

      <ConfirmDialog
        open={deleteConfirm}
        onClose={() => setDeleteConfirm(false)}
        onConfirm={() => deleteMutation.mutate()}
        title="Delete Automation"
        message={`Are you sure you want to delete "${auto.name}"? This cannot be undone.`}
        confirmLabel="Delete"
        danger
        loading={deleteMutation.isPending}
      />

      <ConfirmDialog
        open={!!deleteTriggerConfirm}
        onClose={() => setDeleteTriggerConfirm(null)}
        onConfirm={() => { deleteTriggerMutation.mutate(deleteTriggerConfirm!); setDeleteTriggerConfirm(null) }}
        title="Remove Trigger"
        message="Remove this trigger? Make sure it's not referenced in the condition tree."
        confirmLabel="Remove"
        danger
      />

      {/* Webhook fire modal */}
      <Modal open={!!webhookModal} onClose={() => setWebhookModal(null)} title="Fire Webhook" size="sm">
        <div className="space-y-4">
          <p className="text-sm text-slate-400">Manually fire the webhook trigger <strong className="text-white">{webhookModal}</strong>.</p>
          <div>
            <label className="label">Webhook Secret (optional)</label>
            <input
              type="password"
              value={webhookSecret}
              onChange={e => setWebhookSecret(e.target.value)}
              className="input"
              placeholder="Leave blank if no secret required"
            />
          </div>
          <div className="flex justify-end gap-2">
            <button onClick={() => setWebhookModal(null)} className="btn-secondary">Cancel</button>
            <button
              onClick={() => fireWebhookMutation.mutate(webhookSecret)}
              className="btn-primary"
              disabled={fireWebhookMutation.isPending}
            >
              <Webhook size={14} /> {fireWebhookMutation.isPending ? 'Firing…' : 'Fire'}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  )
}
