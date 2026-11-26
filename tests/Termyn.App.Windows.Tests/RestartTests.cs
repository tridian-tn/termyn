using System.Diagnostics;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The hand-off from the copy closing to restart to the one that replaces it.
/// </summary>
/// <remarks>
/// The new copy has to wait for the old one to be gone: started while it still held the
/// single-instance lock, it would hand itself over to a window on its way out and nothing would be
/// left running. That the lock is free once released is the instance's to check, and is checked
/// with it.
/// </remarks>
public class RestartTests
{
    [WinFormsFact]
    public void The_next_copy_is_told_which_one_it_replaces_and_nothing_else()
    {
        var info = Restart.StartInfo(@"C:\Apps\Termyn.exe", 4242);

        Assert.Equal(@"C:\Apps\Termyn.exe", info.FileName);
        Assert.Equal([Restart.AfterFlag, "4242"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    [WinFormsFact]
    public void What_it_was_told_reads_back()
        => Assert.Equal(4242, Restart.Replacing(Restart.StartInfo("Termyn.exe", 4242).ArgumentList.ToArray()));

    [WinFormsTheory]
    [InlineData("")]
    [InlineData("--tray")]
    [InlineData("--after")]
    [InlineData("--after soon")]
    [InlineData("--after -5")]
    [InlineData("--after 0")]
    public void A_launch_that_isnt_a_restart_replaces_nothing(string line)
        => Assert.Null(Restart.Replacing(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [WinFormsFact]
    public void A_copy_still_running_is_waited_for_until_the_wait_runs_out()
    {
        // This process is the one running here, and it isn't going anywhere.
        var waited = Stopwatch.StartNew();

        Assert.False(Restart.WaitFor(Environment.ProcessId, TimeSpan.FromMilliseconds(200)));
        Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(150), waited.Elapsed.ToString());
    }

    [WinFormsFact]
    public void A_copy_that_ends_during_the_wait_lets_the_next_one_go_on()
    {
        // About two seconds: ping waits a second between each of its three tries.
        using var closing = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var waited = Stopwatch.StartNew();

        Assert.True(Restart.WaitFor(closing.Id, TimeSpan.FromSeconds(30), program: "ping"));
        Assert.True(closing.HasExited);
        Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(500), waited.Elapsed.ToString());
    }

    [WinFormsFact]
    public void A_copy_already_gone_isnt_waited_for()
    {
        using var gone = Process.Start(new ProcessStartInfo("cmd", "/c exit") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        var id = gone.Id;
        gone.Dispose();

        var waited = Stopwatch.StartNew();

        Assert.True(Restart.WaitFor(id, TimeSpan.FromSeconds(30), program: "cmd"));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), waited.Elapsed.ToString());
    }

    [WinFormsFact]
    public void An_id_now_running_another_program_isnt_waited_for()
    {
        // The id was handed on after the copy it named ended. Waiting for whatever has it now could
        // hold the restart up for the whole wait.
        var waited = Stopwatch.StartNew();

        Assert.True(Restart.WaitFor(Environment.ProcessId, TimeSpan.FromSeconds(30), program: "not-termyn"));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), waited.Elapsed.ToString());
    }
}
