namespace AnalogWay.Rc400t
{
    /// <summary>
    /// AWJ path layout for the Midra 4K family (Eikos, Pulse, QuickMatrix, QuickVu, Zenith 100/200).
    /// Screens and aux-screens live in separate lists, and a screen's "native" (background) source and
    /// a top PIP layer have their own dedicated leaves instead of being ordinary numbered layers.
    /// </summary>
    public sealed class MidraPathSet : AwjPathSet
    {
        /// <summary>Pass this as the layer id to address a screen's native/background source.</summary>
        public const string NativeLayer = "NATIVE";

        /// <summary>Pass this as the layer id to address the top (PIP) layer's source.</summary>
        public const string TopLayer = "TOP";

        public override AwjPlatform Platform => AwjPlatform.Midra;

        public override string[] XUpdatePath => new[] { "device", "preset", "control", "pp", "xUpdate" };

        private static string ScreenListName(bool isAux) => isAux ? "auxiliaryScreenList" : "screenList";

        private static string Num(ushort id) => id.ToString();

        public override string[] TransitionControlPath(ushort screenId, bool isAux, string leaf)
        {
            return new[] { "device", "transition", ScreenListName(isAux), "items", Num(screenId), "control", "pp", leaf };
        }

        public override string[] PresetPath(ushort screenId, bool isAux, AwjPresetBus bus)
        {
            return new[]
            {
                "device", ScreenListName(isAux), "items", Num(screenId),
                "presetList", "items", PresetKey(bus)
            };
        }

        public override string[] LayerSourcePath(ushort screenId, bool isAux, AwjPresetBus bus, string layerId)
        {
            var preset = PresetPath(screenId, isAux, bus);
            if (isAux)
            {
                // Midra aux screens only ever show a single background source.
                return Join(preset, "background", "source", "pp", "content");
            }
            if (layerId == NativeLayer)
            {
                return Join(preset, "background", "source", "pp", "set");
            }
            if (layerId == TopLayer)
            {
                return Join(preset, "top", "source", "pp", "frame");
            }
            return Join(preset, "liveLayerList", "items", layerId, "source", "pp", "input");
        }

        public override string[] LayerPositionPath(ushort screenId, bool isAux, string layerId)
        {
            return new[]
            {
                "device", ScreenListName(isAux), "items", Num(screenId),
                "liveLayerList", "items", layerId, "position", "pp"
            };
        }

        public override string[] LayerSizePath(ushort screenId, bool isAux, string layerId)
        {
            return new[]
            {
                "device", ScreenListName(isAux), "items", Num(screenId),
                "liveLayerList", "items", layerId, "size", "pp"
            };
        }

        public override string[] ScreenMemoryRecallPath(ushort screenId, bool isAux, AwjPresetBus bus, ushort memoryId)
        {
            // Screens and aux-screens are recalled from two different banks on Midra.
            var bank = isAux ? "auxBank" : "bank";
            return new[]
            {
                "device", "preset", bank, "control", "load", "slotList", "items", Num(memoryId),
                ScreenListName(isAux), "items", Num(screenId),
                "presetList", "items", PresetKey(bus), "pp", "xRequest"
            };
        }

        public override string[] MasterMemoryRecallPath(AwjPresetBus bus, ushort memoryId)
        {
            return new[]
            {
                "device", "preset", "masterBank", "control", "load", "slotList", "items", Num(memoryId),
                "presetList", "items", PresetKey(bus), "pp", "xRequest"
            };
        }

        public override string[] InputFreezePath(ushort inputId)
        {
            return new[] { "device", "inputList", "items", Num(inputId), "control", "pp", "freeze" };
        }

        public override string[] PowerRequestPath()
        {
            return new[] { "device", "system", "shutdown", "standby", "control", "pp", "xRequest" };
        }
    }
}
