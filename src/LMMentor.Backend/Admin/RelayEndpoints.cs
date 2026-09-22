using System.Net;
using LMMentor.Backend.Data;

namespace LMMentor.Backend.Admin;

/// <summary>
/// API pública OpenAI-compatible do relay: /v1/models e /v1/chat/completions.
/// A autenticação é feita pela chave Bearer (sk-lm-...) emitida no admin, sem cookie.
/// </summary>
public static class RelayEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1");

        // Lista os modelos habilitados (nome exposto), no formato do OpenAI.
        group.MapGet("/models", (HttpRequest request, RelayService relay) =>
        {
            var apiKeyValue = ExtractBearerToken(request);
            if (apiKeyValue is null)
            {
                return Error(HttpStatusCode.Unauthorized, "Missing API key. Use Authorization: Bearer sk-lm-...");
            }

            try
            {
                return Results.Json(relay.ListModels(apiKeyValue));
            }
            catch (RelayException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }
        });

        // Chat completion: encaminha para o endpoint upstream e registra o uso.
        group.MapPost("/chat/completions", async (HttpRequest request, HttpResponse response, RelayService relay, CancellationToken ct) =>
        {
            var apiKeyValue = ExtractBearerToken(request);
            if (apiKeyValue is null)
            {
                return Error(HttpStatusCode.Unauthorized, "Missing API key. Use Authorization: Bearer sk-lm-...");
            }

            // Lê o corpo uma única vez; é reutilizado na resolução e no encaminhamento.
            string raw;
            try
            {
                using var reader = new StreamReader(request.Body);
                raw = await reader.ReadToEndAsync(ct);
            }
            catch (Exception)
            {
                return Error(HttpStatusCode.BadRequest, "Could not read the request body.");
            }

            string model;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(HttpStatusCode.BadRequest, "Invalid JSON body.");
            }

            ResolvedModel resolved;
            try
            {
                resolved = relay.Resolve(apiKeyValue, model);
            }
            catch (RelayException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }

            RelayResponse relayResponse;
            try
            {
                relayResponse = await relay.RelayChatAsync(resolved, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)), ct);
            }
            catch (Exception)
            {
                // Falha de rede/timeout ao falar com o upstream.
                return Error(HttpStatusCode.BadGateway, "Failed to reach the upstream endpoint.");
            }

            await WriteRelayResponse(relayResponse, response);
            return Results.Empty;
        });

        return app;
    }

    private static string? ExtractBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return header["Bearer ".Length..].Trim();
    }

    /// <summary>Formato de erro compatível com o OpenAI: { error: { message, type, code } }.</summary>
    private static IResult Error(HttpStatusCode status, string message)
    {
        string? code = null;
        return Results.Json(new { error = new { message, type = "invalid_request_error", code } }, statusCode: (int)status);
    }

    private static async Task WriteRelayResponse(RelayResponse response, HttpResponse http)
    {
        http.StatusCode = (int)response.StatusCode;
        http.ContentType = response.ContentType;
        try
        {
            await response.Body.CopyToAsync(http.Body);
        }
        catch (Exception)
        {
            // Falha no meio do stream: a resposta parcial já foi entregue ao cliente.
        }

        // O descarte dispara o registro de uso do stream SSE (RelayingStream.Complete).
        try
        {
            response.Body.Dispose();
        }
        catch
        {
            // Ignora: a resposta ao cliente já foi entregue.
        }
    }
}
