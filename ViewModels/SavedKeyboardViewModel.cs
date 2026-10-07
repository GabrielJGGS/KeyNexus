namespace KeyNexus.ViewModels;

/// <summary>Teclado com configuração salva que não está conectado agora.</summary>
public sealed class SavedKeyboardViewModel
{
    public string GroupKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}
