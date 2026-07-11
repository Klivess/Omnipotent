#!/bin/bash
# Entrypoint for the Projects desktop container: virtual display → window manager → RFB server.
# x11vnc runs with -forever (survive client disconnects) and no auth — the host port is bound
# to 127.0.0.1 on the Docker host only; see desktop.Dockerfile header for the security model.
#
# Reliability: only Xvfb (the display) and x11vnc (the RFB server the live view needs) are
# treated as critical. The desktop environment (dbus/XFCE) is best-effort — deliberately NOT
# under `set -e`, so a transient dbus/XFCE hiccup can never abort the script before x11vnc and
# drop the container into a Docker restart loop where the live view "never gets its first frame".
set -uo pipefail

W="${DISPLAY_WIDTH:-1920}"
H="${DISPLAY_HEIGHT:-1080}"
export DISPLAY=:1
export HOME="${HOME:-/home/agent}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp/runtime-agent}"
mkdir -p "$XDG_RUNTIME_DIR" "$HOME/.config"
chmod 700 "$XDG_RUNTIME_DIR"
umask 077

# Docker restarts preserve the container filesystem. Xvfb can leave these behind after an
# unclean stop, producing an endless restart loop in which x11vnc accepts no frames.
rm -f /tmp/.X1-lock /tmp/.X11-unix/X1 2>/dev/null || true

Xvfb :1 -screen 0 "${W}x${H}x24" -nolisten tcp &
XVFB_PID=$!

# Wait for the display to accept connections before starting clients. This IS critical — a
# missing display is the one condition where restarting (via the container restart policy) can
# actually help, so hard-exit and let Docker recreate a clean X server.
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

# Keep the session-bus address in a mode-600 shell fragment. Docker exec processes do not inherit
# PID 1's runtime environment, so computer_terminal sources this before launching GUI programs.
# Best-effort: a dbus failure must not stop the RFB server from coming up.
if DBUS_ENV="$(dbus-launch --sh-syntax 2>/dev/null)"; then
    eval "$DBUS_ENV"
    printf 'export DBUS_SESSION_BUS_ADDRESS=%q\n' "$DBUS_SESSION_BUS_ADDRESS" > /tmp/desktop-session.env 2>/dev/null || true
else
    echo "dbus-launch failed; continuing without a session bus" >&2
    : > /tmp/desktop-session.env 2>/dev/null || true
fi

# Desktop environment — best-effort; the live view only needs Xvfb + x11vnc below.
startxfce4 >/tmp/xfce.log 2>&1 &

# XFCE's compositor is wasted work in an off-screen framebuffer and is a common source of
# partially painted/black rectangles. Wait briefly for xfwm, then disable compositing.
for i in $(seq 1 50); do
    if wmctrl -m >/dev/null 2>&1; then break; fi
    sleep 0.1
done
xfconf-query -c xfwm4 -p /general/use_compositing -s false >/dev/null 2>&1 || true

# The RFB server is the container's critical foreground process (PID 1 after exec): if it ever
# dies, the container exits and Docker's restart policy brings the whole desktop back cleanly.
exec x11vnc -display :1 -rfbport 5901 -shared -forever -nopw -noxdamage \
    -wait 10 -defer 10 -xkb -repeat -ncache 0
