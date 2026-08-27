using System.Text.Json.Serialization;

namespace DekhBhai.Core.Rtc;

/// <summary>
/// Control command sent from remote viewer to host via WebRTC DataChannel.
/// All coordinates are normalized [0,1] relative to screen dimensions.
/// </summary>
public sealed class ControlCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    // Mouse commands
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("button")]
    public string? Button { get; set; } // "left", "right", "middle"

    [JsonPropertyName("deltaY")]
    public double DeltaY { get; set; }

    // Keyboard commands
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("modifiers")]
    public KeyboardModifiers? Modifiers { get; set; }
}

public sealed class KeyboardModifiers
{
    [JsonPropertyName("shift")]
    public bool Shift { get; set; }

    [JsonPropertyName("ctrl")]
    public bool Ctrl { get; set; }

    [JsonPropertyName("alt")]
    public bool Alt { get; set; }

    [JsonPropertyName("meta")]
    public bool Meta { get; set; }

    [JsonPropertyName("capslock")]
    public bool CapsLock { get; set; }
}