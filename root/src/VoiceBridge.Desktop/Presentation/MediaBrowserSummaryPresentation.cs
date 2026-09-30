namespace VoiceBridge.Desktop.Presentation;

internal static class MediaBrowserSummaryPresentation
{
    public static string Format(long referenceCount, long sourceMediaFileCount) =>
        $"{referenceCount + sourceMediaFileCount:N0} entries · {referenceCount:N0} references + {sourceMediaFileCount:N0} source media files";
}
