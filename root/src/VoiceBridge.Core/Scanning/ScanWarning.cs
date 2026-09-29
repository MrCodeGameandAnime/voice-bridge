namespace VoiceBridge.Core.Scanning;

public sealed record ScanWarning(string Code, string Message, string? RelativePath = null);
