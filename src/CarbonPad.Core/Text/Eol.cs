using System.Text;

namespace CarbonPad.Core.Text;

public enum EolKind { CrLf, Lf, Cr }

public static class EolKindExtensions
{
    public static string ToNewLine(this EolKind kind) => kind switch
    {
        EolKind.CrLf => "\r\n",
        EolKind.Lf => "\n",
        EolKind.Cr => "\r",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string DisplayName(this EolKind kind) => kind switch
    {
        EolKind.CrLf => "Windows (CR LF)",
        EolKind.Lf => "Unix (LF)",
        EolKind.Cr => "Macintosh (CR)",
        _ => kind.ToString(),
    };

    public static string ShortName(this EolKind kind) => kind switch
    {
        EolKind.CrLf => "CRLF",
        EolKind.Lf => "LF",
        EolKind.Cr => "CR",
        _ => kind.ToString(),
    };
}

/// <summary>Counts of line terminators found in a text.</summary>
public readonly record struct EolStats(int CrLf, int Lf, int Cr)
{
    public int Total => CrLf + Lf + Cr;

    /// <summary>True when more than one kind of terminator is present.</summary>
    public bool IsMixed => (CrLf > 0 ? 1 : 0) + (Lf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) > 1;

    /// <summary>
    /// The most frequent terminator. Ties prefer CRLF, then LF. A file without any
    /// terminator falls back to <paramref name="fallback"/>.
    /// </summary>
    public EolKind Dominant(EolKind fallback)
    {
        if (Total == 0) return fallback;
        if (CrLf >= Lf && CrLf >= Cr) return EolKind.CrLf;
        if (Lf >= Cr) return EolKind.Lf;
        return EolKind.Cr;
    }

    public static EolStats Analyze(ReadOnlySpan<char> text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        return new EolStats(crlf, lf, cr);
    }
}

public static class EolConverter
{
    /// <summary>Replaces every line terminator (CRLF, LF or CR) with <paramref name="target"/>.</summary>
    public static string Convert(string text, EolKind target)
    {
        string nl = target.ToNewLine();
        var sb = new StringBuilder(text.Length + text.Length / 32);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(nl);
            }
            else if (c == '\n') sb.Append(nl);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The platform default for new files.</summary>
    public static EolKind PlatformDefault => OperatingSystem.IsWindows() ? EolKind.CrLf : EolKind.Lf;
}
