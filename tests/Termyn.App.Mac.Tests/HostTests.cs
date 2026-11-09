using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// That these tests run where AppKit can be used, and well away from the real app's data.
/// </summary>
/// <remarks>
/// Both are worth holding rather than trusting to everyone remembering. A plain [Fact] added later
/// would run on a thread-pool thread, where a view can't be made at all and a menu can be and
/// shouldn't, and a test that started naming the app's Keychain store or its folders would be
/// reading the real account.
/// </remarks>
public class HostTests
{
    [AppKitFact]
    public void A_test_here_runs_on_the_main_thread()
        => Assert.True(NSThread.IsMain);

    [AppKitFact]
    public async Task An_await_in_a_test_here_comes_back_to_the_main_thread()
    {
        await Task.Delay(1, TestContext.Current.CancellationToken);

        Assert.True(NSThread.IsMain);
    }

    [AppKitFact]
    public void No_test_here_is_left_off_the_main_thread()
    {
        var stragglers = typeof(HostTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
            .Where(m => m.GetCustomAttribute<AppKitFactAttribute>() is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stragglers.Count == 0, "these run off the main thread and want [AppKitFact]: " + string.Join(", ", stragglers));
    }

    [AppKitFact]
    public void The_tests_here_run_one_at_a_time()
    {
        var parallel = typeof(HostTests).Assembly.GetCustomAttribute<ParallelizationAttribute>();

        Assert.True(parallel?.GetMode() == ParallelMode.None, "the tests here are set to run side by side");
    }

    [AppKitFact]
    public void The_bundle_these_run_in_isnt_the_apps()
    {
        // Its defaults, and whatever else AppKit keeps by bundle id, are under this name.
        Assert.Equal("net.tridian.termyn.tests", NSBundle.MainBundle.BundleIdentifier);
    }

    [AppKitFact]
    public void Nothing_here_names_the_Keychain_store_or_the_apps_folders()
    {
        // They're in Termyn.Platform.Mac, and the compiler only keeps a reference that's used.
        var names = typeof(HostTests).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain("Termyn.Platform.Mac", names);
    }
}
