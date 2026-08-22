/**
 * Runtime configuration. Nothing here is baked in at build time, so the same
 * bundle can point at any proxy. The app asks for these values on first run and
 * keeps them in localStorage.
 */

import * as v from 'valibot'

export const FeedSourceSchema = v.picklist(['ws', 'demo'])

export const RuntimeConfigSchema = v.object({
  /** Where telemetry comes from: the real proxy, or the built-in demo feed. */
  source: FeedSourceSchema,
  /**
   * The proxy's telemetry hub. An http(s) address even though it ends up as a socket:
   * that is the address SignalR is given, and it converts it itself.
   */
  hubUrl: v.string(),
  /** What someone types into a device's proxy field. */
  proxyHost: v.string(),
  proxyPort: v.string(),
  /** The Wi-Fi that already routes through the proxy. May be empty. */
  networkName: v.string(),
  /** Optional. Derived from host and port when empty. */
  caUrl: v.string(),
  pacUrl: v.string(),
})
export type RuntimeConfig = v.InferOutput<typeof RuntimeConfigSchema>

/**
 * A stored config that can no longer do its job is forgotten rather than half-migrated,
 * and the key is bumped to make that happen. `v1` held a `wsUrl` pointing at a raw socket,
 * which cannot reach a hub. `v2` held a proxy address on the port the backend now serves
 * its API from, so the setup guide built from it would tell people to configure a device
 * against a port that does not proxy.
 *
 * A number in a stored address cannot be rewritten safely, because a deployment is free to
 * put the proxy anywhere; sending people through the setup screen once shows them the
 * current defaults and lets them re-enter a custom address.
 */
export const STORAGE_KEY = 'sitm.config.v3'

/**
 * What a fresh install starts with: a proxy running on this machine, which is what
 * `docker compose up` in `integration/` gives you. Certificate and PAC URLs are left
 * empty on purpose — they derive from the address, so they stay right if it changes.
 *
 * The hub is on the API port and the proxy address is the proxy port; they are different
 * listeners in the same process, which is why the two ports here do not match.
 */
export const BLANK_CONFIG: RuntimeConfig = {
  source: 'ws',
  hubUrl: 'http://localhost:8080/hub/telemetry',
  proxyHost: 'localhost',
  proxyPort: '3128',
  networkName: '',
  caUrl: '',
  pacUrl: '',
}

/** Shown as placeholders in the setup form, never as values. */
export const PLACEHOLDERS: Record<Exclude<keyof RuntimeConfig, 'source'>, string> = {
  hubUrl: 'http://proxy.sitm.local:8080/hub/telemetry',
  proxyHost: 'proxy.sitm.local',
  proxyPort: '3128',
  networkName: 'SITM-Guest',
  caUrl: 'http://proxy.sitm.local:3128/ca.crt',
  pacUrl: 'http://proxy.sitm.local:3128/proxy.pac',
}

export function proxyAddressOf(config: RuntimeConfig): string {
  return `${config.proxyHost}:${config.proxyPort}`
}

/** The root certificate a device must trust before HTTPS can be read. */
export function caUrlOf(config: RuntimeConfig): string {
  return config.caUrl.trim() || `http://${proxyAddressOf(config)}/ca.crt`
}

/** Auto-configuration URL, for devices that prefer it over host and port. */
export function pacUrlOf(config: RuntimeConfig): string {
  return config.pacUrl.trim() || `http://${proxyAddressOf(config)}/proxy.pac`
}

/** True once there is an address the setup guide can actually show. */
export function hasProxyAddress(config: RuntimeConfig): boolean {
  return config.proxyHost.trim().length > 0
}

export type ConfigErrors = Partial<Record<keyof RuntimeConfig, string>>

/**
 * Everything the form refuses to accept, keyed by field.
 *
 * The demo feed talks to nothing, so it needs no proxy address. Whatever is
 * filled in is still checked, so a typo does not reach the setup guide.
 */
export function validate(config: RuntimeConfig): ConfigErrors {
  const errors: ConfigErrors = {}
  const proxyRequired = config.source === 'ws'

  if (config.source === 'ws') {
    const url = config.hubUrl.trim()
    if (!url) {
      errors.hubUrl = 'Needed to reach the proxy. Pick the demo feed to run without one.'
    } else if (!isUrl(url, ['http:', 'https:'])) {
      errors.hubUrl = 'Must start with http:// or https://'
    }
  }

  const host = config.proxyHost.trim()
  if (!host) {
    if (proxyRequired) errors.proxyHost = 'The setup guide needs an address to show.'
  } else if (/\s|\//.test(host)) {
    errors.proxyHost = 'Host only, with no scheme or path.'
  }

  const rawPort = config.proxyPort.trim()
  const port = Number(rawPort)
  if (!rawPort) {
    if (proxyRequired || host) errors.proxyPort = 'Required.'
  } else if (!Number.isInteger(port) || port < 1 || port > 65535) {
    errors.proxyPort = 'A port between 1 and 65535.'
  }

  if (config.caUrl.trim() && !isUrl(config.caUrl.trim(), ['http:', 'https:'])) {
    errors.caUrl = 'Must start with http:// or https://'
  }
  if (config.pacUrl.trim() && !isUrl(config.pacUrl.trim(), ['http:', 'https:'])) {
    errors.pacUrl = 'Must start with http:// or https://'
  }

  return errors
}

export function isValid(config: RuntimeConfig): boolean {
  return Object.keys(validate(config)).length === 0
}

function isUrl(value: string, protocols: string[]): boolean {
  try {
    return protocols.includes(new URL(value).protocol)
  } catch {
    return false
  }
}

/** Trim every field, so a stray space never reaches the socket. */
export function normalize(config: RuntimeConfig): RuntimeConfig {
  return {
    source: config.source,
    hubUrl: config.hubUrl.trim(),
    proxyHost: config.proxyHost.trim(),
    proxyPort: config.proxyPort.trim(),
    networkName: config.networkName.trim(),
    caUrl: config.caUrl.trim(),
    pacUrl: config.pacUrl.trim(),
  }
}

/**
 * Rebuild a config from whatever was stored. Each field is checked on its
 * own, so one corrupt value falls back to the blank default instead of
 * throwing the whole config away.
 */
export function coerceConfig(stored: unknown): RuntimeConfig {
  if (!stored || typeof stored !== 'object') return BLANK_CONFIG
  const record = stored as Record<string, unknown>
  const config = { ...BLANK_CONFIG }
  for (const key of Object.keys(RuntimeConfigSchema.entries) as (keyof RuntimeConfig)[]) {
    const result = v.safeParse(RuntimeConfigSchema.entries[key], record[key])
    if (result.success) {
      // Each field's schema is the field's own type, so this is safe.
      ;(config as Record<keyof RuntimeConfig, string>)[key] = result.output
    }
  }
  return normalize(config)
}

/**
 * Read the saved config. Anything that does not validate counts as absent,
 * which sends the app back to the setup screen rather than half-configured
 * into the dashboard.
 */
export function loadConfig(
  storage: Pick<Storage, 'getItem'> = window.localStorage,
): RuntimeConfig | null {
  let raw: string | null
  try {
    raw = storage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
  if (!raw) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return null
  }

  const config = coerceConfig(parsed)
  return isValid(config) ? config : null
}

export function saveConfig(
  config: RuntimeConfig,
  storage: Pick<Storage, 'setItem'> = window.localStorage,
): void {
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify(config))
  } catch {
    // A browser with storage switched off still runs for this session.
  }
}
