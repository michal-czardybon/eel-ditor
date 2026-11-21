using System.Diagnostics;

namespace EelDitor.App.Services;

/// <summary>
/// Prints an HTML page to PDF with a Chromium-based browser in headless mode (Edge on Windows, Chrome or Chromium
/// elsewhere): its layout engine handles tables, wrapping and fonts far better than anything we could draw ourselves,
/// and no PDF library is needed. The browser runs with a throw-away profile and the page itself forbids loading
/// anything from the network.
/// </summary>
public static class PdfExport
{
    /// <summary>The browser used for printing, or null when none is installed.</summary>
    public static string? FindBrowser()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (var name in new[] { "microsoft-edge", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" })
                    candidates.Add(Path.Combine(dir, name));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Writes <paramref name="html"/> to a temporary file next to nothing else and prints it to <paramref name="pdfPath"/>.</summary>
    /// <returns>null on success, otherwise what went wrong.</returns>
    public static async Task<string?> PrintAsync(string html, string pdfPath, CancellationToken cancel = default)
    {
        if (FindBrowser() is not { } browser) return "no-browser";
        string work = Path.Combine(Path.GetTempPath(), "EelDitor-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string page = Path.Combine(work, "page.html");
            await File.WriteAllTextAsync(page, html, cancel);
            string temp = Path.Combine(work, "out.pdf");
            var psi = new ProcessStartInfo(browser)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[]
            {
                "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                "--user-data-dir=" + Path.Combine(work, "profile"), "--no-pdf-header-footer", "--print-to-pdf=" + temp,
                new Uri(page).AbsoluteUri,
            }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null) return "start";
            _ = process.StandardOutput.ReadToEndAsync(cancel);
            var error = process.StandardError.ReadToEndAsync(cancel);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return "timeout";
            }
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                string message = await error;
                return message.Length > 0 ? message.Trim().Split('\n')[^1] : "exit " + process.ExitCode;
            }
            File.Copy(temp, pdfPath, overwrite: true);
            return null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
