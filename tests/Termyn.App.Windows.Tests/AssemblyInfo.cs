using Xunit.Sdk;
using Xunit.v3;

// One test at a time, the way the app runs: on a single UI thread.
//
// A rich edit control keeps a font cache that every thread in the process shares. Tests here
// realise rich edit controls on a thread of their own each, and running them side by side
// occasionally leaves one face at one size broken for the whole process: every selection that lands
// in text set in it comes back at nought, in every control on every thread, until enough other
// fonts have been used to push it out of the cache. That's what the markdown tests kept failing on,
// a few at a time — code in Courier New, a heading, a bold word — and nearly always on a build agent.
// The same work on one thread has never done it, and the app never has a second UI thread.
//
// About a second longer for the whole assembly, against builds going red for reasons nobody could
// reproduce.
[assembly: Parallelization(Mode = ParallelMode.None)]
