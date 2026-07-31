using Termyn.Core.Settings;

namespace Termyn.Core.Platform;

/// <summary>Registers a system-wide key combination and reports when it is pressed.</summary>
public interface IGlobalHotkey : IDisposable
{
    /// <summary>Raised when the registered combination is pressed, anywhere on the desktop.</summary>
    event Action? Pressed;

    /// <summary>The binding currently held, or null when nothing is registered.</summary>
    HotkeyBinding? Current { get; }

    /// <summary>
    /// Takes the combination, replacing whatever was registered before.
    /// </summary>
    /// <returns>
    /// False when the desktop refused it — most often because another application already owns it.
    /// The caller is expected to say so rather than leave the user pressing a key that does nothing.
    /// </returns>
    bool Register(HotkeyBinding binding);

    /// <summary>Gives the combination back to the rest of the desktop.</summary>
    void Unregister();
}
