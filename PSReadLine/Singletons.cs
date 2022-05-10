using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using System;

namespace Microsoft.PowerShell.PSReadLine;

public static class Singletons
{
    private static Renderer __renderer;
    public static History _hs => History.Singleton;
    public static PSConsoleReadLine _rl => PSConsoleReadLine.Singleton;
    public static HistorySearcher _searcher => HistorySearcher.Singleton;
    public static Logger logger { get; }

    static Singletons()
    {
        logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Console(outputTemplate:
                @"{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}")
            .WriteTo.File(Environment.ExpandEnvironmentVariables(@"%AppData%\PSReadline\PSReadlineLog.log"))
            .CreateLogger();
        logger.Information("\n\nLogging has started.");
    }

    public static Renderer _renderer
    {
        get => __renderer ??= new();
        set => __renderer = value;
    }
}