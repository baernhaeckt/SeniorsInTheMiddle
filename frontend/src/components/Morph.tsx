import { useEffect, useRef, useState } from 'react'

const CHURN = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$%&@?!/[]{}<>=+*'

interface MorphProps {
  /** Text that should be standing there when the change finishes. */
  to: string
  /** Class applied to characters that have settled on their final value. */
  settledClass: string
  durationMs?: number
}

/**
 * Animates a real value into its token, one character at a time, with random
 * glyphs in between. Runs on a timer rather than in CSS so the churn looks like
 * work rather than a fade.
 */
export function Morph({ to, settledClass, durationMs = 900 }: MorphProps) {
  const [frame, setFrame] = useState(() => ({ text: to, settled: to.length }))
  const previous = useRef(to)

  useEffect(() => {
    if (previous.current === to) return
    const source = previous.current
    previous.current = to

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    if (reduced) {
      setFrame({ text: to, settled: to.length })
      return
    }

    const start = performance.now()
    const length = Math.max(to.length, source.length)
    let raf = 0

    const tick = (now: number) => {
      const progress = Math.min(1, (now - start) / durationMs)
      // Characters settle left to right, the first quarter is pure churn.
      const settled = Math.floor(Math.max(0, (progress - 0.25) / 0.75) * to.length)
      let text = to.slice(0, settled)
      for (let i = settled; i < length; i += 1) {
        if (i >= to.length && progress > 0.75) break
        text += CHURN[Math.floor(Math.random() * CHURN.length)]
      }
      setFrame({ text, settled })
      if (progress < 1) {
        raf = requestAnimationFrame(tick)
      } else {
        setFrame({ text: to, settled: to.length })
      }
    }

    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [to, durationMs])

  const settledText = frame.text.slice(0, frame.settled)
  const churnText = frame.text.slice(frame.settled)

  return (
    <>
      {settledText && <span className={settledClass}>{settledText}</span>}
      {churnText && <span className="tm__churn">{churnText}</span>}
    </>
  )
}
