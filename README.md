# Analog Way RC400T — Crestron Control Module

A Crestron control module for the video processors an **Analog Way RC400T** event controller
drives: **Aquilon C / Alta 4K** (LivePremier firmware 4.x) and the **Midra 4K** family (Eikos,
Pulse, QuickMatrix, QuickVu, Zenith 100/200). The core logic is a SIMPL# class library
(`src/AnalogWay.Rc400t/`), built via `AnalogWay.Rc400t.sln`; a SIMPL+ wrapper
(`simplplus/AnalogWayRc400t.usp`) brings it into SIMPL Windows as an insertable symbol, and the
same class can be used directly from a SIMPL# Pro program if you're not using SIMPL Windows at all.

## Where the RC400T actually fits in

The RC400T is a hardware control surface (56 buttons, a T-bar, a 3-axis joystick) — it has no
third-party network API of its own to "talk to". Instead, it is one client among several
(Analog Way's own **WebRCS**, the Bitfocus **Companion** plugin, and now this module) that all
control the processor over the same protocol: **AWJ** ("Analog Way JSON"). This module speaks
that protocol directly, so a Crestron program using it reaches the same screens, layers, sources,
memories, T-bar and PIP position/size the RC400T's own controls drive — in effect, a
software-defined stand-in for the panel.

## What's in this repo

| File | Purpose |
|---|---|
| `AnalogWay.Rc400t.sln` | Visual Studio solution — open this to build. |
| `src/AnalogWay.Rc400t/AwjDevice.cs` | The module itself. Public methods are the "inputs", public delegate properties are the "outputs" — call/subscribe to them directly from a SIMPL# Pro program, or through the SIMPL+ wrapper below. |
| `src/AnalogWay.Rc400t/AwjWebSocketClient.cs` | Thin wrapper around Crestron's own `Crestron.SimplSharp.CrestronWebSocketClient.WebSocketClient` — AWJ's live control channel only exists over `ws://`/`wss://`. |
| `src/AnalogWay.Rc400t/AwjRestClient.cs` | The short REST leg of the handshake: `/auth/status`, `/auth/login`, `/api/stores/device`. |
| `src/AnalogWay.Rc400t/AwjMessage.cs` | Builds/parses the `{"channel":"DEVICE","data":{"path":[...],"value":...}}` envelope AWJ wraps every message in. |
| `src/AnalogWay.Rc400t/AwjPathSet.cs`, `MidraPathSet.cs`, `LivePremier4PathSet.cs` | Per-platform AWJ path tables (screens/layers/memories/etc. are addressed differently on the two firmware families). |
| `src/AnalogWay.Rc400t/CrestronWakeOnLan.cs` | Sends the magic packet used to power a fully-off device back on (AWJ itself only answers once it's already up). |
| `simplplus/AnalogWayRc400t.usp` | The SIMPL+ wrapper — links `AwjDevice` into a SIMPL Windows-insertable symbol. See "Using it from SIMPL Windows" below. |

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

### Verified against the real SDK

The C#/Crestron side of this module isn't guesswork either: `Crestron.SimplSharp.SDK.Library`
2.21.252 (the exact NuGet package `AnalogWay.Rc400t.csproj` references) was downloaded directly
from nuget.org and disassembled with `monodis` to confirm every Crestron class, method and enum
this project calls actually exists with the signature used — `CrestronSockets.TCPClient`/
`UDPServer`, `CrestronWebSocketClient.WebSocketClient`, `Net.Http.HttpClient`/`HttpClientRequest`/
`HttpClientResponse`/`UrlParser`/`HttpHeaders`, `Cryptography.SHA1CryptoServiceProvider`, and
`ErrorLog`. That check caught and fixed three real bugs from the first pass: the JSON classes ship
under the plain `Newtonsoft.Json`/`Newtonsoft.Json.Linq` namespaces (not
`Crestron.SimplSharp.Newtonsoft.Json` as first written), `Crestron.SimplSharp.CrestronThread.Thread`
doesn't exist in this package at all (replaced with plain `System.Threading.Thread`), and
`TCPClient` has no `ReceiveData(byte[], int)` overload — which stopped mattering once
`AwjWebSocketClient` was rewritten to wrap Crestron's own native `WebSocketClient` class instead of
a hand-rolled RFC 6455 implementation, dropping the custom framing/handshake code entirely.

Every file was then compiled with the Mono C# compiler (`mcs`) directly against those real
downloaded assemblies. `AwjPlatform.cs`, `AwjPathSet.cs`, `MidraPathSet.cs`,
`LivePremier4PathSet.cs`, `CrestronWakeOnLan.cs` and `AwjWebSocketClient.cs` compile with zero
errors this way. `AwjMessage.cs`, `AwjRestClient.cs` and `AwjDevice.cs` hit one remaining error in
this sandbox: `Newtonsoft.Json.Linq.JObject`/`JToken` need a `System, Version=3.5.0.0,
Retargetable=Yes` facade that this Linux mono install doesn't carry (confirmed via a minimal
repro — the same code compiles clean once that one type is out of the picture, and the interfaces
it's missing demonstrably exist in mono's own `System.dll`, just under a different assembly
identity). That's a mono/Linux-only facade-resolution gap, not a code defect — real Visual Studio
on Windows with the full .NET Framework 4.7.2 resolves retargetable facades automatically. Still,
since this wasn't compiled end-to-end with the actual Crestron/Visual Studio toolchain, treat the
first real build as the final check.

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

`AwjDevice` and its helper classes use only sockets, HTTP, crypto and JSON — none of
`SimplSharpPro.dll`'s hardware I/O surface — so the project builds as a plain **SIMPL#** library
(not "SIMPL# Pro"). That's deliberate: plain SIMPL# libraries are the kind the SIMPL+
cross-compiler can link against, which is what makes the SIMPL+ wrapper below possible, and it
also runs fine standalone from a SIMPL# Pro program.

This needs Crestron's SDK to compile and cannot be built in a general-purpose .NET environment:

1. Open `AnalogWay.Rc400t.sln` in Visual Studio with the Crestron SIMPL# tooling installed and
   restore NuGet packages (it references Crestron's official `Crestron.SimplSharp.SDK.Library`
   package, which transitively pulls in every assembly it ships — see the comment block in
   `AnalogWay.Rc400t.csproj` for the full list). If your toolchain predates that package, replace
   the `PackageReference` with direct `<Reference Include="..." HintPath="..."/>` entries for those
   same DLLs from your local Crestron SIMPL# SDK install directory.
2. Build. This produces `AnalogWay.Rc400t.dll` — the assembly name the `.usp` wrapper references
   via `#USER_SIMPLSHARP_LIBRARY "AnalogWay.Rc400t"`.
3. Targets `net472` (`Crestron.SimplSharp.SDK.Library` also ships a `net6.0` build for VC-4/newer
   4-series firmware — retarget if you need that instead) and uses only Crestron's own
   socket/HTTP/crypto/JSON/websocket namespaces, all confirmed against the real package (see
   "Verified against the real SDK" above) — no 3rd-party NuGet packages beyond Crestron's own SDK.

> **Residual caveat:** this was compiled with `mcs` against the real, downloaded Crestron
> assemblies (see above) and only one sandbox-specific, non-code issue turned up — a missing mono
> facade for `Newtonsoft.Json`'s `JObject`/`JToken` types that real Visual Studio/.NET Framework
> resolves automatically. The SIMPL+ syntax in `AnalogWayRc400t.usp` follows the long-documented
> `#USER_SIMPLSHARP_LIBRARY` / `CALLBACK FUNCTION` interop pattern but hasn't been run through the
> actual SIMPL+ cross-compiler (not available outside Windows) — treat the first SIMPL+ compile as
> the remaining real check.

## Using it from SIMPL Windows (SIMPL+ wrapper)

1. Build `AnalogWay.Rc400t.dll` as above.
2. Open `simplplus/AnalogWayRc400t.usp` in the SIMPL+ IDE. Add the DLL as a referenced assembly
   (SIMPL+ IDE: the compiler-options / referenced-assemblies dialog — exact menu wording varies by
   SIMPL+ version) so the name in `#USER_SIMPLSHARP_LIBRARY "AnalogWay.Rc400t"` resolves.
3. Compile the `.usp` to a `.usp`-compiled module and insert it into your SIMPL Windows program
   like any other module.
4. Wire the joins per the table below. Every screen/aux/layer/bus/memory-scoped command uses a
   **"load the parameters, then pulse the matching `Execute_*` input"** pattern instead of one join
   per screen — set the `Param_*` signals for the object you want to address, then pulse the one
   `Execute_*` digital input for the action. This keeps the join list fixed size regardless of how
   many screens or memories the processor has (Midra alone supports up to 200 screen memories and
   8 layers per screen).

### Join map

| Signal | Type | Meaning |
|---|---|---|
| `Execute_Connect` / `Execute_Disconnect` | Digital in | Calls `Initialize(IPAddress, Port, Password)` + `Connect()`, or `Disconnect()`. |
| `Set_Platform_LivePremier4` / `Set_Platform_Midra` | Digital in | Pulse one before `Execute_Connect` to pick the processor family. Defaults to Midra at power-up. |
| `IPAddress`, `Password` | Serial in | Device address and admin password (leave `Password` empty if authentication is disabled). |
| `Port` | Analog in | TCP port; 0 = default (80). |
| `Online_FB` | Digital out | High once the websocket handshake completes. |
| `Error_FB` | Serial out | Last error/log message. |
| `DeviceModel_FB` / `FirmwareVersion_FB` / `SerialNumber_FB` | Serial out | From the initial AWJ snapshot. |
| `Param_ScreenId` | Analog in | Screen/aux-screen number for the next `Execute_*`. |
| `Param_IsAux` | Digital in | 0 = screen, 1 = aux-screen. |
| `Param_Bus` | Digital in | 0 = preset A, 1 = preset B. |
| `Param_LayerId` | Serial in | `"1"`.."8", or Midra's `"NATIVE"`/`"TOP"`. |
| `Param_SourceId`, `Param_MemoryId`, `Param_InputId` | Analog in | Source/memory/input numbers. |
| `Param_Width`, `Param_Height` | Analog in | Layer size in pixels. |
| `Param_PosX`, `Param_PosY` | Analog in | Layer position in pixels — raw join value, reinterpreted as signed 16-bit before sending (negative positions are valid). |
| `Param_TBarPosition` | Analog in | 0–65535 across full T-bar travel. |
| `Param_TransitionTime` | Analog in | Raw device transition-time units — see the path matrix below. |
| `Execute_Take` / `Execute_TakeToBus` / `Execute_Cut` | Digital in | Transition actions using `Param_ScreenId`/`Param_IsAux`(/`Param_Bus`). |
| `Execute_SetTBarPosition` / `Execute_SetTransitionTime` | Digital in | Apply `Param_TBarPosition` / `Param_TransitionTime`. |
| `Execute_SetLayerSource` | Digital in | Sets `Param_LayerId`'s source to `Param_SourceId` on `Param_Bus`, then commits. |
| `Execute_SetLayerPosition` / `Execute_SetLayerSize` | Digital in | Apply `Param_PosX/Y` or `Param_Width/Height` to `Param_LayerId`. |
| `Execute_RecallScreenMemory` / `Execute_RecallMasterMemory` | Digital in | Recall `Param_MemoryId` on `Param_Bus` (screen memory also uses `Param_ScreenId`/`Param_IsAux`). |
| `Execute_FreezeOn` / `Execute_FreezeOff` | Digital in | Freeze/unfreeze `Param_InputId`. |
| `Execute_StartStreaming` / `Execute_StopStreaming` | Digital in | Midra only. |
| `Execute_PowerOff` / `Execute_Reboot` / `Execute_Standby` | Digital in | See the power row in the path matrix — `Standby` is Midra-only. |
| `MacAddress` + `Execute_WakeOnLan` | Serial in + Digital in | Sends a Wake-on-LAN magic packet. |
| `Param_RawPath`, `Param_RawValue` + `Execute_SendRawPath` | Serial in + Digital in | Escape hatch — see `AwjDevice.SendRawPath`. |
| `Param_QueryPath` + `Execute_QueryState` → `QueryResult_FB` | Serial in + Digital in → Serial out | One-shot read of `AwjDevice.GetStateValue`. |
| `StateChanged_Event` (pulses) with `StateChanged_Path_FB` / `StateChanged_Value_FB` | Digital out + Serial out | Fires for every AWJ path/value update, from this module or any other client (including the RC400T panel). |

## Using it directly from a SIMPL# Pro program

If you're not using SIMPL Windows/SIMPL+ at all, call `AwjDevice` straight from C#:

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

- `wss://` (TLS) is wired for but untested — `AwjWebSocketClient` sets `SSL = false` on Crestron's
  `WebSocketClient` unconditionally; flip that (and thread a "use TLS" flag down from `AwjDevice`)
  if your deployment terminates TLS on the processor. The REST leg of the handshake would also need
  to move to `Crestron.SimplSharp.Net.Https.HttpsClient`.
- The AWJ session cookie is sent to the websocket via `WebSocketClient.AddOnHeader` (one extra raw
  header line on the upgrade request) — this wasn't independently confirmed against Crestron's own
  docs; if a password-protected device doesn't authenticate the websocket, check that header lands
  correctly (a packet capture on first connect will show it either way).
- No screen-lock or sync-selection handling (multi-client "who owns this screen" bookkeeping that
  WebRCS/Companion implement) — every command is sent unconditionally.
- Preset-toggle mode, multiviewer memories, and per-shadow-memory recall are not modeled; use
  `SendRawPath` for those.
