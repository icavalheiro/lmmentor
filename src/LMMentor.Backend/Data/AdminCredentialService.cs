using System.Security.Cryptography;
using LiteDB;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>
/// Gerencia a credencial de administrador: bootstrap no primeiro startup e validação de login.
/// </summary>
public sealed class AdminCredentialService
{
    private const string CollectionName = "admin_credentials";
    private const int PasswordLength = 16;

    private readonly LMMentorDb _db;
    private readonly ILiteCollection<AdminCredential> _collection;

    public AdminCredentialService(LMMentorDb db)
    {
        _db = db;
        _collection = db.Db.GetCollection<AdminCredential>(CollectionName);
    }

    /// <summary>
    /// Garante que existe uma credencial de administrador. No primeiro startup, gera
    /// usuário e senha aleatórios e retorna-os para exibição no console.
    /// </summary>
    public (string username, string? generatedPassword) EnsureBootstrapped()
    {
        var existing = _collection.FindAll().FirstOrDefault();
        if (existing is not null)
        {
            return (existing.Username, null);
        }

        const string username = "admin";
        var password = GeneratePassword(PasswordLength);
        var (hash, salt) = PasswordHasher.Hash(password);

        _collection.Insert(new AdminCredential
        {
            Username = username,
            PasswordHash = hash,
            Salt = salt,
            CreatedAt = DateTime.UtcNow,
        });

        return (username, password);
    }

    public bool Verify(string username, string password)
    {
        var credential = _collection.FindAll().FirstOrDefault();
        if (credential is null || !string.Equals(credential.Username, username, StringComparison.Ordinal))
        {
            return false;
        }

        return PasswordHasher.Verify(password, credential.PasswordHash, credential.Salt);
    }

    private static string GeneratePassword(int length)
    {
        // Alfabeto sem caracteres ambíguos (0/O, 1/l/I).
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
