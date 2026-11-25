# Stock linuxserver image for the pinned upstream release, with Sonarr.Core.dll, Sonarr.Api.V3.dll and
# the web UI (bin/UI) replaced by builds of the patched source. Everything else is the stock image.
# The FROM line must match BASE_IMAGE in upstream.env (build.sh checks this).
FROM lscr.io/linuxserver/sonarr@sha256:c8c4a3dcb49e1bd3ee19cf33e128837fdd6615e7a6b69cf02326133e193e4c29
# The UI file names carry content hashes, so the stock folder is replaced, not merged.
RUN rm -rf /app/sonarr/bin/UI
COPY artifacts/Sonarr.Core.dll artifacts/Sonarr.Api.V3.dll /app/sonarr/bin/
COPY artifacts/UI /app/sonarr/bin/UI
