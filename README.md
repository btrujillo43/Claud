# Analog Way RC400T — Crestron Control Module

A Crestron control module for the video processors an **Analog Way RC400T** event controller
drives: **Aquilon C / Alta 4K** (LivePremier firmware 4.x) and the **Midra 4K** family (Eikos,
Pulse, QuickMatrix, QuickVu, Zenith 100/200). The core logic is a SIMPL# class library
(`src/AnalogWay.Rc400t/`); a SIMPL+ wrapper (`simplplus/AnalogWayRc400t.usp`) brings it into
SIMPL Windows as an insertable symbol, and the same class can be used directly from a SIMPL# Pro
program if you're not using SIMPL Windows at all.

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
| `src/AnalogWay.Rc400t/AwjDevice.cs` | The module itself. Public methods are the "inputs", public delegate properties are the "outputs" — call/subscribe to them directly from a SIMPL# Pro program, or through the SIMPL+ wrapper below. |
| `src/AnalogWay.Rc400t/AwjWebSocketClient.cs` | Hand-rolled RFC 6455 websocket client (Crestron's SDK has no built-in one) — AWJ's live control channel only exists over `ws://`/`wss://`. |
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

`AwjDevice` and its helper classes use only sockets, threading, HTTP, crypto and JSON — none of
`SimplSharpPro.dll`'s hardware I/O surface — so the project builds as a plain **SIMPL#** library
(not "SIMPL# Pro"). That's deliberate: plain SIMPL# libraries are the kind the SIMPL+
cross-compiler can link against, which is what makes the SIMPL+ wrapper below possible, and it
also runs fine standalone from a SIMPL# Pro program on 3-series, 4-series or Virtual Control.

This needs Crestron's SDK to compile and cannot be built in a general-purpose .NET environment:

1. Open `src/AnalogWay.Rc400t/AnalogWay.Rc400t.csproj` in Visual Studio with the Crestron SIMPL#
   tooling installed (or just restore NuGet packages — it references Crestron's official
   `Crestron.SimplSharp.SDK.Library` package). If your toolchain predates that package, replace the
   `PackageReference` with a `<Reference Include="SimplSharp" HintPath="..."/>` pointing at
   `SimplSharp.dll` (plus `Crestron.SimplSharp.Newtonsoft.Json.dll` and
   `Crestron.SimplSharp.Cryptography.dll`) from your local Crestron SDK install.
2. Build. This produces `AnalogWay.Rc400t.dll` — the assembly name the `.usp` wrapper references
   via `#USER_SIMPLSHARP_LIBRARY "AnalogWay.Rc400t"`.
3. Targets `net472` and uses only Crestron's documented socket/HTTP/crypto/JSON namespaces — no
   3rd-party NuGet packages beyond Crestron's own SDK.

> **Note on API surface:** the exact method names on Crestron's `CrestronSockets.TCPClient`,
> `CrestronSockets.UDPServer`, `Net.Http.HttpClient` and `Cryptography.SHA1CryptoServiceProvider`
> classes have shifted slightly across SDK releases. This was written against the long-standing,
> widely-documented shape of those APIs but wasn't compiled against the actual SDK in this
> environment (network access to Crestron's developer portal was not available here) — do a first
> build against your installed SDK version and adjust any renamed members before deploying.
> The same caveat applies to the SIMPL+ syntax in `AnalogWayRc400t.usp` — it follows the
> long-documented `#USER_SIMPLSHARP_LIBRARY` / `CALLBACK FUNCTION` interop pattern, but hasn't been
> run through the actual SIMPL+ cross-compiler; treat the first compile as the real check.

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
