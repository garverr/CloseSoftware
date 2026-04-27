using System.Collections.Concurrent;

namespace FinancialReporting.Api.Services;

/// <summary>
/// Per-tenant lock around token-refresh operations. Without this, two concurrent paths
/// (background ledger worker + manual sync, or two manual syncs) can each read the same
/// stale refresh token, both POST to /connect/token, and one consumes the other's rotation —
/// corrupting the stored refresh token. Xero rotates refresh tokens on every refresh, so a
/// race produces a permanently-broken connection. Cat 1.
/// </summary>
public sealed class XeroTokenRefreshLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        }

        var semaphore = _locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
