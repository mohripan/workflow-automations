import MonacoEditor from '@monaco-editor/react'

interface Props {
  value: string
  language?: string
  height?: string
  onChange?: (v: string) => void
  readOnly?: boolean
}

export function CodeEditor({ value, language = 'json', height = '200px', onChange, readOnly = false }: Props) {
  return (
    <div className="rounded-lg overflow-hidden border border-slate-700">
      <MonacoEditor
        height={height}
        language={language}
        theme="vs-dark"
        value={value}
        onChange={(v) => onChange?.(v ?? '')}
        options={{
          readOnly,
          minimap: { enabled: false },
          fontSize: 13,
          lineNumbers: 'on',
          scrollBeyondLastLine: false,
          wordWrap: 'on',
          padding: { top: 8 },
          scrollbar: { vertical: 'auto', horizontal: 'auto' },
        }}
      />
    </div>
  )
}
