using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfDocsGenerator;

public partial class MermaidGenerator
{
    private readonly CliOptions _options;
    private readonly HashSet<string> _auditColumns;

    private bool UseTableNames => _options.UseTableNames;
    private bool ExcludeAudit => _options.ExcludeAudit;
    private bool IncludeOwned => _options.IncludeOwned;
    private bool CollapseManyToMany => _options.CollapseManyToMany;
    private string DocsPath => _options.DocsPath;

    public MermaidGenerator(CliOptions options)
    {
        _options = options;
        _auditColumns = new HashSet<string>(options.AuditColumns, StringComparer.OrdinalIgnoreCase);
    }

    public string Generate(DbContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Database Schema");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine("erDiagram");

        var entities = ctx.Model.GetEntityTypes()
            .Where(IncludeEntity)
            .ToList();

        foreach (var e in entities)
            RenderEntity(sb, e);

        foreach (var e in entities)
            RenderRelationships(sb, e);

        sb.AppendLine("```");
        return sb.ToString();
    }

    private bool IncludeEntity(IEntityType entity)
    {
        // Skip owned types unless explicitly included
        if (!IncludeOwned && entity.IsOwned())
            return false;

        // Skip explicitly excluded entities
        if (_options.ExcludeEntities.Contains(entity.ClrType.Name, StringComparer.OrdinalIgnoreCase))
            return false;

        // Skip shadow entity types (e.g., join tables that EF creates)
        if (entity.IsPropertyBag)
            return false;

        return true;
    }

    private void RenderEntity(StringBuilder sb, IEntityType e)
    {
        var name = GetEntityName(e);

        sb.AppendLine($"    {name} {{");

        foreach (var p in e.GetProperties())
        {
            if (ExcludeAudit && IsAudit(p)) continue;

            var flags = new List<string>();
            if (p.IsPrimaryKey()) flags.Add("PK");
            if (p.IsForeignKey()) flags.Add("FK");

            var flagStr = flags.Count > 0 ? $"\"{string.Join(",", flags)}\"" : "";
            sb.AppendLine($"        {MapType(p.ClrType)} {p.Name} {flagStr}".TrimEnd());
        }

        sb.AppendLine("    }");
    }

    private void RenderRelationships(StringBuilder sb, IEntityType e)
    {
        foreach (var fk in e.GetForeignKeys())
        {
            // Skip if we're collapsing many-to-many and this is a join entity
            if (CollapseManyToMany && IsJoinEntity(fk.DeclaringEntityType))
                continue;

            var left = GetEntityName(fk.PrincipalEntityType);
            var right = GetEntityName(fk.DeclaringEntityType);

            // Determine relationship cardinality
            var rel = fk.IsUnique ? "||--||" : "||--o{";

            var fkName = string.Join(",", fk.Properties.Select(p => p.Name));
            sb.AppendLine($"    {left} {rel} {right} : \"{fkName}\"");
        }
    }

    private string GetEntityName(IEntityType e)
    {
        return UseTableNames
            ? SanitizeName(e.GetTableName() ?? e.ClrType.Name)
            : SanitizeName(e.ClrType.Name);
    }

    private static string SanitizeName(string name)
    {
        // Mermaid doesn't like certain characters in names
        return SanitizeRegex().Replace(name, "_");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex SanitizeRegex();

    private bool IsAudit(IProperty p) => _auditColumns.Contains(p.Name);

    private static bool IsJoinEntity(IEntityType entity)
    {
        // Join entities typically have exactly 2 foreign keys that form the primary key
        var fks = entity.GetForeignKeys().ToList();
        var pk = entity.FindPrimaryKey();

        if (fks.Count != 2 || pk == null)
            return false;

        var pkProps = pk.Properties.ToHashSet();
        return fks.All(fk => fk.Properties.All(p => pkProps.Contains(p)));
    }

    private static string MapType(Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return underlying.Name switch
        {
            "String" => "string",
            "Int32" => "int",
            "Int64" => "bigint",
            "Int16" => "smallint",
            "Byte" => "tinyint",
            "Boolean" => "bool",
            "DateTime" => "datetime",
            "DateTimeOffset" => "datetimeoffset",
            "DateOnly" => "date",
            "TimeOnly" => "time",
            "TimeSpan" => "timespan",
            "Decimal" => "decimal",
            "Double" => "double",
            "Single" => "float",
            "Guid" => "uuid",
            "Byte[]" => "binary",
            _ when underlying.IsEnum => "enum",
            _ => underlying.Name.ToLowerInvariant()
        };
    }

    private static string MapType(IProperty p)
    {
        var columnType = p.GetColumnType();
        if (!string.IsNullOrEmpty(columnType))
            return columnType;

        return MapType(p.ClrType);
    }

    public void GenerateEntityDocs(DbContext ctx)
    {
        Directory.CreateDirectory(DocsPath);

        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            if (!IncludeEntity(entity)) continue;

            var md = BuildEntityMarkdown(entity);
            WritePreservingNotes(entity, md);
        }

        GenerateIndexFile(ctx);
    }

    private string BuildEntityMarkdown(IEntityType e)
    {
        var sb = new StringBuilder();
        var tableName = e.GetTableName() ?? e.ClrType.Name;
        var schema = e.GetSchema() ?? "public";

        sb.AppendLine($"# {tableName}");
        sb.AppendLine();
        sb.AppendLine($"**Schema:** `{schema}`  ");
        sb.AppendLine($"**Table:** `{tableName}`  ");
        sb.AppendLine($"**Entity:** `{e.ClrType.Name}`");
        sb.AppendLine();

        // Primary Key
        var pk = e.FindPrimaryKey();
        if (pk != null)
        {
            sb.AppendLine("## Primary Key");
            sb.AppendLine();
            sb.AppendLine($"- {string.Join(", ", pk.Properties.Select(p => $"`{p.GetColumnName()}`"))}");
            sb.AppendLine();
        }

        // Columns
        sb.AppendLine("## Columns");
        sb.AppendLine();
        sb.AppendLine("| Column | Type | Nullable | Key | Default | Description |");
        sb.AppendLine("|--------|------|----------|-----|---------|-------------|");

        foreach (var p in e.GetProperties())
        {
            if (ExcludeAudit && IsAudit(p)) continue;

            var columnName = p.GetColumnName() ?? p.Name;
            var columnType = MapType(p);
            var nullable = p.IsNullable ? "Yes" : "No";
            var key = GetKeyInfo(p);
            var defaultValue = p.GetDefaultValueSql() ?? "";

            sb.AppendLine($"| `{columnName}` | `{columnType}` | {nullable} | {key} | {defaultValue} | |");
        }

        sb.AppendLine();

        // Indexes
        var indexes = e.GetIndexes().ToList();
        if (indexes.Count > 0)
        {
            sb.AppendLine("## Indexes");
            sb.AppendLine();
            sb.AppendLine("| Name | Columns | Unique |");
            sb.AppendLine("|------|---------|--------|");

            foreach (var idx in indexes)
            {
                var idxName = idx.GetDatabaseName() ?? "unnamed";
                var columns = string.Join(", ", idx.Properties.Select(p => $"`{p.GetColumnName()}`"));
                var unique = idx.IsUnique ? "Yes" : "No";
                sb.AppendLine($"| `{idxName}` | {columns} | {unique} |");
            }

            sb.AppendLine();
        }

        // Relationships
        var fks = e.GetForeignKeys().ToList();
        var navigations = e.GetNavigations().ToList();

        if (fks.Count > 0 || navigations.Count > 0)
        {
            sb.AppendLine("## Relationships");
            sb.AppendLine();

            foreach (var fk in fks)
            {
                var principal = fk.PrincipalEntityType.GetTableName() ?? fk.PrincipalEntityType.ClrType.Name;
                var fkColumns = string.Join(", ", fk.Properties.Select(p => $"`{p.GetColumnName()}`"));
                var pkColumns = string.Join(", ", fk.PrincipalKey.Properties.Select(p => $"`{p.GetColumnName()}`"));

                sb.AppendLine($"- **{tableName}** ({fkColumns}) -> **{principal}** ({pkColumns})");
            }

            sb.AppendLine();
        }

        // Notes section (preserved across regenerations)
        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine("<!-- NOTES_START -->");
        sb.AppendLine("<!-- Add your notes here. This section is preserved when regenerating. -->");
        sb.AppendLine("<!-- NOTES_END -->");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetKeyInfo(IProperty p)
    {
        var keys = new List<string>();

        if (p.IsPrimaryKey())
            keys.Add("PK");

        if (p.IsForeignKey())
        {
            var fk = p.GetContainingForeignKeys().First();
            var target = fk.PrincipalEntityType.GetTableName() ?? fk.PrincipalEntityType.ClrType.Name;
            keys.Add($"FK->{target}");
        }

        return string.Join(", ", keys);
    }

    private void WritePreservingNotes(IEntityType entity, string newContent)
    {
        var tableName = entity.GetTableName() ?? entity.ClrType.Name;
        var filePath = Path.Combine(DocsPath, $"{tableName}.md");

        // Preserve existing notes if file exists
        if (File.Exists(filePath))
        {
            var existingContent = File.ReadAllText(filePath);
            var notesMatch = NotesRegex().Match(existingContent);

            if (notesMatch.Success)
            {
                var existingNotes = notesMatch.Value;
                newContent = NotesRegex().Replace(newContent, existingNotes);
            }
        }

        File.WriteAllText(filePath, newContent);
        Console.WriteLine($"  Generated: {filePath}");
    }

    [GeneratedRegex(@"<!-- NOTES_START -->.*?<!-- NOTES_END -->", RegexOptions.Singleline)]
    private static partial Regex NotesRegex();

    private void GenerateIndexFile(DbContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Entity Documentation Index");
        sb.AppendLine();
        sb.AppendLine("This documentation is auto-generated from the EF Core model.");
        sb.AppendLine();
        sb.AppendLine("## Entities");
        sb.AppendLine();

        var entities = ctx.Model.GetEntityTypes()
            .Where(IncludeEntity)
            .OrderBy(e => e.GetTableName() ?? e.ClrType.Name)
            .ToList();

        foreach (var entity in entities)
        {
            var tableName = entity.GetTableName() ?? entity.ClrType.Name;
            sb.AppendLine($"- [{tableName}]({tableName}.md)");
        }

        var indexPath = Path.Combine(DocsPath, "README.md");
        File.WriteAllText(indexPath, sb.ToString());
        Console.WriteLine($"  Generated: {indexPath}");
    }
}
