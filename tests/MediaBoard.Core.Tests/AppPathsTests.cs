using MediaBoard.Core;

namespace MediaBoard.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void DataDir_UsesEnvOverride()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MEDIABOARD_DATA_DIR", temp);
        try
        {
            Assert.Equal(Path.Combine(temp, "library.db"), AppPaths.DatabasePath);
            Assert.True(Directory.Exists(temp));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEDIABOARD_DATA_DIR", null);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
}
