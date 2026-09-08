using ETGDriverApp.Core.Services.Jobs;

namespace ETGDriverApp.Services;

public class NoOpJobStatusWriter : IJobStatusWriter
{
    public event EventHandler<string>? StatusWritten;

    Task IJobStatusWriter.SetStatusAsync(
        string jobId, JobStatus status, OnSiteEvent? evidence, CancellationToken ct)
    {
        StatusWritten?.Invoke(this, $"{jobId} -> {status} ({evidence?.Trigger}/{evidence?.Confidence})");

        return Task.CompletedTask;
    }
}