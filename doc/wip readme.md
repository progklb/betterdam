# Better Digital Asset Management

An attempt to make better media management software.

The project evolved from a frustration with Adobe Bridge's video keywording workflow into an idea for a standalone media metadata browser/manager that sits between the filesystem and applications such as Adobe Bridge, Lightroom, Premiere Pro, and DaVinci Resolve.

The core idea is:

> Browse and edit metadata quickly, keep the original media untouched by default, store metadata consistently as XMP, and explicitly "Sync" metadata into files when needed.

## 1. Why the project started

I found it extremely frustrating manually tagging a large collection of videos in Adobe Bridge.

The workflow was frustrating because:

- Bridge effectively makes you work one item at a time.
- Selecting/changing videos causes Bridge to pause for several seconds.
- Video previews are expensive to generate/decode.
- Bridge was modifying the original video files when metadata was changed.
- The file's modification timestamp consequently changed.
- I wanted to preserve the original media and preferably have metadata stored externally.

So the original motivation was:

> I have a large media collection (mainly videos) and metadata management shouldn't be this tedious or destructive.

## 2. A better way

The project became a metadata browser.

The more interesting idea became a dedicated application roughly combining concepts from:

- Adobe Bridge
- Photo Mechanic
- ExifTool
- Lightroom's catalog/search capabilities

The application would primarily be a media browser and metadata editor.

Current software solutions use a mixed approach. Raw photo files get sidecars and non-destructive editing, but JPGs and videos are destructively edited. This is true for edits to media payload, as well as editing metadata like titles, keywords, ratings, etc.

This project's key differentiator would be:

> Treat all media consistently, regardless of whether the file is a JPEG, RAW, MP4, MOV, etc.

The application shouldn't make the user care about the quirks of each media format.

## 3. Core philosophy

The strongest architectural idea we arrived at was a virtual metadata layer.

Suppose you have:

- IMG001.CR3
- IMG002.JPG
- VID001.MP4
- VID002.MOV

The application loads the existing metadata and presents a unified view.

When you edit metadata, it does not immediately modify the media.

Instead:

```
Original media
      +
Embedded metadata
      +
XMP metadata
      +
Pending changes
      ↓
Displayed metadata
```

The user can then explicitly choose:

**Sync*

to commit the metadata to the filesystem.

This gives the application a workflow somewhat analogous to Git:

```
Working state
     ↓
Edit
     ↓
Review
     ↓
Commit / Sync
```

## 4. XMP strategy

This became one of the central decisions.

The desired workflow while editing is:

```
Media file
   +
XMP sidecar
```

For example:

- VID001.mp4
- VID001.xmp

The original video remains untouched while you work.

Then a Sync operation can bake the XMP metadata into the media when necessary.

So conceptually:

```
                XMP
                 │
                 │
       ┌─────────┴─────────┐
       │                   │
   Keep external        Sync/embed
       │                   │
       ▼                   ▼
   sidecar remains     media updated
```

This provides:

- No unnecessary modification of originals.
- No timestamp changes during ordinary editing.
- Easy manual editing.
- Faster viewing and editing (no media file writes).
- Video doesn't reset when a file write occurs (i.e. you can't add a keyword mid playback as it locks up and resets the play head)
- Ability to make lots of changes before committing.
- Ability to regenerate metadata.
- Compatibility with applications that understand XMP.
- A single metadata representation that can later be baked into files.

## 5. Bridge's XMP behaviour

We investigated why Bridge was changing the video itself.

The important distinction is:

### RAW

Adobe commonly uses:

- IMG001.CR3
- IMG001.xmp

### JPEG

Adobe generally expects metadata to be embedded in the JPEG.

### Video

Bridge commonly embeds metadata into formats such as MP4/MOV rather than using an external XMP sidecar as its primary mechanism.

## 6. Photo Mechanic

We specifically investigated Photo Mechanic because it was an example of a program that can write external XMP sidecars for video.

The important appeal was:

- VID001.MP4
- VID001.XMP

without modifying the MP4 itself.

That demonstrated that the external-XMP workflow we wanted was practical.

Photo Mechanic otherwise has a lot of the functionality we are looking for:

- Very fast browsing
- Fast metadata editing
- Batch operations
- XMP support

But it doesn't solve the broader problem of creating a unified metadata tool with the exact behaviour we want, and its expensive.

## 7. Adobe Lightroom

We discussed Lightroom primarily in terms of interoperability.

The goal isn't necessarily to replace Lightroom.

Instead:

> Our application should produce metadata that Adobe software can consume whenever possible.

For RAW workflows, XMP sidecars are a well-established Adobe mechanism.

JPEGs and videos are more complicated because Adobe applications don't universally treat external XMP sidecars as the authoritative metadata source.

This reinforced the idea of having a Sync operation that can eventually embed metadata when Adobe compatibility requires it.

## 8. Adobe Premiere Pro

Premiere was more promising than DaVinci Resolve for interoperability.

Premiere is part of Adobe's broader XMP ecosystem and uses XMP extensively.

The desired ecosystem therefore looks something like:

```
              Metadata Manager
                     │
                  XMP
                     │
          ┌──────────┼──────────┐
          ↓          ↓          ↓
       Bridge     Lightroom   Premiere
```

This is one of the reasons standards-based XMP is an important design goal.

Premiere also has project-specific metadata, so not everything in Premiere is necessarily portable XMP metadata.

But for basic metadata such as:

- Keywords
- Description
- Rating
- Creator
- Copyright
- Camera information
- etc.

the Adobe ecosystem is much more compatible with the project's goals than Resolve.

## 9. DaVinci Resolve

Resolve takes a substantially different approach.

It is much more project/database-centric.

Conceptually:

```
Media
  ↓
Resolve
  ↓
Resolve Project Database
```

Rather than treating XMP as the universal metadata store, Resolve stores a lot of its organizational metadata inside the Resolve project.

Therefore, we don't want to design the application around Resolve.

Instead, Resolve could eventually have a dedicated exporter/integration.

Potential future integrations could include:

- CSV
- ALE
- FCPXML
- Resolve scripting
- Other Resolve-compatible metadata mechanisms

But this is future scope, not core functionality.

## 10. What the actual application should look like

The proposed UI is deliberately familiar to Bridge.

Something roughly like:

+-------------------------------------------------------------+
| Toolbar                                                     |
+-------------------------------------------------------------+
| Folder Tree |       Thumbnail Grid       | Metadata         |
|             |                            | Inspector        |
|             |                            |                  |
|             |                            |                  |
|             |                            |                  |
+-------------+----------------------------+------------------+
|                    Preview / Video Player                  |
+-------------------------------------------------------------+
| Status                                                      |
+-------------------------------------------------------------+

### Left pane

Folder/navigation browser.

Potentially:

- Photos
- Videos
- Projects
- Collections

with normal filesystem folders being the initial priority.

## 11. Thumbnail browser

The central pane shows thumbnails.

Each media item could have metadata/status indicators such as:

```
IMG001.JPG
★★★★
✓ Synced
```

or:

```
VID001.MP4

🟡 Modified
📄 XMP
```

The exact visual design isn't fixed yet.

The important concept is that the browser should immediately communicate:

- media type
- rating
- keywords
- metadata state
- whether changes are pending
- whether a sidecar exists
- whether there is a conflict

## 12. Video preview

Video playback was one of the explicit requirements.

We want to be able to select a video and actually view it in the application.

But you don't necessarily want to decode a massive 4K/5.3K/8K source every time.

Therefore the proposed UI includes playback quality:

Playback Quality

```
○ Original
● 720p
○ 480p
○ 360p
```

The application could generate and cache lightweight proxy videos. This makes browsing much more responsive.

However, this should be entirely optional. If disabled, no cache should be written.

## 13. Image preview

For images, the preview should support at least:

- zoom
- pan
- normal image preview

Potential future additions:

- histogram
- focus checking
- image information

But those aren't necessarily MVP requirements.

## 14. Metadata inspector

The metadata panel was proposed as several logical sections/tabs.

### General

- Title
- Description
- Keywords
- Rating
- Label
- Creator
- Copyright

### Camera

Read-only technical metadata such as:

- Camera
- Lens
- ISO
- Shutter speed
- Aperture
- Focal length
- GPS
- Capture date

### Video

- Codec
- Resolution
- Frame rate
- Duration
- Bitrate
- Colour space
- HDR
- Audio

### XMP

A more advanced/raw view of the actual XMP fields.

For example:

```
dc:title
dc:subject
xmp:Rating
photoshop:Headline
```

This is especially useful for power users.

## 15. Metadata types

We wanted the application to support both:

### Existing technical metadata

Read from the media:

- Camera
- Lens
- Exposure
- Resolution
- Codec
- Duration
- Frame rate
- GPS
- etc.

### User-editable metadata

Such as:

- Title
- Description
- Keywords
- Rating
- Label
- Creator
- Copyright
- Headline

The application should preserve existing metadata rather than accidentally destroying vendor-specific metadata.
