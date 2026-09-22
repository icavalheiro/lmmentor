using System.Security.Cryptography;
using LiteDB;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>Modelo retornado pela API para a UI (chave mascarada).</summary>
public sealed record ApiKeyDto(
    string Id,
    string Name,
    string Key,
    List<string>? AllowedModelIds,
    DateTime CreatedAt,
    DateTime? RevokedAt);

/// <summary>
/// Emite e gerencia chaves de API pública. O valor completo da chave é retornado
/// apenas no momento da criação; nas listagens ele aparece mascarado.
/// </summary>
public sealed class ApiKeyService
{
    private const string CollectionName = "api_keys";

    private readonly LMMentorDb _db;
    private readonly ILiteCollection<ApiKeyEntity> _collection;

    public ApiKeyService(LMMentorDb db)
    {
        _db = db;
        _collection = db.Db.GetCollection<ApiKeyEntity>(CollectionName);
    }

    public IReadOnlyList<ApiKeyDto> GetAll() =>
        _collection.FindAll()
            .OrderByDescending(k => k.CreatedAt)
            .Select(ToMaskedDto)
            .ToList();

    /// <summary>Cria uma chave e retorna o valor completo (exibido uma única vez).</summary>
    public ApiKeyEntity Create(string name, List<string>? allowedModelIds)
    {
        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Key = GenerateKey(),
            AllowedModelIds = (allowedModelIds is { Count: > 0 }) ? allowedModelIds : null,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = null,
        };

        _collection.Insert(entity);
        return entity;
    }

    public bool Revoke(string id)
    {
        var key = _collection.FindById(id);
        if (key is null || key.RevokedAt is not null)
        {
            return false;
        }

        key.RevokedAt = DateTime.UtcNow;
        _collection.Update(key);
        return true;
    }

    public bool Delete(string id) => _collection.Delete(id);

    /// <summary>Retorna a entidade (com chave completa) para validação de requisições do relay.</summary>
    public ApiKeyEntity? FindByKeyValue(string key) =>
        string.IsNullOrEmpty(key) ? null : _collection.FindAll().FirstOrDefault(k => k.Key == key);

    private static ApiKeyDto ToMaskedDto(ApiKeyEntity k) =>
        new(k.Id, k.Name, MaskKey(k.Key), k.AllowedModelIds, ToUtc(k.CreatedAt), k.RevokedAt is { } r ? ToUtc(r) : null);

    /// <summary>Normaliza para UTC (o LiteDB retorna Kind=Local após round-trip).</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>Mascara a chave mantendo o prefixo e os 4 últimos caracteres.</summary>
    private static string MaskKey(string key) =>
        key.Length <= 8 ? "••••" : $"{key[..7]}…{key[^4..]}";

    private static string GenerateKey()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(32);
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return $"sk-lm-{new string(chars)}";
    }
}
