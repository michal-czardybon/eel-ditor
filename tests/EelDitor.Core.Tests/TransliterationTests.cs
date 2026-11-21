using EelDitor.Core.Text;

namespace EelDitor.Core.Tests;

public class TransliterationTests
{
    [Theory]
    [InlineData("Ja pomnju čudnoe mgnovenʹe", "Я помню чудное мгновенье")]
    [InlineData("Ščuka i ëž, èto xorošo", "Щука и ёж, это хорошо")]
    [InlineData("SSSR i Moskva", "СССР и Москва")]
    [InlineData("obʺjavlenie", "объявление")]
    [InlineData("Majakovskij", "Маяковский")]
    public void Russian(string latin, string cyrillic)
    {
        Assert.Equal(cyrillic, Transliteration.Russian.ToCyrillic(latin));
        Assert.Equal(latin, Transliteration.Russian.ToLatin(cyrillic));
    }

    [Fact]
    public void Russian_AcceptsCommonInputVariants() =>
        Assert.Equal("мальчик хорош", Transliteration.Russian.ToCyrillic("mal'čik horoš"));

    [Theory]
    [InlineData("Kyjiv je stolyceju Ukrajiny", "Київ є столицею України")]
    [InlineData("Ščastja i hanok, gudzyk", "Щастя і ганок, ґудзик")]
    [InlineData("Jevropa, pʼjatʹ", "Європа, пʼять")]
    public void Ukrainian(string latin, string cyrillic)
    {
        Assert.Equal(cyrillic, Transliteration.Ukrainian.ToCyrillic(latin));
        Assert.Equal(latin, Transliteration.Ukrainian.ToLatin(cyrillic));
    }

    [Theory]
    [InlineData("Ljubav, njiva, džep, đak, ćup, čaša, šuma, žaba", "Љубав, њива, џеп, ђак, ћуп, чаша, шума, жаба")]
    [InlineData("LJUBAV i Džordž", "ЉУБАВ и Џорџ")]
    [InlineData("Beograd, Srbija", "Београд, Србија")]
    public void Serbian(string latin, string cyrillic)
    {
        Assert.Equal(cyrillic, Transliteration.Serbian.ToCyrillic(latin));
        Assert.Equal(latin, Transliteration.Serbian.ToLatin(cyrillic));
    }

    [Theory]
    [InlineData("Medžuslovjansky jezyk jest lěpy", "Меджусловјанскы језык јест лєпы")]
    [InlineData("ljubov, konj, člověky, šest, žena", "љубов, коњ, чловєкы, шест, жена")]
    public void Interslavic(string latin, string cyrillic)
    {
        Assert.Equal(cyrillic, Transliteration.Interslavic.ToCyrillic(latin));
        Assert.Equal(latin, Transliteration.Interslavic.ToLatin(cyrillic));
    }

    [Fact]
    public void Interslavic_EtymologicalLettersAndOtherCyrillic()
    {
        Assert.Equal("ѧзык ћи", Transliteration.Interslavic.ToCyrillic("ęzyk ći"));
        Assert.Equal("dža", Transliteration.Interslavic.ToLatin("џа"));
    }

    [Fact]
    public void KeepsWhatIsNotALetterOfTheAlphabet() =>
        Assert.Equal("2024 — „цитата” (q, w)", Transliteration.Russian.ToCyrillic("2024 — „citata” (q, w)"));
}
