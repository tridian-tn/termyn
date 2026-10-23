namespace Termyn.Core.Capture;

/// <summary>
/// A reminder a capture's <c>!</c> shortcut names, read the way Todoist's apps read it. One of two
/// shapes, matching the two reminders the sync engine can queue: a moment of its own, or a span
/// before the task falls due.
/// </summary>
/// <remarks>
/// The parser reads these whether or not the plan takes reminders — it doesn't know the plan. The
/// caller gates them, adding each onto the created task's id only when the plan allows, and never
/// sending one the server would reject.
/// </remarks>
public abstract record CaptureReminder;

/// <summary>A reminder for a fixed moment, whatever the task's own due date is.</summary>
/// <param name="Date">The day it fires on</param>
/// <param name="Time">The time of day it fires at</param>
public sealed record AbsoluteReminder(DateOnly Date, TimeOnly Time) : CaptureReminder;

/// <summary>A reminder a number of minutes before the task falls due.</summary>
/// <param name="MinutesBefore">How long before the due time it fires</param>
public sealed record RelativeReminder(int MinutesBefore) : CaptureReminder;
