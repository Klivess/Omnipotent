#!/bin/bash
set -euo pipefail
umask 077
: "${WORKER_ADDRESS:?Supply the worker IP reachable by Windows}"
[[ "$WORKER_ADDRESS" =~ ^[0-9.]+$ ]] || exit 2
cd /etc/ka
[[ ! -e ca-key.pem ]] || { echo 'CA exists; refusing to replace identities'; exit 1; }
openssl req -x509 -newkey rsa:3072 -nodes -keyout ca-key.pem -out ca.pem -days 3650 -subj /CN=KA-Worker-CA
for name in ka-api ka-broker ka-worker; do
  openssl req -new -newkey rsa:2048 -nodes -keyout "$name-key.pem" -out "$name.csr" -subj "/CN=$name"
  if [[ "$name" == ka-worker ]]; then
    printf 'subjectAltName=IP:%s\nextendedKeyUsage=serverAuth\n' "$WORKER_ADDRESS" > "$name.ext"
  else
    printf 'extendedKeyUsage=clientAuth\n' > "$name.ext"
  fi
  openssl x509 -req -in "$name.csr" -CA ca.pem -CAkey ca-key.pem -CAcreateserial -out "$name.pem" -days 365 -extfile "$name.ext"
done
echo 'Copy only ca.pem, ka-api.pem and ka-api-key.pem to protected host configuration. Never copy the CA key or broker identity into a computer.'
