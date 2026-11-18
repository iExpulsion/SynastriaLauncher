namespace SynastriaLauncher.Core.Patching;

// A circle filling the square, opaque inside with a one-pixel soft edge, for the HD portrait render target.
internal static class PortraitMask
{
    public const int Size = 256;

    public static byte[] Bytes { get; } = Build();

    private static byte[] Build()
    {
        var mask = new byte[Size * Size];
        var radius = Size / 2.0 - 1.0;
        var center = (Size - 1) / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                var alpha = Math.Round((radius - distance + 0.5) * 255.0, MidpointRounding.ToEven);
                mask[y * Size + x] = (byte)Math.Max(0, Math.Min(255, alpha));
            }
        }
        return mask;
    }
}
