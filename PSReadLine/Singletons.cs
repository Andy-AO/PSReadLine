using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace Microsoft.PowerShell.PSReadLine;

public static class Singletons
{
    private static Renderer __renderer;
    public static History _hs => History.Singleton;
    public static PSConsoleReadLine _rl => PSConsoleReadLine.Singleton;
    public static Logger logger { get; }

    static Singletons()
    {
        logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Console(outputTemplate:
                @"{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();
    }

    public static Renderer _renderer
    {
        get => __renderer ??= new();
        set => __renderer = value;
    }
}