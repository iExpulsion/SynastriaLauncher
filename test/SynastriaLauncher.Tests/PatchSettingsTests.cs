// Covers the patch settings: damage color tier parsing and formatting, DLL folder names and range validation.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class PatchSettingsTests
{
    // The default tiers parse highest first with their colors.
    [Fact]
    public void DefaultColorsParseHighestFirst() =>
        Assert.Equal(
            [new DamageColorTier(5_000_000, 0xC040FF), new DamageColorTier(1_000_000, 0xFF3030), new DamageColorTier(250_000, 0xFFD700)],
            DamageColors.Default);

    // Ten tiers are accepted, and the on-disk table keeps the highest four.
    [Fact]
    public void TenTiersParseAndDiskKeepsFour()
    {
        var tiers = DamageColors.Parse(string.Join(",", Enumerable.Range(1, 10).Select(i => $"{i}K={i:X6}")), out var errors);
        Assert.Empty(errors);
        Assert.Equal(10, tiers.Count);
        Assert.Equal(tiers.Take(DamageColors.MaxDiskTiers), DamageColors.FromTable(DamageColors.ToTable(tiers), 0));
    }

    // Suffixes and hex digits are case-insensitive, decimals and a leading # are allowed.
    [Fact]
    public void ColorsAcceptLooseFormat()
    {
        var tiers = DamageColors.Parse(" 2.5m = #ff0000 , 100k=00ff00 ", out var errors);
        Assert.Empty(errors);
        Assert.Equal([new DamageColorTier(2_500_000, 0xFF0000), new DamageColorTier(100_000, 0x00FF00)], tiers);
    }

    // Bad items, out-of-range amounts and too many or no tiers are all reported.
    [Theory]
    [InlineData("1M=FF30")]
    [InlineData("3B=FF3030")]
    [InlineData("")]
    [InlineData("1K=000000,2K=000000,3K=000000,4K=000000,5K=000000,6K=000000,7K=000000,8K=000000,9K=000000,10K=000000,11K=000000")]
    public void InvalidColorsAreRejected(string text)
    {
        var tiers = DamageColors.Parse(text, out var errors);
        Assert.NotEmpty(errors);
        Assert.Empty(tiers);
    }

    // Amounts format in their shortest exact form with at most one decimal.
    [Theory]
    [InlineData(2_500_000, "2.5M")]
    [InlineData(50_000, "50K")]
    [InlineData(1_000_000_000, "1B")]
    [InlineData(1_250, "1250")]
    [InlineData(999, "999")]
    public void AmountsFormatShort(int amount, string expected) =>
        Assert.Equal(expected, DamageColors.FormatAmount(amount));

    // Formatted tiers list lowest first, as the user types them.
    [Fact]
    public void ColorsFormatLowestFirst() =>
        Assert.Equal("250K=FFD700, 1M=FF3030, 5M=C040FF", DamageColors.Format(DamageColors.Default));

    // Folder names normalize to backslashes without outer slashes; dots, drive paths and long names are refused.
    [Theory]
    [InlineData("DLLs", "DLLs")]
    [InlineData(" Mods/Native/ ", @"Mods\Native")]
    [InlineData(@"\My Mods\", "My Mods")]
    [InlineData("..", null)]
    [InlineData(@"C:\DLLs", null)]
    [InlineData("a.b", null)]
    [InlineData("", null)]
    public void DllFoldersNormalize(string text, string? expected) =>
        Assert.Equal(expected, DllFolder.Normalize(text));

    // A folder of 64 characters doesn't fit the 64-byte setting with its terminator.
    [Fact]
    public void DllFolderMustFitSetting()
    {
        Assert.NotNull(DllFolder.Normalize(new string('a', 63)));
        Assert.Null(DllFolder.Normalize(new string('a', 64)));
    }

    // Validation accepts null (keep) and in-range values, and names each bad one.
    [Fact]
    public void SettingsValidate()
    {
        Assert.Empty(PatchSettings.Keep.Validate());
        Assert.Empty(new PatchSettings(20, 150, DamageColors.Default, @"Mods\Native").Validate());
        Assert.Equal(4, new PatchSettings(19, 151, [], "Mods/Native").Validate().Count);
    }
}
