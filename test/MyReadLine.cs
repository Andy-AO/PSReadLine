using Test;
using Xunit.Abstractions;

namespace UnitTestPSReadLine;

public class MyReadLine : ReadLine
{
    public MyReadLine(ConsoleFixture fixture, ITestOutputHelper output, string lang, string os) : base(fixture, output,
        lang, os)
    {
    }
}