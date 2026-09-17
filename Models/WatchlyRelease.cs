using System.Security.Cryptography;
using System.Text;

namespace KategoriSecici.Models;

public static class WatchlyRelease
{
    public const string Product = "Watchly";
    public const string Version = "1.2";
    public const string Author = "Asxeyt";

    private const string SignatureHash = "f557b9cf9beddf5db1fa1ee79233ab7d644a11c5419f8cf6d10c9dd48fdc466d";

    public static string Display => $"{Product} v{Version} · {Author}";

    public static void EnsureSignatureIntegrity()
    {
        var payload = Encoding.UTF8.GetBytes($"{Product}|{Version}|{Author}");
        var actualHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash),
                Encoding.ASCII.GetBytes(SignatureHash)))
        {
            throw new InvalidOperationException("Watchly release signature is invalid.");
        }
    }
}
