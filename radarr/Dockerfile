# Stock linuxserver Radarr, pinned by digest, with the patched Radarr.Core.dll.
# BASE_IMAGE defaults to the digest in upstream.env; build.sh passes it explicitly.
ARG BASE_IMAGE=lscr.io/linuxserver/radarr@sha256:044788f5508b7f67a3e0368cdd142c77c4d4cd5aec2efaba53e1a68b6b6231e7
FROM ${BASE_IMAGE}
COPY artifacts/Radarr.Core.dll /app/radarr/bin/Radarr.Core.dll
