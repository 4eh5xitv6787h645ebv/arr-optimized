#!/bin/sh
# Generates the small sample media used by FFProbeCacheFixture (needs ffmpeg with libx264/libx265).
# Output: <outdir>/ffprobe_cache_*.{mkv,ts,mp4}
set -eu
out=${1:-Files/Media}
mkdir -p "$out"
cd "$out"
q="-hide_banner -loglevel error -y"

cat > subs.srt <<'SRT'
1
00:00:00,000 --> 00:00:01,500
Hello

2
00:00:01,500 --> 00:00:02,900
World
SRT

# 1) Matroska: H.264 + two audio tracks (stereo AAC eng with title, 5.1 AC3 ger) + forced/SDH subtitles, container title.
ffmpeg $q -f lavfi -i testsrc2=size=320x180:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 \
  -f lavfi -i "sine=frequency=660:sample_rate=48000" -i subs.srt -t 3 \
  -map 0:v -map 1:a -map 2:a -map 3:s \
  -c:v libx264 -preset ultrafast -crf 40 -pix_fmt yuv420p \
  -c:a:0 aac -b:a:0 64k -ac:a:0 2 -c:a:1 ac3 -b:a:1 192k -ac:a:1 6 -c:s srt \
  -metadata title="Cache Sample" \
  -metadata:s:a:0 language=eng -metadata:s:a:0 title="Stereo Commentary" \
  -metadata:s:a:1 language=ger \
  -metadata:s:s:0 language=eng -disposition:s:0 forced+hearing_impaired \
  ffprobe_cache_h264.mkv

# 2) MPEG-TS: MPEG-2 video + MP2 audio.
ffmpeg $q -f lavfi -i testsrc2=size=320x180:rate=25 -f lavfi -i sine=frequency=500:sample_rate=48000 -t 3 \
  -c:v mpeg2video -q:v 20 -c:a mp2 -b:a 96k -metadata:s:a:0 language=fre \
  ffprobe_cache_mpeg2.ts

# 3) HEVC Main10 PQ with mastering display / content light metadata (exercises the GetFrames pass).
ffmpeg $q -f lavfi -i testsrc2=size=320x180:rate=24 -f lavfi -i sine=frequency=300:sample_rate=48000 -t 2 \
  -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le \
  -x265-params "log-level=error:crf=35:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400" \
  -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -tag:v hvc1 \
  -c:a aac -b:a 48k ffprobe_cache_hevc_pq.mp4

# 4) Video only (no audio stream: exercises the 150 MB second ffprobe pass).
ffmpeg $q -f lavfi -i testsrc2=size=320x180:rate=24 -t 2 -c:v libx264 -preset ultrafast -crf 40 -pix_fmt yuv420p \
  ffprobe_cache_noaudio.mkv

rm -f subs.srt
ls -l ffprobe_cache_*
