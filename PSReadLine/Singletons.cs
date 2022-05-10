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

    private static string LoggerFilePath =
        Environment.ExpandEnvironmentVariables(@"%AppData%\PSReadline\PSReadlineLog.log");

    static Singletons()
    {
        logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.File(LoggerFilePath,
                outputTemplate:
                @"{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();
        logger.Information("\nLogging has started.");
    }

    public static Renderer _renderer
    {
        get => __renderer ??= new();
        set => __renderer = value;
    }
}