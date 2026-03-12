using Lumi;

namespace Lumi.Tests;

public sealed class HeadlessTestApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Tests create their own windows.
    }
}
