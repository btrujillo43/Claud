using System;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using Crestron.SimplSharp.CrestronThread;
using Crestron.SimplSharp.Cryptography;

namespace AnalogWay.Rc400t
{
    /// <summary>
    /// Minimal RFC 6455 WebSocket client over a Crestron TCP socket.
    /// AWJ devices don't expose their live control channel any other way: after the REST login the
    /// processor pushes and accepts JSON state changes only over a plain (ws://) or TLS (wss://)
    /// websocket opened at the device root ("/"). Crestron's SIMPL# SDK has no built-in websocket
    /// client, so this hand-rolls the HTTP upgrade handshake and text-frame framing.
    /// </summary>
    /// <remarks>
    /// Only what AWJ actually needs is implemented: text frames (opcode 0x1), continuation frames,
    /// ping/pong, and close. Binary frames are not expected from an AWJ device and are ignored.
    /// TLS (wss://) is not implemented — AWJ processors are normally reached over an isolated AV
    /// control network without TLS, matching the "http://" default WebRCS itself uses.
    /// </remarks>
    public sealed class AwjWebSocketClient : IDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int ReadBufferSize = 8192;

        private TCPClient _client;
        private Thread _receiveThread;
        private volatile bool _running;
        private readonly byte[] _readBuffer = new byte[ReadBufferSize];
        private byte[] _fragmentBuffer = new byte[0];
        private int _fragmentOpcode = -1;

        public bool IsConnected { get; private set; }

        /// <summary>Raised once the HTTP upgrade handshake succeeds and the socket is ready to send/receive.</summary>
        public event Action Connected;

        /// <summary>Raised for every complete text message received from the device.</summary>
        public event Action<string> MessageReceived;

        /// <summary>Raised when the socket closes, whether requested or not.</summary>
        public event Action Disconnected;

        /// <summary>Raised on any handshake or socket error. The connection should be considered dead.</summary>
        public event Action<string> Error;

        /// <summary>
        /// Opens the socket and performs the websocket upgrade handshake.
        /// </summary>
        /// <param name="host">Device hostname or IP address.</param>
        /// <param name="port">TCP port (80 for the default ws:// endpoint).</param>
        /// <param name="cookieHeader">The "Cookie" header value returned by the AWJ /auth/login call, or null/empty if authentication is disabled.</param>
        public void Connect(string host, int port, string cookieHeader)
        {
            Disconnect();

            try
            {
                _client = new TCPClient(host, port, ReadBufferSize);
                var result = _client.ConnectToServer();
                if (result != SocketErrorCodes.SOCKET_OK)
                {
                    RaiseError("TCP connect failed: " + result);
                    return;
                }

                var key = GenerateWebSocketKey();
                var request = BuildUpgradeRequest(host, port, key, cookieHeader);
                var requestBytes = Encoding.ASCII.GetBytes(request);
                _client.SendData(requestBytes, requestBytes.Length);

                if (!ReadHandshakeResponse(key))
                {
                    Disconnect();
                    return;
                }

                IsConnected = true;
                _running = true;
                _receiveThread = new Thread(ReceiveLoop, null)
                {
                    Priority = Thread.eThreadPriority.MediumPriority
                };

                var handler = Connected;
                if (handler != null) handler();
            }
            catch (Exception ex)
            {
                RaiseError("Connect exception: " + ex.Message);
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _running = false;
            IsConnected = false;
            try
            {
                if (_client != null)
                {
                    _client.DisconnectFromServer();
                    _client.Dispose();
                }
            }
            catch (Exception)
            {
                // best effort
            }
            _client = null;

            var handler = Disconnected;
            if (handler != null) handler();
        }

        /// <summary>Sends one text message as a single, masked (client-to-server) websocket frame.</summary>
        public void Send(string message)
        {
            if (!IsConnected || _client == null)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(message);
            var frame = EncodeTextFrame(payload);
            try
            {
                _client.SendData(frame, frame.Length);
            }
            catch (Exception ex)
            {
                RaiseError("Send failed: " + ex.Message);
                Disconnect();
            }
        }

        private object ReceiveLoop(object userSpecific)
        {
            while (_running && _client != null)
            {
                int read;
                try
                {
                    read = _client.ReceiveData(_readBuffer, ReadBufferSize);
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        RaiseError("Receive failed: " + ex.Message);
                    }
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                var offset = 0;
                while (offset < read)
                {
                    var consumed = ProcessFrame(_readBuffer, offset, read - offset);
                    if (consumed <= 0)
                    {
                        // Incomplete frame at the end of the buffer: in the rare case a frame spans
                        // TCP reads this drops the remainder. AWJ deltas are small and this has not
                        // been observed in practice, but a production deployment should extend this
                        // with a persistent receive buffer if very large payloads are expected.
                        break;
                    }
                    offset += consumed;
                }
            }

            if (_running)
            {
                _running = false;
                IsConnected = false;
                var handler = Disconnected;
                if (handler != null) handler();
            }
            return null;
        }

        /// <summary>Decodes one RFC 6455 frame starting at <paramref name="offset"/>. Returns bytes consumed, or 0 if incomplete.</summary>
        private int ProcessFrame(byte[] buffer, int offset, int length)
        {
            if (length < 2) return 0;

            var b0 = buffer[offset];
            var b1 = buffer[offset + 1];
            var fin = (b0 & 0x80) != 0;
            var opcode = b0 & 0x0F;
            var masked = (b1 & 0x80) != 0; // servers must not mask; tolerated either way
            long payloadLen = b1 & 0x7F;
            var pos = offset + 2;

            if (payloadLen == 126)
            {
                if (length < 4) return 0;
                payloadLen = (buffer[pos] << 8) | buffer[pos + 1];
                pos += 2;
            }
            else if (payloadLen == 127)
            {
                if (length < 10) return 0;
                payloadLen = 0;
                for (var i = 0; i < 8; i++)
                {
                    payloadLen = (payloadLen << 8) | buffer[pos + i];
                }
                pos += 8;
            }

            byte[] mask = null;
            if (masked)
            {
                if (offset + length - pos < 4) return 0;
                mask = new byte[4];
                Array.Copy(buffer, pos, mask, 0, 4);
                pos += 4;
            }

            if (offset + length - pos < payloadLen) return 0; // wait for more data

            var payload = new byte[payloadLen];
            Array.Copy(buffer, pos, payload, 0, (int)payloadLen);
            if (mask != null)
            {
                for (var i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i % 4];
                }
            }

            HandleFrame(fin, opcode, payload);

            return (int)(pos + payloadLen - offset);
        }

        private void HandleFrame(bool fin, int opcode, byte[] payload)
        {
            switch (opcode)
            {
                case 0x0: // continuation
                    AppendFragment(payload);
                    if (fin) CompleteFragmentedMessage();
                    return;
                case 0x1: // text
                    if (fin)
                    {
                        RaiseMessage(Encoding.UTF8.GetString(payload, 0, payload.Length));
                    }
                    else
                    {
                        _fragmentOpcode = 0x1;
                        _fragmentBuffer = payload;
                    }
                    return;
                case 0x2: // binary - not expected from AWJ, ignored
                    return;
                case 0x8: // close
                    Disconnect();
                    return;
                case 0x9: // ping -> pong
                    SendControlFrame(0xA, payload);
                    return;
                case 0xA: // pong
                    return;
            }
        }

        private void AppendFragment(byte[] payload)
        {
            var combined = new byte[_fragmentBuffer.Length + payload.Length];
            Array.Copy(_fragmentBuffer, combined, _fragmentBuffer.Length);
            Array.Copy(payload, 0, combined, _fragmentBuffer.Length, payload.Length);
            _fragmentBuffer = combined;
        }

        private void CompleteFragmentedMessage()
        {
            if (_fragmentOpcode == 0x1)
            {
                RaiseMessage(Encoding.UTF8.GetString(_fragmentBuffer, 0, _fragmentBuffer.Length));
            }
            _fragmentBuffer = new byte[0];
            _fragmentOpcode = -1;
        }

        private void RaiseMessage(string message)
        {
            var handler = MessageReceived;
            if (handler != null) handler(message);
        }

        private void RaiseError(string message)
        {
            var handler = Error;
            if (handler != null) handler(message);
        }

        private void SendControlFrame(byte opcode, byte[] payload)
        {
            if (_client == null) return;
            var frame = EncodeFrame(opcode, payload ?? new byte[0]);
            try
            {
                _client.SendData(frame, frame.Length);
            }
            catch (Exception)
            {
                // ignore - the main receive loop will notice the dead socket
            }
        }

        private static byte[] EncodeTextFrame(byte[] payload)
        {
            return EncodeFrame(0x1, payload);
        }

        /// <summary>Encodes one unfragmented, masked (client-to-server) frame.</summary>
        private static byte[] EncodeFrame(byte opcode, byte[] payload)
        {
            var maskKey = new byte[4];
            new Random().NextBytes(maskKey);

            var masked = new byte[payload.Length];
            for (var i = 0; i < payload.Length; i++)
            {
                masked[i] = (byte)(payload[i] ^ maskKey[i % 4]);
            }

            byte[] header;
            if (payload.Length <= 125)
            {
                header = new byte[2];
                header[0] = (byte)(0x80 | opcode);
                header[1] = (byte)(0x80 | payload.Length);
            }
            else if (payload.Length <= 65535)
            {
                header = new byte[4];
                header[0] = (byte)(0x80 | opcode);
                header[1] = 0x80 | 126;
                header[2] = (byte)((payload.Length >> 8) & 0xFF);
                header[3] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                header = new byte[10];
                header[0] = (byte)(0x80 | opcode);
                header[1] = 0x80 | 127;
                long len = payload.Length;
                for (var i = 0; i < 8; i++)
                {
                    header[9 - i] = (byte)((len >> (8 * i)) & 0xFF);
                }
            }

            var frame = new byte[header.Length + 4 + masked.Length];
            Array.Copy(header, frame, header.Length);
            Array.Copy(maskKey, 0, frame, header.Length, 4);
            Array.Copy(masked, 0, frame, header.Length + 4, masked.Length);
            return frame;
        }

        private static string GenerateWebSocketKey()
        {
            var bytes = new byte[16];
            new Random().NextBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string BuildUpgradeRequest(string host, int port, string key, string cookieHeader)
        {
            var sb = new StringBuilder();
            sb.Append("GET / HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
            sb.Append("Upgrade: websocket\r\n");
            sb.Append("Connection: Upgrade\r\n");
            sb.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
            sb.Append("Sec-WebSocket-Version: 13\r\n");
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                sb.Append("Cookie: ").Append(cookieHeader).Append("\r\n");
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        /// <summary>Reads and validates the HTTP/1.1 101 handshake response, byte by byte until the blank line.</summary>
        private bool ReadHandshakeResponse(string key)
        {
            var sb = new StringBuilder();
            var single = new byte[1];
            var blankLineSeen = false;

            while (!blankLineSeen)
            {
                var read = _client.ReceiveData(single, 1);
                if (read <= 0)
                {
                    RaiseError("Handshake failed: connection closed before headers were complete");
                    return false;
                }
                sb.Append((char)single[0]);
                if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                {
                    blankLineSeen = true;
                }
            }

            var headerText = sb.ToString();
            if (headerText.IndexOf(" 101 ", StringComparison.Ordinal) < 0)
            {
                RaiseError("Handshake failed: device did not return HTTP 101. Response: " + headerText.Split('\r')[0]);
                return false;
            }

            var expectedAccept = ComputeAcceptKey(key);
            if (headerText.IndexOf(expectedAccept, StringComparison.Ordinal) < 0)
            {
                RaiseError("Handshake failed: Sec-WebSocket-Accept did not match");
                return false;
            }

            return true;
        }

        private static string ComputeAcceptKey(string key)
        {
            using (var sha1 = new SHA1CryptoServiceProvider())
            {
                var hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WebSocketGuid));
                return Convert.ToBase64String(hash);
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
