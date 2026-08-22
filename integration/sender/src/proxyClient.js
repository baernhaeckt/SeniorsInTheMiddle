/**
 * Both ways a client talks to a forward proxy, written out by hand.
 *
 * There is no proxy library here on purpose. The proxy tells its two kinds of traffic
 * apart by the request line, so the harness has to produce those lines exactly:
 *
 *   HTTP   GET http://receiver.sitm.local:3000/api/v1/status HTTP/1.1
 *          -> ForwardProxy.GetProxyDestination sees absolute form and forwards it.
 *
 *   HTTPS  CONNECT receiver.sitm.local:3443 HTTP/1.1
 *          -> ConnectProxyMiddleware answers 200, presents a certificate it minted for
 *             that host, opens its own TLS connection upstream, and copies between them.
 *
 * The second one only completes if the client trusts the proxy's CA, which is why every
 * TLS context here is given the certificate fetched from /ca.crt.
 */
import http from 'node:http'
import net from 'node:net'
import tls from 'node:tls'

const MAX_CAPTURE = 262_144

/** Opens a CONNECT tunnel and hands back the raw socket. */
function openTunnel({ proxyHost, proxyPort, host, port, timeoutMs }) {
  return new Promise((resolve, reject) => {
    const socket = net.connect({ host: proxyHost, port: proxyPort })
    let header = Buffer.alloc(0)

    const fail = (cause) => {
      socket.destroy()
      reject(cause instanceof Error ? cause : new Error(String(cause)))
    }

    const timer = setTimeout(() => fail(new Error(`CONNECT timed out after ${timeoutMs} ms`)), timeoutMs)

    const onData = (chunk) => {
      header = Buffer.concat([header, chunk])
      const end = header.indexOf('\r\n\r\n')
      if (end === -1) {
        if (header.length > 16_384) fail(new Error('CONNECT response header too large'))
        return
      }

      clearTimeout(timer)
      socket.removeListener('data', onData)
      // Removing the last 'data' listener does not stop a flowing stream, and anything
      // read now would be dropped before TLS gets it.
      socket.pause()

      const statusLine = header.subarray(0, header.indexOf('\r\n')).toString('ascii')
      const status = Number(statusLine.split(' ')[1])

      // Anything the proxy sent after the blank line already belongs to the tunnel.
      const rest = header.subarray(end + 4)
      if (rest.length > 0) socket.unshift(rest)

      if (status !== 200) return fail(new Error(`proxy refused CONNECT: ${statusLine}`))
      resolve(socket)
    }

    socket.on('data', onData)
    socket.once('error', fail)
    socket.once('connect', () => {
      socket.write(`CONNECT ${host}:${port} HTTP/1.1\r\nHost: ${host}:${port}\r\nProxy-Connection: keep-alive\r\n\r\n`)
    })
  })
}

/** TLS over the tunnel, verified against the proxy's own CA. */
function secureTunnel(socket, { host, caPem, timeoutMs }) {
  return new Promise((resolve, reject) => {
    const secured = tls.connect({ socket, servername: host, ca: [caPem] })
    const timer = setTimeout(() => {
      secured.destroy()
      reject(new Error(`TLS handshake timed out after ${timeoutMs} ms`))
    }, timeoutMs)

    secured.once('secureConnect', () => {
      clearTimeout(timer)
      if (!secured.authorized) {
        secured.destroy()
        return reject(new Error(`TLS not authorized: ${secured.authorizationError}`))
      }
      resolve(secured)
    })
    secured.once('error', (cause) => {
      clearTimeout(timer)
      reject(cause)
    })
  })
}

function collect(request, timeoutMs) {
  return new Promise((resolve, reject) => {
    request.setTimeout(timeoutMs, () => request.destroy(new Error(`response timed out after ${timeoutMs} ms`)))
    request.once('error', reject)
    request.once('response', (response) => {
      const chunks = []
      let bytes = 0
      response.on('data', (chunk) => {
        bytes += chunk.length
        if (bytes <= MAX_CAPTURE) chunks.push(chunk)
      })
      response.once('end', () =>
        resolve({
          status: response.statusCode,
          headers: response.headers,
          bytes,
          body: Buffer.concat(chunks).toString('utf8'),
        }),
      )
      response.once('error', reject)
    })
  })
}

function headersFor(spec, target, port) {
  const headers = {
    host: `${target}:${port}`,
    'user-agent': 'sitm-harness-sender/1.0',
    accept: '*/*',
    connection: 'close',
    ...spec.headers,
  }
  if (spec.body) headers['content-length'] = Buffer.byteLength(spec.body)
  // The receiver cannot recognise a person's name by shape, so the harness declares the
  // ones it used. Base64 because header values are ASCII and Swiss names are not --
  // "Käthi Bürki" in a raw header is rejected before it ever reaches the proxy.
  // Both ends are the harness; nothing here is a trust boundary.
  if (spec.names?.length) {
    headers['x-harness-names'] = Buffer.from(spec.names.join('|'), 'utf8').toString('base64')
  }
  return headers
}

/**
 * Sends one request through the proxy and reports what came back. Never throws: a
 * failure is part of the record, because a proxy that drops connections is exactly what
 * the harness is looking for.
 */
export async function send(spec, options) {
  const { proxyHost, proxyPort, targetHost, timeoutMs, caPem } = options
  const port = spec.scheme === 'https' ? options.targetHttpsPort : options.targetHttpPort
  const startedAt = process.hrtime.bigint()

  let tunnel
  try {
    let result
    let tlsInfo = null

    if (spec.scheme === 'http') {
      const request = http.request({
        host: proxyHost,
        port: proxyPort,
        method: spec.method,
        // Absolute form. This is what makes it a proxy request rather than a call to the
        // proxy's own API on the same port.
        path: `http://${targetHost}:${port}${spec.path}`,
        headers: headersFor(spec, targetHost, port),
        agent: false,
      })
      if (spec.body) request.write(spec.body)
      request.end()
      result = await collect(request, timeoutMs)
    } else {
      const socket = await openTunnel({ proxyHost, proxyPort, host: targetHost, port, timeoutMs })
      tunnel = await secureTunnel(socket, { host: targetHost, caPem, timeoutMs })

      const peer = tunnel.getPeerCertificate()
      tlsInfo = {
        authorized: tunnel.authorized,
        // The give-away that interception happened: upstream is the receiver's own
        // certificate, but what the client sees is one the proxy minted.
        issuer: peer?.issuer?.CN ?? null,
        subject: peer?.subject?.CN ?? null,
      }

      // http, not https, and deliberately with no `agent` key at all.
      //
      // The socket is already encrypted, so the http module is the right one. And Node
      // only honours `createConnection` when no agent is in play: `agent: false` still
      // builds a throwaway Agent, whose own createConnection opens a second connection --
      // for https, a second TLS handshake that knows nothing about the proxy's CA, and
      // for http, a plain connection straight to the TLS port. Both fail in ways that
      // look like the proxy's fault and are not.
      const request = http.request({
        createConnection: () => tunnel,
        host: targetHost,
        port,
        method: spec.method,
        path: spec.path,
        headers: headersFor(spec, targetHost, port),
      })
      if (spec.body) request.write(spec.body)
      request.end()
      result = await collect(request, timeoutMs)
    }

    return {
      ok: spec.expect === undefined || result.status === spec.expect,
      status: result.status,
      responseBytes: result.bytes,
      responseBody: result.body,
      tls: tlsInfo,
      error: null,
      durationMs: Number(process.hrtime.bigint() - startedAt) / 1e6,
    }
  } catch (cause) {
    return {
      ok: false,
      status: null,
      responseBytes: 0,
      responseBody: '',
      tls: null,
      error: cause.message,
      durationMs: Number(process.hrtime.bigint() - startedAt) / 1e6,
    }
  } finally {
    tunnel?.destroy()
  }
}
