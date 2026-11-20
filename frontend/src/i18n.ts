import { useSyncExternalStore } from 'react'
import { en } from './i18n/en'

// Enkel oversettelse uten bibliotek: norsk tekst er nøkkelen, og den engelske
// ordboken (src/i18n/en) oversetter. Mangler en oversettelse, vises norsk tekst.
//
//   t('Lagre')                          -> «Save» på engelsk
//   t('Tenant: {name}', { name })       -> {name} byttes ut med verdien

export type Language = 'no' | 'en'

const STORAGE_KEY = 'language'

function initialLanguage(): Language {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'no' || saved === 'en') return saved
  } catch {
    // localStorage kan være blokkert; da brukes norsk
  }
  return 'no'
}

let current: Language = initialLanguage()
const listeners = new Set<() => void>()

document.documentElement.lang = current === 'en' ? 'en' : 'nb'

export function getLanguage(): Language {
  return current
}

export function setLanguage(language: Language): void {
  if (language === current) return
  current = language
  document.documentElement.lang = language === 'en' ? 'en' : 'nb'
  try {
    localStorage.setItem(STORAGE_KEY, language)
  } catch {
    // ignorer: valget gjelder da bare til siden lastes på nytt
  }
  listeners.forEach((listener) => listener())
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

// Språket som gjelder nå; komponenten tegnes på nytt når språket endres
export function useLanguage(): Language {
  return useSyncExternalStore(subscribe, getLanguage)
}

export function t(text: string, vars?: Record<string, string | number>): string {
  const translated = current === 'en' ? (en[text] ?? text) : text
  if (!vars) return translated
  return translated.replace(/\{(\w+)\}/g, (match, key: string) =>
    key in vars ? String(vars[key]) : match
  )
}

// Locale for datoer og tall
export function locale(): string {
  return current === 'en' ? 'en-GB' : 'nb-NO'
}
