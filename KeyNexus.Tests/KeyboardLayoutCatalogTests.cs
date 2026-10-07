using KeyNexus.Core;

namespace KeyNexus.Tests;

public class KeyboardLayoutCatalogTests
{
    private static string? LayoutIds(int id) => id == 0x0001 ? "00020409" : null;

    [Theory]
    [InlineData(0x04160416u, "00000416")] // Português (Brasil ABNT)
    [InlineData(0x08160416u, "00000816")] // Portugal, dentro do idioma pt-BR
    [InlineData(0xF0010416u, "00020409")] // Estados Unidos (internacional), pelo "Layout Id" 0001
    [InlineData(0x00000409u, "00000409")] // palavra alta zero: usa o idioma
    [InlineData(0xE0010411u, "E0010411")] // IME
    public void HklToKlid_ResolvesTheWindowsLayout(uint hkl, string expected)
    {
        Assert.Equal(expected, KeyboardLayoutCatalog.HklToKlid(hkl, LayoutIds));
    }

    [Fact]
    public void HklToKlid_UnknownLayoutId_FallsBackToLanguage()
    {
        Assert.Equal("00000416", KeyboardLayoutCatalog.HklToKlid(0xF0050416u, LayoutIds));
    }

    [Fact]
    public void Describe_UsesTheWindowsNameInsteadOfTheLanguage()
    {
        // Nesta máquina F0010416 é o layout Estados Unidos (internacional); em outras o KLID ainda deve bater.
        var info = KeyboardLayoutCatalog.Describe("F0010416");

        Assert.Equal("F0010416", info.Hkl);
        Assert.Equal("00020409", info.Klid);
        Assert.False(string.IsNullOrWhiteSpace(info.LayoutName));
        Assert.DoesNotContain("Brazilian", info.LayoutName, StringComparison.OrdinalIgnoreCase);
    }
}
