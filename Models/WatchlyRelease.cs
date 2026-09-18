using System.Security.Cryptography;
using System.Text;

namespace KategoriSecici.Models;

public static class WatchlyRelease
{
    public const string Product = "Watchly";
    public const string Version = "2.0";
    public const string Author = "Asxeyt";

    private const string SignatureHash = "f280e29807fe4bd1fb4be537c82e086157558460669ddb703cd144d0a97014ae";

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
