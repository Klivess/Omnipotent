#!/bin/bash
# Runs only inside the VM created and owned by the portable Windows installer.
set -euo pipefail
umask 077
exec 9>/run/ka-first-boot.lock
flock -n 9 || exit 0
mkdir -p /var/lib/ka-bootstrap
status() { phase="$1"; python3 - "$1" "$2" <<'PY'
import json,os,sys,time
p='/var/lib/ka-bootstrap/status.json'
with open(p+'.tmp','w') as f:
 json.dump(dict(state=sys.argv[1],reason=sys.argv[2],updated=time.time()),f); f.flush(); os.fsync(f.fileno())
os.replace(p+'.tmp',p)
PY
}
phase=starting
trap 'status retrying "Linux setup could not finish the $phase stage; existing disks are preserved and the installer will retry"' ERR
if [[ -f /etc/ka/broker.json ]]; then
    systemctl start ka-broker
    if [[ ! -f /var/lib/ka-bootstrap/smoke-passed.json ]]; then
        status testing 'Checking a disposable desktop and terminal before connecting Omnipotent'
        python3 /opt/ka-bootstrap/source/AgentWorker/deploy/smoke.py
    fi
    status ready 'Worker configured; checking authenticated health from Windows'
    exit 0
fi
status installing 'Installing worker prerequisites'
export DEBIAN_FRONTEND=noninteractive
apt-get -o DPkg::Lock::Timeout=180 update
apt-get -o DPkg::Lock::Timeout=180 install -y btrfs-progs incus python3 openssl nftables restic
status storage 'Preparing the dedicated computer data disk'
# The installer attaches exactly one new data disk at SCSI LUN 1, distinct from
# the OS at LUN 0. Refuse ambiguous, partitioned, mounted or previously used disks.
python3 - <<'PY'
import json,os,pathlib,subprocess
run=lambda *a: subprocess.check_output(a,text=True).strip()
config=json.loads(pathlib.Path('/etc/ka-bootstrap.json').read_text())
label=config['dataLabel']
existing=pathlib.Path('/dev/disk/by-label')/label
mount=pathlib.Path('/srv/ka'); mount.mkdir(parents=True,exist_ok=True)
if not existing.exists():
 disks=json.loads(run('lsblk','-J','-b','-o','NAME,TYPE,SIZE,FSTYPE,MOUNTPOINTS'))['blockdevices']
 candidates=[]
 for d in disks:
  device=pathlib.Path('/sys/class/block')/d['name']/'device'
  if d['type']=='disk' and device.resolve().name.endswith(':1') and int(d['size'])==config['dataBytes']:
   candidates.append(d)
 if len(candidates)!=1: raise RuntimeError('Dedicated LUN 1 data disk is missing or ambiguous')
 d=candidates[0]; disk='/dev/'+d['name']
 if d.get('children') or d.get('fstype') or any(d.get('mountpoints') or []): raise RuntimeError('Data disk is not empty; refusing format')
 if run('wipefs','--no-act','--noheadings','-o','TYPE',disk): raise RuntimeError('Existing disk signature; refusing format')
 subprocess.run(['mkfs.btrfs','-L',label,disk],check=True)
 subprocess.run(['udevadm','settle'],check=True)
 if not existing.exists(): raise RuntimeError('Formatted disk identity unavailable')
uuid=run('blkid','-s','UUID','-o','value',str(existing))
fstab=pathlib.Path('/etc/fstab'); text=fstab.read_text()
entry=f'UUID={uuid} /srv/ka btrfs defaults 0 0'
if not any(line.split()[1:2]==['/srv/ka'] for line in text.splitlines() if not line.startswith('#')):
 with fstab.open('a') as f: f.write('\n'+entry+'\n'); f.flush(); os.fsync(f.fileno())
subprocess.run(['mount','/srv/ka'],check=not os.path.ismount('/srv/ka'))
if run('findmnt','-n','-o','UUID','--target','/srv/ka')!=uuid: raise RuntimeError('Unexpected data filesystem mounted')
PY
mkdir -p /opt/ka-bootstrap/source
tar -xzf /opt/ka-bootstrap/payload.tar.gz -C /opt/ka-bootstrap/source
root=/opt/ka-bootstrap/source/AgentWorker
python3 - "$root" <<'PY'
from pathlib import Path
import sys
for p in Path(sys.argv[1]).rglob('*'):
 if p.is_file() and (p.suffix in ('.sh','.service','.timer','.nft') or p.name=='ka-desktop'):
  p.write_bytes(p.read_bytes().replace(b'\r\n',b'\n'))
PY
bash "$root/deploy/install-worker.sh"
status certificates 'Creating private worker identities'
WORKER_ADDRESS=10.78.0.2 bash "$root/deploy/create-certificates.sh"
status image 'Building the persistent desktop image; this can take several minutes'
if ! incus image info ka-base-debian12 >/dev/null 2>&1; then
    incus image copy images:debian/12/amd64 local: --alias ka-base-debian12
fi
incus image info ka-base-debian12 --format json > /etc/ka/base-image.lock.json
export BASE_IMAGE_FINGERPRINT
BASE_IMAGE_FINGERPRINT=$(python3 -c 'import json;print(json.load(open("/etc/ka/base-image.lock.json"))["fingerprint"])')
if ! incus image info ka-computer-candidate >/dev/null 2>&1; then bash "$root/deploy/build-computer.sh"; fi
incus image info ka-computer-candidate --format json > /etc/ka/computer-image.lock.json
status testing 'Running Linux storage, operation and terminal tests'
(cd "$root" && python3 -m unittest discover -s tests -v) > /var/lib/ka-bootstrap/tests.log 2>&1
python3 - "$root/deploy/broker.example.json" <<'PY'
import json,os,sys
config=json.load(open(sys.argv[1]))
config['host_pressure_file']='/var/lib/ka-bootstrap/host-pressure.json'
config['image_fingerprint']=json.load(open('/etc/ka/computer-image.lock.json'))['fingerprint']
with open('/etc/ka/broker.json.tmp','w') as f:
 json.dump(config,f);f.flush();os.fsync(f.fileno())
os.replace('/etc/ka/broker.json.tmp','/etc/ka/broker.json')
PY
systemctl enable --now ka-broker
status testing 'Checking a disposable desktop and terminal before connecting Omnipotent'
python3 "$root/deploy/smoke.py"
status ready 'Worker installed; Linux unit tests passed. Production acceptance gates remain separate.'
