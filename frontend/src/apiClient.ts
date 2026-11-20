import { useCallback } from 'react'
import { useMsal } from '@azure/msal-react'
import { loginRequest } from './msalConfig'
import { t } from './i18n'

// Backend-adresse fra miljøvariabel (se .env.example); localhost som standard i utvikling
export const API_BASE_URL = (
  import.meta.env.VITE_API_URL ?? 'http://localhost:5086'
).replace(/\/+$/, '')

// Bygger full URL til et API-endepunkt, f.eks. apiUrl('/api/tickets')
export function apiUrl(path: string): string {
  return `${API_BASE_URL}${path.startsWith('/') ? path : `/${path}`}`
}

// Felles henting av token for API-kall. Funksjonen er stabil mellom renderinger,
// så den kan trygt stå i avhengighetslisten til useEffect.
export function useAuthHeaders(): () => Promise<HeadersInit> {
  const { instance, accounts } = useMsal()

  return useCallback(async () => {
    const account = instance.getActiveAccount() ?? accounts[0]

    if (!account) {
      throw new Error(t('Du må logge inn med Microsoft.'))
    }

    const tokenResponse = await instance.acquireTokenSilent({
      ...loginRequest,
      account,
    })

    return {
      Authorization: `Bearer ${tokenResponse.accessToken}`,
      'Content-Type': 'application/json',
    }
  }, [instance, accounts])
}
