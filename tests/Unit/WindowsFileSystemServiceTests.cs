using NoniPilot.FileSystem;

namespace NoniPilot.Tests.Unit;

public class WindowsFileSystemServiceTests : IDisposable
{
    private readonly string _sandbox;
    private readonly WindowsFileSystemService _service = new();

    public WindowsFileSystemServiceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "NoniPilotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    [Fact]
    public void IsProtectedPath_WindowsDirectory_ReturnsTrue()
    {
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(_service.IsProtectedPath(Path.Combine(windowsPath, "System32")));
    }

    [Fact]
    public void IsProtectedPath_OrdinaryTempFolder_ReturnsFalse()
    {
        Assert.False(_service.IsProtectedPath(_sandbox));
    }

    [Fact]
    public async Task CreateDirectoryAsync_UnderProtectedPath_ThrowsUnauthorized()
    {
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var target = Path.Combine(windowsPath, "NoniPilotShouldNeverCreateThis");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateDirectoryAsync(target));
    }

    [Fact]
    public async Task CreateThenDeleteAsync_InSandbox_RoundTripsCleanly()
    {
        var folder = Path.Combine(_sandbox, "created");

        var createdPath = await _service.CreateDirectoryAsync(folder);
        Assert.True(Directory.Exists(createdPath));

        await _service.DeleteAsync(createdPath, useRecycleBin: false);
        Assert.False(Directory.Exists(createdPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }
}
