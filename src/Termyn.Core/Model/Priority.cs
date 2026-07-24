namespace Termyn.Core.Model;

/// <summary>UI-facing task priority. P1 is highest; P4 is the default/lowest.</summary>
public enum Priority
{
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4,
}

/// <summary>
/// Converts between Todoist's API priority (1 = normal … 4 = urgent) and Termyn's UI priority
/// (P1 = highest … P4 = lowest). The two scales are inverted; this type is the single owner of
/// that conversion so the UI never sees raw API numbers.
/// </summary>
public static class PriorityMap
{
    /// <summary>Maps an API priority (1–4) to the UI priority.</summary>
    public static Priority FromApi(int apiPriority) => apiPriority switch
    {
        4 => Priority.P1,
        3 => Priority.P2,
        2 => Priority.P3,
        _ => Priority.P4,
    };

    /// <summary>Maps a UI priority to the API priority (1–4).</summary>
    public static int ToApi(Priority priority) => priority switch
    {
        Priority.P1 => 4,
        Priority.P2 => 3,
        Priority.P3 => 2,
        _ => 1,
    };
}
