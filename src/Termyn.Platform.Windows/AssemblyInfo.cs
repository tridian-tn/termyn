using System.Runtime.CompilerServices;

// The key the autostart entry is written to, and the hotkey's key-name mapping, are internal: both
// are implementation detail the app has no business setting, and both are worth a test.
[assembly: InternalsVisibleTo("Termyn.Platform.Windows.Tests")]
