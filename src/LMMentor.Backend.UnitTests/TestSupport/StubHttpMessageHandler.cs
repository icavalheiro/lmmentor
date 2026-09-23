using System.Net;

namespace LMMentor.Backend.UnitTests.TestSupport;

/// <summary>Handler HTTP controlado pelo teste: responde a partir de rotas registradas.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requisições recebidas, na ordem, com o corpo já materializado.</summary>
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public StubHttpMessageHandler Respond(string url, HttpStatusCode status, string body, string contentType = "application/json")
    {
        _routes[url] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        };

        return this;
    }

    public StubHttpMessageHandler RespondJson(string url, string json) => Respond(url, HttpStatusCode.OK, json);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        var url = request.RequestUri?.ToString() ?? "";
        if (_routes.TryGetValue(url, out var responder))
        {
            return responder(request);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"{{\"error\":\"no stub for {url}\"}}", System.Text.Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Fábrica que devolve sempre um cliente ligado ao handler de teste.</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
