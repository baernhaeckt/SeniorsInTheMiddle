import { clipParts } from '../engine/text'

interface ClipProps {
  value: string
  className?: string
  /** How many characters to hold at the end. */
  tail?: number
}

/**
 * Text that shortens itself when its box does. The trimming is CSS, so the
 * whole string stays in the DOM and is still selected, copied and read out in
 * full — only the pixels are missing.
 */
export function Clip({ value, className, tail }: ClipProps) {
  const parts = clipParts(value, tail)

  return (
    <span className={className ? `clip ${className}` : 'clip'}>
      <span className="clip__head">{parts.head}</span>
      {parts.tail && <span className="clip__tail">{parts.tail}</span>}
    </span>
  )
}
