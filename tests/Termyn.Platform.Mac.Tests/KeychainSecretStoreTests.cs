using System.Diagnostics;
using System.Runtime.InteropServices;
using Foundation;
using Security;
using Termyn.Core.Platform;

namespace Termyn.Platform.Mac.Tests;

/// <summary>
/// The token in the Keychain: what's stored, what counts as missing and as out of reach, and when
/// it's asked for again.
/// </summary>
/// <remarks>
/// Against the default keychain, under a service of their own, so the real item is never touched.
/// The store can only be told where to look by service, and a keychain of their own would mean
/// changing which one is the user's default, which a crash would leave changed. An item left behind
/// by a crash is cleared by the next run.
///
/// Prompts are off for the whole process: what would have asked fails instead. So no run can stop
/// to wait for someone, which also gives a way to see the store turned down.
/// </remarks>
public sealed class KeychainSecretStoreTests : IDisposable
{
    private const string Service = "net.tridian.termyn.tests";
    private const string Account = "todoist-token";

    public KeychainSecretStoreTests()
    {
        Assert.Equal(0, SecKeychainSetUserInteractionAllowed(false));
        Forget();
    }

    public void Dispose() => Forget();

    [Fact]
    public void A_token_set_is_read_back_by_another_store()
    {
        // Another store, which has nothing in memory, so it can only have come from the Keychain.
        Store().SetToken("tok");

        Assert.Equal("tok", Store().GetToken());
    }

    [Fact]
    public void A_token_set_over_another_replaces_it()
    {
        Store().SetToken("first");
        Store().SetToken("second");

        Assert.Equal("second", Store().GetToken());
    }

    [Fact]
    public void A_token_cleared_is_gone()
    {
        var store = Store();
        store.SetToken("tok");
        store.ClearToken();

        Assert.Null(store.GetToken());
        Assert.Null(Store().GetToken());
    }

    [Fact]
    public void Clearing_when_nothing_is_stored_is_fine()
        => Store().ClearToken();

    [Fact]
    public void No_item_reads_as_missing()
    {
        var read = Store().ReadToken();

        Assert.Null(read.Token);
        Assert.False(read.Unreachable);
    }

    [Fact]
    public void An_item_that_wont_be_handed_over_reads_as_out_of_reach()
    {
        // Made by another program, so this one would have to ask, and with prompts off it can't.
        // What a locked Keychain or an unanswered prompt looks like from here: opposite in meaning
        // to no item at all, since the account is still signed in.
        Assert.Equal(0, Security("add-generic-password", "-s", Service, "-a", Account, "-w", "theirs"));

        var read = Store().ReadToken();

        Assert.Null(read.Token);
        Assert.True(read.Unreachable);
    }

    [Fact]
    public void A_token_read_once_is_held_rather_than_asked_for_again()
    {
        // Every ask is a prompt until Always Allow, and the engine asks before every request.
        Store().SetToken("held");
        var store = Store();
        Assert.Equal("held", store.GetToken());

        Store().ClearToken();

        Assert.Equal("held", store.GetToken());
    }

    [Fact]
    public void A_token_set_carries_the_items_name()
    {
        Store().SetToken("tok");

        Assert.Equal("Termyn – Todoist API token", StoredName());
    }

    [Fact]
    public void An_item_under_the_old_name_is_renamed_when_read()
    {
        Assert.Equal(SecStatusCode.Success, SecKeyChain.Add(new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Account,
            ValueData = NSData.FromString("old", NSStringEncoding.UTF8),
        }));
        Assert.Equal(Service, StoredName());

        Assert.Equal("old", Store().GetToken());

        Assert.Equal("Termyn – Todoist API token", StoredName());
        Assert.Equal("old", Store().GetToken());
    }

    private static KeychainSecretStore Store() => new(Service);

    private static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = Account };

    private static string? StoredName() => SecKeyChain.QueryAsRecord(Query(), out _)?.Label;

    /// <summary>Clears the item, by way of whichever program may: only its maker can, without asking.</summary>
    private static void Forget()
    {
        if (SecKeyChain.Remove(Query()) is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            Security("delete-generic-password", "-s", Service, "-a", Account);
    }

    /// <summary>Runs the system's keychain tool, as a program other than this one.</summary>
    /// <returns>What it exited with</returns>
    private static int Security(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/security") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var tool = Process.Start(start)!;
        tool.WaitForExit();
        return tool.ExitCode;
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.U1)] bool state);
}
