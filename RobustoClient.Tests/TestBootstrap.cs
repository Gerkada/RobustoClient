using System.Reflection;
using System.Runtime.CompilerServices;

namespace RobustoClient.Tests;

/// <summary>
/// Loads the content assemblies before any test runs.
/// </summary>
/// <remarks>
/// <see cref="RobustoClient.Reflection.Reflect.FindTypeByName"/> only sees loaded assemblies, and several
/// tests resolve upstream types by simple name. Without this, a test that runs before anything else touches
/// Content.Client would see an index without it and fail depending on execution order.
/// </remarks>
internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Assembly.Load("Content.Client");
        Assembly.Load("Content.Shared");
    }
}
