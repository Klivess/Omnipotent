"""Admission controls creation, never evicts admitted computers."""
from dataclasses import asdict, dataclass
from pathlib import Path
import time
import json

MIB = 1024 * 1024


def host_headroom(path):
    """Thin VHDX guest free space must not conceal exhausted Windows storage."""
    if not path:
        return None
    try:
        status = json.loads(Path(path).read_text())
        if time.time() - status['sampled'] > 300:
            return 'Waiting for current host storage telemetry; existing computers are preserved'
        if status['freeBytes'] < 10 * 1024 ** 3:
            return 'Waiting for free space on the Windows disk backing computer storage'
    except (OSError, ValueError, KeyError, TypeError):
        return 'Waiting for host storage telemetry; existing computers are preserved'
    return None


@dataclass
class Pressure:
    available: int
    total: int
    swap_free: int
    swap_total: int
    memory_full_avg10: float
    sampled: float

    @classmethod
    def read(cls):
        mem = {}
        for line in Path("/proc/meminfo").read_text().splitlines():
            key, value = line.split(":", 1)
            mem[key] = int(value.strip().split()[0]) * 1024
        full = 0.0
        for line in Path("/proc/pressure/memory").read_text().splitlines():
            if line.startswith("full "):
                full = float(dict(part.split("=") for part in line.split()[1:])["avg10"])
        return cls(mem["MemAvailable"], mem["MemTotal"], mem["SwapFree"], mem["SwapTotal"], full, time.time())

    def json(self):
        return asdict(self)


class Admission:
    def __init__(self, reserve_bytes=1024 * MIB, start_bytes=1536 * MIB):
        self.reserve = reserve_bytes
        self.start = start_bytes

    def reason(self, pressure, pending_bytes=0):
        if time.time() - pressure.sampled > 30:
            return "Waiting for fresh worker memory telemetry"
        # Hyper-V guests can retain a high PSI full average after boot/image
        # creation even when most guest memory is available and swap is idle.
        # Treat PSI as an admission blocker only when usable headroom is also
        # tight; otherwise a healthy worker can remain queued indefinitely.
        if (pressure.memory_full_avg10 >= 5 and
                pressure.available - pending_bytes < self.reserve + self.start):
            return "Waiting for memory pressure to subside; existing work is preserved"
        if pressure.swap_total and pressure.swap_free < pressure.swap_total // 2:
            return "Waiting for swap usage to recover; existing work is preserved"
        if pressure.available - pending_bytes < self.reserve + self.start:
            return "Waiting for memory headroom; existing work is preserved"
        return None
