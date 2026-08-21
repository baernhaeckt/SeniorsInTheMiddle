import type { ServerEvent } from '../protocol/types'

export type LinkState = 'idle' | 'connecting' | 'live' | 'retrying' | 'closed'

export interface LinkStatus {
  state: LinkState
  /** What the view is attached to, shown verbatim in the header. */
  endpoint: string
  /** Set while retrying, so the header can count down honestly. */
  attempt?: number
  detail?: string
}

export interface Transport {
  /** Human-readable name of the source, e.g. `websocket` or `demo feed`. */
  readonly kind: 'websocket' | 'demo'
  start(): void
  stop(): void
  onEvent(handler: (event: ServerEvent) => void): () => void
  onStatus(handler: (status: LinkStatus) => void): () => void
}

/** Minimal fan-out helper shared by both transports. */
export function createEmitter<T>() {
  const handlers = new Set<(value: T) => void>()
  return {
    subscribe(handler: (value: T) => void) {
      handlers.add(handler)
      return () => handlers.delete(handler)
    },
    emit(value: T) {
      for (const handler of handlers) handler(value)
    },
    clear() {
      handlers.clear()
    },
  }
}
