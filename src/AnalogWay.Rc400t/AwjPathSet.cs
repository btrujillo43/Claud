using System.Collections.Generic;

namespace AnalogWay.Rc400t
{
    /// <summary>
    /// Builds the AWJ ("path" + "value") command addresses for one device family.
    /// AWJ addresses a parameter with a path that looks like a file system path, e.g.
    /// <c>device/screenList/items/1/presetList/items/A/liveLayerList/items/2/source/pp/input</c>.
    /// The exact segment names differ between the LivePremier4 (Aquilon C / Alta 4K) and Midra
    /// (Eikos / Pulse / QuickMatrix / QuickVu / Zenith) firmware families, so each family gets its
    /// own concrete implementation instead of forcing a single shared layout.
    /// </summary>
    public abstract class AwjPathSet
    {
        public abstract AwjPlatform Platform { get; }

        /// <summary>Path whose value is pulsed false/true to make the device apply pending preset edits.</summary>
        public abstract string[] XUpdatePath { get; }

        /// <summary>"A" or "B" preset bus key as used inside "presetList/items/{key}".</summary>
        public static string PresetKey(AwjPresetBus bus)
        {
            return bus == AwjPresetBus.B ? "B" : "A";
        }

        /// <summary>
        /// Path to the transition/control node of a screen or aux-screen, used for xTake, xCut,
        /// tbarPosition, takeTime and enablePresetToggle.
        /// </summary>
        public abstract string[] TransitionControlPath(ushort screenId, bool isAux, string leaf);

        /// <summary>Path to a screen/aux's preset (A or B) node, the parent of layer and background edits.</summary>
        public abstract string[] PresetPath(ushort screenId, bool isAux, AwjPresetBus bus);

        /// <summary>
        /// Path to a layer's source selector under a given preset.
        /// <paramref name="layerId"/> is the platform layer key: a numeric string for a normal layer,
        /// or a platform specific keyword (see remarks on each implementation) for background/native layers.
        /// </summary>
        public abstract string[] LayerSourcePath(ushort screenId, bool isAux, AwjPresetBus bus, string layerId);

        /// <summary>Path to the live (on-air) layer's position node (posH/posV live under this as leaves).</summary>
        public abstract string[] LayerPositionPath(ushort screenId, bool isAux, string layerId);

        /// <summary>Path to the live (on-air) layer's size node (sizeH/sizeV live under this as leaves).</summary>
        public abstract string[] LayerSizePath(ushort screenId, bool isAux, string layerId);

        /// <summary>Path whose value is pulsed false/true to recall a screen memory into a preset bus.</summary>
        public abstract string[] ScreenMemoryRecallPath(ushort screenId, bool isAux, AwjPresetBus bus, ushort memoryId);

        /// <summary>Path whose value is pulsed false/true to recall a master (multi-screen) memory.</summary>
        public abstract string[] MasterMemoryRecallPath(AwjPresetBus bus, ushort memoryId);

        /// <summary>Path to an input's freeze control.</summary>
        public abstract string[] InputFreezePath(ushort inputId);

        /// <summary>Path used to request standby / power off / wake. Meaning of value is family specific.</summary>
        public abstract string[] PowerRequestPath();

        public static AwjPathSet For(AwjPlatform platform)
        {
            switch (platform)
            {
                case AwjPlatform.Midra:
                    return new MidraPathSet();
                case AwjPlatform.LivePremier4:
                default:
                    return new LivePremier4PathSet();
            }
        }

        protected static string[] Join(IEnumerable<string> parts, params string[] tail)
        {
            var list = new List<string>(parts);
            list.AddRange(tail);
            return list.ToArray();
        }
    }
}
