using System.IO;
using PingWhen.Features;
using PingWhen.Shell;

namespace PingWhen.Tests;

[TestClass]
public static class TestSetup
{
    /// <summary>
    /// Tests of corrupt files log warnings; they must not create %APPDATA%\PingWhen\logs on the machine.
    /// Engines in tests save watchers.json: into a temp folder, never the real %APPDATA%\PingWhen of a running PingWhen.
    /// </summary>
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Log.Off = true;
        Store.Dir = Path.Combine(Path.GetTempPath(), "PingWhen.Tests", "data");
    }
}
