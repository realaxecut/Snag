# Snag

A clean, dark Windows app for downloading videos from YouTube, TikTok and Instagram with [yt-dlp](https://github.com/yt-dlp/yt-dlp), with a built-in player and a Browser tab for YouTube (ad-free), TikTok and Instagram.

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
- A download queue with live speed and time left, plus cancel, retry, play and show-in-folder. You can run 1–4 downloads at once. Once a download is queued, the page clears, ready for the next link.

**Browser tab**
- **YouTube, TikTok and Instagram** inside Snag, in Snag's light or dark mode. The first time you open the tab after Snag starts, pick one of the three. The switcher at the top changes site any time. It runs on Microsoft Edge's WebView2 engine, which is built into Windows 11 (on a PC without it, the tab offers Microsoft's free installer).
- **Sign in** to each site and you stay signed in. Sign-in pop-ups like "Continue with Google" or "Log in with Facebook" open in a small window of their own. Google sometimes refuses sign-in from browsers built into apps; if it says the browser "may not be secure", there's no way around that from Snag's side.
- **Download** any video: a YouTube video, a TikTok video or photo post, or an Instagram post or reel. Use the **Download** button while it's open, or right-click its link and choose **Download…**. The same download options pop-up as the Download page opens over the tab, with MP4 or MP3, resolution, codec, folder and extras, and the video's sizes once Snag has read it.
- On YouTube:
  - **Ads blocked:** requests to ad servers never leave your PC, the ad data is stripped out of YouTube's player before any ad is scheduled, ad boxes are hidden from the feed and watch pages, and any ad that still slips through is muted and skipped. The shield button turns it off and on and counts what it stopped. Ad and tracker servers are blocked on TikTok and Instagram too.
  - **SponsorBlock:** sponsor reads and self-promotion are skipped as they come up, using the community's [SponsorBlock](https://sponsor.ajay.app) data. It works in Snag's player too, for streamed videos.
  - **Dislikes are back:** the dislike count shows next to the dislike button again, from [Return YouTube Dislike](https://returnyoutubedislike.com).
  - **Play in Snag** streams a video in Snag's player, so nothing is downloaded, and **Queue** adds it to the end of the player's queue. Both are also on the right-click menu.
  - **⋯ → Play clicked YouTube videos in Snag's player** makes a click on a video play it in Snag instead of on the page.
  - Only one thing plays at a time: starting a video on the page pauses Snag's player, and the other way round.
- The search box searches the site you're on, or opens a link you paste. The tab only ever shows these three sites and their sign-in pages; other links open in your normal browser.
- **⋯ → Clear browsing data** signs you out of all three and clears the tab's cookies, history and cache.

**Player**
- Plays your downloads and any video or audio file on your PC: **+** adds files, the folder button adds a whole folder, or drag files onto the player.
- Also plays YouTube videos streamed from the Browser tab, so nothing is downloaded. They show *Streaming from YouTube* in the queue. Snag looks each one up with yt-dlp when its turn comes, which takes a few seconds, and looks up the next one while the current one plays.
  - To keep one, right-click it and choose **Download video (MP4)** or **Download audio (MP3)**, or use the download button on its row. Once it's saved, the file takes the stream's place in the queue.
  - Streams play at up to 1080p. Captions come with downloading.
- Uses the VLC engine, so it plays every codec Snag can download (H.264, VP9, AV1, HEVC, MP3, AAC, Opus) plus almost anything else, with no Windows extensions needed. Snag installs its own copy of the engine; your installed VLC isn't touched.
  - Decoding runs on the CPU. Hardware decoding crashed when switching videos quickly, so it's off. A 1080p AV1 video uses most of one CPU core; everything else uses much less.
- Files you add from your PC get a picture in the playlist: a frame from halfway through the video, or the song's embedded cover art. FFmpeg makes it once, and Snag keeps it in its thumbnail folder.
- Click the video to pause or play; a play badge shows while it's paused. Songs show their cover art. A spinner appears only if a file takes more than a moment to open.
- **Captions (CC button, or `C`)** look like YouTube's: white text on a dark box behind each line, sized to the picture, and they move up out of the way of the full-screen controls.
  - Snag finds captions inside the video (for example from the **English subtitles** download option) and `.srt`/`.vtt` files next to it, such as `Video.en.vtt`. **Add subtitle file…** loads any other file.
  - The CC menu picks the language and has **Size** (Small, Normal, Large, Huge; also `+`/`-`) and **Background** (Box, Solid, or None with a shadow).
  - Snag remembers whether you like captions on, and your size and background.
- When the playlist ends you get a **Play again** screen. A file that can't be played is skipped. If there's nothing left to skip to, the player says why and offers **Remove from playlist** and **Show in folder**.
- Reorder the queue by dragging, with the ↑/↓ buttons, with `Alt+↑/↓`, or from the right-click menu. The save button keeps the whole queue as a playlist.
- **Shuffle** and **Repeat**. Repeat cycles through off → whole playlist → this item.
- Real full screen (double-click the video or press `F`). The controls float over the video and fade out, along with the cursor, after 2.5 seconds without mouse movement.
- **Shut down when playlist ends**: once the last item finishes playing (streamed videos count too), a 60-second countdown appears with a big **Cancel** button (or press `Esc`). Then Windows shuts down normally. Apps with unsaved work can still stop it, because Snag never forces it.
  - The switch is off every time Snag starts.
  - Repeat and this switch turn each other off, since a repeating playlist never ends.

**Playlists**
- Make as many playlists as you like, and name each one (just type over the name).
- Fill a playlist three ways:
  - **Add files** from your PC.
  - **Add from downloads**: tick any number of downloads from your history and add them all at once.
  - Drop files or folders onto it.
- Reorder items with ↑/↓, or remove them. Files that have been moved or deleted are marked.
- **Play** replaces the player's queue with the playlist and starts it, using your shuffle and repeat settings. **Add to player** puts it at the end of the queue instead.
- Playlists are saved in `%LOCALAPPDATA%\Snag\playlists.json`. Deleting a playlist never deletes the files in it.

**History**
- Every finished download, with search (by title, file or link), play, add to the playlist, and show in folder.
- **Copy link** copies the video, Short or post's link, even if you've since deleted the file.
- **Delete** removes the video from your computer. It asks first, then moves the file to the **Recycle Bin**, so you can still restore it. It's also removed from the player.

**Themes** (Settings → Appearance)
- **Mode:** Auto (follows Windows), Light or Dark.
- **Colour:** Violet, Crimson, Ocean, Forest, Sunset, Sakura or YouTube (YouTube's red on its near-black or white). Every colour has a light and a dark version, and changes apply instantly.
- **Custom:** pick any colour with the hue slider, a `#RRGGBB` code or the quick colour dots, and choose whether the window and cards take a faint tint of it. **Save preset** keeps it, with its light/dark mode, as a swatch of its own. A selected preset can be edited or deleted.

### Keyboard (Player page)
`Space` play/pause · `←/→` seek 5 s · `↑/↓` volume · `M` mute · `N`/`P` next/previous · `S` shuffle · `R` repeat · `C` captions · `+`/`-` caption size · `F` full screen · `Esc` leave full screen

## How it stays safe

- **Only YouTube, TikTok and Instagram links are accepted**, and thumbnails load only from those sites' image servers. Each link is rebuilt as a clean `https://` URL and passed to yt-dlp after `--`, as a list of arguments. There's never a command shell, so nothing in a link can be run as a command.
- **yt-dlp runs locked down.** `--ignore-config` and `--no-plugin-dirs` mean no config file or plugin on your PC can change what it does. `--no-remote-components` stops it fetching extra code.
- **Verified tools.** yt-dlp, FFmpeg and Deno come only from their official GitHub releases, and the VLC engine comes from VideoLAN's own download server. Each file is SHA-256-checked against the checksum its publisher posts next to it, and thrown away if it doesn't match. Zip contents are checked so a crafted path can't write outside the tools folder.
- **Sandboxed JavaScript.** YouTube now requires running its player code. That runs in Deno, which by default has no access to your files or network.
- **No leftovers.** Every process Snag starts belongs to a Windows job object, so it's killed when Snag exits, even after a crash.
- **Verified app updates.** Snag only downloads its own updates from this repository's releases, and runs the installer only if its SHA-256 matches the one published with it.
- **A fenced-in Browser tab.**
  - The tab only shows YouTube, TikTok, Instagram and the sign-in pages they use (Google, Facebook, Apple). Any other link opens in your normal browser, and a page can't send itself anywhere else on its own.
  - Pop-ups open only for sign-in, under the same rules. Browser downloads, and requests for your camera, microphone, location or notifications, are all refused.
  - Developer tools, saved passwords and form autofill are off.
  - The page talks to Snag only through a few fixed messages, and Snag re-checks every video link it's handed.
- **Your sign-ins stay in the tab.** They live in the tab's own browser profile. Snag only checks whether a site's sign-in cookie exists, never its value, and yt-dlp never gets it.
- **Little leaves your PC.** For SponsorBlock, Snag sends only the first 4 characters of a hash of the video's ID and finds the video in the answer itself. Return YouTube Dislike is sent the video's ID, as its browser extension does. Both can be turned off in the tab's ⋯ menu.
- **No telemetry.** Admin rights are needed only to install or update Snag itself. Snag and its tools always run as you.

Only download videos you own or have permission to save, and follow each site's Terms of Service.

## Where things live

| What | Where |
|---|---|
| Snag itself | `C:\Program Files\Snag` |
| Your videos | The **Save to** folder on the Download page (default `Downloads\Snag`) |
| Tools (yt-dlp, FFmpeg, Deno, VLC engine) | `%LOCALAPPDATA%\Snag\tools` (about 230 MB to download, 450 MB installed) |
| Settings, player queue, saved playlists, history, log | `%LOCALAPPDATA%\Snag` |
| Thumbnail copies for History (TikTok/Instagram links expire), player pictures for your own files | `%LOCALAPPDATA%\Snag\thumbs` |
| The Browser tab's sign-ins, cookies, history and cache | `%LOCALAPPDATA%\Snag\browser` |

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

The project is .NET 10 WPF using the built-in Fluent theme. It has two NuGet packages:
- **LibVLCSharp** drives the VLC engine. The engine itself is downloaded on first run rather than bundled.
- **Microsoft.Web.WebView2** hosts the Browser tab on the WebView2 engine that comes with Windows.

The Browser tab's page script (ad blocking, SponsorBlock, dislikes, handing videos to Snag) is `Snag\Assets\youtube-tab.js`. Video frames are drawn straight into a WPF image (`Views\VlcVideoSurface.cs`), so the player's overlays sit on top of the video. `Snag\Assets\make-icon.ps1` regenerates the icon.

Switches for testing:
- `Snag.exe --dry-run-shutdown` runs the shutdown countdown without turning the PC off.
- Setting the `SNAG_DATA_DIR` environment variable runs a second copy with its own settings and history.
- `Snag-Setup-x.y.z.exe /CURRENTUSER /DIR=<folder>` installs without admin rights, for trying out the installer.

Third-party licenses are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
