import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, RotateCcw, Trash2, ChevronDown } from 'lucide-react'
import { dlqApi } from '../api/dlq'
import { PageLoader, ErrorState, EmptyState } from '../components/ui/States'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { formatDistanceToNow } from 'date-fns'
import toast from 'react-hot-toast'

export default function DLQPage() {
  const qc = useQueryClient()
  const [replayId, setReplayId] = useState<string | null>(null)
  const [deleteId, setDeleteId] = useState<string | null>(null)
  const [expandedId, setExpandedId] = useState<string | null>(null)

  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['dlq'],
    queryFn: () => dlqApi.list(50),
    refetchInterval: 30_000,
  })

  const replayMutation = useMutation({
    mutationFn: (id: string) => dlqApi.replay(id),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['dlq'] }); toast.success('Message replayed'); setReplayId(null) },
    onError: () => toast.error('Replay failed'),
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => dlqApi.delete(id),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['dlq'] }); toast.success('Entry deleted'); setDeleteId(null) },
    onError: () => toast.error('Delete failed'),
  })

  if (isLoading) return <PageLoader />
  if (error) return <ErrorState message="Failed to load DLQ" onRetry={refetch} />

  return (
    <div className="p-6 space-y-5">
      <div>
        <h1 className="text-xl font-bold text-white">Dead Letter Queue</h1>
        <p className="text-slate-400 text-sm mt-0.5">Messages that failed to process after maximum retries</p>
      </div>

      {/* Warning */}
      <div className="flex gap-3 p-4 bg-amber-500/10 border border-amber-500/30 rounded-xl">
        <AlertTriangle size={18} className="text-amber-400 flex-shrink-0 mt-0.5" />
        <div>
          <p className="text-sm font-medium text-amber-300">Replay with care</p>
          <p className="text-xs text-amber-400/80 mt-0.5">
            Only replay after resolving the root cause. Replaying a failed message without a fix will cause it to fail again.
          </p>
        </div>
      </div>

      {(!data || data.length === 0) ? (
        <EmptyState message="Dead letter queue is empty. 🎉" />
      ) : (
        <div className="card overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-700">
                <th className="text-left px-5 py-3 text-xs text-slate-400 font-medium">Stream</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium">Error</th>
                <th className="text-left px-3 py-3 text-xs text-slate-400 font-medium hidden md:table-cell">When</th>
                <th className="px-3 py-3 text-xs text-slate-400 font-medium text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {data.map(entry => (
                <>
                  <tr key={entry.id} className="border-b border-slate-700/50 hover:bg-slate-700/20 transition-colors">
                    <td className="px-5 py-3">
                      <code className="text-xs text-indigo-400 bg-indigo-500/10 px-1.5 py-0.5 rounded">{entry.sourceStream}</code>
                    </td>
                    <td className="px-3 py-3">
                      <p className="text-xs text-red-300 truncate max-w-xs">{entry.error}</p>
                    </td>
                    <td className="px-3 py-3 text-xs text-slate-500 hidden md:table-cell">
                      {formatDistanceToNow(new Date(entry.createdAt), { addSuffix: true })}
                    </td>
                    <td className="px-3 py-3">
                      <div className="flex items-center justify-end gap-1">
                        <button
                          onClick={() => setExpandedId(expandedId === entry.id ? null : entry.id)}
                          className="btn-ghost text-xs p-1.5"
                          title="View payload"
                        >
                          <ChevronDown size={13} className={`transition-transform ${expandedId === entry.id ? 'rotate-180' : ''}`} />
                        </button>
                        <button
                          onClick={() => setReplayId(entry.id)}
                          className="btn-secondary text-xs py-1 px-2"
                          title="Replay"
                        >
                          <RotateCcw size={12} /> Replay
                        </button>
                        <button
                          onClick={() => setDeleteId(entry.id)}
                          className="btn-ghost text-xs p-1.5 hover:text-red-400"
                          title="Delete"
                        >
                          <Trash2 size={13} />
                        </button>
                      </div>
                    </td>
                  </tr>
                  {expandedId === entry.id && (
                    <tr className="border-b border-slate-700/50 bg-slate-900/50">
                      <td colSpan={4} className="px-5 py-3">
                        <p className="text-xs text-slate-400 mb-2 font-medium">Payload</p>
                        <pre className="text-xs text-slate-300 bg-slate-900 p-3 rounded-lg border border-slate-700 overflow-x-auto max-h-48">
                          {(() => { try { return JSON.stringify(JSON.parse(entry.payload), null, 2) } catch { return entry.payload } })()}
                        </pre>
                      </td>
                    </tr>
                  )}
                </>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={!!replayId}
        onClose={() => setReplayId(null)}
        onConfirm={() => replayMutation.mutate(replayId!)}
        title="Replay Message"
        message="Re-publish this message to its source stream. Make sure the underlying issue is resolved first."
        confirmLabel="Replay"
        loading={replayMutation.isPending}
      />

      <ConfirmDialog
        open={!!deleteId}
        onClose={() => setDeleteId(null)}
        onConfirm={() => deleteMutation.mutate(deleteId!)}
        title="Delete DLQ Entry"
        message="Permanently delete this DLQ entry. This cannot be undone."
        confirmLabel="Delete"
        danger
        loading={deleteMutation.isPending}
      />
    </div>
  )
}
