using System.Runtime.CompilerServices;
using Azure.Core;

namespace Microsoft.Extensions.AI.Decision1.Internal;

/// <summary>
/// Caches one Entra ID access token and refreshes it shortly before expiry. Thread-safe; concurrent
/// callers share a single refresh.
/// </summary>
internal sealed class Decision1TokenCache(TokenCredential credential, string scope, TimeProvider timeProvider)
{
    /// <summary>Tokens are refreshed when fewer than this many minutes remain.</summary>
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TokenRequestContext _context = new([scope]);
    // A reference holder, so concurrent readers never see a torn AccessToken struct.
    private volatile StrongBox<AccessToken>? _token;

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetFresh(out var cached))
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(out cached))
                return cached;

            var token = await credential.GetTokenAsync(_context, cancellationToken).ConfigureAwait(false);
            _token = new StrongBox<AccessToken>(token);
            return token.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached token, for example after the service answers 401.</summary>
    public void Invalidate() => _token = null;

    private bool TryGetFresh(out string token)
    {
        var current = _token;
        if (current is not null)
        {
            var t = current.Value;
            var now = timeProvider.GetUtcNow();
            var refreshAt = t.RefreshOn is { } refreshOn && refreshOn < t.ExpiresOn ? refreshOn : t.ExpiresOn - RefreshMargin;
            if (now < refreshAt)
            {
                token = t.Token;
                return true;
            }
        }
        token = "";
        return false;
    }
}
