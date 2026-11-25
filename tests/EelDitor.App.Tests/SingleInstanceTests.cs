using EelDitor.App.Services;

namespace EelDitor.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task SecondStart_HandsItsFilesToTheRunningInstance()
    {
        string pipe = "EelDitorTest-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource<string[]>();
        SingleInstance.Listen(files => received.TrySetResult(files), stop.Token, pipe);
        Thread.Sleep(200);

        string file = Path.Combine(Path.GetTempPath(), "zażółć gęślą.txt");
        string repository = Path.Combine("test", "repo z Git");
        string gitTarget = SingleInstance.GitArgumentPrefix + repository;
        Assert.True(SingleInstance.TryHandOver([file, "--some-switch", gitTarget], pipe, checkMutex: false));
        var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([Path.GetFullPath(file), SingleInstance.GitArgumentPrefix + Path.GetFullPath(repository)], files);

        Assert.False(SingleInstance.TryHandOver([file, SingleInstance.NewWindowSwitch], pipe, checkMutex: false));
        stop.Cancel();
    }

    [Fact]
    public void WithoutARunningInstance_TheProgramStartsNormally()
    {
        // The first instance takes the mutex and does not wait for a pipe at all.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SingleInstance.TryHandOver(["x.txt"], "EelDitorTest-" + Guid.NewGuid().ToString("N")));
        Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");
    }
}
