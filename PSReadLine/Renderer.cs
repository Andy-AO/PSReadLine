using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Text;

namespace Microsoft.PowerShell
{
    public class Renderer
    {
        public static readonly Renderer Singleton = new();
        private static readonly PSConsoleReadLine _rl = PSConsoleReadLine.Singleton;

        public struct LineInfoForRendering
        {
            public int CurrentLogicalLineIndex;
            public int CurrentPhysicalLineCount;
            public int PreviousLogicalLineIndex;
            public int PreviousPhysicalLineCount;
            public int PseudoPhysicalLineOffset;
        }

        public struct RenderedLineData
        {
            public string line;
            public int columns;
        }

        public class RenderData
        {
            public int bufferWidth;
            public int bufferHeight;
            public bool errorPrompt;
            public RenderedLineData[] lines;
        }

        public class SavedTokenState
        {
            internal Token[] Tokens { get; set; }
            internal int Index { get; set; }
            internal string Color { get; set; }
        }

        private readonly List<StringBuilder> _consoleBufferLines = new List<StringBuilder>(1)
            {new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth)};

        public List<StringBuilder> ConsoleBufferLines => _consoleBufferLines;
    }
}