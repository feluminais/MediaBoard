# Players

CozyCouch detects MPC-HC, MPC-BE, mpv and VLC, lets you pick a default or browse to any `.exe`, and resumes from where you
stopped.

| Player | Started from CozyCouch | Opened by you |
| --- | --- | --- |
| MPC-HC | Live | Live, with its web interface on |
| MPC-BE | Live | Live, with its web interface on |
| mpv | Live | Live, with `input-ipc-server` set |
| VLC | Live | Live, with its web interface and Lua password set |
| Any other program | Asks whether you finished | Not followed |

Live means position is saved while you watch. [Following a player you opened yourself](#following-a-player-you-opened-yourself)
has the setup.

| Player | How progress is tracked |
| --- | --- |
| MPC-HC | CozyCouch starts it in slave mode and talks to it through its remote-control messages. It works with the player's default settings. |
| MPC-BE | The same adapter, since MPC-BE is a fork of MPC-HC. |
| mpv | JSON IPC over a named pipe: live position, duration, pause and end. |
| VLC | Its web interface on localhost, polled for position and length. |
| Any other program | Launched with the file. When it closes, CozyCouch asks whether you finished. |

Players are found in the standard install folders, the registry's uninstall entries and `PATH`. You install the player
yourself, and the status bar tells you if none is found.

Position is saved every few seconds, on pause, at the end of the stream and when the player closes. A file counts as
watched at 90% of its duration. A file stopped before that point stays in progress.

## Following a player you opened yourself

Start mpv, VLC or MPC-HC the way you always do, and CozyCouch notices, attaches and records progress for any library file
you play. With **Follow players I open myself** on (the default), it looks every few seconds for a player started outside CozyCouch.
The player has to be set up to listen:

- **mpv:** add this line to `mpv.conf` and restart mpv.

  ```
  input-ipc-server=\\.\pipe\cozycouch-mpv
  ```

- **MPC-HC and MPC-BE:** turn on the web interface under *Options › Player* and enter its port in Settings (default 13579).
- **VLC:** turn on *Web* under *Interface › Main interfaces* (show all settings), set its Lua password, and enter the port
  (default 8080) and password in Settings.

CozyCouch records the files that belong to your library.
