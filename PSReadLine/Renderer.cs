using System;
using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.PowerShell.Internal;

namespace Microsoft.PowerShell
{
    public class Renderer
    {
        public static readonly Renderer Singleton = new();
        private static readonly PSConsoleReadLine _rl = PSConsoleReadLine.Singleton;

        private static readonly string[] _spacesArr = new string[80];

        private static readonly RenderData _initialPrevRender = new RenderData
        {
            lines = new[] {new RenderedLineData {columns = 0, line = ""}}
        };

        private readonly StringBuilder _buffer;

        private readonly IConsole _console = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? PlatformWindows.OneTimeInit(_rl)
            : new VirtualTerminal();

        private readonly List<StringBuilder> _consoleBufferLines = new List<StringBuilder>(1)
            {new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth)};

        private int _current;
        private int _emphasisLength;
        private int _emphasisStart;
        private int _initialX;
        private int _initialY;

        private RenderData _previousRender;
        private bool _waitingToRender;

        private Renderer()
        {
        }

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

        public IConsole RLConsole => _console;

        private StringBuilder RLBuffer => _rl.buffer;

        private PSConsoleReadLineOptions Options => _rl.Options;

        public void RecomputeInitialCoords()
        {
            if ((PreviousRender.bufferWidth != RLConsole.BufferWidth)
                || (PreviousRender.bufferHeight != RLConsole.BufferHeight))
            {
                // If the buffer width changed, our initial coordinates
                // may have as well.
                // Recompute X from the buffer width:
                InitialX = InitialX % RLConsole.BufferWidth;

                // Recompute Y from the cursor
                InitialY = 0;
                var pt = ConvertOffsetToPoint(Current);
                InitialY = RLConsole.CursorTop - pt.Y;
            }
        }

        public int LengthInBufferCells(char c)
        {
            if (c < 256)
            {
                // We render ^C for Ctrl+C, so return 2 for control characters
                return Char.IsControl(c) ? 2 : 1;
            }

            // The following is based on http://www.cl.cam.ac.uk/~mgk25/c/wcwidth.c
            // which is derived from http://www.unicode.org/Public/UCD/latest/ucd/EastAsianWidth.txt

            bool isWide = c >= 0x1100 &&
                          (c <= 0x115f || /* Hangul Jamo init. consonants */
                           c == 0x2329 || c == 0x232a ||
                           (c >= 0x2e80 && c <= 0xa4cf &&
                            c != 0x303f) || /* CJK ... Yi */
                           (c >= 0xac00 && c <= 0xd7a3) || /* Hangul Syllables */
                           (c >= 0xf900 && c <= 0xfaff) || /* CJK Compatibility Ideographs */
                           (c >= 0xfe10 && c <= 0xfe19) || /* Vertical forms */
                           (c >= 0xfe30 && c <= 0xfe6f) || /* CJK Compatibility Forms */
                           (c >= 0xff00 && c <= 0xff60) || /* Fullwidth Forms */
                           (c >= 0xffe0 && c <= 0xffe6));
            // We can ignore these ranges because .Net strings use surrogate pairs
            // for this range and we do not handle surrogage pairs.
            // (c >= 0x20000 && c <= 0x2fffd) ||
            // (c >= 0x30000 && c <= 0x3fffd)
            return 1 + (isWide ? 1 : 0);
        }

        public int LengthInBufferCells(string str, int start, int end)
        {
            var sum = 0;
            for (var i = start; i < end; i++)
            {
                var c = str[i];
                if (c == 0x1b && (i + 1) < end && str[i + 1] == '[')
                {
                    // Simple escape sequence skipping
                    i += 2;
                    while (i < end && str[i] != 'm')
                        i++;

                    continue;
                }

                sum += LengthInBufferCells(c);
            }

            return sum;
        }

        public int LengthInBufferCells(string str)
        {
            return LengthInBufferCells(str, 0, str.Length);
        }

        internal Point ConvertOffsetToPoint(int offset)
        {
            int x = InitialX;
            int y = InitialY;

            int bufferWidth = RLConsole.BufferWidth;
            var continuationPromptLength = LengthInBufferCells(Options.ContinuationPrompt);

            for (int i = 0; i < offset; i++)
            {
                char c = RLBuffer[i];
                if (c == '\n')
                {
                    y += 1;
                    x = continuationPromptLength;
                }
                else
                {
                    int size = LengthInBufferCells(c);
                    x += size;
                    // Wrap?  No prompt when wrapping
                    if (x >= bufferWidth)
                    {
                        // If character didn't fit on current line, it will move entirely to the next line.
                        x = ((x == bufferWidth) ? 0 : size);

                        // If cursor is at column 0 and the next character is newline, let the next loop
                        // iteration increment y.
                        if (x != 0 || !(i + 1 < offset && RLBuffer[i + 1] == '\n'))
                        {
                            y += 1;
                        }
                    }
                }
            }

            // If next character actually exists, and isn't newline, check if wider than the space left on the current line.
            if (RLBuffer.Length > offset && RLBuffer[offset] != '\n')
            {
                int size = LengthInBufferCells(RLBuffer[offset]);
                if (x + size > bufferWidth)
                {
                    // Character was wider than remaining space, so character, and cursor, appear on next line.
                    x = 0;
                    y++;
                }
            }

            return new Point {X = x, Y = y};
        }

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
            public int bufferHeight;
            public int bufferWidth;
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