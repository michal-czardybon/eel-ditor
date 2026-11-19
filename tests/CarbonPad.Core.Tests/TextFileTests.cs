using System.Text;
using CarbonPad.Core.Text;

namespace CarbonPad.Core.Tests;

public class TextFileTests
{
    static TextFileTests() => TextEncoding.EnsureInitialized();

    public static TheoryData<string, byte[]> RoundTripCases()
    {
        var cp1250 = Encoding.GetEncoding(1250);
        var cp852 = Encoding.GetEncoding(852);
        var sjis = Encoding.GetEncoding(932);
        const string pl = "Zażółć gęślą jaźń\r\nZAŻÓŁĆ GĘŚLĄ JAŹŃ\r\nkoniec";
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "ascii-lf", Encoding.ASCII.GetBytes("a\nb\nc\n") },
            { "utf8-crlf", Encoding.UTF8.GetBytes(pl) },
            { "utf8-bom", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(pl)] },
            { "utf8-mixed-eol", Encoding.UTF8.GetBytes("a\r\nb\nc\rd\r\n\n") },
            { "utf8-no-final-eol", Encoding.UTF8.GetBytes("line1\nline2") },
            { "utf16le-bom", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(pl)] },
            { "utf16be-bom", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(pl)] },
            { "cp1250", cp1250.GetBytes(pl + " i jeszcze więcej tekstu, żeby detektor miał z czego zgadywać. Źdźbło, łódź, gęś.") },
            { "cp852", cp852.GetBytes(pl) },
            { "sjis", sjis.GetBytes("日本語のテキストです。これはテストです。\r\n二行目") },
            { "emoji-utf8", Encoding.UTF8.GetBytes("😀 test \U0001F680\n") },
        };
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void DecodeThenEncode_ReproducesBytesExactly(string name, byte[] bytes)
    {
        var loaded = TextFile.Decode(bytes);
        var saved = TextFile.Encode(loaded.Text, loaded.Encoding);
        Assert.True(bytes.AsSpan().SequenceEqual(saved), $"{name}: detected {loaded.Encoding}, round trip differs");
    }

    [Fact]
    public void DetectsBomEncodings()
    {
        Assert.Equal(TextEncoding.Utf8Bom, TextFile.Decode([0xEF, 0xBB, 0xBF, 0x41]).Encoding);
        Assert.Equal(TextEncoding.Utf16Le, TextFile.Decode([0xFF, 0xFE, 0x41, 0x00]).Encoding);
        Assert.Equal(TextEncoding.Utf16Be, TextFile.Decode([0xFE, 0xFF, 0x00, 0x41]).Encoding);
    }

    [Fact]
    public void Utf8WithoutBom_IsDetected()
    {
        var loaded = TextFile.Decode(Encoding.UTF8.GetBytes("gęś"));
        Assert.Equal(TextEncoding.Utf8, loaded.Encoding);
        Assert.Equal("gęś", loaded.Text);
        Assert.False(loaded.IsLossy);
    }

    [Fact]
    public void NonUtf8_FallsBackToCodePage_AndIsNotLossy()
    {
        var bytes = Encoding.GetEncoding(1250).GetBytes("Zażółć gęślą jaźń. Pchnąć w tę łódź jeża lub ośm skrzyń fig.");
        var loaded = TextFile.Decode(bytes);
        Assert.False(loaded.Encoding.IsUnicode);
        Assert.False(loaded.IsLossy);
    }

    [Fact]
    public void ForcedEncoding_ReinterpretsBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("ż");
        var loaded = TextFile.Decode(bytes, new TextEncoding(1252, false));
        Assert.Equal("Å¼", loaded.Text);
    }

    [Fact]
    public void Encode_ThrowsWithLineNumbers_WhenCharacterDoesNotFit()
    {
        var ex = Assert.Throws<UnencodableCharactersException>(() =>
            TextFile.Encode("ok\r\nemoji 😀\r\nok\r\nź", new TextEncoding(1252, false)));
        Assert.Equal([2, 4], ex.Lines);
    }

    [Fact]
    public async Task SaveAsync_WritesAtomically_AndPreservesContent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "carbonpad-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "a.txt");
            await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\r', (byte)'\n']);
            var loaded = await TextFile.LoadAsync(path);
            await TextFile.SaveAsync(path, loaded.Text + "y", loaded.Encoding);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\r', (byte)'\n', (byte)'y' }, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class EolTests
{
    [Fact]
    public void Analyze_CountsEachKind()
    {
        var s = EolStats.Analyze("a\r\nb\nc\rd\r\n");
        Assert.Equal(new EolStats(2, 1, 1), s);
        Assert.True(s.IsMixed);
        Assert.Equal(EolKind.CrLf, s.Dominant(EolKind.Lf));
    }

    [Fact]
    public void NoTerminators_UsesFallback()
    {
        var s = EolStats.Analyze("abc");
        Assert.False(s.IsMixed);
        Assert.Equal(EolKind.Lf, s.Dominant(EolKind.Lf));
    }

    [Theory]
    [InlineData("a\r\nb\nc\r", EolKind.Lf, "a\nb\nc\n")]
    [InlineData("a\nb\n", EolKind.CrLf, "a\r\nb\r\n")]
    [InlineData("a\r\n\r\nb", EolKind.Cr, "a\r\rb")]
    public void Convert_NormalizesAllTerminators(string input, EolKind target, string expected) =>
        Assert.Equal(expected, EolConverter.Convert(input, target));
}
