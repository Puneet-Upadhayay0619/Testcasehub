using System.Security.Cryptography;
using System.Text;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Services;

public class ApiKeyService
{
    private readonly IDataStore _store;
    public ApiKeyService(IDataStore store) => _store = store;

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    // Returns the raw key (shown to the Admin exactly once) and the persisted record.
    public async Task<(string RawKey, ApiKey Key)> IssueAsync(string name, string scope, string createdBy)
    {
        var raw = "tch_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var key = await _store.CreateApiKeyAsync(new ApiKey { Name = name, KeyHash = Hash(raw), Scope = scope, CreatedBy = createdBy });
        return (raw, key);
    }

    // No expiry (explicit decision) — valid until an Admin revokes it.
    public async Task<ApiKey?> ValidateAsync(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;
        var key = await _store.GetApiKeyByHashAsync(Hash(rawKey));
        if (key is null || key.Revoked) return null;
        key.LastUsedAt = DateTime.UtcNow;
        await _store.UpdateApiKeyAsync(key);
        return key;
    }
}
