# BetterDAM — Implementation Progress

A running log of what has been built, phase by phase. Each entry records what changed, the
decisions worth remembering, and what is deliberately left for later.

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
- `LazyThumbnail` (an `Image` subclass) requests its thumbnail only when the virtualizing panel
  realizes its container — opening a folder of 50,000 files only decodes what is on screen.
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

**RAW files get no thumbnail yet.** Skia cannot decode CR3/NEF/ARW. They are scanned, listed, and
selectable, but show "No preview". The fix is to pull the embedded JPEG preview out of the RAW with
ExifTool, which is Phase 2/3 work.

**The preview pane is a large cached thumbnail** (1600px), not the original file. Real zoom/pan on
full-resolution originals is a later refinement.

**Cache location:** `~/Library/Application Support/BetterDAM/Cache` (Thumbnails, Logs). It is fully
disposable — delete it and the app rebuilds from the originals.

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

## Phase 4 — Video ⏳ Not started

FFmpeg integration, video playback, proxy generation, playback quality selection, video metadata
display.
