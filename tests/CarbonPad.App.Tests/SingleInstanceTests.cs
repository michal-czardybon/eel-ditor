using CarbonPad.App.Services;

namespace CarbonPad.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task SecondStart_HandsItsFilesToTheRunningInstance()
    {
        string pipe = "CarbonEditorTest-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource<string[]>();
        SingleInstance.Listen(files => received.TrySetResult(files), stop.Token, pipe);
        Thread.Sleep(200);

        string file = Path.Combine(Path.GetTempPath(), "zażółć gęślą.txt");
        Assert.True(SingleInstance.TryHandOver([file, "--some-switch"], pipe, checkMutex: false));
        var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([Path.GetFullPath(file)], files);

        Assert.False(SingleInstance.TryHandOver([file, SingleInstance.NewWindowSwitch], pipe, checkMutex: false));
        stop.Cancel();
    }

    [Fact]
    public void WithoutARunningInstance_TheProgramStartsNormally()
    {
        // The first instance takes the mutex and does not wait for a pipe at all.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SingleInstance.TryHandOver(["x.txt"], "CarbonEditorTest-" + Guid.NewGuid().ToString("N")));
        Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");
    }
}
