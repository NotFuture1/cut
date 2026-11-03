# changelog

## v0.2.1

cut is now a small editor, not just a trimmer. everything is faster, and new versions install over the old one.

**see exactly where you cut**
- cut edges are 1-pixel lines on the exact frame, with a bracket pointing at what you keep. dragging an edge shows that frame in the preview and a bubble with its time and frame number.
- edges snap to the playhead, other edges and whole frames (hold alt to stop). zoom in and the ruler shows every frame.
- fast mode shows in amber where a copied cut really starts (the keyframe before it), measured with ffmpeg itself, so the timeline matches the file you get.

**editing**
- a real timeline: layers, the video with thumbnails, and one lane per audio track with waveforms.
- drag across any lane to mark a part: `x` cuts it, `m` mutes it. `i`/`o` mark in and out, `s` splits, right-click for everything else.
- mute just part of one track. per-track volume, solo while listening, leave a track out, drag tracks to reorder them.
- layers: images (with transparency), gifs, videos and music. drop them on the timeline, drag them on the video to place them, drag a corner to resize, trim and move them in time.
- the preview plays the real mix (every track, volumes, mutes, layers) and skips the cut parts.
- a details panel for whatever is selected, with exact time fields (up/down nudges a frame).
- edits are saved per video and come back when you reopen it. full undo/redo.

**export**
- the quality slider is gone: pick a size instead. live estimate ("≈ 9.4 MB · 96% smaller than the original") and presets: original, high, small, fits discord · 10 mb, tiny. resolution and frame rate drop automatically when the size needs it.
- exact mode re-encodes on your gpu when it can (nvenc, quick sync, amf), decodes only the parts you keep, and falls back to the cpu if anything goes wrong.
- fast mode cuts video and audio separately at measured points and joins them with exact lengths, so audio can't drift across cuts.
- mix every track into one (what discord plays) or keep them separate.

**installing and updating**
- new versions install straight over the old one: same folder, settings kept, a running cut is closed for you, no uninstall.
- cut tells you when an update is out and can install it itself.
- smaller download: ffmpeg ships as one shared build.
