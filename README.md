# Snag

A clean, dark Windows app for downloading videos from YouTube, TikTok and Instagram with [yt-dlp](https://github.com/yt-dlp/yt-dlp), with a built-in player.

## Install

1. Download **`Snag-Setup-x.y.z.exe`** from the [latest release](https://github.com/realaxecut/Snag/releases/latest).
2. Run it. It installs Snag to `Program Files`, so Windows asks for permission. Nothing else is needed: the .NET runtime is included.
3. On first launch Snag downloads its tools (yt-dlp, FFmpeg, Deno and the VLC engine, about 230 MB) and checks every one.

The installer isn't code-signed yet, so Windows SmartScreen may say *"Windows protected your PC"* the first time. Click **More info → Run anyway**.

To uninstall, use **Settings → Apps → Installed apps → Snag**. It offers to remove Snag's tools and settings too, and never touches your downloaded videos.

### Updates
Snag checks this repository for a new release each time it starts (**Settings → Tell me when there's a new version**). When one is out, a dot appears on **Settings**. There you can read what's new and click **Download and install**. Snag downloads the installer and checks it against the release's `SHA256SUMS.txt`. Windows then asks for permission, and Snag closes, updates and reopens on its own. Nothing is downloaded until you click.

## What it does

**Download**
- Paste a link, then press **Fetch** (or press `Ctrl+V` anywhere on the Download page). Supported links:
  - **YouTube**: videos, Shorts, playlists, channels and YouTube Music.
  - **TikTok**: videos, and profiles (the newest 200 posts, as a pick-list).
  - **Instagram**: public Reels and posts. Instagram often refuses anonymous downloads; Snag says so when it happens, and trying later usually helps.
- Pick **MP4 video** or **MP3 audio**.
  - MP4: choose from the resolutions that video actually has (up to 4K and 60 fps), each with an estimated file size.
  - Pick a **codec**: Auto, H.264 (plays on everything), VP9, AV1 (smallest files) or HEVC, depending on what that video offers. Resolutions the chosen codec doesn't have are greyed out ("Not in AV1"). Auto takes the highest resolution and prefers H.264 when there's a tie.
  - MP3: Best (VBR), 320, 256, 192 or 128 kbps.
- Optional **options pop-up** (Settings → Downloads, off by default). After you fetch a link, the video, or the playlist's list, opens in a window with every MP4/MP3 option, while the app blurs behind it. Esc, the ✕ or a click outside closes it, and **Download options** on the preview reopens it.
- Playlists show every video with a checkbox: select all, none, or just the one you linked. Each playlist gets its own folder, and files can be numbered (`03 - Title.mp4`).
- Extras: cover art and metadata, English subtitles (MP4), and SponsorBlock to skip sponsor segments.
- A download queue with live speed and time left, plus cancel, retry, play and show-in-folder. You can run 1–4 downloads at once.

**Player**
- Plays your downloads and any video or audio file on your PC: **+** adds files, the folder button adds a whole folder, or drag files onto the player.
- Uses the VLC engine, so it plays every codec Snag can download (H.264, VP9, AV1, HEVC, MP3, AAC, Opus) plus almost anything else, with no Windows extensions needed. Snag installs its own copy of the engine; your installed VLC isn't touched.
  - Decoding runs on the CPU. Hardware decoding crashed when switching videos quickly, so it's off. A 1080p AV1 video uses most of one CPU core; everything else uses much less.
- Files you add from your PC get a picture in the playlist: a frame from halfway through the video, or the song's embedded cover art. FFmpeg makes it once, and Snag keeps it in its thumbnail folder.
- Click the video to pause or play; a play badge shows while it's paused. Songs show their cover art. A spinner appears only if a file takes more than a moment to open.
- When the playlist ends you get a **Play again** screen. A file that can't be played is skipped. If there's nothing left to skip to, the player says why and offers **Remove from playlist** and **Show in folder**.
- Reorder the playlist by dragging, with the ↑/↓ buttons, with `Alt+↑/↓`, or from the right-click menu.
- **Shuffle** and **Repeat**. Repeat cycles through off → whole playlist → this item.
- Real full screen (double-click the video or press `F`). The controls float over the video and fade out, along with the cursor, after 2.5 seconds without mouse movement.
- **Shut down when playlist ends**: once the last item finishes playing, a 60-second countdown appears with a big **Cancel** button (or press `Esc`). Then Windows shuts down normally. Apps with unsaved work can still stop it, because Snag never forces it.
  - The switch is off every time Snag starts.
  - Repeat and this switch turn each other off, since a repeating playlist never ends.

**History**
- Every finished download, with search (by title, file or link), play, add to the playlist, and show in folder.
- **Copy link** copies the video, Short or post's link, even if you've since deleted the file.
- **Delete** removes the video from your computer. It asks first, then moves the file to the **Recycle Bin**, so you can still restore it. It's also removed from the player.

**Themes** (Settings → Appearance)
- **Mode:** Auto (follows Windows), Light or Dark.
- **Colour:** Violet, Crimson, Ocean, Forest, Sunset, Sakura or YouTube (YouTube's red on its near-black or white). Every colour has a light and a dark version, and changes apply instantly.
- **Custom:** pick any colour with the hue slider, a `#RRGGBB` code or the quick colour dots, and choose whether the window and cards take a faint tint of it. **Save preset** keeps it, with its light/dark mode, as a swatch of its own. A selected preset can be edited or deleted.

### Keyboard (Player page)
`Space` play/pause · `←/→` seek 5 s · `↑/↓` volume · `M` mute · `N`/`P` next/previous · `S` shuffle · `R` repeat · `F` full screen · `Esc` leave full screen

## How it stays safe

- **Only YouTube, TikTok and Instagram links are accepted**, and thumbnails load only from those sites' image servers. Each link is rebuilt as a clean `https://` URL and passed to yt-dlp after `--`, as a list of arguments. There's never a command shell, so nothing in a link can be run as a command.
- **yt-dlp runs locked down.** `--ignore-config` and `--no-plugin-dirs` mean no config file or plugin on your PC can change what it does. `--no-remote-components` stops it fetching extra code.
- **Verified tools.** yt-dlp, FFmpeg and Deno come only from their official GitHub releases, and the VLC engine comes from VideoLAN's own download server. Each file is SHA-256-checked against the checksum its publisher posts next to it, and thrown away if it doesn't match. Zip contents are checked so a crafted path can't write outside the tools folder.
- **Sandboxed JavaScript.** YouTube now requires running its player code. That runs in Deno, which by default has no access to your files or network.
- **No leftovers.** Every process Snag starts belongs to a Windows job object, so it's killed when Snag exits, even after a crash.
- **Verified app updates.** Snag only downloads its own updates from this repository's releases, and runs the installer only if its SHA-256 matches the one published with it.
- **No accounts, cookies or telemetry.** Admin rights are needed only to install or update Snag itself. Snag and its tools always run as you.

Only download videos you own or have permission to save, and follow each site's Terms of Service.

## Where things live

| What | Where |
|---|---|
| Snag itself | `C:\Program Files\Snag` |
| Your videos | The **Save to** folder on the Download page (default `Downloads\Snag`) |
| Tools (yt-dlp, FFmpeg, Deno, VLC engine) | `%LOCALAPPDATA%\Snag\tools` (about 230 MB to download, 450 MB installed) |
| Settings, player playlist, history, log | `%LOCALAPPDATA%\Snag` |
| Thumbnail copies for History (TikTok/Instagram links expire), player pictures for your own files | `%LOCALAPPDATA%\Snag\thumbs` |

The tools live in your user folder, not Program Files, so yt-dlp can update itself without admin prompts. YouTube changes often, so that matters. yt-dlp updates at most once a day when Snag starts, and you can turn that off in **Settings**. The tools' **Check for updates** button also updates Deno and the VLC engine.

## Building from source

Needs the .NET 10 SDK and [Inno Setup](https://jrsoftware.org/isinfo.php) 6.7+ (`winget install JRSoftware.InnoSetup`).

```bash
.\build-release.ps1
```

This builds a self-contained app and `dist\Snag-Setup-<version>.exe` plus its `SHA256SUMS.txt`. The version comes from `<Version>` in `Snag\Snag.csproj`.

### Releasing an update
1. Bump `<Version>` in `Snag\Snag.csproj` and commit.
2. Run `.\build-release.ps1 -Publish -Notes "- What changed"`. It needs the GitHub CLI, signed in.

That pushes, then creates release `v<version>` with the installer and checksums attached. Every copy of Snag offers it the next time it starts.

The project is .NET 10 WPF using the built-in Fluent theme. Its only NuGet package is LibVLCSharp, which drives the VLC engine; the engine itself is downloaded on first run rather than bundled. Video frames are drawn straight into a WPF image (`Views\VlcVideoSurface.cs`), so the player's overlays sit on top of the video. `Snag\Assets\make-icon.ps1` regenerates the icon.

Switches for testing:
- `Snag.exe --dry-run-shutdown` runs the shutdown countdown without turning the PC off.
- Setting the `SNAG_DATA_DIR` environment variable runs a second copy with its own settings and history.
- `Snag-Setup-x.y.z.exe /CURRENTUSER /DIR=<folder>` installs without admin rights, for trying out the installer.

Third-party licenses are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
