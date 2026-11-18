// Covers byte-replacement planning on small synthetic byte arrays: statuses, ordering and ambiguity checks.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class ReplacementPlanTests
{
    private static PatchDefinition TestPatch(params Replacement[] replacements) =>
        new("Test", "Test", PatchGroup.Advanced, "", "", false, replacements, []);

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // An unpatched pattern is pending and gets replaced in the plan's copy, leaving the input alone.
    [Fact]
    public void PendingReplacementIsPlanned()
    {
        var bytes = Hex("00 11 22 33 44 00");
        var plan = ExePatcher.Plan(bytes, TestPatch(new Replacement("11 22 33", "11 AA 33")), PatchSettings.Keep);
        Assert.Equal(PatchStatus.NotApplied, plan.Status);
        Assert.Equal(Hex("00 11 AA 33 44 00"), plan.Bytes);
        Assert.Equal(Hex("00 11 22 33 44 00"), bytes);
    }

    // Bytes that already hold the replacement count as applied.
    [Fact]
    public void AppliedReplacementIsRecognized() =>
        Assert.Equal(PatchStatus.Applied,
            ExePatcher.Plan(Hex("00 11 AA 33 44"), TestPatch(new Replacement("11 22 33", "11 AA 33")), PatchSettings.Keep).Status);

    // One applied and one pending replacement make the patch partly applied.
    [Fact]
    public void MixedReplacementsArePartlyApplied()
    {
        var patch = TestPatch(new Replacement("11 22", "11 AA"), new Replacement("33 44", "33 BB"));
        Assert.Equal(PatchStatus.PartlyApplied, ExePatcher.Plan(Hex("11 AA 33 44"), patch, PatchSettings.Keep).Status);
    }

    // A pattern found twice (overlapping matches included), or not at all, makes the patch unavailable.
    [Theory]
    [InlineData("11 22 00 11 22")]
    [InlineData("11 11 11")]
    [InlineData("00 00 00")]
    public void AmbiguousOrMissingPatternIsUnavailable(string bytes)
    {
        var find = bytes.StartsWith("11 11") ? "11 11" : "11 22";
        var plan = ExePatcher.Plan(Hex(bytes), TestPatch(new Replacement(find, "11 AA")), PatchSettings.Keep);
        Assert.Equal(PatchStatus.Unavailable, plan.Status);
    }

    // A replacement of a different length is refused rather than shifting the exe.
    [Fact]
    public void LengthMismatchIsUnavailable() =>
        Assert.Equal(PatchStatus.Unavailable,
            ExePatcher.Plan(Hex("11 22 33"), TestPatch(new Replacement("11 22", "11 22 33")), PatchSettings.Keep).Status);

    // Replacements run in order, so a later one sees an earlier one's edit.
    [Fact]
    public void LaterReplacementSeesEarlierEdit()
    {
        var patch = TestPatch(new Replacement("11 22", "11 33"), new Replacement("33 44", "55 44"));
        var plan = ExePatcher.Plan(Hex("11 22 44"), patch, PatchSettings.Keep);
        Assert.Equal(Hex("11 55 44"), plan.Bytes);
        Assert.Equal(2, plan.PendingCount);
    }

    // An exe that isn't WowExt.exe (here the test host itself) has no available patches.
    [Fact]
    public void OtherExeHasNoAvailablePatches() =>
        Assert.Empty(ExePatcher.AvailableIds(File.ReadAllBytes(Environment.ProcessPath!)));
}
