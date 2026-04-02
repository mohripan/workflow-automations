import { useEffect, useRef, useCallback } from 'react'
import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr'
import keycloak from '../keycloak'

const DEV_MODE = import.meta.env.VITE_DEV_MODE === 'true'
const HUB_URL = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/hubs/job-status`
  : '/hubs/job-status'

export function useJobSignalR(jobId: string, onUpdate: (update: unknown) => void) {
  const connRef = useRef<HubConnection | null>(null)
  const onUpdateRef = useRef(onUpdate)
  onUpdateRef.current = onUpdate

  const connect = useCallback(async () => {
    const builder = new HubConnectionBuilder()
      .withUrl(HUB_URL, DEV_MODE ? {} : { accessTokenFactory: () => keycloak.token ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    builder.on('OnJobStatusChanged', (update) => onUpdateRef.current(update))

    try {
      await builder.start()
      await builder.invoke('JoinJobGroup', jobId)
      connRef.current = builder
    } catch (e) {
      console.warn('SignalR connect failed', e)
    }
  }, [jobId])

  useEffect(() => {
    connect()
    return () => {
      connRef.current?.invoke('LeaveJobGroup', jobId).catch(() => {})
      connRef.current?.stop()
      connRef.current = null
    }
  }, [connect, jobId])
}
