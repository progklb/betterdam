# BetterDAM — Implementation Progress

A running log of what has been built, phase by phase. Each entry records what changed, the
decisions worth remembering, and what is deliberately left for later.

**All seven MVP phases are complete.** The solution is:

```text
Core/        models, interfaces, scanning, pending changes, batch, sync   (no UI, no external tools)
Metadata/    ExifTool: reading, XMP sidecars, embedding, preview extraction
Preview/     thumbnails, RAW previews, video proxies, frame decoding, cache
Database/    SQLite catalog, FTS5 search
UI/          Avalonia desktop app
Tests/       267 tests
```

External tools: **ExifTool** (metadata) and **FFmpeg** (video). Both optional — the app degrades
with a clear notice rather than failing.

---

## Phase 1 — Browser ✅

**Goal (from the README):** open folder, recursive scanning, image thumbnails, video thumbnails,
basic preview, basic file information.

### Solution layout

```
BetterDAM.sln
    Core/BetterDAM.Core          Models, interfaces, scanning services. No UI, no ExifTool, no FFmpeg.
    Preview/BetterDAM.Preview    Thumbnail generation (Skia for images, FFmpeg for video) + disk cache.
    UI/BetterDAM.UI              Avalonia desktop app (MVVM). Assembly name: BetterDAM.
    Tests/BetterDAM.Tests        xUnit tests.
```

The dependency direction is one-way: `UI → Preview → Core`. `Core` references nothing but
`Microsoft.Extensions.Logging.Abstractions`, so the metadata/database/sync layers can be added in
later phases without dragging UI concerns along.

### Technology choices

| Component | Choice | Note |
| --------- | ------ | ---- |
| Runtime | .NET 9 | .NET 10 SDK is not installed on this machine; 9.0.306 is the newest available. |
| UI | Avalonia **11.3.20** | See "Avalonia version" below. |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Source-generated `[ObservableProperty]` / `[RelayCommand]`. |
| DI | Microsoft.Extensions.DependencyInjection 9.0.18 | Wired in `UI/Services/ServiceCollectionExtensions.cs`. |
| Imaging | SkiaSharp 4.151.1 | Downsampled JPEG/PNG decode. |
| Video | FFmpeg (external process) | Optional — see "FFmpeg is optional" below. |
| Logging | Serilog 4.2.0 → console + rolling file | |

**Avalonia version.** Avalonia 12.1.1 is the current release, but 11.3.20 is the latest of the
mature 11.x line and is what this phase targets. Moving to 12.x is a reasonable follow-up but it
carries breaking changes, so it should be a deliberate migration rather than a side effect of
Phase 1. Nothing in the code is coupled to 11.x specifics beyond standard XAML.

### What was built

**Core**
- `MediaFile` — a scanned file (path, name, media type, size, timestamps). Deliberately carries **no
  metadata**; that arrives in Phase 2.
- `MediaTypeRegistry` — extension → `Image` / `Video` / `Unsupported`. Covers JPEG/PNG/TIFF/DNG and
  the common RAW formats, plus MP4/MOV/AVI/MXF.
- `IMediaScanner` / `MediaScanner` — recursive, cancellable scan that **streams** results as
  `IAsyncEnumerable<MediaFile>`. Directory reads happen on the thread pool; unreadable folders are
  logged and skipped rather than aborting the scan. Reports `ScanProgress`.
- `IFolderBrowser` / `FolderBrowser` — filesystem roots and lazy subfolder listing.
- `IAppPaths` / `AppPaths` — cache and log locations under `LocalApplicationData/BetterDAM/Cache`.

**Preview**
- `ThumbnailCache` — content-addressed JPEG cache on disk. The cache key is a SHA-256 of
  *path + size + modification time + requested size*, so **a file edited outside the app naturally
  misses the cache** instead of serving a stale thumbnail. Writes go to a temp file and are then
  moved into place, so an interrupted write can never leave a truncated image that later reads as a
  cache hit. Entries are sharded by the first two hex characters of the key.
- `SkiaImageThumbnailGenerator` — decodes via `SKCodec` at the **nearest scale the codec supports
  natively**, so a 50MP JPEG is never fully decoded just to produce a 320px tile. Applies EXIF
  orientation (all 8 origins).
- `FfmpegVideoThumbnailGenerator` — extracts one frame via `ffmpeg` to a piped MJPEG stream. Seeks 3
  seconds in to dodge black/fade-in opening frames, falling back to frame 0 for short clips. 30s
  timeout, and the process is killed on timeout or cancellation. **Reads only — never writes to the
  source file.**
- `ThumbnailService` — cache lookup → generator dispatch → cache write, with concurrency bounded to
  `ProcessorCount - 1` so a large scan cannot starve the UI thread.

**UI**
- Bridge-style layout: toolbar / folder tree / thumbnail grid / inspector / preview / status bar.
- `LazyThumbnail` (an `Image` subclass) requests its thumbnail when its container is realized.
  > **Correction (measured after Phase 3):** this was written believing the grid virtualized. It did
  > not — `ListBox` was given a `WrapPanel`, which is not a virtualizing panel, so every item was
  > realized and every thumbnail generated on open. Fixed afterwards with a purpose-built
  > `VirtualizingWrapPanel`; see "Performance — first preview latency" at the end of this document.
- Scan results are added to the UI collection in **batches** (64 items or 80ms) rather than one at a
  time, which is the difference between a grid that fills smoothly and one that crawls.
- Starting a new scan cancels the in-flight one; likewise for preview loads.
- Optional command-line folder argument: `BetterDAM /path/to/media` opens and scans it on startup.
- **Missing-FFmpeg notice** in the preview pane, shown *only while a video is selected*. It stays
  out of the way during metadata work but is unmissable the moment someone wants to watch something.
  Carries a platform-appropriate install command (`brew` / `winget` / `apt`).

### Verified

- `dotnet build BetterDAM.sln` — clean, 0 warnings.
- `dotnet test` — **29/29 passing** (scanner recursion/filtering/cancellation/progress, media-type
  classification, cache key invalidation and round-trip, image thumbnail sizing and orientation,
  FFmpeg locator override).
- **Ran the real app** against a generated test folder (12 PNGs + 5 in a subfolder + 1 MP4 + 1 .txt):
  found 18 media files recursively, ignored the `.txt`, rendered thumbnails.
- Clicked through selection → inspector → preview for both an image and a video, with FFmpeg both
  present (real frame thumbnail and preview) and forced absent (notice shown, see below).

### Things to know

**FFmpeg is optional.** Without it, video thumbnails, previews and playback are unavailable;
everything else — including all metadata work — behaves normally.

```sh
brew install ffmpeg     # macOS
```

The locator checks `$BETTERDAM_FFMPEG_DIR` first, then `PATH`, then common install directories
(`/opt/homebrew/bin`, `/usr/local/bin`, …). That last fallback matters: a GUI-launched app on macOS
inherits a minimal `PATH` that usually excludes Homebrew, so PATH alone would fail to find a
perfectly good install. Bundling FFmpeg is a Phase 4 decision.

`BETTERDAM_FFMPEG_DIR` is **authoritative**: if it is set and the tool is not there, FFmpeg is
treated as unavailable rather than quietly falling back to another copy on the system. That makes
the "not installed" state reproducible for testing:

```sh
BETTERDAM_FFMPEG_DIR=/nonexistent dotnet run --project UI/BetterDAM.UI -- /path/to/media
```

**RAW files got no thumbnail in Phase 1** — Skia cannot decode CR3/NEF/ARW, so they were scanned and
selectable but showed "No preview". Resolved after Phase 3 by extracting the embedded JPEG preview
with ExifTool; see the "Interlude — RAW thumbnails" section below.

**The preview pane is a large cached thumbnail** (1600px), not the original file. Real zoom/pan on
full-resolution originals is a later refinement.

**Cache location:** `~/Library/Application Support/BetterDAM/Cache`. It is fully disposable —
delete it and the app rebuilds from the originals. Logs later moved out of it; see
"Interlude — Settings and cache management".

### How to run

```sh
dotnet run --project UI/BetterDAM.UI                    # opens with the folder tree
dotnet run --project UI/BetterDAM.UI -- /path/to/media  # opens and scans that folder
dotnet test                                             # all tests
```

### Deliberately deferred

Nothing in Phase 1 reads or writes metadata. No ExifTool, no XMP, no SQLite catalog, no video
playback, no search. The interfaces are shaped so those slot in without rework:
`IThumbnailGenerator` is already a pluggable list, and `IMetadataProvider` will sit alongside it in
Core in the same way.

---

## Phase 2 — Metadata ✅

**Goal (from the README):** read embedded metadata, read XMP, display camera metadata, display video
metadata, edit basic metadata, edit keywords, edit ratings.

### New project

```text
Metadata/BetterDAM.Metadata    ExifTool integration + XMP sidecar resolution.
```

Dependency direction stays one-way: `UI → Metadata → Core`. `Core` gained the metadata *models and
interfaces* but knows nothing about ExifTool.

### The virtual metadata layer

This is the heart of the phase, and the part worth understanding before reviewing:

```text
Embedded metadata  ─┐
XMP sidecar        ─┼─→  Effective  ─→  + pending edit  ─→  what the inspector shows
                    │
User edits ─────────┘  (kept in PendingChangeStore, never on disk)
```

- `MediaMetadata` keeps the **embedded and sidecar layers separate** rather than pre-merging them.
  Phase 2 only displays `Effective`, but keeping both is exactly what Phase 3 conflict detection
  needs — no redesign required.
- Merging is **per field**, not wholesale: a sidecar carrying only a rating overrides the rating and
  leaves the embedded title alone. Verified against real files (see below).
- **Nothing is written to disk.** Editing a field records a `PendingChange` in memory. Editing a
  value back to its original silently drops the entry, so a field changed and un-changed by hand
  leaves nothing pending.

### ExifTool integration

`ExifToolSession` drives one long-lived process through ExifTool's `-stay_open` protocol. ExifTool
is a Perl script costing a few hundred milliseconds to start; paying that per file would make batch
operations unusable. Each request ends with `-execute{n}` and is matched to its `{ready{n}}` reply.
Requests are serialised — one process has one stdin.

The session survives a bad response: a timeout or broken pipe leaves the stream out of step with the
sequence numbers, so the process is discarded and restarted rather than reused.

`ExifToolMetadataProvider` reads the media file **and its sidecar in a single round trip** and maps
ExifTool's `-G`-prefixed JSON onto the models, taking the first non-empty of a candidate list per
field (`XMP:Title` → `IPTC:ObjectName` → `QuickTime:Title` → …). It also dedupes the camera name so
a Canon body reads "Canon EOS R5" rather than "Canon Canon EOS R5".

### UI

- Inspector rebuilt as tabs: **General** (editable) / **Camera** / **Video** / **XMP**. The Video tab
  only appears for videos.
- General: 5-star rating (clicking the current rating clears it, as Bridge and Photo Mechanic do),
  Title, Headline, Description, keyword chips with ✕ removal, Label, Creator, Copyright.
  Pasting `a, b, c` into the keyword box adds three keywords.
- **XMP** tab lists every tag ExifTool reported, sidecar tags included and marked, for power users.
- A `● MODIFIED` badge on the thumbnail, a "Modified — not yet written to disk" strip with **Revert**,
  and a pending-file count plus **Discard all** in the status bar.
- Missing-ExifTool notice at the top of the inspector, matching the FFmpeg pattern.

### Verified

- `dotnet test` — **81/81 passing** (was 29).
- **ExifTool 13.55 installed and verified against real files** (created with ExifTool/FFmpeg):
  - Editable fields, camera fields (`Canon EOS R5`, `RF100-500mm`, ISO 800, 1/1250, f/7.1, 500.0 mm,
    capture date, GPS, orientation) and video fields (1920×1080, 25 fps, 4.00 s, 252 kbps, mp4a,
    1 channel, 44100) all read correctly.
  - **Sidecar precedence confirmed end to end**: a JPEG with embedded `Rating=1, Title="Embedded
    title", Label="Green"` plus a sidecar carrying `Rating=5, Label="Red"` displays rating 5 and
    label Red from the sidecar, while keeping the embedded title and keywords the sidecar is silent
    about.
  - Every mapped tag key was checked against real `exiftool -json -G` output.
- Editing, keyword chips, star rating, MODIFIED badge, Revert and the status-bar count all exercised
  by driving the real UI.
- Incidentally stress-tested: a stray click started a recursive scan of `/`; the UI stayed responsive
  and Cancel Scan stopped it cleanly.

### Bugs found and fixed during verification

Worth recording because two were silent:

1. **Star rating did nothing.** `[RelayCommand] void SetRating(int)` generates `RelayCommand<int>`,
   whose `CanExecute` rejects the *string* a XAML `CommandParameter="4"` literal produces — so the
   button no-opped with no exception. Now takes a string and parses it. Regression test added.
2. **Hidden Video tab stayed selected.** Selecting an image after a video left the panel showing
   empty video fields with no tab highlighted. `SelectedIndex` is now bound and coerced off the
   video tab for stills. Regression test added.
3. **`{ready}` marker sharing a line with the payload** hung the session until the 60s timeout when
   output did not end in a newline. Now matched at end-of-line rather than as a whole line.
4. **Empty metadata rows left blank gaps** — the label was hidden but the value row still occupied
   space. Fields are now hidden as a unit.
5. Four inspector tabs wrapped to two rows in the 360px column; tab font size reduced.

### Testing without ExifTool

`FakeExifTool` is a shell script speaking enough of the `-stay_open` protocol to exercise the session
and provider without ExifTool installed. It verifies argument framing, response matching and process
reuse (five reads → one process). Like FFmpeg, `BETTERDAM_EXIFTOOL_DIR` is an authoritative override:

```sh
BETTERDAM_EXIFTOOL_DIR=/nonexistent dotnet run --project UI/BetterDAM.UI -- /path/to/media
```

### Deliberately deferred

- **No writing.** `IMetadataProvider` is read-only on purpose; there is no `WriteAsync` yet. Sidecar
  writing is Phase 3, embedding is Phase 6.
- **No conflict detection or resolution UI** — Phase 3. The data it needs is already being collected.
- **Pending changes are in-memory only.** Quitting discards them. Persisting the working tree belongs
  with the SQLite catalog.
- **No batch editing** (Phase 5) and **no History tab** yet.

---

## Phase 3 — XMP ✅

**Goal (from the README):** create XMP sidecars, read XMP sidecars, update XMP sidecars, preserve
unknown metadata, detect XMP/media conflicts.

This is the first phase that **writes to disk**, so the safety principles drive the design.

### The promise, and how it is enforced

> Ordinary metadata editing never modifies the original media.

`ExifToolSidecarWriter` only ever targets a `.xmp` path. `IsSafeSidecarTarget` asserts the target
has a `.xmp` extension *and* is not the media file, and refuses the write otherwise — belt and
braces, because getting this wrong means damaging someone's originals. It is covered by a theory
test, and by an integration test that hashes the media file before and after a write and asserts
the bytes **and the modification time** are unchanged.

### Preserving metadata we do not understand

Updating an existing sidecar only assigns the fields BetterDAM manages, so anything another
application wrote survives. Verified end to end: a sidecar carrying `XMP-photoshop:City=Windhoek`
still had it afterwards, alongside the newly written values.

### Conflict detection

A conflict requires **both** layers to carry a value **and** for them to differ. A field the sidecar
simply does not mention is not a conflict — that is the normal case for a sidecar holding only a
rating, and reporting it would make the warning meaningless. Keyword conflicts compare membership,
not order.

The inspector lists each conflicting field with both values and offers:

| Choice | Meaning |
| ------ | ------- |
| Keep embedded | Take the media file's value — but fields only the sidecar has are kept |
| Keep sidecar | Take the sidecar's value |
| Merge | Union the keywords; the sidecar wins for single-valued fields |

Resolving records a **pending change**; it writes nothing. Committing is a separate, explicit act.

**Worth knowing:** the conflict warning legitimately persists after saving, because writing the
sidecar does not change the copy inside the media file — the two layers still differ, and will until
Sync embeds the metadata in Phase 6. The strip says so up front, since otherwise it looks like the
resolution failed.

### What was built

- `MetadataConflict` / `MetadataConflictDetector` (Core) — detection and resolution, no I/O.
- `IMetadataWriter` / `SidecarWriteOptions` / `SidecarWriteResult` (Core).
- `ExifToolSidecarWriter` (Metadata) — creates or updates the sidecar, clears fields that were
  emptied, replaces the keyword list, and reads the result back to validate it.
- `ExifToolHost` — the single `-stay_open` process is now **shared by the reader and the writer**
  rather than each owning one.
- UI: conflict strip with the three resolutions; `Write XMP sidecar` per file; `Write all sidecars`
  in the status bar; `⚠ CONFLICT` and `XMP` badges in the grid; success/failure feedback.

### Verified

- `dotnet test` — **110/110 passing** (was 81).
- Integration tests run against **real ExifTool**, and skip cleanly when it is absent.
- Driven through the real UI against files with a deliberate conflict:
  - All three conflicts (title, rating, keywords) listed with both sides.
  - **Merge** unioned `alpha, beta` + `gamma` → `alpha, beta, gamma` and kept the sidecar title.
  - Writing produced `Saved to CONFLICT.xmp`; on disk the sidecar had the merged keywords **and
    still had `XMP:City = Windhoek`**.
  - Creating a sidecar from scratch for a file that had none.
  - **Both media files byte-identical before and after** (SHA-256), modification times unchanged.

### Bug found and fixed during verification

**Removing a keyword silently did nothing.** The intuitive ExifTool incantation —
`-XMP:Subject=` to clear, then `-XMP:Subject+=kw` to add — does *not* replace the list. The empty
assignment is ignored when append operations follow in the same command, so keywords were appended
to the old list: removing `a` and `c` from `[a,b,c]` left `[a,b,c,b]`. The correct form is repeated
plain assignment (`-XMP:Subject=a -XMP:Subject=b`), which ExifTool treats as "set the list to
these". Found because a test asserted the read-back rather than trusting the exit status; the
tests now assert `Success` on every write so a validation failure cannot pass unnoticed.

### Things to know

**Grid badges populate on inspection.** `⚠ CONFLICT` and `XMP` appear once a file has been selected,
because detecting them means reading its metadata. Eagerly reading every file in a folder is a job
for the SQLite catalog, not a synchronous scan.

**Sidecar naming.** New sidecars use the Adobe convention (`IMG001.xmp`). An existing
`IMG001.CR3.xmp` is detected and updated in place rather than a second file being created.

**Multi-line descriptions** travel via a temp file, because ExifTool argument files are line-based.

### Deliberately deferred

- **No embedding into media files** — that is Phase 6 Sync, along with backups, timestamp
  preservation, a summary/preview dialog, resumability and per-file error reporting.
- `Write all sidecars` is a simple sequential loop with a status-bar count. The reviewed,
  cancellable, resumable batch operation is Phase 6.
- Pending changes are still **in-memory only** — quitting before saving discards them.
- The raw XMP tab is still read-only.

---

## Interlude — RAW thumbnails ✅

Closing the gap left open in Phase 1: CR3/NEF/ARW/RAF and friends showed "No preview" because Skia
cannot decode them. Now possible cheaply because the ExifTool plumbing already exists.

### How it works

Every camera embeds a ready-made JPEG inside the RAW — the same one Bridge and Photo Mechanic
display. Extracting it is both the only practical way to show a RAW thumbnail and far faster than
developing the RAW. **Nothing here develops the RAW.**

```text
RAW file → ExifTool -b -PreviewImage → JPEG bytes → Skia decode/orient/resize → cached thumbnail
```

- `IEmbeddedPreviewExtractor` (Core) — the abstraction, so `Preview` needs no reference to
  `Metadata`; DI wires the two together.
- `ExifToolPreviewExtractor` (Metadata) — tries `PreviewImage` → `JpgFromRaw` → `OtherImage` →
  `ThumbnailImage`, largest and most useful first. Validates the JPEG magic bytes so an ExifTool
  diagnostic on stdout cannot be mistaken for image data.
- `RawThumbnailGenerator` (Preview) — renders the extracted preview.
- `SkiaThumbnailRenderer` — the decode/orient/resize logic, **extracted from the still-image
  generator and now shared**, so RAW and ordinary images cannot drift apart on orientation handling.

**Why a separate ExifTool process.** The shared `-stay_open` session reads stdout as *text*, line by
line, hunting for `{ready}`. Pushing JPEG bytes through it would corrupt them. A one-shot process
gives clean binary output, and the cost is paid once per file because the result is cached.

**Orientation comes for free.** The embedded preview carries the same EXIF orientation tag as the
RAW, so the shared renderer rotates it correctly with no extra ExifTool round trip. Confirmed
against real files before writing the code.

**Format coverage is defined as the complement** — "an image Skia cannot decode" — rather than a RAW
extension list. New formats are attempted rather than silently unsupported, and a file with no
embedded preview simply yields null, exactly as before.

### Verified

- `dotnet test` — **126/126 passing** (was 110).
- Against **four real Fujifilm RAF files** (~25MB each) from a real library, deliberately chosen to
  cover both orientations:
  - Two `Horizontal` RAFs → **320 × 213** thumbnails.
  - Two `Rotate 90 CW` / `Rotate 270 CW` RAFs → **213 × 320** thumbnails.
  - All four sensors are 4416 × 2944 landscape, so the portrait results prove the rotation is being
    applied — and inspecting the images confirms they are upright, not sideways.

### Bug found and fixed

**Thumbnails were coming out smaller and softer than requested.** The JPEG codec only offers
discrete scales (eighths) and rounds *down*: asking for 320px of a 2400px image decoded at 300px,
and since the renderer never upscales, that is what got cached. The decode now steps back up to the
next supported scale so it is at least the target size, then resizes down precisely. This affected
ordinary JPEGs too, so **all thumbnails are now slightly sharper**.

### Note

RAW files still have no *metadata* limitations — they were always readable. This was purely about
pixels.

---

## Phase 4 — Video ✅

**Goal (from the README):** FFmpeg integration, video playback, proxy generation, playback quality
selection, video metadata display (the last of which landed in Phase 2).

### The playback decision

The obvious .NET route — LibVLCSharp — turned out to be unusable here: `VideoLAN.LibVLC.Mac` ships
an **Intel-only native binary built in 2018**, and an arm64 .NET process cannot load an x64 dylib.
It would have needed a separate VLC.app install, on top of LibVLCSharp's Avalonia video control
being least proven on macOS.

The chosen approach is **FFmpeg-only**: decode to raw frames and render them, no new dependencies.
That trades audio for zero risk and delivers the browsing workflow the project exists for. Proxies
are generated **with** their audio, so playback with sound can be added later without regenerating
a single file.

### Spike before building

The pipeline was measured before any UI was written, on a 4K/12s/58 MB clip:

| | Result |
| --- | --- |
| 720p proxy generation | **2.3 s**, 4.8 MB (8.3% of source) |
| Scrub seek off proxy | **64 ms**/frame |
| Scrub seek off the 4K original | 183 ms/frame |
| Sustained decode off proxy | **515 fps** — 17× realtime headroom at 30fps |

### What was built

- `FfprobeVideoInfoProvider` — duration, dimensions and frame rate as values a timeline can do
  arithmetic with. ExifTool already showed these in the inspector, but a timeline cannot be laid out
  from the string "0:00:12". Handles NTSC rationals (`30000/1001` → 29.97).
- `FfmpegVideoProxyService` — generates and caches proxies keyed like the thumbnail cache
  (path + size + mtime + quality). Uses **`h264_videotoolbox`** on macOS, so encoding is hardware
  accelerated and leaves the CPU free for browsing. Real progress from ffmpeg's `-progress` stream
  rather than a spinner. Concurrent requests for the same proxy share one job.
- `FfmpegFrameSource` — one long-lived ffmpeg process writing raw BGRA to stdout, read at a fixed
  frame size. Buffers come from `ArrayPool` because a 720p frame is 3.5 MB and 25 of them a second
  would otherwise be ~90 MB/s of garbage. Decode is capped at 720p regardless of source.
- `VideoSurface` — a control that blits frames into one reused `WriteableBitmap`. Frames are
  **pushed** rather than bound, because a binding would mean a bitmap allocation per frame.
- `VideoPlayerViewModel` — transport, scrubbing, frame stepping, quality selection. Playback paces
  itself against a wall clock using each frame's timestamp, so it runs at the right speed without a
  timer and degrades gracefully if decoding ever falls behind.
- Video proxies are included in cache size, clearing and rolling eviction alongside thumbnails.

### Verified in the real app

Against a 4K/12s clip and an HD clip:

- Player loads with the first frame, correct `0:12` duration, and **"Playing the original at
  3840×2160"**.
- **Playback runs at true realtime**: position advanced 0:03 → 0:06 across exactly 3 seconds of wall
  clock, decoding a 4K source live. Frame captures confirm the picture genuinely advances rather
  than freezing on frame one.
- Switching to 720p showed **"Generating 720p proxy…"** with a live progress bar, then
  **"Playing a 1280×720 proxy — the original is untouched"**. On disk: one 4.8 MB proxy from a
  58 MB source.
- Scrubbing jumps to the clicked position; frame stepping changes the displayed frame.
- **The source file is byte-identical** (SHA-256) before and after playback, proxy generation,
  scrubbing and stepping.

### Things to know

**There is no audio yet.** This plays video frames only. It is a deliberate scope choice, not an
oversight — see the playback decision above.

**Original quality writes nothing to disk.** Choosing Original decodes the source directly, so the
"proxies are entirely optional; if disabled no cache is written" requirement is satisfied by the
quality selector itself rather than a separate toggle.

**A source smaller than the requested proxy is not upscaled** — asking for 720p of a 360p clip
returns the original rather than encoding a larger file for no benefit.

### Deliberately deferred

- **Audio, and true A/V sync.** The natural next step, and the reason proxies already carry audio.
- **Waveforms** — listed in the README's cache layout but not needed until audio exists.
- Proxy generation is on demand when a quality is selected; batch/background pre-generation for a
  whole folder belongs with the Phase 5 job system.
- Seeks use fast container-index seeking, which lands on the nearest keyframe. Frame-exact stepping
  would need decode-accurate seeking, noticeably slower and not worth it for a preview.

---

## Phase 5 — Batch operations ✅

**Goal (from the README):** multi-selection, batch keywords, batch ratings, batch metadata,
background processing.

### The design decision that shaped it

The batch panel is an **"apply these changes" form**, not a merged view of the selection's existing
values. Showing common values would mean reading every selected file before the user has decided
anything — 1,000 ExifTool reads to render a panel nobody may use.

Every field is **opt-in**. A blank box can never mean "clear this on 500 files"; only an explicitly
ticked field is touched. Keywords default to **add/remove** rather than replace, because a single
shared keyword list across a mixed selection is rarely what anyone means. Replace is available, but
it is a deliberate tick.

### What was built

- `BatchMetadataEdit` (Core) — the edit itself, with a **pure** `ApplyTo`. No I/O, no shared state,
  so the semantics are exhaustively testable without touching a disk.
- `BatchMetadataService` (Core) — reads baselines, computes each file's edit, records pending
  changes. Reports progress, is cancellable, and collects per-file failures rather than aborting.
- `IMetadataProvider.ReadManyAsync` — batched ExifTool reads, **100 files per invocation**. This is
  what makes a large selection viable: the expensive part of a batch edit is not the edit, it is
  fetching each file's current metadata to use as a baseline.
- Multi-selection in the grid (`Ctrl`/`Cmd`-click, shift-click, `Cmd+A`), which swaps the right
  panel from the single-file inspector to the batch editor.
- Progress bar with a cancel button, and a per-file failure list.

### Two properties worth stating plainly

**Batch edits are still pending changes.** They go through the same store as single-file edits, so
they show the same `● MODIFIED` badges and are committed by the same explicit *Write all sidecars*.
Batch editing is not a back door around the non-destructive workflow.

**Successive batches compose.** A second run builds on the first run's pending edit, not on disk
alone — so adding "wildlife" then "Namibia" gives you both, rather than the second undoing the first.

### Verified in the real app

30 files, two of them seeded with existing metadata to exercise the interesting cases:

- `Cmd+A` selected all 30; the panel switched to batch mode showing "30 files selected".
- Added keywords `wildlife, Namibia` and rating ★★★★, then applied:
  **"30 file(s) modified. Nothing is written until you save."**
- After *Write all sidecars*, on disk:

| File | Before | After |
| --- | --- | --- |
| B05 (plain) | — | `Rating 4`, `wildlife, Namibia` |
| B01 | `Rating 5`, keyword `existing` | `Rating 4`, **`existing, wildlife, Namibia`** |
| B02 | keyword `wildlife` | `Rating 4`, **`wildlife, Namibia`** — not duplicated |

All 30 sidecars written, all with rating 4, and **the 30 originals byte-identical** (SHA-256).

### Bug found and fixed during verification

**The rating stars were unreachable.** They were disabled until "Set rating" was ticked — but
clicking a star was the only thing that ticked it. A perfect chicken-and-egg: the field could not be
used at all. Now interacting with any field opts it in (and never opts it *out*, so deliberately
ticking then blanking a field to clear it across a selection still works). Regression tests added
for the stars, the text fields, and the clear-after-ticking case.

### Deliberately deferred

- **No retry or resumability yet.** Failures are reported per file but there is no "retry failed"
  action, and a cancelled run does not resume. Both belong with Phase 6 Sync, which the README
  already scopes them to.
- The job UI is inline in the batch panel rather than a general queue; multiple concurrent jobs and
  a job history are not needed until Sync.
- Batch editing writes to sidecars only, like everything else so far — embedding is Phase 6.

---

## Phase 6 — Sync ✅

**Goal (from the README):** pending-change tracking, sync preview, embed metadata, preserve
timestamps, optional backups, validation, error reporting.

**This is the first and only phase that modifies original media** — and only when explicitly asked.

### Shape of the operation

Split into **plan** then **execute**, so the user sees exactly what is about to happen before
anything is written:

```text
pending changes → plan (counts, file types, conflicts) → options → write → journal → report
```

The dialog shows the README's summary — "8 JPG / 1 MP4" — plus a conflict count, and a plain-English
line that changes with the options:

> *XMP sidecars will be written. Your original media will not be modified.*
> *XMP sidecars will be written, and metadata will be written into the original media files.*

### Options

| Option | Default | Notes |
| ------ | ------- | ----- |
| Embed metadata into originals | **Off** | The only setting in the application that modifies a user's media |
| Back up originals | On | Keeps `<name>.<ext>_original`, using ExifTool's own tested backup path |
| Preserve file timestamps | On | `-P`; the original complaint this project started from |
| Validate after writing | On | Reads each file back and compares |
| Skip conflicted files | On | Files whose embedded and sidecar metadata disagree are left alone |

**Sidecars are always written, even when embedding**, so the two layers agree afterwards and a
freshly synced file does not immediately look conflicted.

### Resumability

`SyncJournal` records each file the moment it commits. It is an **append-only line-per-path text
file** rather than a serialised document, deliberately: appending one line is about as close to
atomic as a filesystem gets, so a crash — or a pulled cable — mid-run leaves a readable journal
rather than a half-rewritten blob. It lives outside the cache, because losing it would mean redoing
work.

A cancelled run therefore resumes; the dialog says how many files it will skip and offers to start
over instead. A run that finishes cleanly clears the journal, so the next sync does not wrongly
believe it is resuming.

### Verified in the real app

9 files (8 JPG + 1 MP4), all backdated to `2020-01-02 03:04:05` so a rewritten timestamp would be
unmistakable. Batch-applied a keyword and rating, then synced **with embedding on**:

- Result: **"9 file(s) written and embedded."**
- **All 9 timestamps still `2020-01-02 03:04:05`** — including the video.
- 9 backups created; `S01.jpg_original` hashes **identical to the pre-sync file**, while the file
  itself now hashes differently. The backup carries none of the new metadata.
- Metadata verified *inside* the originals — `Rating 4`, `Subject: synced` — **including inside the
  MP4**.
- 9 sidecars written and agreeing with the embedded values.
- Journal cleared after the clean run.

Also covered by tests: sidecar-only sync leaving originals byte-identical, embedding without
backups, conflicted files being skipped while keeping their pending change, resume skipping
already-committed files, and discard-resume starting over.

### Bug found and fixed during verification

After a successful run the dialog still read **"Changes pending: 9 file(s)"** directly above
**"9 file(s) written and embedded"** — the plan was captured before the run and never refreshed, so
a complete success looked like a failure. It now re-plans afterwards while preserving the result
message.

### Things to know

**Embedding writes XMP into the media file.** That is the project's stated interoperability target.
Writing IPTC/EXIF equivalents as well, for older tools that do not read embedded XMP, is a
deliberate non-goal for now.

**Backups accumulate.** `_original` files sit next to the media and are never cleaned up
automatically — that is the point, but a large embed run doubles disk usage for those files.

### Deliberately deferred

- **No catalog update step.** The README's sync sequence includes "update the local catalog"; there
  is no SQLite catalog yet, so there is nothing to update. Search (Phase 7) is where that lands.
- Retry is "retry everything still outstanding" rather than per-file selection.
- Conflict resolution still happens in the inspector; the sync dialog reports conflicts and skips
  them rather than offering to resolve them inline.

---

## Phase 7 — Search ✅

**Goal (from the README):** keyword search, description search, rating filtering, camera/lens
filtering, media type filtering, date filtering, basic query syntax, SQLite FTS5.

This is the phase that finally introduces the **local catalog** — the SQLite database the README's
architecture has been pointing at since Phase 1.

### New project

```text
Database/BetterDAM.Database    SQLite catalog: schema, migrations, FTS5, Dapper repository
```

Dependency direction still one-way: `UI → Database → Core`.

### FTS5 checked before building on it

`Microsoft.Data.Sqlite` bundles its own SQLite, and FTS5 is a compile-time option — so rather than
assume, a throwaway program confirmed it first: **SQLite 3.46.1 with FTS5 and prefix matching
working**. Worth the two minutes; the whole design depends on it.

### Schema

Versioned from the start, so later phases can add columns without asking anyone to delete their
catalog:

| Table | Purpose |
| ----- | ------- |
| `Media` | One row per file, with the searchable metadata denormalised onto it |
| `Keyword` / `MediaKeyword` | Normalised keywords, so `keyword:x` is an indexed lookup rather than a text scan |
| `MediaSearch` | FTS5 over title, description, headline, keywords and creator |

`MediaSearch` shares `Media.Id` as its rowid, so refreshing a file's index entry is a delete by
rowid rather than a scan. WAL is on, so indexing can write while the UI reads.

**The catalog lives outside the cache** (`<AppData>/catalog.db`), alongside settings. It is derived
data, but rebuilding it means re-reading metadata for the whole library — not something to lose to a
"Clear cache".

### Query syntax

Exactly the syntax the README specified, plus dates:

```text
keyword:motorcycle      camera:Sony        type:video
rating:>=4              lens:"RF 100-500"  date:>=2024-01-01
lioness dawn            (bare words → full text, prefix-matched)
```

Terms combine with implicit AND; a literal `AND` is accepted. The parser is pure and separate from
the SQL, so its behaviour is testable without a database.

Two decisions worth noting:

- **Unrecognised filters are reported, not dropped.** `rating:9` does not silently return everything;
  the status bar says what was ignored. Quietly discarding a filter is how a search tool lies to you.
- **Every value is parameterised.** A search box is user input, and a test asserts none of it appears
  in the generated SQL.

### Indexing

Runs in the background **after** a scan populates the grid, so browsing is never blocked, with
progress and a Stop button in the status bar. Work is chunked (100 files per ExifTool round trip,
reusing Phase 5's batched reads) so a cancelled index of a large library keeps what it already did.

### Verified in the real app

An 8-file library with distinct metadata, auto-indexed on scan (`Indexed 8 of 8 file(s)`):

| Query | Result |
| ----- | ------ |
| `keyword:motorcycle` | The 2 tagged files, image and video |
| `rating:>=4 AND keyword:motorcycle AND type:video` | **CLIP01.mp4 only** — correctly excluding the motorcycle *image* |
| `camera:Sony` | The 2 Sony files |
| `sunset` | IMG04.jpg — the only file with that word in its **description** |
| `date:>=2024-01-01` | IMG01.jpg — the only 2024 capture date |

Plus 50 tests covering the parser, the SQL builder, and the catalog against a real SQLite file —
including that re-indexing updates rather than duplicating, that removed keywords stop matching, and
that the catalog survives being reopened.

### Bug found during verification

Dapper could not materialise the result rows: SQLite returns every integer as `Int64`, so a record
declaring `int MediaType` had **no matching constructor** and every catalog query threw. The row
type now reads them as `long` and narrows on the way out.

### Things to know

**Search covers what has been indexed, not what is on disk.** Open a folder once and it is
searchable from then on; a folder never visited is invisible to search. A "index this whole library"
action would be the natural next step.

**Re-indexing is not automatic on external change.** A file edited by another application keeps its
old catalog entry until that folder is scanned again. This is what the README's file-watching
section is for.

### Deliberately deferred

- **No saved searches or smart collections** — the README lists them under future features.
- No `OR` or `NOT`; the syntax is AND-only, as specified.
- `RemoveMissingAsync` exists and is tested but is not yet wired to a UI action or run automatically.
- Search results are a flat list ordered by filename; relevance ranking (FTS5 offers `bm25()`) is not
  used yet.

---

## Performance — first preview latency ✅ Fixed

**Symptom:** open a folder, click a file, and the preview took a long time. Once the first preview
appeared, every subsequent one was near-instant.

**Diagnosis.** The preview never became slow — it *waited*. Three compounding causes:

1. **The grid did not virtualize.** `ListBox` was given a `WrapPanel`, which is not a virtualizing
   panel, so *every* item in the folder was realized on open and *every* thumbnail requested
   immediately. Measured: 60 files in a window showing ~12 produced 60 thumbnails within 2s.
2. **One shared concurrency gate.** Grid tiles and previews both passed through the same
   `SemaphoreSlim(cores - 1)`, roughly FIFO, so a preview joined the back of a queue of N.
3. **The preview is a separate cache entry** (1600px vs 320px tiles), so the first view of a file
   always generates. Cache hits short-circuit *before* the gate, which is why a second visit was
   always instant.

### The fixes

- **`VirtualizingWrapPanel`** (`UI/Controls`) — Avalonia ships `VirtualizingStackPanel` but no
  wrapping equivalent, so the grid got its own. It realizes only the rows intersecting the viewport
  plus one row of buffer, recycles containers through the `ItemContainerGenerator`, and implements
  keyboard navigation and `ScrollIntoView`. It assumes **uniformly sized items**, which is true here
  and is what makes the layout arithmetic exact and cheap.
- **Two priority lanes** in `ThumbnailService` — `ThumbnailPriority.Interactive` for what the user
  selected, `Background` for grid tiles, each with its own semaphore. Background also dropped to
  `cores - 2` to leave headroom.
- **Cancellation on recycle** — `LazyThumbnail` cancels its item's in-flight work when the tile
  leaves the viewport or its container is recycled for another file, and the item can request again
  if it scrolls back. An already-decoded thumbnail is kept, so scrolling back is instant.

### Measured

400 × 26MP JPEGs, 8-core machine, cold cache, UI removed so the numbers are unambiguous:

| | Time to first preview | Grid work |
| --- | --- | --- |
| Original (shared gate, no virtualization) | **1889 ms** | 1960 ms (400 thumbnails) |
| Priority lanes only | **107 ms** | 1669 ms (400 thumbnails) |
| Priority lanes + virtualization | **71 ms** | 122 ms (24 thumbnails) |

For reference, that same preview on a completely idle queue costs 67 ms — so it is now essentially
free of queueing delay.

**End to end in the real app**, 400-file folder:

- **20 thumbnails generated on open, not 400** — and it stays at 20 indefinitely without scrolling.
- Scrolling realized 12 more on demand; a fast scroll through the whole folder and back generated
  165 rather than 400, the rest being cancelled as they left the viewport.
- Selection, preview, inspector, keyboard navigation and scroll-back-to-cached all verified working.

### Still worth doing later

- The 320px tile and 1600px preview are generated independently. For RAW that means extracting the
  same embedded preview twice. Generating the preview from the already-decoded larger image, or
  caching one intermediate size, would remove that.
- `ScrollIntoView` calls `UpdateLayout()` to materialize the container it must return. That is
  correct but synchronous; if it ever shows up in a profile, returning null and letting the caller
  retry after layout would avoid it.

---

## Interlude — Settings and cache management ✅

The cache had no ceiling, no way to see its size, and no way to clear it from inside the app.

### Layout change

Settings and logs moved **out** of the cache directory, so clearing or relocating the cache can
never take them with it:

```text
<LocalAppData>/BetterDAM/
    settings.json          preferences
    Logs/                  diagnostics  (was Cache/Logs)
    Cache/Thumbnails/      disposable derived data, relocatable
```

On macOS that is `~/Library/Application Support/BetterDAM`; `%LOCALAPPDATA%` on Windows and
`~/.local/share` on Linux, all via `LocalApplicationData` with no per-OS branching.

*Upgrade note:* an empty `Cache/Logs` directory is left behind from the old layout. Harmless.

### Settings window

Reached from **⚙ Settings** in the toolbar, pinned right so a long folder path cannot push it off.
A `TabControl` with a single **Cache** tab for now — the shape is there for future tabs.

| Control | Behaviour |
| ------- | --------- |
| Location | Shows the current path; **Change…** picks a new one, **Use default** reverts. Takes effect immediately — `AppPaths.CacheRoot` reads the setting live rather than caching it at construction. |
| Current size | Logical bytes and file count, with **Refresh**. |
| Rolling cache | Optional ceiling from 50 MB to 50 GB. Applying a limit trims straight away rather than waiting for the next write. |
| Clear cache | Two-step inline confirmation, then reports the bytes freed. |

### Rolling eviction

`ThumbnailCacheMaintenance` evicts least-recently-used entries until the cache fits, targeting 90%
of the limit so the next few writes do not immediately trigger another pass. Eviction can be blunt
because entries are content-addressed and independent — discarding one only costs regenerating it.

It runs on startup, when a limit is applied, and on its own once **32 MB** has been written since
the last pass. That threshold exists because trimming enumerates the whole cache directory, so doing
it after every thumbnail would cost more than it saves. Only one trim runs at a time, never on the
caller's thread.

Ordering uses last-write rather than last-access time: access times are unreliable on volumes
mounted `noatime`, and for an immutable cache the two only differ for entries read but never
rewritten.

### Verified

- `dotnet test` — **145/145 passing** (was 129). Covers settings round-trip, corrupt-settings
  fallback, statistics, clear, eviction *order*, staying within the limit, shard tidy-up, and that
  `LogRoot` is not inside `CacheRoot`.
- Driven through the real UI: opened Settings, saw **83.3 MB in 14,707 files**, cleared it, and got
  **"Cleared 83,3 MB"** — on disk 14,707 files → **0**, empty shard directories removed, and the log
  file still present.

### Worth knowing

**Reported size is logical bytes, not disk usage.** `du` reports ~123 MB for what this calls 83 MB,
because ~15,000 small files each round up to a 4 KB block. Logical bytes is the right thing for the
limit to control, but the on-disk footprint of a cache full of tiny thumbnails is larger.

**Changing the location does not move existing thumbnails.** They stay at the old path and are
regenerated at the new one; the UI says so. Clearing before switching reclaims the space.

---

## Interlude — Catalog management ✅

Prompted by an observation from real use: *"Seems like cache is global, as I am filtering and seeing
your test files."*

That was the catalog, not the cache — and it was working as designed. The catalog spans everything
ever indexed rather than the current folder, which is what makes search useful across a library that
does not fit in one directory. The problem was not that it is global; it was that it was
**unmanageable**: no way to see it, empty it, prune it, or move it.

### What the catalog is

Worth restating because it drives every decision below: the catalog is a **cache of what is already
in the files and their sidecars**. The media stays authoritative. Deleting the catalog costs
re-indexing and nothing else — no user data lives only there. That is why clearing and relocating
can both be blunt.

### Layout

```text
<LocalAppData>/BetterDAM/
    settings.json
    catalog.db             the search index  (relocatable)
    Logs/
    Cache/Thumbnails/      (separately relocatable)
```

`catalog.db` sits outside `Cache/` deliberately, alongside settings and logs, so **"Clear cache"
never destroys the search index** — they are independent things and the user chose one of them.

### Settings → Catalog tab

| Control | Behaviour |
| ------- | --------- |
| Location | Path, with **Change…** / **Use default**. Live, like the cache path. |
| Contents | `138 files, 412 keywords · 2,1 MB`, with **Refresh**. |
| Remove entries for missing files | Prunes only rows whose file is gone. |
| Clear catalog | Two-step inline confirmation. |

### Implementation notes

**The path resolves per connection, not once at construction.** `SqliteCatalog` reads
`IAppPaths.CatalogPath` every time it opens a connection, so relocating takes effect without
restarting. It tracks `_initialisedPath` — the path the schema was last applied to — rather than an
`_initialised` bool, because a bool would treat a *new, empty* file at a *new* location as already
migrated and then fail on the first query.

**Reported size includes the WAL.** SQLite's `-wal` and `-shm` companions routinely dwarf the `.db`
itself, so summing only the main file reports a size the user can see is wrong in Finder.

**Clearing vacuums.** Deleting every row leaves the file exactly as large as it was, so a "Clear"
that reports the same size afterwards looks broken. `ClearAsync` now does
`PRAGMA wal_checkpoint(TRUNCATE)` then `VACUUM` so the space is actually returned.

**Pruning finally has a caller.** `RemoveMissingAsync` existed and was tested since Phase 7 but
nothing invoked it — precisely the gap that let stale entries accumulate.

### Related fix — the stack trace on missing files

Generating a thumbnail for a file the catalog still lists but that no longer exists logged a full
`FileNotFoundException` stack trace. It was caught and handled correctly, but a stale catalog is a
*routine* condition, and stack traces for routine conditions bury the real faults. Now a one-line
`Debug` message.

### Verified

- `dotnet test` — **273/273 passing** (was 267). Six new tests: size-on-disk is non-zero, relocating
  starts a fresh catalog *and leaves the old file intact*, the relocated catalog is usable
  immediately (schema really is applied at the new path), clearing genuinely shrinks the file,
  pruning removes only the missing entries, and pruning an intact catalog removes nothing.
- The Catalog tab itself has **not** been driven through the running UI — see below.

### Worth knowing

**Changing the location starts an empty catalog; it does not move the old one.** The old file stays
put. Re-index to repopulate. The UI says this rather than leaving it to be discovered.

**Prune trusts the filesystem.** If the library lives on an external drive that is not mounted,
every entry on it looks missing and will be removed. Harmless — re-indexing rebuilds it — but the
button is slower to recover from than it looks. Worth a mount check before pruning if this bites.

**Search is still global, by design.** If per-folder search is wanted, that is a scope filter on the
query, not a change to how the catalog is stored.

---

## Interlude — macOS menu bar ✅

**Open Folder…** and **Settings** moved out of the in-window toolbar and into the system menu bar.

| Item | Where | Shortcut |
| ---- | ----- | -------- |
| Open Folder… | **File** | ⌘O |
| Settings… | **BetterDAM** application menu | ⌘, |

`Application.Name` is now set to `BetterDAM`; without it the menu bar reads *"Avalonia Application"*.

### The trap: NativeMenuItem has no DataContext

`Command="{Binding OpenFolderCommand}"` **compiles, runs, and silently does nothing.**
`NativeMenuItem` is an `AvaloniaObject`, not a `StyledElement` — it has no `DataContext` property at
all, so there is nothing for a binding to resolve against and the command stays null. The menu item
still appears, still looks enabled, and does nothing when clicked.

This was caught by probing the live menu (`NativeMenu.GetMenu(window)`) at three points in the
window lifecycle and printing whether `Command` was null. It was null at all three. Both items now
use `Click` handlers, which are verified when the XAML compiles.

Worth remembering for any future menu item: **on a native menu, use `Click`, not `Command`.**

### Cross-platform

macOS is the only platform with a system menu bar, so:

- `NativeMenuBar` sits at the top of the window's `DockPanel`. On macOS the menu is exported to the
  system bar and this renders nothing; on Windows and Linux it draws the menu inside the window.
- The application menu only exists on macOS, so `MenuConventions.ShowSettingsInFileMenu` adds
  Settings (and a separator) to **File** everywhere else, where it would otherwise be unreachable.
- Modifiers differ too: `MenuConventions` builds ⌘O/⌘, on macOS and Ctrl+O/Ctrl+, elsewhere. Avalonia
  does not translate `Cmd` per platform — left alone it would bind the Windows key on Windows.

`KeyGesture.Parse` accepts `"Cmd+O"` and `"Cmd+,"`, but **not** the `⌘` glyph — verified against
Avalonia 11.3.20 rather than assumed.

### Verified in the running app

- Menu bar reads **BetterDAM  File**.
- **File → Open Folder…** present, showing ⌘O.
- **BetterDAM → Settings… ⌘,** sits above Services/Hide/Quit, per macOS convention, and opening it
  from there really does show the dialog — that path goes through `App`, which has no window
  reference of its own and forwards to `MainWindow.OpenSettingsAsync`.
- The toolbar now starts at **Recursive**; both buttons are gone.
- The Catalog tab reported **969 files, 33 keywords · 484 KB**.
- `dotnet test` — **273/273 passing**.

### Worth knowing

**Discoverability drops on first launch.** With the button gone, an empty window offers no visible
way to open a folder — the status bar says *"Ready. Choose a folder to begin."* but points nowhere.
An empty-state prompt in the grid area would fix it, if wanted.

**Avalonia's stock application menu shows "Hide Others ⌥⌘Q".** The usual macOS binding is ⌥⌘H. That
menu is built by Avalonia, not by this code.

---

## Interlude — Empty state ✅

Moving Open Folder into the menu bar left the first launch with no visible way in. The grid now
explains itself whenever it is empty, with **three** distinct messages — an empty grid has three
quite different causes and one generic message would misread two of them as failures:

| State | Says |
| ----- | ---- |
| Nothing opened yet | *No folder open* — with an **Open Folder…** button |
| Folder has no readable media | *Nothing to show here* — and suggests **Recursive** when it is off |
| Search matched nothing | *No matches* — and explains that search only covers indexed folders |

The last one matters: "no matches" on a library you know contains the file looks like a bug, when
the real answer is that the folder has not been browsed (and therefore indexed) yet.

Visibility is driven from `MediaItems.CollectionChanged` rather than from the four places that
mutate the collection, so it cannot drift out of step with what is on screen. It stays hidden while
scanning, since a scan has its own progress indicator and flashing "nothing here" before the first
results arrive would be wrong as often as right.

### Fixed on the way

`CurrentFolderPath` doubles as display text and holds `"Search: ..."` during a search. Clearing a
search with no folder selected left the old hits on screen with an empty search box, presenting them
as a folder listing. `ClearSearch` now empties the grid and resets the path in that case.

---

## Phase 8 — Workspaces ✅

A folder opened now becomes the **workspace**: the root of the tree, the scope of search, and what
the application reopens next launch. Modelled on how VS Code opens a folder.

This is also the real answer to *"I am filtering and seeing your test files"* — a workspace gives
search a boundary, which beats pruning the catalog by hand forever.

### What changed

**The tree has one root.** `OpenPathAsync` used to `Insert` the opened folder alongside Home, `/`
and the volumes, so every folder ever opened accumulated in the tree. It now replaces them. With no
workspace open the tree is empty and the empty state carries the prompt.

**Search is scoped.** `ICatalog.SearchAsync` takes an optional `rootPath`. The **Everywhere**
checkbox next to the search box widens it back to the whole catalog — off by default, because a
workspace that returned results from unrelated folders would not be much of a workspace. The status
line names the scope: *"12 match(es) in namibia"* vs *"in everywhere"*.

**The workspace persists.** `LastWorkspacePath` reopens on launch; a folder on the command line
still wins. `RecentWorkspaces` (capped at 10, de-duplicated, most recent first) drives
**File → Open Recent**. **File → Close Workspace** (⇧⌘W) returns to the empty state.

### Prefix matching, carefully

Scoping is `substr(m.Path, 1, @rootLength) = @root`, **not** `LIKE @root || '%'`:

- A path may contain `%` or `_`, which LIKE treats as wildcards. `/photos/100%` would match
  `/photos/100x`. Escaping is possible but easy to get subtly wrong; `substr` sidesteps it.
- The root is normalised to end with a directory separator, so `/photos/nam` cannot swallow
  `/photos/namibia`.

Both cases are tested. The cost is that `substr` cannot use an index on `Path` where a
`LIKE 'prefix%'` could — irrelevant at catalog sizes measured in hundreds of thousands of rows, but
worth knowing if it ever needs to scale further.

### The second native-menu trap

`x:Name` on a `NativeMenuItem` generates **no field** in the code-behind — it is not part of the
visual tree. Open Recent has to be found by walking `NativeMenu.GetMenu(window)`, so its header is a
shared constant (`MenuConventions.OpenRecentHeader`) referenced by both the XAML and the lookup
rather than a literal repeated in two places that would drift the first time one was reworded.

Its items are built in code for the same reason `Click` replaced `Command` last time: no DataContext.

### Verified in the running app

- Opened with a folder argument: title reads **"testmedia — BetterDAM"**, the tree has exactly one
  root, and the **Everywhere** checkbox appears.
- **File** shows Open Folder… ⌘O, Open Recent ▸, Close Workspace ⇧⌘W; the submenu lists the folder.
- Relaunched with **no** argument and it reopened the workspace by itself.
- Against the real catalog, the scoping predicate splits **987 rows into 18 in-workspace and 969
  outside** — precisely the separation that was missing.
- `dotnet test` — **288/288 passing** (was 273).

### Worth knowing

**Open Recent shows name first, then an abbreviated path.** The folder name alone is ambiguous —
every library has a "2024" — but the full path dragged the menu wider than the screen. Home becomes
`~`, and anything still over 45 characters is elided from the **front**, since the tail is what
identifies a folder. The full path is on the tooltip.

**A missing workspace removes itself from the recent list.** Opening one that has been moved or
unmounted reports it and drops it rather than offering it again forever.

**Scoping is by path prefix, so it follows the filesystem, not the library.** Media outside the
workspace folder is invisible to a scoped search even if logically part of the same collection.
Everywhere is the escape hatch.

### Next: indexing (step 3)

Agreed but not yet built:

- **Skip files already indexed whose size and mtime match.** Makes reopening a workspace near-free
  and makes interrupting an index cheap — which is why *no* "are you sure you want to quit" dialog
  is planned. Indexing already commits every 100 files, so a kill loses at most 100 files of work.
- **Index the whole workspace up front**, in the background, so search covers the workspace rather
  than only the folders that happen to have been browsed.
- **An inline, non-modal prompt above a file-count threshold** — "48,213 files. Index them for
  search? [Index] [Not now]" — rather than a modal, with the answer stored per workspace so it is
  asked once.

---

## Phase 9 — Workspace indexing ✅

Step 3 of the workspace work. Search now covers the **whole workspace** rather than only the folders
that happened to have been browsed, and re-opening one is nearly free.

### Skip what has not changed

`CatalogIndexer` asks the catalog what it already knows before reading anything, and skips files
whose **size and modified time both match**. Size *and* time rather than either alone: an edit that
preserves the timestamp usually changes the size, and one that preserves the size usually changes
the timestamp. Content hashing would be exact but reading every byte would cost far more than the
metadata read it is avoiding.

The lookup is per 100-file chunk (`Path IN @paths`) rather than one query for the whole workspace —
bounded memory regardless of library size, and negligible next to the ExifTool reads it prevents.

`IndexAsync` now returns `IndexResult(Indexed, Skipped)` instead of a bare count, because "0 files"
after opening a large workspace reads as a failure where *"All 48,213 file(s) already indexed"*
reads as fast.

### Why there is no "are you sure you want to quit" dialog

Because interruption is no longer worth warning about:

- Chunks are committed as they go, so stopping keeps everything already done — that was true before.
- Skip-if-unchanged means resuming re-reads only the remainder — that is new.

Together, a kill mid-index costs at most 100 files of work. A dialog guarding two seconds of work is
friction that teaches people to dismiss dialogs. The status line says *"Indexing stopped — progress
so far is kept"* instead. Both behaviours are covered by tests that interrupt a run part-way and
assert what survives and what the next run re-reads.

### Offering, not demanding

Above **5,000 files** the workspace is not indexed automatically. An **inline, non-modal banner**
over the grid asks, and browsing carries on regardless of whether it is ever answered:

> 5,001 files in this workspace. Index them so you can search titles, keywords, ratings and camera
> details? Browsing works either way.  **[Index] [Not now]**

The answer is stored **per workspace**, so it is asked once rather than every open. **Not now** is
not a one-way door — an **Index workspace** button then appears in the status bar.

Below the threshold the work is short enough that asking would be more disruptive than the indexing.

### Indexing the workspace, not the folder

The workspace pass always walks the tree recursively, regardless of the **Recursive** toggle — that
toggle controls what is *shown*, and a search promising the workspace while only covering its top
folder would be a poor promise.

Two guards stop the passes fighting: browsing a subfolder cannot cancel a running workspace index,
and the per-folder index is suppressed while a workspace pass is pending. Without the second one,
opening a workspace indexed the top folder and then immediately walked the same files again — caught
by seeing the same line logged twice.

Declining also suppresses per-folder indexing, which would otherwise quietly override the answer.

### Verified in the running app

- Reopening the 18-file workspace: *"Indexed 0 file(s), skipped 18 already current"*.
- A synthetic **5,001-file** workspace showed the banner, kept browsing underneath, and recorded
  **Not now** as `false` in settings.
- **Index workspace** then indexed all 5,001 and flipped the stored answer to `true`.
- Reopening it read **zero** files: *"Indexed 0 file(s), skipped 5001 already current, of 5001"*.
- `dotnet test` — **302/302 passing** (was 288).

### Checked and not a bug

`RemoveMissingAsync` deletes from `Media` and `MediaSearch` but not `MediaKeyword`, which looked like
it would orphan keyword links: the cascade relies on `PRAGMA foreign_keys`, which is per connection
and only set where the schema is applied. It turns out **Microsoft.Data.Sqlite enables foreign keys
by default**, so the cascade fires on every connection. A regression test now pins that, since the
code would silently rot if the provider ever changed its default.

Note this does *not* hold for the `sqlite3` CLI, which leaves foreign keys off — hand-written
maintenance SQL against this database has to enable them explicitly.

### Worth knowing

**The threshold is a file count, not a size or a time estimate.** Count is what is known before any
work starts. A workspace of 4,000 RAWs will index without asking and still take a while.

**Nothing re-indexes on a timer.** A file changed by another application is picked up the next time
its workspace is opened or the folder is browsed, not while the application sits idle.

---

## Interlude — Layout ✅

Controls that act on one panel moved into that panel, leaving the top bar to do one job.

| Was | Now |
| --- | --- |
| **Recursive** checkbox in the top bar | **Include subfolders**, in a footer on the folders panel |
| **Size** slider in the top bar | A slim strip along the bottom of the thumbnail panel |
| — | **☰** in the top bar collapses the folders panel (⌘B) |
| **Cancel Scan** in the top bar | Beside the scan progress bar in the status bar, which is what it cancels |

The top bar is now a single full-width search field, following the reference: magnifier inside on the
left, clear **✕** inside on the right while results are showing, and the scope toggle as a filter
button to its right.

### The search field names its own scope

The watermark reads **"Search testmedia"** — the workspace name — rather than a syntax example, and
switches to **"Search everything indexed"** when the scope toggle is on. A search box that says
*"Search testmedia"* while quietly searching the whole catalog would be a lie, so the watermark is
computed from the workspace *and* the toggle, with both notifying it.

The filter syntax it used to advertise (`keyword: rating: type:`) moved to the tooltip. Discoverable
on hover, not occupying the field permanently.

### Collapsing binds the column, not the panel

`IsVisible` on the tree alone would leave its 240px column behind as a gap. The first
`ColumnDefinition.Width` is bound to a `GridLength` on the ViewModel instead, and the splitter
follows the panel's visibility so there is no orphaned drag handle against the window edge.

### Two layout fixes found by looking

**The size strip was overlaying the thumbnails.** Anchored bottom with a translucent background, it
sat on top of the third row of tiles, which showed through it. Given its own `Auto` row it takes
space instead of covering content.

**The default slider is tall.** At its natural height the strip stole most of a row of thumbnails,
so the slider height is pinned to 24.

### Verified in the running app

- Collapsed: the grid goes from 4 tiles across to 5, with no leftover gap, and restores on a second
  click.
- **Include subfolders** sits at the foot of the folders panel; the size strip at the foot of the
  thumbnails, hidden while the empty state is showing.
- Incidental confirmation of Phase 9: `/private/tmp` was swept overnight, so the test media was
  recreated at the same paths with new sizes and timestamps. The next run re-read all 15
  (*"Indexed 15, skipped 0"*) and the run after skipped all 15 — exactly the intended behaviour for
  changed-in-place files.
- `dotnet test` — **302/302 passing**.

### Worth knowing

**The collapse state is not persisted.** Reopening starts with the panel showing. Adding it to
settings alongside the workspace would be a few lines if that becomes annoying.

### Fixed — the splitter broke the layout

Reported from testing: dragging the horizontal splitter emptied the thumbnail grid and left the size
slider floating in the middle of a black panel.

**Cause, and it was the size bar's fault.** Giving the bar its own row put it *between* the grid and
the splitter:

```text
Auto  index prompt
*     thumbnails
Auto  size bar      <- splitter resized this
4     splitter
280   preview
```

A `GridSplitter` resizes the rows on either side of it. With the size bar directly above, dragging
inflated **the size bar's row** rather than the thumbnails. The `*` row collapsed to nothing, so the
grid vanished, and the slider — vertically centred in a now-enormous row — was left floating.

The bar belongs *inside* the thumbnail region, not beside it. Row 1 is now a `DockPanel` with the bar
docked to its bottom and the grid filling the rest, so the splitter is once again adjacent to the
thumbnails and resizes them. This also fixes the bar being clipped by the splitter, since it now sits
above it by construction rather than by coincidence.

The lesson generalises: **anything docked between a splitter and the content it resizes changes what
the splitter does.**

### The size bar became a View flyout

The strip was replaced rather than tuned further. Two rounds of fixing it — first for overlaying the
tiles, then for being clipped by the splitter and misaligning its own label — were symptoms of a
control that had no good place to live: it competed with the splitter for the bottom edge of the
panel and had to be kept clear of it by hand.

A **View** button in the toolbar, right of the filter, now opens a flyout holding the thumbnail size
slider with a live `px` readout. It costs no permanent space, cannot collide with the splitter, and
is the place for the display controls that follow — sort order, what the tiles show — without each
one having to find room in the chrome.

With the strip gone, the thumbnail region is a plain `Panel` again: the grid and the empty-state
overlay, on one surface.

*Superseded note, kept because the trap is easy to hit again:* an attempt to make the strip compact
pinned the slider to `Height="20"`. That is shorter than the Fluent template needs, so the thumb was
**clipped in half** and the track pushed off centre, leaving the label misaligned with the line it
labelled. Clamping a control shorter than its template fights the template and loses.

### A real filter icon

The `⑂` placeholder is now a `PathIcon` funnel — `M2 4 H22 L14 13 V20 L10 18 V13 Z` — wide mouth,
converging neck, stem down the middle. Vector, so it scales with the button and takes the theme
foreground.

### Verified

- Splitter dragged **up** and **down** to both extremes: the thumbnails resize, the grid never
  empties, and the size bar stays pinned at the bottom of the thumbnail region, never under the
  splitter.
- `dotnet test` — **302/302 passing**.

### Icons

Both toolbar icons are `PathIcon` vectors, so they scale with the button and take the theme
foreground rather than depending on a font having the glyph:

| Button | Geometry |
| ------ | -------- |
| Search scope | Funnel — `M2 4 H22 L14 13 V20 L10 18 V13 Z` |
| View | Three slider tracks with knobs, the standard "tune" geometry |

The first attempt at the View icon was hand-drawn from two tracks and read as a muddle at 14px.
Small icons are mostly negative space, and a shape that works at 24px often does not survive the
reduction — the three-track version was drawn for this size and does.

### Verified

- View sits right of the filter; the flyout opens on click and its slider drives the grid live
  (dragged to **261 px**, tiles resized immediately).
- The thumbnail panel has no chrome of its own now, and the splitter has nothing to collide with.
- `dotnet test` — **302/302 passing**.

### Fixed — the grid did not re-flow when tiles grew

Reported from testing: dragging the size slider **up** made the tiles zoom in place and overlap each
other, while the column count stayed put. Dragging **down**, or resizing the panel, laid out
correctly.

**Cause.** `VirtualizingWrapPanel` learned its cell size by measuring a live tile — deliberately, so
the layout followed the zoom slider without the panel needing to know the slider exists. That
inference was sound; the assumption underneath it was not. **A child invalidating its own measure
does not reliably re-run its parent's `MeasureOverride` in Avalonia.** So the tiles re-measured
themselves and grew, while the panel kept laying out on the cell size it learned last time.

Instrumenting the measure pass made it obvious: across a 20-step drag the panel measured **twice**,
and its cell size stalled at 192px while the slider reached 283.

The asymmetry follows from that. Tiles bigger than a stale cell overflow it — nothing clips them, so
they bleed into their neighbours. Tiles smaller than a stale cell simply sit inside it and look
fine, which is why only one direction appeared broken. Resizing the panel changed `availableSize`,
which *does* force a measure, so that appeared to fix it too.

**Fix.** An `ItemWidth` styled property on the panel, registered with `AffectsMeasure` and bound to
the same `ThumbnailSize`. The panel still does not lay out from it — a live tile remains the only
thing that knows the true cell size including margins and caption — it exists solely to guarantee the
invalidation. The probe is also explicitly invalidated before measuring, since `Measure` is a no-op
when a control still considers itself valid for the same constraint and would otherwise hand back
the previous size.

After the fix the same drag produced **22 measure passes**, tracking the slider continuously
(273 → 278 → 283 → 288 → 293).

The general shape of this one is worth keeping: **inferring layout state from a child is fine, but
the parent still has to be told when to look again.**

---

## Phase 10 — Audio ✅

Video played silently. It now plays with sound, and the transport has a volume control.

### How it works

```text
ffmpeg -vn -f s16le -ar 48000 -ac 2  →  PCM on stdout  →  CoreAudio AudioQueue
```

A second ffmpeg decodes the audio track of the **same file** the video frames come from — the proxies
have always carried their audio, so nothing needed regenerating and there are no two sources to
correlate. ffmpeg resamples anything to one fixed format, so the output device never negotiates.

**CoreAudio via P/Invoke rather than a bundled media library.** AudioQueue is part of the OS: no
native binary to ship, sign or update. The interop is six functions — create, allocate, enqueue,
start, stop, dispose. LibVLCSharp was already ruled out in Phase 4 (its macOS build is Intel-only,
from 2018), and SDL2 or OpenAL would mean shipping binaries for a preview feature.

The device *asks* for audio rather than being pushed to: a callback fires as each buffer is consumed
and refills it. That callback runs on a CoreAudio thread, so it never blocks on the decoder, never
allocates, and never throws — an exception crossing back into native code would take the process
down, so it catches everything and pads with silence instead.

Three buffers of 100 ms each. Enough that a scheduling hiccup does not produce a gap, short enough
that a seek is heard promptly. The queue feeding them is bounded, and that back-pressure is what
holds decoding to realtime instead of letting ffmpeg race to the end of the file.

### Volume

Applied by **scaling the samples in the pump**, not through a device volume control. It therefore
works identically on any future backend, mutes exactly rather than merely quietly, and takes effect
within one decode chunk. The multiplier is fixed-point so the audio path does no floating-point work
per sample and reads of it are atomic without a lock.

`Volume` and `IsMuted` are separate so unmuting returns to the level that was set rather than to a
default.

### The control

A speaker button sits with the transport, right of the frame-step buttons, opening a flyout with a
mute toggle and a slider. It is **disabled rather than hidden** for a silent file, so the control
stays where the eye expects it and its tooltip can say why nothing happens.

Knowing a file is silent needed ffprobe to report more: it used to ask only for `v:0`, so audio was
invisible to it. It now lists every stream's `codec_type`, which means the video stream has to be
picked out by type rather than assumed to be first — with a fallback to "the first stream that has
dimensions" for output that does not label its streams. That fallback is not hypothetical: it is
what three existing parser tests were built on, and they caught the regression immediately.

### Verified

- A test tone through the interop produced callbacks every 96–106 ms against a 100 ms buffer, which
  is the device consuming samples at realtime rate — and **confirmed audible** by ear.
- A clip with a real audio track: the speaker button enabled itself (so `HasAudio` was detected), the
  flyout showed **80%**, and playback ran with the decoder invoked exactly as intended:
  `-ss 0 -i WITHSOUND.mp4 -vn -f s16le -acodec pcm_s16le -ar 48000 -ac 2 -`.
- After the clip ended: **no leftover ffmpeg processes**, no errors logged.
- `dotnet test` — **316/316 passing** (was 302).

### Worth knowing

**Audio is macOS-only for now.** `SilentAudioOutput` reports itself unavailable on Windows and Linux,
so the player skips decoding entirely rather than running ffmpeg to throw the samples away, and the
volume control disables itself. Adding a platform means implementing one interface with four methods.

**Video and audio are started together and each runs at its own rate.** No audio clock, no frame
dropping. For preview-length clips the drift is not perceptible, but this is the thing to revisit
first if playing something long ever looks out of step — the fix is to slave the video pacing to the
audio position rather than to a stopwatch.

**Seeking restarts the audio decoder.** `-ss` before `-i` keeps that fast, but a seek is a new
process, not a repositioning of the existing one.

---

## Phase 11 — Fullscreen and zoom ✅

Fullscreen inspection for both stills and video, with scroll-to-zoom and drag-to-pan.

### One viewer, not two

`ZoomPanViewer` transforms whatever child it is given, so a still and a video surface become
inspectable through the same control instead of each growing its own zoom implementation. That is
also what makes **side-by-side comparison** a matter of placing two of them beside each other later,
with their scale and offset optionally linked.

The child is laid out at its natural size and moved with a render transform, so zooming costs a
transform rather than a layout pass — and scale means what it says: **at 1, one content pixel covers
one screen pixel.** That matters for the stated purpose. "Fit" is therefore a computed scale rather
than a separate mode, and 100% is a real 1:1.

### The arithmetic is separate from the control

`ZoomState` holds the maths with no dependency on a window, so the fiddly parts are tested rather
than eyeballed:

- **Zoom anchors on the pointer.** The content point under the cursor is computed before the scale
  change and put back under it afterwards, which is what makes wheel zoom feel like it is pulling
  the image rather than scrolling past it.
- **Panning cannot lose the image.** Offsets are clamped to the content's own edges, and any axis
  where the content is smaller than the viewport is centred rather than draggable.
- **Resizing the window keeps the chosen magnification**; loading different content refits. The
  user's zoom is not the window manager's to reset.
- Trackpad deltas are fractional, so the step is raised to that power — a slow two-finger scroll is
  smooth instead of jumping a notch at a time.

### The fullscreen window

A separate window rather than a mode of the main one: the main window keeps its layout and selection
untouched, and closing needs nothing restored. Video **keeps playing** into it, because the player
pushes frames to whoever is listening rather than owning one view — the fullscreen surface simply
subscribes to the same events.

Entry points: the ⛶ button on the transport, the one on the still preview, double-clicking a still,
or **F**. Inside: scroll to zoom, drag to pan, double-click to toggle fit ↔ 100%, `0` fit, `1` actual
size, space to play/pause, Esc or F to leave.

**F is handled in code-behind rather than as a KeyBinding** so it can be ignored while a text box has
focus — otherwise typing "f" into the search box would throw you into fullscreen.

### Verified

- `dotnet test` — **327/327 passing** (was 316), 11 of them covering the zoom arithmetic: anchoring,
  clamping at both ends, pan limits, centring, refit-on-new-content and keep-scale-on-resize.
- Driven through the UI: see the follow-up section below, which is where the interesting bugs were.

### Worth knowing

**Zooming video magnifies a 720p frame.** `FfmpegFrameSource` caps decode at `MaxDecodeHeight = 720`
regardless of the Quality setting, because decoding a 5.3K frame to shrink it into a preview pane is
exactly the waste proxies exist to avoid. Zoom therefore works on video and is genuinely useful for
framing and motion, but it is **not** pixel-level quality inspection of a 4K source — past about 1:1
of the decoded frame you are looking at upscaling. Lifting that requires decoding at native
resolution when zoomed in, which is a real change to the decode path rather than a constant edit:
the frame buffers are pooled by size and a 5.3K BGRA frame is ~30 MB.

**Stills are inspected at full resolution**, since the preview bitmap is decoded at up to 1600px and
the source is re-read at native size — so 100% on a photo is a true 1:1.

### Follow-up — five fixes from testing

**It was not actually fullscreen.** Three attempts failed before the cause turned up:

| Attempt | Result |
| ------- | ------ |
| `WindowState="FullScreen"` in XAML | Ordinary small window |
| Sized manually to `screen.Bounds`, `SystemDecorations="None"` | Filled the screen, but the macOS menu bar drew over the top |
| `WindowState = FullScreen` in `Opened`, then posted to the dispatcher | No effect at all |

The cause was none of those things: **macOS will not take an owned window fullscreen.** The viewer was
shown with `Show(this)`, which makes it a child of the main window, and the request was silently
ignored. Shown with `Show()` instead, `WindowState.FullScreen` works exactly as advertised — no menu
bar, no chrome, the whole screen.

Worth remembering, because the failure mode is silence: the window simply stays the size it was.

**The initial fit was computed at the wrong size.** The window is created at an ordinary size and
only *then* goes fullscreen, so the first fit measured the small viewport and the zoom opened at 57%.
The rule that resizing preserves magnification — correct for a user resizing a window — was
preserving a number that had never been right. The viewer now refits on resize until the view is
first touched by hand, after which the magnification is the user's and resizing leaves it alone.

**Space re-triggered the last button instead of fitting.** Clicking *100%* left that button focused,
and a focused button takes Space as "press me". Fixed by handling keys on the **tunnel** rather than
the bubble, so the viewer's shortcuts run before any focused control sees them — safe here because
there is nothing to type into — and by making the overlay buttons non-focusable as well.

**Video opened black while paused.** Frames are pushed to whoever is listening, so a surface created
after the last frame was sent has nothing to show. `RefreshFrameAsync` re-emits the frame at the
current position when a new view appears.

**The transport is now one control, not two.** `VideoTransport` is shared by the inline preview and
the viewer, so they cannot drift apart. Its fullscreen button raises an event rather than acting,
because what it means depends on the host: enter from the main window, leave from the viewer.

### Also added

- **Space fits**, alongside `0`. Play/pause in the viewer is on the transport, `K` or `Enter` —
  resetting the view is what an inspection pass wants constantly, so it gets the big key.
- **← and →** move through the set, sharing the main ViewModel so the grid selection follows. Stops
  at the ends rather than wrapping, which would give no clue the last file had been reached.
- **A counter**, top-left: `4 of 17 · IMG001.png`.
- **The hint fades** after four seconds, via a transition on its opacity.

### Verified in the running app

- Fullscreen covers the entire screen, menu bar included, and reads **Fit** on open.
- **→ →** moved from *2 of 17 · DETAIL.png* to *4 of 17 · IMG001.png*.
- *100%* then **space** returned the label to **Fit**.
- A video opened fullscreen shows its paused frame and carries the complete transport — step, play,
  volume, scrub, quality.
- `dotnet test` — **327/327 passing**.

### Worth knowing

**Fullscreen uses a macOS Space.** That is what native fullscreen means on the platform, and it is
the price of covering the menu bar; the alternative that avoids it leaves the menu bar on top.

---

## Interlude — Viewer refinements ✅

### Portrait video was stretched

Reported from real footage. The cause was rotation metadata, and it is worth spelling out because
nothing in the chain looks wrong on its own:

- Cameras and phones record portrait footage as a **landscape stream plus a rotation**. ffprobe
  reports the *stored* size — 1920×1080 with `"rotation": 90`.
- **ffmpeg applies that rotation when decoding.** Frames arrive 1080×1920.
- The frame source scaled to the dimensions ffprobe reported, so `scale=1920:1080` squashed portrait
  content into a landscape frame.

`VideoSurface` letterboxes correctly and `ZoomPanViewer` fits correctly — they were both faithfully
presenting a frame that had already been ruined.

Fixed where it starts: the probe now reads the display matrix and swaps width and height on a quarter
turn, so `VideoMediaInfo` describes what frames will actually look like rather than how they are
stored. The old `rotate` tag is read too, for files that predate side data. Proxies were never
affected — they scale with `-2:height`, which derives the width after rotation.

Confirmed against a real rotated file: reported **720×1280**, decode target **404×720**. Unrotated
files are untouched. *Aspect ratio confirmed correct in the app by the user.*

### Opening the viewer

- **Double-clicking a thumbnail** opens it, which is what double-click means everywhere else.
- **Right-click** offers *Open Fullscreen* and *Reveal in Finder* — named for the platform's own file
  manager, so it reads "Show in Explorer" on Windows. Both act on the **right-clicked tile**, not the
  selected one, since right-clicking something that was not selected should act on what was clicked.

### Fullscreen or maximised, as a setting

macOS treats these as different things and the difference matters here: real fullscreen hides the
menu bar but takes a **Space of its own**, with the animation and context switch that implies — a lot
of ceremony for a look at one photo. The View flyout now offers both, and **Maximised is the
default**: it fills the current screen, stays on this desktop, and opens instantly.

Fullscreen remains available for when the menu bar is genuinely in the way. The choice is persisted.

### Verified in the running app

- Double-clicking a tile opened the viewer **maximised**, counter reading *3 of 18 · IMG000.png*.
- The View flyout shows **Open viewer as: Maximised window / Fullscreen**, maximised selected.
- Rotation verified end to end against the real file, through the real probe.
- `dotnet test` — **343/343 passing** (was 327).

### Worth knowing

**The context menu was confirmed working by the user** — both items — after automation could not
reach it (the app lost focus mid-test while the machine was in use). The platform command is covered
by tests regardless, including the Windows `/select,` quirk where a space after the comma opens
Documents instead of selecting the file.

---

## Interlude — Full-quality viewing ✅

Reported: images looked compressed fullscreen. They were. The viewer was showing the **preview**,
which is deliberately not the photograph:

- capped at **1600px** on the long edge, and
- re-encoded as **JPEG quality 85**.

Both are right for a grid of hundreds of tiles and wrong for judging a shot. On a 24MP file that is a
quarter of the linear resolution plus a lossy round-trip — and it made the zoom readout dishonest,
because "100%" meant one *preview* pixel per screen pixel, not one image pixel.

### What now happens

`SkiaFullImageDecoder` decodes the original at native resolution and hands back **raw BGRA** rather
than encoded bytes — re-encoding to pass it along would reintroduce exactly the loss being removed.
The UI blits those pixels straight into a `WriteableBitmap`.

The viewer shows the cached rendition first, because it is already in memory and appears instantly,
then swaps in the full decode when it arrives. Swapping refits **only if the view had not been
adjusted**, so a zoom set while waiting is not thrown away. `NaturalSize` follows the real image, so
100% is now a true 1:1.

Measured on a 6000×4000 JPEG: **91 MB decoded in 1.2s**. A 3000×2000 PNG: 22 MB in 93ms. The delay
is covered by the rendition being on screen throughout.

### Not keeping it around

A 24MP image is ~96 MB as BGRA. It is decoded on demand when the viewer opens, replaced when the
selection moves — a late-arriving decode checks the selection has not changed before displacing what
is on screen — and released when the viewer closes.

Very large images are decoded downscaled, above about **80MP**: that is already 320 MB, and a
panorama should not be able to exhaust memory from a double-click. The codec is asked to scale during
decode rather than decoding everything and shrinking it.

### Verified

- The decoder run against real files through its real code path: **6000×4000 in, 6000×4000 out**,
  where the preview pipeline would have produced 1600×1067.
- `dotnet test` — **349/349 passing** (was 343). Six new: native size preserved, buffer matches
  dimensions, JPEG sources, video rejected, missing file returns null rather than throwing, and RAW
  with no extractor yields nothing.
- **Not driven through the UI by hand** — the machine was in use, so automation stopped rather than
  taking over the screen.

### Worth knowing

**RAW still goes through the embedded preview.** That is the largest image available without a
demosaicing library, and for most cameras it is the full-size JPEG the camera itself produced — so
this is a large improvement on the 1600px rendition, but it is not a RAW development. Actual RAW
decoding would mean taking on something like LibRaw.

**The grid and inline preview are unchanged.** They still use the fast JPEG renditions, which is what
keeps browsing quick; only the viewer pays for full quality.

---

## Phase 12 — RAW development ✅

RAW files were displayed from the JPEG the camera embedded beside them. They are now demosaiced
properly, with a setting to choose.

### Correcting an earlier claim

While investigating I told the user the embedded preview was "full sensor resolution". **It is not.**
On the X-S20:

| | Pixels |
| --- | --- |
| Embedded preview | 4416×2944 — 13MP |
| Developed RAW | **6252×4176 — 26MP** |

So developing is not only about tonal latitude and skipping in-camera processing: it is **twice the
pixels**. That materially changes what the feature is worth, which is why it is recorded here rather
than quietly fixed.

### Why LibRaw, after trying not to need it

macOS ImageIO decodes RAW natively and would have meant no new dependency — the same argument that
made CoreAudio right for audio. It lists `com.fuji.raw-image` among its supported types, so it looked
like a straight win.

It cannot decode this camera. `CGImageSourceCreateWithURL` recognises the file and reports the right
UTI; `CGImageSourceCreateImageAtIndex` returns null, as does the thumbnail path. `sips` fails with
"Cannot extract image from file". macOS 26.1 simply does not support X-S20 RAF, and Apple's RAW
support is only ever as current as the OS.

So: **LibRaw**, as a third optional external tool alongside ExifTool and FFmpeg, found by the same
kind of locator and degrading the same way — without it, RAW files still display from their embedded
preview and only the developed rendering is lost.

### The CLI, not the library

`dcraw_emu` is driven as a process rather than P/Invoking `libraw.dylib`:

- It matches how ExifTool and FFmpeg are already used.
- A malformed RAW crashes the tool, not the application.
- Nothing native has to be shipped or signed.

Output comes back as a **PPM on stdout** (`-Z -`), parsed directly into BGRA. A 26MP develop is 78MB;
writing that to a temporary file and reading it back on every image would cost more than the
demosaic. The parser is written out rather than pulled in — a P6 PPM is a header and a block of RGB
bytes, and this is the hot path for a 26MP image.

Arguments worth their comments: `-w` uses the **camera's** white balance, because the comparison
being made is against the photograph as shot rather than LibRaw's guess at neutral; `-o 1` for sRGB;
`-q 3` for AHD interpolation, which is slower than bilinear and visibly better on fine detail — the
entire reason for developing at all.

### The setting

**View → RAW files → Develop the RAW / Embedded JPEG**, persisted, defaulting to developing. Changing
it reloads what is on screen, so the difference is visible immediately.

Developing is worth defaulting to because the cost is hidden: the viewer already shows the cached
rendition instantly and swaps in the full decode when it lands, so the wait happens behind a picture
rather than a blank screen. A failed develop falls back to the embedded preview rather than showing
nothing.

### Verified against a real file

Run through the real decode path on an X-S20 RAF (borrowed from the user's library read-only, and
deleted afterwards):

```text
LibRaw available: True (/opt/homebrew/bin/dcraw_emu)
  embedded JPEG  4416x2944   49 MB    383 ms
  developed RAW  6252x4176   99 MB   3781 ms
```

`dotnet test` — **358/358 passing** (was 349). Nine new, covering the camera white balance flag,
stdout and sRGB arguments, PPM parsing to BGRA in the right channel order, headers with comments and
irregular whitespace, and rejection of the greyscale variant, 16-bit, truncated bodies and zero
dimensions — anything that could be half-read into a plausible-looking wrong image.

### Worth knowing

**Developing takes about four seconds for 26MP.** That is LibRaw doing real work at `-q 3`, and it is
per image, so arrowing through a folder of RAWs in the viewer will always be showing the embedded
preview first. Switching the setting to Embedded JPEG makes browsing instant at the cost of half the
pixels.

**The grid and inline preview still use the embedded preview**, deliberately — a folder of RAWs would
be unusable if every tile demosaiced.

**LibRaw is LGPL-2.1 / CDDL**, used here as a separate executable, which keeps it at arm's length
from the application's own licensing.

### Follow-up — develop feedback and switching

**A processing hint.** Developing takes seconds and the rendition on screen looks final, so the
viewer said nothing was happening. A progress strip now reads *"Developing RAW…"* — or *"Loading full
resolution…"* for ordinary images — and clears when the decode lands. Only the run that is still
current may clear it, so an older decode finishing late cannot hide the wait for the image now on
screen.

**`\` switches between the developed RAW and the embedded JPEG**, the way Lightroom uses it to flip
between two renderings of the same shot. The key is reported under three different names depending on
keyboard layout (`OemBackslash`, `OemPipe`, `Oem5`), so all three are handled.

### Two bugs the hint exposed

**Cancelled develops were left running.** Disposing a `Process` does not stop it, so arrowing through
a folder of RAWs would abandon a full develop for every file passed over, each burning a core while
the one actually wanted competed for CPU. The decoder now kills the process in a `finally`, as the
video frame source already did.

**The same decode was started two or three times per selection.** Selecting an item raises
`SelectedItem`, `Preview` and sometimes `IsVideoSelected`, and the viewer refreshed on each — so a
four-second develop was started and cancelled repeatedly before the one that survived. The ViewModel
now remembers what the in-flight decode is for and ignores repeat requests for the same file. The
`\` toggle explicitly clears that guard, since it changes *how* the same file is rendered.

### Verified

- *"Developing RAW…"* appeared over the embedded preview and cleared when the develop landed.
- `\` flipped the setting `True → False`, persisted.
- No `dcraw_emu` processes left behind afterwards.
- `dotnet test` — **358/358 passing**.

---

## Interlude — Develop controls ✅

Four develop options exposed where they are used, and the speed/quality choice put where it can be
switched mid-session.

### A Develop panel in the viewer

A **Develop** button beside the zoom controls, appearing only for RAW files and only when developing
is on — a panel of adjustments that cannot do anything is worse than no panel.

| Control | LibRaw | Why it is one of the four |
| ------- | ------ | ------------------------- |
| Highlights | `-H 0..3` | Clip / Unclip / Blend / **Rebuild**. Reconstructing a blown sky from the channels that did not clip is the main reason to open the RAW instead of the JPEG. |
| White balance | `-w` / `-a` | Camera is the picture as shot; Auto averages the frame when the camera got it wrong. |
| Exposure | `-aexpo` | In **stops**, applied during the develop, so it recovers detail rather than brightening a finished image. |
| Noise reduction | `-fbdd 1\|2` | Applied before demosaicing, which is where it belongs. Off by default; it costs time and detail. |

Plus **Reset to as shot**, disabled while there is nothing to reset, and a line making clear the
settings apply to every RAW and nothing is written to the files. This is a viewer, not an editor.

### Speed as a working mode, not a buried preference

**RAW quality** — Fast / Balanced / Best — sits in **View options** rather than Settings. The request
was framed as an app setting, but the use case given was *"sometimes I want to quickly browse for a
first-pass accept/reject, sometimes a full quality inspection for final picks"*. That is something
switched several times in a session, so it belongs one click away rather than two dialogs deep. It
still persists like any other preference.

Named for the decision rather than the algorithm, because the choice being made is "am I culling or
judging", not "do I want AHD": Fast is `-q 0` linear, Balanced `-q 2` PPG, Best `-q 3` AHD.

### Details worth their code

**Exposure is debounced by 400ms.** A develop takes seconds; starting one per slider tick would queue
work faster than it could be cancelled. Everything else applies immediately, being a discrete choice.

**"As shot" passes no exposure argument at all**, rather than a multiplier of 1.0 — which is not
quite a no-op in LibRaw. As shot should mean exactly that.

**Stops are converted to LibRaw's linear multiplier and clamped** to the 0.25–8 it accepts, so a
slider cannot produce an argument the tool refuses. Written invariantly, because a comma decimal
separator would make dcraw reject it.

**Auto white balance swaps the flag rather than adding one.** Passing `-w` and `-a` together would
let dcraw pick, which is not a choice the user made.

**Output colour space is still not offered.** With no colour management, a wider space would be shown
as though it were sRGB — misrepresenting the colour rather than improving it. Worth revisiting
properly, since the display is P3.

### Verified in the running app

- The Develop panel appeared for a RAF and not for JPEGs, with Reset correctly disabled until
  something was changed.
- Exposure moved to **+2,5 EV**, persisted as multiplier **5.657** — 2^2.5 — and the image visibly
  brightened.
- **Reset to as shot** returned it to `IsDefault: True`.
- View options shows **RAW files** and **RAW quality** together.
- `dotnet test` — **376/376 passing** (was 358). Eighteen new, covering every flag mapping: highlight
  and quality codes, the white-balance swap, noise reduction only when asked for, silence at as-shot,
  stops to multiplier, clamping, and invariant formatting.

### Follow-up — re-rendering is a comparison

Two changes, both from the same observation: when a develop setting changes, the *only* thing that
should change is the pixels.

**The preview no longer flashes back.** `EnsureFullPreviewAsync` discarded the current full-size
render before starting the next one, so several seconds of lower-quality rendition appeared between
two versions of the same photograph — which is precisely what makes a comparison useless. The
existing render is now kept on screen until its replacement is ready, and only released once the new
one has been assigned. It is still discarded immediately when moving to a *different* file, where the
old picture would simply be wrong.

**The view no longer resets.** Zoom and position survive a re-render, so a region can be examined
while adjusting noise reduction or exposure — which is the only way to judge either.

That needed `ZoomState.SetContent` to stop refitting whenever the content changed. Content changing
does not mean a different photograph: re-developing produces different pixels of the same scene, and
switching between the developed RAW and its embedded JPEG produces a **different resolution** of it.
So it now preserves the view, keeping the same region framed and the same magnification *relative to
fit* — which is what makes the comparison honest across 6252×4176 and 4416×2944. Fitting is left to
the caller, which is the only thing that knows a new photograph has been opened.

### Verified

- Zoomed to **47%** on a detail, changed highlights and exposure: the zoom stayed at 47% throughout
  the re-develop, and the framing came back pixel-identical — same subjects, same crop, only the
  tone different.
- `dotnet test` — **379/379 passing** (was 376). One existing test was rewritten rather than patched:
  *"loading different content refits"* encoded the old contract, and the new one is that the caller
  decides. Three added for the new behaviour: same-size replacement changes nothing, and a
  different-size replacement keeps both the framed region and the relative zoom.
