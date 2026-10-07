using System;
using System.Collections.Generic;
using KeyNexus.Core;

namespace KeyNexus.ViewModels;

/// <summary>Uma regra desenhada como teclas: gatilho → saída.</summary>
public sealed class RemapRuleViewModel : ObservableObject
{
    private bool _isEditing;

    public RemapRuleViewModel(RemapRule rule, string? layoutHkl)
    {
        Rule = rule;
        TriggerParts = LayoutKeyHelper.GetTriggerParts(rule.TriggerVk, rule.Modifiers, layoutHkl);
        TriggerTooltip = string.Join(" + ", TriggerParts);

        switch (rule.OutputType)
        {
            case RemapOutputType.Key:
                IsKey = true;
                OutputParts = LayoutKeyHelper.GetOutputParts(rule.OutputVk, rule.OutputModifiers, layoutHkl);
                OutputTooltip = "Simula a tecla " +
                    string.Join(" + ", LayoutKeyHelper.GetTriggerParts(rule.OutputVk, rule.OutputModifiers, layoutHkl));
                TypeDisplay = "Tecla";
                break;

            case RemapOutputType.Text:
                OutputText = $"\"{rule.OutputText}\"";
                OutputTooltip = "Digita o texto como caracteres Unicode";
                TypeDisplay = "Texto";
                CanConvertToKey = rule.OutputText.Length == 1;
                if (CanConvertToKey)
                    Warning = "Texto pode falhar em terminal e SSH. Converta para tecla.";
                break;

            case RemapOutputType.Sequence:
                int steps = rule.Sequence?.Count ?? 0;
                OutputText = steps == 1 ? "1 passo" : $"{steps} passos";
                OutputTooltip = "Macro: envia uma sequência de teclas";
                TypeDisplay = "Macro";
                break;
        }
    }

    public RemapRule Rule { get; }
    public IReadOnlyList<string> TriggerParts { get; }
    public string TriggerTooltip { get; }
    public IReadOnlyList<string> OutputParts { get; } = Array.Empty<string>();
    public string OutputText { get; } = string.Empty;
    public string OutputTooltip { get; } = string.Empty;
    public bool IsKey { get; }
    public bool IsNotKey => !IsKey;
    public string TypeDisplay { get; } = string.Empty;
    public bool CanConvertToKey { get; }
    public string Warning { get; } = string.Empty;
    public bool HasWarning => Warning.Length > 0;

    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    public static RemapRule Clone(RemapRule rule) => new()
    {
        TriggerVk = rule.TriggerVk,
        Modifiers = ModifierFlags.Normalize(rule.Modifiers),
        OutputType = rule.OutputType,
        OutputVk = rule.OutputVk,
        OutputModifiers = ModifierFlags.Normalize(rule.OutputModifiers),
        OutputText = rule.OutputText ?? string.Empty,
        Sequence = rule.Sequence == null ? new List<RemapSequenceStep>() : new List<RemapSequenceStep>(rule.Sequence),
        Description = rule.Description ?? string.Empty
    };
}
