// Only VITE_-prefixed variables reach the browser bundle. Never put secrets here.
export const env = {
  apiBaseUrl: (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/+$/, ''),
  appName: import.meta.env.VITE_APP_NAME ?? 'Afno Parcel',
} as const;
