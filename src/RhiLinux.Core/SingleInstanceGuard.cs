using System.Security.Cryptography;
using System.Text;

namespace RhiLinux.Core;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
    }

    public bool IsPrimary => ownsMutex;

    public static SingleInstanceGuard Acquire(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Instance identity is required.", nameof(identity));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var mutex = new Mutex(true, $"rhi-linux-{digest[..24]}", out var created);
        return new(mutex, created);
    }

    public void Dispose()
    {
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            ownsMutex = false;
        }
        mutex.Dispose();
    }
}
