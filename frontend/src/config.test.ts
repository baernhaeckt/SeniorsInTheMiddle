import { describe, expect, it } from 'vitest'
import {
  BLANK_CONFIG,
  STORAGE_KEY,
  apiBaseOf,
  caUrlOf,
  coerceConfig,
  loadConfig,
  normalize,
  pacUrlOf,
  saveConfig,
  validate,
  type RuntimeConfig,
} from './config'

const LIVE: RuntimeConfig = {
  source: 'ws',
  hubUrl: 'http://proxy:8080/hub/telemetry',
  proxyHost: 'proxy',
  proxyPort: '3128',
  networkName: '',
  caUrl: '',
  pacUrl: '',
}

function memoryStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial))
  return {
    getItem: (key: string) => data.get(key) ?? null,
    setItem: (key: string, value: string) => {
      data.set(key, value)
    },
  }
}

describe('validate', () => {
  it('accepts a complete live config', () => {
    expect(validate(LIVE)).toEqual({})
  })

  it('ships a default that works against a proxy on this machine', () => {
    // A fresh install should reach `docker compose up` in integration/ with no typing.
    expect(validate(BLANK_CONFIG)).toEqual({})
    // The hub is on the API port, the certificate and PAC file on the proxy port.
    expect(BLANK_CONFIG.hubUrl).toBe('http://localhost:8080/hub/telemetry')
    expect(caUrlOf(BLANK_CONFIG)).toBe('http://localhost:3128/ca.crt')
    expect(pacUrlOf(BLANK_CONFIG)).toBe('http://localhost:3128/proxy.pac')
  })

  it('requires a hub url and proxy address for the live source', () => {
    const errors = validate({ ...LIVE, hubUrl: '', proxyHost: '', proxyPort: '' })
    expect(Object.keys(errors).sort()).toEqual(['hubUrl', 'proxyHost', 'proxyPort'])
  })

  it('rejects the wrong url scheme', () => {
    // A hub address is an http url even though the client turns it into a socket.
    expect(validate({ ...LIVE, hubUrl: 'ws://x' }).hubUrl).toMatch(/http:\/\//)
    expect(validate({ ...LIVE, caUrl: 'ftp://x' }).caUrl).toMatch(/http/)
    expect(validate({ ...LIVE, pacUrl: 'nope' }).pacUrl).toMatch(/http/)
  })

  it('rejects a host with scheme or path, and a port out of range', () => {
    expect(validate({ ...LIVE, proxyHost: 'http://x' }).proxyHost).toBeDefined()
    expect(validate({ ...LIVE, proxyHost: 'x/y' }).proxyHost).toBeDefined()
    expect(validate({ ...LIVE, proxyPort: '0' }).proxyPort).toBeDefined()
    expect(validate({ ...LIVE, proxyPort: '70000' }).proxyPort).toBeDefined()
    expect(validate({ ...LIVE, proxyPort: '8.5' }).proxyPort).toBeDefined()
  })

  it('makes the proxy address optional for the demo feed, but still checks it', () => {
    expect(validate({ ...BLANK_CONFIG, source: 'demo', proxyHost: '', proxyPort: '' })).toEqual({})
    const noPort = validate({ ...BLANK_CONFIG, source: 'demo', proxyHost: 'x', proxyPort: '' })
    expect(noPort.proxyPort).toBeDefined()
    const badHost = validate({ ...BLANK_CONFIG, source: 'demo', proxyHost: 'bad host' })
    expect(badHost.proxyHost).toBeDefined()
  })
})

describe('normalize and derived urls', () => {
  it('trims every field', () => {
    const config = normalize({
      ...LIVE,
      hubUrl: ' http://x/hub ',
      proxyHost: ' h ',
      networkName: ' n ',
    })
    expect(config).toMatchObject({ hubUrl: 'http://x/hub', proxyHost: 'h', networkName: 'n' })
  })

  it('derives certificate and PAC urls unless given', () => {
    expect(caUrlOf(LIVE)).toBe('http://proxy:3128/ca.crt')
    expect(pacUrlOf(LIVE)).toBe('http://proxy:3128/proxy.pac')
    expect(caUrlOf({ ...LIVE, caUrl: 'https://c' })).toBe('https://c')
    expect(pacUrlOf({ ...LIVE, pacUrl: ' https://p ' })).toBe('https://p')
  })
})

describe('coerceConfig', () => {
  it('falls back per field rather than throwing the whole config away', () => {
    const config = coerceConfig({
      source: 'demo',
      hubUrl: 42,
      proxyHost: 'h',
      proxyPort: null,
      extra: true,
    })
    expect(config).toEqual({ ...BLANK_CONFIG, source: 'demo', proxyHost: 'h' })
  })

  it('treats garbage as blank', () => {
    expect(coerceConfig(null)).toEqual(BLANK_CONFIG)
    expect(coerceConfig('str')).toEqual(BLANK_CONFIG)
    expect(coerceConfig({ source: 'carrier-pigeon' }).source).toBe('ws')
  })
})

describe('loadConfig / saveConfig', () => {
  it('round-trips through storage', () => {
    const storage = memoryStorage()
    saveConfig(LIVE, storage)
    expect(loadConfig(storage)).toEqual(LIVE)
  })

  it('returns null for nothing stored, broken JSON, or a config that fails validation', () => {
    expect(loadConfig(memoryStorage())).toBeNull()
    expect(loadConfig(memoryStorage({ [STORAGE_KEY]: '{nope' }))).toBeNull()
    const badHost = memoryStorage({
      [STORAGE_KEY]: JSON.stringify({ source: 'ws', proxyHost: 'bad host' }),
    })
    expect(loadConfig(badHost)).toBeNull()
  })

  it('ignores a config saved under an earlier key', () => {
    // A v2 config holds a proxy address on what is now the backend's API port, so the
    // setup guide built from it would point a device at a port that does not proxy.
    const stale = memoryStorage({
      'sitm.config.v2': JSON.stringify({ ...LIVE, proxyPort: '8080' }),
    })
    expect(STORAGE_KEY).toBe('sitm.config.v3')
    expect(loadConfig(stale)).toBeNull()
  })

  it('salvages a partially corrupt stored config when what is left validates', () => {
    const stored = { ...LIVE, networkName: 12345 }
    const storage = memoryStorage({ [STORAGE_KEY]: JSON.stringify(stored) })
    expect(loadConfig(storage)).toEqual({ ...LIVE, networkName: '' })
  })

  it('survives storage that throws', () => {
    const throwing = {
      getItem: () => {
        throw new Error('blocked')
      },
      setItem: () => {
        throw new Error('blocked')
      },
    }
    expect(loadConfig(throwing)).toBeNull()
    expect(() => {
      saveConfig(LIVE, throwing)
    }).not.toThrow()
  })
})

describe('apiBaseOf', () => {
  it('takes the origin from the hub URL', () => {
    // The API and the hub are the same listener, so nobody is asked for a second address.
    expect(apiBaseOf({ ...BLANK_CONFIG, hubUrl: 'http://localhost:8080/hub/telemetry' })).toBe(
      'http://localhost:8080',
    )
  })

  it('keeps a non-default port and drops the path', () => {
    expect(
      apiBaseOf({ ...BLANK_CONFIG, hubUrl: 'https://proxy.sitm.local:9443/hub/telemetry' }),
    ).toBe('https://proxy.sitm.local:9443')
  })

  it('tolerates surrounding whitespace', () => {
    expect(apiBaseOf({ ...BLANK_CONFIG, hubUrl: '  http://proxy:8080/hub/telemetry  ' })).toBe(
      'http://proxy:8080',
    )
  })

  it('is empty for something that is not a URL', () => {
    expect(apiBaseOf({ ...BLANK_CONFIG, hubUrl: 'proxy:8080' })).toBe('')
    expect(apiBaseOf({ ...BLANK_CONFIG, hubUrl: '' })).toBe('')
  })
})
