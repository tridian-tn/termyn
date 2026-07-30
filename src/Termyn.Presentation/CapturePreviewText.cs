using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>
/// Renders what the local parser made of some capture text, for the line under a quick-add box.
/// Shared by the main window and the global one, which show the same thing — and here rather than
/// in the app, because none of it needs a window and all of it is user-facing wording.
/// </summary>
public static class CapturePreviewText
{
    public static string For(CapturePreview preview)
    {
        var parse = preview.Parse;
        var parts = new List<string> { $"\"{parse.Content}\"" };

        if (parse.ProjectName is { } project)
            parts.Add(preview.ProjectResolved ? "#" + project : $"#{project} (unknown — goes to Inbox)");
        if (parse.SectionName is { } section)
            parts.Add(preview.SectionResolved ? "/" + section : $"/{section} (unknown)");
        foreach (var label in parse.Labels)
            parts.Add("@" + label);
        if (parse.Priority != Priority.P4)
            parts.Add(parse.Priority.ToString());
        if (parse.DueDate is { } date)
            parts.Add(parse.DueTime is { } time ? $"{date:yyyy-MM-dd} {time:HH:mm}" : $"{date:yyyy-MM-dd}");
        if (parse.Unsupported.Count > 0)
            parts.Add("(needs a connection: " + string.Join(", ", parse.Unsupported) + ")");

        return string.Join("  ·  ", parts);
    }
}
