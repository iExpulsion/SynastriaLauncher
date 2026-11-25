namespace SynastriaLauncher.Core.Addons;

public static class AddonDependencies
{
    public static bool IsInstalled(AddonState? state) =>
        state is AddonState.UpToDate or AddonState.UpdateAvailable or AddonState.Unknown or AddonState.Linked;

    // The required addons (by id) that aren't installed yet, which an install brings along first.
    public static IReadOnlyList<string> Missing(AddonSource source, Func<string, AddonState?> stateOf) =>
        (source.Requires ?? []).Where(r => stateOf(r) == AddonState.NotInstalled).ToList();

    // A library stays off the list while there's nothing to do with it: not installed (an addon that needs it brings
    // it along), still being checked, or installed and current for an installed addon that requires it. An update,
    // a problem, a linked folder or an install nothing needs anymore shows it.
    public static bool IsFolded(AddonSource library, AddonState? state, bool needsUpdate,
        IEnumerable<(AddonSource Source, AddonState? State)> all)
    {
        var quiet = state is null or AddonState.UpToDate or AddonState.Unknown
            || state == AddonState.UpdateAvailable && !needsUpdate;
        var needed = all.Any(o => IsInstalled(o.State) && (o.Source.Requires ?? []).Any(r => SameRepo(r, library.Id)));
        return library.Library && (state == AddonState.NotInstalled || quiet && needed);
    }

    private static bool SameRepo(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
