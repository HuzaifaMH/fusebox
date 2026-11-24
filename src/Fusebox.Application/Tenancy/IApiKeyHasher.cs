namespace Fusebox.Application.Tenancy;

/// <summary>Port: one-way hashing of tenant API keys, so raw keys are never stored or compared.</summary>
public interface IApiKeyHasher
{
    string Hash(string apiKey);
}
