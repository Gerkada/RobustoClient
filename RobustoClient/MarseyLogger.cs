using System.Reflection;

/// <summary>
/// This is a logging class used by the patch to send logging messages to the loader.
/// </summary>
// ReSharper disable once CheckNamespace
public static class MarseyLogger
{
    // Info enums are identical to those in the loader however they cant be easily casted between the two
    private enum LogType
    {
        INFO,
        WARN,
        FATL,
        DEBG
    }

    // Delegate gets casted to Marsey::Utility::Log(AssemblyName, string) at runtime by the loader
    public delegate void Forward(AssemblyName asm, string message);
    
    public static Forward? logDelegate;

    private static void Log(LogType type, string message)
    {
        logDelegate?.Invoke(Assembly.GetExecutingAssembly().GetName(), $"[{type.ToString()}] {message}");
    }

    public static void Info(string message)
    {
        Log(LogType.INFO, message);
    }
    
    public static void Warn(string message)
    {
        Log(LogType.WARN, message);
    }
    
    public static void Fatal(string message)
    {
        Log(LogType.FATL, message);
    }
    
    public static void Debug(string message)
    {
        Log(LogType.DEBG, message);
    }
}