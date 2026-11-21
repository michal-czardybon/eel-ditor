using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace EelDitor.App.Services;

/// <summary>
/// One window per user: a second start (double click in Explorer, "Open with", the context menu) passes its files
/// to the running instance over a named pipe and exits. <c>--new-window</c> starts a separate instance anyway.
/// </summary>
internal static class SingleInstance
{
    // Per user and per settings folder, so test and portable setups (EELDITOR_CONFIG_DIR) do not collide.
    // (string.GetHashCode is randomized per process, so a stable hash is needed; FNV-1a avoids loading the
    // cryptography libraries at startup.)
    // The build is part of the name: a newly built or installed version never hands its files to an older
    // window that is still open (it opens its own window instead).
    private static string PipeName =>
        "EelDitor-" + Environment.UserName + "-" + StableHash(SettingsStore.Directory.ToLowerInvariant()).ToString("X16")
        + "-" + typeof(SingleInstance).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    internal static ulong StableHash(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in text)
        {
            hash = (hash ^ (byte)c) * 1099511628211;
            hash = (hash ^ (byte)(c >> 8)) * 1099511628211;
        }
        return hash;
    }

    public const string NewWindowSwitch = "--new-window";

    /// <summary>Sends the files to a running instance. True when it took them (this process should exit).</summary>
    public static bool TryHandOver(string[] args) => TryHandOver(args, PipeName);

    // Held for the lifetime of the first instance: later starts see it and connect; the first one does not wait at all.
    private static Mutex? _instanceMutex;

    internal static bool TryHandOver(string[] args, string pipeName, bool checkMutex = true)
    {
        if (args.Contains(NewWindowSwitch)) return false;
        if (checkMutex)
        {
            _instanceMutex ??= new Mutex(initiallyOwned: false, @"Local\" + pipeName, out _);
            bool first;
            try { first = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { first = true; } // the previous instance crashed
            if (first) return false;
        }
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(3000);
            // We were started by the user, so we may bring a window to the front; let the running instance do it.
            if (OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint pid))
                AllowSetForegroundWindow(pid);
            var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Path.GetFullPath);
            byte[] message = Encoding.UTF8.GetBytes(string.Join('\n', files) + "\n\n");
            client.Write(message);
            client.Flush();
            // Wait for the acknowledgement, so the files are not lost if the other instance is closing.
            return client.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Accepts files from later starts and passes them to <paramref name="open"/> (called on a background thread).</summary>
    public static void Listen(Action<string[]> open, CancellationToken ct) => Listen(open, ct, PipeName);

    internal static void Listen(Action<string[]> open, CancellationToken ct, string pipeName)
    {
        var thread = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                    var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    while (true)
                    {
                        int n = server.Read(chunk, 0, chunk.Length);
                        if (n <= 0) break;
                        buffer.Write(chunk, 0, n);
                        if (buffer.Length >= 2 && buffer.GetBuffer()[buffer.Length - 1] == '\n' && buffer.GetBuffer()[buffer.Length - 2] == '\n') break;
                    }
                    var files = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    open(files);
                    server.WriteByte(1);
                    server.Flush();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another instance already listens (started at the same moment) or a client went away: retry.
                    Thread.Sleep(200);
                }
            }
        })
        { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
