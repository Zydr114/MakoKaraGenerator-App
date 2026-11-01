namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class ServerShutdownTests
{
    [Fact]
    public async Task Failed_preview_drain_reports_failure_and_still_disposes_remaining_resources()
    {
        List<string> stages = [];
        using StringWriter errors = new();
        bool succeeded = await ServerShutdown.DisposeAsync(
            () => { stages.Add("preview"); return ValueTask.FromException(new AggregateException(new InvalidOperationException("private detail"))); },
            () => { stages.Add("sessions"); return ValueTask.CompletedTask; },
            () => stages.Add("render"), errors);
        Assert.False(succeeded);
        Assert.Equal(["preview", "sessions", "render"], stages);
        Assert.Contains("BACKEND_SHUTDOWN_FAILED preview AggregateException", errors.ToString());
        Assert.DoesNotContain("private detail", errors.ToString());
    }

    [Fact]
    public async Task Successful_drain_reports_success()
    {
        using StringWriter errors = new();
        Assert.True(await ServerShutdown.DisposeAsync(() => ValueTask.CompletedTask, () => ValueTask.CompletedTask, () => { }, errors));
        Assert.Equal(string.Empty, errors.ToString());
    }
}
