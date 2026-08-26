using ProfileSvr.Common.Accounts;

namespace ProfileSvr.Jobs;

/// <summary>Liveness marker for the credit-posting job, surfaced on GET /health.</summary>
public sealed class JobHeartbeat
{
    private long _lastTickTicks = DateTime.UtcNow.Ticks;
    private long _tickCount;

    public string JobName { get; init; } = "cba-credit-post";

    public void Beat()
    {
        Interlocked.Exchange(ref _lastTickTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _tickCount);
    }

    public DateTime LastTickUtc => new(Interlocked.Read(ref _lastTickTicks), DateTimeKind.Utc);

    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>Healthy while the last tick is within a few intervals (never less than 30s).</summary>
    public bool IsHealthy(int intervalSeconds) =>
        DateTime.UtcNow - LastTickUtc <= TimeSpan.FromSeconds(Math.Max(30, intervalSeconds * 5));
}

/// <summary>
/// Sweeps stored virtual-account credits into the CBA naira account on a short interval
/// (mirrors vliquidity's PostCbaCreditsJob). Config: VirtualAccountCredit:{Enabled, BatchSize,
/// IntervalSeconds, MaxPostAttempts, HeartbeatLogMinutes}.
/// </summary>
public class PostCbaCreditsJob(
    IServiceScopeFactory scopeFactory,
    JobHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<PostCbaCreditsJob> logger) : BackgroundService
{
    private readonly int _batchSize =
        configuration.GetValue("VirtualAccountCredit:BatchSize", 1000);

    private readonly TimeSpan _interval =
        TimeSpan.FromSeconds(configuration.GetValue("VirtualAccountCredit:IntervalSeconds", 2));

    private readonly TimeSpan _heartbeatEvery =
        TimeSpan.FromMinutes(configuration.GetValue("VirtualAccountCredit:HeartbeatLogMinutes", 5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PostCbaCreditsJob started (interval {Interval}s, batch {Batch}).",
            _interval.TotalSeconds, _batchSize);

        var lastHeartbeatLog = DateTime.UtcNow;
        var totalSettled = 0L;

        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Liveness marker on every tick, before any work.
            heartbeat.Beat();

            try
            {
                // Scoped: the processor depends on the scoped DbContext.
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IVirtualAccountCreditProcessor>();

                var settled = await processor.ProcessPendingCreditsAsync(_batchSize, stoppingToken);
                totalSettled += settled;
                if (settled > 0)
                    logger.LogInformation("PostCbaCreditsJob posted {Count} credit(s) to the CBA.", settled);

                // Periodic proof-of-life in the logs.
                if (DateTime.UtcNow - lastHeartbeatLog >= _heartbeatEvery)
                {
                    logger.LogInformation(
                        "PostCbaCreditsJob alive — {Ticks} ticks, {Settled} credit(s) posted so far.",
                        heartbeat.TickCount, totalSettled);
                    lastHeartbeatLog = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down — exit quietly.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PostCbaCreditsJob tick failed.");
            }
        }

        logger.LogInformation("PostCbaCreditsJob stopping.");
    }
}
