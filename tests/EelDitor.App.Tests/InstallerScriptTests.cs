using System.Runtime.CompilerServices;

namespace EelDitor.App.Tests;

/// <summary>The Inno Setup script.</summary>
public class InstallerScriptTests
{
    private static string Script([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "installer", "EelDitor.iss"));

    /// <summary>
    /// Inno Setup reads a script without a BOM as ANSI: the Polish task texts showed as "OtwĂłrz" in the installer.
    /// </summary>
    [Fact]
    public void Script_WithNonAsciiText_IsUtf8WithBom()
    {
        byte[] bytes = File.ReadAllBytes(Script());
        Assert.Contains(bytes, b => b >= 0x80); // the Polish messages are in the script
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "installer/EelDitor.iss must start with a UTF-8 BOM");
    }
}
