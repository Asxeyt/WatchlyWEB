using System.Security.Cryptography;
using System.Text;

namespace KategoriSecici.Models;

public static class WatchlyRelease
{
    public const string Product = "Watchly";
    public const string Version = "1.1";
    public const string Author = "Asxeyt";

    private const string SignatureHash = "98b562e4080ebd4942e3141237fe72acd515ccdf7887846afaaddb20410ce588";

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
