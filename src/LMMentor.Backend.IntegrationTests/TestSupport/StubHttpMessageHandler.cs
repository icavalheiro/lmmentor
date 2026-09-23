using System.Net;

namespace LMMentor.Backend.IntegrationTests.TestSupport;

/// <summary>Upstream falso: responde às URLs registradas pelo teste e guarda as requisições.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body, string ContentType)> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public StubHttpMessageHandler Respond(string url, HttpStatusCode status, string body, string contentType = "application/json")
    {
        _routes[url] = (status, body, contentType);
        return this;
    }

    public StubHttpMessageHandler RespondJson(string url, string json) => Respond(url, HttpStatusCode.OK, json);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        var url = request.RequestUri?.ToString() ?? "";
        if (!_routes.TryGetValue(url, out var route))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"{{\"error\":\"no stub for {url}\"}}", System.Text.Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(route.Status)
        {
            Content = new StringContent(route.Body, System.Text.Encoding.UTF8, route.ContentType),
        };
    }
}

/// <summary>Fábrica que sempre devolve um cliente ligado ao upstream falso.</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
