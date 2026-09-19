#!/bin/bash
# Prerequisite: Ubuntu 24.04 VM, dedicated Btrfs filesystem already mounted at /srv/ka.
# This script never formats a disk and never touches WSL or Docker data.
set -euo pipefail
[[ $EUID == 0 ]] || { echo 'Run as root on the dedicated Linux worker'; exit 1; }
. /etc/os-release
[[ "$ID" == ubuntu && "$VERSION_ID" == 24.04 ]] || { echo 'Ubuntu 24.04 required'; exit 1; }
[[ "$(findmnt -n -o FSTYPE --target /srv/ka)" == btrfs ]] || { echo 'Mount the dedicated Btrfs data disk at /srv/ka first'; exit 1; }
root=$(cd "$(dirname "$0")/.." && pwd)
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y incus btrfs-progs python3 openssl nftables restic
install -d -m 0700 /etc/ka /srv/ka/state /srv/ka/projects /srv/ka/transfers /srv/ka/certificates /opt/ka
cp -a "$root/ka_worker" /opt/ka/
cp -a "$root/deploy" /opt/ka/
install -m 0644 "$root/deploy/ka-backup.service" "$root/deploy/ka-backup.timer" /etc/systemd/system/
incus storage show ka >/dev/null 2>&1 || incus storage create ka btrfs source=/srv/ka/incus
incus network show ka-computers >/dev/null 2>&1 || incus network create ka-computers \
  ipv4.address=10.77.0.1/24 ipv4.nat=true ipv6.address=none
# Block east-west traffic at each virtual NIC; prevent access to host/LAN services except DNS/DHCP.
install -m 0600 "$root/deploy/ka-network.nft" /etc/ka/network.nft
if ! nft list table inet ka_isolation >/dev/null 2>&1; then nft -f /etc/ka/network.nft; fi
install -m 0644 "$root/deploy/ka-network.service" /etc/systemd/system/ka-network.service
systemctl enable ka-network.service
# A bounded emergency buffer, not capacity promised to new computers.
if [[ ! -f /srv/ka/worker.swap ]]; then btrfs filesystem mkswapfile --size 8G /srv/ka/worker.swap; fi
grep -q '^/srv/ka/worker.swap ' /etc/fstab || printf '/srv/ka/worker.swap none swap defaults 0 0\n' >> /etc/fstab
swapon --show=NAME --noheadings | grep -Fxq /srv/ka/worker.swap || swapon /srv/ka/worker.swap
install -m 0644 "$root/deploy/ka-broker.service" /etc/systemd/system/ka-broker.service
systemctl daemon-reload
echo 'Installed. Generate certificates, build and test the computer image, then supply /etc/ka/broker.json before enabling ka-broker.'
