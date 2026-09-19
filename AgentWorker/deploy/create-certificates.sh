#!/bin/bash
set -euo pipefail
umask 077
: "${WORKER_ADDRESS:?Supply the worker IP reachable by Windows}"
[[ "$WORKER_ADDRESS" =~ ^[0-9.]+$ ]] || exit 2
cd /etc/ka
if [[ ! -f ca-key.pem ]]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out ca-key.pem.new
  mv ca-key.pem.new ca-key.pem
fi
if [[ ! -f ca.pem ]]; then
  openssl req -x509 -new -key ca-key.pem -out ca.pem.new -days 3650 -subj /CN=KA-Worker-CA
  mv ca.pem.new ca.pem
fi
for name in ka-api ka-broker ka-worker; do
  [[ ! -f "$name.pem" ]] || continue
  if [[ ! -f "$name-key.pem" ]]; then
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$name-key.pem.new"
    mv "$name-key.pem.new" "$name-key.pem"
  fi
  openssl req -new -key "$name-key.pem" -out "$name.csr" -subj "/CN=$name"
  if [[ "$name" == ka-worker ]]; then
    printf 'subjectAltName=IP:%s\nextendedKeyUsage=serverAuth\n' "$WORKER_ADDRESS" > "$name.ext"
  else
    printf 'extendedKeyUsage=clientAuth\n' > "$name.ext"
  fi
  openssl x509 -req -in "$name.csr" -CA ca.pem -CAkey ca-key.pem -CAcreateserial -out "$name.pem.new" -days 365 -extfile "$name.ext"
  mv "$name.pem.new" "$name.pem"
done
echo 'Copy only ca.pem, ka-api.pem and ka-api-key.pem to protected host configuration. Never copy the CA key or broker identity into a computer.'
