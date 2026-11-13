# Players

MediaBoard detects MPC-HC, MPC-BE, mpv and VLC, lets you pick a default or browse to any `.exe`, and resumes from where you
stopped.

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

## Following a player you opened yourself

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
