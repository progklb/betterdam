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
    Tests/BetterDAM.Tests        xUnit tests. 27 passing.
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
dotnet test                                             # 27 tests
```

### Deliberately deferred

Nothing in Phase 1 reads or writes metadata. No ExifTool, no XMP, no SQLite catalog, no video
playback, no search. The interfaces are shaped so those slot in without rework:
`IThumbnailGenerator` is already a pluggable list, and `IMetadataProvider` will sit alongside it in
Core in the same way.

---

## Phase 2 — Metadata ⏳ Not started

Read embedded metadata, read XMP, display camera and video metadata, edit basic metadata, keywords,
and ratings.
