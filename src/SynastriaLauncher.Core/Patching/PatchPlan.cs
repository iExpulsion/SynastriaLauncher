namespace SynastriaLauncher.Core.Patching;

public enum PatchStatus
{
    Applied,
    NotApplied,
    PartlyApplied,
    Unavailable,
}

public sealed record PatchPlan(byte[] Bytes, int AppliedCount, int PendingCount, IReadOnlyList<string> Problems,
    string Detail = "")
{
    public PatchStatus Status =>
        Problems.Count > 0 ? PatchStatus.Unavailable
        : PendingCount == 0 ? PatchStatus.Applied
        : AppliedCount == 0 ? PatchStatus.NotApplied
        : PatchStatus.PartlyApplied;

    public static PatchPlan Failed(byte[] bytes, string problem) => new(bytes, 0, 0, [problem]);
}
