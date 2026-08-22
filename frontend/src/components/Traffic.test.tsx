import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { store } from '../engine/store'
import treated from '../protocol/fixtures/treated-exchange.json'
import type { ServerEvent } from '../protocol/types'
import { Traffic } from './Traffic'

const frames = treated as ServerEvent[]

beforeEach(() => {
  store.reset()
})

describe('Traffic', () => {
  it('shows an empty state, then rows newest first', () => {
    render(<Traffic />)
    expect(screen.getByText(/nothing has crossed yet/i)).toBeInTheDocument()
    act(() => {
      for (const frame of frames) store.apply(frame)
    })
    const urls = Array.from(document.querySelectorAll('.tr__url'))
    expect(urls[0]).toHaveTextContent('/v1/claims')
    expect(screen.getByText('css')).toBeInTheDocument()
    expect(screen.getByText('2 PII')).toBeInTheDocument()
  })

  it('only treated rows are buttons, and clicking pins then unpins', async () => {
    for (const frame of frames) store.apply(frame)
    render(<Traffic />)
    const buttons = screen.getAllByRole('button')
    expect(buttons).toHaveLength(1)
    const row = buttons[0]
    if (!row) throw new Error('no row')
    expect(row).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(row)
    expect(store.getSnapshot().pinnedId).toBe('x-1')
    expect(row).toHaveAttribute('aria-pressed', 'true')
    await userEvent.click(row)
    expect(store.getSnapshot().pinnedId).toBeNull()
  })

  it('shows the latest proxy log line', () => {
    render(<Traffic />)
    expect(screen.getByText(/waiting for the proxy/i)).toBeInTheDocument()
    act(() => {
      store.apply({ type: 'log', at: 1, level: 'info', message: 'hello there' })
    })
    expect(screen.getByText('hello there')).toBeInTheDocument()
  })
})
