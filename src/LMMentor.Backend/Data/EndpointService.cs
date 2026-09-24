using LiteDB;
using Microsoft.AspNetCore.Http;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Modelo retornado pela API (projeção da entidade para a UI).</summary>
public sealed record ModelDto(
    string Id,
    string EndpointId,
    string UpstreamModelId,
    string DisplayName,
    int? ContextSize,
    int? MaxOutputTokens,
    bool Enabled,
    IReadOnlyList<ModelAvailabilityWindow> BlockedWindows);

/// <summary>Erro de validação com status HTTP associado (ex.: nome duplicado).</summary>
public sealed class ValidationException(string message) : Exception(message)
{
    public int StatusCode { get; } = StatusCodes.Status409Conflict;
}

/// <summary>
/// Gerencia endpoints de API e os modelos descobertos neles. A criação/refresh de um
/// endpoint dispara a descoberta real via HTTP e sincroniza a coleção de modelos,
/// preservando customizações (nome exposto, habilitação) dos modelos já existentes.
/// </summary>
public sealed class EndpointService
{
    private const string EndpointsCollection = "api_endpoints";
    private const string ModelsCollection = "models";

    private readonly LMMentorDb _db;
    private readonly ModelDiscoveryService _discovery;
    private readonly ILiteCollection<ApiEndpointEntity> _endpoints;
    private readonly ILiteCollection<ModelEntity> _models;

    public EndpointService(LMMentorDb db, ModelDiscoveryService discovery)
    {
        _db = db;
        _discovery = discovery;
        _endpoints = db.Db.GetCollection<ApiEndpointEntity>(EndpointsCollection);
        _models = db.Db.GetCollection<ModelEntity>(ModelsCollection);
    }

    public IReadOnlyList<ApiEndpointEntity> GetAll() =>
        _endpoints.FindAll().Select(e => Normalize(e)!).OrderBy(e => e.Name).ToList();

    public ApiEndpointEntity? GetById(string id) => Normalize(_endpoints.FindById(id));

    /// <summary>Normaliza os DateTime para UTC (o LiteDB retorna Kind=Local após round-trip).</summary>
    private static ApiEndpointEntity? Normalize(ApiEndpointEntity? e)
    {
        if (e is null)
        {
            return null;
        }

        // O LiteDB 5 persiste strings vazias como null; normaliza para manter o contrato não-nulo.
        e.Name = NullToEmpty(e.Name);
        e.Type = NullToEmpty(e.Type);
        e.Url = NullToEmpty(e.Url);
        e.AccessToken = NullToEmpty(e.AccessToken);
        e.Status = NullToEmpty(e.Status);
        e.LastCheckedAt = ToUtc(e.LastCheckedAt);
        e.CreatedAt = ToUtc(e.CreatedAt);
        return e;
    }

    private static string NullToEmpty(string? value) => value ?? string.Empty;

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    public IReadOnlyList<ModelDto> GetModelsByEndpoint(string endpointId) =>
        _models.FindAll()
            .Where(m => m.EndpointId == endpointId)
            .Select(ToDto)
            .OrderBy(m => m.UpstreamModelId)
            .ToList();

    /// <summary>Todos os modelos (usado na validação de nomes únicos entre endpoints).</summary>
    public IReadOnlyList<ModelEntity> GetAllModels() => _models.FindAll().ToList();

    /// <summary>Todos os modelos como DTO (para a UI listar/consultar em qualquer página).</summary>
    public IReadOnlyList<ModelDto> GetAllModelDtos() =>
        _models.FindAll().Select(ToDto).OrderBy(m => m.UpstreamModelId).ToList();

    /// <summary>Cria um endpoint, dispara a descoberta e persiste os modelos encontrados.</summary>
    public async Task<ApiEndpointEntity> CreateAsync(
        string name, string type, string url, string accessToken, CancellationToken ct)
    {
        var entity = new ApiEndpointEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Type = type,
            Url = url.Trim(),
            AccessToken = accessToken ?? string.Empty,
            Status = "offline",
            LastCheckedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };

        _endpoints.Insert(entity);
        await RefreshAsync(entity.Id, ct);
        return GetById(entity.Id)!;
    }

    /// <summary>Remove o endpoint e todos os modelos descobertos nele.</summary>
    public bool Delete(string id)
    {
        var deleted = _endpoints.Delete(id);
        if (deleted)
        {
            foreach (var model in _models.FindAll().Where(m => m.EndpointId == id).ToList())
            {
                _models.Delete(model.Id);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Força a re-verificação do status e a descoberta de modelos: adiciona novos,
    /// remove os que sumiram do provedor e preserva as customizações dos existentes.
    /// Modelos novos entram desabilitados, pois expor um modelo é escolha explícita
    /// do usuário. O contexto é reavaliado a cada refresh, pois o provedor pode alterá-lo.
    /// </summary>
    public async Task<ApiEndpointEntity?> RefreshAsync(string id, CancellationToken ct)
    {
        var endpoint = _endpoints.FindById(id);
        if (endpoint is null)
        {
            return null;
        }

        var (online, discovered) = await _discovery.DiscoverAsync(endpoint, ct);

        var existing = _models.FindAll().Where(m => m.EndpointId == id).ToList();
        var byUpstreamId = existing.ToDictionary(m => m.UpstreamModelId, StringComparer.Ordinal);

        // Modelos que sumiram do provedor são removidos.
        foreach (var model in existing)
        {
            if (!discovered.Any(d => d.UpstreamModelId == model.UpstreamModelId))
            {
                _models.Delete(model.Id);
            }
        }

        // Novos modelos são criados desabilitados; existentes mantêm nome exposto e habilitação.
        foreach (var found in discovered)
        {
            if (byUpstreamId.TryGetValue(found.UpstreamModelId, out var current))
            {
                // Cada update pode mudar o contexto (ex.: modelo recarregado com outro
                // tamanho), então o valor reportado sempre substitui o gravado.
                var changed = false;
                if (found.ContextSize is not null && current.ContextSize != found.ContextSize)
                {
                    current.ContextSize = found.ContextSize;
                    changed = true;
                }

                if (found.MaxOutputTokens is not null && current.MaxOutputTokens != found.MaxOutputTokens)
                {
                    current.MaxOutputTokens = found.MaxOutputTokens;
                    changed = true;
                }

                if (changed)
                {
                    _models.Update(current);
                }

                continue;
            }

            _models.Insert(new ModelEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                EndpointId = id,
                UpstreamModelId = found.UpstreamModelId,
                DisplayName = string.Empty,
                ContextSize = found.ContextSize,
                MaxOutputTokens = found.MaxOutputTokens,
                Enabled = false,
                CreatedAt = DateTime.UtcNow,
            });
        }

        endpoint.Status = online ? "online" : "offline";
        endpoint.LastCheckedAt = DateTime.UtcNow;
        _endpoints.Update(endpoint);
        return Normalize(endpoint);
    }

    /// <summary>
    /// Renomeia o nome exposto de um modelo, garantindo unicidade entre todos os
    /// endpoints. Nome vazio volta a usar o id upstream.
    /// </summary>
    public ModelDto? RenameModel(string modelId, string displayName)
    {
        var model = _models.FindById(modelId);
        if (model is null)
        {
            return null;
        }

        var effectiveName = displayName.Trim();
        var conflict = _models.FindAll().Where(m => m.Id != modelId).FirstOrDefault(m =>
            string.Equals(EffectiveName(m), effectiveName, StringComparison.OrdinalIgnoreCase));

        if (conflict is not null)
        {
            throw new ValidationException("A model with this name already exists on another endpoint.");
        }

        model.DisplayName = effectiveName;
        _models.Update(model);
        return ToDto(model);
    }

    public ModelDto? SetModelEnabled(string modelId, bool enabled)
    {
        var model = _models.FindById(modelId);
        if (model is null)
        {
            return null;
        }

        model.Enabled = enabled;
        _models.Update(model);
        return ToDto(model);
    }

    /// <summary>
    /// Substitui as janelas recorrentes de indisponibilidade do modelo (ex.: rush hour de um
    /// provedor). Lista vazia remove todas as restrições.
    /// </summary>
    public ModelDto? SetModelBlockedWindows(string modelId, List<ModelAvailabilityWindow> windows)
    {
        var model = _models.FindById(modelId);
        if (model is null)
        {
            return null;
        }

        model.BlockedWindows = windows;
        _models.Update(model);
        return ToDto(model);
    }

    /// <summary>Nome efetivamente exposto: alias customizado ou o id upstream.</summary>
    public static string EffectiveName(ModelEntity model) =>
        string.IsNullOrWhiteSpace(model.DisplayName) ? model.UpstreamModelId : model.DisplayName!;

    private static ModelDto ToDto(ModelEntity m) =>
        new(m.Id, m.EndpointId, m.UpstreamModelId, NullToEmpty(m.DisplayName), m.ContextSize, m.MaxOutputTokens, m.Enabled, m.BlockedWindows ?? new());
}
