using Termyn.Core.Platform;

namespace Termyn.Core.Settings;

/// <summary>
/// Settles the disagreement between what Termyn's settings say about launching at login and what the
/// machine is actually set to.
/// </summary>
/// <remarks>
/// Two things write the same registry entry — the installer's tick-box and Termyn's own settings
/// screen — so on any given start the file and the machine can disagree, and which one is right
/// depends entirely on whether Termyn has ever had settings to speak for.
/// </remarks>
public static class StartupReconciliation
{
    /// <summary>
    /// Brings the two into line and returns the settings to run with.
    /// </summary>
    /// <returns>The settings, with launch-at-login as it should now be understood.</returns>
    public static AppSettings OnLaunch(SettingsStore store, AppSettings settings, IAutoStartService autoStart)
    {
        // A file was there and we read it, so it is a preference the user expressed. Re-asserted
        // rather than assumed: the entry can be removed by a startup manager, and it holds a path a
        // reinstall elsewhere would have left pointing at the old binary. Unconditional, because
        // turning it off when it is already off is a no-op.
        if (store.Existed && store.Readable)
        {
            autoStart.SetEnabled(settings.LaunchAtLogin);
            return settings;
        }

        // Either a first run, or a file we couldn't read and had to fall back from. Either way these
        // settings are our defaults, not the user's wishes, and asserting a default here deletes the
        // entry the installer just wrote — or the one the user set before the file went bad. What
        // the machine says is the better evidence, so adopt it.
        var adopted = settings with { LaunchAtLogin = autoStart.IsEnabled };

        // Only worth persisting when there is nothing on disk to persist over. A file we failed to
        // read is one the store already refuses to write, and it may still be perfectly good.
        if (!store.Existed)
            store.Save(adopted);

        return adopted;
    }
}
