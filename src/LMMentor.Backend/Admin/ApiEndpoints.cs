using LMMentor.Backend.Data;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Admin;

public sealed record CreateEndpointRequest(string Name, string Type, string Url, string? AccessToken);

public sealed record RenameModelRequest(string DisplayName);

public sealed record SetModelEnabledRequest(bool Enabled);

/// <summary>Payload de uma janela recorrente de indisponibilidade (validado antes de persistir).</summary>
public sealed record AvailabilityWindowRequest(List<DayOfWeek> DaysOfWeek, string StartTime, string EndTime);

public sealed record SetModelScheduleRequest(List<AvailabilityWindowRequest> BlockedWindows);

public sealed record CreateKeyRequest(string Name, List<string>? AllowedModelIds);

public sealed record UpdateSettingsRequest(bool OllamaCompatibilityEnabled);

/// <summary>
/// Endpoints de administração (protegidos por autenticação): CRUD de endpoints de API,
/// modelos descobertos, chaves de API e resumo de uso para o dashboard.
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        // Endpoints de API upstream.
        group.MapGet("/endpoints", (EndpointService endpoints) => Results.Ok(endpoints.GetAll()));

        group.MapPost("/endpoints", async (CreateEndpointRequest request, EndpointService endpoints, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.BadRequest(new { error = "Name and URL are required." });
            }

            var endpoint = await endpoints.CreateAsync(request.Name, request.Type, request.Url, request.AccessToken ?? "", ct);
            return Results.Ok(endpoint);
        });

        group.MapGet("/endpoints/{id}", (string id, EndpointService endpoints) =>
            endpoints.GetById(id) is { } endpoint ? Results.Ok(endpoint) : Results.NotFound());

        group.MapDelete("/endpoints/{id}", (string id, EndpointService endpoints) =>
            endpoints.Delete(id) ? Results.NoContent() : Results.NotFound());

        // Força a re-verificação do status e a descoberta de novos modelos.
        group.MapPost("/endpoints/{id}/refresh", async (string id, EndpointService endpoints, CancellationToken ct) =>
            await endpoints.RefreshAsync(id, ct) is { } endpoint ? Results.Ok(endpoint) : Results.NotFound());

        // Modelos de um endpoint.
        group.MapGet("/endpoints/{id}/models", (string id, EndpointService endpoints) =>
            Results.Ok(endpoints.GetModelsByEndpoint(id)));

        // Todos os modelos (para a UI listar/consultar em qualquer página).
        group.MapGet("/models", (EndpointService endpoints) => Results.Ok(endpoints.GetAllModelDtos()));

        group.MapPatch("/models/{id}", (string id, RenameModelRequest request, EndpointService endpoints) =>
        {
            try
            {
                return endpoints.RenameModel(id, request.DisplayName ?? "") is { } model
                    ? Results.Ok(model)
                    : Results.NotFound();
            }
            catch (ValidationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPatch("/models/{id}/enabled", (string id, SetModelEnabledRequest request, EndpointService endpoints) =>
            endpoints.SetModelEnabled(id, request.Enabled) is { } model ? Results.Ok(model) : Results.NotFound());

        // Janelas recorrentes de indisponibilidade (ex.: rush hour de um provedor).
        group.MapPatch("/models/{id}/schedule", (string id, SetModelScheduleRequest request, EndpointService endpoints) =>
        {
            var windows = new List<ModelAvailabilityWindow>();
            foreach (var window in request.BlockedWindows ?? new())
            {
                var hasDays = window.DaysOfWeek is { Count: > 0 };
                var hasValidTimes = TimeOnly.TryParse(window.StartTime, out _) && TimeOnly.TryParse(window.EndTime, out _);
                if (!hasDays || !hasValidTimes)
                {
                    return Results.BadRequest(new { error = "Each window needs at least one day and valid start/end times (HH:mm)." });
                }

                windows.Add(new ModelAvailabilityWindow
                {
                    DaysOfWeek = window.DaysOfWeek.Distinct().ToList(),
                    StartTime = window.StartTime,
                    EndTime = window.EndTime,
                });
            }

            return endpoints.SetModelBlockedWindows(id, windows) is { } model ? Results.Ok(model) : Results.NotFound();
        });

        // Chaves de API.
        group.MapGet("/keys", (ApiKeyService keys) => Results.Ok(keys.GetAll()));

        group.MapPost("/keys", (CreateKeyRequest request, ApiKeyService keys) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "Name is required." });
            }

            var key = keys.Create(request.Name, request.AllowedModelIds);
            // O valor completo da chave é retornado apenas aqui, no momento da criação.
            return Results.Ok(new { id = key.Entity.Id, name = key.Entity.Name, key = key.Value, allowedModelIds = key.Entity.AllowedModelIds, createdAt = key.Entity.CreatedAt });
        });

        group.MapPost("/keys/{id}/revoke", (string id, ApiKeyService keys) =>
            keys.Revoke(id) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/keys/{id}", (string id, ApiKeyService keys) =>
            keys.Delete(id) ? Results.NoContent() : Results.NotFound());

        // Configurações globais do serviço.
        group.MapGet("/settings", (ApplicationSettingsService settings) => Results.Ok(settings.Get()));

        group.MapPut("/settings", (UpdateSettingsRequest request, ApplicationSettingsService settings) =>
            Results.Ok(settings.SetOllamaCompatibilityEnabled(request.OllamaCompatibilityEnabled)));

        // Resumo de uso para o dashboard.
        group.MapGet("/usage/summary", (UsageService usage, int? days) =>
            Results.Ok(usage.GetSummary(Math.Clamp(days ?? 7, 1, 90))));

        return app;
    }
}
