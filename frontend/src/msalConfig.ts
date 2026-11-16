import type { Configuration } from '@azure/msal-browser'

export const msalConfig: Configuration = {
  auth: {
    clientId: '73962775-c92f-4113-872b-234035c282cf',
    // Én tenant: https://login.microsoftonline.com/<tenant-id>
    // Flere tenanter (f.eks. test og prod): https://login.microsoftonline.com/organizations
    // (krever at app-registreringene er satt til flere organisasjoner)
    authority:
      import.meta.env.VITE_AUTHORITY ??
      'https://login.microsoftonline.com/3af5c92b-38f9-48ce-8746-82bb4c113032',
    // Samme opprinnelse som appen kjører på (localhost i utvikling, egen URL i Azure)
    redirectUri: `${window.location.origin}/auth-popup.html`,
  },
  cache: {
    cacheLocation: 'sessionStorage',
  },
}

export const loginRequest = {
  scopes: [
    'api://e33710da-36b0-4e4b-9696-bb4fbdff647f/access_as_user',
  ],
}