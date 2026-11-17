import { useCallback, useEffect, useState } from 'react'
import { apiUrl, useAuthHeaders } from '../../apiClient'

type ResourceState<T> = { data: T | null; loading: boolean; error: string }

// Henter JSON fra API-et; endre reloadKey for å hente på nytt
export function useResource<T>(path: string, reloadKey = 0): ResourceState<T> {
  const getAuthHeaders = useAuthHeaders()
  const [state, setState] = useState<ResourceState<T>>({ data: null, loading: true, error: '' })

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const response = await fetch(apiUrl(path), { headers: await getAuthHeaders() })
        if (!response.ok) throw new Error()

        const data: T = await response.json()
        if (!cancelled) setState({ data, loading: false, error: '' })
      } catch {
        if (!cancelled) setState({ data: null, loading: false, error: 'Kunne ikke hente data.' })
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [path, reloadKey, getAuthHeaders])

  return state
}

type ListState<T> = { items: T[]; loading: boolean; error: string }

// Henter en liste fra API-et; endre reloadKey for å hente på nytt
export function useList<T>(path: string, reloadKey = 0): ListState<T> {
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
        throw new Error(
          text && text.length < 200 && !text.startsWith('{')
            ? text
            : 'Lagringen feilet. Prøv igjen.'
        )
      }

      return response
    },
    [getAuthHeaders]
  )
}

export function errorText(error: unknown): string {
  return error instanceof Error ? error.message : 'Noe gikk galt.'
}
