# Projects desktop container — one isolated Linux desktop per agent (or shared per project).
#
# Design note: the plan named KasmVNC, but the KM website's live view reuses Omnipotent's
# existing JPEG-over-WebSocket pattern (the host-side VncTransport streams frames), so a
# web-native VNC server buys nothing here. A classic RFB server (x11vnc over Xvfb) is what
# the in-house VncTransport RFB client speaks, with zero protocol wrapping. Security type is
# None by design: the port is only ever bound to the Docker host's 127.0.0.1 — network
# isolation is the auth boundary (see ContainerOrchestrator).
#
# Build:  docker build -t omnipotent/projects-desktop:latest -f desktop.Dockerfile .
# The image is shared by all projects; project-specific software is installed post-boot by
# agents themselves (passwordless sudo → apt), never baked into per-project images.

FROM debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive \
    DISPLAY=:1 \
    DISPLAY_WIDTH=1920 \
    DISPLAY_HEIGHT=1080

RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb x11vnc xfce4 xfce4-terminal dbus-x11 \
        firefox-esr chromium \
        xdotool wmctrl xclip x11-utils \
        curl ca-certificates fonts-dejavu \
        python3 python3-pip python3-venv python3-websocket \
        ffmpeg \
        sudo \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Non-root desktop user with passwordless sudo — the desktop is the agent's own machine, and
# installing the tools a task needs (apt/pip) is part of the job. Security model unchanged:
# the container boundary, not the user boundary, is what isolates a desktop from the host.
# /project is the shared per-project volume mount.
RUN useradd -m -s /bin/bash agent && mkdir -p /project && chown agent:agent /project \
    && echo 'agent ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/agent && chmod 0440 /etc/sudoers.d/agent

# ── Baked browser-automation core (hybrid provisioning) ──
# The mission-critical automation stack is baked into the base image, NOT left to a fragile
# per-task `pip install` (that silent failure — a venv with only greenlet+pip and no browser —
# is exactly what stalled the first live project). Task-specific tools (niche libs) still install
# post-boot via the agent's passwordless sudo; only the browser/automation core is guaranteed here.
# Playwright lives in a well-known venv the preflight probes by path. OS libraries the browsers
# need are installed as root (install-deps); the browser builds download into the agent's cache
# (~/.cache/ms-playwright) so they're owned by the user that will launch them.
ENV KLIVE_PW_VENV=/opt/klive/venv
RUN mkdir -p /opt/klive && chown agent:agent /opt/klive
# apt-get update first: the base layer cleared the package lists, and `playwright install-deps`
# shells out to apt to install the browsers' shared-library dependencies.
RUN apt-get update \
    && python3 -m venv "$KLIVE_PW_VENV" \
    && "$KLIVE_PW_VENV/bin/pip" install --no-cache-dir --upgrade pip \
    && "$KLIVE_PW_VENV/bin/pip" install --no-cache-dir playwright \
    && "$KLIVE_PW_VENV/bin/playwright" install-deps chromium firefox \
    && chown -R agent:agent "$KLIVE_PW_VENV" \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
# Download the browser builds as the agent user so they land in ~agent/.cache/ms-playwright
# (owned by the user that will launch them), not root's cache.
USER agent
RUN "$KLIVE_PW_VENV/bin/playwright" install chromium firefox
USER root

COPY desktop-entrypoint.sh /usr/local/bin/desktop-entrypoint.sh
COPY browser-inspect.py /usr/local/bin/browser-inspect.py
RUN chmod +x /usr/local/bin/desktop-entrypoint.sh /usr/local/bin/browser-inspect.py

# Capability stamp — the preflight reads this to know what the image ships without probing each
# tool. Bump "imageVersion" whenever the baked capability set changes so the staleness check and
# the readiness summary stay meaningful.
RUN printf '%s\n' \
    '{"imageVersion":"2","capabilities":["display","chromium","firefox","browser-inspect","playwright","python3","ffmpeg"],"playwrightVenv":"/opt/klive/venv","display":":1"}' \
    > /etc/klive-desktop.json && chmod 0444 /etc/klive-desktop.json

USER agent
WORKDIR /home/agent

EXPOSE 5901
ENTRYPOINT ["/usr/local/bin/desktop-entrypoint.sh"]
