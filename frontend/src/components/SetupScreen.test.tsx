import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { BLANK_CONFIG } from '../config'
import { SetupScreen } from './SetupScreen'

describe('SetupScreen', () => {
  it('opens straight onto the dashboard on a fresh install', async () => {
    // The defaults describe a proxy on this machine, which is what the integration
    // harness gives you, so first run should need no typing at all.
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    await userEvent.click(screen.getByRole('button', { name: /open the dashboard/i }))
    expect(onSave).toHaveBeenCalledWith(BLANK_CONFIG)
  })

  it('shows per-field errors once the defaults are cleared, and does not save', async () => {
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    await userEvent.clear(screen.getByLabelText('Telemetry hub URL'))
    await userEvent.clear(screen.getByLabelText('Host'))
    await userEvent.click(screen.getByRole('button', { name: /open the dashboard/i }))
    expect(onSave).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Telemetry hub URL')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('Host')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument()
  })

  it('saves a normalized config', async () => {
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    const hub = screen.getByLabelText('Telemetry hub URL')
    await userEvent.clear(hub)
    await userEvent.type(hub, ' http://p:8080/hub/telemetry ')
    const host = screen.getByLabelText('Host')
    await userEvent.clear(host)
    await userEvent.type(host, 'proxy ')
    await userEvent.click(screen.getByRole('button', { name: /open the dashboard/i }))
    expect(onSave).toHaveBeenCalledWith({
      ...BLANK_CONFIG,
      hubUrl: 'http://p:8080/hub/telemetry',
      proxyHost: 'proxy',
    })
  })

  it('folds the proxy group away for the demo feed and saves without one', async () => {
    const onSave = vi.fn()
    render(<SetupScreen initial={null} onSave={onSave} />)
    await userEvent.click(screen.getByRole('radio', { name: /demo feed/i }))
    expect(screen.queryByLabelText('Telemetry hub URL')).not.toBeInTheDocument()
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
