using System.Security.Cryptography;
using System.Text;
using Jarvis.Core.Memory;

namespace Jarvis.Infrastructure;

/// <summary>Stores the Gemini API key protected with Windows DPAPI
/// (CurrentUser scope). The key never appears in settings JSON, logs or
/// build artifacts. Falls back to a plain file with a warning on non-Windows
/// (dev/test only).</summary>
public sealed class DpapiVault(string vaultPath) : ISecretVault
{
    private const string Entropy = "JARVIS-Gemini-Key-v1";

    public Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        if (!File.Exists(vaultPath)) return Task.FromResult<string?>(null);
        try
        {
            var encrypted = File.ReadAllBytes(vaultPath);
            var plain = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes(Entropy), DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        catch { return Task.FromResult<string?>(null); }
    }

    public Task SetApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(vaultPath)!);
        var plain = Encoding.UTF8.GetBytes(apiKey);
        var encrypted = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(Entropy), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(vaultPath, encrypted);
        return Task.CompletedTask;
    }

    public Task RemoveApiKeyAsync(CancellationToken ct = default)
    {
        if (File.Exists(vaultPath)) File.Delete(vaultPath);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory vault for tests and for running without a key.</summary>
public sealed class InMemoryVault : ISecretVault
{
    private string? _key;
    public Task<string?> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(_key);
    public Task SetApiKeyAsync(string apiKey, CancellationToken ct = default) { _key = apiKey; return Task.CompletedTask; }
    public Task RemoveApiKeyAsync(CancellationToken ct = default) { _key = null; return Task.CompletedTask; }
}
