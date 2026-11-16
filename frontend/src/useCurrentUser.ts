import { useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from './apiClient'

// Roller kommer fra API-tokenet, så vi spør backend i stedet for å lese ID-tokenet
export type CurrentUser = {
  objectId: string | null
  name: string | null
  // Tenanten brukeren er logget inn i; data vises bare for denne tenanten
  tenantId: string | null
  tenantName: string | null
  isHelpdeskAdmin: boolean
  isEmployeeAdmin: boolean
}

const NO_ROLES: CurrentUser = {
  objectId: null,
  name: null,
  tenantId: null,
  tenantName: null,
  isHelpdeskAdmin: false,
  isEmployeeAdmin: false,
}

export function useCurrentUser(): CurrentUser {
  const getAuthHeaders = useAuthHeaders()
  const [user, setUser] = useState<CurrentUser>(NO_ROLES)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const response = await fetch(apiUrl('/api/tickets/me'), {
          headers: await getAuthHeaders(),
        })

        if (!response.ok) return

        const data: CurrentUser = await response.json()

        if (!cancelled) setUser(data)
      } catch {
        // Uten innlogging eller svar fra backend vises ingen admin-funksjoner
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [getAuthHeaders])

  return user
}
