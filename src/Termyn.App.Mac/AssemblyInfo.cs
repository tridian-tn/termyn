using System.Runtime.CompilerServices;

// The window's classes are internal, with nothing outside the app to use them, and the tests build
// them directly: the keys, the menus and the popovers are what's being tested.
[assembly: InternalsVisibleTo("Termyn.App.Mac.Tests")]
