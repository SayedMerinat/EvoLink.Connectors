using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client.Tests
{
    /// <summary>
    /// HTTP handler recording the JSON-RPC requests and answering through <see cref="Respond"/>.
    /// </summary>
    internal sealed class FakeHttpHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string? MediaType, string Body)> Requests { get; } = [];

        /// <summary>Answer of a request (its body parsed), by default a JSON-RPC result computed by <see cref="Result"/>.</summary>
        public Func<JObject, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }

        /// <summary>Result of the JSON-RPC calls when <see cref="Respond"/> is not replaced: uid 7 for authenticate, true otherwise.</summary>
        public Func<JObject, JToken> Result { get; set; } = body => (string?)body["params"]!["method"] == "authenticate" ? 7 : true;

        public FakeHttpHandler()
        {
            Respond = (body, _) => Task.FromResult(Json(new JObject { ["jsonrpc"] = "2.0", ["id"] = body["id"], ["result"] = Result(body) }));
        }

        public IEnumerable<JObject> Bodies => Requests.Select(r => JObject.Parse(r.Body));

        public IEnumerable<JArray> ExecuteKwArgs => Bodies.Where(b => (string?)b["params"]!["method"] == "execute_kw").Select(b => (JArray)b["params"]!["args"]!);

        public static HttpResponseMessage Json(JToken content, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(content.ToString(Formatting.None), Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Error(JToken id, string name, string message, int code = 200)
            => Json(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = "Odoo Server Error",
                    ["data"] = new JObject { ["name"] = name, ["message"] = message, ["debug"] = "Traceback (most recent call last): ...", ["arguments"] = new JArray(message) },
                },
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);

            lock (Requests)
                Requests.Add((request.RequestUri!, request.Content.Headers.ContentType?.MediaType, body));

            return await Respond(JObject.Parse(body), cancellationToken);
        }
    }
}
