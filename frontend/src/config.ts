/**
 * Where the proxy lives, as told to the people setting up a device.
 *
 * Everything here is build-time configuration. Set it in `.env` (see
 * `.env.example`) or pass it to `docker build` with `--build-arg`. Nothing here
 * is discovered at runtime. The dashboard does not probe the network.
 */

const env = import.meta.env

const clean = (value: string | undefined, fallback: string): string => {
  const trimmed = value?.trim()
  return trimmed && trimmed.length > 0 ? trimmed : fallback
}

export const proxyHost = clean(env.VITE_PROXY_HOST, 'proxy.sitm.local')
export const proxyPort = clean(env.VITE_PROXY_PORT, '8888')

/** What someone types into a device's proxy field. */
export const proxyAddress = `${proxyHost}:${proxyPort}`

/** The root certificate a device must trust before HTTPS can be read. */
export const caUrl = clean(env.VITE_PROXY_CA_URL, `http://${proxyAddress}/ca.crt`)

/** Auto-configuration URL, for devices that prefer it over host and port. */
export const pacUrl = clean(env.VITE_PROXY_PAC_URL, `http://${proxyAddress}/proxy.pac`)

/** The Wi-Fi that already routes through the proxy, so nothing needs setting. */
export const networkName = clean(env.VITE_PROXY_NETWORK, 'SITM-Guest')
