using System.Text;
using System.Text.Json;

namespace Alcatraz.Cli.Commands.WhoAmI;

public sealed record JwtClaims(string? Sub, string? Email, string? PreferredUsername);

internal static class JwtPayloadDecoder
{
    public static JwtClaims? TryDecode(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            return new JwtClaims(
                ReadString(root, "sub"),
                ReadString(root, "email"),
                ReadString(root, "preferred_username"));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
