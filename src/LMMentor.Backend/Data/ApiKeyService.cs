using System.Security.Cryptography;
using System.Text;
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

public sealed record CreatedApiKey(ApiKeyEntity Entity, string Value);

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
        MigrateLegacyKeys();
    }

    public IReadOnlyList<ApiKeyDto> GetAll() =>
        _collection.FindAll()
            .OrderByDescending(k => k.CreatedAt)
            .Select(ToMaskedDto)
            .ToList();

    /// <summary>Cria uma chave e retorna o valor completo (exibido uma única vez).</summary>
    public CreatedApiKey Create(string name, List<string>? allowedModelIds)
    {
        var value = GenerateKey();
        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            KeyHash = HashKey(value),
            KeyPreview = MaskKey(value),
            AllowedModelIds = (allowedModelIds is { Count: > 0 }) ? allowedModelIds : null,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = null,
        };

        _collection.Insert(entity);
        return new CreatedApiKey(entity, value);
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

    /// <summary>Retorna a entidade correspondente para validação de requisições do relay.</summary>
    public ApiKeyEntity? FindByKeyValue(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var hash = HashKey(key);
        return _collection.FindAll().FirstOrDefault(candidate => HashesMatch(candidate.KeyHash, hash));
    }

    private static ApiKeyDto ToMaskedDto(ApiKeyEntity k) =>
        new(k.Id, k.Name, k.KeyPreview, k.AllowedModelIds, ToUtc(k.CreatedAt), k.RevokedAt is { } r ? ToUtc(r) : null);

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

    private void MigrateLegacyKeys()
    {
        foreach (var key in _collection.FindAll().Where(key => string.IsNullOrEmpty(key.KeyHash) && !string.IsNullOrEmpty(key.Key)))
        {
            key.KeyHash = HashKey(key.Key!);
            key.KeyPreview = MaskKey(key.Key!);
            key.Key = null;
            _collection.Update(key);
        }
    }

    private static bool HashesMatch(string storedHash, string suppliedHash) =>
        storedHash.Length == suppliedHash.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedHash), Encoding.UTF8.GetBytes(suppliedHash));

    private static string HashKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string GenerateKey() =>
        $"sk-lm-{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
}
