import type { Stage } from './store'

type Tone = 'warm' | 'cool'
export type PacketKind = 'request' | 'response'

export interface Slot {
  x: number
  y: number
  /** Inside the gate: the packet hides and the gate readout does the talking. */
  docked: boolean
  tone: Tone
  /** How long the move to this slot should take. */
  travelMs: number
}

/** Request rides the upper rail, the reply comes back on the lower one. */
const LANE = { request: 0.235, response: 0.775 }

export function laneY(kind: PacketKind, height: number): number {
  return LANE[kind] * height
}

export function edgeX(width: number): number {
  return width * (width < 760 ? 0.16 : 0.125)
}

/** Mirrors the `.gatestack` width clamp in band.css. */
function gateWidth(width: number): number {
  return Math.min(Math.max(280, width * 0.34), 520)
}

/** Mirrors the `.pk` width clamp in band.css. */
function packetWidth(width: number): number {
  return Math.min(Math.max(150, width * 0.17), 208)
}

/**
 * How far either side of the middle a packet parks while the gate works on it.
 *
 * Derived from the two things it has to clear — half the gate and half itself —
 * rather than picked, so widening the gate cannot leave a packet tucked under
 * its corner. Capped short of the far slot, or a packet would travel backwards
 * on its way out.
 */
function dockOffset(width: number): number {
  const clear = (gateWidth(width) + packetWidth(width)) / 2 + 24
  return Math.min(clear, Math.max(0, width / 2 - edgeX(width) - 12))
}

/** Where a packet belongs right now, in pixels inside the band. */
export function slotFor(stage: Stage, kind: PacketKind, width: number, height: number): Slot {
  const centre = width / 2
  const dock = dockOffset(width)
  const near = edgeX(width)
  const far = width - near
  const y = laneY(kind, height)

  const at = (x: number, tone: Tone, travelMs: number, docked = false): Slot => ({
    x,
    y,
    tone,
    travelMs,
    docked,
  })

  if (kind === 'request') {
    switch (stage) {
      case 'ingress':
        return at(centre - dock, 'warm', 1300)
      case 'inspect':
        // Held at the door while the proxy reads it. Nothing has moved yet.
        return at(centre - dock, 'warm', 200)
      case 'redact':
        return at(centre, 'warm', 300, true)
      case 'egress':
        return at(centre + dock, 'cool', 380)
      default:
        return at(far, 'cool', 1200)
    }
  }

  switch (stage) {
    case 'return':
      return at(centre + dock, 'cool', 1200)
    case 'rehydrate':
      return at(centre, 'cool', 260, true)
    case 'deliver':
      return at(near, 'warm', 1300)
    default:
      return at(near, 'warm', 600)
  }
}

/** Where a packet first appears. */
export function originFor(kind: PacketKind, width: number, height: number) {
  return {
    x: kind === 'request' ? edgeX(width) : width - edgeX(width),
    y: laneY(kind, height),
  }
}

export function easeInOut(t: number): number {
  return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2
}
