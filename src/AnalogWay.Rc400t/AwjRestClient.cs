using System;
using Crestron.SimplSharp.Net.Http;
using Crestron.SimplSharp.Newtonsoft.Json.Linq;

namespace AnalogWay.Rc400t
{
    /// <summary>Outcome of <see cref="AwjRestClient.Login"/>.</summary>
    public class AwjLoginResult
    {
        public bool Success { get; set; }
        public string CookieHeader { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// The short REST leg of the AWJ handshake: check whether the device requires a password,
    /// log in if it does, then hand the resulting session cookie to the websocket client.
    /// This mirrors exactly what WebRCS (and therefore the RC400T) and the AWJ Companion module do
    /// before opening their control websocket.
    /// </summary>
    public class AwjRestClient
    {
        private readonly string _host;
        private readonly int _port;

        public AwjRestClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        private string BaseUrl
        {
            get { return "http://" + _host + ":" + _port; }
        }

        /// <summary>Returns true if the device requires a password to control it.</summary>
        public bool IsAuthenticationEnabled()
        {
            using (var client = new HttpClient())
            {
                var request = new HttpClientRequest { Url = new UrlParser(BaseUrl + "/auth/status"), RequestType = RequestType.Get };
                var response = client.Dispatch(request);
                if (response == null || response.Code != 200)
                {
                    throw new InvalidOperationException("GET /auth/status failed with HTTP " + (response == null ? "no response" : response.Code.ToString()));
                }
                var json = JObject.Parse(response.ContentString);
                var flag = json["authentication"] != null ? json["authentication"]["isAuthenticationEnabled"] : null;
                return flag != null && flag.Type == JTokenType.Boolean && (bool)flag;
            }
        }

        /// <summary>Logs in with the device's admin password and returns the session cookie to reuse on the websocket.</summary>
        public AwjLoginResult Login(string password)
        {
            using (var client = new HttpClient())
            {
                var body = new JObject { ["password"] = password ?? string.Empty }.ToString(Crestron.SimplSharp.Newtonsoft.Json.Formatting.None);
                var request = new HttpClientRequest
                {
                    Url = new UrlParser(BaseUrl + "/auth/login"),
                    RequestType = RequestType.Post,
                    ContentString = body
                };
                request.Header.SetHeaderValue("Content-Type", "application/json");

                var response = client.Dispatch(request);
                if (response == null || response.Code >= 400)
                {
                    return new AwjLoginResult { Success = false, Error = "Login rejected, HTTP " + (response == null ? "no response" : response.Code.ToString()) };
                }

                var cookie = response.Header.GetHeaderValue("Set-Cookie");
                return new AwjLoginResult { Success = true, CookieHeader = cookie };
            }
        }

        /// <summary>
        /// Downloads the full device state ("device" branch of the AWJ object tree). Useful for
        /// reading the current model/firmware/serial once, and as a fallback when the websocket's own
        /// INIT snapshot is not needed. Can be a multi-hundred-kilobyte response on a fully configured
        /// processor with many memories, so callers on constrained processors may prefer to skip this
        /// and rely on the websocket INIT snapshot alone.
        /// </summary>
        public string GetDeviceState(string cookieHeader)
        {
            using (var client = new HttpClient())
            {
                var request = new HttpClientRequest { Url = new UrlParser(BaseUrl + "/api/stores/device"), RequestType = RequestType.Get };
                if (!string.IsNullOrEmpty(cookieHeader))
                {
                    request.Header.SetHeaderValue("Cookie", cookieHeader);
                }
                var response = client.Dispatch(request);
                if (response == null || response.Code != 200)
                {
                    throw new InvalidOperationException("GET /api/stores/device failed with HTTP " + (response == null ? "no response" : response.Code.ToString()));
                }
                return response.ContentString;
            }
        }
    }
}
