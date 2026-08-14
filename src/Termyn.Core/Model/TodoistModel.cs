using System.Text.Json.Nodes;

namespace Termyn.Core.Model;

/// <summary>
/// In-memory raw-resource store keyed by type + id. The <see cref="JsonObject"/> per resource is
/// the authoritative local copy; typed access goes through <see cref="Projections"/>.
/// </summary>
public sealed class TodoistModel
{
    /// <summary>Fields that hold a reference to another resource's id.</summary>
    public static readonly string[] ReferenceKeys = ["parent_id", "section_id", "project_id", "item_id"];

    private readonly Dictionary<string, Dictionary<string, JsonObject>> _byType = new();

    /// <summary>
    /// Projected tasks, held until their JSON changes.
    /// </summary>
    /// <remarks>
    /// Only tasks are cached, because only tasks are numerous. Every publish reads all of them and a
    /// publish happens on every keystroke, write and sync, so re-parsing five thousand unchanged
    /// objects each time was the largest single cost in getting a write onto the screen. Every path
    /// that changes a task's JSON goes through this type, which is what makes the cache safe to keep.
    /// </remarks>
    private readonly Dictionary<string, TaskItem> _projectedItems = new(StringComparer.Ordinal);

    public string SyncToken { get; set; } = "*";

    public JsonObject? Get(string type, string id)
        => _byType.TryGetValue(type, out var m) && m.TryGetValue(id, out var o) ? o : null;

    /// <summary>Finds a resource by id across all types, returning its type and object.</summary>
    public (string Type, JsonObject Json)? Find(string id)
    {
        foreach (var (type, map) in _byType)
            if (map.TryGetValue(id, out var o))
                return (type, o);
        return null;
    }

    public void Upsert(string type, string id, JsonObject json)
    {
        if (!_byType.TryGetValue(type, out var m))
        {
            m = new Dictionary<string, JsonObject>();
            _byType[type] = m;
        }
        m[id] = json;
        Invalidate(type, id);
    }

    public bool Remove(string type, string id)
    {
        Invalidate(type, id);
        return _byType.TryGetValue(type, out var m) && m.Remove(id);
    }

    public void Rename(string type, string oldId, string newId)
    {
        if (_byType.TryGetValue(type, out var m) && m.Remove(oldId, out var o))
        {
            o["id"] = newId;
            m[newId] = o;
            Invalidate(type, oldId);
            Invalidate(type, newId);
        }
    }

    /// <summary>
    /// Drops a cached projection whose JSON has moved on.
    /// </summary>
    /// <remarks>
    /// Only the calls from <see cref="Upsert"/> and <see cref="RewriteReferences"/> can prevent a
    /// stale read — those are the paths where a task's JSON changes while it is still held. The rest
    /// are there so the cache can't outlive the model it was projected from: nothing reads a
    /// projection whose resource has gone, but without this it would sit there for the session.
    /// </remarks>
    private void Invalidate(string type, string id)
    {
        if (type == ResourceType.Items)
            _projectedItems.Remove(id);
    }

    /// <summary>
    /// Repoints reference fields from <paramref name="oldId"/> to <paramref name="newId"/> across
    /// every retained resource, so children created offline don't keep a stale temporary parent.
    /// </summary>
    public IEnumerable<(string Type, string Id, JsonObject Json)> RewriteReferences(string oldId, string newId)
    {
        foreach (var (type, map) in _byType)
        {
            foreach (var (id, obj) in map)
            {
                var changed = false;
                foreach (var key in ReferenceKeys)
                {
                    if (obj.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.ToString() == oldId)
                    {
                        obj[key] = newId;
                        changed = true;
                    }
                }
                if (changed)
                {
                    // Mutated in place rather than replaced, so the cache doesn't hear about it from
                    // Upsert the way every other edit does.
                    Invalidate(type, id);
                    yield return (type, id, obj);
                }
            }
        }
    }

    public void Clear()
    {
        _byType.Clear();
        _projectedItems.Clear();
    }

    public IEnumerable<JsonObject> All(string type)
        => _byType.TryGetValue(type, out var m) ? m.Values : [];

    /// <summary>
    /// The keys held for a type, as a copy so the caller can remove while walking them. These are
    /// the model's own keys, which for a singleton is the type name rather than anything inside.
    /// </summary>
    public IReadOnlyList<string> Keys(string type)
        => _byType.TryGetValue(type, out var m) ? m.Keys.ToList() : [];

    public IEnumerable<TaskItem> Items()
    {
        if (!_byType.TryGetValue(ResourceType.Items, out var items))
            return [];

        var projected = new List<TaskItem>(items.Count);
        foreach (var (id, json) in items)
        {
            if (!_projectedItems.TryGetValue(id, out var item))
            {
                item = Projections.ToTaskItem(json);
                _projectedItems[id] = item;
            }
            projected.Add(item);
        }
        return projected;
    }

    public IEnumerable<Project> Projects() => All(ResourceType.Projects).Select(Projections.ToProject);

    public IEnumerable<Section> Sections() => All(ResourceType.Sections).Select(Projections.ToSection);

    public IEnumerable<Label> Labels() => All(ResourceType.Labels).Select(Projections.ToLabel);

    public IEnumerable<Filter> Filters() => All(ResourceType.Filters).Select(Projections.ToFilter);

    public IEnumerable<Reminder> Reminders() => All(ResourceType.Reminders).Select(Projections.ToReminder);

    /// <summary>Every comment held, on tasks and on projects alike.</summary>
    public IEnumerable<Comment> Comments()
        => All(ResourceType.Notes).Concat(All(ResourceType.ProjectNotes)).Select(Projections.ToComment);

    /// <summary>
    /// How many comments each task and project carries.
    /// </summary>
    /// <remarks>
    /// Read straight off the raw JSON rather than by projecting every comment. The outline asks for
    /// this on every publish — which is every keystroke — and all it needs is who each one belongs
    /// to, so building a Comment per row to then throw it away is work nobody is waiting for.
    /// </remarks>
    public Dictionary<string, int> CommentCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var json in All(ResourceType.Notes).Concat(All(ResourceType.ProjectNotes)))
        {
            if ((JsonRead.String(json, "item_id") ?? JsonRead.String(json, "project_id")) is not { } owner)
                continue;

            counts[owner] = counts.GetValueOrDefault(owner) + 1;
        }

        return counts;
    }

    /// <summary>
    /// The account's plan limits, or null before the first sync has brought them. Null is not
    /// "unlimited": a caller gating on a plan feature has to treat not-knowing as not-allowed, or
    /// it will offer something the server then refuses.
    /// </summary>
    public PlanLimits? PlanLimits()
        => Get(ResourceType.UserPlanLimits, ResourceType.UserPlanLimits) is { } o
            ? Projections.ToPlanLimits(o)
            : null;
}
