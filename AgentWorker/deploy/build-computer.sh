#!/bin/bash
# Run on the dedicated worker, never in a project computer.
set -euo pipefail
: "${BASE_IMAGE_FINGERPRINT:?Supply a verified Debian 12 Incus image fingerprint}"
[[ "$BASE_IMAGE_FINGERPRINT" =~ ^[a-f0-9]{64}$ ]] || exit 2
root=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$root/build"
name="ka-image-build-v1"
incus info "$name" >/dev/null 2>&1 || incus init "$BASE_IMAGE_FINGERPRINT" "$name" -s ka -n ka-computers -c security.privileged=false
if [[ -z "$(incus config device get "$name" eth0 network 2>/dev/null)" ]]; then
  incus config device add "$name" eth0 nic network=ka-computers name=eth0
fi
if [[ "$(incus list "$name" --format csv -c s)" != RUNNING ]]; then incus start "$name"; fi
incus exec "$name" -- bash -euxo pipefail -c '
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends systemd systemd-sysv dbus-user-session sudo python3 openssl ca-certificates \
  openbox lxpanel pcmanfm xterm mousepad tigervnc-standalone-server xauth x11-utils x11-xserver-utils \
  xdotool wmctrl xclip imagemagick tesseract-ocr chromium firefox-esr curl wget git \
  python3-venv python3-pip python3-websocket nodejs npm fonts-dejavu fonts-liberation procps iproute2
id agent >/dev/null 2>&1 || useradd -m -u 1000 -s /bin/bash agent
printf "agent ALL=(ALL) NOPASSWD: ALL\n" > /etc/sudoers.d/ka-agent
chmod 0440 /etc/sudoers.d/ka-agent
install -d -o agent -g agent /etc/ka /home/agent/Desktop /home/agent/Downloads /home/agent/.local/state/ka
install -d /opt/ka /project
loginctl enable-linger agent
dpkg-query -W > /etc/ka/packages.lock
apt-get clean
'
incus file push -r "$root/ka_worker" "$name/opt/ka/"
helper="$root/browser-inspect.py"
[[ -f "$helper" ]] || helper="$root/../Omnipotent/Services/Projects/Containers/browser-inspect.py"
incus file push "$helper" "$name/opt/ka/browser-inspect.py"
incus file push "$root/deploy/ka-session.service" "$name/etc/systemd/system/ka-session.service"
incus file push "$root/deploy/ka-desktop.service" "$name/etc/systemd/system/ka-desktop.service"
incus file push "$root/deploy/ka-desktop" "$name/usr/local/bin/ka-desktop" --mode=0755
incus exec "$name" -- systemctl enable ka-session.service
# The GUI is started on demand. No browser runs in the template.
incus stop "$name" --timeout 120
incus publish "$name" --alias ka-computer-candidate
incus image info ka-computer-candidate --format json > "$root/build/computer-image.json"
printf '%s\n' "Candidate created. Pin its fingerprint in broker.json only after acceptance tests. Build instance retained: $name"
