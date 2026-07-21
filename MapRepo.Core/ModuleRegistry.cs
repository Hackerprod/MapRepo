using System.Reflection;
using Microsoft.Extensions.Logging;

namespace MapRepo.Core;

public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IRepositoryLanguageModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ModuleRegistry> _logger;

    public ModuleRegistry(ILogger<ModuleRegistry> logger) => _logger = logger;

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
            catch (Exception ex)
            {
                // Optional modules cannot prevent the core server from starting, but a silently
                // skipped module is otherwise invisible until someone wonders why a language never indexes.
                _logger.LogWarning(ex, "Failed to load optional module from {Path}", path);
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
