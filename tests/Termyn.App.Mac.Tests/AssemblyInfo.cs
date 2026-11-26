using Xunit.Sdk;
using Xunit.v3;

// One test at a time, the way the app runs: they all end up on the main thread anyway.
[assembly: Parallelization(Mode = ParallelMode.None)]
