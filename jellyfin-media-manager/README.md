# Jellyfin Media Manager

Admin-only plugin for Jellyfin **10.11.x** that adds a **Media Manager** page to the dashboard sidebar:

- Drag-and-drop upload into any library folder (8 MB chunks, retries, cancel)
- Browse library folders, create folders, rename, move and delete
- Queues a library scan after an upload batch (optional)

## Install

1. Stop Jellyfin.
2. Create a folder named `Media Manager_1.0.0.0` inside Jellyfin's `plugins` directory and put
   `Jellyfin.Plugin.MediaManager.dll` and `meta.json` in it (both are in `MediaManager_1.0.0.0.zip`).

   | Install type | Plugins directory |
   |---|---|
   | Linux package | `/var/lib/jellyfin/plugins/` |
   | Docker | `/config/plugins/` (inside the container) |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\` |

3. Start Jellyfin. Open **Dashboard → Media Manager** in the sidebar.
   Settings are under **Dashboard → Plugins → Media Manager**.

## Things that commonly go wrong

- **Write permission.** The account Jellyfin runs as must be able to write to the library folders.
  In Docker, a media volume mounted `:ro` will make every upload fail with a permission error.
- **Reverse proxy body limits.** Each chunk is up to 8 MB. nginx defaults to 1 MB, so set
  `client_max_body_size 16m;` (or larger) for the Jellyfin server block.
- **Cloudflare and similar.** Free tiers cap request bodies at 100 MB, which is fine for 8 MB chunks.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Maximum upload size | 0 (no limit) | In MB |
| Allowed extensions | common video, audio, subtitle, image and `.nfo` types | Applies to uploads and renames |
| Scan after upload | on | One scan per batch |
| Allow delete | on | Turn off to make the page upload-only for deletes |
| Debug logging | off | Per-chunk and per-browse detail in the Jellyfin log |

Uploads, renames, moves and deletes are always logged as `[MediaManager] <user> ...`.

## Safety design

- Only users with the Jellyfin administrator role can call the API (`RequiresElevation`).
- Every path is resolved and must sit inside a configured library folder. `..` traversal is refused.
- Paths that pass through symbolic links or junctions are refused and links are hidden from the browser.
- Only allow-listed file extensions can be uploaded or renamed to. Names ending `.mmpart` are reserved.
- Library root folders cannot be renamed, moved or deleted.
- Uploads go to `name.mmpart` and are renamed into place only when the last byte arrives.

## Build from source

Needs the .NET 9 SDK.

```
./build.sh
```

Output: `dist/MediaManager_1.0.0.0.zip`. `tests/ApiCheck` hosts the real controller with a fake library and
exercises 33 upload and file-operation cases (`cd tests/ApiCheck && dotnet run`).

## Known limits (v1)

- Dropping a whole folder is not supported; drop the files inside it, or create the folder first.
- A page reload during an upload discards that upload (the unfinished piece is cleaned up).
- Folders cannot be moved across different drives.
