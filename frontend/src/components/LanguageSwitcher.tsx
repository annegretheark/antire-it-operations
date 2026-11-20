import { setLanguage, t, useLanguage, type Language } from '../i18n'

const OPTIONS: { value: Language; label: string }[] = [
  { value: 'no', label: 'Norsk' },
  { value: 'en', label: 'English' },
]

// Knapper for å bytte språk; valget huskes i nettleseren
export default function LanguageSwitcher() {
  const language = useLanguage()

  return (
    <div className="language-switcher" role="group" aria-label={t('Språk')}>
      {OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          className={language === option.value ? 'active' : ''}
          aria-pressed={language === option.value}
          onClick={() => setLanguage(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}
