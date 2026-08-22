#!/bin/sh
# Throwaway PKI for the harness. Two things come out of it:
#
#   receiver.crt / receiver.key  the receiver's HTTPS certificate
#   bundle.pem                   the trust store the proxy container is pointed at
#
# The proxy validates upstream certificates normally (ConnectProxyMiddleware calls
# AuthenticateAsClientAsync with no custom callback), so it has to trust whoever signed
# the receiver. bundle.pem is the image's own CA bundle plus this CA, handed to the
# container through SSL_CERT_FILE -- an environment variable and a read-only mount, so
# the image under test stays exactly as it is built.
#
# None of this material is secret and none of it leaves the compose network.
set -eu

PKI=${PKI_DIR:-/pki}
RECEIVER_CN=${RECEIVER_CN:-receiver.sitm.local}
DAYS_CA=3650
DAYS_LEAF=825

if [ -f "$PKI/receiver.crt" ] && [ -f "$PKI/receiver.key" ] && [ -f "$PKI/bundle.pem" ]; then
  echo "certgen: material already present in $PKI, reusing it"
  exit 0
fi

mkdir -p "$PKI"
cd "$PKI"

echo "certgen: generating the harness CA"
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days "$DAYS_CA" \
  -keyout harness-ca.key -out harness-ca.pem \
  -subj "/CN=SeniorsInTheMiddle Harness CA/O=Integration Harness" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1

echo "certgen: issuing the receiver certificate for $RECEIVER_CN"
cat > leaf.ext <<EXT
subjectAltName = DNS:$RECEIVER_CN, DNS:receiver, DNS:localhost, IP:127.0.0.1
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
basicConstraints = critical, CA:FALSE
EXT

openssl req -newkey rsa:2048 -nodes \
  -keyout receiver.key -out receiver.csr \
  -subj "/CN=$RECEIVER_CN" >/dev/null 2>&1

openssl x509 -req -in receiver.csr -sha256 -days "$DAYS_LEAF" \
  -CA harness-ca.pem -CAkey harness-ca.key -CAcreateserial \
  -extfile leaf.ext -out receiver.crt >/dev/null 2>&1

# The image's own bundle first, so the proxy keeps trusting public CAs and only gains
# this one. If the certgen image ships without a bundle, the harness CA alone still
# covers everything the harness itself talks to.
SYSTEM_BUNDLE=/etc/ssl/certs/ca-certificates.crt
if [ -f "$SYSTEM_BUNDLE" ]; then
  cat "$SYSTEM_BUNDLE" harness-ca.pem > bundle.pem
else
  echo "certgen: no system CA bundle found, bundle.pem carries the harness CA only"
  cp harness-ca.pem bundle.pem
fi

rm -f receiver.csr leaf.ext

# Everything here is world-readable on purpose: the proxy and the receiver run as
# unprivileged users from their own images and have to read these files.
chmod 644 harness-ca.pem harness-ca.key receiver.crt receiver.key bundle.pem

echo "certgen: done"
openssl x509 -in receiver.crt -noout -subject -issuer -dates
