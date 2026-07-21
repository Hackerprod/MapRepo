using System.Text.Json;
using MapRepo.Core;

namespace MapRepo.Server;

/// <summary>
/// Persists the set of registered repositories so the server restores them after a restart.
/// Stored next to the per-repository databases as data-v4/catalog.json.
/// </summary>
public sealed class RepositoryCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, RepositoryDefinition> _entries = new(StringComparer.OrdinalIgnoreCase);

    public RepositoryCatalog(IHostEnvironment environment)
    {
        var root = Path.Combine(environment.ContentRootPath, "data-v4");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "catalog.json");
        Load();
    }

    public IReadOnlyList<RepositoryDefinition> All()
    {
        lock (_lock) return _entries.Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Upsert(RepositoryDefinition definition)
    {
        lock (_lock)
        {
            _entries[definition.Id] = definition;
            Save();
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _entries.Remove(id);
            if (removed) Save();
            return removed;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var definitions = JsonSerializer.Deserialize<List<RepositoryDefinition>>(File.ReadAllText(_path), Options) ?? [];
            _entries = definitions.Where(d => !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.RootPath))
                .ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _entries = new Dictionary<string, RepositoryDefinition>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var payload = JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase), Options);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, payload);
        File.Move(temp, _path, overwrite: true);
    }
}
