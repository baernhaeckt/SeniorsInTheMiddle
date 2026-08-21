import { createDemoTransport } from './demoTransport'
import { createWebSocketTransport } from './websocketTransport'
import type { Transport } from './types'
import type { RuntimeConfig } from '../config'

/** The setup screen already decided which source to use. */
export function createTransport(config: RuntimeConfig): Transport {
  return config.source === 'ws' ? createWebSocketTransport(config.wsUrl) : createDemoTransport()
}

export type { Transport, LinkStatus, LinkState } from './types'
