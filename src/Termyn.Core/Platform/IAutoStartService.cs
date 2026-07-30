namespace Termyn.Core.Platform;

/// <summary>Controls whether Termyn starts with the desktop session.</summary>
public interface IAutoStartService
{
    /// <summary>
    /// Whether Termyn is currently registered to launch at login. Reflects what the OS actually
    /// holds, not what was last asked for: the entry can be removed by a startup manager, and a
    /// settings screen that showed its own last answer would then be wrong.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>Registers or removes the launch-at-login entry.</summary>
    /// <returns>False when the OS refused, so the caller can leave the setting where it was.</returns>
    bool SetEnabled(bool enabled);
}
