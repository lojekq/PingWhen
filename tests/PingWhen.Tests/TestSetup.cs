using System.IO;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Tests;

[TestClass]
public static class TestSetup
{
    /// <summary>
    /// Tests run as a dev instance in any configuration (CI runs -c Release): data in TEMP, own mutex, pipe and AUMID,
    /// never the installed copy's. Set before anything touches AppInfo. Tests of corrupt files log warnings; logs stay off.
    /// Engines in tests save watchers.json into a temp folder too.
    /// </summary>
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Environment.SetEnvironmentVariable(AppInfo.Name.ToUpperInvariant() + "_DATA_DIR", Path.Combine(Path.GetTempPath(), AppInfo.Name + ".Tests"));
        Log.Off = true;
        Store.Dir = Path.Combine(Path.GetTempPath(), "PingWhen.Tests", "data");
    }
}
