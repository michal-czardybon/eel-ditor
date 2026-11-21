using EelDitor.Core.Text;

namespace EelDitor.Core.Tests;

/// <summary>Examples from "Polska cyrylica 2.0".</summary>
public class PolishCyrillicTests
{
    private const string Breve = "̆", Caron = "̌", Acute = "́";

    public static TheoryData<string, string, string> Examples => new()
    {
        {
            "Czasami jesienią na grządkę w ogrodzie deszczowa pogoda przychodzi.",
            $"Часамі єсєня{Breve} на гр{Caron}ӑдкӗ в огродзє дещова погода пр{Caron}ыходзі.",
            $"Часамі єсєнѭ на гр{Acute}ѫдкѧ в огроѕє дещова погода пр{Acute}ыхоѕі."
        },
        { "dźwięki pieśni", $"дзьвє{Breve}кі пєсьні", $"ѕ{Acute}вѩкі пєс{Acute}ні" },
        { "Litwo, Ojczyzno moja! ty jesteś jak zdrowie;", "Љітво, Ойчызно моя! ты єстесь як здровє;", $"Љітво, Ойчызно моя! ты єстес{Acute} як здровє;" },
        { "nie kiosk jogurt objazd", "нє кьоск йогурт об’язд", "нє кьоск йогурт об’язд" },
        { "ścieżka", "сцєжка", $"с{Acute}цєжка" },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void ConvertsLikeThePage(string latin, string a, string b)
    {
        Assert.Equal(a, PolishCyrillic.ToCyrillic(latin, CyrillicVariant.A));
        Assert.Equal(b, PolishCyrillic.ToCyrillic(latin, CyrillicVariant.B));
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void ConvertsBackToLatin(string latin, string a, string b)
    {
        Assert.Equal(latin, PolishCyrillic.ToLatin(a));
        Assert.Equal(latin, PolishCyrillic.ToLatin(b));
    }

    [Theory]
    [InlineData("Zażółć gęślą jaźń. ŻÓŁW Szczebrzeszyn, Chrząszcz; Dzień dobry, Ćma i Ńmm?")]
    [InlineData("sierpień, ciocia, zięć, dziadek, siostra, kiedy, miód, wiór, dżem, władza, Rzym, rzeka, Józef")]
    public void RoundTrips(string latin)
    {
        foreach (var v in new[] { CyrillicVariant.A, CyrillicVariant.B })
            Assert.Equal(latin, PolishCyrillic.ToLatin(PolishCyrillic.ToCyrillic(latin, v)));
    }

    [Fact]
    public void KeepsNonPolishTextAndPunctuation()
    {
        Assert.Equal("2024 — „цытат” (x)", PolishCyrillic.ToCyrillic("2024 — „cytat” (x)", CyrillicVariant.A)); // x is not Polish: kept
        Assert.Equal("Ойчызно", PolishCyrillic.ToCyrillic("Ojczyzno", CyrillicVariant.B));
        Assert.Equal("ЧӐС", PolishCyrillic.ToCyrillic("CZĄS", CyrillicVariant.A));
    }
}
