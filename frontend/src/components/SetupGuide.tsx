import { useEffect, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { caUrlOf, hasProxyAddress, pacUrlOf, proxyAddressOf, type RuntimeConfig } from '../config'

/**
 * How to put a device behind the proxy. Three steps in the order someone
 * actually does them, with the address they need to type.
 */

interface Platform {
  key: string
  label: string
  steps: string[]
  command?: string
}

const PLATFORMS: [Platform, ...Platform[]] = [
  {
    key: 'ios',
    label: 'iPhone / iPad',
    steps: [
      'Open the link in Safari. It downloads a configuration profile.',
      'Settings, General, VPN & Device Management, then install the profile.',
      'Settings, General, About, Certificate Trust Settings, then turn it on.',
    ],
  },
  {
    key: 'android',
    label: 'Android',
    steps: [
      'Open the link in Chrome and save the file.',
      'Settings, Security, Encryption & credentials, Install a certificate.',
      'Choose CA certificate and pick the file you saved.',
    ],
  },
  {
    key: 'windows',
    label: 'Windows',
    steps: ['Download the file, then run this in an admin terminal.'],
    command: 'certutil -addstore -f Root ca.crt',
  },
  {
    key: 'macos',
    label: 'macOS',
    steps: ['Download the file, then run this and enter your password.'],
    command:
      'sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt',
  },
]

interface SetupGuideProps {
  config: RuntimeConfig
  onClose: () => void
}

/**
 * A native modal dialog: focus is trapped, Escape closes it, and the page
 * behind is inert, all without any code here.
 */
export function SetupGuide({ config, onClose }: SetupGuideProps) {
  const dialog = useRef<HTMLDialogElement>(null)
  const configured = hasProxyAddress(config)
  const proxyAddress = proxyAddressOf(config)
  const caUrl = caUrlOf(config)
  const pacUrl = pacUrlOf(config)

  useEffect(() => {
    const element = dialog.current
    if (!element || element.open) return
    element.showModal()
  }, [])

  // A click on the backdrop lands on the dialog element itself; clicks inside
  // the panel land on descendants.
  const onBackdropClick = (event: MouseEvent<HTMLDialogElement>) => {
    if (event.target === event.currentTarget) onClose()
  }

  return (
    // The dialog element itself is only hit by a click on its backdrop; the
    // keyboard equivalent (Escape) is handled natively and lands in onClose.
    // eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-noninteractive-element-interactions
    <dialog
      ref={dialog}
      className="sheet"
      aria-labelledby="setup-title"
      onClose={onClose}
      onClick={onBackdropClick}
    >
      <div className="sheet__panel">
        <header className="sheet__head">
          <div>
            <h2 className="sheet__title u-display" id="setup-title">
              Proxy setup
            </h2>
            <p className="sheet__lede">
              Three steps. After the last one, the device shows up in the traffic list.
            </p>
          </div>
          <button type="button" className="sheet__close" onClick={onClose}>
            Close
          </button>
        </header>

        {!configured ? (
          <div className="sheet__empty">
            <p className="step__body">
              No proxy address is configured, so there is nothing to type into a device yet.
            </p>
            <p className="step__body">
              Use <b>Reconfigure</b> in the header to add a host and port. This guide then shows the
              address, the certificate to trust, and how to install it on each platform.
            </p>
          </div>
        ) : (
          <ol className="steps">
            <li className="step">
              <span className="step__n u-display">1</span>
              <h3 className="step__title">Join the network</h3>
              {config.networkName ? (
                <>
                  <p className="step__body">
                    Connect the device to <b>{config.networkName}</b>. Everything it sends then goes
                    through the proxy, with nothing to configure.
                  </p>
                  <p className="step__body">On another network, set the proxy by hand instead:</p>
                </>
              ) : (
                <p className="step__body">Set the proxy on the device by hand:</p>
              )}
              <Field label="Host" value={config.proxyHost} />
              <Field label="Port" value={config.proxyPort} />
              <p className="step__aside">
                Devices that prefer auto-configuration can use the PAC file at{' '}
                <Copyable value={pacUrl} />
              </p>
            </li>

            <li className="step">
              <span className="step__n u-display">2</span>
              <h3 className="step__title">Trust the certificate</h3>
              <p className="step__body">
                Open this address on the device and install what it downloads:
              </p>
              <Field label="Certificate" value={caUrl} wide />
              <p className="step__body">
                Until the device trusts it, the proxy cannot read HTTPS, and those requests cross
                unopened.
              </p>

              <PlatformTabs />
            </li>

            <li className="step">
              <span className="step__n u-display">3</span>
              <h3 className="step__title">Check that it works</h3>
              <p className="step__body">
                Open any app on the device. Its requests appear in the traffic list within a second,
                each marked with what the proxy did about it.
              </p>
              <ul className="marks">
                <li className="marks__row">
                  <span className="tr__mark tr__mark--passthrough">css</span>
                  Assets pass untouched.
                </li>
                <li className="marks__row">
                  <span className="tr__mark tr__mark--clean">clean</span>
                  The body was read, nothing identifying in it.
                </li>
                <li className="marks__row">
                  <span className="tr__mark tr__mark--treated">3 PII</span>
                  Identifiers held back. Click the row to read the payload.
                </li>
              </ul>
              <p className="step__aside">
                Nothing appears? The certificate is usually the reason. Repeat step 2.
              </p>
            </li>
          </ol>
        )}

        <footer className="sheet__foot">
          {configured ? (
            <>
              Proxy at <b>{proxyAddress}</b>. Use Reconfigure in the header to point somewhere else.
            </>
          ) : (
            'No proxy address configured.'
          )}
        </footer>
      </div>
    </dialog>
  )
}

/** WAI-ARIA tabs: arrow keys move between tabs, each tab owns one panel. */
function PlatformTabs() {
  const [index, setIndex] = useState(0)
  const tabs = useRef<(HTMLButtonElement | null)[]>([])
  const active = PLATFORMS[index] ?? PLATFORMS[0]

  const move = (event: KeyboardEvent<HTMLButtonElement>) => {
    const delta =
      event.key === 'ArrowRight'
        ? 1
        : event.key === 'ArrowLeft'
          ? -1
          : event.key === 'Home'
            ? -index
            : event.key === 'End'
              ? PLATFORMS.length - 1 - index
              : 0
    if (delta === 0) return
    event.preventDefault()
    const next = (index + delta + PLATFORMS.length) % PLATFORMS.length
    setIndex(next)
    tabs.current[next]?.focus()
  }

  return (
    <>
      <div className="tabs" role="tablist" aria-label="Platform">
        {PLATFORMS.map((item, i) => (
          <button
            key={item.key}
            ref={(element) => {
              tabs.current[i] = element
            }}
            type="button"
            role="tab"
            id={`platform-tab-${item.key}`}
            aria-selected={i === index}
            aria-controls={`platform-panel-${item.key}`}
            tabIndex={i === index ? 0 : -1}
            className="tabs__tab"
            data-active={i === index}
            onClick={() => {
              setIndex(i)
            }}
            onKeyDown={move}
          >
            {item.label}
          </button>
        ))}
      </div>

      <div
        role="tabpanel"
        id={`platform-panel-${active.key}`}
        aria-labelledby={`platform-tab-${active.key}`}
      >
        <ol className="howto">
          {active.steps.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ol>
        {active.command && <Field label="Command" value={active.command} wide />}
      </div>
    </>
  )
}

function Field({ label, value, wide }: { label: string; value: string; wide?: boolean }) {
  return (
    <div className="field" data-wide={wide}>
      <span className="field__label u-label">{label}</span>
      <code className="field__value">{value}</code>
      <CopyButton value={value} />
    </div>
  )
}

function Copyable({ value }: { value: string }) {
  return (
    <code className="field__value field__value--inline">
      {value} <CopyButton value={value} />
    </code>
  )
}

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)

  const copy = () => {
    navigator.clipboard.writeText(value).then(
      () => {
        setCopied(true)
        window.setTimeout(() => {
          setCopied(false)
        }, 1400)
      },
      () => {
        // Clipboard access can be refused. Selecting the text still works.
        setCopied(false)
      },
    )
  }

  return (
    <button type="button" className="field__copy" onClick={copy} aria-label={`Copy ${value}`}>
      {copied ? 'copied' : 'copy'}
    </button>
  )
}
