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

/**
 * Stages at which the readout has already shown everything it is going to.
 *
 * Only two transitions change what is written in the box: into `redact`, where
 * the values become tokens, and into `rehydrate`, where they come back. After
 * the second one the text is settled — the numbers under it still fill in, but
 * the payload does not move again. Read by `gateStack.ts` to decide when a card
 * should let the next one have the gate.
 */
const SPENT_STAGES: ReadonlySet<Stage> = new Set(['rehydrate', 'deliver', 'done'])

export function isSpentStage(stage: Stage): boolean {
  return SPENT_STAGES.has(stage)
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
