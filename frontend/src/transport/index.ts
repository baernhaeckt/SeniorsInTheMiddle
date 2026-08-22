import type { RuntimeConfig } from '../config'
import { createDemoTransport } from './demoTransport'
import type { Transport } from './types'
import { createWebSocketTransport } from './websocketTransport'

/** The setup screen already decided which source to use. */
export function createTransport(config: RuntimeConfig): Transport {
  return config.source === 'ws' ? createWebSocketTransport(config.wsUrl) : createDemoTransport()
}
