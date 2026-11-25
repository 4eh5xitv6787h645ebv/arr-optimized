# Stock linuxserver Radarr, pinned by digest, with Radarr.Core.dll, Radarr.Api.V3.dll and the web UI
# (bin/UI) replaced by builds of the patched source. Everything else is the stock image.
# BASE_IMAGE defaults to the digest in upstream.env; build.sh passes it explicitly.
ARG BASE_IMAGE=lscr.io/linuxserver/radarr@sha256:044788f5508b7f67a3e0368cdd142c77c4d4cd5aec2efaba53e1a68b6b6231e7
FROM ${BASE_IMAGE}
# The UI file names carry content hashes, so the stock folder is replaced, not merged.
RUN rm -rf /app/radarr/bin/UI
COPY artifacts/Radarr.Core.dll artifacts/Radarr.Api.V3.dll /app/radarr/bin/
COPY artifacts/UI /app/radarr/bin/UI
