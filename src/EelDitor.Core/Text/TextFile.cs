using System.Text;

namespace EelDitor.Core.Text;

/// <summary>Result of decoding a file.</summary>
public sealed class LoadedText
{
    public required string Text { get; init; }
    public required TextEncoding Encoding { get; init; }
    public required EolStats Eol { get; init; }

    /// <summary>
    /// True when the bytes could not be decoded losslessly (invalid sequences were replaced),
    /// so saving would not reproduce the original file.
    /// </summary>
    public bool IsLossy { get; init; }

    /// <summary>True when the content looks binary (NUL bytes in a non UTF-16/32 file).</summary>
    public bool LooksBinary { get; init; }

    public long FileSize { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
}

public sealed class UnencodableCharactersException(TextEncoding encoding, IReadOnlyList<int> lines)
    : Exception($"Some characters cannot be encoded in {encoding.DisplayName}.")
{
    public TextEncoding Encoding { get; } = encoding;

    /// <summary>1-based line numbers containing unencodable characters (at most 50).</summary>
    public IReadOnlyList<int> Lines { get; } = lines;
}

public static class TextFile
{
    private const int DetectionSampleSize = 256 * 1024;

    public static async Task<LoadedText> LoadAsync(string path, TextEncoding? forcedEncoding = null, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var result = Decode(bytes, forcedEncoding);
        return new LoadedText
        {
            Text = result.Text,
            Encoding = result.Encoding,
            Eol = result.Eol,
            IsLossy = result.IsLossy,
            LooksBinary = result.LooksBinary,
            FileSize = bytes.LongLength,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
        };
    }

    /// <summary>Decodes bytes, detecting the encoding unless one is forced.</summary>
    public static LoadedText Decode(byte[] bytes, TextEncoding? forcedEncoding = null)
    {
        TextEncoding.EnsureInitialized();
        TextEncoding encoding;
        int bomLength;
        if (forcedEncoding is not null)
        {
            // Honour a BOM if the forced encoding matches it, so "reopen as UTF-8" on a BOM file works.
            var detectedBom = DetectBom(bytes);
            if (detectedBom is not null && detectedBom.CodePage == forcedEncoding.CodePage)
            {
                encoding = detectedBom;
                bomLength = detectedBom.Preamble.Length;
            }
            else
            {
                encoding = forcedEncoding with { HasBom = false };
                bomLength = 0;
            }
        }
        else
        {
            var bom = DetectBom(bytes);
            if (bom is not null) { encoding = bom; bomLength = bom.Preamble.Length; }
            else { encoding = DetectWithoutBom(bytes); bomLength = 0; }
        }

        var span = bytes.AsSpan(bomLength);
        string text;
        bool lossy = false;
        try
        {
            text = encoding.GetStrictEncoding().GetString(span);
        }
        catch (DecoderFallbackException)
        {
            text = encoding.GetLenientEncoding().GetString(span);
            lossy = true;
        }

        if (!lossy && !encoding.IsUnicode)
        {
            // Single/double byte code pages may map several byte values to the same char; verify round trip.
            try
            {
                var back = encoding.GetStrictEncoding().GetBytes(text);
                lossy = !span.SequenceEqual(back);
            }
            catch (EncoderFallbackException) { lossy = true; }
        }

        bool binary = !encoding.IsUnicode || encoding.CodePage == TextEncoding.Utf8CodePage
            ? span[..Math.Min(span.Length, 8192)].IndexOf((byte)0) >= 0
            : false;

        return new LoadedText
        {
            Text = text,
            Encoding = encoding,
            Eol = EolStats.Analyze(text),
            IsLossy = lossy,
            LooksBinary = binary,
            FileSize = bytes.LongLength,
        };
    }

    public static TextEncoding? DetectBom(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) return new(TextEncoding.Utf32LeCodePage, true);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF) return new(TextEncoding.Utf32BeCodePage, true);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return TextEncoding.Utf8Bom;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return TextEncoding.Utf16Le;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return TextEncoding.Utf16Be;
        return null;
    }

    private static TextEncoding DetectWithoutBom(byte[] bytes)
    {
        if (bytes.Length == 0) return TextEncoding.Utf8;

        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, DetectionSampleSize));

        // UTF-16 without BOM: text in Latin scripts has a zero in every other byte.
        if (sample.Length >= 16 && (sample.Length & 1) == 0)
        {
            int evenZeros = 0, oddZeros = 0;
            for (int i = 0; i + 1 < sample.Length; i += 2)
            {
                if (sample[i] == 0) evenZeros++;
                if (sample[i + 1] == 0) oddZeros++;
            }
            int pairs = sample.Length / 2;
            if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05) return new(TextEncoding.Utf16LeCodePage, false);
            if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05) return new(TextEncoding.Utf16BeCodePage, false);
        }

        if (IsValidUtf8(bytes)) return TextEncoding.Utf8;

        try
        {
            var detection = UtfUnknown.CharsetDetector.DetectFromBytes(sample.ToArray());
            var detected = detection.Detected;
            if (detected?.Encoding is { } enc && detected.Confidence >= 0.5f
                && enc.CodePage != TextEncoding.Utf8CodePage && enc.CodePage != 20127 /* ASCII */)
            {
                return new TextEncoding(enc.CodePage, false);
            }
        }
        catch (Exception)
        {
            // Detection is best effort; fall back to the ANSI code page.
        }

        return new TextEncoding(TextEncoding.AnsiCodePage, false);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);

    /// <summary>Encodes text, throwing <see cref="UnencodableCharactersException"/> if some characters do not fit.</summary>
    public static byte[] Encode(string text, TextEncoding encoding)
    {
        TextEncoding.EnsureInitialized();
        byte[] body;
        try
        {
            body = encoding.GetStrictEncoding().GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw new UnencodableCharactersException(encoding, FindUnencodableLines(text, encoding));
        }
        var preamble = encoding.Preamble;
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    public static IReadOnlyList<int> FindUnencodableLines(string text, TextEncoding encoding)
    {
        var enc = encoding.GetStrictEncoding();
        var lines = new List<int>();
        int lineNo = 1, start = 0;
        for (int i = 0; i <= text.Length && lines.Count < 50; i++)
        {
            bool end = i == text.Length || text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'));
            if (!end) continue;
            try { enc.GetByteCount(text.AsSpan(start, i - start)); }
            catch (EncoderFallbackException) { lines.Add(lineNo); }
            lineNo++;
            start = i + 1;
        }
        return lines;
    }

    /// <summary>
    /// Writes the file atomically (temp file + replace) and keeps the original file's attributes.
    /// Returns the new last write time.
    /// </summary>
    public static async Task<DateTime> SaveAsync(string path, string text, TextEncoding encoding, CancellationToken ct = default)
    {
        byte[] bytes = Encode(text, encoding);
        string fullPath = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        if (!File.Exists(fullPath))
        {
            await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);
            return File.GetLastWriteTimeUtc(fullPath);
        }

        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"Plik jest tylko do odczytu: {fullPath}");

        string temp = Path.Combine(dir, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            try
            {
                // File.Replace keeps the original's ACLs and creation time on Windows.
                File.Replace(temp, fullPath, null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // Some file systems (network shares, FAT) do not support Replace; fall back to an in-place write.
                await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);
            }
            try { File.SetAttributes(fullPath, attributes); } catch (IOException) { }
        }
        finally
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } }
        }
        return File.GetLastWriteTimeUtc(fullPath);
    }
}
