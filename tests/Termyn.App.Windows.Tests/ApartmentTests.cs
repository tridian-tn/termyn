using System.Reflection;
using System.Threading;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That these tests run where Windows Forms can be run.
/// </summary>
/// <remarks>
/// Windows Forms wants a single-threaded apartment and a message pump. xUnit gives a test neither:
/// its bodies run on thread-pool threads, which are MTA, with nothing pumping. Most of what these
/// tests do works there anyway — setting a selection is a synchronous SendMessage — and the parts
/// that don't are the ones that have been failing on the build agent and never here.
///
/// So every test in this assembly is a WinForms one, and that is worth holding rather than trusting
/// to everyone remembering: a plain [Fact] added later would run in the wrong place and nothing
/// would say so until a build went red for reasons nobody could reproduce.
/// </remarks>
public class ApartmentTests
{
    [WinFormsFact]
    public void A_test_here_runs_single_threaded_with_something_pumping()
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
    }

    [WinFormsFact]
    public void No_test_here_is_left_in_the_wrong_apartment()
    {
        var stragglers = typeof(ApartmentTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
            .Where(m => m.GetCustomAttribute<WinFormsFactAttribute>() is null
                     && m.GetCustomAttribute<WinFormsTheoryAttribute>() is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stragglers.Count == 0,
            "these run in the default apartment and want [WinFormsFact] or [WinFormsTheory]: "
            + string.Join(", ", stragglers));
    }
}
