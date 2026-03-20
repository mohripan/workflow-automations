using System.Diagnostics.Metrics;

namespace FlowForge.Infrastructure.Telemetry;

public static class FlowForgeMetrics
{
    public const string MeterName = "FlowForge";

    private static readonly Meter _meter = new(MeterName);

    // Counters
    public static readonly Counter<long> JobsCreated =
        _meter.CreateCounter<long>("flowforge.jobs.created");

    public static readonly Counter<long> JobsCompleted =
        _meter.CreateCounter<long>("flowforge.jobs.completed");

    public static readonly Counter<long> JobsFailed =
        _meter.CreateCounter<long>("flowforge.jobs.failed");

    public static readonly Counter<long> TriggersFired =
        _meter.CreateCounter<long>("flowforge.triggers.fired");

    // Histograms
    public static readonly Histogram<double> JobDurationSeconds =
        _meter.CreateHistogram<double>("flowforge.jobs.duration_seconds");
}
