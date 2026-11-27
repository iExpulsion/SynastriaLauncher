// Covers the patch catalog's own lists, without a game exe.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class PatchCatalogTests
{
    // Recommended Setup names only real patches, and leaves out the ones with a warning.
    [Fact]
    public void RecommendedSetupIsKnownAndSafe()
    {
        var byId = PatchCatalog.All.ToDictionary(p => p.Id);
        Assert.All(PatchCatalog.RecommendedSetup, id => Assert.Null(byId[id].Warning));
        Assert.DoesNotContain(PatchIds.GraphicsLimits, PatchCatalog.RecommendedSetup);
        Assert.DoesNotContain(PatchIds.GroundTargets, PatchCatalog.RecommendedSetup);
        Assert.DoesNotContain(PatchIds.NameplateUnits, PatchCatalog.RecommendedSetup);
    }
}
