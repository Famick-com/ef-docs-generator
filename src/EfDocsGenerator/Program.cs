using EfDocsGenerator;

var options = CliOptions.Parse(args);

if (!options.Validate(out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Use --help for usage information.");
    return 1;
}

Console.WriteLine("EF Docs Generator");
Console.WriteLine("=================");
Console.WriteLine();

try
{
    using var loader = new DbContextLoader(options.AssemblyPath!);

    // List contexts mode
    if (options.ListContexts)
    {
        var contextTypes = loader.FindDbContextTypes();
        Console.WriteLine($"Found {contextTypes.Count} DbContext type(s):");
        Console.WriteLine();
        foreach (var type in contextTypes)
        {
            Console.WriteLine($"  - {type.FullName}");
        }
        return 0;
    }

    // Generate documentation
    Console.WriteLine($"Loading assembly: {options.AssemblyPath}");
    using var ctx = loader.CreateDbContext(options.ContextName);
    Console.WriteLine($"Using DbContext: {ctx.GetType().Name}");
    Console.WriteLine();

    var generator = new MermaidGenerator(options);

    // Generate ER diagram
    Console.WriteLine($"Generating ER diagram: {options.OutputPath}");
    var outputDir = Path.GetDirectoryName(options.OutputPath);
    if (!string.IsNullOrEmpty(outputDir))
    {
        Directory.CreateDirectory(outputDir);
    }

    var output = generator.Generate(ctx);
    File.WriteAllText(options.OutputPath, output);
    Console.WriteLine("  Done.");
    Console.WriteLine();

    // Generate entity documentation
    if (options.GenerateEntityDocs)
    {
        Console.WriteLine($"Generating entity documentation in: {options.DocsPath}");
        generator.GenerateEntityDocs(ctx);
        Console.WriteLine("  Done.");
    }

    Console.WriteLine();
    Console.WriteLine("Generation complete!");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
#if DEBUG
    Console.Error.WriteLine(ex.StackTrace);
#endif
    return 1;
}
