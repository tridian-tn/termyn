using Termyn.App.Mac;

// No storyboard: the menus and windows are built in code, so what they are is in the source rather
// than in a file only Xcode's editor reads.
NSApplication.Init();
NSApplication.SharedApplication.Delegate = new AppDelegate();
NSApplication.Main(args);
