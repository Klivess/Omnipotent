#!/bin/bash
# Entrypoint for the Projects desktop container: virtual display → window manager → RFB server.
# x11vnc runs with -forever (survive client disconnects) and no auth — the host port is bound
# to 127.0.0.1 on the Docker host only; see desktop.Dockerfile header for the security model.
set -e

W="${DISPLAY_WIDTH:-1280}"
H="${DISPLAY_HEIGHT:-800}"

Xvfb :1 -screen 0 "${W}x${H}x24" -nolisten tcp &
XVFB_PID=$!

# Wait for the display to accept connections before starting clients.
for i in $(seq 1 50); do
    if xdpyinfo -display :1 >/dev/null 2>&1; then break; fi
    sleep 0.1
done

dbus-launch startxfce4 &

exec x11vnc -display :1 -rfbport 5901 -shared -forever -nopw -noxdamage -quiet
