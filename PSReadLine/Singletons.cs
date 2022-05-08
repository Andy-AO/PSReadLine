namespace Microsoft.PowerShell.PSReadLine;

public static class Singletons
{
    public static History _hs => History.Singleton;
    public static PSConsoleReadLine _rl => PSConsoleReadLine.Singleton;
    public static Renderer _renderer => Renderer.Singleton;
}