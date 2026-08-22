import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { BLANK_CONFIG } from '../config'
import { SetupScreen } from './SetupScreen'

describe('SetupScreen', () => {
  it('shows per-field errors on an empty live submit and does not save', async () => {
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    await userEvent.click(screen.getByRole('button', { name: /open the dashboard/i }))
    expect(onSave).not.toHaveBeenCalled()
    expect(screen.getByLabelText('WebSocket URL')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('Host')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument()
  })

  it('saves a normalized config', async () => {
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    await userEvent.type(screen.getByLabelText('WebSocket URL'), ' ws://p:5080/s ')
    await userEvent.type(screen.getByLabelText('Host'), 'proxy ')
    await userEvent.click(screen.getByRole('button', { name: /open the dashboard/i }))
    expect(onSave).toHaveBeenCalledWith({
      ...BLANK_CONFIG,
      wsUrl: 'ws://p:5080/s',
      proxyHost: 'proxy',
    })
  })

  it('folds the proxy group away for the demo feed and saves without one', async () => {
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    await userEvent.click(screen.getByRole('radio', { name: /demo feed/i }))
    expect(screen.queryByLabelText('WebSocket URL')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Host')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /open the dashboard/i }))
    expect(onSave).toHaveBeenCalledWith({ ...BLANK_CONFIG, source: 'demo' })
  })

  it('offers cancel only when reconfiguring', async () => {
    const onCancel = vi.fn()
    render(
      <SetupScreen
        initial={{ ...BLANK_CONFIG, source: 'demo' }}
        onSave={vi.fn()}
        onCancel={onCancel}
      />,
    )
    await userEvent.click(screen.getByRole('button', { name: /cancel/i }))
    expect(onCancel).toHaveBeenCalled()
    expect(screen.getByRole('button', { name: /save and reconnect/i })).toBeInTheDocument()
  })
})
