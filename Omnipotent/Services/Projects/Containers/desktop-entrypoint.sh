#!/bin/bash
# Entrypoint for a complete Projects desktop: Xvfb -> XFCE session -> VNC.
#
# x11vnc is the critical foreground process. XFCE components are supervised separately so a
# panel or desktop crash cannot tear down the display, and a VNC failure still lets Docker's
# restart policy recover the container. The VNC port is bound to the Docker host's loopback
# interface by ContainerOrchestrator, so passwordless RFB is not exposed to the network.
set -uo pipefail

W="${DISPLAY_WIDTH:-1920}"
H="${DISPLAY_HEIGHT:-1080}"
export DISPLAY=:1
export HOME="${HOME:-/home/agent}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp/runtime-agent}"
mkdir -p "$XDG_RUNTIME_DIR" "$HOME/.config" "$HOME/.cache/sessions"
chmod 700 "$XDG_RUNTIME_DIR"
umask 077

# Docker restarts preserve the container filesystem. Xvfb can leave these behind after an
# unclean stop, producing a restart loop before the display accepts connections.
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1 2>/dev/null || true

Xvfb :1 -screen 0 "${W}x${H}x24" -nolisten tcp &
XVFB_PID=$!

for i in $(seq 1 100); do
    if xdpyinfo -display :1 >/dev/null 2>&1; then break; fi
    if ! kill -0 "$XVFB_PID" 2>/dev/null; then
        echo "Xvfb exited before display :1 became ready" >&2
        exit 1
    fi
    sleep 0.1
done
if ! xdpyinfo -display :1 >/dev/null 2>&1; then
    echo "Display :1 did not become ready within 10 seconds" >&2
    exit 1
fi

# Keep a visible background during the short interval before xfdesktop paints the real desktop,
# and prevent virtual-display power management from blanking an unattended agent desktop.
xsetroot -solid "#3465a4" >/dev/null 2>&1 || true
xset -dpms >/dev/null 2>&1 || true
xset s off >/dev/null 2>&1 || true
xset s noblank >/dev/null 2>&1 || true

# Seed XFCE's packaged defaults once. This supplies the normal applications menu, taskbar,
# desktop settings, keyboard shortcuts, and file-manager integration without overwriting any
# customisations the agent makes later in the container.
if [ ! -e "$HOME/.config/xfce4/xfconf/xfce-perchannel-xml/xfce4-panel.xml" ]; then
    mkdir -p "$HOME/.config/xfce4"
    cp -a /etc/xdg/xfce4/. "$HOME/.config/xfce4/" 2>/dev/null || true
fi
# Never restore process IDs from an XFCE session saved before a container restart.
rm -rf "$HOME/.cache/sessions"/* 2>/dev/null || true

# Docker exec processes do not inherit PID 1's environment. Persist the display and session-bus
# values so computer_terminal and application-launch tools join this same visible desktop.
if DBUS_ENV="$(dbus-launch --sh-syntax 2>/dev/null)"; then
    eval "$DBUS_ENV"
else
    echo "dbus-launch failed; a complete XFCE desktop cannot start" >&2
    exit 1
fi
{
    printf 'export DISPLAY=%q\n' "$DISPLAY"
    printf 'export HOME=%q\n' "$HOME"
    printf 'export XDG_RUNTIME_DIR=%q\n' "$XDG_RUNTIME_DIR"
    printf 'export DBUS_SESSION_BUS_ADDRESS=%q\n' "${DBUS_SESSION_BUS_ADDRESS:-}"
} > /tmp/desktop-session.env 2>/dev/null || true
chmod 600 /tmp/desktop-session.env 2>/dev/null || true

start_xfce_session() {
    if ! pgrep -u "$(id -u)" -x xfce4-session >/dev/null 2>&1; then
        DISPLAY=:1 xfce4-session >/tmp/xfce4-session.log 2>&1 &
    fi
}

start_xfce_component() {
    local process_name="$1"
    shift
    if ! pgrep -u "$(id -u)" -x "$process_name" >/dev/null 2>&1; then
        DISPLAY=:1 "$@" >>"/tmp/${process_name}.log" 2>&1 &
    fi
}

start_xfce_session

# A first-run or damaged XFCE configuration can omit a component. Give the session a chance to
# create the normal shell, then explicitly fill only missing pieces. These are desktop-shell
# processes, never Chromium, so browser ownership remains with the single-flight browser launcher.
for i in $(seq 1 100); do
    if pgrep -x xfwm4 >/dev/null 2>&1 &&
       pgrep -x xfdesktop >/dev/null 2>&1 &&
       pgrep -x xfce4-panel >/dev/null 2>&1; then
        break
    fi
    sleep 0.1
done
start_xfce_component xfwm4 xfwm4 --compositor=off
start_xfce_component xfdesktop xfdesktop
start_xfce_component xfce4-panel xfce4-panel

# Keep the ordinary desktop usable if an individual shell component exits. Readiness checks all
# three components, so a persistently broken shell is reported and rebuilt instead of being
# mistaken for a healthy black framebuffer.
(
    while kill -0 "$XVFB_PID" 2>/dev/null; do
        start_xfce_session
        start_xfce_component xfwm4 xfwm4 --compositor=off
        start_xfce_component xfdesktop xfdesktop
        start_xfce_component xfce4-panel xfce4-panel
        sleep 5
    done
) &

exec x11vnc -display :1 -rfbport 5901 -shared -forever -nopw -noxdamage \
    -wait 10 -defer 10 -xkb -repeat -ncache 0 -o /tmp/x11vnc.log
