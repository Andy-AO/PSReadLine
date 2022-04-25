/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.PowerShell.Internal;
using static Microsoft.PowerShell.Renderer;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private static readonly Renderer _renderer = Renderer.Singleton;

        private RenderData PreviousRender
        {
            get => _renderer.PreviousRender;
            set => _renderer.PreviousRender = value;
        }

        private static RenderData InitialPrevRender => Renderer.InitialPrevRender;

        private int InitialX
        {
            get => _renderer.InitialX;
            set => _renderer.InitialX = value;
        }

        private int InitialY
        {
            get => _renderer.InitialY;
            set => _renderer.InitialY = value;
        }

        private int Current
        {
            get => _renderer.Current;
            set => _renderer.Current = value;
        }

        private int EmphasisStart
        {
            get => _renderer.EmphasisStart;
            set => _renderer.EmphasisStart = value;
        }

        private int EmphasisLength
        {
            get => _renderer.EmphasisLength;
            set => _renderer.EmphasisLength = value;
        }


        private void RenderWithPredictionQueryPaused()
        {
            // Sometimes we need to re-render the buffer to show status line, or to clear
            // the visual selection, or to clear the visual emphasis.
            // In those cases, the buffer text is unchanged, and thus we can skip querying
            // for prediction during the rendering, but instead, use the existing results.
            using var _ = _Prediction.PauseQuery();
            _renderer.Render();
        }

        private void MoveCursor(int newCursor)
        {
            // Only update screen cursor if the buffer is fully rendered.
            if (!_renderer.WaitingToRender)
            {
                // In case the buffer was resized
                _renderer.RecomputeInitialCoords();
                PreviousRender.bufferWidth = RLConsole.BufferWidth;
                PreviousRender.bufferHeight = RLConsole.BufferHeight;

                var point = _renderer.ConvertOffsetToPoint(newCursor);
                if (point.Y < 0)
                {
                    Ding();
                    return;
                }

                if (point.Y == RLConsole.BufferHeight)
                {
                    // The cursor top exceeds the buffer height, so adjust the initial cursor
                    // position and the to-be-set cursor position for scrolling up the buffer.
                    InitialY -= 1;
                    point.Y -= 1;

                    // Insure the cursor is on the last line of the buffer prior
                    // to issuing a newline to scroll the buffer.
                    RLConsole.SetCursorPosition(point.X, point.Y);

                    // Scroll up the buffer by 1 line.
                    RLConsole.Write("\n");
                }
                else
                {
                    RLConsole.SetCursorPosition(point.X, point.Y);
                }
            }

            // While waiting to render, and a keybinding has occured that is moving the cursor,
            // converting offset to point could potentially result in an invalid screen position,
            // but the insertion point should reflect the move.
            Current = newCursor;
        }

        /// <summary>
        /// Returns the logical line number under the cursor in a multi-line buffer.
        /// When rendering, a logical line may span multiple physical lines.
        /// </summary>
        private int GetLogicalLineNumber()
        {
            var current = Current;
            var lineNumber = 1;

            for (int i = 0; i < current; i++)
            {
                if (buffer[i] == '\n')
                {
                    lineNumber++;
                }
            }

            return lineNumber;
        }

        /// <summary>
        /// Returns the number of logical lines in a multi-line buffer.
        /// When rendering, a logical line may span multiple physical lines.
        /// </summary>
        private int GetLogicalLineCount()
        {
            var count = 1;

            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private bool LineIsMultiLine()
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\n')
                    return true;
            }

            return false;
        }
        private bool PromptYesOrNo(string s)
        {
            _statusLinePrompt = s;
            _renderer.Render();

            var key = ReadKey();

            _statusLinePrompt = null;
            _renderer.Render();
            return key.KeyStr.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Scroll the display up one screen.
        /// </summary>
        public static void ScrollDisplayUp(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Singleton.RLConsole;
            var newTop = console.WindowTop - (numericArg * console.WindowHeight);
            if (newTop < 0)
            {
                newTop = 0;
            }

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        /// Scroll the display up one line.
        /// </summary>
        public static void ScrollDisplayUpLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Singleton.RLConsole;
            var newTop = console.WindowTop - numericArg;
            if (newTop < 0)
            {
                newTop = 0;
            }

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        /// Scroll the display down one screen.
        /// </summary>
        public static void ScrollDisplayDown(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Singleton.RLConsole;
            var newTop = console.WindowTop + (numericArg * console.WindowHeight);
            if (newTop > (console.BufferHeight - console.WindowHeight))
            {
                newTop = (console.BufferHeight - console.WindowHeight);
            }

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        /// Scroll the display down one line.
        /// </summary>
        public static void ScrollDisplayDownLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Singleton.RLConsole;
            var newTop = console.WindowTop + numericArg;
            if (newTop > (console.BufferHeight - console.WindowHeight))
            {
                newTop = (console.BufferHeight - console.WindowHeight);
            }

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        /// Scroll the display to the top.
        /// </summary>
        public static void ScrollDisplayTop(ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton.RLConsole.SetWindowPosition(0, 0);
        }

        /// <summary>
        /// Scroll the display to the cursor.
        /// </summary>
        public static void ScrollDisplayToCursor(ConsoleKeyInfo? key = null, object arg = null)
        {
            // Ideally, we'll put the last input line at the bottom of the window
            int offset = Singleton.buffer.Length;
            var point = _renderer.ConvertOffsetToPoint(offset);

            var console = Singleton.RLConsole;
            var newTop = point.Y - console.WindowHeight + 1;

            // If the cursor is already visible, and we're on the first
            // page-worth of the buffer, then just scroll to the top (we can't
            // scroll to before the beginning of the buffer).
            //
            // Note that we don't want to just return, because the window may
            // have been scrolled way past the end of the content, so we really
            // do need to set the new window top to 0 to bring it back into
            // view.
            if (newTop < 0)
            {
                newTop = 0;
            }

            // But if the cursor won't be visible, make sure it is.
            if (newTop > console.CursorTop)
            {
                // Add 10 for some extra context instead of putting the
                // cursor on the bottom line.
                newTop = console.CursorTop - console.WindowHeight + 10;
            }

            // But we can't go past the end of the buffer.
            if (newTop > (console.BufferHeight - console.WindowHeight))
            {
                newTop = (console.BufferHeight - console.WindowHeight);
            }

            console.SetWindowPosition(0, newTop);
        }
    }
}