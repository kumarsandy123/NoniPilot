namespace NoniPilot.Desktop.Navigation;

/// <summary>One sidebar entry: a stable key (used to cache/look up its page), a display label
/// and glyph, and a factory that builds the page the first time it's navigated to.</summary>
public sealed class NavItem
{
    public required string Key { get; init; }
    public required string Glyph { get; init; }
    public required string Label { get; init; }
    public required Func<System.Windows.Controls.UserControl> CreatePage { get; init; }

    public override string ToString() => Label;
}
