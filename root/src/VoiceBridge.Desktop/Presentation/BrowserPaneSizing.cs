namespace VoiceBridge.Desktop.Presentation;

internal static class BrowserPaneSizing
{
    public const double SplitterWidth = 8;
    public const double MinimumListWidth = 220;
    public const double MinimumDetailWidth = 300;
    public const double KeyboardResizeStep = 24;

    public static double ClampListWidth(double requestedWidth, double availableWidth)
    {
        if (!double.IsFinite(requestedWidth))
        {
            requestedWidth = MinimumListWidth;
        }

        var maximumListWidth = Math.Max(
            MinimumListWidth,
            availableWidth - SplitterWidth - MinimumDetailWidth);
        return Math.Clamp(requestedWidth, MinimumListWidth, maximumListWidth);
    }

    public static double AdjustListWidth(double currentWidth, double availableWidth, int direction) =>
        ClampListWidth(currentWidth + Math.Sign(direction) * KeyboardResizeStep, availableWidth);
}
