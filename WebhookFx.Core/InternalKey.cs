using System.Security.Cryptography;
using System.Text;

namespace WebhookFx.Core;

/// <summary>
/// The shared secret that lets a caller inside the deployment (the MCP server) reach the
/// webhook endpoint without being on the IP whitelist.
/// </summary>
public static class InternalKey
{
    public const string HeaderName = "X-Internal-Key";
    public const string ConfigurationName = "WEBHOOKFX_INTERNAL_KEY";
    public const int MinimumLength = 32;

    /// <summary>
    /// True only when a key is configured and the presented value equals it. Both sides are
    /// hashed first, so the comparison takes the same time whatever the presented length is.
    /// </summary>
    public static bool Matches(string? presented, string? configured)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(configured))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }

    /// <summary>
    /// Normalises the configured value: null means the internal path is switched off.
    /// A key that is set but too short to be a secret is a configuration error.
    /// </summary>
    public static string? Validate(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        if (configured.Length < MinimumLength)
            throw new InvalidOperationException(
                $"{ConfigurationName} must be at least {MinimumLength} characters. Leave it unset to disable the internal path.");

        return configured;
    }
}
