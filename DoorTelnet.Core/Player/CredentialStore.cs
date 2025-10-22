using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace DoorTelnet.Core.Player;

public class CredentialStore
{
    private readonly string _filePath;
    private readonly string _keyPath;
    private readonly object _sync = new();
    private static byte[]? _keyCache; // 32 bytes

    private class CredentialRecord
    {
        public string Username { get; set; } = string.Empty;
        public byte[] ProtectedPassword { get; set; } = Array.Empty<byte>();
    }

    private List<CredentialRecord> _records = new();

    public CredentialStore(string filePath)
    {
        _filePath = filePath;
        _keyPath = Path.Combine(Path.GetDirectoryName(filePath)!, "key.bin");
        LoadKey();
        Load();
    }

    public IEnumerable<string> ListUsernames()
    {
        lock (_sync) return _records.Select(r => r.Username).OrderBy(x => x).ToList();
    }

    public void AddOrUpdate(string username, string plainPassword)
    {
        var protectedBytes = Protect(plainPassword);
        lock (_sync)
        {
            var existing = _records.FirstOrDefault(r => r.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                _records.Add(new CredentialRecord { Username = username, ProtectedPassword = protectedBytes });
            else
                existing.ProtectedPassword = protectedBytes;
            Save();
        }
    }

    public string? GetPassword(string username)
    {
        lock (_sync)
        {
            var rec = _records.FirstOrDefault(r => r.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (rec == null) return null;
            return Unprotect(rec.ProtectedPassword);
        }
    }

    public bool Remove(string username)
    {
        lock (_sync)
        {
            var rec = _records.FirstOrDefault(r => r.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (rec == null) return false;
            _records.Remove(rec);
            Save();
            return true;
        }
    }

    private void LoadKey()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            if (File.Exists(_keyPath))
            {
                _keyCache = File.ReadAllBytes(_keyPath);
            }
            else
            {
                _keyCache = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(_keyPath, _keyCache);
            }
            if (_keyCache!.Length != 32)
            {
                // regenerate if size wrong
                _keyCache = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(_keyPath, _keyCache);
            }
        }
        catch
        {
            // fallback insecure key in memory only
            _keyCache = [.. Encoding.UTF8.GetBytes("DoorTelnetFallbackKeyDoorTelnetFallbackKey").Take(32)];
        }
    }

    private byte[] Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        try
        {
            using var aes = Aes.Create();
            aes.Key = _keyCache!;
            aes.GenerateIV();
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length); // prepend IV
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }
        catch
        {
            return data; // fallback plaintext
        }
    }

    private string Unprotect(byte[] protectedData)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = _keyCache!;
            using var ms = new MemoryStream(protectedData);
            var iv = new byte[aes.BlockSize / 8];
            int read = ms.Read(iv, 0, iv.Length);
            if (read != iv.Length) return string.Empty;
            aes.IV = iv;
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var outMs = new MemoryStream();
            cs.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
        catch
        {
            try { return Encoding.UTF8.GetString(protectedData); } catch { return string.Empty; }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<CredentialRecord>>(json);
            if (list != null) _records = list;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
