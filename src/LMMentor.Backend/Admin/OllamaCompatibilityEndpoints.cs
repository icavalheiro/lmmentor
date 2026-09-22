using LMMentor.Backend.Data;

namespace LMMentor.Backend.Admin;

/// <summary>
/// Endpoints mínimos da API Ollama usados pelo provider BYOK nativo do VS Code.
/// As respostas de chat continuam usando a API OpenAI-compatible em /v1.
/// </summary>
public static class OllamaCompatibilityEndpoints
{
    private const int DefaultContextSize = 32_768;

    public static IEndpointRouteBuilder MapOllamaCompatibilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", IResult (ApplicationSettingsService settings) =>
            settings.OllamaCompatibilityEnabled ? Results.Ok(new { version = "0.6.4" }) : Results.NotFound());

        app.MapGet("/api/tags", IResult (EndpointService endpoints, ApplicationSettingsService settings) =>
        {
            if (!settings.OllamaCompatibilityEnabled)
            {
                return Results.NotFound();
            }

            var models = endpoints.GetAllModelDtos()
                .Where(model => model.Enabled)
                .Select(model => new
                {
                    model = EffectiveName(model),
                    name = EffectiveName(model),
                });

            return Results.Ok(new { models });
        });

        app.MapPost("/api/show", IResult (OllamaShowRequest request, EndpointService endpoints, ApplicationSettingsService settings) =>
        {
            if (!settings.OllamaCompatibilityEnabled)
            {
                return Results.NotFound();
            }

            var requestedModel = request.Model?.Trim();
            if (string.IsNullOrWhiteSpace(requestedModel))
            {
                return Results.BadRequest(new { error = "Model is required." });
            }

            var model = endpoints.GetAllModelDtos().FirstOrDefault(candidate =>
                candidate.Enabled && string.Equals(EffectiveName(candidate), requestedModel, StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                return Results.NotFound(new { error = $"Model '{requestedModel}' not found." });
            }

            var modelName = EffectiveName(model);
            var contextSize = model.ContextSize ?? DefaultContextSize;
            return Results.Ok(new
            {
                template = string.Empty,
                capabilities = new[] { "tools" },
                details = new { family = "lmmentor" },
                remote_model = modelName,
                model_info = new Dictionary<string, object>
                {
                    ["general.basename"] = modelName,
                    ["general.architecture"] = "lmmentor",
                    ["lmmentor.context_length"] = contextSize,
                },
            });
        });

        return app;
    }

    private static string EffectiveName(ModelDto model) =>
        string.IsNullOrWhiteSpace(model.DisplayName) ? model.UpstreamModelId : model.DisplayName;
}

/// <summary>Payload da consulta de detalhes de um modelo pela API Ollama.</summary>
public sealed record OllamaShowRequest(string? Model);