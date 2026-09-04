using System;
using System.Text;
using Crestron.SimplSharp.CrestronWebSocketClient;

namespace AnalogWay.Rc400t
{
    /// <summary>
    /// Thin wrapper around Crestron's own <see cref="WebSocketClient"/> (shipped in the SIMPL# SDK's
    /// SimplSharpHelperInterface assembly). AWJ devices push and accept JSON state changes only over
    /// a websocket opened at the device root ("/"), so this is the transport the rest of the module
    /// talks to the processor over.
    /// </summary>
    public sealed class AwjWebSocketClient : IDisposable
    {
        private WebSocketClient _client;

        public bool IsConnected { get; private set; }

        /// <summary>Raised once the websocket handshake succeeds and the socket is ready to send/receive.</summary>
        public event Action Connected;

        /// <summary>Raised for every complete text message received from the device.</summary>
        public event Action<string> MessageReceived;

        /// <summary>Raised when the socket closes, whether requested or not.</summary>
        public event Action Disconnected;

        /// <summary>Raised on any connect or socket error. The connection should be considered dead.</summary>
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

            _client = new WebSocketClient
            {
                URL = "ws://" + host + ":" + port + "/",
                SSL = false,
                KeepAlive = true
            };

            // WebSocketClient.AddOnHeader appends one extra raw header line to the HTTP upgrade
            // request - this is where the AWJ session cookie from /auth/login goes when the device
            // requires a password. Unconfirmed against Crestron's own docs (not reachable from this
            // environment) - verify this lands the Cookie header correctly on your first connect.
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                _client.AddOnHeader = "Cookie: " + cookieHeader;
            }

            _client.ConnectionCallBack = OnConnectResult;
            _client.DisconnectCallBack = OnDisconnectResult;
            _client.ReceiveCallBack = OnReceiveResult;

            var result = _client.ConnectAsync();
            if (result != WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_SUCCESS &&
                result != WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_PENDING)
            {
                RaiseError("ConnectAsync failed: " + result);
            }
        }

        public void Disconnect()
        {
            if (_client == null)
            {
                return;
            }
            try
            {
                _client.Disconnect();
                _client.Dispose();
            }
            catch (Exception)
            {
                // best effort
            }
            _client = null;
            if (IsConnected)
            {
                IsConnected = false;
                var handler = Disconnected;
                if (handler != null) handler();
            }
        }

        /// <summary>Sends one text message over the websocket.</summary>
        public void Send(string message)
        {
            if (!IsConnected || _client == null)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(message);
            var result = _client.SendAsync(payload, (uint)payload.Length, WebSocketClient.WEBSOCKET_PACKET_TYPES.LWS_WS_OPCODE_07__TEXT_FRAME);
            if (result != WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_SUCCESS &&
                result != WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_PENDING)
            {
                RaiseError("Send failed: " + result);
            }
        }

        private int OnConnectResult(WebSocketClient.WEBSOCKET_RESULT_CODES error)
        {
            if (error == WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_SUCCESS)
            {
                IsConnected = true;
                var handler = Connected;
                if (handler != null) handler();

                // Arm the first receive; each receive callback re-arms the next one.
                var receiveResult = _client.ReceiveAsync();
                if (receiveResult != WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_SUCCESS &&
                    receiveResult != WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_PENDING)
                {
                    RaiseError("ReceiveAsync failed: " + receiveResult);
                }
            }
            else
            {
                RaiseError("Connect failed: " + error);
            }
            return 0;
        }

        private int OnDisconnectResult(WebSocketClient.WEBSOCKET_RESULT_CODES error, object userObject)
        {
            IsConnected = false;
            var handler = Disconnected;
            if (handler != null) handler();
            return 0;
        }

        private int OnReceiveResult(byte[] data, uint dataLength, WebSocketClient.WEBSOCKET_PACKET_TYPES opcode, WebSocketClient.WEBSOCKET_RESULT_CODES error)
        {
            if (error == WebSocketClient.WEBSOCKET_RESULT_CODES.WEBSOCKET_CLIENT_SUCCESS)
            {
                if (opcode == WebSocketClient.WEBSOCKET_PACKET_TYPES.LWS_WS_OPCODE_07__TEXT_FRAME && data != null)
                {
                    var handler = MessageReceived;
                    if (handler != null) handler(Encoding.UTF8.GetString(data, 0, (int)dataLength));
                }
                // Binary/ping/pong/continuation frames are not expected from an AWJ device and are ignored.
            }
            else
            {
                RaiseError("Receive failed: " + error);
            }

            if (_client != null && IsConnected)
            {
                _client.ReceiveAsync(); // re-arm for the next message
            }
            return 0;
        }

        private void RaiseError(string message)
        {
            var handler = Error;
            if (handler != null) handler(message);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
