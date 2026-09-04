# Analog Way RC400T — Crestron Control Module

A Crestron SIMPL# Pro module for controlling the video processors an **Analog Way RC400T**
event controller drives: **Aquilon C / Alta 4K** (LivePremier firmware 4.x) and the **Midra 4K**
family (Eikos, Pulse, QuickMatrix, QuickVu, Zenith 100/200).

## Where the RC400T actually fits in

The RC400T is a hardware control surface (56 buttons, a T-bar, a 3-axis joystick) — it has no
third-party network API of its own to "talk to". Instead, it is one client among several
(Analog Way's own **WebRCS**, the Bitfocus **Companion** plugin, and now this module) that all
control the processor over the same protocol: **AWJ** ("Analog Way JSON"). This module speaks
that protocol directly, so a Crestron program using it reaches the same screens, layers, sources,
memories, T-bar and PIP position/size the RC400T's own controls drive — in effect, a
software-defined stand-in for the panel.

## What's in `src/AnalogWay.Rc400t/`

| File | Purpose |
|---|---|
| `AwjDevice.cs` | The module itself — the class to drop into a SIMPL# Pro program. Public methods are the "inputs" (digital/analog/serial joins once compiled into SIMPL Windows), public delegate properties are the "outputs". |
| `AwjWebSocketClient.cs` | Hand-rolled RFC 6455 websocket client (Crestron's SDK has no built-in one) — AWJ's live control channel only exists over `ws://`/`wss://`. |
| `AwjRestClient.cs` | The short REST leg of the handshake: `/auth/status`, `/auth/login`, `/api/stores/device`. |
| `AwjMessage.cs` | Builds/parses the `{"channel":"DEVICE","data":{"path":[...],"value":...}}` envelope AWJ wraps every message in. |
| `AwjPathSet.cs`, `MidraPathSet.cs`, `LivePremier4PathSet.cs` | Per-platform AWJ path tables (screens/layers/memories/etc. are addressed differently on the two firmware families). |
| `CrestronWakeOnLan.cs` | Sends the magic packet used to power a fully-off device back on (AWJ itself only answers once it's already up). |

## Protocol references

This was built and cross-checked against:

- Analog Way's public product/API description for the RC400T and its "Web RCS" software (TCP/IP
  control, JSON-over-websocket protocol, "AWJ programmer's guide").
- The open-source [Bitfocus Companion AWJ module](https://github.com/bitfocus/companion-module-analogway-awj)
  (MIT-licensed), which controls the same LivePremier4/Midra processors. Its `src/connection.ts`,
  `src/state.ts`, and the `src/midra/actions.ts` / `src/livepremier4/actions.ts` action callbacks
  were used to confirm the handshake sequence, the websocket message envelope, and the exact AWJ
  path strings this module sends — no code from that project was copied, only the wire protocol it
  implements.

Analog Way's own AWJ Programmer's Guide PDF (linked from analogway.com) is the authoritative
source and was not reachable from this environment's network sandbox; before relying on this
module for a live show, spot-check the path tables below against that guide or against traffic
captured from an actual RC400T/WebRCS session.

### Handshake

1. `GET http://<host>/auth/status` → `{"authentication":{"isAuthenticationEnabled": bool}, "device": {...}}`
2. If enabled, `POST http://<host>/auth/login` with body `{"password": "..."}`; the response's
   `Set-Cookie` header is the session cookie for everything that follows.
3. `GET http://<host>/api/stores/device` returns the full device state (used here for initial
   model/firmware/serial feedback).
4. Open `ws://<host>/` (send the saved cookie in the upgrade request). The device immediately
   pushes an `{"channel":...,"data":{"channel":"INIT","snapshot":{...}}}` message, then a stream of
   `{"channel":"DEVICE","data":{"path":[...],"value":...}}` / JSON-patch updates for every change —
   from this module, from the RC400T, from WebRCS, or from anyone else connected.

### Command path support matrix

| Capability | Midra path (verified) | LivePremier4 path (verified) |
|---|---|---|
| Take | `device/transition/{screenList\|auxiliaryScreenList}/items/{n}/control/pp/xTake` = `true` | `device/screenAuxGroupList/items/{S\|A}{n}/control/pp/xTakeUp`/`xTakeDown` = `true` |
| Cut | `.../control/pp/xCut` = `true` | same convention, not independently confirmed on LP4 — verify on hardware |
| T-bar position | `.../control/pp/tbarPosition` = `0..65535` | same |
| Transition time | `.../control/pp/takeTime` = raw units | same |
| Layer source | `.../presetList/items/{A\|B}/liveLayerList/items/{layer}/source/pp/input` (+ `NATIVE`/`TOP` special cases) | `.../presetList/items/{A\|B}/layerList/items/{layer}/source/pp/inputNum` |
| Layer position/size | separate `position/pp` (`posH`/`posV`) and `size/pp` (`sizeH`/`sizeV`) nodes | combined `position/pp` node (`posH`/`posV`/`sizeH`/`sizeV`) |
| Screen memory recall | `device/preset/{bank\|auxBank}/control/load/slotList/items/{mem}/{screenList\|auxiliaryScreenList}/items/{n}/presetList/items/{A\|B}/pp/xRequest`, pulsed false→true | `device/presetBank/control/load/slotList/items/{mem}/{screenList\|auxiliaryList}/items/{S\|A}{n}/presetList/items/{A\|B}/pp/xRequest`, pulsed false→true |
| Master memory recall | `device/preset/masterBank/control/load/slotList/items/{mem}/presetList/items/{A\|B}/pp/xRequest`, pulsed false→true | `device/masterPresetBank/control/load/slotList/items/{mem}/presetList/items/{A\|B}/pp/xRequest`, pulsed false→true |
| Commit / "xUpdate" | `device/preset/control/pp/xUpdate`, pulsed false→true after any preset edit | `device/screenAuxGroupList/control/pp/xUpdate`, pulsed false→true |
| Input freeze | `device/inputList/items/{n}/control/pp/freeze` = bool | same |
| Power off / reboot | `device/system/shutdown/standby/control/pp/xRequest` = `"SWITCH_OFF"` / separate `xReboot` pulse | `device/system/shutdown/cmd/pp/xRequest` = `"NONE"` then `"SHUTDOWN"`/`"REBOOT"` |
| Standby | `.../xRequest` = `"STANDBY"` | not modeled — not confirmed for this family |
| Streaming start/stop | `device/streaming/control/pp/start` = bool | Midra-only feature |

Anything not in this table (preset-toggle mode, screen locking, multiviewer memories, sync
selection) is intentionally left out rather than shipped as a guessed path — use
`AwjDevice.SendRawPath(path, jsonValue)` for those once you've confirmed the exact path from the
Programmer's Guide or from a packet capture.

## Building

This is a standard SIMPL# Pro class library — it needs Crestron's SDK to compile and cannot be
built in a general-purpose .NET environment:

1. Open `src/AnalogWay.Rc400t/AnalogWay.Rc400t.csproj` in Visual Studio with the Crestron SIMPL#
   Pro tooling installed (or just restore NuGet packages — it references Crestron's official
   `Crestron.SimplSharp.SDK.ProgramLibrary` package). If your toolchain predates that package,
   replace the `PackageReference` with `<Reference>` entries pointing at `SimplSharp.dll`,
   `SimplSharpPro.dll`, `Crestron.SimplSharp.Newtonsoft.Json.dll` and
   `Crestron.SimplSharp.Cryptography.dll` from your local Crestron SDK install.
2. Build. Crestron's compiler produces the `.dll` you insert into a SIMPL Windows program as a
   "SIMPL# Module" — SIMPL Windows reflects `AwjDevice`'s public members into the join list
   automatically, so there's no separate join map to maintain by hand.
3. Target processor: any 4-series or Virtual Control (VC-4) SIMPL# Pro processor. This targets
   `net472` and uses only Crestron's documented socket/HTTP/crypto/JSON namespaces — no 3rd-party
   NuGet packages beyond Crestron's own SDK.

> **Note on API surface:** the exact method names on Crestron's `CrestronSockets.TCPClient`,
> `CrestronSockets.UDPServer`, `Net.Http.HttpClient` and `Cryptography.SHA1CryptoServiceProvider`
> classes have shifted slightly across SDK releases. This was written against the long-standing,
> widely-documented shape of those APIs but wasn't compiled against the actual SDK in this
> environment (network access to Crestron's developer portal was not available here) — do a first
> build against your installed SDK version and adjust any renamed members before deploying.

## Using it in a SIMPL# Pro program

```csharp
var processor = new AwjDevice();
processor.SetPlatform(1); // 0 = LivePremier4 (Aquilon C / Alta 4K), 1 = Midra
processor.Initialize("192.168.1.50", 80, "");
processor.OnlineFeedback += online => CrestronConsole.PrintLine("AWJ online: {0}", online);
processor.ErrorFeedback += msg => CrestronConsole.PrintLine("AWJ error: {0}", msg);
processor.Connect();

// Later, e.g. from a button press:
processor.SetLayerSource(screenId: 1, isAux: 0, bus: 0 /* A */, layerId: "1", sourceId: 3);
processor.Take(screenId: 1, isAux: 0);
processor.SetTBarPosition(screenId: 1, isAux: 0, position: 32768); // ~50%
processor.RecallScreenMemory(screenId: 1, isAux: 0, bus: 0, memoryId: 5);
```

## Known limitations

- No TLS (`wss://`) support — AWJ processors are normally reached over an isolated AV control
  network without TLS, matching WebRCS's own `http://` default; add a `SecureTCPClient`-based path
  if your deployment terminates TLS on the processor itself.
- No screen-lock or sync-selection handling (multi-client "who owns this screen" bookkeeping that
  WebRCS/Companion implement) — every command is sent unconditionally.
- Preset-toggle mode, multiviewer memories, and per-shadow-memory recall are not modeled; use
  `SendRawPath` for those.
- The websocket receive loop assumes an AWJ frame arrives whole within one TCP read; extremely
  fragmented deliveries (unlikely for AWJ's small delta messages) would need a persistent
  reassembly buffer — see the comment in `AwjWebSocketClient.ReceiveLoop`.
