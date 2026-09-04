namespace AnalogWay.Rc400t
{
    /// <summary>
    /// AWJ path layout for LivePremier firmware 4.x and later (Aquilon C, Alta 4K). Screens and
    /// aux-screens share one transition list ("screenAuxGroupList") addressed by an "S{n}"/"A{n}"
    /// key, and a layer's position and size fields live together under a single "position/pp" node.
    /// </summary>
    public sealed class LivePremier4PathSet : AwjPathSet
    {
        /// <summary>Pass this as the layer id to address a screen's native/background layer.</summary>
        public const string NativeLayer = "NATIVE";

        public override AwjPlatform Platform => AwjPlatform.LivePremier4;

        public override string[] XUpdatePath => new[] { "device", "screenAuxGroupList", "control", "pp", "xUpdate" };

        private static string ScreenItemId(ushort screenId, bool isAux) => (isAux ? "A" : "S") + screenId;

        private static string ScreenListName(bool isAux) => isAux ? "auxiliaryList" : "screenList";

        public override string[] TransitionControlPath(ushort screenId, bool isAux, string leaf)
        {
            return new[] { "device", "screenAuxGroupList", "items", ScreenItemId(screenId, isAux), "control", "pp", leaf };
        }

        public override string[] PresetPath(ushort screenId, bool isAux, AwjPresetBus bus)
        {
            return new[]
            {
                "device", ScreenListName(isAux), "items", ScreenItemId(screenId, isAux),
                "presetList", "items", PresetKey(bus)
            };
        }

        public override string[] LayerSourcePath(ushort screenId, bool isAux, AwjPresetBus bus, string layerId)
        {
            return Join(PresetPath(screenId, isAux, bus), "layerList", "items", layerId, "source", "pp", "inputNum");
        }

        public override string[] LayerPositionPath(ushort screenId, bool isAux, string layerId)
        {
            // LivePremier4 keeps posH/posV/sizeH/sizeV together under one "position/pp" node.
            return new[]
            {
                "device", ScreenListName(isAux), "items", ScreenItemId(screenId, isAux),
                "layerList", "items", layerId, "position", "pp"
            };
        }

        public override string[] LayerSizePath(ushort screenId, bool isAux, string layerId)
        {
            return LayerPositionPath(screenId, isAux, layerId);
        }

        public override string[] ScreenMemoryRecallPath(ushort screenId, bool isAux, AwjPresetBus bus, ushort memoryId)
        {
            return new[]
            {
                "device", "presetBank", "control", "load", "slotList", "items", memoryId.ToString(),
                ScreenListName(isAux), "items", ScreenItemId(screenId, isAux),
                "presetList", "items", PresetKey(bus), "pp", "xRequest"
            };
        }

        public override string[] MasterMemoryRecallPath(AwjPresetBus bus, ushort memoryId)
        {
            return new[]
            {
                "device", "masterPresetBank", "control", "load", "slotList", "items", memoryId.ToString(),
                "presetList", "items", PresetKey(bus), "pp", "xRequest"
            };
        }

        public override string[] InputFreezePath(ushort inputId)
        {
            return new[] { "device", "inputList", "items", inputId.ToString(), "control", "pp", "freeze" };
        }

        public override string[] PowerRequestPath()
        {
            return new[] { "device", "system", "shutdown", "cmd", "pp", "xRequest" };
        }
    }
}
