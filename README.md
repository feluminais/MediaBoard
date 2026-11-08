<h1 align="center">MediaBoard</h1>

<p align="center">A Windows app for your own films and series. Your player, and it never touches the files.</p>

<p align="center">
  <a href="#quick-start"><strong>Quick start</strong></a>
  &nbsp; · &nbsp;
  <a href="#players">Players</a>
  &nbsp; · &nbsp;
  <a href="#sharing">Sharing</a>
  &nbsp; · &nbsp;
  <a href="#identification">Identification</a>
  &nbsp; · &nbsp;
  <a href="#development">Development</a>
  &nbsp; · &nbsp;
  <a href="https://github.com/feluminais/MediaBoard/issues">Issues</a>
</p>

MediaBoard finds the movies and series in your folders, fetches descriptions, posters and ratings from TMDB, and remembers
where you stopped. You play in the player you already use (MPC-HC, MPC-BE, mpv, VLC or any program), and it keeps your
progress from that player, even when you opened the player yourself.

Everything runs in one desktop app, with no background service. If you share your library (see [Sharing](#sharing)), the
app has to stay running. Renames, hidden titles and fixed matches live in MediaBoard's own database, so the files on disk
keep their names and your torrent client keeps seeding them.

## Quick start

MediaBoard targets Windows 11 and .NET 10. There is no release yet, so run it from a clone with the .NET 10 SDK:

```bash
git clone https://github.com/feluminais/MediaBoard
cd MediaBoard
dotnet run --project src/MediaBoard.Desktop
```

Then:

1. Open **Settings** and paste a free [TMDB API key](https://www.themoviedb.org/settings/api). Without one everything works
   except identification and online search, and the status bar says so.
2. Open **Folders** and add the folders that hold your media. Removing a folder removes its titles from the library, not
   from disk.
3. Pick a player in **Settings**, or leave the detected one.

Building also keeps a MediaBoard shortcut on your desktop pointed at the latest build. Set `MediaBoardNoShortcut=true` to skip it.

## Players

Detects MPC-HC, MPC-BE, mpv and VLC, lets you pick a default or browse to any `.exe`, and resumes from where you stopped.

| Player | How progress is tracked |
| --- | --- |
| MPC-HC | MediaBoard starts it in slave mode and talks to it through its remote-control messages. No web interface, no port, no change to the player's settings. |
| MPC-BE | The same adapter. Support is unconfirmed, and the app says so if the player never connects. |
| mpv | JSON IPC over a named pipe: live position, duration, pause and end. |
| VLC | Its web interface on localhost, polled for position and length. |
| Any other program | Launched with the file. When it closes, MediaBoard asks whether you finished. |

Players are found in the standard install folders, the registry's uninstall entries and `PATH`. MediaBoard doesn't download
or bundle a player. If none is found, the status bar says so.

Position is saved every few seconds, on pause, at the end of the stream and when the player closes. A file counts as
watched at 90% of its duration. If a player stops early, that doesn't count as finished.

### Following a player you opened yourself

Start mpv, VLC or MPC-HC the way you always do, and MediaBoard notices, attaches and records progress for any library file
you play. With **Follow players I open myself** on (the default), it looks every few seconds for a player it didn't start.
It can only find a player that was set up to listen:

- **mpv:** add this line to `mpv.conf` and restart mpv.

  ```
  input-ipc-server=\\.\pipe\mediaboard-mpv
  ```

- **MPC-HC and MPC-BE:** turn on the web interface under *Options › Player* and enter its port in Settings (default 13579).
- **VLC:** turn on *Web* under *Interface › Main interfaces* (show all settings), set its Lua password, and enter the port
  (default 8080) and password in Settings.

Only files that belong to your library are recorded.

## Identification

Reads the whole path, not only the file name, and handles release noise, fansub names and episode ranges.

1. A `[tmdbid-123]` or `[imdbid-tt123]` tag in a file or folder name, or an `.nfo` file with an ID, is a certain match.
2. Otherwise the path is parsed: release noise (resolution, codec, source, group, language tags) is stripped, then the year,
   `S01E01`, `S01E01-E02`, `1x02` and season folders are read. A bare episode number counts when the folder is one season.
3. TMDB is searched, retrying with shorter titles and without the year when nothing fits.
4. Candidates are scored on title similarity, year, type and popularity, and checked against the episode list, episode titles
   and the file's duration (read with `ffprobe` when it's available).
5. A confident match is accepted. Anything else goes to **Needs review** with candidates and a search box, and your choice is
   remembered for the other episodes of the same show folder.

One file can hold several episodes, and a movie inside a show folder is identified as a movie. Folders with no video, partial
downloads and sample files are ignored, and the Folders view says why.

## Browse and search

- **Continue watching.** Home shows what you started and the next episode of what you finished, plus what was added recently.
- **Search.** `Ctrl+F` opens one box over your library (instant, works offline) and over TMDB, so you can read the description
  and rating of a show you don't have. Type `>` for commands such as *Rescan library*.
- **Episode graph.** A show's seasons as a grid of episodes, coloured by TMDB rating or by how far you got.
- **Display-only edits.** Rename, hide, fix a wrong match, mark watched or unwatched. None of it renames or moves a file.

## Status

A status bar always shows what the app is doing: scanning, identifying, online or offline, key missing, folder unavailable,
player running. An activity panel lists recent events and errors, and each error says what to do.

## Works offline

Browsing, playback and progress need no internet. New files get a title from their name and are identified when the
connection returns.

## Library behaviour

- **Incremental scans.** The library rescans on startup and skips files whose size and modified time are unchanged. A file
  watcher picks up new, changed and removed files while the app is open.
- **Missing files.** If a drive is unplugged or a file moves, the title stays with its progress and edits, and recovers when
  the file returns. A file with the same name and size that turns up elsewhere in a library folder is relinked automatically.
- **Delete from disk** is separate and explicit. It names the files, sends them to the Recycle Bin and warns that a torrent
  client seeding them will lose them.
- **Stays fast.** Virtualised lists and grids, images decoded at display size, and a UI size and poster size you can set.

## Sharing

You can let family watch your library from their own PC. Sharing is off until you turn it on in the **Sharing** page, and
the app must stay running, in the tray at least, for people to connect. It works through MediaBoard on both sides.

1. On the Sharing page, open **Connection**, enter your public address (a name or IP that reaches this PC) and, if you
   want, a home address for people on your network, then save. An invite can't be made without an address.
2. Turn sharing on. It listens on port `47880` over HTTPS, on every network this PC is connected to. The first time,
   Windows asks about the firewall: allow Private networks only. To be reachable from outside your home, forward that
   port on your router.
3. Choose **Add person**, pick the folders they may see, and create an invite. It already carries your addresses, so just
   send it. An invite lasts 24 hours and works once.
4. They unzip MediaBoard (built with `scripts/Publish.ps1`; it needs no .NET installed), choose **Connect to a server**,
   paste the invite and pick a password.

They browse, search and play from your library, and their progress is their own. A device unused for 90 days is signed
out, and signing back in takes the password. The files stay on your PC and are never copied. Files that a symbolic link or
junction brings into a folder aren't shared, and the Sharing page counts them. Replacing your certificate means everyone
needs a new invite. If this PC connects to someone else's server for a while, sharing comes back on when you switch back to
your own library.

> [!NOTE]
> The app is not signed, so Windows SmartScreen warns once on first start.

## Install

There is no installer or release yet, so every version runs from a clone (see [Quick start](#quick-start)).
`scripts/Publish.ps1` builds a self-contained win-x64 folder and zips it, so a PC with no .NET can run it. It refuses to run
while the working tree has uncommitted changes, and writes to `artifacts\` by default.

> [!NOTE]
> English only, for metadata and for the interface. Windows 11 only. There is no Android client, though the code leaves room
> for one.

## Your data

Everything is under `%LOCALAPPDATA%\MediaBoard`: the SQLite library, cached posters and the settings file. Nothing is written
into your media folders. Your TMDB key is stored in the settings file on your PC.

## Development

The solution has three projects under `src/`:

| Project | Covers |
| --- | --- |
| `MediaBoard.Desktop` | The Avalonia app. |
| `MediaBoard.Core` | The UI-free core: scanning, parsing, identification, TMDB, storage, player adapters. |
| `MediaBoard.Server` | The ASP.NET Core server behind [Sharing](#sharing), which the desktop app hosts. |

The app reaches the library through an interface, so a shared library on another PC can stand in for the local one. Each
project has a test project of its own in `tests/`.

```bash
dotnet build
dotnet test --solution MediaBoard.sln -- --parallel none
```

`--parallel none` is needed because the headless Avalonia tests aren't reliable under default parallelism.
`scripts/Coverage.ps1` runs the tests with coverage and writes an HTML report to `coverage-report/`. Warnings are errors, and
the Meziantou analyzers run on every build.

After a fresh clone, run `dotnet tool restore && dotnet husky install` once. The pre-commit hook scans staged changes for
secrets with [gitleaks](https://github.com/gitleaks/gitleaks) when it's installed. Bug reports and feature requests go to
[Issues](https://github.com/feluminais/MediaBoard/issues).

## Credits

This product uses the TMDB API but is not endorsed or certified by TMDB. Metadata and images come from
[The Movie Database](https://www.themoviedb.org).
