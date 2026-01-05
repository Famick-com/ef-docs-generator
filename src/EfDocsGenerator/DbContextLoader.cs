using System.Reflection;
using System.Runtime.Loader;
using Microsoft.EntityFrameworkCore;

namespace EfDocsGenerator;

public class DbContextLoader : IDisposable
{
    private readonly AssemblyLoadContext _loadContext;
    private readonly Assembly _assembly;

    public DbContextLoader(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var assemblyDir = Path.GetDirectoryName(fullPath)!;

        _loadContext = new AssemblyLoadContext("DbContextLoader", isCollectible: true);

        // Add resolver for dependencies in the same directory
        _loadContext.Resolving += (context, name) =>
        {
            var dependencyPath = Path.Combine(assemblyDir, $"{name.Name}.dll");
            if (File.Exists(dependencyPath))
            {
                return context.LoadFromAssemblyPath(dependencyPath);
            }
            return null;
        };

        _assembly = _loadContext.LoadFromAssemblyPath(fullPath);
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

        // Try constructor with DbContextOptions
        var optionsCtor = contextType.GetConstructors()
            .FirstOrDefault(c =>
            {
                var parameters = c.GetParameters();
                return parameters.Length == 1 &&
                       parameters[0].ParameterType.IsGenericType &&
                       parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(DbContextOptions<>);
            });

        if (optionsCtor != null)
        {
            // Create options using SQLite in-memory (just to build the model)
            var optionsType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
            var optionsBuilder = Activator.CreateInstance(optionsType)!;

            var useSqliteMethod = typeof(SqliteDbContextOptionsBuilderExtensions)
                .GetMethods()
                .First(m => m.Name == "UseSqlite" &&
                           m.GetParameters().Length == 2 &&
                           m.GetParameters()[0].ParameterType.IsGenericType);

            var genericUseSqlite = useSqliteMethod.MakeGenericMethod(contextType);
            genericUseSqlite.Invoke(null, [optionsBuilder, "Data Source=:memory:"]);

            var optionsProperty = optionsType.GetProperty("Options")!;
            var options = optionsProperty.GetValue(optionsBuilder);

            return (DbContext)optionsCtor.Invoke([options])!;
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
