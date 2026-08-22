import type { Exchange, Stage } from './store'

type Tone = 'real' | 'token'

/**
 * What an exchange is carrying at a given stage: which body to show and which
 * identifier to frame in it. The gate readout and the packet on the band both
 * read from here, so they never disagree about what is on screen.
 */
export interface StageView {
  /** The body in play at this stage. */
  text: string
  /** The first identifier, as it appears in that body: real value or token. */
  focus: string
  /** Whether that identifier is the real thing or its stand-in. */
  tone: Tone
}

const TOKENIZED_STAGES: ReadonlySet<Stage> = new Set(['redact', 'egress', 'thinking', 'return'])

export function isTokenizedStage(stage: Stage): boolean {
  return TOKENIZED_STAGES.has(stage)
}

export function stageViewOf(exchange: Exchange, stage: Stage = exchange.stage): StageView {
  const first = exchange.entities[0]
  const redacted = exchange.redactedRequestBody ?? exchange.requestBody

  switch (stage) {
    case 'ingress':
    case 'inspect':
      return { text: exchange.requestBody, focus: first?.value ?? '', tone: 'real' }
    case 'redact':
    case 'egress':
    case 'thinking':
      return { text: redacted, focus: first?.token ?? '', tone: 'token' }
    case 'return':
      return {
        text: exchange.tokenizedResponseBody ?? '',
        focus: first?.token ?? '',
        tone: 'token',
      }
    case 'rehydrate':
    case 'deliver':
    case 'done':
      return { text: exchange.responseBody ?? '', focus: first?.value ?? '', tone: 'real' }
  }
}
