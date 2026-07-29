using System.Text.Json.Nodes;

namespace Termyn.Core.Model;

/// <summary>
/// In-memory raw-resource store keyed by type + id. The <see cref="JsonObject"/> per resource is
/// the authoritative local copy; typed access goes through <see cref="Projections"/>.
/// </summary>
public sealed class TodoistModel
{
    /// <summary>Fields that hold a reference to another resource's id.</summary>
    public static readonly string[] ReferenceKeys = ["parent_id", "section_id", "project_id"];

    private readonly Dictionary<string, Dictionary<string, JsonObject>> _byType = new();

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
    }

    public bool Remove(string type, string id)
        => _byType.TryGetValue(type, out var m) && m.Remove(id);

    public void Rename(string type, string oldId, string newId)
    {
        if (_byType.TryGetValue(type, out var m) && m.Remove(oldId, out var o))
        {
            o["id"] = newId;
            m[newId] = o;
        }
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
                    yield return (type, id, obj);
            }
        }
    }

    public void Clear() => _byType.Clear();

    public IEnumerable<JsonObject> All(string type)
        => _byType.TryGetValue(type, out var m) ? m.Values : [];

    public IEnumerable<TaskItem> Items() => All(ResourceType.Items).Select(Projections.ToTaskItem);

    public IEnumerable<Project> Projects() => All(ResourceType.Projects).Select(Projections.ToProject);

    public IEnumerable<Section> Sections() => All(ResourceType.Sections).Select(Projections.ToSection);

    public IEnumerable<Label> Labels() => All(ResourceType.Labels).Select(Projections.ToLabel);

    public IEnumerable<Filter> Filters() => All(ResourceType.Filters).Select(Projections.ToFilter);

    public IEnumerable<Reminder> Reminders() => All(ResourceType.Reminders).Select(Projections.ToReminder);

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
