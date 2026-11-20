// Engelsk ordbok: norsk tekst -> engelsk tekst. Én fil per område.
import { app } from './app'
import { helpdesk } from './helpdesk'
import { operations } from './operations'
import { directory } from './directory'
import { settings } from './settings'

export const en: Record<string, string> = {
  ...app,
  ...helpdesk,
  ...operations,
  ...directory,
  ...settings,
}
