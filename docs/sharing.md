# Sharing

You can let family watch your library from their own PC. Sharing is off until you turn it on in the **Sharing** page, and
the app must stay running, in the tray at least, for people to connect. It works through MediaBoard on both sides.

## Set it up

1. On the Sharing page, open **Connection**, enter your public address (a name or IP that reaches this PC) and, if you
   want, a home address for people on your network, then save. An invite can't be made without an address.
2. Turn sharing on. It listens on port `47880` over HTTPS, on every network this PC is connected to. The first time,
   Windows asks about the firewall: allow Private networks only. To be reachable from outside your home, forward that
   port on your router.
3. Choose **Add person**, pick the folders they may see, and create an invite. It already carries your addresses, so just
   send it. An invite lasts 24 hours and works once.
4. They run MediaBoard, choose **Connect to a server**, paste the invite and pick a password. A PC without .NET can run
   the zip that `scripts/Publish.ps1` builds: a self-contained win-x64 folder, written to `artifacts\` by default. The script
   refuses to run while the working tree has uncommitted changes.

## What they get

They browse, search and play from your library, and their progress is their own. A device unused for 90 days is signed
out, and signing back in takes the password. The files stay on your PC and are never copied.

Files that a symbolic link or junction brings into a folder aren't shared, and the Sharing page counts them. Replacing your
certificate means everyone needs a new invite. If this PC connects to someone else's server for a while, sharing comes back
on when you switch back to your own library.

The app is not signed, so Windows SmartScreen warns once on first start.
