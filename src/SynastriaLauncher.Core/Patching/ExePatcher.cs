using System.Globalization;

namespace SynastriaLauncher.Core.Patching;

public sealed record PatchFailure(PatchDefinition Patch, IReadOnlyList<string> Problems);

public sealed record ApplyResult(
    byte[] Bytes,
    IReadOnlyList<PatchDefinition> Changed,
    IReadOnlyList<PatchFailure> Failed)
{
    public bool HasChanges => Failed.Count == 0 && Changed.Count > 0;
}

// Plans and applies patches on the exe's bytes in memory. Each plan works on a copy, so nothing changes until a
// caller writes the bytes out.
public static class ExePatcher
{
    public static PatchPlan Plan(byte[] bytes, PatchDefinition patch, PatchSettings settings) => patch.Id switch
    {
        _ when patch.MemoryOnly => PatchPlan.Failed(bytes, "Only works when patches load in memory"),
        PatchIds.LargeAddressAware => LargeAddressAwarePlan(bytes),
        PatchIds.NameplateDistance => NameplateDistancePlan(bytes, settings),
        PatchIds.NameplateStack => NameplateStackPlan(bytes, patch, settings),
        PatchIds.DamageColors => DamageColorsPlan(bytes, patch, settings),
        PatchIds.DllLoader => DllLoaderPlan(bytes, patch, settings),
        _ when patch.Routines.Count > 0 => RoutinePlan(bytes, patch),
        _ => ReplacementPlan(bytes, patch.Replacements),
    };

    public static PatchPlan Plan(byte[] bytes, string id, PatchSettings settings) =>
        Plan(bytes, PatchCatalog.Get(id), settings);

    // Applies the patches in catalog order. If any can't be applied, the result keeps the original bytes.
    public static ApplyResult Apply(byte[] bytes, IEnumerable<string> ids, PatchSettings settings)
    {
        var wanted = ids.ToHashSet();
        foreach (var id in wanted)
        {
            PatchCatalog.Get(id);
        }
        var work = bytes;
        var changed = new List<PatchDefinition>();
        var failed = new List<PatchFailure>();
        foreach (var patch in PatchCatalog.All.Where(p => wanted.Contains(p.Id)))
        {
            var plan = Plan(work, patch, settings);
            if (plan.Status == PatchStatus.Unavailable)
            {
                failed.Add(new PatchFailure(patch, plan.Problems));
            }
            else if (plan.Status != PatchStatus.Applied)
            {
                work = plan.Bytes;
                changed.Add(patch);
            }
        }
        return new ApplyResult(failed.Count == 0 ? work : bytes, changed, failed);
    }

    // Every patch that fits this exe. Large Address Aware fits any 32-bit exe, so when nothing else fits this isn't
    // Synastria's WowExt.exe and the list is empty.
    public static IReadOnlyList<string> AvailableIds(byte[] bytes)
    {
        var ids = PatchCatalog.All
            .Where(p => Plan(bytes, p, PatchSettings.Keep).Status != PatchStatus.Unavailable)
            .Select(p => p.Id)
            .ToList();
        return ids.Any(id => id != PatchIds.LargeAddressAware) ? ids : [];
    }

    public static CurrentSettings ReadSettings(byte[] bytes)
    {
        var section = PatchSection.Find(bytes);
        var distanceOffset = NameplateDistanceOffset(bytes);
        var spacing = section is null ? 0f : BitConverter.ToSingle(bytes, (int)section.RawOffset + PatchCatalog.StackSpacingOffset);
        var colors = section is null ? [] : DamageColors.FromTable(bytes, (int)section.RawOffset + PatchCatalog.ColorTableOffset);
        var folder = DllFolderSetting(bytes);
        return new CurrentSettings(
            distanceOffset is { } offset ? (int)Math.Round(Math.Sqrt(BitConverter.ToSingle(bytes, offset))) : null,
            spacing > 0 ? (int)Math.Round(spacing * 200.0) : null,
            colors.Count > 0 ? colors : null,
            folder.Length > 0 ? folder : null);
    }

    // Replacements run in order on a copy, so a later one sees the earlier edits.
    private static PatchPlan ReplacementPlan(byte[] bytes, IReadOnlyList<Replacement> replacements)
    {
        var work = (byte[])bytes.Clone();
        var applied = 0;
        var pending = 0;
        var problems = new List<string>();
        foreach (var replacement in replacements)
        {
            var found = Bytes.FindAll(work, replacement.Find);
            if (replacement.Find.Length != replacement.Replace.Length)
            {
                problems.Add($"{replacement.FindHex} (replacement is a different length)");
            }
            else if (found.Count == 1)
            {
                replacement.Replace.CopyTo(work, found[0]);
                pending++;
            }
            else if (found.Count == 0 && Bytes.Contains(work, replacement.Replace))
            {
                applied++;
            }
            else
            {
                problems.Add($"{replacement.FindHex} (found {found.Count} times)");
            }
        }
        return new PatchPlan(work, applied, pending, problems);
    }

    // Writes each routine into the patch section (adding the section if needed), or recognizes it as already
    // there, and applies the patch's call sites.
    private static PatchPlan RoutinePlan(byte[] bytes, PatchDefinition patch)
    {
        var (sectioned, problem) = PatchSection.Add(bytes);
        PatchPlan plan;
        if (sectioned is not null)
        {
            var calls = ReplacementPlan(sectioned, patch.Replacements);
            var section = PatchSection.Find(calls.Bytes)!;
            var applied = calls.AppliedCount;
            var pending = calls.PendingCount;
            var problems = calls.Problems.ToList();
            foreach (var routine in patch.Routines)
            {
                var offset = (int)section.RawOffset + routine.Offset;
                if (Bytes.RegionMatches(calls.Bytes, offset, routine.Code.Length, routine.Code))
                {
                    applied++;
                }
                else if (Bytes.RegionMatches(calls.Bytes, offset, routine.Code.Length, []))
                {
                    routine.Code.CopyTo(calls.Bytes, offset);
                    pending++;
                }
                else
                {
                    problems.Add($"The patch section already has something else at +0x{routine.Offset:X}");
                }
            }
            plan = new PatchPlan(calls.Bytes, applied, pending, problems);
        }
        else
        {
            plan = PatchPlan.Failed(bytes, problem);
        }
        return plan;
    }

    // Edits the live PE header found through e_lfanew; WowExt.exe carries a leftover copy of the header at 0x40,
    // so a byte pattern can't be used.
    private static PatchPlan LargeAddressAwarePlan(byte[] bytes)
    {
        var work = (byte[])bytes.Clone();
        var layout = PeLayout.Read(work);
        PatchPlan plan;
        if (layout is not null)
        {
            var flagsOffset = layout.PeOffset + 22;
            var flags = BitConverter.ToUInt16(work, flagsOffset);
            var isSet = (flags & 0x20) != 0;
            if (!isSet)
            {
                Bytes.WriteUInt16(work, flagsOffset, (ushort)(flags | 0x20));
            }
            plan = new PatchPlan(work, isSet ? 1 : 0, isSet ? 0 : 1, []);
        }
        else
        {
            plan = PatchPlan.Failed(work, "No valid PE header found");
        }
        return plan;
    }

    // The client compares the squared distance against a float found by the bytes just before it (including 400.0,
    // the 20-yard square), which stay the same whatever distance was set before.
    private static int? NameplateDistanceOffset(byte[] bytes)
    {
        var anchor = Bytes.FromHex(PatchCatalog.NameplateDistanceAnchor);
        var found = Bytes.FindAll(bytes, anchor);
        return found.Count == 1 && found[0] + anchor.Length + 4 <= bytes.Length ? found[0] + anchor.Length : null;
    }

    private static PatchPlan NameplateDistancePlan(byte[] bytes, PatchSettings settings)
    {
        var work = (byte[])bytes.Clone();
        PatchPlan plan;
        if (NameplateDistanceOffset(work) is { } offset)
        {
            var current = Math.Round(Math.Sqrt(BitConverter.ToSingle(work, offset)));
            var detail = $"{current.ToString(CultureInfo.InvariantCulture)} yards";
            var wanted = settings.NameplateDistance ?? PatchSettings.DefaultNameplateDistance;
            var keepCurrent = current != 41 && settings.NameplateDistance is null;
            var isSet = current == wanted || keepCurrent;
            if (!isSet)
            {
                Bytes.WriteSingle(work, offset, wanted * wanted);
            }
            plan = new PatchPlan(work, isSet ? 1 : 0, isSet ? 0 : 1, [], detail);
        }
        else
        {
            var matches = Bytes.FindAll(work, Bytes.FromHex(PatchCatalog.NameplateDistanceAnchor)).Count;
            plan = PatchPlan.Failed(work, $"Distance setting not found (anchor matched {matches} times)");
        }
        return plan;
    }

    // Settings in the patch section keep an existing value unless a new one is given; the default is used only
    // when nothing is set yet. A changed setting on an applied patch counts as one pending change.
    private static PatchPlan WithSetting(PatchPlan routines, bool settingSame, string detail) =>
        routines.Status == PatchStatus.Applied
            ? new PatchPlan(routines.Bytes, settingSame ? 1 : 0, settingSame ? 0 : 1, [], detail)
            : new PatchPlan(routines.Bytes, routines.AppliedCount, routines.PendingCount, [], detail);

    private static PatchPlan NameplateStackPlan(byte[] bytes, PatchDefinition patch, PatchSettings settings)
    {
        var plan = RoutinePlan(bytes, patch);
        PatchPlan result = plan;
        if (plan.Status != PatchStatus.Unavailable)
        {
            var existing = PatchSection.Find(bytes);
            var current = existing is null ? 0f : BitConverter.ToSingle(bytes, (int)existing.RawOffset + PatchCatalog.StackSpacingOffset);
            var currentPercent = (int)Math.Round(current * 200.0);
            var wanted = current > 0 && settings.StackSpacing is null
                ? currentPercent
                : settings.StackSpacing ?? PatchSettings.DefaultStackSpacing;
            var offset = (int)PatchSection.Find(plan.Bytes)!.RawOffset + PatchCatalog.StackSpacingOffset;
            Bytes.WriteSingle(plan.Bytes, offset, (float)(wanted / 200.0));
            result = WithSetting(plan, currentPercent == wanted, current > 0 ? $"{currentPercent}% spacing" : "");
        }
        return result;
    }

    private static PatchPlan DamageColorsPlan(byte[] bytes, PatchDefinition patch, PatchSettings settings)
    {
        var plan = RoutinePlan(bytes, patch);
        PatchPlan result = plan;
        if (plan.Status != PatchStatus.Unavailable)
        {
            var existing = PatchSection.Find(bytes);
            var tableOffset = existing is null ? 0 : (int)existing.RawOffset + PatchCatalog.ColorTableOffset;
            var hasTable = existing is not null && BitConverter.ToInt32(bytes, tableOffset) > 0;
            var detail = hasTable ? DamageColors.Format(DamageColors.FromTable(bytes, tableOffset)) : "";
            var wanted = DamageColors.ToTable(settings.DamageColors ?? DamageColors.Default);
            var same = hasTable &&
                (Bytes.RegionMatches(bytes, tableOffset, DamageColors.TableSize, wanted) || settings.DamageColors is null);
            if (!same)
            {
                wanted.CopyTo(plan.Bytes, (int)PatchSection.Find(plan.Bytes)!.RawOffset + PatchCatalog.ColorTableOffset);
            }
            result = WithSetting(plan, same, detail);
        }
        return result;
    }

    private static string DllFolderSetting(byte[] bytes)
    {
        var section = PatchSection.Find(bytes);
        return section is null
            ? ""
            : Bytes.Latin1.GetString(bytes, (int)section.RawOffset + PatchCatalog.DllFolderOffset, DllFolder.Size).Split('\0')[0];
    }

    // The exe's entry point moves to the loader routine, which loads the DLLs and then jumps to the original.
    private static PatchPlan DllLoaderPlan(byte[] bytes, PatchDefinition patch, PatchSettings settings)
    {
        var plan = RoutinePlan(bytes, patch);
        var layout = PeLayout.Read(plan.Bytes);
        var entryField = layout?.EntryPointField ?? 0;
        var entry = layout is null ? 0 : BitConverter.ToUInt32(plan.Bytes, entryField);
        PatchPlan result;
        if (plan.Status == PatchStatus.Unavailable)
        {
            result = plan;
        }
        else if (entry != PatchCatalog.OriginalEntry && entry != PatchCatalog.LoaderEntry)
        {
            result = PatchPlan.Failed(bytes,
                $"The exe's entry point is 0x{entry:X}, not the expected 0x{PatchCatalog.OriginalEntry:X}");
        }
        else
        {
            var current = DllFolderSetting(bytes);
            var wanted = current.Length > 0 && settings.DllFolder is null ? current : settings.DllFolder ?? DllFolder.Default;
            var setting = new byte[DllFolder.Size];
            Bytes.Latin1.GetBytes(wanted).CopyTo(setting, 0);
            setting.CopyTo(plan.Bytes, (int)PatchSection.Find(plan.Bytes)!.RawOffset + PatchCatalog.DllFolderOffset);
            Bytes.WriteUInt32(plan.Bytes, entryField, PatchCatalog.LoaderEntry);
            var onLoader = entry == PatchCatalog.LoaderEntry;
            var applied = plan.AppliedCount + (onLoader ? 1 : 0);
            var pending = plan.PendingCount + (onLoader ? 0 : 1);
            var detail = (current.Length > 0 ? current : wanted) + "\\";
            result = pending == 0 && current == wanted ? new PatchPlan(plan.Bytes, applied, 0, [], detail)
                : pending == 0 ? new PatchPlan(plan.Bytes, 0, 1, [], detail)
                : new PatchPlan(plan.Bytes, applied, pending, [], detail);
        }
        return result;
    }
}
