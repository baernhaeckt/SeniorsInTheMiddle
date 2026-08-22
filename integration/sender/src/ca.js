/**
 * The proxy's own CA, fetched the way a device would fetch it.
 *
 * MitmCertificateProvider generates a CA on first start and serves its public
 * certificate at /ca.crt as DER. Every intercepted HTTPS host is then presented with a
 * certificate signed by it, so a client that does not trust this CA cannot complete a
 * single handshake through the proxy.
 *
 * The request goes to the proxy in origin form, so ForwardProxyMiddleware hands it to
 * the app rather than forwarding it -- the same path the dashboard's download link uses.
 */
import http from 'node:http'
import { X509Certificate } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'

const PEM_HEADER = '-----BEGIN CERTIFICATE-----'

function get(host, port, path, timeoutMs) {
  return new Promise((resolve, reject) => {
    const request = http.request({ host, port, path, method: 'GET', timeout: timeoutMs }, (response) => {
      const chunks = []
      response.on('data', (chunk) => chunks.push(chunk))
      response.on('end', () => resolve({ status: response.statusCode, body: Buffer.concat(chunks) }))
    })
    request.on('timeout', () => request.destroy(new Error(`timed out after ${timeoutMs} ms`)))
    request.on('error', reject)
    request.end()
  })
}

/** DER in, PEM out. Saves pulling openssl into a Node image for ten lines of base64. */
export function derToPem(der) {
  const base64 = der.toString('base64').match(/.{1,64}/g)?.join('\n') ?? ''
  return `${PEM_HEADER}\n${base64}\n-----END CERTIFICATE-----\n`
}

/**
 * Waits for the proxy to answer and returns its CA. Doubles as the readiness check the
 * sender needs anyway: neither image in this compose file ships curl or wget, so a
 * healthcheck would have to be written in Node regardless.
 */
export async function waitForCa({
  host,
  port,
  timeoutMs = 5000,
  attemptDelayMs = 1000,
  giveUpAfterMs = 120_000,
  onAttempt,
}) {
  const deadline = Date.now() + giveUpAfterMs
  let lastReason = 'no attempt made'

  for (let attempt = 1; ; attempt++) {
    if (Date.now() > deadline) {
      throw new Error(
        `the proxy did not answer /ca.crt within ${Math.round(giveUpAfterMs / 1000)}s ` +
          `(${host}:${port}, last: ${lastReason})`,
      )
    }
    try {
      const response = await get(host, port, '/ca.crt', timeoutMs)
      if (response.status !== 200) throw new Error(`/ca.crt answered ${response.status}`)
      if (response.body.length === 0) throw new Error('/ca.crt answered with an empty body')

      const text = response.body.toString('utf8')
      const pem = text.includes(PEM_HEADER) ? text : derToPem(response.body)
      const certificate = new X509Certificate(pem)

      return {
        pem,
        subject: certificate.subject.replace(/\n/g, ', '),
        fingerprint: certificate.fingerprint256,
        validTo: certificate.validTo,
        attempts: attempt,
      }
    } catch (cause) {
      lastReason = cause.message
      onAttempt?.(attempt, cause.message)
      await delay(attemptDelayMs)
    }
  }
}
