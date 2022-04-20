using System.Management.Automation.Language;

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
    }
}