using KeyNexus.Core;

namespace KeyNexus.ViewModels;

/// <summary>Item do seletor de layout: nome do Windows (ou apelido) em cima, idioma embaixo.</summary>
public sealed class LayoutOption
{
    /// <summary>
    /// Verdadeiro enquanto a lista compartilhada é reconstruída: o ComboBox manda null nessa hora
    /// e isso não pode desvincular o layout do teclado.
    /// </summary>
    public static bool IsRefreshing { get; set; }

    public string Hkl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string TechnicalInfo { get; init; } = string.Empty;
    public string LayoutName { get; init; } = string.Empty;

    public static LayoutOption None() => new()
    {
        Hkl = string.Empty,
        Title = "Nenhum",
        Subtitle = "Usar o layout que estiver ativo no Windows",
        TechnicalInfo = "O KeyNexus não troca o layout para este teclado"
    };

    public static LayoutOption From(KeyboardLayoutInfo info, string? alias)
    {
        bool hasAlias = !string.IsNullOrWhiteSpace(alias);
        string subtitle = hasAlias ? $"{info.LayoutName} · {info.LanguageName}" : info.LanguageName;
        if (!info.IsInstalled)
            subtitle = $"Não está instalado no Windows · {subtitle}";

        return new LayoutOption
        {
            Hkl = info.Hkl,
            Title = hasAlias ? alias!.Trim() : info.LayoutName,
            Subtitle = subtitle,
            TechnicalInfo = info.TechnicalInfo,
            LayoutName = info.LayoutName
        };
    }

    public override string ToString() => Title;
}
