import { useEffect, useRef, useState, type ReactNode } from 'react'

interface PeekProps {
  /** The address in full. Empty means there is nothing worth showing, so no panel opens. */
  value: string
  /** A second line under it, for whatever else the trigger had to leave out. */
  note?: string
  /** Which edge of the trigger the panel lines up with. */
  align?: 'start' | 'center' | 'end'
  /** Anything else worth a look while the panel is open. */
  extra?: ReactNode
  className?: string
  children: ReactNode
}

/**
 * The whole of something the header had to shorten.
 *
 * The panel is a sibling of the trigger rather than a `title`, so the address
 * inside it can be selected and copied — which is the point, since these are
 * addresses someone is about to type into a device. It stays open while the
 * pointer is anywhere inside, including on the panel itself.
 */
export function Peek({ value, note, align = 'center', className, children, extra }: PeekProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('keydown', onKeyDown)

    return () => {
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  const shows = value.length > 0

  return (
    <div
      className={className ? `peek ${className}` : 'peek'}
      ref={root}
      onPointerEnter={() => {
        if (shows) setOpen(true)
      }}
      onPointerLeave={() => {
        setOpen(false)
      }}
      onFocus={() => {
        if (shows) setOpen(true)
      }}
      onBlur={(event) => {
        if (!root.current?.contains(event.relatedTarget)) setOpen(false)
      }}
    >
      {children}

      {open && (
        <div className="peek__pop" data-align={align}>
          <div className="peek__card" role="tooltip">
            <span className="peek__value u-mono">{value}</span>
            {note && <span className="peek__note">{note}</span>}
            {extra}
          </div>
        </div>
      )}
    </div>
  )
}
