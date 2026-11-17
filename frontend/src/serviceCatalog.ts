import { useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from './apiClient'

// Tjenestekatalogen (GET /api/servicecatalog)

export type ServiceType = 'incident' | 'request' | 'access'

export type ServiceCatalogItem = {
  id: number
  name: string
  category: string
  description: string | null
  type: ServiceType
  defaultPriority: string
  requiresApproval: boolean
  entraGroupId: string | null
  isActive: boolean
}

export const SERVICE_TYPE_LABELS: Record<ServiceType, string> = {
  incident: 'Feil og hendelser',
  request: 'Bestillinger',
  access: 'Tilgang til system',
}

export const PRIORITIES = ['Lav', 'Normal', 'Høy', 'Kritisk']

type CatalogState = {
  items: ServiceCatalogItem[]
  loading: boolean
  error: string
}

// Henter aktive tjenester, f.eks. useServiceCatalog('type=access').
// reloadKey kan endres for å hente på nytt.
export function useServiceCatalog(query = '', reloadKey = 0): CatalogState {
  const getAuthHeaders = useAuthHeaders()
  const [state, setState] = useState<CatalogState>({ items: [], loading: true, error: '' })

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const response = await fetch(
          apiUrl(`/api/servicecatalog${query ? `?${query}` : ''}`),
          { headers: await getAuthHeaders() }
        )
        if (!response.ok) throw new Error()

        const items: ServiceCatalogItem[] = await response.json()
        if (!cancelled) setState({ items, loading: false, error: '' })
      } catch {
        if (!cancelled) {
          setState({ items: [], loading: false, error: 'Kunne ikke hente tjenestekatalogen.' })
        }
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [query, reloadKey, getAuthHeaders])

  return state
}
