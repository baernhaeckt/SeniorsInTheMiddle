import { useEffect, useRef, useState } from 'react'
import { caUrl, networkName, pacUrl, proxyAddress, proxyHost, proxyPort } from '../config'

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

const PLATFORMS: Platform[] = [
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
    command: 'sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt',
  },
]

export function SetupGuide({ onClose }: { onClose: () => void }) {
  const [platform, setPlatform] = useState(PLATFORMS[0].key)
  const closeButton = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    closeButton.current?.focus()
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const active = PLATFORMS.find((item) => item.key === platform) ?? PLATFORMS[0]

  return (
    <div className="sheet" onClick={onClose}>
      <div
        className="sheet__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="setup-title"
        onClick={(event) => event.stopPropagation()}
      >
        <header className="sheet__head">
          <div>
            <h2 className="sheet__title u-display" id="setup-title">
              Proxy setup
            </h2>
            <p className="sheet__lede">
              Three steps. After the last one, the device shows up in the traffic list.
            </p>
          </div>
          <button ref={closeButton} type="button" className="sheet__close" onClick={onClose}>
            Close
          </button>
        </header>

        <ol className="steps">
          <li className="step">
            <span className="step__n u-display">1</span>
            <h3 className="step__title">Join the network</h3>
            <p className="step__body">
              Connect the device to <b>{networkName}</b>. Everything it sends then goes through
              the proxy, with nothing to configure.
            </p>
            <p className="step__body">On another network, set the proxy by hand instead:</p>
            <Field label="Host" value={proxyHost} />
            <Field label="Port" value={proxyPort} />
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

            <div className="tabs" role="tablist" aria-label="Platform">
              {PLATFORMS.map((item) => (
                <button
                  key={item.key}
                  type="button"
                  role="tab"
                  aria-selected={item.key === platform}
                  className="tabs__tab"
                  data-active={item.key === platform}
                  onClick={() => setPlatform(item.key)}
                >
                  {item.label}
                </button>
              ))}
            </div>

            <ol className="howto">
              {active.steps.map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ol>
            {active.command && <Field label="Command" value={active.command} wide />}
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

        <footer className="sheet__foot">
          Proxy at <b>{proxyAddress}</b>. Change it with VITE_PROXY_HOST and VITE_PROXY_PORT in
          your .env file.
        </footer>
      </div>
    </div>
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

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1400)
    } catch {
      // Clipboard access can be refused. Selecting the text still works.
      setCopied(false)
    }
  }

  return (
    <button type="button" className="field__copy" onClick={copy} aria-label={`Copy ${value}`}>
      {copied ? 'copied' : 'copy'}
    </button>
  )
}
