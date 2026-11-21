using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwelveTestersTray;

/// <summary>
/// Stores the Twelve Testers email + password encrypted with Windows DPAPI
/// (CurrentUser scope): only your Windows account on this PC can decrypt it.
/// </summary>
public static class CredentialStore
{
    static string FilePath => Path.Combine(Settings.Dir, "login.dat");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TwelveTestersTray.login.v1");

    public static bool Exists => File.Exists(FilePath);

    public static (string Email, string Password)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var parts = JsonSerializer.Deserialize<string[]>(raw);
            if (parts is not { Length: 2 }) return null;
            return (parts[0], parts[1]);
        }
        catch
        {
            return null; // unreadable (e.g. copied from another PC/user)
        }
    }

    public static void Save(string email, string password)
    {
        Directory.CreateDirectory(Settings.Dir);
        var raw = JsonSerializer.SerializeToUtf8Bytes(new[] { email, password });
        File.WriteAllBytes(FilePath, ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { /* already gone */ }
    }
}
