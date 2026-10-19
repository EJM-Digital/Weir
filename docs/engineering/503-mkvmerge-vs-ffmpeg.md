# Trial: mkvmerge vs ffmpeg for Matroska output

Trial date: 2026-09-17. Answers [#503](https://github.com/EJM-Digital/Weir/issues/503).

**Outcome.** Adopted in [#548](https://github.com/EJM-Digital/Weir/issues/548). A workflow's writer is
`best` by default: mkvmerge writes Matroska when it is installed and ffmpeg writes everything else,
and a write that fails to run or fails output validation is rewritten by ffmpeg and validated again
(`apps/server/src/Weir.Core/Media/RemuxWriterChoice.cs`). `ffmpeg` is the other choice. ffmpeg's own
remux argv now maps attachment streams where the output container accepts them (finding 1). The
report below is the trial as run.

## Question

Does mkvmerge (MKVToolNix) produce measurably better, safe Matroska output than
Weir's ffmpeg stream-copy remux? Adopt it for `.mkv` writes only if the evidence
says so; ffmpeg keeps probing, validation and non-Matroska containers either way.

## Method

1. Generated a 15-file corpus locally with the bundled ffmpeg
   (`dist/windows/WeirServer/_internal/bin/ffmpeg`, `N-126416-g9997fd0606`,
   2026-09-05) using `-f lavfi` sources — script:
   `apps/server/tests/Trial503Harness/generate-corpus.sh` (see below).
2. For each file, built a representative plan (drop one audio track — the
   commentary-flagged one where present, else the last — keep every subtitle,
   set default/forced from the source's own disposition) and ran it through
   **the real production code**, `Weir.Core.Media.FfmpegCommands.BuildRemuxArgv`,
   token for token as the remux pass would call it. A throwaway console harness,
   `Trial503Harness` (not in `Weir.slnx`, not built by CI, not referenced by any
   production project — removed after this trial concluded; its source is at
   commit [`d26e4743`](https://github.com/EJM-Digital/Weir/commit/d26e4743)), does
   this and builds the equivalent `mkvmerge` command line for the same kept
   tracks/order/flags (`-d`/`-a`/`-s`, `--default-track-flag`,
   `--forced-display-flag`, `--track-order`), then runs both tools for real.
3. Compared outputs with `ffprobe -show_streams -show_format -show_chapters`
   (JSON) and `mkvinfo`: track count/order/codec/language/title, disposition
   flags, attachments, chapters, HDR-relevant colorimetry, file size and wall
   time. Ran `ffmpeg -v error -i <out> -f null -` on every output as a
   play-proxy decode check.
4. mkvmerge v100.0 (`C:\Program Files\MKVToolNix\mkvmerge.exe`) and mkvinfo
   alongside it. No other tool was downloaded (no `dovi_tool`, no
   `hdr10plus_tool`); where a check needed one, it is marked as a gap below.

Run it yourself (check out commit `d26e4743` first, or cherry-pick
`apps/server/tests/Trial503Harness` from it — the harness itself is not on `main`):

```powershell
bash apps/server/tests/Trial503Harness/generate-corpus.sh <ffmpeg-dir> <scratch>/corpus
dotnet run --project apps/server/tests/Trial503Harness -- `
  <scratch>/corpus <scratch>/out <ffmpeg-dir>/ffmpeg.exe <ffmpeg-dir>/ffprobe.exe `
  "C:\Program Files\MKVToolNix\mkvmerge.exe" "C:\Program Files\MKVToolNix\mkvinfo.exe"
```

Nothing under `<scratch>` is committed; `<scratch>/out/summary.md` and
`<scratch>/out/results/*.json` are the raw evidence this report is built from.

## Corpus and gaps

The bundled ffmpeg build is `--disable-libx264 --disable-libx265`: there is no
software encoder with 10-bit support (`libkvazaar`, the only software HEVC
encoder present, is 8-bit `yuv420p` only; `hevc_mf`/`hevc_qsv`/`hevc_vaapi`/
`hevc_nvenc`/`hevc_amf` all need hardware the trial machine did not have and
failed or were not attempted). That constrains the corpus more than the issue's plan
assumed:

| # | File | Covers | Gap, if any |
| --- | --- | --- | --- |
| 01 | `01_h264_multi_audio_ass_font` | H.264, AAC+AC-3 (one commentary), SRT+ASS, attached font | — |
| 02 | `02_hevc8_ac3_eac3` | HEVC 8-bit, AC-3(eng)+E-AC-3(jpn) | 10-bit not achievable (see above) |
| 03 | `03_hevc8_hdr_tag_truehd_chapters` | HEVC 8-bit tagged BT.2020/PQ colorimetry, TrueHD+AC-3, 2 chapters | No mastering-display/MaxCLL SEI (needs x265 or `hdr10plus_tool`/`dovi_tool`, neither available); TrueHD is ffmpeg's experimental encoder, no Atmos objects |
| 04 | `04_dts_flac_aac_multi_lang` | DTS(core)+FLAC+AAC, 3 languages, default flag | DTS is ffmpeg's experimental `dca` core encoder, not DTS-HD MA |
| 05 | `05_ass_font_srt` | ASS+attached font, SRT | — |
| 06 | `06_chapters_single_edition` | Chapters, single edition | ffmpeg's CLI (`ffmetadata`) has no way to author a second edition or ordered chapters at all — see finding 6 |
| 07 | `07_vfr_video` | Genuinely variable frame timestamps (concatenated 23.976/30 fps segments) | Real anime VFR patterns differ; this only proves both muxers carry through irregular timestamps identically |
| 08 | `08_long_sparse_90min` | 90-minute duration at 1 fps (small file, long timeline) | — |
| 09 | `09_multi_lang_flags` | 4 audio (default/commentary/description) + 3 subtitle tracks | The `descriptions` disposition did not stick during generation (ffmpeg CLI limitation); noted, not blocking |
| 10 | `10_two_font_attachments` | Two font attachments | — |
| 11 | `11_sdh_forced_default_subs` | SDH-named + forced+default subtitle combination | — |
| 12 | `12_truncated` | Byte-truncated Matroska (the #494/#539 shape) | — |
| 13 | `13_surround_vs_stereo` | 5.1 AC-3 vs stereo AAC | — |
| 14 | `14_embedded_poster` | Second video stream (intended as an attached-picture poster) | Could not force the Matroska-equivalent of MP4's `attached_pic` disposition from the ffmpeg CLI, nor get a fallback frame rate of `0/0` from a single still frame — `MetadataStreams.IsImageStream`'s heuristics were not exercised. Needs a real sample with a genuine embedded poster track. |
| 15 | `15_webvtt_and_srt` | WebVTT + SRT | — |

**Not achievable at all in this environment** (documented as the issue asked,
not worked around):

- **Dolby Vision (profile 7/8) and HDR10+.** No encoder, no RPU injection tool
  (`dovi_tool` explicitly excluded from this trial). Needs real Dolby Vision
  samples plus `dovi_tool info` on both tools' output, exactly as the issue
  specifies — this is the single largest untested risk, given that Dolby Vision
  configuration records were the issue's main concern.
- **TrueHD Atmos object audio.** ffmpeg's `truehd` encoder is core-only
  (experimental, no joint-object-coding metadata); a real Atmos sample is
  needed to test whether either muxer disturbs the Atmos metadata blocks.
- **PGS (bitmap) subtitles.** No PGS encoder exists in ffmpeg (PGS is
  effectively decode-only tooling everywhere); needs a real disc-sourced PGS
  track.
- **True 10-bit HEVC + HDR10 static metadata (mastering display / MaxCLL
  SEI).** See above; the closest achievable approximation is BT.2020/PQ/BT.2020-NCL
  **VUI colorimetry** tags baked into the bitstream (file 03), which — being
  inside the codec bitstream, not container metadata — both tools copied
  byte-identically (finding 3). That result says nothing about container-level
  HDR10 static metadata or Dolby Vision RPUs, which live differently in a
  Matroska file and are exactly what real samples are needed for.
- **DTS-HD MA / DTS:X.** Only ffmpeg's experimental `dca` core encoder is
  available; a real DTS-HD MA sample is needed to check extension-substream
  handling.

## Per-file results

| File | ffmpeg ms | mkvmerge ms | ffmpeg bytes | mkvmerge bytes | tracks match | flags match | attachments (src/ffmpeg/mkvmerge) | notes |
| --- | ---: | ---: | ---: | ---: | --- | --- | --- | --- |
| 01_h264_multi_audio_ass_font | 22 | 36 | 162,423 | 167,935 | yes | yes | 1 / 0 / 1 | ffmpeg dropped the attachment |
| 02_hevc8_ac3_eac3 | 19 | 35 | 519,379 | 524,436 | yes | yes | 0 / 0 / 0 | |
| 03_hevc8_hdr_tag_truehd_chapters | 29 | 48 | 622,389 | 628,251 | yes | yes | 0 / 0 / 0 | colorimetry identical (finding 3) |
| 04_dts_flac_aac_multi_lang | 23 | 36 | 903,835 | 906,927 | yes | yes | 0 / 0 / 0 | |
| 05_ass_font_srt | 20 | 32 | 121,953 | 127,694 | yes | yes | 1 / 0 / 1 | ffmpeg dropped the attachment |
| 06_chapters_single_edition | 21 | 32 | 161,464 | 166,422 | yes | yes | 0 / 0 / 0 | both kept 2/2 chapters |
| 07_vfr_video | 20 | 34 | 171,669 | 176,488 | yes | yes | 0 / 0 / 0 | both reproduce the same source DTS irregularity identically (not a muxer defect) |
| 08_long_sparse_90min | 404 | 476 | 64,210,882 | 63,018,208 | yes | yes | 0 / 0 / 0 | mkvmerge ~1.9% smaller here |
| 09_multi_lang_flags | 23 | 35 | 223,092 | 227,732 | yes | yes | 0 / 0 / 0 | |
| 10_two_font_attachments | 20 | 31 | 121,710 | 127,238 | yes | yes | 2 / 0 / 2 | ffmpeg dropped both attachments |
| 11_sdh_forced_default_subs | 19 | 33 | 121,435 | 127,076 | yes | yes | 0 / 0 / 0 | forced+default combo round-trips on both |
| 12_truncated | 19 | 31 | 79,731 | 84,987 | yes | yes | 0 / 0 / 0 | both silently mux only the readable prefix — finding 4 |
| 13_surround_vs_stereo | 20 | 32 | 269,819 | 275,023 | yes | yes | 0 / 0 / 0 | 5.1 channel layout identical |
| 14_embedded_poster | 20 | 32 | 128,057 | 133,333 | yes | yes | 0 / 0 / 0 | see corpus gap above |
| 15_webvtt_and_srt | 20 | 33 | 121,420 | 127,065 | yes | yes | 0 / 0 / 0 | |

"tracks match" / "flags match" compare `(type, codec, language)` and
`(default, forced)` per kept track between the two outputs — every file
matched. Decode check (`ffmpeg -v error -i out -f null -`) was clean on every
output from both tools except file 07, where both outputs reproduce the exact
same "non monotonically increasing dts" warning from the source (a corpus
artifact from concatenating independently-generated segments, not something
either muxer introduced — the messages are byte-identical between the two
outputs down to the frame numbers).

Total wall time across the corpus: ffmpeg ≈699 ms, mkvmerge ≈956 ms. The gap is
10–70 ms per file and shrinks in relative terms as files grow (72 ms of 400+ ms
on the 90-minute file); it is process-startup overhead, not muxing throughput —
both tools stream-copy at effectively the same speed. File size differences
are single-digit-KB either way except the 90-minute sparse file, where
mkvmerge was ~1.2 MB (1.9%) smaller. Neither difference is decisive.

## Key differences found, with evidence

### 1. ffmpeg's remux argv silently drops every Matroska attachment

`FfmpegCommands.BuildRemuxArgv` maps only `plan.VideoIndices`, `plan.Audio` and
`plan.Subtitles` — never a stream with `codec_type == attachment`. Confirmed on
files 01, 05 and 10 (font attachments): the ffmpeg output has 0 attachment
streams where the source had 1 or 2; mkvmerge's output (which keeps
attachments by default, no flag needed) kept all of them. Minimal repro,
independent of the plan machinery:

```
ffmpeg -hide_banner -loglevel error -xerror -err_detect explode -nostdin -y \
  -i 01_h264_multi_audio_ass_font.mkv \
  -map 0:0 -map 0:1 -map 0:2 -map 0:3 -map 0:4 -c copy out.mkv
# ffprobe out.mkv: 5 streams (video, 2 audio, 2 subtitle) — the font is gone.
```

This is a real, currently-shipping defect, independent of the mkvmerge
decision: **any Weir workflow with ASS/SSA subtitles carrying a custom font
attachment loses that font on every remux**, which silently degrades to a
fallback font (or squares/tofu) in players that don't ship the same font.
The issue suspected exactly this.

### 2. ffmpeg's output carries stale per-track statistics tags; mkvmerge's doesn't

`mkvinfo` on the file-02 outputs:

```
=== ffmpeg output ===
+ Tags
| + Tag
|   + Name: ENCODER / String: Lavf63.6.100
| + Tag  (attached to the video track)
|   + Name: ENCODER / String: Lavc63.10.100 libkvazaar
|   + Name: DURATION / String: 00:00:04.000000000
| + Tag  (attached to the audio track)
|   + Name: ENCODER / String: Lavc63.10.100 ac3
|   + Name: DURATION / String: 00:00:04.041000000

=== mkvmerge output ===
(no Tags element at all)
```

ffmpeg's remux is a stream copy — it never re-encodes — yet it carries forward
the source's `ENCODER`/`DURATION` tags from whatever *originally* produced
those streams, unchanged. Reproduced with a completely default, flagless
`mkvmerge -o out.mkv in.mkv` on the same source: mkvmerge drops those tags
outright rather than propagate stale ones. Neither behaviour is strictly
correct. mkvmerge's is cleaner (no misleading lineage metadata survives a
copy); ffmpeg's at least preserves *some* record of how the elementary stream
was made. The issue named track statistics tags as an edge case to check.

### 3. HDR-relevant colorimetry is bitstream-level and copies identically either way

File 03's HEVC stream was VUI-tagged BT.2020/PQ/BT.2020-NCL via
`hevc_metadata` bitstream filter. Both outputs report identical
`color_range`/`color_space`/`color_transfer`/`color_primaries`, because this
data lives inside the HEVC bitstream, not container metadata — a plain stream
copy preserves it regardless of muxer. This is a *non-finding* worth stating
plainly: for whatever HDR signaling lives in the codec bitstream itself,
ffmpeg and mkvmerge are equivalent. The open question is entirely about
container-level HDR10 static metadata and Dolby Vision configuration
records/RPUs, neither of which this trial could generate (see gaps).

### 4. Neither tool errors on truncated Matroska input — mkvmerge is not a substitute for Weir's own validation

File 12 is `01`'s source cut to exactly half its bytes. Its own
`format.duration` still reports the original 4.023 s (the stale-header shape
behind #494/#539). Both ffmpeg and mkvmerge:

- exited 0,
- produced a shorter file containing only the data before the cut, and
- **correctly** wrote their own output's duration as 2.066 s — no misleading
  duration survives to the *output* header — but neither raised any warning or
  non-zero exit that a caller could act on.

```
source (truncated):      format.duration = 4.023000
ffmpeg  output mkvinfo:   Duration: 00:00:02.066000000  (exit 0, stderr: "File ended prematurely")
mkvmerge output mkvinfo:  Duration: 00:00:02.066000000  (exit 0, stderr: empty)
```

mkvmerge's stderr did not mention the truncation. ffmpeg printed
"File ended prematurely" at error level, so it does surface, but as a message,
not a failure.
**Conclusion: whichever muxer Weir uses, `ValidateRemuxOutputAsync` /
`ValidateMediaIntegrityAsync` (the #539 fix) stays load-bearing.** Adopting
mkvmerge does not let Weir remove or weaken that safety net.

### 5. ffmpeg's disposition flags are binary and clobber anything else on kept audio tracks

Reading `FfmpegCommands.BuildRemuxArgv` directly (not corpus-dependent): the
audio disposition loop is

```csharp
args.AddRange([$"-disposition:a:{i}", plan.Audio[i].Default ? "default" : "0"]);
```

This sets *only* `default` or clears to `0` — it never re-applies `comment`,
`dub`, `original`, `descriptions` or `visual_impaired` even when
`PlannedTrack.Commentary` says the kept track is a commentary track. Today
this is largely moot because Weir's own rules typically drop commentary
tracks rather than keep them tagged; but any future rule that *keeps* a
commentary/description audio track while remuxing through ffmpeg will silently
strip that flag from the container. An mkvmerge-based writer, by contrast,
preserves a kept track's original disposition unless told otherwise — so a
writer abstraction must decide, explicitly, whether to preserve or continue to
reset non-default audio flags, rather than inherit ffmpeg's current behaviour
by default.

### 6. ffmpeg's CLI cannot author multiple editions or ordered chapters at all

Confirmed from ffmpeg's own `ffmetadata` chapter format (used to generate file
06 and reused for file 03): it has no syntax for a second `EditionEntry` or
for chapter "ordered" flags — Matroska's ordered-chapters feature (used for
some anime distributions) is simply not expressible from the ffmpeg CLI's
metadata-file input. mkvmerge's XML chapter format supports both natively.
This trial did not exercise remuxing an existing multi-edition source (none
could be synthesized), so this is a documented capability gap sourced from
each tool's own format documentation, not a corpus result — but it means any
source with genuine multiple editions is something **only mkvmerge can even
attempt to preserve**; ffmpeg's remux would collapse it to Matroska's default
single edition regardless of what the source had.

## Recommendation

**(a) Adopt mkvmerge for Matroska (`.mkv`) writes.** ffmpeg keeps probing,
validation and every non-Matroska container, unchanged, exactly as the issue
frames it. This trial found:

- a concrete, currently-shipping correctness defect in ffmpeg's remux path
  (dropped font attachments, finding 1) that mkvmerge's default behaviour
  fixes for free;
- a structural capability ffmpeg cannot reach at all (multiple
  editions/ordered chapters, finding 6);
- no case where ffmpeg's output was structurally *better* than mkvmerge's;
- no material performance or size penalty for switching (both are
  stream-copy-speed; overhead differences are tens of milliseconds,
  dominated by process startup);
- but also no evidence either way for the issue's single biggest motivating
  concern — Dolby Vision configuration records — because no DV/Atmos/PGS/true
  HDR10 sample could be produced or sourced in this environment. **That gap
  should be closed with real-world samples before this ships**, not treated
  as settled by this trial. Recommend queuing this as the first task of the
  follow-up issue, using 2–3 real releases (one DV profile 7 or 8 FEL/MEL, one
  TrueHD Atmos, one PGS) run through both tools with `dovi_tool info` and
  playback checks on at least one real device, per the issue's original step
  3 — none of which needed local corpus generation.
- `.webm` was out of scope for this trial (the corpus is all `.mkv`); keep it
  on ffmpeg until it gets the same treatment, since WebM is a constrained
  Matroska profile and mkvmerge's attachment/codec allowances don't
  automatically carry over.

File finding 1 (attachment loss) as its own defect regardless of this
decision's timeline: it is a one-line fix (map attachment streams) and is
worth shipping against ffmpeg immediately rather than waiting on the mkvmerge
writer, since Weir will keep running ffmpeg remuxes in production for however
long the follow-up takes.

## Follow-up (sizing only — not this issue)

1. **Real-sample validation** (blocking, do first): DV7/8, Atmos, PGS,
   DTS-HD MA samples through both tools; `dovi_tool info` diff; one real
   playback surface (Jellyfin/Plex web is enough to start; TV/Infuse if
   available). Half a day.
2. **Fix finding 1 in ffmpeg's argv regardless of the muxer decision**: map
   `codec_type == attachment` streams in `FfmpegCommands.BuildRemuxArgv`
   (`-map 0:t?` equivalent) so today's shipping path stops losing fonts. A
   few hours, including a golden-fixture update.
3. **Writer abstraction** in `apps/server/src/Weir.Infrastructure/Processing/RemuxPass`
   (`RemuxPassMedia.cs`/`RemuxPassHandler.cs` today call `MediaTools`
   directly): an `IRemuxWriter` seam with `FfmpegRemuxWriter` (existing) and
   `MkvmergeRemuxWriter` (new), selected by output extension (`.mkv`/`.webm`
   once validated → mkvmerge; everything else → ffmpeg). `ValidateRemuxOutputAsync`
   / `ValidateMediaIntegrityAsync` run unchanged after either writer, per
   finding 4. Explicitly decide the finding-5 question (preserve vs. reset
   non-default audio disposition flags) as part of this design, not as an
   incidental side effect of the writer swap. 3–4 days.
4. **Bundling**: MKVToolNix is GPL-2 — add its licence text and a NOTICE entry
   alongside the existing ffmpeg (LGPL/GPL build) notices in the Windows
   installer and Docker image; `mkvmerge.exe` + its Qt-free CLI dependencies
   add roughly 25–35 MB to the bundle (measure exactly against the actual
   packaged build, not this trial's system install). A `resolve_mkvmerge`
   sibling to `resolve_ffprobe_ffmpeg`, a startup health-check entry and a
   version string in the diagnostics/health endpoint, matching how ffmpeg is
   surfaced today. 1–2 days.
5. **Tests**: an `FfmpegCommands`-style golden argv builder for the mkvmerge
   command line (mirrors `MediaGoldenParityTests`), plus `RealFfmpegTests`-style
   tests that actually run mkvmerge on lavfi-generated fixtures the way
   `Trial503Harness` does here, promoted from throwaway harness into
   `Weir.Infrastructure.Tests` (skipped when mkvmerge isn't found, same
   pattern as `RequiresFfmpegFactAttribute`). 1–2 days.

Total follow-up estimate: roughly 1.5–2 weeks including the blocking
real-sample step, which cannot start until real DV/Atmos/PGS media is
available.

## Done-when checklist (from #503)

- [x] Corpus results posted here with the decision: **(a)** adopt mkvmerge for
      Matroska writes, contingent on the real-sample DV/Atmos/PGS check called
      out above landing before rollout.
- [ ] Follow-up issue filed for the chosen path (file after this report is
      reviewed; scope is section "Follow-up" above).
