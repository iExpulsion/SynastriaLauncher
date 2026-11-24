using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SynastriaLauncher.App.Services;

// Windows shortcuts (.lnk) through the shell's own ShellLink object. The interfaces are source-generated COM, which
// works in the trimmed single-file build where the runtime's built-in COM is off.
public static partial class Shortcuts
{
    // Writes the shortcut to the desktop, replacing one of the same name, and returns its path.
    public static string CreateOnDesktop(string name, string target, string arguments, string description)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".lnk");
        var hr = CoCreateInstance(ShellLinkClass, IntPtr.Zero, InprocServer, typeof(IShellLinkW).GUID, out var instance);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var link = (IShellLinkW)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.Release(instance);
        }
        return path;
    }

    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private const int InprocServer = 1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr instance);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription(IntPtr name, int size);
        void SetDescription(string name);
        void GetWorkingDirectory(IntPtr folder, int size);
        void SetWorkingDirectory(string folder);
        void GetArguments(IntPtr arguments, int size);
        void SetArguments(string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation(IntPtr path, int size, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig]
        int IsDirty();
        void Load(string file, uint mode);
        void Save(string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string file);
        void GetCurFile(out IntPtr file);
    }
}
