# Buoy for Windows, with homelab sync: MVP

Implementation plan, drafted 2026-10-22. Written so another agent can carry it
out without this conversation. Read `CLAUDE.md` first: the Mac app's rules
describe the behaviour this app copies, and the "critical bug pattern" notes
apply here too.

**Status (2026-10-23):** Phases 0 and 1 are done. `dotnet test` runs the core
tests; `script/rtf-check.ps1` runs the Cocoa fixtures through RichEdit.

## Decisions already made

- **A second, native app.** C# on .NET 10 with WinUI 3 (Windows App SDK). It
  shares no compiled code with the Swift app. Swift runs on Windows, but
  AppKit and SwiftUI don't.
- **Notes sync through a small Buoy server on the homelab.** It's an ASP.NET
  Core service in a Docker container, reached over Tailscale. It syncs
  **note by note**, not by copying the database file. Every device keeps its
  own local database and works offline.
- **Three kinds of client, built in order.** Windows first (this plan), then a
  phone client served by the same server, then a sync client in the Mac app.
  No Mac is available right now, and the Mac app can only be built on one.
- **The local database keeps the Mac schema.** The Windows app reads and writes
  the schema the Mac app is at today (GRDB migrations `v1_initial` …
  `v9_noteSortOrder`), so a Mac database still opens on Windows and the Mac
  sync client can be added later without a data conversion.
- **Windows 11 first.** Mica, rounded windows and the tray behaviour are
  designed for 11. Windows 10 should still run, with a plain background.

## What the MVP is

| In | Out (later phases) |
|----|-------------------|
| Tray icon: left-click toggles, right-click menu | Phone client (PWA), Mac sync client |
| Global hotkey, rebindable in Settings | Harbor Mode and Harbor Timer |
| Borderless floating window, always on top, resizable, Mica backdrop, hidden from the taskbar | Folders UI (synced and preserved, not editable) |
| Editor: bold, italic, underline, strikethrough, bullets, to-dos, nesting, hanging indents | Drag to reorder (notes, pinned, list lines) |
| Note title field | Find in note, Insert/edit link dialog |
| New, delete (with confirm), previous/next, copy note | Fog Mode, confetti |
| All Notes: Pinned + All Notes sections, search, open, pin/unpin, delete | Onboarding, What's New, Bug Report |
| Settings: always on top, launch at login, font size, theme, global hotkey, sync server | Rebindable in-app shortcuts, accent and tint colours |
| Debounced auto-save, restore last note on launch, single instance | Live push updates (MVP polls) |
| **Sync server in Docker behind Tailscale** | Installer, update check, auto-title, rich paste |
| **Windows sync client: offline queue, conflict copies** | |

## Layout

```
dotnet/
  Buoy.sln
  src/Buoy.Core/          net10.0 library, no UI: data, settings, BuoyDoc, RTF writer, sync protocol types
  src/Buoy.Windows/       WinUI 3 app (unpackaged, self-contained)
  src/Buoy.Server/        ASP.NET Core minimal API
  tests/Buoy.Core.Tests/  xUnit
  tests/Buoy.Server.Tests/ xUnit + WebApplicationFactory
  deploy/compose.yaml     server + Tailscale sidecar
  deploy/Dockerfile
  script/run.ps1          build Debug and launch the Windows app
  script/publish.ps1      self-contained Release build of the Windows app, zipped
```

The folder is `dotnet/` rather than `windows/` because the server lives
there too. `Buoy.Core` is shared by the app and the server, and it's split out
so the data, document and sync rules can be tested without WinUI.

Packages: `Microsoft.WindowsAppSDK`, `Microsoft.Data.Sqlite`,
`H.NotifyIcon.WinUI` (tray), `Microsoft.Windows.CsWin32` (`RegisterHotKey`,
window styles), and `System.Security.Cryptography.ProtectedData` (to store the
sync token). Nothing else unless a phase below says so.

---

## Local data contract

### Files

| Data | Windows path | Notes |
|------|--------------|-------|
| Notes | `%USERPROFILE%\.buoy\notes.db` | Same schema as the Mac's `~/.buoy/notes.db`, plus the `sync_` tables. |
| Settings | `%USERPROFILE%\.buoy\settings.json` | Same key names where the meaning is shared; unknown keys are kept on write. |
| Backups | `%USERPROFILE%\.buoy\backups\notes-before-<version>-<time>.db` | Newest 3 kept, same as the Mac. |

Keep SQLite's default rollback journal; don't turn on WAL. Nothing else
should open this file, and that includes Syncthing and Nextcloud. Sync goes
through the server.

### Schema

Run the same nine migrations, with the same SQL and in the same order as
`NoteStore.runMigrations()` in `Buoy/Models/NoteStore.swift`. That includes
the legacy `content TEXT` column from `v1_initial`. Record each migration in
GRDB's own bookkeeping table, so that a Mac sees them as done:

```sql
CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);
INSERT INTO grdb_migrations (identifier) VALUES ('v1_initial');  -- … through v9
```

**A newer database is opened read-only.** If `grdb_migrations` has an
identifier this build doesn't know, don't write to it. Show a toast saying the
notes were saved by a newer version of Buoy.

**Sync state goes in its own tables, never in `notes`.** Create them with
`CREATE TABLE IF NOT EXISTS` and don't record them in `grdb_migrations`:

```sql
sync_notes      (noteID TEXT PRIMARY KEY, baseRev INTEGER NOT NULL, dirty INTEGER NOT NULL, doc TEXT)
sync_folders    (folderID TEXT PRIMARY KEY, baseRev INTEGER NOT NULL)
sync_tombstones (id TEXT PRIMARY KEY, kind TEXT NOT NULL, baseRev INTEGER NOT NULL)
sync_meta       (key TEXT PRIMARY KEY, value TEXT)   -- lastSeq, deviceID, schema version
```

The Mac app ignores tables it doesn't know, so this database still opens on
a Mac. When the Mac app gets its sync client, it adds the same tables as its
`v10_sync` migration (same `IF NOT EXISTS` SQL).

Back up before migrating, under the same rule as the Mac's
`backUpBeforeMigrating`, using `SqliteConnection.BackupDatabase`. The HTML-to-RTF
migration is out of scope.

### Columns the Windows app writes

| Column | Rule |
|--------|------|
| `id` | Milliseconds since epoch, as a string (`Note.newID()`). If two devices pick the same id, the server treats it as a conflict (see Sync). |
| `title` | Typing a title also sets `autoTitleLocked = 1` in the same `UPDATE`, as `persistTitle` does. |
| `contentRTF` | Written by Buoy's own RTF writer (see RTF). An empty blob means an empty note. |
| `createdAt`, `updatedAt` | Milliseconds since epoch, `INTEGER`. |
| `isPinned`, `pinnedOrder` | Pinning sets `pinnedOrder = MAX(pinnedOrder)+1`; unpinning sets it to `NULL`. |
| `sortOrder` | New notes get `MAX(sortOrder)+1`. List order is `sortOrder, createdAt, id`; ties after a sync are fine. |
| `autoTitleStage`, `autoTitleLocked`, `autoTitleDefaultTitle` | New notes are written as the Mac writes them: `0`, `0`, and the `"Note N"` title. |
| `folderID`, `folderOrder`, `folders` | Only sync writes them. The user can't change them on Windows in the MVP; a delete renumbers the folder's `folderOrder` from 0, as the Mac does. |

Every local write that a user makes also sets `sync_notes.dirty = 1`. Deletes
add a row to `sync_tombstones`.

Deleting keeps at least one note, like `deleteNote`.

### Saving

Port the Mac's debounce exactly, including its bug fix:

- Content saves after 1.0s and the title after 0.6s, each on its own timer.
- **Capture the note id when the save is scheduled, never when it runs** (see
  `CLAUDE.md` → State Management).
- Flush pending saves before every change to the current note (switch,
  create, delete), on hide, on exit, and **before a sync pass**.
- A note a sync pass changes while it is open in the editor is reloaded only
  if the editor has no unsaved changes. Otherwise the local edit wins and the
  conflict rule below applies on the next push.

---

## Document format: BuoyDoc

The server and every client exchange note content as **BuoyDoc**, a small JSON
model of what a Buoy note can hold. RTF stays the *local* storage format on
the desktop, because the Mac app and RichEditBox both speak it natively. It
isn't the sync format, because a phone client would then need a full RTF
reader and writer for two dialects, and the server couldn't read content.

```json
{
  "v": 1,
  "blocks": [
    { "type": "paragraph", "runs": [ { "text": "Groceries " }, { "text": "today", "b": true } ] },
    { "type": "bullet", "level": 0, "runs": [ { "text": "eggs" } ] },
    { "type": "todo", "level": 1, "checked": true, "runs": [ { "text": "milk", "s": true } ] },
    { "type": "paragraph", "runs": [ { "text": "recipe", "link": "https://example.com" } ] }
  ]
}
```

- `type`: `paragraph`, `bullet` or `todo`. `level` is 0–2 (list blocks only).
  `checked` applies to to-dos only.
- Run flags: `b`, `i`, `u`, `s` (strikethrough), `link`. Nothing else: fonts,
  colours and spacing are normalised away by every client anyway.
- A reader drops anything it doesn't know and keeps the text. A writer never
  emits an empty run.
- `Buoy.Core.BuoyDoc` owns parsing, validation, plain text (for search) and
  equality. The server validates every incoming document with it.

## RTF

On Windows the conversions are:

- **Load** (`contentRTF` → editor): `Document.SetText(TextSetOptions.FormatRtf, …)`,
  then the load rules below.
- **Save** (editor → `contentRTF` + BuoyDoc): walk the document by paragraph
  and by run (`ITextRange` with `TextRangeUnit.Paragraph` and
  `CharacterFormat`) into a BuoyDoc. Then write `contentRTF` from that BuoyDoc
  with `Buoy.Core.RtfWriter`, and store the BuoyDoc in `sync_notes.doc` for
  the next push. Don't save RichEdit's own `GetText(FormatRtf)` output: the
  writer gives one minimal, testable dialect.
- **Pull** (BuoyDoc from the server → `contentRTF`): `RtfWriter` alone, with no
  UI involved.
- **First upload** of notes that existed before sync was set up: load each one
  into an offscreen RichEditBox once to get its BuoyDoc.

The rules the load path and `RtfWriter` follow:

| Feature | On disk | Rule |
|---------|---------|------|
| To-do | `☐` U+2610 or `☑` U+2611 at the start of a paragraph, **with no space after it** (`☐milk`): the Mac's `rtfContent()` swaps the checkbox and its spacer for the bare marker. Builds before `ce627a4` wrote `☐ milk`. | Readers accept any number of spaces after the marker; the writer writes none. In the editor the marker is a real character followed by one space; clicking it swaps it. Show it in a symbol font. |
| Bullet | `• ` U+2022 at level 0; `◦ ` U+25E6 at levels 1 and 2 | Same characters. Tab and Shift+Tab swap `•` ↔ `◦`. |
| Nesting level | `firstLineHeadIndent / 20pt`, at most 2 | The first line starts at `\li + \fi` twips, so level = `round((\li + \fi) / 400)`. |
| Hanging indent | `headIndent = firstLineHeadIndent + marker width` | The writer emits `\li` = level × 400 and `\fi0`, with no hang: both apps recompute it from the marker's width on load, and only the first-line position carries the level. |
| Bold, italic, underline, strikethrough | `\b`, `\i`, `\ul`, `\strike` | Kept. |
| Links | `{\field{\*\fldinst{HYPERLINK "url"}}{\fldrslt text}}` | Kept and clickable. Editing links is out of the MVP. |
| Text | 7-bit ASCII, everything else as `\uN?` | So the blob is ASCII both ways. |
| Font, colour, spacing | One font table entry, no colour table | Every client normalises these on load. |

---

## Sync

### Server

`Buoy.Server`: an ASP.NET Core minimal API with its own SQLite database at
`/data/buoy.db`, in a Docker volume.

Single user. Every request carries `Authorization: Bearer <token>`, and the
token comes from the `BUOY_TOKEN` environment variable. Tailscale is the
network boundary; the token is the second lock.

Tables: `notes` (every synced field from the local schema, plus `doc` as
BuoyDoc JSON, `rev`, `seq` and `deleted`), `folders` (the same idea), and a
global counter for `seq`. `rev` counts changes to one row; `seq` orders all
changes across the server.

| Endpoint | Does |
|----------|------|
| `GET /v1/health` | Liveness. No token needed; returns nothing private. |
| `GET /v1/changes?since=<seq>` | Every note and folder row, tombstones included, with `seq > since`, oldest first, paged at 500. Returns the new high-water `seq`. |
| `PUT /v1/notes/{id}` | Body: `baseRev` plus the note's fields and `doc`. Accepted if `baseRev` equals the row's current `rev` (0 = create): `rev+1`, a new `seq`, 200. Otherwise 409 with the current row. |
| `DELETE /v1/notes/{id}?baseRev=` | Same check; leaves a tombstone with the text cleared. |
| `PUT` / `DELETE /v1/folders/{id}` | Same as notes. Windows only pulls folders in the MVP, but the server takes them so the Mac client can push its folders later. |

The server validates everything, since this is a trust boundary: id length,
`doc` against BuoyDoc, levels 0–2, body size (cap 2 MB), and integer ranges.
Tombstones are kept forever; they're tiny.

Nightly, the server writes a SQLite backup into `/data/backups`, keeping 7.

### Deployment

`deploy/compose.yaml` runs two containers:

- `buoy-server`, built from `deploy/Dockerfile` on .NET 10's ASP.NET runtime
  image, listening on 8080 inside the compose network only.
- A `tailscale/tailscale` sidecar that joins your tailnet as `buoy` and runs
  `tailscale serve` to `buoy-server:8080`. That gives
  `https://buoy.<tailnet>.ts.net` a real certificate. HTTPS is needed anyway:
  the phone client is a PWA, and service workers require it.

You supply `TS_AUTHKEY` and `BUOY_TOKEN` in an `.env` file next to the
compose file. That file is git-ignored, and the agent never writes those
values.

### Windows client

`SyncService` in the app, logic in `Buoy.Core` so it can be tested against
the real server in-process.

A sync pass:

1. Flush pending saves.
2. **Push.** For each dirty note, `PUT` with its `baseRev` and stored `doc`.
   On 200, store the new `rev` and clear `dirty`. Push tombstones the same
   way.
3. **Pull.** `GET /v1/changes?since=lastSeq` and apply each row:
   - The local row isn't dirty: overwrite it, writing `contentRTF` from the
     `doc` through `RtfWriter`.
   - The local row is dirty, which means a conflict (below).
   - The row is a tombstone and the note is open: switch to the next note
     first, keeping at least one note.
4. Save `lastSeq`.

**Conflicts never lose text.**

- Both sides changed `doc`: the server's version takes the original id, and
  the local text becomes a new note titled `"<title> (conflict)"`.
- Only the title or metadata differs: the newer `updatedAt` wins.
- Edited on one side, deleted on the other: the edit wins and the note comes
  back.

**When it runs:** at launch, when the panel is shown, 3s after a local save
lands, and every 60s while the app runs. Requests have a 10s timeout. While
offline, dirty rows just wait. One pass at a time, ever.

**Settings:** server URL and token, with a "Sync now" button and the last
sync time. The token is encrypted with DPAPI (`ProtectedData`, current user)
in `settings.json`; it's never written in plain text. The footer shows a
small sync-state dot: synced, syncing, offline, or error. Hovering it
explains which.

---

## Phases

Each phase ends in something that can be run and checked. Build and run the
tests at the end of every phase. The Mac app's "no builds" rule is about the
Mac app; this code can be built on this machine.

### Phase 0: RTF and BuoyDoc spike

The riskiest step, done first. There's no Mac to round-trip through, so:

1. Hand-write fixtures in Cocoa's RTF dialect (`\cocoartf`, `\pard … \li \fi`,
   `\uN` escapes, `HYPERLINK` fields) for nested bullets, checked and unchecked
   to-dos, wrapped lines, bold, italic, underline, strikethrough and a link.
   Store them in `tests/Buoy.Core.Tests/Fixtures/cocoa/`.
2. A throwaway WinUI page loads each fixture into a RichEditBox, converts it to
   BuoyDoc and writes it back with `RtfWriter`.

→ **Verify:** every fixture gives the expected BuoyDoc; `RtfWriter` output loads
back into RichEditBox and gives the same BuoyDoc; nesting levels and to-do
states survive both directions.

**Not verifiable yet:** Cocoa reading `RtfWriter` output. It uses only
standard control words, so it should work, but check it on the first Mac
available, before the Mac sync client ships.

**Result:** all five fixtures pass all three trips. RichEdit reads Cocoa's
`\uc0\uN` and `\'xx` escapes, `\`-newline paragraph breaks, hanging indents
(as `LeftIndent` + `FirstLineIndent`, in points) and `HYPERLINK` fields. A link's
instruction text comes back as hidden text, which the reader skips, and its
label carries the target in `ITextRange.Link`, wrapped in quotes. RichEdit
doesn't report link labels as underlined.

### Phase 1: Core library

`BuoyPaths`, `Note`, `Folder`, `Migrations`, `NoteStore`, `AppSettings`,
`SettingsStore`, `BuoyDoc`, `RtfWriter`, `ListRules` (the Enter, Backspace and
Tab outcomes as functions on a line's text and level), and the sync protocol
DTOs.

→ **Verify with tests:**
- A fresh database has the Mac v9 schema plus the `sync_` tables, and the
  nine `grdb_migrations` rows.
- An older database (v8) is backed up first, then migrated, with `sortOrder`
  seeded from `createdAt`.
- A database with an unknown migration opens read-only.
- Create, delete (with folder renumbering and the "keep one note" rule), pin
  and unpin, and previous and next with wrap-around.
- The debounce writes to the note it was scheduled for.
- Settings tolerate missing keys and keep unknown ones.
- BuoyDoc: parse and write round-trips, and validation rejects bad levels,
  unknown types and oversized input.

### Phase 2: Shell

- Single instance (`AppInstance.FindOrRegisterForKey`). A second launch shows
  the first copy's window.
- Borderless `AppWindow` with `OverlappedPresenter` (no title bar or border,
  resizable), Mica backdrop, `IsAlwaysOnTop` from settings, and
  `IsShownInSwitchers = false`. Start at about 320 × 380; centre it on first
  show and keep its position for the session. Drag it by the header.
- Clicking outside doesn't hide it (as on the Mac). The hotkey, tray click and
  Ctrl+W do.
- Tray icon: left-click toggles. The menu has New Note, Sync Now, Settings…,
  About Buoy and Quit Buoy.
- Global hotkey through `RegisterHotKey`, Ctrl+Alt+N by default. If
  registration fails, show a toast that points to Settings.
- Launch at login through the `HKCU\…\CurrentVersion\Run` value.
- Theme: system, light or dark.

→ **Verify:** by hand: the hotkey and tray toggle the window, it stays on top,
it isn't in Alt+Tab, and a second launch brings up the first.

### Phase 3: Editor

`BuoyEditor`, a RichEditBox:
- `DisabledFormattingAccelerators = All`. Buoy owns Ctrl+B, Ctrl+I, Ctrl+U and
  Ctrl+Shift+X; RichEdit's own Ctrl+L, Ctrl+E, Ctrl+R, Ctrl+1/2/5 and
  Ctrl+Shift+L shortcuts must not reach the document.
- In `PreviewKeyDown`: `-` then Space makes a bullet, `[]` then Space makes a
  to-do. Enter continues a list, or on an empty item removes the marker and
  the indent. Backspace right after a marker removes it. Tab and Shift+Tab
  change the level (0–2). All of these follow `ListRules`, and each is one
  undo step (`BeginUndoGroup` / `EndUndoGroup`).
- Include both Mac guards from `CLAUDE.md`'s nested-list note.
- Hanging indents are reapplied to every edited list paragraph, from one
  place.
- Clicking a to-do glyph toggles it.
- Paste is plain text. Lines starting with `- `, `* ` or `• ` become bullets.
- Save through the BuoyDoc → `RtfWriter` path and `NoteStore`'s debounce.
- Toolbar: Bold, Italic, Underline, Strikethrough, Bullet List, To-Do, each
  with an accessible name.

→ **Verify:** `ListRules` tests for every Enter, Backspace and Tab case. By
hand: every list case, plus undo, plus reopening the app shows the same note.

### Phase 4: Notes UI and settings

- Header: title field (0.6s debounce), All Notes button, New Note button.
- Footer: Copy Note, the sync-state dot, and a Settings gear that opens a
  flyout.
- A confirmation before deleting.
- All Notes overlay: search titles and plain text. Cache the plain text per
  `id` + `updatedAt` (the Mac's `NotePlainText` rule); BuoyDoc gives the plain
  text without decoding RTF. Pinned and All Notes sections, with headers only
  when both have notes. Pin/unpin and delete on each row and in the
  right-click menu. ↑/↓ and Enter work from the search box; Escape clears the
  search, then closes the overlay.
- Fixed shortcuts:

  | Command | Windows | Mac |
  |---------|---------|-----|
  | New Note | Ctrl+N | ⌘N |
  | Delete Note | Ctrl+D | ⌘⌫ |
  | Copy Note | Ctrl+Shift+C | ⌘↩ |
  | Previous / Next Note | Ctrl+PgUp / Ctrl+PgDn | ⌘← / ⌘→ |
  | All Notes | Ctrl+L | ⌘L |
  | Settings | Ctrl+, | ⌘, |
  | Hide Buoy | Ctrl+W | ⌘W |

  Ctrl+←/→ and Ctrl+Backspace already move and delete by word in every
  Windows text box, so they can't be reused.
- Settings flyout: always on top, launch at login, font size, theme, the
  global hotkey recorder, and the sync section (left disabled until Phase 6).
- Restore `lastSelectedNoteID` on launch.

→ **Verify:** by hand, through every editing and notes row of the "In"
column.

### Phase 5: Sync server

`Buoy.Server`, `deploy/Dockerfile`, `deploy/compose.yaml`, and a
`deploy/README.md` covering setup: enable HTTPS certificates in the tailnet,
create an auth key, fill in `.env`, run `docker compose up -d`.

→ **Verify with tests:** create, update and delete, `baseRev` conflicts (409
with the current row), paging `/changes`, tombstones, auth (missing or wrong
token gives 401), and validation rejects. Then by hand: deploy on the Docker
host and `curl https://buoy.<tailnet>.ts.net/v1/health` from another
device on the tailnet.

### Phase 6: Windows sync client

`SyncService` and the Settings sync section.

→ **Verify with tests** (in-process server, two simulated clients):
- An edit on A shows on B.
- Edits to different notes on A and B both survive.
- The same note edited on both gives one original plus one "(conflict)" note,
  and no text is lost.
- Edit beats delete.
- Offline edits push once the server is back.
- Two clients creating the same id end up with two notes.
- The first sync uploads every pre-existing local note.

Then by hand: two Windows machines, or two Windows user accounts on one PC,
both pointed at the homelab.

### Phase 7: Ship the Windows app

- `script/publish.ps1`: self-contained x64 and arm64 Release builds, zipped.
- `dotnet/README.md`: running the app, setting up sync, and bringing an
  existing Mac `notes.db` over (copy it in once, with the app closed; the
  first sync uploads it).
- A short "Windows app and sync" section in `CLAUDE.md`, pointing here.

→ **Verify:** the zip runs on a Windows 11 machine with no .NET installed, and
syncs.

---

## After the MVP

In order, each with its own plan when it starts:

1. **Phone client.** A PWA served by `Buoy.Server` at `/app`, using the same
   API and token. The editor is a web rich-text editor whose schema matches
   BuoyDoc's three block types and five run flags. It installs to the home
   screen from the `ts.net` address on iOS and Android.
2. **Mac sync client.** Needs a Mac. Add the `sync_` tables as `v10_sync`, map
   `NSAttributedString` ↔ BuoyDoc, and add the same `SyncService` logic in
   Swift. Check Cocoa reading `RtfWriter` output before it ships (Phase 0's
   open item).
3. **Live updates.** Server-sent events from `/v1/events`, so a change shows on
   other devices in about a second instead of on the next poll.
4. Windows feature parity: Harbor Mode, folders UI, reordering, find, links.

## Known limits of the MVP

- **Polling, not live.** Another device's change appears on the next pass:
  when the panel opens, or within 60s.
- **Conflicts are copies, not merges.** Editing the same note on two offline
  devices gives two notes to reconcile by hand.
- **Settings are per device.** They don't sync.
- **Tombstones grow forever.** That's harmless at sticky-note scale; prune
  them if it ever matters.
