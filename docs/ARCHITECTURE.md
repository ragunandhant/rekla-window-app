# Race Video Processor — Architecture & Implementation Design

This document intentionally starts with the requested design decisions before describing the implementation.

## 1. Recommended architecture

The application is split into three runtime layers plus tests:

```text
WPF UI / MVVM
    ↓ explicit user commands only
Application orchestration (polling + sync + view models)
    ↓
Core contracts and domain models
    ↓
Infrastructure adapters
    ├── Real API provider → IRealApiPayloadAdapter
    ├── Mock provider (100 deterministic entries)
    ├── SQLite local-state repository
    ├── FFprobe service
    ├── FFmpeg processing/preview service
    └── local log
```

The most important boundary is that **remote data, local video mapping, and local processing state are different objects**. Polling can mutate remote entry metadata. It cannot select a file and cannot start FFmpeg.

`VideoProcessingService` also owns a `SemaphoreSlim(1,1)` gate. Even if commands are triggered twice, the application will not create a parallel encoding queue.

## 2. Project / folder structure

```text
RaceVideoProcessor.sln
src/
  RaceVideoProcessor.Core/
    Models/
    Interfaces/
    Services/
  RaceVideoProcessor.Infrastructure/
    Data/
    Providers/
    Video/
    Logging/
    Services/
  RaceVideoProcessor.App/
    Commands/
    Services/
    ViewModels/
    Views/
tests/
  RaceVideoProcessor.Tests/
scripts/
  publish-win-x64.ps1
docs/
  ARCHITECTURE.md
```

Core has no WPF dependency. Infrastructure does not depend on the UI. WPF composes the system using dependency injection.

## 3. Data models

The operator-facing identifier is the **primary player's card number**
(`player.cartNo`). The application never invents a sequential entry number.
Stable API identifiers are kept for data identity only and are not displayed.

```csharp
RaceEntry
    EntryId         // internal identity: marker ?? playerId ?? cardNo
    CardNumber      // player.cartNo — the entry, as far as the operator is concerned
    PrimaryName     // player.ownerName, Tamil
    PrimaryLocation // player.location, Tamil
    SecondaryName?  // secondaryPlayer.ownerName, null when there is no second player
    SecondaryLocation?
    TimingSeconds   // timings — race performance time, never a date
    RaceTypes       // distinct player.raceId[].types, e.g. ["200","300"]
    ExtractionStatus / RemoteStatusText   // from status
    VideoLink / IsVideoEnabled            // remote reference only
    EntryDateUtc
    PlayerId / UserId / Marker            // technical, diagnostics only
```

`PrimaryDisplay` and `SecondaryDisplay` compose `"name, location"` once, and both
the UI and the scoreboard use them, so the two can never drift apart.
`SecondaryDisplay` is `—` when there is no secondary player. There is no
secondary card number anywhere in the model.

`EntryLocalState` is keyed by `EntryId` and holds everything the operator owns:
the mapped local video, processing status, output path, error, and the data-version
hashes. Each entry's state is independent; selecting a different entry reads a
different row and mutates nothing.

## 4. Mock API design

`MockDataProvider` owns 100 deterministic records built from varied Indian names/cities and repeatable completion times.

- Startup availability defaults to exactly Entry 001.
- Automatic release uses `DemoEntryIntervalSeconds` (default 20 s).
- Each qualifying mock poll releases at most one entry.
- `SimulateNextEntryAsync()` releases exactly one immediately.
- The released count is persisted in settings so restart does not reset the demo unexpectedly.
- Every 10th demo item briefly exposes `NOT_COMPLETED` for one poll before `COMPLETED`, which exercises remote-status updates.
- `Reset Demo Arrival` resets only the mock arrival cursor; it does not erase processing history.

## 5. Real API adapter design

`RaceApiPayloadAdapter` is the only type that knows the provider's JSON. It
accepts a bare array, a single object, or an array under a wrapper key
(`data`, `results`, `entries`, …), and maps:

| API field | Model |
| --- | --- |
| `player.cartNo` | `CardNumber` — the entry identifier shown everywhere |
| `player.ownerName` / `player.location` | `PrimaryName` / `PrimaryLocation` |
| `secondaryPlayer.ownerName` / `.location` | `SecondaryName` / `SecondaryLocation` |
| `timings` | `TimingSeconds` (a duration, not a date) |
| `player.raceId[].types` | `RaceTypes`, distinct |
| `status` | `ExtractionStatus` + the raw text |
| `videoLink`, `isVideoEnabled` | reference only — never downloaded |
| `marker`, `playerId`, `player.userId` | technical identity |

Defensive by design: `secondaryPlayer: null` is normal, every optional field
tolerates absence, an item with no card number is skipped rather than failing the
whole poll, and repeats within one response collapse to one entry.

## 6. Entry state machine

Remote extraction and local processing are independent state dimensions.

```text
REMOTE:
UNKNOWN → NOT_COMPLETED → COMPLETED (or FAILED)

LOCAL:
VIDEO_NOT_SELECTED
    ↓ select existing local file
READY
    ↓ explicit START PROCESSING
PROCESSING
    ├── validated success → COMPLETED
    └── error            → FAILED → explicit retry

COMPLETED
    └── overlay-affecting remote data changes → OUTDATED → explicit REPROCESS

Any mapped file that disappears → VIDEO_NOT_FOUND
```

There is no transition from `new API entry` directly to `PROCESSING`.

## 7. SQLite schema

SQLite is used as durable local state, not race-data authority.

```sql
CREATE TABLE entry_local_state (
    entry_id TEXT PRIMARY KEY,
    local_video_path TEXT,
    processing_status TEXT NOT NULL,
    output_path TEXT,
    error_message TEXT,
    last_processed_data_hash TEXT,
    last_seen_data_hash TEXT,
    last_seen_utc TEXT,
    processed_utc TEXT,
    updated_utc TEXT NOT NULL
);

CREATE TABLE app_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    json TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE sync_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    last_attempt_utc TEXT,
    last_success_utc TEXT,
    last_error TEXT
);
```

WAL mode is enabled. On restart, the UI can show local history placeholders immediately; the next API poll refreshes remote metadata wherever that entry is returned.

## 8. FFmpeg architecture

### Probe first

`FfprobeService` reads:

- source duration
- width / height
- average frame rate
- video codec
- audio codec
- pixel format

### Scoreboard

A broadcast lower-third, built entirely from `drawbox` and `drawtext`:

```text
        ╔═══════════════════════════════════════════════════════════╗  ← accent rule
        ║ PRIMARY                 ┌──────────┐              SECONDARY║
        ║ S கருப்புசாமி           │   CARD   │                 ரமேஷ்║
        ║ கணியூர்                 │ 1000AAA  │        கோயம்புத்தூர்║
        ╚═══════════════════════════════════════════════════════════╝
```

- The card number sits in a filled accent plate at dead centre. It is the entry
  identifier, so it is the focal point, and a filled plate stays legible over
  moving footage where plain text would not.
- Left and right regions are symmetric about that plate, so the plate does not
  move between entries that do and do not have a secondary player.
- Name over location, separated by size and opacity; a micro label above each.
- A soft drop shadow under the panel and an accent rule along its top edge.
- When `secondaryPlayer` is null the right block and its divider are omitted
  entirely rather than filled with a dash.

Every dimension is a fraction of the probed frame — panel height `0.112·H`,
name `0.040·H`, card `0.052·H`, side margin `0.030·W` — so 720p through 4K are
proportional rather than special-cased.

### Fitting long names

`LayoutTextFitter` reduces the font size step by step until the string fits its
region, and only ellipsizes if it still overflows at the floor. Shrinking keeps a
long Tamil name readable; truncating it loses the name. Tamil is measured with a
wider per-glyph factor than Latin because its clusters are visually wider, so a
Tamil name does not overrun the card plate.

### Closing timing plaque

The API's `timings` (seconds, e.g. `22.5`) is formatted by `TimingFormatter`
into `00:22.50` — never shown as a bare number — and drawn in a centred plaque
with the same accent rule, enabled only for the final N seconds via
`enable='between(t,duration-N,duration)'`.

### Encoding

`EncoderCapabilityService` performs a real tiny NVENC hardware encode test rather than merely checking whether the encoder name is compiled into FFmpeg.

Auto / NVENC mode:

- `h264_nvenc`
- p6/p7 quality-oriented preset
- HQ tune
- constant-quality-oriented VBR/CQ settings
- spatial/temporal AQ
- lookahead

Fallback:

- `libx264`
- CRF 18 (High) / 16 (VeryHigh)
- medium / slow preset

No `scale` or `-r` is inserted, so resolution and source timing/frame-rate behavior are preserved. Audio is mapped optionally and stream-copied (`-c:a copy`) to avoid generational loss.

### Safe output

The service renders to:

```text
Processed/<base>.processing-<guid>.<ext>
```

Then it verifies:

1. FFmpeg exit code == 0
2. file exists
3. file size > 0
4. FFprobe succeeds
5. valid video dimensions exist
6. duration is within tolerance
7. output resolution matches input

Only then is the temporary file moved to `Processed/<original filename>`.

If an existing final output exists, the UI requires explicit confirmation unless the setting explicitly allows overwrite. The prior valid file remains untouched until the replacement validates.

### Real progress

FFmpeg uses `-progress pipe:1 -nostats`. Progress comes from `out_time`, source duration and a stopwatch. ETA is derived from observed progress; no synthetic timer is used.

## 9. UI

Two panels under one top bar, in a standard resizable window with the ordinary
Windows minimise / maximise / close chrome. The window remembers its size and
position, and reopens maximised if it was closed that way.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│ RACE VIDEO PROCESSOR  [DEMO] ● CONNECTED  Last sync 10:30:20   Work Settings Logs [POLL NOW] │
├──────────────────────────┬────────────────────────────────────────────────┤
│ ENTRIES        12 entries│ 1000AAA                                        │
│ [ Search … ]             │ S கருப்புசாமி, கணியூர்            ✓ COMPLETED  │
│ (All)(Ready)(No video)…  │                                                │
│ ┌──────────────────────┐ │ ┌ RACE DETAILS ─────────┐ ┌ RENDER ──────────┐│
│ │ 1000AAA      ✓ DONE  │ │ │ Primary   S கருப்பு…  │ │ Processing 1000…  ││
│ │ S கருப்புசாமி, கணி… │ │ │ Card      1000AAA     │ │ 62%               ││
│ ├──────────────────────┤ │ │ Secondary —           │ │ ▓▓▓▓▓▓▓░░░        ││
│ │ 1000AAB   ⟳ PROCESS. │ │ │ Race type 200 / 300   │ │ 00:08     00:05   ││
│ ├──────────────────────┤ │ │ Timing    00:22.50    │ └───────────────────┘│
│ │ 1000AAC   ○ NO VIDEO │ │ └───────────────────────┘ ┌ OUTPUT ───────────┐│
│ └──────────────────────┘ │ ┌ LOCAL VIDEO ──────────┐ │ Race_001.mp4      ││
│                          │ │ [SELECT][PREVIEW][CLR]│ │ [OPEN][FOLDER]    ││
│                          │ └───────────────────────┘ └───────────────────┘│
│                          │ [ START PROCESSING ] [RETRY] [CANCEL RENDER]   │
├──────────────────────────┴────────────────────────────────────────────────┤
│ ● CONNECTED     Processing 1000AAB     NVIDIA NVENC        Nirmala UI      │
└───────────────────────────────────────────────────────────────────────────┘
```

Rules the layout enforces:

1. **The list is always navigable.** Selection is a plain settable property with
   no guard clause anywhere in its path. An entry with no video, a failed entry
   and a rendering entry are all equally clickable, and each keeps its own state.
2. **A render belongs to the entry it started on**, not to "the selection", so
   the operator can look at other entries while FFmpeg works. Only one render
   runs at a time, and `CANCEL RENDER` stops it without touching the source.
3. **Every unavailable action states its reason** next to the button.
4. **High contrast for outdoor use.** Light ground, near-black text, solid
   borders, 12px minimum type, no meaning carried by transparency. Status is
   carried by a glyph *and* a colour, never colour alone.
5. **Technical detail lives on the Logs page**, so the work screen stays clear.

## 10. Processing workflow

```text
poll provider asynchronously
    ↓
deduplicate by EntryId
    ↓
update visible remote metadata
    ↓
if completed output hash differs → OUTDATED
    ↓
WAIT FOR USER
    ↓
Select / Change Video
    ↓
persist explicit local mapping
    ↓
Preview (two real FFmpeg-generated frames)
    ↓
WAIT FOR USER
    ↓
START PROCESSING
    ↓
FFprobe source → choose NVENC/x264 → render temp output
    ↓
real FFmpeg progress
    ↓
validate temp output with FFprobe
    ↓
move to Processed/<same filename>
    ↓
persist COMPLETED + overlay hash
```

API polling continues while FFmpeg is running. An API outage does not cancel a local render.

## 11. Error / recovery strategy

- **API offline / timeout:** keep last in-memory data, show OFFLINE, preserve last-success time, retry next interval.
- **Unknown real JSON contract:** REAL API mode reports the adapter configuration error; Demo Mode remains fully usable.
- **Duplicate entry:** collapse by stable EntryId.
- **Remote entry changes:** update UI; if overlay-affecting data changed after completion, mark OUTDATED.
- **Mapped file removed:** mark VIDEO NOT FOUND.
- **FFmpeg missing/fails:** capture stderr tail, mark FAILED, preserve original and any prior valid output.
- **NVENC unavailable:** automatically fall back to x264.
- **Output exists:** prompt unless overwrite is explicitly enabled.
- **FFprobe/validation failure:** do not promote temporary output; mark FAILED.
- **Application restart:** restore local mappings/status/output/error state from SQLite, then refresh remote data from provider.
- **UI responsiveness:** HTTP, FFmpeg, FFprobe, SQLite operations are asynchronous; process output is consumed concurrently to avoid pipe deadlocks.

## 12. Tamil text and fonts

Race data is Tamil, and both the application and the rendered video must show it
correctly.

**In the UI**: the font stack is `Nirmala UI, Noto Sans Tamil, Segoe UI, Arial`.
Nirmala UI ships with Windows 8 and later and covers Tamil, so WPF resolves Tamil
glyphs from it and Latin from whichever face comes first.

**In the video**: FFmpeg's `drawtext` can only be relied on with an explicit
`fontfile=` path — `font=` needs fontconfig, which Windows FFmpeg builds
generally lack. `FontResolver` therefore resolves a real file, in order:

1. an explicit path from Settings,
2. `tools\fonts\NotoSansTamil-Regular.ttf` bundled by the installer,
3. a Tamil-capable Windows font (Nirmala UI, then Latha),
4. a Latin-only system font — reported as Tamil-incapable so the UI can warn,
   because Tamil rendered with it produces boxes rather than an error.

If nothing resolves, the render fails with a clear message instead of producing
a video full of empty rectangles. The resolved font and any warning are shown on
the Settings page and above the workspace.

**Escaping**: every string reaches `drawtext` through `textfile=` pointing at a
UTF-8 file without a BOM. No Tamil, comma, colon or quote ever passes through
filter-string escaping, which is the usual source of mangled overlay text.


## 13. Races, categories and the publish workflow

### Race context

`Race { RaceId, RaceName, RaceDate }` is stored in the `races` table (`race_id` UNIQUE, case-insensitive). The selected race and category form a `RaceScope(RaceId, Category)`, and every provider call, stored record and job carries one:

```
Race ── Race ID ──┬── 200 m → GET /v1/races/{raceId}/players/all?type=200
                  └── 300 m → GET /v1/races/{raceId}/players/all?type=300
```

URLs come from templates in Settings (`{raceId}`, `{type}`, `{playerId}`), built by `BackendUrls`. Settings hold no Race IDs; `SelectedRaceId` only remembers which saved race was open.

### Storage (schema v2)

`race_entry_state` is keyed by `(race_id, race_type, entry_id)` and holds cart number, player id, marker, API date, remote video link, original and processed file, `processing_status`, `upload_status`, `uploaded_video_link`, `assignment_status`, the authentication-failure flag, error, hashes and timestamps. It references `races(race_id)`; inserts are conditional on the race existing, so a job finishing after its race was deleted cannot resurrect rows. The v1 `entry_local_state` table cannot be attributed to a race and is left untouched and unread.

`DeleteRaceAsync` deletes the race's entry rows and the race row in one transaction and rolls back on any failure.

### Pipeline

`EntryWorkflowService.RunAsync(job, state)` runs processing → upload → assignment, persisting each stage as it starts and ends and skipping completed stages:

| Stored state | Next run does |
|---|---|
| no validated output | process |
| processed, upload failed / not started | upload the existing processed file |
| uploaded, assignment failed / not started | PATCH with the stored link only |
| upload OFF | stop after processing (`UploadStatus.Disabled`) |

Cancellation during processing → `Cancelled`, no upload. Failures are returned, never thrown. `OverallStatus` is derived from the three stage statuses.

`WorkflowRecovery` runs at startup: a stored *Processing* becomes *Failed*; *Uploading* / *Assigning* go back to *NotStarted* and are resumed automatically, one at a time, when that race and category are open and upload is ON.

### Backend client

`RaceBackendClient` logs in with the configured credentials, caches the bearer token, and on a 401 logs in again and repeats the identical request once (requests are rebuilt from a factory, so upload bodies are re-streamed from the file). Uploads use `ProgressFileContent`, which streams the file in 256 KB chunks and reports bytes written. In Demo mode `DemoMediaPublisher` reads the file for honest progress and returns a `demo.invalid` link without any network access.
