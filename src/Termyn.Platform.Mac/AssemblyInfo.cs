using System.Runtime.CompilerServices;

// The Keychain store and the folders take a service and a library of their own from the tests, and
// nothing outside the assembly has any business choosing either.
[assembly: InternalsVisibleTo("Termyn.Platform.Mac.Tests")]
