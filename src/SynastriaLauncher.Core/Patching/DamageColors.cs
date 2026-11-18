using System.Globalization;
using System.Text.RegularExpressions;

namespace SynastriaLauncher.Core.Patching;

public sealed record DamageColorTier(int Amount, uint Rgb);

public static partial class DamageColors
{
    public const int MaxTiers = 10;
    public const int MaxDiskTiers = 4;
    public const int TableSize = 40;
    public const string DefaultText = "250K=FFD700,1M=FF3030,5M=C040FF";

    public static IReadOnlyList<DamageColorTier> Default { get; } = Parse(DefaultText);

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)([KMB]?)\s*=\s*#?([0-9A-F]{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex TierPattern();

    // Parses "250K=FFD700,1M=FF3030" into tiers sorted highest first; errors is empty when the text is valid.
    public static IReadOnlyList<DamageColorTier> Parse(string text, out IReadOnlyList<string> errors)
    {
        var tiers = new List<DamageColorTier>();
        var problems = new List<string>();
        var items = text.Split(',').Select(i => i.Trim()).Where(i => i.Length > 0);
        foreach (var item in items)
        {
            var match = TierPattern().Match(item);
            if (match.Success)
            {
                var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
                {
                    "K" => 1_000d,
                    "M" => 1_000_000d,
                    "B" => 1_000_000_000d,
                    _ => 1d,
                };
                var amount = Math.Round(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * multiplier);
                if (amount >= 1 && amount <= int.MaxValue)
                {
                    tiers.Add(new DamageColorTier((int)amount, uint.Parse(match.Groups[3].Value, NumberStyles.HexNumber)));
                }
                else
                {
                    problems.Add($"'{item}' is out of range (1 to 2.1B)");
                }
            }
            else
            {
                problems.Add($"'{item}' should look like 1M=FF3030");
            }
        }
        if (tiers.Count is 0 or > MaxTiers)
        {
            problems.Add($"Give between 1 and {MaxTiers} tiers");
        }
        errors = problems;
        return problems.Count == 0 ? tiers.OrderByDescending(t => t.Amount).ToList() : [];
    }

    public static IReadOnlyList<DamageColorTier> Parse(string text)
    {
        var tiers = Parse(text, out var errors);
        return errors.Count == 0 ? tiers : throw new FormatException(string.Join("; ", errors));
    }

    // Shortest exact form with at most one decimal: 2500000 -> 2.5M, 50000 -> 50K.
    public static string FormatAmount(int amount)
    {
        (long Unit, string Suffix)[] units = [(1_000_000_000, "B"), (1_000_000, "M"), (1_000, "K")];
        foreach (var (unit, suffix) in units)
        {
            if (amount >= unit && amount * 10L % unit == 0)
            {
                return (amount / (double)unit).ToString(CultureInfo.InvariantCulture) + suffix;
            }
        }
        return amount.ToString(CultureInfo.InvariantCulture);
    }

    public static string Format(IEnumerable<DamageColorTier> tiers) =>
        string.Join(", ", tiers.OrderBy(t => t.Amount).Select(t => $"{FormatAmount(t.Amount)}={t.Rgb:X6}"));

    internal static byte[] ToTable(IReadOnlyList<DamageColorTier> tiers)
    {
        var table = new byte[TableSize];
        for (var i = 0; i < Math.Min(tiers.Count, MaxDiskTiers); i++)
        {
            Bytes.WriteUInt32(table, 8 * i, (uint)tiers[i].Amount);
            Bytes.WriteUInt32(table, 8 * i + 4, 0xFF000000 | tiers[i].Rgb);
        }
        return table;
    }

    internal static IReadOnlyList<DamageColorTier> FromTable(byte[] bytes, int offset)
    {
        var tiers = new List<DamageColorTier>();
        for (var i = 0; i < MaxDiskTiers && BitConverter.ToInt32(bytes, offset + 8 * i) > 0; i++)
        {
            tiers.Add(new DamageColorTier(BitConverter.ToInt32(bytes, offset + 8 * i),
                BitConverter.ToUInt32(bytes, offset + 8 * i + 4) & 0xFFFFFF));
        }
        return tiers;
    }
}
