import { useQuery } from '@tanstack/react-query'
import { triggersApi } from '../../api/triggers'
import type { ConfigField } from '../../types'
import { CodeEditor } from '../ui/CodeEditor'

interface Props {
  typeId: string
  value: Record<string, string>
  onChange: (v: Record<string, string>) => void
  disabled?: boolean
}

function FieldInput({
  field, value, onChange, disabled,
}: { field: ConfigField; value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const { dataType, label, description, required, defaultValue, enumValues } = field

  if (dataType === 'Script' || dataType === 'MultilineString') {
    return (
      <div>
        <label className="label">{label}{required && <span className="text-red-400 ml-1">*</span>}</label>
        {description && <p className="text-xs text-slate-500 mb-1">{description}</p>}
        <CodeEditor
          value={value || defaultValue || ''}
          language={dataType === 'Script' ? 'python' : 'plaintext'}
          height="160px"
          onChange={onChange}
          readOnly={disabled}
        />
      </div>
    )
  }

  if (dataType === 'Bool') {
    return (
      <label className="flex items-center gap-2 cursor-pointer">
        <input
          type="checkbox"
          checked={value === 'true'}
          onChange={(e) => onChange(String(e.target.checked))}
          disabled={disabled}
          className="rounded border-slate-600 bg-slate-900 text-indigo-500"
        />
        <span className="text-sm text-slate-300">{label}</span>
        {description && <span className="text-xs text-slate-500">— {description}</span>}
      </label>
    )
  }

  if (dataType === 'Enum' && enumValues?.length) {
    return (
      <div>
        <label className="label">{label}{required && <span className="text-red-400 ml-1">*</span>}</label>
        <select
          value={value || defaultValue || ''}
          onChange={(e) => onChange(e.target.value)}
          disabled={disabled}
          className="input"
        >
          <option value="">Select…</option>
          {enumValues.map(v => <option key={v} value={v}>{v}</option>)}
        </select>
      </div>
    )
  }

  // String, Int, CronExpression
  return (
    <div>
      <label className="label">
        {label}{required && <span className="text-red-400 ml-1">*</span>}
      </label>
      {description && <p className="text-xs text-slate-500 mb-1">{description}</p>}
      {dataType === 'CronExpression' && (
        <p className="text-xs text-slate-500 mb-1">Example: <code className="text-indigo-400">0 0 9 * * ?</code> (daily at 09:00)</p>
      )}
      <input
        type={dataType === 'Int' ? 'number' : 'text'}
        value={value === '***' ? '' : (value || defaultValue || '')}
        placeholder={value === '***' ? '(encrypted — leave blank to keep)' : ''}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled || value === '***'}
        className="input"
      />
    </div>
  )
}

export function TriggerConfigForm({ typeId, value, onChange, disabled }: Props) {
  const { data: schema, isLoading } = useQuery({
    queryKey: ['trigger-type', typeId],
    queryFn: () => triggersApi.getType(typeId),
    enabled: !!typeId,
  })

  if (isLoading) return <p className="text-slate-500 text-sm">Loading schema…</p>
  if (!schema) return null

  return (
    <div className="space-y-4">
      {schema.fields.map((field) => (
        <FieldInput
          key={field.name}
          field={field}
          value={value[field.name] ?? ''}
          onChange={(v) => onChange({ ...value, [field.name]: v })}
          disabled={disabled}
        />
      ))}
    </div>
  )
}
