import { useEffect, useRef, useState } from 'react'
import { initialsOf, type Session } from '../auth/session'

interface UserBubbleProps {
  session: Session
  onSignOut: () => void
}

/**
 * Who is signed in, and the way back out.
 *
 * Collapsed to initials because the header is already dense and this is not what anyone is
 * looking at the wall to read; the name and address are one click away when someone wants to
 * check whose session the display is showing.
 */
export function UserBubble({ session, onSignOut }: UserBubbleProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    const onPointerDown = (event: MouseEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false)
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }

    document.addEventListener('mousedown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)

    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  return (
    <div className="who" ref={root}>
      <button
        type="button"
        className="who__bubble"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Signed in as ${session.username}`}
        onClick={() => {
          setOpen((current) => !current)
        }}
      >
        {initialsOf(session.username)}
      </button>

      {open && (
        <div className="who__menu" role="menu">
          <div className="who__id">
            <div className="who__name">{session.username}</div>
            {session.email && <div className="who__mail u-mono">{session.email}</div>}
          </div>
          <button type="button" className="who__out" role="menuitem" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      )}
    </div>
  )
}
