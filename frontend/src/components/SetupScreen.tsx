import { useState, type FormEvent } from 'react'
import {
  BLANK_CONFIG,
  PLACEHOLDERS,
  normalize,
  validate,
  type ConfigErrors,
  type RuntimeConfig,
} from '../config'
import { COPY } from '../copy'
import { Mark, Wordmark } from './Brand'

interface SetupScreenProps {
  /** Current values when reconfiguring. Null on first run. */
  initial: RuntimeConfig | null
  onSave: (config: RuntimeConfig) => void
  /** Only available when there is already a dashboard to go back to. */
  onCancel?: () => void
}

/**
 * The first screen. Nothing is baked into the bundle, so the app asks where the
 * proxy is before it shows anything else.
 */
export function SetupScreen({ initial, onSave, onCancel }: SetupScreenProps) {
  const [draft, setDraft] = useState<RuntimeConfig>(initial ?? BLANK_CONFIG)
  const [errors, setErrors] = useState<ConfigErrors>({})
  const [advanced, setAdvanced] = useState(Boolean(initial?.caUrl ?? initial?.pacUrl))
  // The demo feed talks to nothing, so its proxy address is opt-in.
  const [showProxy, setShowProxy] = useState(Boolean(initial?.proxyHost))
  const proxyOptional = draft.source === 'demo'

  const set = <K extends keyof RuntimeConfig>(key: K, value: RuntimeConfig[K]) => {
    setDraft((current) => ({ ...current, [key]: value }))
    setErrors((current) => ({ ...current, [key]: undefined }))
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const cleaned = normalize(draft)
    const found = validate(cleaned)
    if (Object.keys(found).length > 0) {
      setErrors(found)
      return
    }
    onSave(cleaned)
  }

  return (
    <div className="boot">
      <form className="boot__card" onSubmit={submit} noValidate>
        <header className="boot__head">
          <Mark className="boot__mark" />
          <div>
            <h1 className="boot__name u-display">
              <Wordmark />
            </h1>
            <p className="u-label boot__sub">{COPY.tagline}</p>
          </div>
        </header>

        <p className="boot__lede">
          {initial
            ? 'Change where the dashboard reads from and what the setup guide shows.'
            : 'Tell the dashboard where to read from. It keeps these on this browser.'}
        </p>

        <fieldset className="group">
          <legend className="u-label group__legend">Telemetry stream</legend>

          <div className="choice">
            <Choice
              name="source"
              value="ws"
              current={draft.source}
              onPick={() => {
                set('source', 'ws')
              }}
              title="Live proxy"
              note="Read events from a running backend."
            />
            <Choice
              name="source"
              value="demo"
              current={draft.source}
              onPick={() => {
                set('source', 'demo')
              }}
              title="Demo feed"
              note="Canned traffic, no backend needed."
            />
          </div>

          {draft.source === 'ws' && (
            <Input
              id="wsUrl"
              label="WebSocket URL"
              value={draft.wsUrl}
              placeholder={PLACEHOLDERS.wsUrl}
              error={errors.wsUrl}
              onChange={(value) => {
                set('wsUrl', value)
              }}
            />
          )}
        </fieldset>

        {proxyOptional && !showProxy ? (
          <div className="group group--folded">
            <p className="group__note">
              The demo feed needs no proxy. You can still add an address for the setup guide.
            </p>
            <button
              type="button"
              className="group__more"
              onClick={() => {
                setShowProxy(true)
              }}
            >
              Add a proxy address
            </button>
          </div>
        ) : (
          <fieldset className="group">
            <legend className="u-label group__legend">
              Proxy address{proxyOptional ? ' (optional)' : ''}
            </legend>
            <p className="group__note">What the setup guide tells people to type into a device.</p>

            <div className="group__row">
              <Input
                id="proxyHost"
                label="Host"
                value={draft.proxyHost}
                placeholder={PLACEHOLDERS.proxyHost}
                error={errors.proxyHost}
                onChange={(value) => {
                  set('proxyHost', value)
                }}
              />
              <Input
                id="proxyPort"
                label="Port"
                value={draft.proxyPort}
                placeholder={PLACEHOLDERS.proxyPort}
                error={errors.proxyPort}
                narrow
                onChange={(value) => {
                  set('proxyPort', value)
                }}
              />
            </div>

            <Input
              id="networkName"
              label="Wi-Fi name"
              value={draft.networkName}
              placeholder={PLACEHOLDERS.networkName}
              hint="The network that already routes through the proxy. Leave empty if there is none."
              onChange={(value) => {
                set('networkName', value)
              }}
            />

            <button
              type="button"
              className="group__more"
              onClick={() => {
                setAdvanced((open) => !open)
              }}
              aria-expanded={advanced}
            >
              {advanced ? 'Hide' : 'Show'} certificate and PAC URLs
            </button>

            {advanced && (
              <>
                <Input
                  id="caUrl"
                  label="Certificate"
                  value={draft.caUrl}
                  placeholder={PLACEHOLDERS.caUrl}
                  error={errors.caUrl}
                  hint="Leave empty to derive it from the host and port."
                  onChange={(value) => {
                    set('caUrl', value)
                  }}
                />
                <Input
                  id="pacUrl"
                  label="PAC file"
                  value={draft.pacUrl}
                  placeholder={PLACEHOLDERS.pacUrl}
                  error={errors.pacUrl}
                  onChange={(value) => {
                    set('pacUrl', value)
                  }}
                />
              </>
            )}
          </fieldset>
        )}

        <footer className="boot__foot">
          {onCancel && (
            <button type="button" className="btn btn--quiet" onClick={onCancel}>
              Cancel
            </button>
          )}
          <button type="submit" className="btn btn--go">
            {initial ? 'Save and reconnect' : 'Open the dashboard'}
          </button>
        </footer>
      </form>
    </div>
  )
}

interface InputProps {
  id: string
  label: string
  value: string
  placeholder: string
  error?: string
  hint?: string
  narrow?: boolean
  onChange: (value: string) => void
}

function Input({ id, label, value, placeholder, error, hint, narrow, onChange }: InputProps) {
  const note = error ?? hint
  return (
    <div className="input" data-narrow={narrow} data-invalid={Boolean(error)}>
      <label className="u-label input__label" htmlFor={id}>
        {label}
      </label>
      <input
        id={id}
        className="input__box"
        value={value}
        placeholder={placeholder}
        spellCheck={false}
        autoComplete="off"
        aria-invalid={Boolean(error)}
        aria-describedby={note ? `${id}-note` : undefined}
        onChange={(event) => {
          onChange(event.target.value)
        }}
      />
      {note && (
        <p className="input__note" id={`${id}-note`} data-error={Boolean(error)}>
          {note}
        </p>
      )}
    </div>
  )
}

interface ChoiceProps {
  name: string
  value: string
  current: string
  title: string
  note: string
  onPick: () => void
}

function Choice({ name, value, current, title, note, onPick }: ChoiceProps) {
  const picked = current === value
  return (
    <label className="choice__opt" data-picked={picked}>
      <input
        type="radio"
        name={name}
        value={value}
        checked={picked}
        onChange={onPick}
        className="choice__radio"
      />
      <span className="choice__title">{title}</span>
      <span className="choice__note">{note}</span>
    </label>
  )
}
