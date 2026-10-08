using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkerControl.Service.Database;

/// <summary>
/// Writes what creates a table or a type from what the catalog says about it: the instance
/// keeps no text for those. And reads, from the text of a procedure or function, what the
/// catalog does not keep.
/// </summary>
internal static partial class SqlScript
{
    public static string Name(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Name(string schema, string name) => Name(schema) + "." + Name(name);

    public static string TypeName(RawType type)
    {
        if (type.Schema is not null)
            return Name(type.Schema, type.Name);
        var name = type.Name.ToLowerInvariant();
        return name switch
        {
            "nvarchar" or "nchar" => $"{name}({(type.MaxLength < 0 ? "max" : (type.MaxLength / 2).ToString())})",
            "varchar" or "char" or "varbinary" or "binary" => $"{name}({(type.MaxLength < 0 ? "max" : type.MaxLength.ToString())})",
            "decimal" or "numeric" => $"{name}({type.Precision}, {type.Scale})",
            "datetime2" or "time" or "datetimeoffset" => type.Scale == 7 ? name : $"{name}({type.Scale})",
            "float" => type.Precision is 53 or 0 ? name : $"{name}({type.Precision})",
            _ => name
        };
    }

    public static string Table(CatalogDetail detail, string? databaseCollation)
    {
        var table = Name(detail.Object.Schema, detail.Object.Name);
        var lines = detail.Columns.Select(column => Column(column, databaseCollation, withDefault: true)).ToList();
        lines.AddRange(detail.Indexes.Where(index => index.PrimaryKey || index.UniqueConstraint).Select(index =>
            $"CONSTRAINT {Name(index.Name)} {(index.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {index.Variety} ({Keys(index)})"));
        lines.AddRange(detail.Checks.Select(check => $"CONSTRAINT {Name(check.Name)} CHECK {check.Definition}"));
        lines.AddRange(detail.ForeignKeys.Select(key =>
            $"CONSTRAINT {Name(key.Name)} FOREIGN KEY ({string.Join(", ", key.Columns.Select(Name))}) REFERENCES {Name(key.ReferencedSchema, key.ReferencedTable)} ({string.Join(", ", key.ReferencedColumns.Select(Name))})"
            + Action("DELETE", key.OnDelete) + Action("UPDATE", key.OnUpdate)));

        var script = new StringBuilder();
        script.Append("CREATE TABLE ").Append(table).Append(" (\n    ").Append(string.Join(",\n    ", lines)).Append("\n);\n");

        foreach (var index in detail.Indexes.Where(index => !index.PrimaryKey && !index.UniqueConstraint))
        {
            script.Append("GO\n\nCREATE ").Append(index.Unique ? "UNIQUE " : "").Append(index.Variety).Append(" INDEX ").Append(Name(index.Name)).Append(" ON ").Append(table);
            script.Append(" (").Append(Keys(index)).Append(')');
            var included = index.Columns.Where(column => column.Included).ToList();
            if (included.Count > 0)
                script.Append(" INCLUDE (").Append(string.Join(", ", included.Select(column => Name(column.Name)))).Append(')');
            if (!string.IsNullOrEmpty(index.Filter))
                script.Append(" WHERE ").Append(index.Filter);
            script.Append(";\n");
        }

        foreach (var key in detail.ForeignKeys.Where(key => key.Disabled))
            script.Append("GO\n\nALTER TABLE ").Append(table).Append(" NOCHECK CONSTRAINT ").Append(Name(key.Name)).Append(";\n");
        foreach (var check in detail.Checks.Where(check => check.Disabled))
            script.Append("GO\n\nALTER TABLE ").Append(table).Append(" NOCHECK CONSTRAINT ").Append(Name(check.Name)).Append(";\n");

        foreach (var trigger in detail.Triggers.Where(trigger => !string.IsNullOrWhiteSpace(trigger.Definition)))
        {
            script.Append("GO\n\n").Append(trigger.Definition!.Trim()).Append('\n');
            if (trigger.Disabled)
                script.Append("GO\n\nDISABLE TRIGGER ").Append(Name(detail.Object.Schema, trigger.Name)).Append(" ON ").Append(table).Append(";\n");
        }
        return script.ToString();
    }

    public static string TableType(CatalogDetail detail, string? databaseCollation)
    {
        var lines = detail.Columns.Select(column => Column(column, databaseCollation, withDefault: true)).ToList();
        lines.AddRange(detail.Indexes.Where(index => index.PrimaryKey || index.UniqueConstraint).Select(index =>
            $"{(index.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {index.Variety} ({Keys(index)})"));
        lines.AddRange(detail.Checks.Select(check => $"CHECK {check.Definition}"));
        return $"CREATE TYPE {Name(detail.Object.Schema, detail.Object.Name)} AS TABLE (\n    {string.Join(",\n    ", lines)}\n);\n";
    }

    public static string ScalarType(CatalogObject type, string baseType, bool nullable) =>
        $"CREATE TYPE {Name(type.Schema, type.Name)} FROM {baseType} {(nullable ? "NULL" : "NOT NULL")};\n";

    private static string Column(CatalogColumn column, string? databaseCollation, bool withDefault)
    {
        if (column.Computed is not null)
            return $"{Name(column.Name)} AS {column.Computed}{(column.Persisted ? " PERSISTED" : "")}";

        var line = new StringBuilder(Name(column.Name)).Append(' ').Append(column.Type);
        if (column.Collation is not null && column.Raw.Schema is null && !string.Equals(column.Collation, databaseCollation, StringComparison.OrdinalIgnoreCase))
            line.Append(" COLLATE ").Append(column.Collation);
        if (column.Identity)
            line.Append($" IDENTITY({column.Seed ?? "1"}, {column.Increment ?? "1"})");
        line.Append(column.Nullable ? " NULL" : " NOT NULL");
        if (withDefault && column.Default is not null)
        {
            // A name the instance made up is different on every instance: not part of what the table is.
            if (column.DefaultName is not null && !column.DefaultNamedBySystem)
                line.Append(" CONSTRAINT ").Append(Name(column.DefaultName));
            line.Append(" DEFAULT ").Append(column.Default);
        }
        return line.ToString();
    }

    private static string Keys(CatalogIndex index) =>
        string.Join(", ", index.Columns.Where(column => !column.Included).Select(column => Name(column.Name) + (column.Descending ? " DESC" : " ASC")));

    private static string Action(string on, string action) =>
        action is "NO_ACTION" or "" ? "" : $" ON {on} {action.Replace('_', ' ')}";

    /// <summary>
    /// The same for the same object whatever the line endings and the blanks at the ends of the lines.
    /// </summary>
    public static string Fingerprint(string script)
    {
        var lines = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.TrimEnd());
        var text = string.Join("\n", lines).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The default of each parameter, which the catalog does not keep: read from the header of
    /// the text, up to where the body starts. What cannot be told is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParameterDefaults(string? definition, IEnumerable<string> parameters)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(definition))
            return defaults;

        var text = Comments().Replace(definition, match => new string(' ', match.Length));
        var names = parameters.Where(name => name.Length > 0).ToList();
        // The header ends where the last parameter was declared and the body begins.
        var last = names.Select(name => Declared(text, name)).DefaultIfEmpty(-1).Max();
        if (last < 0)
            return defaults;
        var body = BodyStart().Match(text, last);
        var header = body.Success ? text[..body.Index] : text;

        foreach (var name in names)
        {
            var at = Declared(header, name);
            if (at < 0)
                continue;
            var match = Default().Match(header, at + name.Length);
            // Only when the default is of this parameter and not of one further on.
            if (!match.Success)
                continue;
            // Between the name and the sign there is only the type, whose size may have a comma of its own.
            var between = Regex.Replace(header[(at + name.Length)..match.Index], @"\([^()]*\)", "");
            if (!between.Contains('@') && !between.Contains(','))
                defaults[name] = match.Groups[1].Value.Trim();
        }
        return defaults;
    }

    private static int Declared(string text, string name)
    {
        var match = Regex.Match(text, Regex.Escape(name) + @"(?![\w@#$])", RegexOptions.IgnoreCase);
        return match.Success ? match.Index : -1;
    }

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"(?<![\w@#$])(AS|RETURNS)(?![\w@#$])", RegexOptions.IgnoreCase)]
    private static partial Regex BodyStart();

    [GeneratedRegex(@"=\s*(N?'(?:[^']|'')*'|\([^()]*\)|[^\s,()]+(?:\([^()]*\))?)")]
    private static partial Regex Default();
}
