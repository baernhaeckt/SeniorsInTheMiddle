import type { ServerEvent } from '../protocol/types'

export type LinkState = 'idle' | 'connecting' | 'live' | 'retrying' | 'closed'

export interface LinkStatus {
  state: LinkState
  /** What the view is attached to, shown verbatim in the header. */
  endpoint: string
  /** Set while retrying, so the header can count down honestly. */
  attempt?: number
  detail?: string
  /** Frames received that did not validate against the protocol. */
  dropped?: number
}

export interface Transport {
  /** Open the source. Safe to call after `stop`; it starts over. */
  start(): void
  /** Close the source and cancel anything pending. */
  stop(): void
  onEvent(handler: (event: ServerEvent) => void): () => void
  /** Calls the handler immediately with the current status, then on every change. */
  onStatus(handler: (status: LinkStatus) => void): () => void
}

/** Minimal fan-out helper shared by both transports. */
export function createEmitter<T>() {
  const handlers = new Set<(value: T) => void>()
  return {
    subscribe: (handler: (value: T) => void) => {
      handlers.add(handler)
      return () => {
        handlers.delete(handler)
      }
    },
    emit: (value: T) => {
      for (const handler of handlers) handler(value)
    },
  }
}
