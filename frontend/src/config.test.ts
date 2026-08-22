import { describe, expect, it } from 'vitest'
import {
  BLANK_CONFIG,
  STORAGE_KEY,
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
  wsUrl: 'ws://proxy:5080/stream',
  proxyHost: 'proxy',
  proxyPort: '8888',
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

  it('requires a ws url and proxy address for the live source', () => {
    const errors = validate({ ...LIVE, wsUrl: '', proxyHost: '', proxyPort: '' })
    expect(Object.keys(errors).sort()).toEqual(['proxyHost', 'proxyPort', 'wsUrl'])
  })

  it('rejects the wrong url scheme', () => {
    expect(validate({ ...LIVE, wsUrl: 'http://x' }).wsUrl).toMatch(/ws:\/\//)
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
    const config = normalize({ ...LIVE, wsUrl: ' ws://x ', proxyHost: ' h ', networkName: ' n ' })
    expect(config).toMatchObject({ wsUrl: 'ws://x', proxyHost: 'h', networkName: 'n' })
  })

  it('derives certificate and PAC urls unless given', () => {
    expect(caUrlOf(LIVE)).toBe('http://proxy:8888/ca.crt')
    expect(pacUrlOf(LIVE)).toBe('http://proxy:8888/proxy.pac')
    expect(caUrlOf({ ...LIVE, caUrl: 'https://c' })).toBe('https://c')
    expect(pacUrlOf({ ...LIVE, pacUrl: ' https://p ' })).toBe('https://p')
  })
})

describe('coerceConfig', () => {
  it('falls back per field rather than throwing the whole config away', () => {
    const config = coerceConfig({
      source: 'demo',
      wsUrl: 42,
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
    const incomplete = memoryStorage({ [STORAGE_KEY]: JSON.stringify({ source: 'ws' }) })
    expect(loadConfig(incomplete)).toBeNull()
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
