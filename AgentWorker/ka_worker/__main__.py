import json
import sys
from pathlib import Path

if len(sys.argv) != 3 or sys.argv[1] not in ("broker", "session"):
    raise SystemExit("Usage: python3 -m ka_worker broker|session /etc/ka/config.json")
config = json.loads(Path(sys.argv[2]).read_text())
if sys.argv[1] == "broker":
    from .broker import main
else:
    from .session import main
main(config)
