# AnyHttpProxy

Use the web apps running on one computer from another computer, on the same ports, without opening
ports, VPNs or firewall rules between them.

```
 host computer                              gateway computer
+-------------------+                      +---------------------+
| :3000  node       |                      | localhost:3000      |
| :5173  vite https | <== Tailcat.Link ==> | localhost:5173      |
| ahp-host          |   no IP, no ports    | ahp-gateway         |
+-------------------+                      +---------------------+
```

- **ahp-host** runs where the services are. It finds every port that answers HTTP or HTTPS and
  shares all of them by default - untick the ones gateways should not see.
- **ahp-gateway** runs where you want to use them. Each shared service listens here on the same
  port; a connection to it is carried byte for byte to the host (TCP tunnel), so HTTPS keeps its
  certificate and WebSockets, SSE and HTTP/2 work.

One host can serve many gateways, and one gateway can connect to many hosts.

## Step by step

1. On the host computer start `ahp-host`. The list fills with HTTP/HTTPS services.
2. Click **Invite gateway** and send the code to the other computer (works once, 15 minutes).
3. On the gateway computer start `ahp-gateway`, paste the code, click **Add host**.
4. The host's services appear under it and listen on the same ports. Click **Open** or use
   `http://localhost:<port>` as you would on the host.

Pairing is permanent: both apps reconnect by themselves after a restart. Remove a gateway on the
host (or a host on the gateway) to end it - coming back then needs a new code.

## When a port is taken on the gateway

If something on the gateway computer already uses the port, the service shows **port busy** with the
process that holds it. Type another local port and press **Apply** (or **Suggest free port**), or
untick the service to turn its reverse proxy off. **Retry busy ports** tries again after you stop the
other program.

## Networks and firewall

By default the gateway listens on all networks of its computer, so other machines in that network can
use the ports too. **Change** next to *listen on* lets you pick networks (subnets) instead; localhost
always listens. When Windows Firewall would block the ports for other machines, a banner offers
**Allow in firewall**, which adds inbound rules (group `AnyHttpProxy`) after an admin prompt.

## Security in short

- The invite code works once and expires after 15 minutes.
- A gateway can reach only the ports the host currently shares - not any other address or port.
- Anyone who can reach a gateway's port can use the service behind it. Pick networks accordingly.
- Traffic is end-to-end encrypted by Tailcat.Link; relays cannot read it.

## Install

Copy **`ahp-host.exe` or `ahp-gateway.exe` together with `msquic.dll`** from the same folder.
Without `msquic.dll` the apps still work, but every connection goes through the relay and is slower.
Windows only (the port scanner and firewall integration use Windows APIs).

## Build

Needs the .NET 10 SDK and Python 3, and the Tailcat.Link sources checked out next to this repository
(`../tailcat-dotnet-lib`) - the library is built from source until its next release.

```bash
dotnet build -c Release AnyHttpProxy.slnx
python build.py                  # single-file exes + msquic.dll -> publish/win-x64
python build.py --only gateway   # one app
```

Architecture notes: [CLAUDE.md](CLAUDE.md).
