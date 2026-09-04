using System;
using Crestron.SimplSharp.Newtonsoft.Json;
using Crestron.SimplSharp.Newtonsoft.Json.Linq;

namespace AnalogWay.Rc400t
{
    /// <summary>What kind of update a parsed AWJ websocket message carries.</summary>
    public enum AwjMessageKind
    {
        Unknown,
        /// <summary>A full state snapshot, sent once right after the websocket opens.</summary>
        Snapshot,
        /// <summary>A single path/value update, either one we caused or one another client caused.</summary>
        PathValue,
        /// <summary>An RFC6902-style JSON patch operation (replace/add/remove).</summary>
        Patch
    }

    /// <summary>A decoded AWJ websocket message.</summary>
    public class AwjIncomingMessage
    {
        public AwjMessageKind Kind { get; set; }
        public string Channel { get; set; }
        public string Path { get; set; }
        public JToken Value { get; set; }
        public JToken Snapshot { get; set; }
        public string PatchOp { get; set; }
    }

    /// <summary>
    /// Builds and parses the small JSON envelope AWJ wraps every websocket message in:
    /// <c>{"channel":"DEVICE","data":{"path":[...],"value":...}}</c> for direct writes, or
    /// <c>{"channel":...,"data":{"channel":"PATCH","patch":{"op":...,"path":...,"value":...}}}</c>
    /// for change notifications, or <c>{"channel":...,"data":{"channel":"INIT","snapshot":{...}}}</c>
    /// for the initial state dump.
    /// </summary>
    public static class AwjMessage
    {
        /// <summary>Builds a single "set this path to this value" message.</summary>
        public static string BuildSet(string[] path, object value)
        {
            var obj = new JObject
            {
                ["channel"] = "DEVICE",
                ["data"] = new JObject
                {
                    ["path"] = new JArray(path),
                    ["value"] = JToken.FromObject(value)
                }
            };
            return obj.ToString(Formatting.None);
        }

        /// <summary>
        /// Builds the false/then/true pulse AWJ uses to edge-trigger an action (xTake, xRequest, a
        /// memory recall's "pp/xRequest", ...). Send both strings in order.
        /// </summary>
        public static string[] BuildPulse(string[] path)
        {
            return new[] { BuildSet(path, false), BuildSet(path, true) };
        }

        /// <summary>Builds the false/then/true pulse used to commit pending preset edits.</summary>
        public static string[] BuildXUpdate(string[] xUpdatePath)
        {
            return BuildPulse(xUpdatePath);
        }

        /// <summary>Parses one incoming websocket text frame's payload.</summary>
        public static AwjIncomingMessage Parse(string json)
        {
            var result = new AwjIncomingMessage { Kind = AwjMessageKind.Unknown };
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return result;
            }

            result.Channel = (string)root["channel"];
            var data = root["data"] as JObject;
            if (data == null)
            {
                return result;
            }

            var innerChannel = (string)data["channel"];
            if (innerChannel == "INIT")
            {
                result.Kind = AwjMessageKind.Snapshot;
                result.Snapshot = data["snapshot"];
                return result;
            }

            if (innerChannel == "PATCH" && data["patch"] is JObject patch)
            {
                result.Kind = AwjMessageKind.Patch;
                result.PatchOp = (string)patch["op"];
                result.Path = PathToString(patch["path"]);
                result.Value = patch["value"];
                return result;
            }

            if (data["path"] != null)
            {
                result.Kind = AwjMessageKind.PathValue;
                result.Path = PathToString(data["path"]);
                result.Value = data["value"];
                return result;
            }

            return result;
        }

        private static string PathToString(JToken path)
        {
            if (path == null)
            {
                return string.Empty;
            }
            if (path.Type == JTokenType.Array)
            {
                return string.Join("/", path.ToObject<string[]>());
            }
            return path.ToString().TrimStart('/');
        }
    }
}
