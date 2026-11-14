namespace CozyCouch.Core.Activity;

public enum ActivityLevel { Info, Warning, Error }

public sealed record ActivityEntry(DateTime At, ActivityLevel Level, string Message, string? Hint);
