import { useContext } from 'react'
import { AuthContext } from '../components/layout/AuthProvider'

export function useAuth() {
  return useContext(AuthContext)
}
