# Stock linuxserver image for the pinned upstream release, with only Sonarr.Core.dll replaced.
# The FROM line must match BASE_IMAGE in upstream.env (build.sh checks this).
FROM lscr.io/linuxserver/sonarr@sha256:c8c4a3dcb49e1bd3ee19cf33e128837fdd6615e7a6b69cf02326133e193e4c29
COPY artifacts/Sonarr.Core.dll /app/sonarr/bin/Sonarr.Core.dll
