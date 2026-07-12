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
        sudo \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Non-root desktop user with passwordless sudo — the desktop is the agent's own machine, and
# installing the tools a task needs (apt/pip) is part of the job. Security model unchanged:
# the container boundary, not the user boundary, is what isolates a desktop from the host.
# /project is the shared per-project volume mount.
RUN useradd -m -s /bin/bash agent && mkdir -p /project && chown agent:agent /project \
    && echo 'agent ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/agent && chmod 0440 /etc/sudoers.d/agent

COPY desktop-entrypoint.sh /usr/local/bin/desktop-entrypoint.sh
COPY browser-inspect.py /usr/local/bin/browser-inspect.py
RUN chmod +x /usr/local/bin/desktop-entrypoint.sh /usr/local/bin/browser-inspect.py

USER agent
WORKDIR /home/agent

EXPOSE 5901
ENTRYPOINT ["/usr/local/bin/desktop-entrypoint.sh"]
