/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_PROXY_WS_URL?: string
  readonly VITE_PROXY_HOST?: string
  readonly VITE_PROXY_PORT?: string
  readonly VITE_PROXY_NETWORK?: string
  readonly VITE_PROXY_CA_URL?: string
  readonly VITE_PROXY_PAC_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
