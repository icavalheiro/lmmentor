
namespace LMMentor.Backend.Dev;

/// <summary>
/// Em modo watch (Development + LMMENTOR_DEV_PROXY=true), inicia o Vite dev server e faz proxy
/// de todos os requests não-API para ele. Requests /api/* seguem para os endpoints do backend.
/// </summary>
public sealed class ViteProxyMiddleware
{
    private readonly RequestDelegate _next;

    public ViteProxyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IHttpClientFactory httpClientFactory, ViteDevServer vite)
    {
        // Somente /admin e /admin/* vão para o Vite dev server; todas as demais rotas seguem para o backend.
        var path = context.Request.Path;
        var isAdminRoute = path == "/admin" || path.StartsWithSegments("/admin/");
        if (!isAdminRoute)
        {
            await _next(context);
            return;
        }

        // Aguarda o Vite aceitar conexões antes do primeiro proxy.
        await vite.Ready;

        var client = httpClientFactory.CreateClient();
        var pathAndQuery = (context.Request.Path.Value ?? "/") + context.Request.QueryString.Value;
        var baseUri = new Uri(vite.Url);
        var targetUri = new Uri(baseUri, pathAndQuery);

        using var request = new HttpRequestMessage(HttpMethod.Parse(context.Request.Method), targetUri);

        foreach (var header in context.Request.Headers)
        {
            if (IsHopByHopHeader(header.Key))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var hasBody = !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method);
        if (hasBody)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
        {
            if (IsHopByHopHeader(header.Key))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        if (response.Content is not null)
        {
            foreach (var header in response.Content.Headers)
            {
                if (IsHopByHopHeader(header.Key))
                {
                    continue;
                }

                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        // Remove o Content-Length: o corpo é copiado tal como vem do Vite.
        context.Response.Headers.Remove("Transfer-Encoding");

        if (response.Content is not null)
        {
            await response.Content.CopyToAsync(context.Response.Body);
        }
    }

    private static bool IsHopByHopHeader(string name) => name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailers", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
}
