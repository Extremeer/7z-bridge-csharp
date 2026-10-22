using System;

namespace SevenZip.CSharp;

public enum EngineStage
{
    Preparing,
    Scanning,
    Reading,
    Compressing,
    Extracting,
    Writing,
    Copying,
    Finalizing,
    CleaningUp,
    Validating,
    Committing,
}

public sealed record EngineProgress(
    EngineStage Stage,
    double? Percentage = null,
    string? CurrentItem = null,
    long ProcessedBytes = 0,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? EstimatedRemaining = null,
    int? ProcessedItemCount = null,
    int? TotalItemCount = null,
    long? OutputBytes = null,
    bool HasMeasuredProcessedBytes = false,
    string? MetricsScope = null,
    double? OutputBytesPerSecond = null,
    bool IsRemainingEstimated = false,
    bool OutputIncludesExistingEntries = false,
    long? RatioInputBytes = null,
    TimeSpan? ActiveElapsed = null,
    bool IsPaused = false,
    long? NativeWorkCompleted = null,
    long? NativeWorkTotal = null,
    long? RatioOutputBytes = null,
    int? AffectedItemCount = null,
    bool IsTimingHeld = false)
{
    public double? BoundedPercentage => Percentage is { } percentage && double.IsFinite(percentage)
        ? Math.Clamp(percentage, 0d, 100d)
        : null;
}

public enum EngineOperationStatus
{
    Succeeded,
    Canceled,
    PartiallySucceeded,
    Failed,
    RequiresInteraction,
}

public sealed record EngineOperationSummary(
    EngineOperationStatus Status,
    int ProcessedItemCount = 0,
    int SkippedItemCount = 0,
    long InputBytes = 0,
    long OutputBytes = 0,
    TimeSpan? Elapsed = null,
    string? RecoveryPath = null);
