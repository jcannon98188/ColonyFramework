using System.Globalization;
using System.Text;

namespace Colony.ProcGen.Validation;

/// <summary>One golden hash: a stage's output over the validated chunks for a seed.</summary>
public readonly record struct GoldenEntry(ulong Seed, string Stage, ulong Hash);

/// <summary>
/// Per-(seed, stage) golden hashes (spec section 9.3). Text form, one entry per line:
/// <c>&lt;seed hex&gt; &lt;stage name&gt; &lt;hash hex&gt;</c>; lines starting with <c>#</c> are comments.
/// </summary>
public sealed class GoldenHashes
{
    private const string Header = "# colony generation golden hashes v1: <seed> <stage> <hash>";

    private readonly GoldenEntry[] _entries;

    public GoldenHashes(IEnumerable<GoldenEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
        for (int i = 0; i < _entries.Length; i++)
        {
            if (string.IsNullOrEmpty(_entries[i].Stage) || _entries[i].Stage.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"Golden stage name '{_entries[i].Stage}' is empty or contains whitespace.", nameof(entries));
            }

            for (int j = 0; j < i; j++)
            {
                if (_entries[j].Seed == _entries[i].Seed && string.Equals(_entries[j].Stage, _entries[i].Stage, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Duplicate golden hash for stage '{_entries[i].Stage}' and seed 0x{_entries[i].Seed:x}.", nameof(entries));
                }
            }
        }
    }

    public IReadOnlyList<GoldenEntry> Entries => _entries;

    public static GoldenHashes Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<GoldenEntry>();
        int number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3
                || !ulong.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong seed)
                || !ulong.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hash))
            {
                throw new FormatException($"Golden hash line {number} is not '<seed hex> <stage> <hash hex>': '{line}'.");
            }

            entries.Add(new GoldenEntry(seed, parts[1], hash));
        }

        return new GoldenHashes(entries);
    }

    public bool TryGet(ulong seed, string stage, out ulong hash)
    {
        foreach (var entry in _entries)
        {
            if (entry.Seed == seed && string.Equals(entry.Stage, stage, StringComparison.Ordinal))
            {
                hash = entry.Hash;
                return true;
            }
        }

        hash = 0;
        return false;
    }

    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        foreach (var entry in _entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{entry.Seed:x16} {entry.Stage} {entry.Hash:x16}").Append('\n');
        }

        return text.ToString();
    }
}
