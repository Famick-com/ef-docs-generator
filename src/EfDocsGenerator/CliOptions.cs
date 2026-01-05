namespace EfDocsGenerator;

public class CliOptions
{
    public string? AssemblyPath { get; set; }
    public string? StartupAssemblyPath { get; set; }
    public string? ContextName { get; set; }
    public string OutputPath { get; set; } = "schema.md";
    public string DocsPath { get; set; } = "docs/entities";
    public bool UseTableNames { get; set; } = true;
    public bool ExcludeAudit { get; set; } = false;
    public bool IncludeOwned { get; set; } = true;
    public bool CollapseManyToMany { get; set; } = true;
    public bool GenerateEntityDocs { get; set; } = true;
    public bool ListContexts { get; set; } = false;
    public string[] ExcludeEntities { get; set; } = [];
    public string[] AuditColumns { get; set; } = ["CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "DeletedAt", "DeletedBy", "IsDeleted"];

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly" or "-a":
                    if (i + 1 < args.Length) options.AssemblyPath = args[++i];
                    break;
                case "--startup-assembly" or "-s":
                    if (i + 1 < args.Length) options.StartupAssemblyPath = args[++i];
                    break;
                case "--context" or "-c":
                    if (i + 1 < args.Length) options.ContextName = args[++i];
                    break;
                case "--output" or "-o":
                    if (i + 1 < args.Length) options.OutputPath = args[++i];
                    break;
                case "--docs" or "-d":
                    if (i + 1 < args.Length) options.DocsPath = args[++i];
                    break;
                case "--use-table-names":
                    options.UseTableNames = true;
                    break;
                case "--use-entity-names":
                    options.UseTableNames = false;
                    break;
                case "--exclude-audit":
                    options.ExcludeAudit = true;
                    break;
                case "--audit-columns":
                    if (i + 1 < args.Length) options.AuditColumns = args[++i].Split(',');
                    break;
                case "--exclude-owned":
                    options.IncludeOwned = false;
                    break;
                case "--expand-many-to-many":
                    options.CollapseManyToMany = false;
                    break;
                case "--no-entity-docs":
                    options.GenerateEntityDocs = false;
                    break;
                case "--exclude":
                    if (i + 1 < args.Length) options.ExcludeEntities = args[++i].Split(',');
                    break;
                case "--list-contexts" or "-l":
                    options.ListContexts = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return options;
    }

    public bool Validate(out string error)
    {
        if (string.IsNullOrEmpty(AssemblyPath) && !ListContexts)
        {
            error = "Error: --assembly is required. Use --help for usage information.";
            return false;
        }

        if (!string.IsNullOrEmpty(AssemblyPath) && !File.Exists(AssemblyPath))
        {
            error = $"Error: Assembly not found: {AssemblyPath}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            EF Docs Generator - Generate documentation from Entity Framework Core models

            Usage: ef-docs [options]

            Required:
              -a, --assembly <path>        Path to the assembly containing DbContext

            Optional:
              -s, --startup-assembly <path>  Path to startup assembly (for design-time services)
              -c, --context <name>         DbContext class name (auto-detected if only one exists)
              -o, --output <path>          Output path for Mermaid ER diagram (default: schema.md)
              -d, --docs <path>            Output directory for entity docs (default: docs/entities)
              -l, --list-contexts          List all DbContext types in the assembly and exit

            Diagram Options:
              --use-table-names            Use database table names (default)
              --use-entity-names           Use CLR entity class names instead
              --exclude-audit              Exclude audit columns from diagrams
              --audit-columns <cols>       Comma-separated audit column names to exclude
              --exclude-owned              Exclude owned type properties
              --expand-many-to-many        Show junction tables for many-to-many relationships

            Documentation Options:
              --no-entity-docs             Skip generating individual entity documentation
              --exclude <entities>         Comma-separated list of entities to exclude

            Examples:
              # Generate docs from a compiled assembly
              ef-docs -a ./bin/Debug/net8.0/MyApp.dll

              # Specify context when multiple exist
              ef-docs -a ./bin/Debug/net8.0/MyApp.dll -c MyDbContext

              # Custom output locations
              ef-docs -a ./bin/Debug/net8.0/MyApp.dll -o docs/schema.md -d docs/entities

              # List available contexts
              ef-docs -a ./bin/Debug/net8.0/MyApp.dll --list-contexts

            For more information, visit: https://github.com/miketherien/ef-docs-generator
            """);
    }
}
