using System.Net;
using LMMentor.Backend.Data;

namespace LMMentor.Backend.Admin;

/// <summary>
/// API pública do relay, em dois formatos: OpenAI-compatible (/v1/models e /v1/chat/completions)
/// e Anthropic Messages (/v1/messages; o /v1/models responde no formato Anthropic quando o
/// request traz o header anthropic-version). A autenticação é pela chave sk-lm-... emitida no
/// admin, via Authorization: Bearer ou x-api-key (o Claude Code usa as duas formas).
/// </summary>
public static class RelayEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1");

        // Lista os modelos habilitados (nome exposto). O formato depende do header
        // anthropic-version: presente → Anthropic ModelInfo (descoberta do Claude Code);
        // ausente → OpenAI (formato histórico, imutável para clientes existentes).
        group.MapGet("/models", (HttpRequest request, RelayService relay) =>
        {
            var apiKeyValue = ExtractApiKey(request);

            try
            {
                var isAnthropicClient = request.Headers.ContainsKey("anthropic-version");
                return Results.Json(isAnthropicClient ? relay.ListAnthropicModels(apiKeyValue) : relay.ListModels(apiKeyValue));
            }
            catch (RelayException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }
        });

        // Chat completion OpenAI: encaminha para o endpoint upstream e registra o uso.
        group.MapPost("/chat/completions", async (HttpRequest request, HttpResponse response, RelayService relay, CancellationToken ct) =>
        {
            var apiKeyValue = ExtractApiKey(request);

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

            ResolvedModel resolved;
            RelayResponse relayResponse;
            try
            {
                resolved = ParseAndResolve(raw, apiKeyValue, relay);
                relayResponse = await relay.RelayChatAsync(resolved, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)), ct);
            }
            catch (RelayException ex)
            {
                return Error(ex.StatusCode, ex.Message);
            }
            catch (System.Text.Json.JsonException)
            {
                return Error(HttpStatusCode.BadRequest, "Invalid JSON body.");
            }
            catch (Exception)
            {
                // Falha de rede/timeout ao falar com o upstream.
                return Error(HttpStatusCode.BadGateway, "Failed to reach the upstream endpoint.");
            }

            await WriteRelayResponse(relayResponse, response);
            return Results.Empty;
        });

        // Anthropic Messages: usado pelo Claude Code (ANTHROPIC_BASE_URL) e por qualquer
        // cliente no formato Anthropic. Upstream Anthropic → pass-through; OpenAI → conversão.
        group.MapPost("/messages", async (HttpRequest request, HttpResponse response, RelayService relay, CancellationToken ct) =>
        {
            var apiKeyValue = ExtractApiKey(request);

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

            ResolvedModel resolved;
            RelayResponse relayResponse;
            try
            {
                resolved = ParseAndResolve(raw, apiKeyValue, relay);
                var clientBeta = request.Headers["anthropic-beta"].ToString();
                relayResponse = await relay.RelayMessagesAsync(
                    resolved, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)),
                    string.IsNullOrWhiteSpace(clientBeta) ? null : clientBeta, ct);
            }
            catch (RelayException ex)
            {
                return AnthropicError(ex.StatusCode, ex.Message);
            }
            catch (System.Text.Json.JsonException)
            {
                return AnthropicError(HttpStatusCode.BadRequest, "Invalid JSON body.");
            }
            catch (Exception)
            {
                // Falha de rede/timeout ao falar com o upstream.
                return AnthropicError(HttpStatusCode.BadGateway, "Failed to reach the upstream endpoint.");
            }

            await WriteRelayResponse(relayResponse, response);
            return Results.Empty;
        });

        return app;
    }

    /// <summary>Chave do cliente: Authorization: Bearer ou header x-api-key (Claude Code).</summary>
    private static string? ExtractApiKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var xApiKey = request.Headers["x-api-key"].ToString();
        return string.IsNullOrWhiteSpace(xApiKey) ? null : xApiKey.Trim();
    }

    /// <summary>Lê o campo "model" do corpo e resolve o modelo + chave via relay.</summary>
    private static ResolvedModel ParseAndResolve(string raw, string? apiKeyValue, RelayService relay)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        return relay.Resolve(apiKeyValue, model);
    }

    /// <summary>Formato de erro compatível com o OpenAI: { error: { message, type, code } }.</summary>
    private static IResult Error(HttpStatusCode status, string message)
    {
        string? code = null;
        return Results.Json(new { error = new { message, type = "invalid_request_error", code } }, statusCode: (int)status);
    }

    /// <summary>Formato de erro da API Anthropic: { type: "error", error: { type, message } }.</summary>
    private static IResult AnthropicError(HttpStatusCode status, string message)
    {
        var type = status switch
        {
            HttpStatusCode.Unauthorized => "authentication_error",
            HttpStatusCode.Forbidden => "permission_error",
            HttpStatusCode.NotFound => "not_found_error",
            _ => "api_error",
        };

        return Results.Json(new { type = "error", error = new { type, message } }, statusCode: (int)status);
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
