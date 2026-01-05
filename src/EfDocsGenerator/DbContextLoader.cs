using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EfDocsGenerator;

public class DbContextLoader : IDisposable
{
    private readonly AssemblyLoadContext _loadContext;
    private readonly Assembly _assembly;
    private readonly string _assemblyDir;
    private readonly Dictionary<string, string> _depsAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);

    public DbContextLoader(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        _assemblyDir = Path.GetDirectoryName(fullPath)!;

        // Parse deps.json to find NuGet package paths
        LoadDepsJson(fullPath);

        _loadContext = new AssemblyLoadContext("DbContextLoader", isCollectible: true);

        // Add resolver for dependencies
        _loadContext.Resolving += ResolveAssembly;

        _assembly = _loadContext.LoadFromAssemblyPath(fullPath);
    }

    private void LoadDepsJson(string assemblyPath)
    {
        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (!File.Exists(depsPath)) return;

        try
        {
            using var stream = File.OpenRead(depsPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            // Get the runtime target
            if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget)) return;
            var targetName = runtimeTarget.GetProperty("name").GetString();
            if (targetName == null) return;

            if (!root.TryGetProperty("targets", out var targets)) return;
            if (!targets.TryGetProperty(targetName, out var target)) return;

            // Find NuGet packages path
            var nugetPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

            foreach (var lib in target.EnumerateObject())
            {
                if (!lib.Value.TryGetProperty("runtime", out var runtime)) continue;

                // Library name format: "Package/Version"
                var parts = lib.Name.Split('/');
                if (parts.Length != 2) continue;
                var packageName = parts[0].ToLowerInvariant();
                var version = parts[1];

                foreach (var dll in runtime.EnumerateObject())
                {
                    var dllName = Path.GetFileNameWithoutExtension(dll.Name);
                    var dllPath = Path.Combine(nugetPath, packageName, version, dll.Name.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(dllPath) && !_depsAssemblyPaths.ContainsKey(dllName))
                    {
                        _depsAssemblyPaths[dllName] = dllPath;
                    }
                }
            }
        }
        catch
        {
            // Ignore deps.json parsing errors
        }
    }

    private Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name == null) return null;

        // First check the assembly directory
        var localPath = Path.Combine(_assemblyDir, $"{name.Name}.dll");
        if (File.Exists(localPath))
        {
            return context.LoadFromAssemblyPath(localPath);
        }

        // Then check deps.json resolved paths
        if (_depsAssemblyPaths.TryGetValue(name.Name, out var depsPath) && File.Exists(depsPath))
        {
            return context.LoadFromAssemblyPath(depsPath);
        }

        // Try loading from default context (for framework assemblies)
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<Type> FindDbContextTypes()
    {
        var dbContextType = typeof(DbContext);

        return _assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && dbContextType.IsAssignableFrom(t))
            .ToList();
    }

    public DbContext CreateDbContext(string? contextName = null)
    {
        var contextTypes = FindDbContextTypes();

        if (contextTypes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No DbContext types found in assembly: {_assembly.GetName().Name}");
        }

        Type contextType;

        if (!string.IsNullOrEmpty(contextName))
        {
            contextType = contextTypes.FirstOrDefault(t =>
                t.Name.Equals(contextName, StringComparison.OrdinalIgnoreCase) ||
                t.FullName?.Equals(contextName, StringComparison.OrdinalIgnoreCase) == true)
                ?? throw new InvalidOperationException(
                    $"DbContext '{contextName}' not found. Available contexts: {string.Join(", ", contextTypes.Select(t => t.Name))}");
        }
        else if (contextTypes.Count == 1)
        {
            contextType = contextTypes[0];
        }
        else
        {
            throw new InvalidOperationException(
                $"Multiple DbContext types found. Please specify one with --context: {string.Join(", ", contextTypes.Select(t => t.Name))}");
        }

        return CreateDbContextInstance(contextType);
    }

    private DbContext CreateDbContextInstance(Type contextType)
    {
        // Try to find a design-time factory first
        var factoryType = _assembly.GetTypes()
            .FirstOrDefault(t =>
                t.GetInterfaces().Any(i =>
                    i.IsGenericType &&
                    i.GetGenericTypeDefinition().Name == "IDesignTimeDbContextFactory`1" &&
                    i.GetGenericArguments()[0] == contextType));

        if (factoryType != null)
        {
            var factory = Activator.CreateInstance(factoryType);
            var createMethod = factoryType.GetMethod("CreateDbContext");
            if (createMethod != null)
            {
                return (DbContext)createMethod.Invoke(factory, [Array.Empty<string>()])!;
            }
        }

        // Try parameterless constructor
        var parameterlessCtor = contextType.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor != null)
        {
            return (DbContext)Activator.CreateInstance(contextType)!;
        }

        // Try constructor with DbContextOptions<T> (may have additional optional parameters)
        var optionsCtor = contextType.GetConstructors()
            .FirstOrDefault(c =>
            {
                var parameters = c.GetParameters();
                if (parameters.Length == 0) return false;

                // First parameter must be DbContextOptions<T>
                var firstParam = parameters[0];
                if (!firstParam.ParameterType.IsGenericType ||
                    firstParam.ParameterType.GetGenericTypeDefinition() != typeof(DbContextOptions<>))
                    return false;

                // All other parameters must be optional or nullable
                return parameters.Skip(1).All(p => p.HasDefaultValue || p.IsOptional ||
                    (p.ParameterType.IsClass && Nullable.GetUnderlyingType(p.ParameterType) == null));
            });

        if (optionsCtor != null)
        {
            // Create options using SQLite in-memory (just to build the model)
            var optionsType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
            var optionsBuilder = Activator.CreateInstance(optionsType)!;

            // Use the extension method that takes (builder, connectionString, Action?)
            var useSqliteMethod = typeof(SqliteDbContextOptionsBuilderExtensions)
                .GetMethods()
                .First(m => m.Name == "UseSqlite" &&
                           m.IsGenericMethod &&
                           m.GetParameters().Length == 3 &&
                           m.GetParameters()[1].ParameterType == typeof(string));

            var genericUseSqlite = useSqliteMethod.MakeGenericMethod(contextType);
            genericUseSqlite.Invoke(null, [optionsBuilder, "Data Source=:memory:", null]);

            var optionsProperty = optionsType.GetProperty("Options", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? optionsType.GetProperties().First(p => p.Name == "Options" && p.PropertyType.IsGenericType);
            var options = optionsProperty.GetValue(optionsBuilder);

            // Build args array with options + nulls/defaults for optional params
            var ctorParams = optionsCtor.GetParameters();
            var args = new object?[ctorParams.Length];
            args[0] = options;
            for (int i = 1; i < ctorParams.Length; i++)
            {
                args[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : null;
            }

            return (DbContext)optionsCtor.Invoke(args)!;
        }

        // Try constructor with non-generic DbContextOptions
        var nonGenericOptionsCtor = contextType.GetConstructors()
            .FirstOrDefault(c =>
            {
                var parameters = c.GetParameters();
                return parameters.Length >= 1 &&
                       parameters[0].ParameterType == typeof(DbContextOptions);
            });

        if (nonGenericOptionsCtor != null)
        {
            var optionsBuilder = new DbContextOptionsBuilder();
            optionsBuilder.UseSqlite("Data Source=:memory:");
            var options = optionsBuilder.Options;

            var ctorParams = nonGenericOptionsCtor.GetParameters();
            var args = new object?[ctorParams.Length];
            args[0] = options;

            // Fill remaining parameters with null (for optional DI services)
            for (int i = 1; i < ctorParams.Length; i++)
            {
                args[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : null;
            }

            return (DbContext)nonGenericOptionsCtor.Invoke(args)!;
        }

        throw new InvalidOperationException(
            $"Unable to create instance of {contextType.Name}. " +
            "Please ensure the DbContext has a parameterless constructor, " +
            "a constructor accepting DbContextOptions, " +
            "or implement IDesignTimeDbContextFactory<T>.");
    }

    public void Dispose()
    {
        _loadContext.Unload();
    }
}
