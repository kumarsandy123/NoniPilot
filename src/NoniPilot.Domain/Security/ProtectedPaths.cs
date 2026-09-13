namespace NoniPilot.Domain.Security;

/// <summary>
/// The single, shared definition of "OS-critical path" (section 8.5/18: "protect system-critical
/// paths through policy"). Both IPolicyService and IFileSystemService implementations must use
/// this same list so a path can never be treated as safe by one and protected by the other.
/// </summary>
public static class ProtectedPaths
{
    private static readonly string[] ProtectedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
    };

    public static bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            // An unparsable path is treated as protected - fail closed, not open.
            return true;
        }

        foreach (var root in ProtectedRoots)
        {
            if (!string.IsNullOrEmpty(root) &&
                full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
