using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

// In place of the entry point xunit generates, which waits for the run on the thread it started on.
// Here that's the main thread, which the macOS runtime has already given a synchronization context
// that posts to the main run loop, so anything awaited with it captured never comes back: the run
// stops at "Discovering" for good, with no error and every worker idle. AppKit also wants that
// thread for the tests themselves, so the run goes elsewhere and this thread pumps for it.
//
// No Dock icon and no taking the focus from whatever's in front. NSApplication is set up as Main.cs
// in the app does it, but nothing here makes an AppDelegate, which is what opens the Keychain, the
// app's folders and the connection to Todoist.
NSApplication.Init();
NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Prohibited;

// The same choice the generated one makes: `dotnet test` asks for the testing platform by these
// switches, and anything else is someone running the executable by hand.
var run = Task.Run(() => args.Any(arg => arg is "--server" or "--internal-msbuild-node")
    ? TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions)
    : ConsoleRunner.Run(args));

while (!run.IsCompleted)
    NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.05));

return await run;
