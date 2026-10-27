using System.Runtime.CompilerServices;
using Lighthouse.Core;

namespace Lighthouse.Tests;

internal static class TestSetup
{
    /// <summary>Anything the code under test logs goes to a temp folder, never to the real Lighthouse log.</summary>
    [ModuleInitializer]
    internal static void IsolateDataDir() =>
        Paths.DataDir = Path.Combine(Path.GetTempPath(), "lighthouse-tests");
}
