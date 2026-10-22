# ffprobe, as it answers

What the real `ffprobe` (FFmpeg 9.0.2, the Gyan.dev full build) printed for two short files made with the real `ffmpeg`, kept as the tool printed
them (`ffprobe -v quiet -print_format json -show_streams -show_format <file>`). The API tests of the tracks routes answer Weir's probe with these.

| File | What the file holds |
| --- | --- |
| `mkv-with-subtitles-and-font.json` | A Matroska file: one video track, two audio tracks (English default, Japanese), two subtitle tracks (English forced, French) and an attached font |
| `mp4-with-cover-and-timecode.json` | An MP4 file: one video track, one English audio track, two data tracks (a timecode and its companion) and a cover picture (a video track marked `attached_pic`) |

Recorded on 21 October 2026 on a Windows PC. The files themselves are not kept: they were generated from `testsrc` and `sine` sources, with a
system font attached to the Matroska file. A recording is never edited; to change one, record again.
