# CLAUDE.md

Guidance for working in this repository.

## What this is

AnyHttpProxy exposes the HTTP/HTTPS services of one computer (**host**) on other computers
(**gateways**), on the same ports, over **Tailcat.Link** (no IP, no open port). Sister project of
AgentVirtualHand (same author, same look, same link library).

One solution (`AnyHttpProxy.slnx`), .NET 10, Avalonia 12, Tailcat.Link **0.5.2** from NuGet.

What the app relies on from that version, so nothing is worked around here:

- relay liveness: a relay connection that silently stops carrying bytes is replaced in ~3 s;
- resend: what a dead relay connection swallowed goes out again on the new one;
- faster return to a direct path, relay notices in `ITailcatObserver`;
- `msquic.dll` copied beside a single-file exe on publish (the package's `buildTransitive` targets).

Measured against a server whose router cuts every TCP/UDP flow a few hundred KB in (relay reconnect
every ~4-8 s): typically 60-100 requests per 90 s through a tunnel with no failures and no session
drops; that network varies a lot, so compare builds with several alternating runs. Before these
library changes the session dropped every ~25 s.

Link diagnostics: `AHP_LINK_LOG=<file>` writes the library log and relay/path events (`LinkTrace`);
`AHP_FORCE_RELAY1=1` (with the log on) forces the relay1 transport to reproduce a QUIC-less host on
one machine. A link that flaps "the other machine sent nothing for 00:00:10" with `peer ... GONE` in
that log is the other machine dropping off the relay, not this end.

Projects:

- `src/AnyHttpProxy.Core` - everything that is not UI: protocol, tunnel, scanner, both engines.
- `src/AnyHttpProxy.Host` -> **`ahp-host`** (window only).
- `src/AnyHttpProxy.Gateway` -> **`ahp-gateway`** (window only).
- `src/Shared` - linked into both apps, **edit the original**: `UI/*.cs` (ViewModelBase, LogList,
  RowSync, CrashLog, StateBrushes) and `Styles/*.axaml` (Theme, Colors).

## QUIC in single-file builds

A single-file publish would drop `msquic.dll`, leaving QUIC unsupported and every session on the
relay. The Tailcat.Link package copies it beside the exe on publish, and the library logs a warning
when a Windows that has QUIC reports none. **Ship `msquic.dll` together with `ahp-host.exe` /
`ahp-gateway.exe`.**

## Build and release

```bash
dotnet build AnyHttpProxy.slnx
python build.py                     # standalone single-file exes -> publish/win-x64
python build.py --only host --clean
```

Stop running copies first (the exe locks itself): `Get-Process ahp-host,ahp-gateway | Stop-Process -Force`.

## How the pieces fit

- **Engines vs UI.** `HostEngine` and `GatewayEngine` own all logic and raise `Audit(kind, text)` and
  `Changed`. View models only mirror them (`RowSync` updates rows in place, a 1 s timer refreshes).
- **Catalog.** Gateway -> host `RequestAsync` with `CatalogRequest(since)`; the host long-polls up to
  `Wire.CatalogHold` (20 s, a link round-trip must return in ~30 s) and answers with the shared
  services. Version = random per-process epoch + counter, so a restarted host never matches an old one.
- **Tunnel = two one-way channels per TCP connection** (`TcpTunnel`). Gateway accepts a socket, opens
  `ahp.tcp.up` with `TunnelOpen{id, port}`; the host checks the port is *currently shared*, connects to
  it and opens `ahp.tcp.down` back with `TunnelReply{id, ok}`; the gateway pairs them by id
  (`HostConnection._pending`). A channel handler must **stay inside** until the tunnel ends - the
  channel dies when the handler returns - so handlers own reader+enumerator and await the tunnel.
- **Frame byte.** Every data frame starts with `0`; `1` means "aborted". A channel ends the same way
  on close and on failure, so without it a cut response would look complete. End of channel without
  abort = half-close (FIN); abort or session end = RST on the socket.
- **Scanner.** `ListenerTable` (GetExtendedTcpTable with PIDs on Windows) -> group by port -> connect to
  loopback when possible -> `HttpProbe` (TLS first, then plain `HEAD /`). Results cached per
  (port, pid, address); negatives re-probed after 30 s. Own process and `ahp-gateway` are skipped
  (a gateway's ports re-shared would loop).
- **Host sharing default = everything.** `host.json` stores only `hiddenPorts`.
- **Gateway mappings** (`gateway.json`): per host per remote port - local port (default same),
  enabled, `offered` (last catalog). Mappings survive a service disappearing, so a changed port is
  kept. Listeners start from the saved catalog before the link is up.
- **Conflicts.** `PortForwarder.TryStart` checks `ListenerTable` for a foreign owner first (Windows
  lets a wildcard bind sit on top of someone's specific bind) and binds with `ExclusiveAddressUse`.
  A reported conflict is **not** retried on every catalog - only after a port change or "Retry busy
  ports", otherwise the log would repeat every 20 s. Many conflicts at once log as one line.
- **Bind addresses.** All networks = `0.0.0.0` + `[::]`; chosen networks = `127.0.0.1` + chosen IPv4 +
  `::1` + chosen IPv6. The first address is required, extra ones are best effort.
- **Firewall** (`WindowsFirewall`, Windows only): read via COM `HNetCfg.FwPolicy2` with `dynamic`
  (needs `BuiltInComInteropSupport`), approximate evaluation (block rule wins, else allow rule, else
  profile default). Fix = elevated PowerShell (`-EncodedCommand`, avoids quoting) that adds rules
  `AnyHttpProxy gateway TCP <port>` in group `AnyHttpProxy` and removes inbound block rules for the exe.
- **Multi-host gateway.** Each host has its own Tailcat store folder `links/<id>` (own identity), like
  the AVH hub. Host side uses `HostManyAsync` (`MaxPeers` 32).
- **Stale invitations** survive a host restart in the Tailcat store; `HostEngine.StartAsync` revokes
  them because the window cannot show them.

## Conventions

- User-facing text is English; code comments are Polish (like AVH).
- `Wire.AppName = "anyhttpproxy"` is the pairing identity - changing it breaks every pairing.
- Data: `%APPDATA%\ahp-host` (`host.json`, `link/`), `%APPDATA%\ahp-gateway` (`gateway.json`,
  `links/<id>/`). Crash log: `ahp-error.log` next to the exe.
- No `LinkOptions` tuning: heartbeat and timeouts are the library defaults.

## Testing

No test project. Checks are throwaway console apps in the scratchpad that reference
`AnyHttpProxy.Core` and assert with a small `Check` helper. The end-to-end one that has been used:
a child process runs Kestrel on HTTP and HTTPS (self-signed) ports, `HostEngine` + `GatewayEngine`
pair in one process over the real relay, the gateway hits a conflict (same machine), remaps to other
ports, then 30 MB download/upload with SHA-256, streaming, 40 parallel requests, hide/re-share. The
scanner skips its own PID, so the test service must be a separate process. Relay throughput seen:
~2.5 MB/s.

GUI can be driven with UI Automation (`InvokePattern` on buttons, `ValuePattern` on text boxes); the
invite code is exposed via `AutomationProperties.Name`. Do not use the clipboard for this - the user
may be working on the machine.

## Gotchas hit before

- A minimal-API lambda that takes only `HttpContext` and returns `Task<string>` binds as a
  `RequestDelegate` and silently drops the string.
- `IClipboard.SetTextAsync` is an extension in `Avalonia.Input.Platform`.
- `TextBox.Watermark` is obsolete in Avalonia 12 - use `PlaceholderText`.
- A double click (or a UIA invoke) could run "Add host" twice; `AddAsync` guards with `_adding`.
