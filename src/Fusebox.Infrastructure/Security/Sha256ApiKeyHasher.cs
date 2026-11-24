using System.Security.Cryptography;
using System.Text;
using Fusebox.Application.Tenancy;

namespace Fusebox.Infrastructure.Security;

/// <summary>
/// Hashes API keys with SHA-256. Keys are long random strings rather than human passwords,
/// so a fast hash is appropriate, and it makes lookups by hash cheap.
/// </summary>
internal sealed class Sha256ApiKeyHasher : IApiKeyHasher
{
    public string Hash(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }
}
