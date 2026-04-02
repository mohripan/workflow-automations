import { NavLink } from 'react-router-dom'
import {
  LayoutDashboard, Zap, Briefcase, Server, AlertTriangle, Cpu, X,
} from 'lucide-react'
import { useAuth } from '../../hooks/useAuth'
import clsx from 'clsx'

interface Props { open: boolean; onClose: () => void }

const nav = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/automations', label: 'Automations', icon: Zap },
  { to: '/jobs', label: 'Jobs', icon: Briefcase },
  { to: '/host-groups', label: 'Host Groups', icon: Server },
  { to: '/task-types', label: 'Task Types', icon: Cpu },
  { to: '/dlq', label: 'Dead Letter Queue', icon: AlertTriangle, adminOnly: true },
]

export function Sidebar({ open, onClose }: Props) {
  const { hasRole, userName, roles, logout } = useAuth()

  return (
    <>
      {/* Mobile overlay */}
      {open && (
        <div className="fixed inset-0 bg-black/50 z-20 lg:hidden" onClick={onClose} />
      )}

      <aside className={clsx(
        'fixed top-0 left-0 h-full w-60 bg-slate-900 border-r border-slate-800 z-30 flex flex-col',
        'transition-transform duration-200',
        open ? 'translate-x-0' : '-translate-x-full lg:translate-x-0',
      )}>
        {/* Logo */}
        <div className="flex items-center gap-2 px-4 py-4 border-b border-slate-800">
          <div className="w-8 h-8 bg-indigo-600 rounded-lg flex items-center justify-center">
            <Zap size={16} className="text-white" />
          </div>
          <span className="font-bold text-white tracking-tight">FlowForge</span>
          <button onClick={onClose} className="ml-auto lg:hidden text-slate-500 hover:text-white">
            <X size={16} />
          </button>
        </div>

        {/* Nav */}
        <nav className="flex-1 px-2 py-4 space-y-0.5 overflow-y-auto">
          {nav.filter(item => !item.adminOnly || hasRole('admin')).map(({ to, label, icon: Icon }) => (
            <NavLink
              key={to} to={to}
              onClick={onClose}
              className={({ isActive }) =>
                clsx('sidebar-link', isActive && 'active')
              }
            >
              <Icon size={16} />
              {label}
            </NavLink>
          ))}
        </nav>

        {/* User pill */}
        <div className="px-3 py-3 border-t border-slate-800">
          <div className="flex items-center gap-2 mb-2">
            <div className="w-7 h-7 rounded-full bg-indigo-600 flex items-center justify-center text-xs font-bold text-white uppercase">
              {userName?.[0] ?? '?'}
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-sm text-white font-medium truncate">{userName}</p>
              <p className="text-xs text-slate-500 truncate">{roles[0] ?? 'no role'}</p>
            </div>
          </div>
          <button onClick={logout} className="w-full text-left text-xs text-slate-500 hover:text-slate-300 transition-colors">
            Sign out
          </button>
        </div>
      </aside>
    </>
  )
}
