using System.Runtime.CompilerServices;
using Xunit.Internal;
using Xunit.Sdk;
using Xunit.v3;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// A test that runs on the main thread, which is the only one AppKit is meant to be used from and
/// the one <c>MacTestMain.cs</c> keeps pumping.
/// </summary>
/// <remarks>
/// What <c>[WinFormsFact]</c> is to the Windows tests. Xunit.StaFact has nothing for AppKit: its
/// <c>[UIFact]</c> is a thread of its own away from Windows, which isn't the main one.
/// </remarks>
[XunitTestCaseDiscoverer(typeof(AppKitFactDiscoverer))]
[AttributeUsage(AttributeTargets.Method)]
public sealed class AppKitFactAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1) : FactAttribute(sourceFilePath, sourceLineNumber);

/// <summary>Makes each <see cref="AppKitFactAttribute"/> test into a case that runs on the main thread.</summary>
public sealed class AppKitFactDiscoverer : FactDiscoverer
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(discoveryOptions, testMethod, factAttribute);

        return new AppKitTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
            sourceFilePath: details.SourceFilePath,
            sourceLineNumber: details.SourceLineNumber,
            timeout: details.Timeout);
    }
}

/// <summary>A test case that hands its whole run to the main thread, then waits to hear how it went.</summary>
public sealed class AppKitTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public AppKitTestCase()
    {
    }

    public AppKitTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        Type[]? skipExceptions,
        string? skipReason,
        Type? skipType,
        string? skipUnless,
        string? skipWhen,
        Dictionary<string, HashSet<string>>? traits,
        string? sourceFilePath,
        int? sourceLineNumber,
        int? timeout)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit, skipExceptions, skipReason, skipType, skipUnless, skipWhen, traits, null, sourceFilePath, sourceLineNumber, timeout)
    {
    }

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager methodFixtureMappings)
    {
        var finished = new TaskCompletionSource<RunSummary>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Taken across by hand: posting to the main thread doesn't bring it, and it's where xunit
        // keeps TestContext, which the runner reads back and fails the test for not finding.
        var context = ExecutionContext.Capture()!;

        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => ExecutionContext.Run(
            context,
            async _ =>
            {
                try
                {
                    finished.SetResult(await XunitRunnerHelper.RunXunitTestCase(
                        this,
                        messageBus,
                        cancellationTokenSource,
                        parallelMode,
                        scheduler,
                        aggregator,
                        explicitOption,
                        constructorArguments,
                        methodFixtureMappings));
                }
                catch (Exception ex)
                {
                    finished.SetException(ex);
                }
            },
            null));

        return new ValueTask<RunSummary>(finished.Task);
    }
}
