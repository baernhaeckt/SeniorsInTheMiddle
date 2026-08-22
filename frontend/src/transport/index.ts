import type { RuntimeConfig } from '../config'
import { createDemoTransport } from './demoTransport'
import { createSignalRTransport } from './signalrTransport'
import type { Transport } from './types'

/** The setup screen already decided which source to use. */
export function createTransport(config: RuntimeConfig): Transport {
  return config.source === 'ws' ? createSignalRTransport(config.hubUrl) : createDemoTransport()
}
