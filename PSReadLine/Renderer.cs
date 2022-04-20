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

        public int EmphasisLength
        {
            get => _emphasisLength;
            set => _emphasisLength = value;
        }

        public RenderData PreviousRender
        {
            get => _previousRender;
            set => _previousRender = value;
        }

        public int InitialX
        {
            get => _initialX;
            set => _initialX = value;
        }

        public int InitialY
        {
            get => _initialY;
            set => _initialY = value;
        }

        public bool WaitingToRender
        {
            get => _waitingToRender;
            set => _waitingToRender = value;
        }

        public int Current
        {
            get => _current;
            set => _current = value;
        }

        public int EmphasisStart
        {
            get => _emphasisStart;
            set => _emphasisStart = value;
        }

        public static RenderData InitialPrevRender => _initialPrevRender;

        public static string[] SpacesArr => _spacesArr;

        private static readonly string[] _spacesArr = new string[80];

        private static readonly RenderData _initialPrevRender = new RenderData
        {
            lines = new[] { new RenderedLineData{ columns = 0, line = ""}}
        };
        private RenderData _previousRender;
        private int _initialX; 
        private int _initialY; 
        private bool _waitingToRender;

        private int _current;
        private int _emphasisStart;
        private int _emphasisLength;
    }
}