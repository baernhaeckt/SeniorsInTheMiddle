import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { BLANK_CONFIG, type RuntimeConfig } from '../config'
import { SetupGuide } from './SetupGuide'

const CONFIG: RuntimeConfig = {
  ...BLANK_CONFIG,
  proxyHost: 'proxy.local',
  proxyPort: '8888',
  networkName: 'SITM',
}

describe('SetupGuide', () => {
  it('opens as a modal dialog with the address and derived urls', () => {
    render(<SetupGuide config={CONFIG} onClose={vi.fn()} />)
    const dialog = screen.getByRole('dialog', { name: /proxy setup/i })
    expect(dialog).toHaveAttribute('open')
    expect(screen.getByText('proxy.local')).toBeInTheDocument()
    expect(screen.getByText('http://proxy.local:8888/ca.crt')).toBeInTheDocument()
    expect(screen.getByText('SITM')).toBeInTheDocument()
  })

  it('closes from the button and from the native close event', async () => {
    const onClose = vi.fn()
    render(<SetupGuide config={CONFIG} onClose={onClose} />)
    await userEvent.click(screen.getByRole('button', { name: /close/i }))
    expect(onClose).toHaveBeenCalledTimes(1)
    const dialog = screen.getByRole('dialog')
    if (!(dialog instanceof HTMLDialogElement)) throw new Error('not a dialog')
    dialog.close()
    expect(onClose).toHaveBeenCalledTimes(2)
  })

  it('switches platform with click and arrow keys', async () => {
    render(<SetupGuide config={CONFIG} onClose={vi.fn()} />)
    const ios = screen.getByRole('tab', { name: /iphone/i })
    expect(ios).toHaveAttribute('aria-selected', 'true')
    await userEvent.click(screen.getByRole('tab', { name: /windows/i }))
    expect(screen.getByRole('tabpanel')).toHaveTextContent(/certutil/)
    screen.getByRole('tab', { name: /windows/i }).focus()
    await userEvent.keyboard('{ArrowRight}')
    expect(screen.getByRole('tab', { name: /macos/i })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: /macos/i })).toHaveFocus()
    await userEvent.keyboard('{ArrowRight}')
    expect(ios).toHaveAttribute('aria-selected', 'true')
  })

  it('explains itself when no address is configured', () => {
    // The defaults carry a localhost address, so this state only happens once someone
    // has cleared it on the setup screen.
    render(<SetupGuide config={{ ...BLANK_CONFIG, proxyHost: '' }} onClose={vi.fn()} />)
    expect(screen.getByText(/no proxy address is configured/i)).toBeInTheDocument()
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
  })
})
