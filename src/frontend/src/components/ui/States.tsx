import { Loader2 } from 'lucide-react'

export function Spinner({ size = 20 }: { size?: number }) {
  return <Loader2 size={size} className="animate-spin text-indigo-400" />
}

export function PageLoader() {
  return (
    <div className="flex items-center justify-center py-24">
      <div className="flex flex-col items-center gap-3">
        <Spinner size={28} />
        <p className="text-slate-500 text-sm">Loading…</p>
      </div>
    </div>
  )
}

export function ErrorState({ message, onRetry }: { message?: string; onRetry?: () => void }) {
  return (
    <div className="flex flex-col items-center gap-3 py-16">
      <p className="text-red-400 text-sm">{message ?? 'Something went wrong.'}</p>
      {onRetry && <button onClick={onRetry} className="btn-secondary text-xs">Retry</button>}
    </div>
  )
}

export function EmptyState({ message }: { message: string }) {
  return (
    <div className="flex items-center justify-center py-16">
      <p className="text-slate-500 text-sm">{message}</p>
    </div>
  )
}
