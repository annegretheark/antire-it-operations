// IT-utstyr: typer og norske visningsnavn for kodene fra /api/assets

export type Asset = {
  id: number
  assetTag: string
  type: string
  manufacturer: string | null
  model: string | null
  serialNumber: string | null
  status: string
  assignedToEmployeeId: number | null
  assignedToName: string | null
  purchaseDate: string | null
  warrantyEndsAt: string | null
  source: string
  notes: string | null
}

export type AssetTicket = {
  id: number
  category: string
  description: string
  status: string
  priority: string
  createdAt: string
}

export type AssetImportResult = {
  created: number
  updated: number
  errors: string[]
}

export const ASSET_TYPE_LABELS: Record<string, string> = {
  laptop: 'Bærbar PC',
  desktop: 'Stasjonær PC',
  mobile: 'Mobil',
  monitor: 'Skjerm',
  other: 'Annet',
}

export const ASSET_STATUS_LABELS: Record<string, string> = {
  in_stock: 'På lager',
  in_use: 'I bruk',
  repair: 'Til reparasjon',
  retired: 'Kassert',
}

// «AB-123 · Dell Latitude»
export function assetName(asset: Pick<Asset, 'assetTag' | 'manufacturer' | 'model'>): string {
  const model = [asset.manufacturer, asset.model].filter(Boolean).join(' ')
  return model ? `${asset.assetTag} · ${model}` : asset.assetTag
}

export function formatDate(value: string | null): string {
  return value ? new Date(value).toLocaleDateString('nb-NO') : '–'
}
