# cut

minimal video trimmer for windows. open a video, cut parts out, control each audio track, export.

- plays anything (mpv under the hood): mp4, mkv, mov, webm, avi, ts, flv, ...
- cut multiple regions out of the middle, not just the ends
- per audio track: include/exclude, mute, volume (game + mic recordings)
- lossless export by default (stream copy, original quality), precise re-encode mode when the exact frame matters
- exports with ffmpeg

## keys

| key | action |
| --- | --- |
| space | play / pause |
| left / right | frame step |
| shift + left / right | jump 1s |
| s | split at playhead |
| delete | remove selected segment |
| ctrl + z / y | undo / redo |
| ctrl + o | open |
| ctrl + e | export |
| ctrl + wheel on timeline | zoom |

## build

windows build only, done by github actions: publish, stage ffmpeg/ffprobe/libmpv next to the exe, run a headless selftest (real exports, verified with ffprobe), then pack an inno setup installer and a portable zip. tags `v*` create a release.

local compile check from any os: `dotnet build src/Cut.csproj`
