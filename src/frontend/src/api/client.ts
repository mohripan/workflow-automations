import axios from 'axios'
import keycloak from '../keycloak'

const DEV_MODE = import.meta.env.VITE_DEV_MODE === 'true'
const API_BASE = import.meta.env.VITE_API_URL ? `${import.meta.env.VITE_API_URL}/api` : '/api'

const client = axios.create({ baseURL: API_BASE })

client.interceptors.request.use(async (config) => {
  if (DEV_MODE) return config
  if (keycloak.isTokenExpired(30)) {
    try { await keycloak.updateToken(30) } catch { keycloak.login() }
  }
  if (keycloak.token) {
    config.headers.Authorization = `Bearer ${keycloak.token}`
  }
  return config
})

client.interceptors.response.use(
  (r) => r,
  (err) => {
    if (err.response?.status === 401 && !DEV_MODE) keycloak.login()
    return Promise.reject(err)
  },
)

export default client
