import type { RuntimeConfig } from '../config'
import { createDemoTransport } from './demoTransport'
import { createSignalRTransport } from './signalrTransport'
import type { Transport } from './types'

export interface TransportOptions {
  /** Read on every connect attempt. Null once signed out; unused by the demo feed. */
  getToken?: () => string | null
  /** A connection attempt failed; the caller decides whether the session or the proxy is at fault. */
  onConnectFailed?: (detail: string) => void
}

/** The setup screen already decided which source to use. */
export function createTransport(config: RuntimeConfig, options: TransportOptions = {}): Transport {
  return config.source === 'ws'
    ? createSignalRTransport(config.hubUrl, options)
    : createDemoTransport()
}
