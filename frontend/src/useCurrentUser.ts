import { useEffect, useState } from 'react'
import { useMsal } from '@azure/msal-react'
import { loginRequest } from './msalConfig'

// Roller kommer fra API-tokenet, så vi spør backend i stedet for å lese ID-tokenet
export type CurrentUser = {
  objectId: string | null
  name: string | null
  isHelpdeskAdmin: boolean
  isEmployeeAdmin: boolean
}

const ME_URL = 'http://localhost:5086/api/tickets/me'

const NO_ROLES: CurrentUser = {
  objectId: null,
  name: null,
  isHelpdeskAdmin: false,
  isEmployeeAdmin: false,
}

export function useCurrentUser(): CurrentUser {
  const { instance, accounts } = useMsal()
  const [user, setUser] = useState<CurrentUser>(NO_ROLES)

  useEffect(() => {
    let cancelled = false

    async function load() {
      const account = instance.getActiveAccount() ?? accounts[0]
      if (!account) return

      try {
        const token = await instance.acquireTokenSilent({
          ...loginRequest,
          account,
        })

        const response = await fetch(ME_URL, {
          headers: { Authorization: `Bearer ${token.accessToken}` },
        })

        if (!response.ok) return

        const data: CurrentUser = await response.json()

        if (!cancelled) setUser(data)
      } catch {
        // Uten svar fra backend vises ingen admin-funksjoner
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [instance, accounts])

  return user
}
