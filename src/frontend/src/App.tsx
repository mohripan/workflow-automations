import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Toaster } from 'react-hot-toast'
import { AuthProvider } from './components/layout/AuthProvider'
import { AppShell } from './components/layout/AppShell'
import Dashboard from './pages/Dashboard'
import AutomationsList from './pages/AutomationsList'
import AutomationDetail from './pages/AutomationDetail'
import AutomationFormPage from './pages/AutomationFormPage'
import JobsList from './pages/JobsList'
import JobDetail from './pages/JobDetail'
import HostGroupsPage from './pages/HostGroups'
import DLQPage from './pages/DLQPage'
import TaskTypesPage from './pages/TaskTypes'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 10_000 } },
})

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <BrowserRouter>
          <Toaster
            position="bottom-right"
            toastOptions={{
              style: { background: '#1e293b', color: '#f1f5f9', border: '1px solid #334155' },
              success: { iconTheme: { primary: '#22c55e', secondary: '#f1f5f9' } },
              error:   { iconTheme: { primary: '#ef4444', secondary: '#f1f5f9' } },
            }}
          />
          <Routes>
            <Route element={<AppShell />}>
              <Route index element={<Navigate to="/dashboard" replace />} />
              <Route path="dashboard" element={<Dashboard />} />
              <Route path="automations" element={<AutomationsList />} />
              <Route path="automations/new" element={<AutomationFormPage />} />
              <Route path="automations/:id" element={<AutomationDetail />} />
              <Route path="automations/:id/edit" element={<AutomationFormPage />} />
              <Route path="jobs" element={<JobsList />} />
              <Route path="jobs/:connectionId/:id" element={<JobDetail />} />
              <Route path="host-groups" element={<HostGroupsPage />} />
              <Route path="dlq" element={<DLQPage />} />
              <Route path="task-types" element={<TaskTypesPage />} />
              <Route path="*" element={<Navigate to="/dashboard" replace />} />
            </Route>
          </Routes>
        </BrowserRouter>
      </AuthProvider>
    </QueryClientProvider>
  )
}
