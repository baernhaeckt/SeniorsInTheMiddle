import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { Session } from '../auth/session'
import { UserBubble } from './UserBubble'

const SESSION: Session = {
  token: 'a.b.c',
  username: 'ruth meier',
  email: 'ruth@test.ch',
  origin: 'http://proxy:8080',
}

function renderBubble(session: Session = SESSION) {
  const onSignOut = vi.fn()
  render(<UserBubble session={session} onSignOut={onSignOut} />)
  return { onSignOut, bubble: screen.getByRole('button', { name: /signed in as/i }) }
}

describe('UserBubble', () => {
  it('shows initials and names who is signed in', () => {
    const { bubble } = renderBubble()

    expect(bubble).toHaveTextContent('RM')
    expect(bubble).toHaveAccessibleName('Signed in as ruth meier')
    expect(bubble).toHaveAttribute('aria-expanded', 'false')
  })

  it('opens the menu with the username and email', async () => {
    const { bubble } = renderBubble()

    await userEvent.click(bubble)

    expect(screen.getByRole('menu')).toBeInTheDocument()
    expect(screen.getByText('ruth meier')).toBeInTheDocument()
    expect(screen.getByText('ruth@test.ch')).toBeInTheDocument()
    expect(bubble).toHaveAttribute('aria-expanded', 'true')
  })

  it('signs out', async () => {
    const { bubble, onSignOut } = renderBubble()

    await userEvent.click(bubble)
    await userEvent.click(screen.getByRole('menuitem', { name: 'Sign out' }))

    expect(onSignOut).toHaveBeenCalled()
  })

  it('closes on Escape', async () => {
    const { bubble } = renderBubble()
    await userEvent.click(bubble)

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('closes when something else is clicked', async () => {
    const { bubble } = renderBubble()
    await userEvent.click(bubble)

    await userEvent.click(document.body)

    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('leaves out an email it does not have', async () => {
    const { bubble } = renderBubble({ ...SESSION, email: '' })

    await userEvent.click(bubble)

    expect(screen.getByRole('menu')).toBeInTheDocument()
    expect(screen.queryByText('ruth@test.ch')).not.toBeInTheDocument()
  })
})
