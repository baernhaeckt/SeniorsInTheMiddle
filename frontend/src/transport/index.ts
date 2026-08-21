import { createDemoTransport } from './demoTransport'
import { createWebSocketTransport } from './websocketTransport'
import type { Transport } from './types'

/**
 * The WebSocket wins whenever an endpoint is configured. `?source=demo` in the
 * URL forces the demo feed, which is how the demo is driven on stage.
 */
export function createTransport(): Transport {
  const forced = new URLSearchParams(window.location.search).get('source')
  const url = import.meta.env.VITE_PROXY_WS_URL?.trim()

  if (forced === 'demo') return createDemoTransport()
  if (forced === 'ws' || url) {
    if (!url) throw new Error('source=ws requires VITE_PROXY_WS_URL')
    return createWebSocketTransport(url)
  }
  return createDemoTransport()
}

export type { Transport, LinkStatus, LinkState } from './types'
