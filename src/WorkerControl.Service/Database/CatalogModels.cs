namespace WorkerControl.Service.Database;

/// <summary>
/// Something defined in a database. The kind is one of Table, View, Procedure, Function and Type.
/// </summary>
public sealed record CatalogObject(int Id, string Kind, string Schema, string Name, string Variety, DateTime? CreatedAt, DateTime? ModifiedAt, long? Rows, long? SizeKb);

/// <summary>
/// A type as the catalog gives it, before it is written the way a script writes it.
/// </summary>
public sealed record RawType(string Name, string? Schema, int MaxLength, int Precision, int Scale);

public sealed record CatalogColumn(string Name, RawType Raw, bool Nullable, bool Identity, string? Seed, string? Increment,
    string? DefaultName, string? Default, bool DefaultNamedBySystem, string? Computed, bool Persisted, string? Collation)
{
    /// <summary>
    /// The type as a script writes it: <c>varchar(20)</c>, <c>decimal(18, 2)</c>.
    /// </summary>
    public string Type => SqlScript.TypeName(Raw);
}

public sealed record CatalogParameter(string Name, RawType Raw, bool Output, bool ReadOnly)
{
    public string Type => SqlScript.TypeName(Raw);

    /// <summary>
    /// The value it takes when none is given, as read from the text of the object.
    /// </summary>
    public string? Default { get; init; }
}

public sealed record CatalogIndexColumn(string Name, bool Descending, bool Included);

public sealed record CatalogIndex(string Name, string Variety, bool Unique, bool PrimaryKey, bool UniqueConstraint, bool Disabled, string? Filter, IReadOnlyList<CatalogIndexColumn> Columns);

public sealed record CatalogForeignKey(string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    string OnDelete, string OnUpdate, bool Disabled);

public sealed record CatalogCheck(string Name, string Definition, bool Disabled);

public sealed record CatalogTrigger(string Name, bool Disabled, bool InsteadOf, string? Definition);

/// <summary>
/// Another object this one has to do with. Without a kind, it is not in this database.
/// </summary>
public sealed record CatalogReference(string? Schema, string Name, string? Kind, string? Database);

public sealed record CatalogDetail
{
    public required CatalogObject Object { get; init; }
    public IReadOnlyList<CatalogColumn> Columns { get; init; } = [];
    public IReadOnlyList<CatalogParameter> Parameters { get; init; } = [];

    /// <summary>
    /// What a function that gives one value gives back.
    /// </summary>
    public string? Returns { get; init; }

    /// <summary>
    /// What a type that is not a table is made from.
    /// </summary>
    public string? BaseType { get; init; }
    public IReadOnlyList<CatalogIndex> Indexes { get; init; } = [];
    public IReadOnlyList<CatalogForeignKey> ForeignKeys { get; init; } = [];
    public IReadOnlyList<CatalogCheck> Checks { get; init; } = [];
    public IReadOnlyList<CatalogTrigger> Triggers { get; init; } = [];
    public IReadOnlyList<CatalogReference> Uses { get; init; } = [];
    public IReadOnlyList<CatalogReference> UsedBy { get; init; } = [];

    /// <summary>
    /// What creates the object. Null when the instance keeps it encrypted.
    /// </summary>
    public string? Script { get; init; }

    /// <summary>
    /// "instance" when the script is the text the instance keeps, "generated" when it was
    /// written from the catalog, "encrypted" when there is none to show.
    /// </summary>
    public string ScriptSource { get; init; } = "generated";

    /// <summary>
    /// What tells one version of the object from another: the same script gives the same one.
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// The parts that could not be read, each with what the instance said.
    /// </summary>
    public IReadOnlyDictionary<string, string> Problems { get; init; } = new Dictionary<string, string>();
}
