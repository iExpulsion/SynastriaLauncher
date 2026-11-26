namespace SynastriaLauncher.Core.Patching;

// A null setting keeps the value already in the exe, or the default when the exe has none.
public sealed record PatchSettings(
    int? NameplateDistance = null,
    int? StackSpacing = null,
    IReadOnlyList<DamageColorTier>? DamageColors = null,
    string? DllFolder = null)
{
    public const int DefaultNameplateDistance = 60;
    public const int MinNameplateDistance = 20;
    public const int MaxNameplateDistance = 100;
    public const int DefaultStackSpacing = 100;
    public const int MinStackSpacing = 40;
    public const int MaxStackSpacing = 150;
    public const int DefaultCameraZoom = 50;
    public const int MinCameraZoom = 30;
    public const int MaxCameraZoom = 100;
    public const int DefaultCameraFov = 90;
    public const int MinCameraFov = 60;
    public const int MaxCameraFov = 150;

    public static PatchSettings Keep { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (NameplateDistance is < MinNameplateDistance or > MaxNameplateDistance)
        {
            errors.Add($"Nameplate distance must be {MinNameplateDistance} to {MaxNameplateDistance} yards");
        }
        if (StackSpacing is < MinStackSpacing or > MaxStackSpacing)
        {
            errors.Add($"Stack spacing must be {MinStackSpacing} to {MaxStackSpacing} percent");
        }
        if (DamageColors is { Count: 0 or > Patching.DamageColors.MaxTiers })
        {
            errors.Add($"Give between 1 and {Patching.DamageColors.MaxTiers} damage color tiers");
        }
        if (DllFolder is not null && Patching.DllFolder.Normalize(DllFolder) != DllFolder)
        {
            errors.Add("The DLL folder must be a folder inside the game folder, like DLLs");
        }
        return errors;
    }
}

// What the exe holds now; null where it holds nothing yet.
public sealed record CurrentSettings(
    int? NameplateDistance,
    int? StackSpacing,
    IReadOnlyList<DamageColorTier>? DamageColors,
    string? DllFolder,
    int? CameraZoom = null,
    int? CameraFov = null);
