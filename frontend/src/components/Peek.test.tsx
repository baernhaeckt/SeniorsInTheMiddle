import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { Peek } from './Peek'

const ADDRESS = 'http://seniorsinthemiddle-backend.northeurope.azurecontainerapps.io:3128'

describe('Peek', () => {
  it('shows the address in full on hover and hides it again on the way out', async () => {
    render(
      <Peek value={ADDRESS} note="reattaching">
        <button type="button">Proxy address</button>
      </Peek>,
    )
    const trigger = screen.getByRole('button')

    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()

    await userEvent.hover(trigger)
    expect(screen.getByRole('tooltip')).toHaveTextContent(ADDRESS)
    expect(screen.getByRole('tooltip')).toHaveTextContent('reattaching')

    await userEvent.unhover(trigger)
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('opens on keyboard focus too', async () => {
    render(
      <Peek value={ADDRESS}>
        <button type="button">Proxy address</button>
      </Peek>,
    )

    await userEvent.tab()

    expect(screen.getByRole('tooltip')).toHaveTextContent(ADDRESS)
  })

  it('stays shut when there is no address to show', async () => {
    render(
      <Peek value="">
        <button type="button">not set</button>
      </Peek>,
    )

    await userEvent.hover(screen.getByRole('button'))

    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })
})
