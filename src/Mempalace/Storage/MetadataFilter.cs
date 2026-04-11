namespace Mempalace.Storage;

/// <summary>
///     Simple metadata filter — equality only, AND conjunction.
///     Covers all current palace query patterns.
/// </summary>
public sealed class MetadataFilter
{
    private readonly Dictionary<string, object?> _clauses = new();

    public static MetadataFilter Where(string key, object? value)
    {
        var f = new MetadataFilter();
        f._clauses[key] = value;
        return f;
    }

    public MetadataFilter And(string key, object? value)
    {
        _clauses[key] = value;
        return this;
    }

    public IReadOnlyDictionary<string, object?> Clauses => _clauses;
}
