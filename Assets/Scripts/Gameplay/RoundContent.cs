using System;

/// <summary>One entry in RoundManager's authored round pool: a prompt plus its two starting endpoint labels.</summary>
[Serializable]
public class RoundContent
{
    public string prompt = "";
    public string leftLabel = "";
    public string rightLabel = "";
}
