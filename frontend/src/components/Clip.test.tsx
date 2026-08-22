import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Clip } from './Clip'

const LONG = 'http://seniorsinthemiddle-backend.northeurope.azurecontainerapps.io:3128/ca.crt'

describe('Clip', () => {
  it('renders the whole address, so it is still copied and read out in full', () => {
    const { container } = render(<Clip value={LONG} />)

    expect(container.textContent).toBe(LONG)
  })

  it('holds the port and path at the end, where the ellipsis never reaches', () => {
    const { container } = render(<Clip value={LONG} />)

    expect(container.querySelector('.clip__tail')).toHaveTextContent(':3128/ca.crt')
  })
})
