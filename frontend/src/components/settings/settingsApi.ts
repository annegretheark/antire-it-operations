import { useCallback, useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from '../../apiClient'

type ResourceState<T> = { data: T | null; loading: boolean; error: string }

// Henter JSON fra API-et; endre reloadKey for å hente på nytt. path = null henter ingenting.
export function useResource<T>(path: string | null, reloadKey = 0): ResourceState<T> {
  const getAuthHeaders = useAuthHeaders()
  const [state, setState] = useState<ResourceState<T>>({ data: null, loading: path !== null, error: '' })

  useEffect(() => {
    let cancelled = false

    async function load(path: string) {
      try {
        const response = await fetch(apiUrl(path), { headers: await getAuthHeaders() })

        if (!response.ok) {
          // Backend kan forklare feilen, f.eks. { message: "Appen mangler rettigheten …" }
          const body = await response.json().catch(() => null)
          throw new Error(typeof body?.message === 'string' ? body.message : '')
        }

        const data: T = await response.json()
        if (!cancelled) setState({ data, loading: false, error: '' })
      } catch (e) {
        const message = e instanceof Error && e.message ? e.message : 'Kunne ikke hente data.'
        if (!cancelled) setState({ data: null, loading: false, error: message })
      }
    }

    if (path !== null) void load(path)

    return () => {
      cancelled = true
    }
  }, [path, reloadKey, getAuthHeaders])

  return state
}

type ListState<T> = { items: T[]; loading: boolean; error: string }

// Henter en liste fra API-et; endre reloadKey for å hente på nytt
export function useList<T>(path: string | null, reloadKey = 0): ListState<T> {
  const { data, loading, error } = useResource<T[]>(path, reloadKey)
  return { items: data ?? [], loading, error }
}

// Sender en endring til API-et. Kaster en feil med backendens melding
// (f.eks. «Navn og kategori er påkrevd.») når svaret ikke er OK.
export function useSend() {
  const getAuthHeaders = useAuthHeaders()

  return useCallback(
    async (method: string, path: string, body?: unknown): Promise<Response> => {
      const response = await fetch(apiUrl(path), {
        method,
        headers: await getAuthHeaders(),
        body: body === undefined ? undefined : JSON.stringify(body),
      })

      if (!response.ok) {
        const text = await response.text().catch(() => '')
        throw new Error(readableError(text) ?? 'Lagringen feilet. Prøv igjen.')
      }

      return response
    },
    [getAuthHeaders]
  )
}

// Feilmelding fra backend: ren tekst («Navn er påkrevd.») eller { message: "…" }
function readableError(text: string): string | null {
  if (!text || text.length > 500) return null

  if (text.startsWith('{')) {
    try {
      const body = JSON.parse(text)
      return typeof body?.message === 'string' ? body.message : null
    } catch {
      return null
    }
  }

  return text
}

export function errorText(error: unknown): string {
  return error instanceof Error ? error.message : 'Noe gikk galt.'
}

export type EntraGroup = { id: string; displayName: string | null }

// Grupper fra Entra ID (Helpdesk.Admin), til forslag i skjemaer. Tom liste hvis
// Graph ikke svarer; da kan gruppe-ID-en fortsatt limes inn.
export function useEntraGroups() {
  const groups = useList<EntraGroup>('/api/m365/groups')
  const names = new Map(groups.items.map((g) => [g.id.toLowerCase(), g.displayName ?? g.id]))
  return { groups: groups.items, nameOf: (id: string) => names.get(id.toLowerCase()) }
}
