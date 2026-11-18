using Avalonia.Data.Converters;

namespace SynastriaLauncher.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter HiddenOpacity =
        new FuncValueConverter<bool, double>(hidden => hidden ? 0.5 : 1.0);

    public static readonly IValueConverter Positive =
        new FuncValueConverter<int, bool>(count => count > 0);
}

// The status labels the page headers share: Title Case, counted by number.
public static class StatusText
{
    public static string Updates(int count) =>
        count == 0 ? "All Up to Date" : count == 1 ? "1 Update Available" : $"{count} Updates Available";
}
