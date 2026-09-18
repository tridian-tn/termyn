namespace Termyn.Core.Logging;

/// <summary>
/// Where Termyn writes down what went wrong, for somebody reading it afterwards.
/// </summary>
/// <remarks>
/// Nothing of the account's own goes in here: no task, project, section or label names, no
/// descriptions, no comments, and never the token. Ids, counts and the server's own words about a
/// refused command say enough to follow what happened; a log carrying the account's text would be a
/// second copy of somebody's tasks in a file nothing sweeps and nothing encrypts.
///
/// Thin on purpose: three levels, a line each. Anything that can't be written is dropped rather
/// than thrown, because a diagnostic is never a reason to stop working.
/// </remarks>
public interface ILog
{
    /// <summary>Something worth knowing later: a session starting, a cache rebuilt.</summary>
    /// <param name="message">What happened, in one line</param>
    void Info(string message);

    /// <summary>Something went wrong that Termyn carried on from.</summary>
    /// <param name="message">What happened, in one line</param>
    /// <param name="error">The exception behind it, if there was one</param>
    void Warn(string message, Exception? error = null);

    /// <summary>Something went wrong that the user saw.</summary>
    /// <param name="message">What happened, in one line</param>
    /// <param name="error">The exception behind it, if there was one</param>
    void Error(string message, Exception? error = null);
}
