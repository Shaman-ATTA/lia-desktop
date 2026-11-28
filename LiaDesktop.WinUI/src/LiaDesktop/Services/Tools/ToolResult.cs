namespace LiaDesktop.Services.Tools;

/// <summary>Tool payload + optional verification screenshot for act-observe.</summary>
public sealed class ToolResult
{
    public object Meta { get; init; } = new { };
    public ScreenShot? Observe { get; init; }
}
