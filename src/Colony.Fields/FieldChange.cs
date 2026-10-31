namespace Colony.Fields;

/// <summary>One entry of the change feed: a chunk of a Field written at <see cref="Version"/>.</summary>
public readonly record struct FieldChange(int FieldId, int Ordinal, uint Version);

/// <summary>
/// Enumerates the (Field, chunk) pairs whose change version is above a threshold, in Field id then Morton
/// order (spec section 5.1). A ref struct over the live version tables: it allocates nothing and reflects
/// writes made so far, so a Late-Phase System sees what the Simulate Phase changed this Tick.
/// </summary>
public ref struct FieldChangeFeed
{
    private readonly FieldSet _set;
    private readonly uint _since;
    private int _field;
    private int _ordinal;

    internal FieldChangeFeed(FieldSet set, uint since)
    {
        _set = set;
        _since = since;
        _field = 0;
        _ordinal = -1;
    }

    public readonly FieldChangeFeed GetEnumerator() => this;

    public FieldChange Current { get; private set; }

    public bool MoveNext()
    {
        while (_field < _set.FieldCount)
        {
            var versions = _set.VersionsOf(_field);
            while (++_ordinal < versions.Length)
            {
                uint version = versions[_ordinal];
                if (version > _since)
                {
                    Current = new FieldChange(_field, _ordinal, version);
                    return true;
                }
            }

            _field++;
            _ordinal = -1;
        }

        return false;
    }
}
