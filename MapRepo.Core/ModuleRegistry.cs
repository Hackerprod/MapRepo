using System.Reflection;

namespace MapRepo.Core;

public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IRepositoryLanguageModule> _modules = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IRepositoryLanguageModule> Modules => _modules.Values;

    public void Register(IRepositoryLanguageModule module) => _modules[module.Descriptor.Id] = module;

    public void Discover(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "MapRepo.Module*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IRepositoryLanguageModule).IsAssignableFrom(t)))
                {
                    if (Activator.CreateInstance(type) is IRepositoryLanguageModule module) Register(module);
                }
            }
            catch
            {
                // Optional modules cannot prevent the core server from starting.
            }
        }
    }

    public IReadOnlyList<IRepositoryLanguageModule> Resolve(RepositoryDefinition repository)
    {
        if (repository.EnabledModules is { Count: > 0 })
            return repository.EnabledModules.Where(_modules.ContainsKey).Select(id => _modules[id]).ToArray();
        return Modules.ToArray();
    }
}
