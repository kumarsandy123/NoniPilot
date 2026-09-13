using System.Runtime.InteropServices;

namespace NoniPilot.FileSystem;

/// <summary>Recycle-Bin-aware delete via the classic Shell32 SHFileOperation API (no extra NuGet dependency needed).</summary>
internal static class RecycleBin
{
    private const int FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofSilent = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr HwndOwner;
        public int WFunc;
        public string PFrom;
        public string? PTo;
        public ushort FFlags;
        public bool FAnyOperationsAborted;
        public IntPtr HNameMappings;
        public string? LpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>Deletes a file or directory permanently or to the Recycle Bin. Path must be double-null-terminated for the Shell API.</summary>
    public static void Delete(string path, bool useRecycleBin)
    {
        var op = new ShFileOpStruct
        {
            WFunc = FoDelete,
            PFrom = path + '\0' + '\0',
            FFlags = useRecycleBin
                ? (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent)
                : (ushort)(FofNoConfirmation | FofSilent),
        };

        var result = SHFileOperation(ref op);
        if (result != 0)
        {
            throw new IOException($"Failed to delete '{path}' (SHFileOperation returned 0x{result:X}).");
        }
    }
}
