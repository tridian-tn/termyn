using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Platform;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the read-only task list.</summary>
public sealed record TaskRow(string Content, Priority Priority, string Project, string Due);

/// <summary>Loads a full sync and exposes a read-only, flattened active-task list.</summary>
public sealed class MainPresenter
{
    private readonly ITodoistApi _api;
    private readonly ISecretStore _secrets;

    public MainPresenter(ITodoistApi api, ISecretStore secrets)
    {
        _api = api;
        _secrets = secrets;
    }

    public IReadOnlyList<TaskRow> Rows { get; private set; } = [];

    /// <summary>Performs a full sync and materialises active tasks into <see cref="Rows"/>.</summary>
    /// <remarks>If Todoist rejects the token, it is cleared so the next launch re-prompts.</remarks>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var token = _secrets.GetToken()
                    ?? throw new InvalidOperationException("No Todoist token is stored.");

        SyncResult result;
        try
        {
            result = await _api.SyncAsync(token, "*", ["projects", "items"], ct);
        }
        catch (TodoistAuthException)
        {
            _secrets.ClearToken();
            throw;
        }

        var projectNames = result.Projects
            .DistinctBy(p => p.Id)
            .ToDictionary(p => p.Id, p => p.Name);

        Rows = result.Items
            .Where(i => !i.Completed)
            .OrderBy(i => i.ChildOrder)
            .Select(i => new TaskRow(
                i.Content,
                i.Priority,
                i.ProjectId is not null && projectNames.TryGetValue(i.ProjectId, out var name) ? name : string.Empty,
                i.DueText ?? i.DueDate ?? string.Empty))
            .ToList();
    }
}
