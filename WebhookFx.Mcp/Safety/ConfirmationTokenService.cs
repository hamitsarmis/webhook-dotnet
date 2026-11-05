using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookFx.Mcp.Safety;

public enum TokenCheck
{
    Valid,

    /// <summary>Not a token this server could have issued.</summary>
    Malformed,

    /// <summary>The signature does not cover these arguments: they differ from the preview, or the token was altered.</summary>
    Mismatch,

    Expired,

    /// <summary>The token was already redeemed once.</summary>
    AlreadyUsed,
}

/// <param name="Nonce">The token's unique part. Set only when the check is Valid.</param>
public sealed record TokenRedemption(TokenCheck Check, string? Nonce = null);

/// <summary>
/// Confirmation tokens tie a write to the preview that came before it. A token is an
/// HMAC-SHA256 over the canonical arguments of the write, lives for 60 seconds and can be
/// redeemed once. The signing secret is random per process, so tokens die with the server.
/// </summary>
public sealed class ConfirmationTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private const string Version = "v1";

    private readonly TimeProvider _clock;
    private readonly byte[] _secret;
    private readonly ConcurrentDictionary<string, long> _redeemed = new();

    public ConfirmationTokenService(TimeProvider clock, byte[]? secret = null)
    {
        _clock = clock;
        _secret = secret ?? RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>Issues a token for exactly these arguments.</summary>
    public string Issue(string canonicalArguments)
    {
        var expires = _clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var header = $"{Version}.{expires.ToString(CultureInfo.InvariantCulture)}.{nonce}";
        return $"{header}.{Sign(header, canonicalArguments)}";
    }

    /// <summary>
    /// Checks a token against the arguments of the write it is presented with and, when it is
    /// valid, marks it as used. The signature is checked before anything else is revealed.
    /// </summary>
    public TokenRedemption Redeem(string? token, string canonicalArguments)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 4 }
            || parts[0] != Version
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || parts[2].Length != 32)
        {
            return new TokenRedemption(TokenCheck.Malformed);
        }

        var nonce = parts[2];
        var expected = Sign($"{parts[0]}.{parts[1]}.{nonce}", canonicalArguments);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.UTF8.GetBytes(parts[3])))
            return new TokenRedemption(TokenCheck.Mismatch);

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (now > expires)
            return new TokenRedemption(TokenCheck.Expired);

        Forget(now);
        if (!_redeemed.TryAdd(nonce, expires))
            return new TokenRedemption(TokenCheck.AlreadyUsed);

        return new TokenRedemption(TokenCheck.Valid, nonce);
    }

    private string Sign(string header, string canonicalArguments)
    {
        var mac = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes($"{header}\n{canonicalArguments}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Drops redeemed nonces whose tokens have expired anyway.</summary>
    private void Forget(long now)
    {
        foreach (var (nonce, expires) in _redeemed)
        {
            if (expires < now)
                _redeemed.TryRemove(nonce, out _);
        }
    }
}
