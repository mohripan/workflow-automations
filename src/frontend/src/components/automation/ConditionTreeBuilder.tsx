import { Plus, X, ChevronsUpDown } from 'lucide-react'
import type { TriggerConditionNode } from '../../types'

interface Props {
  node: TriggerConditionNode
  onChange: (node: TriggerConditionNode) => void
  triggerNames: string[]
  depth?: number
}

function makeLeaf(name: string): TriggerConditionNode {
  return { operator: null, triggerName: name, nodes: null }
}

function makeComposite(op: 'And' | 'Or'): TriggerConditionNode {
  return { operator: op, triggerName: null, nodes: [makeLeaf(''), makeLeaf('')] }
}

export function ConditionTreeBuilder({ node, onChange, triggerNames, depth = 0 }: Props) {
  const isLeaf = node.operator === null

  if (isLeaf) {
    return (
      <div className="flex items-center gap-2">
        <div className="w-2 h-2 rounded-full bg-indigo-400 flex-shrink-0" />
        <select
          value={node.triggerName ?? ''}
          onChange={(e) => onChange({ ...node, triggerName: e.target.value })}
          className="input flex-1 py-1 text-xs"
        >
          <option value="">— select trigger —</option>
          {triggerNames.map(n => <option key={n} value={n}>{n}</option>)}
        </select>
      </div>
    )
  }

  // Composite node
  return (
    <div className={`border border-slate-700 rounded-lg p-3 space-y-2 ${depth > 0 ? 'ml-4' : ''}`}>
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={() => onChange({ ...node, operator: node.operator === 'And' ? 'Or' : 'And' })}
          className="flex items-center gap-1 px-2 py-0.5 rounded bg-slate-700 hover:bg-slate-600 text-xs font-bold text-indigo-300 transition-colors"
        >
          {node.operator} <ChevronsUpDown size={10} />
        </button>
        <span className="text-xs text-slate-500">of the following conditions are true</span>
      </div>

      <div className="space-y-2 pl-2">
        {(node.nodes ?? []).map((child, i) => (
          <div key={i} className="flex items-start gap-2">
            <div className="flex-1">
              <ConditionTreeBuilder
                node={child}
                onChange={(updated) => {
                  const nodes = [...(node.nodes ?? [])]
                  nodes[i] = updated
                  onChange({ ...node, nodes })
                }}
                triggerNames={triggerNames}
                depth={depth + 1}
              />
            </div>
            <button
              type="button"
              onClick={() => {
                const nodes = (node.nodes ?? []).filter((_, j) => j !== i)
                onChange({ ...node, nodes })
              }}
              className="text-slate-600 hover:text-red-400 transition-colors flex-shrink-0 mt-1"
            >
              <X size={14} />
            </button>
          </div>
        ))}
      </div>

      <div className="flex gap-2 pt-1">
        <button
          type="button"
          onClick={() => onChange({ ...node, nodes: [...(node.nodes ?? []), makeLeaf('')] })}
          className="flex items-center gap-1 text-xs text-slate-400 hover:text-white transition-colors"
        >
          <Plus size={12} /> Add trigger
        </button>
        <button
          type="button"
          onClick={() => onChange({ ...node, nodes: [...(node.nodes ?? []), makeComposite('And')] })}
          className="flex items-center gap-1 text-xs text-slate-400 hover:text-white transition-colors"
        >
          <Plus size={12} /> Add group
        </button>
      </div>
    </div>
  )
}

export function ConditionTreeDisplay({ node, depth = 0 }: { node: TriggerConditionNode; depth?: number }) {
  if (!node) return null
  if (node.operator === null) {
    return (
      <span className="inline-flex items-center gap-1.5 px-2 py-0.5 bg-indigo-500/20 text-indigo-300 rounded text-xs border border-indigo-500/30">
        {node.triggerName || '(unnamed)'}
      </span>
    )
  }
  return (
    <div className={`${depth > 0 ? 'ml-4' : ''} space-y-1`}>
      <span className="text-xs font-bold text-amber-400">{node.operator}</span>
      <div className="border-l-2 border-slate-700 pl-3 space-y-1">
        {(node.nodes ?? []).map((child, i) => (
          <ConditionTreeDisplay key={i} node={child} depth={depth + 1} />
        ))}
      </div>
    </div>
  )
}
