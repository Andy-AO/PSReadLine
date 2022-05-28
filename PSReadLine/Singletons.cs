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
    public static HistorySearcherReadLine SearcherReadLine => HistorySearcherReadLine.Singleton;
    public static Logger logger { get; }

    private static string LoggerFilePath =
        Environment.ExpandEnvironmentVariables(@"%AppData%\PSReadline\PSReadlineLog.log");

    static Singletons()
    {
        const string outputTemplate = @"{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}";
        var configuration = DeBugMode.IsDeBugMode()? new LoggerConfiguration().MinimumLevel.Debug() : new LoggerConfiguration().MinimumLevel.Warning();
        logger = configuration
            .WriteTo.File(LoggerFilePath,
                outputTemplate:
                outputTemplate)
            .WriteTo.Debug(outputTemplate:
                outputTemplate)
            .CreateLogger();
        logger.Information("\nLogging has started.");
    }

    public static Renderer _renderer
    {
        get => __renderer ??= new Renderer();
        set => __renderer = value;
    }
}