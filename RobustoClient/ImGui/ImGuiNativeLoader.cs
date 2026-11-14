using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace RobustoClient.ImGui;

public static class ImGuiNativeLoader
{
    private static readonly Lock SyncLock = new();
    private static readonly Dictionary<string, Assembly> LoadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static nint _cimguiHandle;

    #pragma warning disable CA2255
    [ModuleInitializer]
    #pragma warning restore CA2255
    public static void Initialize()
    {
        lock (SyncLock)
        {
            if (_initialized)
                return;

            _initialized = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolveAssembly;
            var alc = AssemblyLoadContext.GetLoadContext(typeof(ImGuiNativeLoader).Assembly);
            if (alc != null)
                alc.Resolving += OnResolveAssemblyLoadContext;
        }

        try
        {
            ConfigureHexaLoader();
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"ImGuiNativeLoader failed to configure native resolver: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConfigureHexaLoader()
    {
        HexaGen.Runtime.LibraryLoader.InterceptLibraryLoad += OnInterceptLibraryLoad;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Hexa.NET.ImGui.ImGui).Assembly, (libraryName, _, _) =>
            {
                if (libraryName.Contains("cimgui", StringComparison.OrdinalIgnoreCase))
                    return EnsureNativeCImGuiLoaded();
                return IntPtr.Zero;
            });
        }
        catch
        {
            // Resolver may already be set if re-entered; safe to ignore.
        }
    }

    private static bool OnInterceptLibraryLoad(string libraryName, out nint handle)
    {
        if (libraryName.Contains("cimgui", StringComparison.OrdinalIgnoreCase))
        {
            handle = EnsureNativeCImGuiLoaded();
            return handle != IntPtr.Zero;
        }

        handle = IntPtr.Zero;
        return false;
    }

    private static Assembly? OnResolveAssemblyLoadContext(AssemblyLoadContext alc, AssemblyName assemblyName) =>
        ResolveEmbeddedAssembly(assemblyName.Name, alc);

    private static Assembly? OnResolveAssembly(object? sender, ResolveEventArgs args) =>
        ResolveEmbeddedAssembly(new AssemblyName(args.Name).Name, AssemblyLoadContext.GetLoadContext(typeof(ImGuiNativeLoader).Assembly));

    private static Assembly? ResolveEmbeddedAssembly(string? shortName, AssemblyLoadContext? alc)
    {
        if (string.IsNullOrEmpty(shortName))
            return null;

        if (!shortName.Equals("Hexa.NET.ImGui", StringComparison.OrdinalIgnoreCase) &&
            !shortName.Equals("HexaGen.Runtime", StringComparison.OrdinalIgnoreCase))
        {
            // The mod loads into the game's own load context, which does not probe the game
            // directory for this assembly's dependencies. Anything already loaded there can be
            // handed back by simple name, which is enough for transitive content dependencies such
            // as SixLabors.ImageSharp that Content.Client has already brought in.
            return FindLoadedAssembly(shortName);
        }

        lock (SyncLock)
        {
            if (LoadedAssemblies.TryGetValue(shortName, out var cached))
                return cached;

            var resourceName = $"RobustoClient.Embedded.{shortName}.dll";
            using var stream = typeof(ImGuiNativeLoader).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                MarseyLogger.Warn($"Embedded assembly resource '{resourceName}' not found.");
                return null;
            }

            Assembly loaded;
            if (alc != null)
            {
                loaded = alc.LoadFromStream(stream);
            }
            else
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                loaded = Assembly.Load(bytes);
            }

            LoadedAssemblies[shortName] = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// Returns an assembly that is already loaded under the given simple name, if any.
    /// </summary>
    private static Assembly? FindLoadedAssembly(string shortName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            if (shortName.Equals(assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return assembly;
        }

        return null;
    }

    private static nint EnsureNativeCImGuiLoaded()
    {
        lock (SyncLock)
        {
            if (_cimguiHandle != IntPtr.Zero)
                return _cimguiHandle;

            var (resourceName, ext) = GetPlatformNativeResource();
            using var stream = typeof(ImGuiNativeLoader).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                MarseyLogger.Fatal($"Embedded native resource '{resourceName}' not found.");
                return IntPtr.Zero;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            var hashBytes = SHA256.HashData(bytes);
            var shortHash = Convert.ToHexString(hashBytes, 0, 4).ToLowerInvariant();

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RobustoClient",
                "Native");
            Directory.CreateDirectory(dir);

            var targetPath = Path.Combine(dir, $"cimgui_{shortHash}{ext}");
            if (!File.Exists(targetPath))
            {
                var tmpPath = $"{targetPath}.{Environment.ProcessId}.tmp";
                try
                {
                    File.WriteAllBytes(tmpPath, bytes);
                    File.Move(tmpPath, targetPath, overwrite: false);
                }
                catch (IOException)
                {
                    // Another client instance already extracted the identical binary.
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tmpPath))
                            File.Delete(tmpPath);
                    }
                    catch
                    {
                        // Ignore cleanup race.
                    }
                }
            }

            _cimguiHandle = NativeLibrary.Load(targetPath);
            MarseyLogger.Info($"Loaded native cimgui from {targetPath}");
            return _cimguiHandle;
        }
    }

    private static (string ResourceName, string Extension) GetPlatformNativeResource()
    {
        if (OperatingSystem.IsWindows())
            return ("RobustoClient.Embedded.win-x64.cimgui.dll", ".dll");

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? ("RobustoClient.Embedded.osx-arm64.cimgui.dylib", ".dylib")
                : ("RobustoClient.Embedded.osx-x64.cimgui.dylib", ".dylib");
        }

        return ("RobustoClient.Embedded.linux-x64.cimgui.so", ".so");
    }
}
