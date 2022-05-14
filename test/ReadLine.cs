using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Management.Automation.Runspaces;
using System.Reflection;
using Microsoft.PowerShell;
using UnitTestPSReadLine;
using Xunit;
using Xunit.Abstractions;

namespace Test;

public abstract partial class ReadLine : ReadLineBase
{
    // These colors are random - we just use these colors instead of the defaults
    // so the tests aren't sensitive to tweaks to the default colors.
    protected ReadLine(ConsoleFixture fixture, ITestOutputHelper output, string lang, string os) : base(fixture, output,
        lang, os)
    {
    }
}