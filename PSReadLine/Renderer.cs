using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.PowerShell.Internal;
using Microsoft.PowerShell.PSReadLine;

namespace Microsoft.PowerShell
{
    public partial class Renderer
    {
        private readonly Stopwatch _lastRenderTime = Stopwatch.StartNew();


        private List<StringBuilder> ConsoleBufferLines { get; set; } = new(1)
            {new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth)};

        internal string StatusLinePrompt { get; set; }


        internal RenderData PreviousRender { get; set; } = InitialPrevRender;

        internal int InitialX { get; set; }

        internal int InitialY { get; set; }

        private bool WaitingToRender { get; set; }

        internal int Current { get; set; }

        internal static RenderData InitialPrevRender { get; } = new()
        {
            lines = new[] {new RenderedLineData {columns = 0, line = ""}}
        };

        public Renderer()
        {
            EP.EmphasisInit();
            PreviousRender.bufferWidth = Console.BufferWidth;
            PreviousRender.bufferHeight = Console.BufferHeight;
            InitialX = Console.CursorLeft;
            InitialY = Console.CursorTop;
        }

        static Renderer()
        {
            Console = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? PlatformWindows.OneTimeInit(_rl)
                : new VirtualTerminal();
        }

        internal static string[] SpacesArr { get; } = new string[80];

        internal static IConsole Console;

        private static Renderer _s => _renderer;

        internal void RenderWithPredictionQueryPaused()
        {
            // Sometimes we need to re-render the buffer to show status line, or to clear
            // the visual selection, or to clear the visual emphasis.
            // In those cases, the buffer text is unchanged, and thus we can skip querying
            // for prediction during the rendering, but instead, use the existing results.
            using var _ = _rl._Prediction.PauseQuery();
            Render();
        }

        /// <summary>
        ///     Returns the number of logical lines in a multi-line buffer.
        ///     When rendering, a logical line may span multiple physical lines.
        /// </summary>
        internal int GetLogicalLineNumber()
        {
            var current = Current;
            var lineNumber = 1;

            for (var i = 0; i < current; i++)
                if (_rl.buffer[i] == '\n')
                    lineNumber++;

            return lineNumber;
        }

        internal int GetLogicalLineCount()
        {
            var count = 1;

            for (var i = 0; i < _rl.buffer.Length; i++)
                if (_rl.buffer[i] == '\n')
                    count++;

            return count;
        }

        internal bool LineIsMultiLine()
        {
            for (var i = 0; i < _rl.buffer.Length; i++)
                if (_rl.buffer[i] == '\n')
                    return true;

            return false;
        }

        internal StringBuilder StatusBuffer { get; } = new(256);

        internal bool PromptYesOrNo(string s)
        {
            StatusLinePrompt = s;
            Render();

            var key = RL.ReadKey();

            StatusLinePrompt = null;
            Render();
            return key.KeyStr.Equals("y", StringComparison.OrdinalIgnoreCase);
        }

        internal void MoveCursor(int newCursor)
        {
            // Only update screen cursor if the buffer is fully rendered.
            if (!WaitingToRender)
            {
                // In case the buffer was resized
                RecomputeInitialCoords();
                PreviousRender.bufferWidth = Console.BufferWidth;
                PreviousRender.bufferHeight = Console.BufferHeight;

                var point = ConvertOffsetToPoint(newCursor);
                if (point.Y < 0)
                {
                    RL.Ding();
                    return;
                }

                if (point.Y == Console.BufferHeight)
                {
                    // The cursor top exceeds the buffer height, so adjust the initial cursor
                    // position and the to-be-set cursor position for scrolling up the buffer.
                    InitialY -= 1;
                    point.Y -= 1;

                    // Insure the cursor is on the last line of the buffer prior
                    // to issuing a newline to scroll the buffer.
                    Console.SetCursorPosition(point.X, point.Y);

                    // Scroll up the buffer by 1 line.
                    Console.Write("\n");
                }
                else
                {
                    Console.SetCursorPosition(point.X, point.Y);
                }
            }

            // While waiting to render, and a keybinding has occured that is moving the cursor,
            // converting offset to point could potentially result in an invalid screen position,
            // but the insertion point should reflect the move.
            Current = newCursor;
        }

        internal void Render()
        {
            // If there are a bunch of keys queued up, skip rendering if we've rendered
            // recently.
            if (_rl._queuedKeys.Count > 10 && _lastRenderTime.ElapsedMilliseconds < 50)
            {
                // We won't render, but most likely the tokens will be different, so make
                // sure we don't use old tokens, also allow garbage to get collected.
                WaitingToRender = true;
                return;
            }

            ForceRender();
        }


        internal void GetRegion(out int start, out int length)
        {
            if (_rl._mark < Current)
            {
                start = _rl._mark;
                length = Current - start;
            }
            else
            {
                start = Current;
                length = _rl._mark - start;
            }
        }

        internal void ForceRender()
        {
            var defaultColor = VTColorUtils.DefaultColor;

            RenderData renderData = new DataBuilder().Generate(defaultColor);

            // And then do the real work of writing to the screen.
            // Rendering data is in reused
            ReallyRender(renderData, defaultColor);

            // Cleanup some excess buffers, saving a few because we know we'll use them.
            var bufferCount = ConsoleBufferLines.Count;
            var excessBuffers = bufferCount - renderData.lines.Length;
            if (excessBuffers > 5) ConsoleBufferLines.RemoveRange(renderData.lines.Length, excessBuffers);
        }

        internal int ConvertLineAndColumnToOffset(Point point)
        {
            int offset;
            var x = InitialX;
            var y = InitialY;

            var bufferWidth = Console.BufferWidth;
            var continuationPromptLength = LengthInBufferCells(_rl.Options.ContinuationPrompt);
            for (offset = 0; offset < _rl.buffer.Length; offset++)
            {
                // If we are on the correct line, return when we find
                // the correct column
                if (point.Y == y && point.X <= x) return offset;

                var c = _rl.buffer[offset];
                if (c == '\n')
                {
                    // If we are about to move off of the correct line,
                    // the line was shorter than the column we wanted so return.
                    if (point.Y == y) return offset;

                    y += 1;
                    x = continuationPromptLength;
                }
                else
                {
                    var size = LengthInBufferCells(c);
                    x += size;
                    // Wrap?  No prompt when wrapping
                    if (x >= bufferWidth)
                    {
                        // If character didn't fit on current line, it will move entirely to the next line.
                        x = x == bufferWidth ? 0 : size;

                        // If cursor is at column 0 and the next character is newline, let the next loop
                        // iteration increment y.
                        if (x != 0 || !(offset + 1 < _rl.buffer.Length && _rl.buffer[offset + 1] == '\n')) y += 1;
                    }
                }
            }

            // Return -1 if y is out of range, otherwise the last line was shorter
            // than we wanted, but still in range so just return the last offset.
            return point.Y == y ? offset : -1;
        }


        private void ReallyRender(RenderData renderData, string defaultColor)
        {
            var activeColor = "";
            var bufferWidth = Console.BufferWidth;
            var bufferHeight = Console.BufferHeight;

            void UpdateColorsIfNecessary(string newColor)
            {
                if (!ReferenceEquals(newColor, activeColor))
                {
                    Console.Write(newColor);
                    activeColor = newColor;
                }
            }

            // In case the buffer was resized
            RecomputeInitialCoords();
            renderData.bufferWidth = bufferWidth;
            renderData.bufferHeight = bufferHeight;

            // Make cursor invisible while we're rendering.
            Console.CursorVisible = false;

            // Change the prompt color if the parsing error state changed.
            var cursorMovedToInitialPos = RenderErrorPrompt(renderData, defaultColor);

            // Calculate what to render and where to start the rendering.
            LineInfoForRendering lineInfoForRendering;
            CalculateWhereAndWhatToRender(cursorMovedToInitialPos, renderData, out lineInfoForRendering);

            var previousRenderLines = PreviousRender.lines;
            var previousLogicalLine = lineInfoForRendering.PreviousLogicalLineIndex;
            var previousPhysicalLine = lineInfoForRendering.PreviousPhysicalLineCount;

            var renderLines = renderData.lines;
            var logicalLine = lineInfoForRendering.CurrentLogicalLineIndex;
            var physicalLine = lineInfoForRendering.CurrentPhysicalLineCount;
            var pseudoPhysicalLineOffset = lineInfoForRendering.PseudoPhysicalLineOffset;

            var lenPrevLastLine = 0;
            var logicalLineStartIndex = logicalLine;
            var physicalLineStartCount = physicalLine;

            for (; logicalLine < renderLines.Length; logicalLine++)
            {
                if (logicalLine != logicalLineStartIndex) Console.Write("\n");

                var lineData = renderLines[logicalLine];
                Console.Write(lineData.line);

                physicalLine += PhysicalLineCount(lineData.columns, logicalLine == 0, out var lenLastLine);

                // Find the previous logical line (if any) that would have rendered
                // the current physical line because we may need to clear it.
                // We don't clear it unconditionally to allow things like a prompt
                // on the right side of the line.

                while (physicalLine > previousPhysicalLine
                       && previousLogicalLine < previousRenderLines.Length)
                {
                    previousPhysicalLine += PhysicalLineCount(
                        previousRenderLines[previousLogicalLine].columns,
                        previousLogicalLine == 0,
                        out lenPrevLastLine);
                    previousLogicalLine += 1;
                }

                // Our current physical line might be in the middle of the
                // previous logical line, in which case we need to blank
                // the rest of the line, otherwise we blank just the end
                // of what was written.
                var lenToClear = 0;
                if (physicalLine == previousPhysicalLine)
                {
                    // We're on the end of the previous logical line, so we
                    // only need to clear any extra.

                    if (lenPrevLastLine > lenLastLine)
                        lenToClear = lenPrevLastLine - lenLastLine;
                }
                else if (physicalLine < previousPhysicalLine)
                {
                    // We're in the middle of a previous logical line, we
                    // need to clear to the end of the line.
                    if (lenLastLine < bufferWidth)
                    {
                        lenToClear = bufferWidth - lenLastLine;
                        if (physicalLine == 1)
                            lenToClear -= InitialX;
                    }
                }

                if (lenToClear > 0)
                {
                    UpdateColorsIfNecessary(defaultColor);
                    Console.Write(RL.Spaces(lenToClear));
                }
            }

            UpdateColorsIfNecessary(defaultColor);

            // The last logical line is shorter than our previous render? Clear them.
            for (var currentLines = physicalLine; currentLines < previousPhysicalLine;)
            {
                Console.SetCursorPosition(0, InitialY + currentLines);

                currentLines++;
                var lenToClear = currentLines == previousPhysicalLine ? lenPrevLastLine : bufferWidth;
                if (lenToClear > 0) Console.Write(RL.Spaces(lenToClear));
            }

            // Fewer logical lines than our previous render? Clear them.
            for (var line = previousLogicalLine; line < previousRenderLines.Length; line++)
            {
                if (line > previousLogicalLine || logicalLineStartIndex < renderLines.Length)
                    // For the first of the remaining previous logical lines, if we didn't actually
                    // render anything for the current logical lines, then the cursor is already at
                    // the beginning of the right physical line that should be cleared, and thus no
                    // need to write a new line in such case.
                    // In other cases, we need to write a new line to get the cursor to the correct
                    // physical line.

                    Console.Write("\n");

                // No need to write new line if all we need is to clear the extra previous render.
                Console.Write(RL.Spaces(previousRenderLines[line].columns));
            }

            // Preserve the current render data.
            PreviousRender = renderData;

            // If we counted pseudo physical lines, deduct them to get the real physical line counts
            // before updating '_initialY'.
            physicalLine -= pseudoPhysicalLineOffset;

            // Reset the colors after we've finished all our rendering.
            Console.Write(VTColorUtils.AnsiReset);

            if (InitialY + physicalLine > bufferHeight)
            {
                // We had to scroll to render everything, update _initialY
                InitialY = bufferHeight - physicalLine;
            }
            else if (pseudoPhysicalLineOffset > 0)
            {
                // When we rewrote a logical line (or part of a logical line) that had previously been scrolled up-off
                // the buffer (fully or partially), we need to adjust '_initialY' if the changes to that logical line
                // don't result in the same number of physical lines to be scrolled up-off the buffer.

                // Calculate the total number of physical lines starting from the logical line we re-wrote.
                var physicalLinesStartingFromTheRewrittenLogicalLine =
                    physicalLine - (physicalLineStartCount - pseudoPhysicalLineOffset);

                Debug.Assert(
                    bufferHeight + pseudoPhysicalLineOffset >= physicalLinesStartingFromTheRewrittenLogicalLine,
                    "number of physical lines starting from the first changed logical line should be no more than the buffer height plus the pseudo lines we added.");

                var offset = physicalLinesStartingFromTheRewrittenLogicalLine > bufferHeight
                    ? pseudoPhysicalLineOffset - (physicalLinesStartingFromTheRewrittenLogicalLine - bufferHeight)
                    : pseudoPhysicalLineOffset;

                InitialY += offset;
            }

            // Calculate the coord to place the cursor for the next input.
            var point = ConvertOffsetToPoint(Current);

            if (point.Y == bufferHeight)
            {
                // The cursor top exceeds the buffer height, so we need to
                // scroll up the buffer by 1 line.
                Console.Write("\n");

                // Adjust the initial cursor position and the to-be-set cursor position
                // after scrolling up the buffer.
                InitialY -= 1;
                point.Y -= 1;
            }
            else if (point.Y < 0)
            {
                // This could happen in at least 3 cases:
                //
                //   1. when you are adding characters to the first line in the buffer (top = 0) to make the logical line
                //      wrap to one extra physical line. This would cause the buffer to scroll up and push the line being
                //      edited up-off the buffer.
                //   2. when you are deleting characters (Backspace) from the first line in the buffer without changing the
                //      number of physical lines (either editing the same logical line or causing the current logical line
                //      to merge in the previous but still span to the current physical line). The cursor is supposed to
                //      appear in the previous line (which is off the buffer).
                //   3. Both 'bck-i-search' and 'fwd-i-search' may find a history command with multi-line text, and the
                //      matching string in the text, where the cursor is supposed to be moved to, will be scrolled up-off
                //      the buffer after rendering.
                //
                // In these case, we move the cursor to the left-most position of the first line, where it's closest to
                // the real position it should be in the ideal world.

                // First update '_current' to the index of the first character that appears on the line 0,
                // then we call 'ConvertOffsetToPoint' again to get the right cursor position to use.
                point.X = point.Y = 0;
                Current = ConvertLineAndColumnToOffset(point);
                point = ConvertOffsetToPoint(Current);
            }

            Console.SetCursorPosition(point.X, point.Y);
            Console.CursorVisible = true;

            // TODO: set WindowTop if necessary

            _lastRenderTime.Restart();
            WaitingToRender = false;
        }

        /// <summary>
        ///     Given the length of a logical line, calculate the number of physical lines it takes to render
        ///     the logical line on the console.
        /// </summary>
        private int PhysicalLineCount(int columns, bool isFirstLogicalLine, out int lenLastPhysicalLine)
        {
            if (columns == 0)
            {
                // This could happen for a new logical line with an empty-string continuation prompt.
                lenLastPhysicalLine = 0;
                return 1;
            }

            var cnt = 1;
            var bufferWidth = Console.BufferWidth;

            if (isFirstLogicalLine)
            {
                // The first logical line has the user prompt that we don't touch
                // (except where we turn part to red, but we've finished that
                // before getting here.)
                var maxFirstLine = bufferWidth - InitialX;
                if (columns > maxFirstLine)
                {
                    cnt += 1;
                    columns -= maxFirstLine;
                }
                else
                {
                    lenLastPhysicalLine = columns;
                    return 1;
                }
            }

            lenLastPhysicalLine = columns % bufferWidth;
            if (lenLastPhysicalLine == 0)
            {
                // Handle the last column when the columns is equal to n * bufferWidth
                // where n >= 1 integers
                lenLastPhysicalLine = bufferWidth;
                return cnt - 1 + columns / bufferWidth;
            }

            return cnt + columns / bufferWidth;
        }

        /// <summary>
        ///     Flip the color on the prompt if the error state changed.
        /// </summary>
        /// <returns>
        ///     A bool value indicating whether we need to flip the color,
        ///     namely whether we moved cursor to the initial position.
        /// </returns>
        private bool RenderErrorPrompt(RenderData renderData, string defaultColor)
        {
            if (InitialY < 0
                || _rl.Options.PromptText == null
                || _rl.Options.PromptText.Length == 0
                || string.IsNullOrEmpty(_rl.Options.PromptText[0]))
                // No need to flip the prompt color if either the error prompt is not defined
                // or the initial cursor point has already been scrolled off the buffer.
                return false;

            // We may need to flip the color on the prompt if the error state changed.

            renderData.errorPrompt = _rl.ParseErrors != null && _rl.ParseErrors.Length > 0;
            if (renderData.errorPrompt == PreviousRender.errorPrompt)
                // No need to flip the prompt color if the error state didn't change.
                return false;

            // We need to update the prompt
            Console.SetCursorPosition(InitialX, InitialY);

            var promptText =
                renderData.errorPrompt && _rl.Options.PromptText.Length == 2
                    ? _rl.Options.PromptText[1]
                    : _rl.Options.PromptText[0];

            // promptBufferCells is the number of visible characters in the prompt
            var promptBufferCells = LengthInBufferCells(promptText);
            var renderErrorPrompt = false;
            var bufferWidth = Console.BufferWidth;

            if (Console.CursorLeft >= promptBufferCells)
            {
                renderErrorPrompt = true;
                Console.CursorLeft -= promptBufferCells;
            }
            else
            {
                // The 'CursorLeft' could be less than error-prompt-cell-length in one of the following 3 cases:
                //   1. console buffer was resized, which causes the initial cursor to appear on the next line;
                //   2. prompt string gets longer (e.g. by 'cd' into nested folders), which causes the line to be wrapped to the next line;
                //   3. the prompt function was changed, which causes the new prompt string is shorter than the error prompt.
                // Here, we always assume it's the case 1 or 2, and wrap back to the previous line to change the error prompt color.
                // In case of case 3, the rendering would be off, but it's more of a user error because the prompt is changed without
                // updating 'PromptText' with 'Set-PSReadLineOption'.

                var diffs = promptBufferCells - Console.CursorLeft;
                var newX = bufferWidth - diffs % bufferWidth;
                var newY = InitialY - diffs / bufferWidth - 1;

                // newY could be less than 0 if 'PromptText' is manually set to be a long string.
                if (newY >= 0)
                {
                    renderErrorPrompt = true;
                    Console.SetCursorPosition(newX, newY);
                }
            }

            if (renderErrorPrompt)
            {
                if (!promptText.Contains("\x1b"))
                {
                    var color = renderData.errorPrompt ? _rl.Options._errorColor : defaultColor;
                    Console.Write(color);
                    Console.Write(promptText);
                    Console.Write(VTColorUtils.AnsiReset);
                }
                else
                {
                    Console.Write(promptText);
                }
            }

            return true;
        }

        internal void CalculateWhereAndWhatToRender(bool cursorMovedToInitialPos, RenderData renderData,
            out LineInfoForRendering lineInfoForRendering)
        {
            var bufferWidth = Console.BufferWidth;
            var bufferHeight = Console.BufferHeight;

            var previousRenderLines = PreviousRender.lines;
            var previousLogicalLine = 0;
            var previousPhysicalLine = 0;

            var renderLines = renderData.lines;
            var logicalLine = 0;
            var physicalLine = 0;
            var pseudoPhysicalLineOffset = 0;

            var hasToWriteAll = true;

            if (renderLines.Length > 1)
            {
                // There are multiple logical lines, so it's possible the first N logical lines are not affected by the user's editing,
                // in which case, we can skip rendering until reaching the first changed logical line.

                var minLinesLength = previousRenderLines.Length;
                var linesToCheck = -1;

                if (renderLines.Length < previousRenderLines.Length)
                {
                    minLinesLength = renderLines.Length;

                    // When the initial cursor position has been scrolled off the buffer, it's possible the editing deletes some texts and
                    // potentially causes the final cursor position to be off the buffer as well. In this case, we should start rendering
                    // from the logical line where the cursor is supposed to be moved to eventually.
                    // Here we check for this situation, and calculate the physical line count to check later if we are in this situation.

                    if (InitialY < 0)
                    {
                        var y = ConvertOffsetToPoint(Current).Y;
                        if (y < 0)
                            // Number of physical lines from the initial row to the row where the cursor is supposed to be set at.
                            linesToCheck = y - InitialY + 1;
                    }
                }

                // Find the first logical line that was changed.
                for (; logicalLine < minLinesLength; logicalLine++)
                {
                    // Found the first different logical line? Break out the loop.
                    if (renderLines[logicalLine].line != previousRenderLines[logicalLine].line) break;

                    var count = PhysicalLineCount(renderLines[logicalLine].columns, logicalLine == 0, out _);
                    physicalLine += count;

                    if (linesToCheck < 0) continue;

                    if (physicalLine >= linesToCheck)
                    {
                        physicalLine -= count;
                        break;
                    }
                }

                if (logicalLine > 0)
                {
                    // Some logical lines at the top were not affected by the editing.
                    // We only need to write starting from the first changed logical line.
                    hasToWriteAll = false;
                    previousLogicalLine = logicalLine;
                    previousPhysicalLine = physicalLine;

                    var newTop = InitialY + physicalLine;
                    if (newTop == bufferHeight)
                    {
                        if (logicalLine < renderLines.Length)
                        {
                            // This could happen when adding a new line in the end of the very last line.
                            // In this case, we scroll up by writing out a new line.
                            Console.SetCursorPosition(bufferWidth - 1, bufferHeight - 1);
                            Console.Write("\n");
                        }

                        // It might happen that 'logicalLine == renderLines.Length'. This means the current
                        // logical lines to be rendered are exactly the same the the previous logical lines.
                        // No need to do anything in this case, as we don't need to render anything.
                    }
                    else
                    {
                        // For the logical line that we will start to re-render from, it's possible that
                        //   1. the whole logical line had already been scrolled up-off the buffer. This could happen when you backward delete characters
                        //      on the first line in buffer and cause the current line to be folded to the previous line.
                        //   2. the logical line spans on multiple physical lines and the top a few physical lines had already been scrolled off the buffer.
                        //      This could happen when you edit on the top a few physical lines in the buffer, which belong to a longer logical line.
                        // Either of them will cause 'newTop' to be less than 0.
                        if (newTop < 0)
                        {
                            // In this case, we will render the whole logical line starting from the upper-left-most point of the window.
                            // By doing this, we are essentially adding a few pseudo physical lines (the physical lines that belong to the logical line but
                            // had been scrolled off the buffer would be re-rendered). So, update 'physicalLine'.
                            pseudoPhysicalLineOffset = 0 - newTop;
                            physicalLine += pseudoPhysicalLineOffset;
                            newTop = 0;
                        }

                        Console.SetCursorPosition(0, newTop);
                    }
                }
            }

            if (hasToWriteAll && !cursorMovedToInitialPos)
            {
                // The editing was in the first logical line. We have to write everything in this case.
                // Move the cursor to the initial position if we haven't done so.
                if (InitialY < 0)
                {
                    // The prompt had been scrolled up-off the buffer. Now we are about to render from the very
                    // beginning, so we clear the screen and invoke/print the prompt line.
                    Console.Write("\x1b[2J");
                    Console.SetCursorPosition(0, Console.WindowTop);

                    var newPrompt = RL.Prompt;
                    if (!string.IsNullOrEmpty(newPrompt)) Console.Write(newPrompt);

                    InitialX = Console.CursorLeft;
                    InitialY = Console.CursorTop;
                    PreviousRender = InitialPrevRender;
                }
                else
                {
                    Console.SetCursorPosition(InitialX, InitialY);
                }
            }

            lineInfoForRendering = default;
            lineInfoForRendering.CurrentLogicalLineIndex = logicalLine;
            lineInfoForRendering.CurrentPhysicalLineCount = physicalLine;
            lineInfoForRendering.PreviousLogicalLineIndex = previousLogicalLine;
            lineInfoForRendering.PreviousPhysicalLineCount = previousPhysicalLine;
            lineInfoForRendering.PseudoPhysicalLineOffset = pseudoPhysicalLineOffset;
        }

        internal void RecomputeInitialCoords()
        {
            if (PreviousRender.bufferWidth != Console.BufferWidth
                || PreviousRender.bufferHeight != Console.BufferHeight)
            {
                // If the buffer width changed, our initial coordinates
                // may have as well.
                // Recompute X from the buffer width:
                InitialX = InitialX % Console.BufferWidth;

                // Recompute Y from the cursor
                InitialY = 0;
                var pt = ConvertOffsetToPoint(Current);
                InitialY = Console.CursorTop - pt.Y;
            }
        }

        internal int LengthInBufferCells(char c)
        {
            if (c < 256)
                // We render ^C for Ctrl+C, so return 2 for control characters
                return char.IsControl(c) ? 2 : 1;

            // The following is based on http://www.cl.cam.ac.uk/~mgk25/c/wcwidth.c
            // which is derived from http://www.unicode.org/internal/UCD/latest/ucd/EastAsianWidth.txt

            var isWide = c >= 0x1100 &&
                         (c <= 0x115f || /* Hangul Jamo init. consonants */
                          c == 0x2329 || c == 0x232a ||
                          c >= 0x2e80 && c <= 0xa4cf &&
                          c != 0x303f || /* CJK ... Yi */
                          c >= 0xac00 && c <= 0xd7a3 || /* Hangul Syllables */
                          c >= 0xf900 && c <= 0xfaff || /* CJK Compatibility Ideographs */
                          c >= 0xfe10 && c <= 0xfe19 || /* Vertical forms */
                          c >= 0xfe30 && c <= 0xfe6f || /* CJK Compatibility Forms */
                          c >= 0xff00 && c <= 0xff60 || /* Fullwidth Forms */
                          c >= 0xffe0 && c <= 0xffe6);
            // We can ignore these ranges because .Net strings use surrogate pairs
            // for this range and we do not handle surrogage pairs.
            // (c >= 0x20000 && c <= 0x2fffd) ||
            // (c >= 0x30000 && c <= 0x3fffd)
            return 1 + (isWide ? 1 : 0);
        }

        internal int LengthInBufferCells(string str, int start, int end)
        {
            var sum = 0;
            for (var i = start; i < end; i++)
            {
                var c = str[i];
                if (c == 0x1b && i + 1 < end && str[i + 1] == '[')
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

        internal int LengthInBufferCells(string str)
        {
            return LengthInBufferCells(str, 0, str.Length);
        }

        internal Point ConvertOffsetToPoint(int offset)
        {
            var x = InitialX;
            var y = InitialY;

            var bufferWidth = Console.BufferWidth;
            var continuationPromptLength = LengthInBufferCells(_rl.Options.ContinuationPrompt);

            for (var i = 0; i < offset; i++)
            {
                var c = _rl.buffer[i];
                if (c == '\n')
                {
                    y += 1;
                    x = continuationPromptLength;
                }
                else
                {
                    var size = LengthInBufferCells(c);
                    x += size;
                    // Wrap?  No prompt when wrapping
                    if (x >= bufferWidth)
                    {
                        // If character didn't fit on current line, it will move entirely to the next line.
                        x = x == bufferWidth ? 0 : size;

                        // If cursor is at column 0 and the next character is newline, let the next loop
                        // iteration increment y.
                        if (x != 0 || !(i + 1 < offset && _rl.buffer[i + 1] == '\n')) y += 1;
                    }
                }
            }

            // If next character actually exists, and isn't newline, check if wider than the space left on the current line.
            if (_rl.buffer.Length > offset && _rl.buffer[offset] != '\n')
            {
                var size = LengthInBufferCells(_rl.buffer[offset]);
                if (x + size > bufferWidth)
                {
                    // Character was wider than remaining space, so character, and cursor, appear on next line.
                    x = 0;
                    y++;
                }
            }

            return new Point {X = x, Y = y};
        }

        /// <summary>
        ///     Scroll the display up one screen.
        /// </summary>
        public static void ScrollDisplayUp(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Console;
            var newTop = console.WindowTop - numericArg * console.WindowHeight;
            if (newTop < 0) newTop = 0;

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        ///     Scroll the display up one screen.
        /// </summary>
        /// <summary>
        ///     Scroll the display up one line.
        /// </summary>
        public static void ScrollDisplayUpLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Console;
            var newTop = console.WindowTop - numericArg;
            if (newTop < 0) newTop = 0;

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        ///     Scroll the display down one screen.
        /// </summary>
        public static void ScrollDisplayDown(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Console;
            var newTop = console.WindowTop + numericArg * console.WindowHeight;
            if (newTop > console.BufferHeight - console.WindowHeight)
                newTop = console.BufferHeight - console.WindowHeight;

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        ///     Scroll the display down one line.
        /// </summary>
        public static void ScrollDisplayDownLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, +1);
            var console = Console;
            var newTop = console.WindowTop + numericArg;
            if (newTop > console.BufferHeight - console.WindowHeight)
                newTop = console.BufferHeight - console.WindowHeight;

            console.SetWindowPosition(0, newTop);
        }

        /// <summary>
        ///     Scroll the display to the top.
        /// </summary>
        public static void ScrollDisplayTop(ConsoleKeyInfo? key = null, object arg = null)
        {
            Console.SetWindowPosition(0, 0);
        }

        /// <summary>
        ///     Scroll the display to the cursor.
        /// </summary>
        public static void ScrollDisplayToCursor(ConsoleKeyInfo? key = null, object arg = null)
        {
            // Ideally, we'll put the last input line at the bottom of the window
            var offset = _rl.buffer.Length;
            var s = _s;
            var point = s.ConvertOffsetToPoint(offset);

            var console = Console;
            var newTop = point.Y - console.WindowHeight + 1;

            // If the cursor is already visible, and we're on the first
            // page-worth of the buffer, then just scroll to the top (we can't
            // scroll to before the beginning of the buffer).
            //
            // Note that we don't want to just return, because the window may
            // have been scrolled way past the end of the content, so we really
            // do need to set the new window top to 0 to bring it back into
            // view.
            if (newTop < 0) newTop = 0;

            // But if the cursor won't be visible, make sure it is.
            if (newTop > console.CursorTop)
                // Add 10 for some extra context instead of putting the
                // cursor on the bottom line.
                newTop = console.CursorTop - console.WindowHeight + 10;

            // But we can't go past the end of the buffer.
            if (newTop > console.BufferHeight - console.WindowHeight)
                newTop = console.BufferHeight - console.WindowHeight;

            console.SetWindowPosition(0, newTop);
        }

        internal struct LineInfoForRendering
        {
            internal int CurrentLogicalLineIndex;
            internal int CurrentPhysicalLineCount;
            internal int PreviousLogicalLineIndex;
            internal int PreviousPhysicalLineCount;
            internal int PseudoPhysicalLineOffset;
        }

        internal struct RenderedLineData
        {
            internal string line;
            internal int columns;
        }

        internal class RenderData
        {
            internal int bufferHeight;
            internal int bufferWidth;
            internal bool errorPrompt = false;
            internal RenderedLineData[] lines;
        }

        internal class SavedTokenState
        {
            internal Token[] Tokens { get; set; }
            internal int Index { get; set; }
            internal string Color { get; set; }
        }
    }
}