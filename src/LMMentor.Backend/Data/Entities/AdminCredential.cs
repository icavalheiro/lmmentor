using LiteDB;

namespace LMMentor.Backend.Data.Entities;

/// <summary>Credencial de administrador (bootstrap no primeiro startup).</summary>
public sealed class AdminCredential
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    public string Username { get; set; } = string.Empty;

    /// <summary>Hash PBKDF2-SHA256 da senha, em base64.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public byte[] Salt { get; set; } = [];

    public DateTime CreatedAt { get; set; }
}
