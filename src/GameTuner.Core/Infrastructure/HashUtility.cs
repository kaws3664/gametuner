using System.Security.Cryptography;

namespace GameTuner.Core.Infrastructure;

public static class HashUtility
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha256Text(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
