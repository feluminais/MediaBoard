# Sharing

You can let family watch your library from their own PC. Sharing is off until you turn it on in the **Sharing** page, and
the app must stay running, in the tray at least, for people to connect. It works through CozyCouch on both sides.

## Set it up

1. On the Sharing page, open **Connection**, enter your public address (a name or IP that reaches this PC) and, if you
   want, a home address for people on your network, then save. Every invite carries these addresses.
2. Turn sharing on. It listens on port `47880` over HTTPS, on every network this PC is connected to. The first time,
   Windows asks about the firewall: allow Private networks only. To be reachable from outside your home, forward that
   port on your router.
3. Choose **Add person**, pick the folders they may see, and create an invite. It already carries your addresses, so just
   send it. An invite lasts 24 hours and works once.
4. They run CozyCouch, choose **Connect to a server**, paste the invite and pick a password. Any Windows PC can run
   the zip that `scripts/Publish.ps1` builds: a self-contained win-x64 folder, written to `artifacts\` by default. The script
   needs all changes committed first.

## What they get

They browse, search and play from your library, and their progress is their own. A device unused for 90 days is signed
out, and signing back in takes the password.
Files that a symbolic link or junction brings into a folder are skipped, and the Sharing page counts them. Replacing your
certificate means everyone needs a new invite. If this PC connects to someone else's server for a while, sharing comes back
on when you switch back to your own library.

Windows SmartScreen warns once on first start, because the app is unsigned.
