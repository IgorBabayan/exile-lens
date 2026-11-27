using System.Globalization;
using System.Net.Http.Headers;
using ExileLens.Application.Market;

namespace ExileLens.Infrastructure.Http;

/// <summary>
/// Client-side enforcement of GGG's dynamic rate limits. Rules are learned from the
/// X-Rate-Limit-Rules / X-Rate-Limit-{Rule} / X-Rate-Limit-{Rule}-State headers and Retry-After on 429.
/// Requests wait (briefly) instead of exceeding a limit; waits longer than <see cref="MaxWait"/> fail fast
/// so the overlay can show a clear message. Limits are never bypassed.
/// </summary>
public sealed class RateLimiter(TimeProvider time)
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(8);

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private sealed class Bucket
    {
        public List<Window> Windows { get; } = [];
        public DateTimeOffset BlockedUntil { get; set; }
        public string Policy { get; set; } = string.Empty;
    }

    internal sealed class Window(int max, TimeSpan period)
    {
        public int Max { get; set; } = max;
        public TimeSpan Period { get; set; } = period;
        public Queue<DateTimeOffset> Hits { get; } = new();
    }

    /// <summary>Waits until a request for <paramref name="endpoint"/> is allowed, then records it.</summary>
    public async Task AcquireAsync(string endpoint, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            string policy;
            lock (_gate)
            {
                var bucket = GetBucket(endpoint);
                var now = time.GetUtcNow();
                wait = ComputeWait(bucket, now);
                policy = bucket.Policy;
                if (wait <= TimeSpan.Zero)
                {
                    foreach (var w in bucket.Windows)
                    {
                        w.Hits.Enqueue(now);
                    }

                    return;
                }
            }

            if (wait > MaxWait)
            {
                throw new TradeRateLimitedException(wait, string.IsNullOrEmpty(policy) ? endpoint : policy);
            }

            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Updates rules and server-side state from a response.</summary>
    public void Observe(string endpoint, HttpResponseHeaders headers, int statusCode)
    {
        lock (_gate)
        {
            var bucket = GetBucket(endpoint);
            var now = time.GetUtcNow();
            if (TryGet(headers, "X-Rate-Limit-Policy", out var policy))
            {
                bucket.Policy = policy;
            }

            if (TryGet(headers, "X-Rate-Limit-Rules", out var rules))
            {
                var parsed = new List<Window>();
                foreach (var rule in rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!TryGet(headers, $"X-Rate-Limit-{rule}", out var limits))
                    {
                        continue;
                    }

                    TryGet(headers, $"X-Rate-Limit-{rule}-State", out var state);
                    var limitParts = limits.Split(',');
                    var stateParts = state?.Split(',') ?? [];
                    for (var i = 0; i < limitParts.Length; i++)
                    {
                        if (!TryParseTriple(limitParts[i], out var max, out var period, out _))
                        {
                            continue;
                        }

                        var w = new Window(max, TimeSpan.FromSeconds(period));
                        if (i < stateParts.Length && TryParseTriple(stateParts[i], out var hits, out _, out var restricted))
                        {
                            // Seed our window with the server's view so we don't overshoot after a restart.
                            for (var h = 0; h < Math.Min(hits, max); h++)
                            {
                                w.Hits.Enqueue(now);
                            }

                            if (restricted > 0)
                            {
                                var until = now.AddSeconds(restricted);
                                if (until > bucket.BlockedUntil)
                                {
                                    bucket.BlockedUntil = until;
                                }
                            }
                        }

                        parsed.Add(w);
                    }
                }

                if (parsed.Count > 0)
                {
                    bucket.Windows.Clear();
                    bucket.Windows.AddRange(parsed);
                }
            }

            if (statusCode == 429)
            {
                var retry = headers.RetryAfter?.Delta
                            ?? (headers.RetryAfter?.Date is { } d ? d - now : (TimeSpan?)null)
                            ?? TimeSpan.FromSeconds(60);
                var until = now + retry;
                if (until > bucket.BlockedUntil)
                {
                    bucket.BlockedUntil = until;
                }
            }
        }
    }

    public TimeSpan RemainingBlock(string endpoint)
    {
        lock (_gate)
        {
            var bucket = GetBucket(endpoint);
            var left = bucket.BlockedUntil - time.GetUtcNow();
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    private Bucket GetBucket(string endpoint)
    {
        if (!_buckets.TryGetValue(endpoint, out var bucket))
        {
            bucket = new Bucket();
            _buckets[endpoint] = bucket;
        }

        return bucket;
    }

    private static TimeSpan ComputeWait(Bucket bucket, DateTimeOffset now)
    {
        var wait = bucket.BlockedUntil - now;
        foreach (var w in bucket.Windows)
        {
            while (w.Hits.Count > 0 && now - w.Hits.Peek() >= w.Period)
            {
                w.Hits.Dequeue();
            }

            // Keep one request of headroom: other tools and the website share the same IP limits.
            var allowed = Math.Max(1, w.Max - 1);
            if (w.Hits.Count >= allowed)
            {
                var oldest = w.Hits.ElementAt(w.Hits.Count - allowed);
                var free = oldest + w.Period - now;
                if (free > wait)
                {
                    wait = free;
                }
            }
        }

        return wait;
    }

    private static bool TryParseTriple(string text, out int a, out int b, out int c)
    {
        a = b = c = 0;
        var parts = text.Split(':');
        return parts.Length == 3
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)
               && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out c);
    }

    private static bool TryGet(HttpResponseHeaders headers, string name, out string value)
    {
        if (headers.TryGetValues(name, out var values))
        {
            value = string.Join(',', values);
            return true;
        }

        value = string.Empty;
        return false;
    }
}
