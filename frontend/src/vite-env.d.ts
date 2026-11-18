/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the API. Empty = same origin (Vite proxy in dev, reverse proxy in production). */
  readonly VITE_API_BASE_URL?: string;
  /** Public name shown in the UI. */
  readonly VITE_APP_NAME?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
