namespace Microsoft.PowerShell.PSReadLine;

public static class Singletons
{
    private static Renderer __renderer;
    public static History _hs => History.Singleton;
    public static PSConsoleReadLine _rl => PSConsoleReadLine.Singleton;

    public static Renderer _renderer
    {
        get => __renderer ??= new();
        set => __renderer = value;
    }
}