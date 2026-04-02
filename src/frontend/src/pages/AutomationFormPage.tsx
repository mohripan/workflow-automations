import { useState, useEffect } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQuery, useMutation } from '@tanstack/react-query'
import { ArrowLeft, ArrowRight, Check } from 'lucide-react'
import { automationsApi } from '../api/automations'
import { taskTypesApi } from '../api/taskTypes'
import { hostGroupsApi } from '../api/hostGroups'
import { TriggerConfigForm } from '../components/automation/TriggerConfigForm'
import { ConditionTreeBuilder } from '../components/automation/ConditionTreeBuilder'
import { CodeEditor } from '../components/ui/CodeEditor'
import { PageLoader } from '../components/ui/States'
import { triggersApi } from '../api/triggers'
import toast from 'react-hot-toast'
import type { TriggerConditionNode, CreateTriggerRequest } from '../types'

const STEPS = ['Basic', 'Task', 'Triggers', 'Condition', 'Advanced']

function StepIndicator({ step, total }: { step: number; total: number }) {
  return (
    <div className="flex items-center gap-1">
      {Array.from({ length: total }).map((_, i) => (
        <div key={i} className={`h-1.5 flex-1 rounded-full transition-all ${i <= step ? 'bg-indigo-500' : 'bg-slate-700'}`} />
      ))}
    </div>
  )
}

export default function AutomationFormPage() {
  const { id } = useParams<{ id?: string }>()
  const isEdit = !!id
  const navigate = useNavigate()

  const [step, setStep] = useState(0)

  // Form state
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [hostGroupId, setHostGroupId] = useState('')
  const [isEnabled, setIsEnabled] = useState(true)
  const [taskId, setTaskId] = useState('')
  const [taskConfigRaw, setTaskConfigRaw] = useState('{}')
  const [timeoutSeconds, setTimeoutSeconds] = useState('')
  const [maxRetries, setMaxRetries] = useState('0')

  // Triggers local state
  const [triggers, setTriggers] = useState<(CreateTriggerRequest & { _id: string })[]>([])
  const [showAddTrigger, setShowAddTrigger] = useState(false)
  const [newTrigger, setNewTrigger] = useState({ name: '', typeId: '', config: {} as Record<string, string> })

  // Condition tree
  const [conditionRoot, setConditionRoot] = useState<TriggerConditionNode>({
    operator: null, triggerName: '', nodes: null,
  })

  const { data: taskTypes } = useQuery({ queryKey: ['task-types'], queryFn: taskTypesApi.list })
  const { data: hostGroups } = useQuery({ queryKey: ['host-groups'], queryFn: hostGroupsApi.list })
  const { data: triggerTypes } = useQuery({ queryKey: ['trigger-types'], queryFn: triggersApi.getTypes })

  const { data: existing, isLoading: loadingExisting } = useQuery({
    queryKey: ['automation', id],
    queryFn: () => automationsApi.get(id!),
    enabled: isEdit,
  })

  useEffect(() => {
    if (!existing) return
    setName(existing.name)
    setDescription(existing.description ?? '')
    setHostGroupId(existing.hostGroupId)
    setIsEnabled(existing.isEnabled)
    setTaskId(existing.taskId)
    setTaskConfigRaw(existing.taskConfig ? JSON.stringify(JSON.parse(existing.taskConfig), null, 2) : '{}')
    setTimeoutSeconds(existing.timeoutSeconds?.toString() ?? '')
    setMaxRetries(existing.maxRetries?.toString() ?? '0')
    const existingTriggers = existing.triggers.map(t => ({
      _id: t.id,
      name: t.name,
      typeId: t.typeId,
      configJson: t.configJson,
    }))
    setTriggers(existingTriggers)
    setConditionRoot(
      typeof existing.triggerCondition === 'string'
        ? JSON.parse(existing.triggerCondition)
        : existing.triggerCondition
    )
  }, [existing])

  const saveMutation = useMutation({
    mutationFn: async () => {
      const triggerRequests: CreateTriggerRequest[] = triggers.map(t => ({
        name: t.name,
        typeId: t.typeId,
        configJson: t.configJson,
      }))

      // Only send triggerCondition when there's a real condition; else send a no-op leaf
      const hasCondition = conditionRoot.triggerName || conditionRoot.operator
      const triggerCondition: TriggerConditionNode = hasCondition
        ? conditionRoot
        : { operator: null, triggerName: '', nodes: null }

      const payload = {
        name, description: description || undefined,
        hostGroupId, taskId,
        triggers: triggerRequests,
        triggerCondition,
        timeoutSeconds: timeoutSeconds ? parseInt(timeoutSeconds) : undefined,
        maxRetries: parseInt(maxRetries) || 0,
        taskConfig: taskConfigRaw !== '{}' ? taskConfigRaw : undefined,
      }

      if (isEdit) {
        const updated = await automationsApi.update(id!, payload)
        // Sync enabled state if changed
        if (existing?.isEnabled !== isEnabled) {
          isEnabled ? await automationsApi.enable(id!) : await automationsApi.disable(id!)
        }
        return updated
      } else {
        const created = await automationsApi.create(payload)
        if (isEnabled) await automationsApi.enable(created.id)
        return created
      }
    },
    onSuccess: (a) => {
      toast.success(isEdit ? 'Automation updated!' : 'Automation created!')
      navigate(`/automations/${a.id}`)
    },
    onError: (e: Error) => toast.error(e.message),
  })

  if (isEdit && loadingExisting) return <PageLoader />

  const triggerNames = triggers.map(t => t.name).filter(Boolean)

  return (
    <div className="p-6 max-w-2xl mx-auto space-y-6">
      <div className="flex items-center gap-3">
        <button onClick={() => navigate(isEdit ? `/automations/${id}` : '/automations')} className="btn-ghost">
          <ArrowLeft size={15} />
        </button>
        <div>
          <h1 className="text-xl font-bold text-white">{isEdit ? 'Edit' : 'Create'} Automation</h1>
          <p className="text-slate-500 text-xs mt-0.5">Step {step + 1} of {STEPS.length}: {STEPS[step]}</p>
        </div>
      </div>

      <StepIndicator step={step} total={STEPS.length} />

      {/* Step 0: Basic */}
      {step === 0 && (
        <div className="card p-5 space-y-4">
          <div>
            <label className="label">Name <span className="text-red-400">*</span></label>
            <input value={name} onChange={e => setName(e.target.value)} className="input" placeholder="My Automation" />
          </div>
          <div>
            <label className="label">Description</label>
            <textarea value={description} onChange={e => setDescription(e.target.value)} className="input resize-none" rows={2} placeholder="Optional description…" />
          </div>
          <div>
            <label className="label">Host Group <span className="text-red-400">*</span></label>
            <select value={hostGroupId} onChange={e => setHostGroupId(e.target.value)} className="input">
              <option value="">Select host group…</option>
              {hostGroups?.map(g => <option key={g.id} value={g.id}>{g.name} ({g.connectionId})</option>)}
            </select>
          </div>
          <label className="flex items-center gap-2 cursor-pointer">
            <input type="checkbox" checked={isEnabled} onChange={e => setIsEnabled(e.target.checked)} className="rounded border-slate-600 bg-slate-900 text-indigo-500" />
            <span className="text-sm text-slate-300">Enable automation immediately</span>
          </label>
        </div>
      )}

      {/* Step 1: Task */}
      {step === 1 && (
        <div className="card p-5 space-y-4">
          <div>
            <label className="label">Task Type <span className="text-red-400">*</span></label>
            <select value={taskId} onChange={e => setTaskId(e.target.value)} className="input">
              <option value="">Select task type…</option>
              {taskTypes?.map(t => <option key={t.taskId} value={t.taskId}>{t.displayName} ({t.taskId})</option>)}
            </select>
          </div>
          {taskId && (
            <div>
              <label className="label">Task Config (JSON)</label>
              <p className="text-xs text-slate-500 mb-2">Parameters passed to the task handler. Leave as <code>{'{}'}</code> if not needed.</p>
              <CodeEditor value={taskConfigRaw} onChange={setTaskConfigRaw} height="200px" />
            </div>
          )}
        </div>
      )}

      {/* Step 2: Triggers */}
      {step === 2 && (
        <div className="space-y-3">
          <div className="card overflow-hidden">
            {triggers.length === 0 ? (
              <p className="text-slate-500 text-sm text-center py-8">No triggers added yet.</p>
            ) : (
              <div className="divide-y divide-slate-700/50">
                {triggers.map((t, i) => (
                  <div key={t._id} className="flex items-center gap-3 px-4 py-3">
                    <div className="flex-1">
                      <p className="text-sm font-medium text-slate-200">{t.name}</p>
                      <p className="text-xs text-slate-500">{t.typeId}</p>
                    </div>
                    <button
                      onClick={() => setTriggers(ts => ts.filter((_, j) => j !== i))}
                      className="text-slate-500 hover:text-red-400 transition-colors"
                    >
                      ✕
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          {showAddTrigger ? (
            <div className="card p-4 space-y-4">
              <div>
                <label className="label">Trigger Name <span className="text-red-400">*</span></label>
                <input
                  value={newTrigger.name}
                  onChange={e => setNewTrigger(n => ({ ...n, name: e.target.value }))}
                  className="input" placeholder="e.g. daily-schedule"
                />
              </div>
              <div>
                <label className="label">Type <span className="text-red-400">*</span></label>
                <select
                  value={newTrigger.typeId}
                  onChange={e => setNewTrigger(n => ({ ...n, typeId: e.target.value, config: {} }))}
                  className="input"
                >
                  <option value="">Select type…</option>
                  {triggerTypes?.map(t => <option key={t.typeId} value={t.typeId}>{t.displayName}</option>)}
                </select>
              </div>
              {newTrigger.typeId && (
                <TriggerConfigForm
                  typeId={newTrigger.typeId}
                  value={newTrigger.config}
                  onChange={config => setNewTrigger(n => ({ ...n, config }))}
                />
              )}
              <div className="flex gap-2">
                <button onClick={() => setShowAddTrigger(false)} className="btn-secondary">Cancel</button>
                <button
                  onClick={() => {
                    if (!newTrigger.name || !newTrigger.typeId) return
                    setTriggers(ts => [...ts, {
                      _id: crypto.randomUUID(),
                      name: newTrigger.name,
                      typeId: newTrigger.typeId,
                      configJson: JSON.stringify(newTrigger.config),
                    }])
                    setNewTrigger({ name: '', typeId: '', config: {} })
                    setShowAddTrigger(false)
                  }}
                  className="btn-primary"
                  disabled={!newTrigger.name || !newTrigger.typeId}
                >
                  Add Trigger
                </button>
              </div>
            </div>
          ) : (
            <button onClick={() => setShowAddTrigger(true)} className="btn-secondary w-full justify-center">
              + Add Trigger
            </button>
          )}
        </div>
      )}

      {/* Step 3: Condition */}
      {step === 3 && (
        <div className="card p-5 space-y-4">
          <p className="text-sm text-slate-400">Define when this automation should fire using AND/OR logic.</p>
          {triggerNames.length === 0 ? (
            <p className="text-yellow-400 text-sm">⚠ Add triggers first (step 3) before building the condition.</p>
          ) : (
            <ConditionTreeBuilder
              node={conditionRoot}
              onChange={setConditionRoot}
              triggerNames={triggerNames}
            />
          )}
        </div>
      )}

      {/* Step 4: Advanced */}
      {step === 4 && (
        <div className="card p-5 space-y-4">
          <div>
            <label className="label">Timeout (seconds)</label>
            <input
              type="number" min="1"
              value={timeoutSeconds}
              onChange={e => setTimeoutSeconds(e.target.value)}
              className="input"
              placeholder="Leave blank for no timeout"
            />
          </div>
          <div>
            <label className="label">Max Retries</label>
            <input
              type="number" min="0" max="10"
              value={maxRetries}
              onChange={e => setMaxRetries(e.target.value)}
              className="input"
            />
            <p className="text-xs text-slate-500 mt-1">0 = no retry on failure</p>
          </div>
          <div className="pt-2 border-t border-slate-700">
            <p className="text-xs text-slate-500 font-medium mb-2">Review</p>
            <div className="text-xs text-slate-400 space-y-1">
              <p>Name: <span className="text-white">{name || '—'}</span></p>
              <p>Task: <code className="text-indigo-400">{taskId || '—'}</code></p>
              <p>Triggers: <span className="text-white">{triggers.length}</span></p>
              <p>Enabled: <span className="text-white">{isEnabled ? 'Yes' : 'No'}</span></p>
            </div>
          </div>
        </div>
      )}

      {/* Nav */}
      <div className="flex justify-between">
        <button onClick={() => setStep(s => s - 1)} disabled={step === 0} className="btn-secondary">
          <ArrowLeft size={15} /> Back
        </button>
        {step < STEPS.length - 1 ? (
          <button
            onClick={() => setStep(s => s + 1)}
            disabled={step === 0 && (!name || !hostGroupId)}
            className="btn-primary"
          >
            Next <ArrowRight size={15} />
          </button>
        ) : (
          <button
            onClick={() => saveMutation.mutate()}
            disabled={saveMutation.isPending || !name || !taskId || !hostGroupId}
            className="btn-primary"
          >
            <Check size={15} />
            {saveMutation.isPending ? 'Saving…' : (isEdit ? 'Save Changes' : 'Create Automation')}
          </button>
        )}
      </div>
    </div>
  )
}
