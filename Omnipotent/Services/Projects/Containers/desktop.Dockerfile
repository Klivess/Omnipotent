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
# agents themselves (apt via a script tool), never baked into per-project images.

FROM debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive \
    DISPLAY=:1 \
    DISPLAY_WIDTH=1280 \
    DISPLAY_HEIGHT=800

RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb x11vnc xfce4 xfce4-terminal dbus-x11 \
        firefox-esr \
        xdotool wmctrl xclip x11-utils \
        curl ca-certificates fonts-dejavu \
        python3 \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Non-root desktop user; /project is the shared per-project volume mount.
RUN useradd -m -s /bin/bash agent && mkdir -p /project && chown agent:agent /project

COPY desktop-entrypoint.sh /usr/local/bin/desktop-entrypoint.sh
RUN chmod +x /usr/local/bin/desktop-entrypoint.sh

USER agent
WORKDIR /home/agent

EXPOSE 5901
ENTRYPOINT ["/usr/local/bin/desktop-entrypoint.sh"]
