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
    const buttons = Array.from(document.querySelectorAll('button.tr'))
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

  it('the treated toggle hides untreated rows and back again', async () => {
    for (const frame of frames) store.apply(frame)
    render(<Traffic />)
    expect(document.querySelectorAll('.tr').length).toBeGreaterThan(1)
    const toggle = screen.getByRole('button', { name: /treated/i })
    expect(toggle).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-pressed', 'true')
    const rows = Array.from(document.querySelectorAll('.tr'))
    expect(rows).toHaveLength(1)
    expect(rows[0]).toHaveAttribute('data-treatment', 'treated')
    await userEvent.click(toggle)
    expect(document.querySelectorAll('.tr').length).toBeGreaterThan(1)
  })

  it('shows the proxy log lines with their level, newest first', () => {
    render(<Traffic />)
    expect(screen.getByText(/waiting for the proxy/i)).toBeInTheDocument()
    act(() => {
      store.apply({ type: 'log', at: 1, level: 'info', message: 'hello there' })
      store.apply({ type: 'log', at: 2, level: 'block', message: 'held at the boundary' })
    })
    const lines = Array.from(document.querySelectorAll('.ticker__line'))
    expect(lines[0]).toHaveTextContent('held at the boundary')
    expect(lines[0]).toHaveAttribute('data-level', 'block')
    expect(lines[1]).toHaveTextContent('hello there')
  })

  it('a log line that names an exchange pins it when clicked', async () => {
    for (const frame of frames) store.apply(frame)
    render(<Traffic />)
    const line = document.querySelector('button.ticker__line')
    if (!line) throw new Error('no clickable log line')
    expect(line).toHaveTextContent('held at the boundary')
    await userEvent.click(line)
    expect(store.getSnapshot().pinnedId).toBe('x-1')
  })
})
