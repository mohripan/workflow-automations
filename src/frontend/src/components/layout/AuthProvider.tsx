import React, { createContext, useEffect, useState } from 'react'
import keycloak from '../../keycloak'

interface AuthCtx {
  isReady: boolean
  isAuthenticated: boolean
  token: string | undefined
  userName: string
  roles: string[]
  hasRole: (role: string) => boolean
  logout: () => void
}

export const AuthContext = createContext<AuthCtx>({
  isReady: false, isAuthenticated: false, token: undefined,
  userName: '', roles: [], hasRole: () => false, logout: () => {},
})

const DEV_MODE = import.meta.env.VITE_DEV_MODE === 'true'

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [isReady, setIsReady] = useState(DEV_MODE)
  const [isAuthenticated, setIsAuthenticated] = useState(DEV_MODE)
  const [token, setToken] = useState<string | undefined>(DEV_MODE ? 'dev-token' : undefined)
  const [roles, setRoles] = useState<string[]>(DEV_MODE ? ['admin'] : [])
  const [userName, setUserName] = useState(DEV_MODE ? 'dev-admin' : '')

  useEffect(() => {
    if (DEV_MODE) return
    keycloak
      .init({ onLoad: 'login-required', pkceMethod: 'S256', checkLoginIframe: false })
      .then((auth) => {
        setIsAuthenticated(auth)
        setToken(keycloak.token)
        const realmRoles = (keycloak.tokenParsed as { realm_access?: { roles?: string[] } })
          ?.realm_access?.roles ?? []
        setRoles(realmRoles)
        setUserName(keycloak.tokenParsed?.preferred_username ?? '')
        setIsReady(true)

        // Token refresh
        setInterval(() => {
          keycloak.updateToken(30).then((refreshed) => {
            if (refreshed) setToken(keycloak.token)
          }).catch(() => keycloak.login())
        }, 20_000)
      })
      .catch(() => keycloak.login())
  }, [])

  const value: AuthCtx = {
    isReady, isAuthenticated, token, userName, roles,
    hasRole: (r) => roles.includes(r),
    logout: () => keycloak.logout({ redirectUri: window.location.origin }),
  }

  if (!isReady) {
    return (
      <div className="min-h-screen bg-slate-900 flex items-center justify-center">
        <div className="flex flex-col items-center gap-4">
          <div className="w-10 h-10 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin" />
          <p className="text-slate-400 text-sm">Connecting to FlowForge…</p>
        </div>
      </div>
    )
  }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
