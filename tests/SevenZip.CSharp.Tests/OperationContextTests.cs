using System.Runtime.InteropServices;
using SevenZip.CSharp;

namespace SevenZip.CSharp.Tests;

public sealed class OperationContextTests
{
    [Fact]
    public void PasswordIsBorrowedRedactedAndCannotBeUsedAfterDisposal()
    {
        using var password = new ArchivePassword("secret");
        var context = new ArchiveOperationContext(password);
        context.CheckStop();
        Assert.Same(password, context.Password);
        Assert.True(context.Password!.Use(value => value.SequenceEqual("secret")));
        Assert.DoesNotContain("secret", password.ToString());
        password.Dispose();
        password.Dispose();
        Assert.True(password.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => password.Use(value => value.Length));
    }

    [Fact]
    public void PasswordMemoryIsClearedRatherThanJustDropped()
    {
        var password = new ArchivePassword("secret");
        var memory = (char[])typeof(ArchivePassword).GetField("password",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(password)!;
        password.Dispose();
        Assert.All(memory, value => Assert.Equal('\0', value));
    }

    [Fact]
    public void FailedPasswordConstructionIsSafeToFinalize()
    {
        Assert.Throws<ArgumentException>(() => new ArchivePassword(ReadOnlySpan<char>.Empty));
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public async Task IndependentContextsDoNotShareCancellationOrPause()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new ArchiveOperationControl();
        var second = new ArchiveOperationControl();
        using var firstOperation = first.EnterNativeOperation();
        using var secondOperation = second.EnterNativeOperation();
        var firstContext = new ArchiveOperationContext(control: first, cancellationToken: cancellation.Token);
        var secondContext = new ArchiveOperationContext(control: second);
        Assert.True(first.SetPaused(true));
        Assert.False(second.IsPaused);
        secondContext.CheckStop();
        var blocked = Task.Run(firstContext.CheckStop);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        secondContext.CheckStop();
    }

    [Fact]
    public async Task PauseFreezesEffectiveElapsedAndResumeReleasesCheckpoint()
    {
        var control = new ArchiveOperationControl();
        using var operation = control.EnterNativeOperation();
        Assert.True(control.SetPaused(true));
        var frozen = control.ActiveElapsed;
        var context = new ArchiveOperationContext(control: control);
        var checkpoint = Task.Run(context.CheckStop);
        await Task.Delay(50);
        Assert.False(checkpoint.IsCompleted);
        Assert.Equal(frozen, control.ActiveElapsed);
        Assert.True(control.SetPaused(false));
        await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LeavingLastNativeScopeResumesAndDisablesPause()
    {
        var control = new ArchiveOperationControl();
        var scope = control.EnterNativeOperation();
        Assert.True(control.SetPaused(true));
        scope.Dispose();
        scope.Dispose();
        Assert.False(control.IsPaused);
        Assert.False(control.CanPause);
        Assert.False(control.SetPaused(true));
    }

    [Fact]
    public void WorkStatisticsKeepUnknownsAndCalculateMeasuredValues()
    {
        var metrics = new ArchiveProgressMetrics();
        Assert.Null(metrics.Snapshot(TimeSpan.Zero).Percentage);
        Assert.Null(metrics.Snapshot(TimeSpan.Zero).Remaining);
        metrics.SetTotal(1000);
        metrics.SetCompleted(250);
        metrics.SetRatio(200, 100);
        var snapshot = metrics.Snapshot(TimeSpan.FromSeconds(2));
        Assert.Equal(25d, snapshot.Percentage);
        Assert.Equal(125d, snapshot.AverageSpeed);
        Assert.Equal(TimeSpan.FromSeconds(6), snapshot.Remaining);
        Assert.Equal(200L, snapshot.RatioInput);
        Assert.Equal(100L, snapshot.RatioOutput);
    }

    [Fact]
    public void LibraryHasNoHostOrProcessExecutionReferences()
    {
        var references = typeof(ArchiveOperationContext).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.True(
            reference.Name is "netstandard" or "mscorlib" or "Microsoft.CSharp"
            || reference.Name!.StartsWith("System.", StringComparison.Ordinal)
            || reference.Name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal),
            $"Unexpected non-framework dependency: {reference.Name}"));
        Assert.DoesNotContain(references, reference => reference.Name == "System.Diagnostics.Process");
        Assert.Equal(IntPtr.Size == 4 ? 16 : 24, Marshal.SizeOf<SevenZipNativeVariant>());
    }
}
