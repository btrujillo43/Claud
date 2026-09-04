using System;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronThread;
using Crestron.SimplSharp.Newtonsoft.Json.Linq;

namespace AnalogWay.Rc400t
{
    /// <summary>
    /// SIMPL# Pro control module for the video processors an Analog Way RC400T event controller
    /// drives (Aquilon C / Alta 4K on LivePremier 4.x, and the Midra 4K family: Eikos, Pulse,
    /// QuickMatrix, QuickVu, Zenith 100/200).
    /// </summary>
    /// <remarks>
    /// The RC400T itself is a hardware control surface with no third-party network API — it is one
    /// client among several (WebRCS, Companion, this module...) that all speak the processor's native
    /// "AWJ" (Analog Way JSON) protocol: a short REST handshake followed by a JSON-over-websocket
    /// control channel. This module speaks that same protocol directly, so a Crestron program using it
    /// has the same reach as the RC400T panel sitting next to it — screens, layers, sources, memories,
    /// the T-bar and PIP position/size that the panel's joystick drives.
    /// <para>
    /// Drop this class into a SIMPL# Pro program (or wrap it for SIMPL+) and use its public methods as
    /// digital/analog/serial inputs and its delegate properties as the matching outputs — the usual
    /// pattern for a hand-written SIMPL# module. See README.md for the full join-style command list,
    /// a path-support matrix per platform, and the protocol references this was built from.
    /// </para>
    /// </remarks>
    public sealed class AwjDevice : IDisposable
    {
        private readonly AwjWebSocketClient _ws = new AwjWebSocketClient();
        private readonly Dictionary<string, string> _state = new Dictionary<string, string>();
        private readonly object _stateLock = new object();

        private AwjPlatform _platform = AwjPlatform.Midra;
        private AwjPathSet _paths = AwjPathSet.For(AwjPlatform.Midra);
        private string _host = string.Empty;
        private int _port = 80;
        private string _password = string.Empty;

        public AwjDevice()
        {
            _ws.Connected += OnWsConnected;
            _ws.Disconnected += OnWsDisconnected;
            _ws.MessageReceived += OnWsMessage;
            _ws.Error += OnWsError;
        }

        #region Feedback delegates

        public delegate void UShortFeedbackDelegate(ushort value);
        public delegate void StringFeedbackDelegate(string value);
        public delegate void PathValueFeedbackDelegate(string path, string value);

        /// <summary>1 once the websocket handshake completes, 0 as soon as the socket drops.</summary>
        public UShortFeedbackDelegate OnlineFeedback { get; set; }

        /// <summary>Fires with a human readable message on connect/handshake/socket errors.</summary>
        public StringFeedbackDelegate ErrorFeedback { get; set; }

        /// <summary>Device model string extracted from the initial AWJ snapshot (e.g. "EIKOS_4K", "NLC_AQUILON_C").</summary>
        public StringFeedbackDelegate DeviceModelFeedback { get; set; }

        /// <summary>Firmware version extracted from the initial AWJ snapshot.</summary>
        public StringFeedbackDelegate FirmwareVersionFeedback { get; set; }

        /// <summary>Serial number extracted from the initial AWJ snapshot.</summary>
        public StringFeedbackDelegate SerialNumberFeedback { get; set; }

        /// <summary>
        /// Fires for every AWJ path/value update the device pushes (from us or from any other client,
        /// including the RC400T panel itself). Path segments are joined with "/". Use this to build any
        /// feedback this module doesn't already expose as a named delegate.
        /// </summary>
        public PathValueFeedbackDelegate StateChangedFeedback { get; set; }

        #endregion

        #region Configuration and connection

        /// <summary>0 = LivePremier4 (Aquilon C / Alta 4K), 1 = Midra 4K (Eikos/Pulse/QuickMatrix/QuickVu/Zenith).</summary>
        public void SetPlatform(ushort platform)
        {
            _platform = platform == 0 ? AwjPlatform.LivePremier4 : AwjPlatform.Midra;
            _paths = AwjPathSet.For(_platform);
        }

        /// <summary>Configures the device address. Call before Connect(). Port 0 means the default (80).</summary>
        public void Initialize(string ipAddress, ushort port, string adminPassword)
        {
            _host = ipAddress;
            _port = port == 0 ? 80 : port;
            _password = adminPassword ?? string.Empty;
        }

        public void Connect()
        {
            if (string.IsNullOrEmpty(_host))
            {
                RaiseError("Connect called before Initialize(ipAddress, port, password)");
                return;
            }

            new Thread(ConnectWorker, null) { Priority = Thread.eThreadPriority.MediumPriority };
        }

        private object ConnectWorker(object userSpecific)
        {
            try
            {
                var rest = new AwjRestClient(_host, _port);
                string cookie = null;

                if (rest.IsAuthenticationEnabled())
                {
                    var login = rest.Login(_password);
                    if (!login.Success)
                    {
                        RaiseError("Login failed: " + login.Error);
                        return null;
                    }
                    cookie = login.CookieHeader;
                }

                try
                {
                    var snapshotJson = rest.GetDeviceState(cookie);
                    ApplySnapshot(JObject.Parse(snapshotJson));
                }
                catch (Exception ex)
                {
                    // Non-fatal: the websocket's own INIT message will still bring the state up to date.
                    RaiseError("Initial state fetch failed (continuing): " + ex.Message);
                }

                _ws.Connect(_host, _port, cookie);
            }
            catch (Exception ex)
            {
                RaiseError("Connect failed: " + ex.Message);
            }
            return null;
        }

        public void Disconnect()
        {
            _ws.Disconnect();
        }

        public bool IsConnected
        {
            get { return _ws.IsConnected; }
        }

        #endregion

        #region Transition control

        /// <summary>Pulses the take for a screen (Midra) or aux-screen. On LivePremier4 use TakeToBus instead.</summary>
        public void Take(ushort screenId, ushort isAux)
        {
            SendSet(_paths.TransitionControlPath(screenId, isAux != 0, "xTake"), true);
        }

        /// <summary>LivePremier4: makes preset A or B the program bus for this screen/aux-screen.</summary>
        public void TakeToBus(ushort screenId, ushort isAux, ushort bus)
        {
            var leaf = bus == 0 ? "xTakeUp" : "xTakeDown";
            SendSet(_paths.TransitionControlPath(screenId, isAux != 0, leaf), true);
        }

        public void Cut(ushort screenId, ushort isAux)
        {
            SendSet(_paths.TransitionControlPath(screenId, isAux != 0, "xCut"), true);
        }

        /// <summary>0-65535 across the full T-bar/fader travel, matching the raw AWJ tbarPosition range.</summary>
        public void SetTBarPosition(ushort screenId, ushort isAux, ushort position)
        {
            SendSet(_paths.TransitionControlPath(screenId, isAux != 0, "tbarPosition"), position);
        }

        /// <summary>Raw device transition-time units (verify the slider range for your firmware in the WebRCS UI before mapping to milliseconds).</summary>
        public void SetTransitionTime(ushort screenId, ushort isAux, ushort rawValue)
        {
            SendSet(_paths.TransitionControlPath(screenId, isAux != 0, "takeTime"), rawValue);
        }

        #endregion

        #region Sources and layers

        /// <summary>
        /// Selects the source shown on a layer of the given preset bus, then commits the edit.
        /// <paramref name="layerId"/> is a numeric layer key ("1", "2", ...) for a normal layer.
        /// On Midra, pass <see cref="MidraPathSet.NativeLayer"/> or <see cref="MidraPathSet.TopLayer"/>
        /// to address the screen's background/native source or its top PIP layer instead.
        /// </summary>
        public void SetLayerSource(ushort screenId, ushort isAux, ushort bus, string layerId, ushort sourceId)
        {
            var path = _paths.LayerSourcePath(screenId, isAux != 0, (AwjPresetBus)bus, layerId);
            SendSet(path, sourceId);
            SendXUpdate();
        }

        /// <summary>Moves a live (on-air) layer. Position is in on-screen pixels, origin top-left, matching the AWJ posH/posV fields.</summary>
        public void SetLayerPosition(ushort screenId, ushort isAux, string layerId, short x, short y)
        {
            var basePath = _paths.LayerPositionPath(screenId, isAux != 0, layerId);
            SendSet(Append(basePath, "posH"), x);
            SendSet(Append(basePath, "posV"), y);
        }

        /// <summary>Resizes a live (on-air) layer, in pixels, matching the AWJ sizeH/sizeV fields.</summary>
        public void SetLayerSize(ushort screenId, ushort isAux, string layerId, ushort width, ushort height)
        {
            var basePath = _paths.LayerSizePath(screenId, isAux != 0, layerId);
            SendSet(Append(basePath, "sizeH"), width);
            SendSet(Append(basePath, "sizeV"), height);
        }

        #endregion

        #region Memories

        public void RecallScreenMemory(ushort screenId, ushort isAux, ushort bus, ushort memoryId)
        {
            SendPulse(_paths.ScreenMemoryRecallPath(screenId, isAux != 0, (AwjPresetBus)bus, memoryId));
            SendXUpdate();
        }

        public void RecallMasterMemory(ushort bus, ushort memoryId)
        {
            SendPulse(_paths.MasterMemoryRecallPath((AwjPresetBus)bus, memoryId));
            SendXUpdate();
        }

        #endregion

        #region Inputs and system

        public void FreezeInput(ushort inputId, ushort freeze)
        {
            SendSet(_paths.InputFreezePath(inputId), freeze != 0);
        }

        /// <summary>Midra only: starts or stops the built-in streaming encoder.</summary>
        public void SetStreaming(ushort start)
        {
            SendSet(new[] { "device", "streaming", "control", "pp", "start" }, start != 0);
        }

        public void PowerOff()
        {
            var path = _paths.PowerRequestPath();
            if (_platform == AwjPlatform.Midra)
            {
                SendSet(path, "SWITCH_OFF");
            }
            else
            {
                SendSet(path, "NONE");
                SendSet(path, "SHUTDOWN");
            }
        }

        public void Reboot()
        {
            var path = _paths.PowerRequestPath();
            if (_platform == AwjPlatform.Midra)
            {
                SendPulse(new[] { "device", "system", "shutdown", "pp", "xReboot" });
            }
            else
            {
                SendSet(path, "NONE");
                SendSet(path, "REBOOT");
            }
        }

        public void Standby()
        {
            if (_platform != AwjPlatform.Midra)
            {
                RaiseError("Standby is only modeled for Midra; LivePremier4 exposes PowerOff/Reboot only. Use SendRawPath if your firmware supports a standby state.");
                return;
            }
            SendSet(_paths.PowerRequestPath(), "STANDBY");
        }

        /// <summary>
        /// Sends a Wake-on-LAN magic packet to bring the device out of standby/power-off. AWJ devices
        /// answer only to this, not to any command on the control websocket, while powered down.
        /// </summary>
        public void WakeOnLan(string macAddress)
        {
            try
            {
                CrestronWakeOnLan.Send(macAddress);
            }
            catch (Exception ex)
            {
                RaiseError("Wake on LAN failed: " + ex.Message);
            }
        }

        #endregion

        #region Escape hatch

        /// <summary>
        /// Sends an arbitrary AWJ path/value pair for anything this module doesn't model as a named
        /// method. <paramref name="path"/> is "/"-separated (e.g. "device/streaming/control/pp/start").
        /// <paramref name="jsonValue"/> is a JSON literal: true, false, a bare number, or a quoted string.
        /// </summary>
        public void SendRawPath(string path, string jsonValue)
        {
            if (string.IsNullOrEmpty(path))
            {
                RaiseError("SendRawPath: path is empty");
                return;
            }
            try
            {
                var value = JToken.Parse(jsonValue);
                _ws.Send(AwjMessage.BuildSet(path.Split('/'), value));
            }
            catch (Exception ex)
            {
                RaiseError("SendRawPath: could not parse value '" + jsonValue + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Reads back the last known value at a path (as received from the device), or "" if unknown.
        /// Returns "" rather than null so this is safe to assign directly to a SIMPL+ STRING signal.
        /// </summary>
        public string GetStateValue(string path)
        {
            lock (_stateLock)
            {
                string value;
                return _state.TryGetValue(path, out value) ? value : string.Empty;
            }
        }

        #endregion

        #region Internals

        private static string[] Append(string[] path, string leaf)
        {
            var result = new string[path.Length + 1];
            Array.Copy(path, result, path.Length);
            result[path.Length] = leaf;
            return result;
        }

        private void SendSet(string[] path, object value)
        {
            _ws.Send(AwjMessage.BuildSet(path, value));
        }

        private void SendPulse(string[] path)
        {
            foreach (var message in AwjMessage.BuildPulse(path))
            {
                _ws.Send(message);
            }
        }

        private void SendXUpdate()
        {
            foreach (var message in AwjMessage.BuildXUpdate(_paths.XUpdatePath))
            {
                _ws.Send(message);
            }
        }

        private void OnWsConnected()
        {
            var handler = OnlineFeedback;
            if (handler != null) handler(1);
        }

        private void OnWsDisconnected()
        {
            var handler = OnlineFeedback;
            if (handler != null) handler(0);
        }

        private void OnWsError(string message)
        {
            RaiseError(message);
        }

        private void RaiseError(string message)
        {
            var handler = ErrorFeedback;
            if (handler != null) handler(message);
            ErrorLog.Error("AnalogWay.Rc400t: {0}", message);
        }

        private void OnWsMessage(string json)
        {
            var message = AwjMessage.Parse(json);
            switch (message.Kind)
            {
                case AwjMessageKind.Snapshot:
                    if (message.Snapshot is JObject snapshot)
                    {
                        ApplySnapshot(snapshot);
                    }
                    break;
                case AwjMessageKind.PathValue:
                case AwjMessageKind.Patch:
                    StoreAndNotify(message.Path, message.Value);
                    break;
            }
        }

        private void ApplySnapshot(JObject deviceRoot)
        {
            // deviceRoot is expected to be the "device" branch AWJ returns from /api/stores/device and
            // wraps in the websocket INIT message. Layout confirmed against the officially documented
            // AWJ device model (see README.md "Protocol references").
            try
            {
                var system = deviceRoot["device"] != null ? deviceRoot["device"]["system"] : deviceRoot["system"];
                if (system == null) return;

                var deviceList = system["deviceList"];
                var firstDevice = deviceList != null ? deviceList["items"]?["1"] : null;

                var devicePp = firstDevice != null ? firstDevice["pp"] : system["pp"];
                var modelToken = devicePp != null ? devicePp["dev"] : null;
                if (modelToken != null)
                {
                    RaiseString(DeviceModelFeedback, modelToken.ToString());
                }

                var fwToken = system["version"]?["pp"]?["updater"] ?? firstDevice?["version"]?["pp"]?["updater"];
                if (fwToken != null)
                {
                    RaiseString(FirmwareVersionFeedback, fwToken.ToString());
                }

                var serialToken = system["serial"]?["pp"]?["serialNumber"] ?? firstDevice?["serial"]?["pp"]?["serialNumber"];
                if (serialToken != null)
                {
                    RaiseString(SerialNumberFeedback, serialToken.ToString());
                }
            }
            catch (Exception ex)
            {
                RaiseError("Could not parse device snapshot: " + ex.Message);
            }
        }

        private static void RaiseString(StringFeedbackDelegate handler, string value)
        {
            if (handler != null) handler(value);
        }

        private void StoreAndNotify(string path, JToken value)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // AWJ pushes a periodic clock/temperature tick on every device that is pure noise for a
            // control integration; drop it before it reaches the program.
            if (path.IndexOf("currentTime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("temperature", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            var valueString = value == null ? string.Empty : value.ToString();
            lock (_stateLock)
            {
                _state[path] = valueString;
            }

            var handler = StateChangedFeedback;
            if (handler != null) handler(path, valueString);
        }

        #endregion

        public void Dispose()
        {
            _ws.Dispose();
        }
    }
}
