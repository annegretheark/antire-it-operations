import type { Configuration } from '@azure/msal-browser'

export const msalConfig: Configuration = {
  auth: {
    clientId: '73962775-c92f-4113-872b-234035c282cf',
    authority:
      'https://login.microsoftonline.com/3af5c92b-38f9-48ce-8746-82bb4c113032',
    redirectUri: 'http://localhost:5173/auth-popup.html',
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