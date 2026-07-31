using System.Runtime.InteropServices;
using Termyn.Core.Platform;
using Termyn.Core.Settings;

namespace Termyn.Platform.Windows;

/// <summary>
/// Registers a system-wide hotkey with <c>RegisterHotKey</c>, against a message-only window of its
/// own rather than the main form: the hotkey must keep working with the window closed to the tray,
/// and a registration tied to a window that comes and goes would go with it.
/// </summary>
public sealed class WindowsGlobalHotkey : IGlobalHotkey
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>Holding the key down must fire once, not as fast as the keyboard repeats.</summary>
    private const uint ModNoRepeat = 0x4000;

    private readonly MessageWindow _window;
    private bool _disposed;

    public WindowsGlobalHotkey() => _window = new MessageWindow(() => Pressed?.Invoke());

    public event Action? Pressed;

    public HotkeyBinding? Current { get; private set; }

    /// <summary>
    /// The window WM_HOTKEY is delivered to. Internal so a test can confirm it really is
    /// message-only, and can post to it rather than synthesising a keypress on the user's desktop.
    /// </summary>
    internal IntPtr Handle => _window.Handle;

    /// <inheritdoc />
    public bool Register(HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!binding.IsValid || ToVirtualKey(binding.Key) is not { } key)
            return false;

        Unregister();

        if (!RegisterHotKey(_window.Handle, HotkeyId, ToModifiers(binding.Modifiers) | ModNoRepeat, key))
            return false;

        Current = binding;
        return true;
    }

    public void Unregister()
    {
        if (Current is null)
            return;

        UnregisterHotKey(_window.Handle, HotkeyId);
        Current = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Unregister();
        _window.Dispose();
    }

    private static uint ToModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) result |= ModControl;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= ModShift;
        if (modifiers.HasFlag(HotkeyModifiers.Meta)) result |= ModWin;
        return result;
    }

    /// <summary>
    /// Maps a portable key name onto a virtual-key code. Digits are the awkward case: the name is
    /// "7" but the member is <c>D7</c>, so they can't go through <see cref="Enum.TryParse{T}(string, bool, out T)"/> as written.
    /// </summary>
    internal static uint? ToVirtualKey(string name)
    {
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            return (uint)(Keys.D0 + (name[0] - '0'));

        return Enum.TryParse<Keys>(name, ignoreCase: true, out var key) ? (uint)key : null;
    }

    // DllImport rather than LibraryImport: the generated marshalling for the latter needs unsafe
    // code, which is a lot to turn on for two calls that pass nothing but integers.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    /// <summary>A window with no chrome and no presence, existing only to receive WM_HOTKEY.</summary>
    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        /// <summary>
        /// Parenting to <c>HWND_MESSAGE</c> is what makes a window message-only: it gets posted
        /// messages and nothing else — no z-order, no taskbar presence, and none of the broadcasts
        /// every top-level window otherwise has to be sent.
        /// </summary>
        private static readonly IntPtr MessageOnly = new(-3);

        private readonly Action _onHotkey;

        public MessageWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams { Caption = "Termyn.Hotkey", Parent = MessageOnly });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && (int)m.WParam == HotkeyId)
                _onHotkey();

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
