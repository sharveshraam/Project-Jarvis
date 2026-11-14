using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Core.Abstractions;

namespace Jarvis.Windows;

/// <summary>
/// Credentials encrypted with Windows DPAPI under the current user account. The file is useless on another
/// machine or to another Windows user, and the key is never written to settings, logs or the database.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Project-JARVIS/secret-store/v1");
    private readonly string _path;
    private readonly object _gate = new object();
    private Dictionary<string, string>? _cache;

    public DpapiSecretStore(string path) => _path = path;

    public string? Get(string name)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(name, out var encoded)) return null;
            try
            {
                var clear = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Copied from another machine/user, or corrupted: treat as "no key configured".
                return null;
            }
        }
    }

    public void Set(string name, string value)
    {
        lock (_gate)
        {
            var map = Load();
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            map[name] = Convert.ToBase64String(cipher);
            Save(map);
        }
    }

    public void Remove(string name)
    {
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(name)) Save(map);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
                if (parsed is not null)
                {
                    foreach (var kv in parsed) map[kv.Key] = kv.Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable store: start empty rather than crash.
        }
        _cache = map;
        return map;
    }

    private void Save(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(map));
        File.Move(temp, _path, overwrite: true);
        _cache = map;
    }
}
