using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fusebox.Domain;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Telemetry;

/// <summary>Settings for spotting repeated requests. Section <c>Fusebox:RepeatDetection</c>.</summary>
public sealed class RepeatDetectionOptions
{
    public const string SectionName = "Fusebox:RepeatDetection";

    public bool Enabled { get; set; } = true;

    /// <summary>A request is a repeat if the same tenant sent an identical one to the same route this recently.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many distinct requests are remembered at once. When the limit is reached, new ones are not remembered until old ones expire.</summary>
    public int MaxTracked { get; set; } = 50_000;

    /// <summary>
    /// Headers in which a client states how many times it has already retried this call. Any value above zero marks the
    /// request as a retry. The SDKs of several vendors send <c>X-Stainless-Retry-Count</c>.
    /// </summary>
    public IList<string> RetryHeaders { get; } = ["X-Stainless-Retry-Count"];
}

/// <summary>
/// Spots a request that repeats one already sent, which is the usual signature of a retry loop: the same call, again and again.
/// </summary>
/// <remarks>
/// A request is remembered only as a 128-bit fingerprint of its tenant, route and body, never the body itself, and only for a
/// minute. The memory belongs to one gateway instance, so a retry that lands on another instance is not noticed; with several
/// instances the rate is a lower bound. Identical requests that are not retries (a health check, a fixed prompt) also count.
/// </remarks>
public sealed class RepeatDetector
{
    private readonly RepeatDetectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Fingerprint, long> _lastSeen = new();
    private long _nextSweep;

    public RepeatDetector(IOptions<RepeatDetectionOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _time = time;
    }

    public bool Enabled => _options.Enabled;

    public IReadOnlyList<string> RetryHeaders => [.. _options.RetryHeaders];

    /// <summary>Notes a request and says whether it repeats one seen within the window.</summary>
    /// <param name="declaredRetry">The client said this is a retry.</param>
    public bool Observe(TenantId tenant, string routeId, ReadOnlySpan<byte> body, bool declaredRetry)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(routeId);

        if (!_options.Enabled)
        {
            return false;
        }

        var now = _time.GetTimestamp();
        var window = (long)(_options.Window.TotalSeconds * _time.TimestampFrequency);
        var key = Fingerprint.Of(tenant, routeId, body);

        Sweep(now, window);

        var repeated = _lastSeen.TryGetValue(key, out var seenAt) && now - seenAt <= window;

        if (_lastSeen.ContainsKey(key) || _lastSeen.Count < _options.MaxTracked)
        {
            _lastSeen[key] = now;
        }

        return repeated || declaredRetry;
    }

    // Forgets what is older than the window, at most about once a second, so a quiet gateway does not hold on to old fingerprints.
    private void Sweep(long now, long window)
    {
        var due = Interlocked.Read(ref _nextSweep);

        if (now < due || Interlocked.CompareExchange(ref _nextSweep, now + _time.TimestampFrequency, due) != due)
        {
            return;
        }

        foreach (var (key, seenAt) in _lastSeen)
        {
            if (now - seenAt > window)
            {
                _lastSeen.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct Fingerprint(long High, long Low)
    {
        public static Fingerprint Of(TenantId tenant, string routeId, ReadOnlySpan<byte> body)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(tenant.Value));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(routeId));
            hash.AppendData([0]);
            hash.AppendData(body);

            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            return new Fingerprint(BitConverter.ToInt64(digest[..8]), BitConverter.ToInt64(digest[8..16]));
        }
    }
}
