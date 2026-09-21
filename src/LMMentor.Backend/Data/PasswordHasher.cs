using System.Security.Cryptography;

namespace LMMentor.Backend.Data;

/// <summary>Gera e valida hashes de senha com PBKDF2-SHA256.</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static (string hash, byte[] salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Pbkdf2(password, salt);
        return (Convert.ToBase64String(hash), salt);
    }

    public static bool Verify(string password, string expectedHashBase64, byte[] salt)
    {
        if (!Base64TryDecode(expectedHashBase64, out var expected))
        {
            return false;
        }

        var actual = Pbkdf2(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

    private static bool Base64TryDecode(string value, out byte[] result)
    {
        try
        {
            result = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            result = [];
            return false;
        }
    }
}
