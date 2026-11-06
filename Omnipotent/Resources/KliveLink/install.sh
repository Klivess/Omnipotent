#!/bin/sh
# KliveLink universal Unix bootstrap. Served by /k/linux and /k/macos.
set -eu
umask 077

BASE_URL="__BASE_URL__"
VERSION="2.1.1"
RELEASE_TAG="klivelink-agents-2.1.1"
GITHUB_URL="https://github.com/Klivess/Omnipotent/releases/download/${RELEASE_TAG}"
SYSTEM=0
UNINSTALL=0

for arg in "$@"; do
    case "$arg" in
        --system) SYSTEM=1 ;;
        --uninstall) UNINSTALL=1 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

OS_NAME=$(uname -s 2>/dev/null || echo unknown)
ARCH_NAME=$(uname -m 2>/dev/null || echo unknown)
case "$OS_NAME" in
    Linux) OS_KEY=linux ;;
    Darwin) OS_KEY=osx ;;
    *) echo "Unsupported operating system: $OS_NAME" >&2; exit 2 ;;
esac
case "$ARCH_NAME" in
    x86_64|amd64) ARCH_KEY=x64 ;;
    aarch64|arm64) ARCH_KEY=arm64 ;;
    *) echo "Unsupported architecture: $ARCH_NAME (supported: x86_64, aarch64/arm64)" >&2; exit 2 ;;
esac
RID="${OS_KEY}-${ARCH_KEY}"
ASSET="klivelink-agent-${RID}-${VERSION}"
if [ "$OS_KEY" = "linux" ]; then
    ARCHIVE_NAME="${ASSET}.tar.gz"
else
    ARCHIVE_NAME="${ASSET}.tar.gz"
fi

if [ "$SYSTEM" = "1" ]; then
    [ "$(id -u)" = "0" ] || { echo "System install requires root. Re-run with sudo or omit --system." >&2; exit 2; }
    if [ "$OS_KEY" = "linux" ]; then
        INSTALL_DIR="/opt/klivelink"
        CONFIG_DIR="/etc/klivelink"
        LOG_DIR="/var/log/klivelink"
    else
        INSTALL_DIR="/usr/local/klivelink"
        CONFIG_DIR="/Library/Application Support/KliveLink"
        LOG_DIR="/Library/Logs/KliveLink"
    fi
else
    if [ "$OS_KEY" = "linux" ]; then
        INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/klivelink"
        CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/klivelink"
        LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/klivelink"
    else
        INSTALL_DIR="$HOME/Library/Application Support/KliveLink"
        CONFIG_DIR="$HOME/Library/Application Support/KliveLink"
        LOG_DIR="$HOME/Library/Logs/KliveLink"
    fi
fi
BIN="$INSTALL_DIR/klivelink-agent"
CONFIG="$CONFIG_DIR/klive.config.json"
LOG="$LOG_DIR/agent.log"
INSTALL_LOG="$LOG_DIR/install.log"
SERVICE_NAME="klivelink.service"
PLIST_LABEL="com.kliveLink.agent"
PLIST="$HOME/Library/LaunchAgents/${PLIST_LABEL}.plist"
if [ "$SYSTEM" = "1" ] && [ "$OS_KEY" = "osx" ]; then
    PLIST="/Library/LaunchDaemons/${PLIST_LABEL}.plist"
fi

mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$LOG_DIR" 2>/dev/null || true
touch "$INSTALL_LOG" 2>/dev/null || true

log() { printf '%s\n' "$*" | tee -a "$INSTALL_LOG" >&2; }
fail() { log "ERROR: $*"; exit 1; }

fetch() {
    url="$1"
    dest="$2"
    rm -f "$dest"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --retry 5 --retry-delay 2 --connect-timeout 20 -o "$dest" "$url" && return 0
    fi
    if command -v wget >/dev/null 2>&1; then
        wget -q -T 20 -t 5 -O "$dest" "$url" && return 0
    fi
    rm -f "$dest"
    return 1
}

fetch_text() {
    url="$1"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --retry 3 --connect-timeout 20 "$url" 2>/dev/null && return 0
    fi
    if command -v wget >/dev/null 2>&1; then
        wget -q -T 20 -t 3 -O - "$url" 2>/dev/null && return 0
    fi
    return 1
}

stop_agent() {
    if [ "$OS_KEY" = "linux" ]; then
        if [ "$SYSTEM" = "1" ]; then
            systemctl stop "$SERVICE_NAME" 2>/dev/null || true
        else
            systemctl --user stop "$SERVICE_NAME" 2>/dev/null || true
        fi
        pkill -f "$BIN" 2>/dev/null || true
    else
        launchctl bootout "gui/$(id -u)/${PLIST_LABEL}" 2>/dev/null || true
        launchctl bootout "system/${PLIST_LABEL}" 2>/dev/null || true
        pkill -f "$BIN" 2>/dev/null || true
    fi
}

if [ "$UNINSTALL" = "1" ]; then
    log "Removing KliveLink from $INSTALL_DIR"
    stop_agent
    if [ "$OS_KEY" = "linux" ]; then
        if [ "$SYSTEM" = "1" ]; then
            systemctl disable "$SERVICE_NAME" 2>/dev/null || true
            rm -f "/etc/systemd/system/$SERVICE_NAME"
            systemctl daemon-reload 2>/dev/null || true
        else
            systemctl --user disable "$SERVICE_NAME" 2>/dev/null || true
            rm -f "$HOME/.config/systemd/user/$SERVICE_NAME"
            systemctl --user daemon-reload 2>/dev/null || true
        fi
        rm -f "$HOME/.config/autostart/klivelink.desktop"
        if [ -f "$HOME/.profile" ]; then
            sed -i.bak '/# KliveLink agent/d; /klivelink-agent/d' "$HOME/.profile" 2>/dev/null || true
        fi
    else
        rm -f "$PLIST"
    fi
    rm -rf "$INSTALL_DIR" "$CONFIG_DIR" "$LOG_DIR"
    log "KliveLink removed."
    exit 0
fi

log "KliveLink bootstrap: $OS_NAME $ARCH_NAME ($RID)"

# Bootstrap config. Environment overrides make recovery possible if /bootstrap is down.
BOOTSTRAP=$(fetch_text "$BASE_URL/klivelink/v2/bootstrap" || true)
SERVER="${KLIVELINK_SERVER:-}"
TOKEN="${KLIVELINK_TOKEN:-}"
AUTO_TIER="${KLIVELINK_AUTO_TIER:-}"
if [ -z "$SERVER" ] && [ -n "$BOOTSTRAP" ]; then
    SERVER=$(printf '%s' "$BOOTSTRAP" | sed -n 's/.*"server"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
fi
if [ -z "$TOKEN" ] && [ -n "$BOOTSTRAP" ]; then
    TOKEN=$(printf '%s' "$BOOTSTRAP" | sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
fi
if [ -z "$AUTO_TIER" ] && [ -n "$BOOTSTRAP" ]; then
    AUTO_TIER=$(printf '%s' "$BOOTSTRAP" | sed -n 's/.*"autoTier"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
fi
AUTO_TIER="${AUTO_TIER:-T1}"
[ -n "$SERVER" ] || fail "Could not obtain the server URL from $BASE_URL/klivelink/v2/bootstrap"
[ -n "$TOKEN" ] || fail "Could not obtain an enrollment token from $BASE_URL/klivelink/v2/bootstrap"

TMP=$(mktemp -d "${TMPDIR:-/tmp}/klivelink.XXXXXX" 2>/dev/null || mktemp -d)
trap 'rm -rf "$TMP"' EXIT INT TERM
ARCHIVE="$TMP/$ARCHIVE_NAME"
CHECKSUM="$TMP/$ARCHIVE_NAME.sha256"

log "Downloading $ARCHIVE_NAME ..."
if ! fetch "$BASE_URL/klivelink/v2/agent?rid=$RID" "$ARCHIVE"; then
    log "Server mirror failed; trying GitHub release..."
    fetch "$GITHUB_URL/$ARCHIVE_NAME" "$ARCHIVE" || fail "Download failed from both mirror and GitHub."
fi

if fetch_text "$BASE_URL/klivelink/v2/agent/hash?rid=$RID" > "$CHECKSUM" 2>/dev/null; then
    :
elif fetch "$GITHUB_URL/$ARCHIVE_NAME.sha256" "$CHECKSUM"; then
    :
else
    log "WARNING: checksum unavailable; will verify the binary with --self-test."
    rm -f "$CHECKSUM"
fi

if [ -s "$CHECKSUM" ]; then
    EXPECTED=$(awk '{print $1}' "$CHECKSUM" | head -n 1)
    ACTUAL=""
    if command -v sha256sum >/dev/null 2>&1; then
        ACTUAL=$(sha256sum "$ARCHIVE" | awk '{print $1}')
    elif command -v shasum >/dev/null 2>&1; then
        ACTUAL=$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')
    fi
    if [ -n "$ACTUAL" ] && [ "$ACTUAL" != "$EXPECTED" ]; then
        fail "Checksum mismatch (expected $EXPECTED, got $ACTUAL)."
    fi
fi

log "Extracting..."
tar -xzf "$ARCHIVE" -C "$TMP" || fail "Could not extract $ARCHIVE_NAME"
[ -f "$TMP/klivelink-agent" ] || fail "Archive did not contain klivelink-agent"
stop_agent
install -m 755 "$TMP/klivelink-agent" "$BIN" 2>/dev/null || { cp "$TMP/klivelink-agent" "$BIN"; chmod 755 "$BIN"; }

if [ "$OS_KEY" = "osx" ]; then
    xattr -dr com.apple.quarantine "$BIN" 2>/dev/null || true
    codesign --force --deep --sign - "$BIN" >/dev/null 2>&1 || true
fi

cat > "$CONFIG" <<EOF
{
  "server": "$SERVER",
  "token": "$TOKEN",
  "agentId": "",
  "autoTier": "$AUTO_TIER"
}
EOF
chmod 600 "$CONFIG"

log "Running self-test..."
if ! "$BIN" --self-test >>"$INSTALL_LOG" 2>&1; then
    fail "Agent failed its self-test. See $INSTALL_LOG"
fi

started=0
if [ "$OS_KEY" = "linux" ]; then
    if command -v systemctl >/dev/null 2>&1; then
        if [ "$SYSTEM" = "1" ]; then
            cat > "/etc/systemd/system/$SERVICE_NAME" <<EOF
[Unit]
Description=KliveLink agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart="$BIN" --config "$CONFIG"
Restart=always
RestartSec=10
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF
            if systemctl daemon-reload 2>/dev/null && systemctl enable --now "$SERVICE_NAME" 2>/dev/null; then started=1; fi
        else
            mkdir -p "$HOME/.config/systemd/user"
            cat > "$HOME/.config/systemd/user/$SERVICE_NAME" <<EOF
[Unit]
Description=KliveLink agent
After=network-online.target

[Service]
Type=simple
ExecStart="$BIN" --config "$CONFIG"
Restart=always
RestartSec=10

[Install]
WantedBy=default.target
EOF
            if systemctl --user daemon-reload 2>/dev/null && systemctl --user enable --now "$SERVICE_NAME" 2>/dev/null; then started=1; fi
        fi
    fi
    if [ "$started" = "0" ] && [ -n "${XDG_CURRENT_DESKTOP:-}" ]; then
        mkdir -p "$HOME/.config/autostart"
        cat > "$HOME/.config/autostart/klivelink.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=KliveLink
Exec="$BIN" --config "$CONFIG"
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
    fi
    if [ "$started" = "0" ]; then
        PROFILE="$HOME/.profile"
        if ! grep -q "klivelink-agent" "$PROFILE" 2>/dev/null; then
            printf '\n# KliveLink agent\nif ! pgrep -f "%s" >/dev/null 2>&1; then nohup "%s" --config "%s" >>"%s" 2>&1 & fi\n' "$BIN" "$BIN" "$CONFIG" "$LOG" >> "$PROFILE"
        fi
    fi
else
    mkdir -p "$(dirname "$PLIST")"
    cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>${PLIST_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${BIN}</string>
    <string>--config</string>
    <string>${CONFIG}</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>${LOG}</string>
  <key>StandardErrorPath</key><string>${LOG}</string>
</dict>
</plist>
EOF
    if [ "$SYSTEM" = "1" ]; then
        launchctl bootstrap system "$PLIST" 2>/dev/null && started=1 || true
        launchctl kickstart -k "system/${PLIST_LABEL}" 2>/dev/null || true
    else
        launchctl bootout "gui/$(id -u)/${PLIST_LABEL}" 2>/dev/null || true
        launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null && started=1 || true
        launchctl kickstart -k "gui/$(id -u)/${PLIST_LABEL}" 2>/dev/null || true
    fi
fi

if [ "$started" = "0" ] || ! pgrep -f "$BIN" >/dev/null 2>&1; then
    log "Service manager did not start the agent; using nohup fallback."
    nohup "$BIN" --config "$CONFIG" >>"$LOG" 2>&1 &
fi

sleep 3
if pgrep -f "$BIN" >/dev/null 2>&1; then
    log "KliveLink installed and running."
    log "Binary: $BIN"
    log "Config: $CONFIG"
    log "Log:    $LOG"
    exit 0
fi

log "Agent is not running. Last log lines:"
tail -n 40 "$LOG" 2>/dev/null || true
fail "Installation completed but the agent did not start."


