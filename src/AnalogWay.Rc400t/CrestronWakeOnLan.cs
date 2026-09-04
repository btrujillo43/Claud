using System;
using Crestron.SimplSharp.CrestronSockets;

namespace AnalogWay.Rc400t
{
    /// <summary>
    /// Sends a standard Wake-on-LAN "magic packet" (6 bytes of 0xFF followed by the target MAC address
    /// repeated 16 times) as a UDP broadcast. AWJ processors power up from this exactly like any other
    /// WOL-capable device; it is how WebRCS (and this module) can power a device back on that is fully
    /// off, since nothing answers on the control websocket in that state.
    /// </summary>
    public static class CrestronWakeOnLan
    {
        private const int MacLength = 6;
        private const int RepeatCount = 16;
        private const int DefaultWolPort = 9;

        public static void Send(string macAddress)
        {
            Send(macAddress, "255.255.255.255", DefaultWolPort);
        }

        public static void Send(string macAddress, string broadcastAddress, int port)
        {
            var packet = BuildMagicPacket(macAddress);
            using (var udp = new UDPServer())
            {
                udp.EnableUDPServer(0);
                udp.SendData(packet, packet.Length, broadcastAddress, port);
                udp.DisableUDPServer();
            }
        }

        private static byte[] BuildMagicPacket(string macAddress)
        {
            var macBytes = ParseMac(macAddress);
            var packet = new byte[6 + MacLength * RepeatCount];
            for (var i = 0; i < 6; i++)
            {
                packet[i] = 0xFF;
            }
            for (var i = 0; i < RepeatCount; i++)
            {
                Array.Copy(macBytes, 0, packet, 6 + i * MacLength, MacLength);
            }
            return packet;
        }

        private static byte[] ParseMac(string macAddress)
        {
            if (string.IsNullOrEmpty(macAddress))
            {
                throw new ArgumentException("MAC address is required for Wake on LAN");
            }

            var cleaned = macAddress.Replace(":", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty).Trim();
            if (cleaned.Length != MacLength * 2)
            {
                throw new ArgumentException("MAC address '" + macAddress + "' is not 6 bytes long");
            }

            var bytes = new byte[MacLength];
            for (var i = 0; i < MacLength; i++)
            {
                bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
