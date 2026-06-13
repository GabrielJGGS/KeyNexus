using System.Collections.Generic;

namespace KeyNexus.Core;

public enum RemapOutputType
{
    Key,
    Text,
    Sequence
}

public class RemapRule
{
    public int TriggerVk { get; set; }
    public int Modifiers { get; set; }
    public RemapOutputType OutputType { get; set; } = RemapOutputType.Key;
    public int OutputVk { get; set; }
    public int OutputModifiers { get; set; }
    public string OutputText { get; set; } = string.Empty;
    public List<RemapSequenceStep> Sequence { get; set; } = new();
    public string Description { get; set; } = string.Empty;
}

public class RemapSequenceStep
{
    public int Vk { get; set; }
    public int Modifiers { get; set; }
    public int DelayMs { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class RemapProfile
{
    public string DeviceGroupKey { get; set; } = string.Empty;
    public List<RemapRule> Rules { get; set; } = new();
}
