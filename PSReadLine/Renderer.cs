using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.PowerShell.Internal;
using RL = Microsoft.PowerShell.PSConsoleReadLine;

namespace Microsoft.PowerShell
{
    internal class Renderer
    {
        private static readonly Renderer _s = new();
        private static readonly RL _rl = RL.Singleton;
        private readonly Stopwatch _lastRenderTime = Stopwatch.StartNew();

        public Renderer()
        {
        }

        internal List<StringBuilder> ConsoleBufferLines { get; } = new(1)
            {new(PSConsoleReadLineOptions.CommonWidestConsoleWidth)};

        internal int EmphasisLength { get; set; }

        internal RenderData PreviousRender { get; set; }

        internal int InitialX { get; set; }

        internal int InitialY { get; set; }

        internal bool WaitingToRender { get; set; }

        internal int Current { get; set; }

        internal int EmphasisStart { get; set; }

        internal static RenderData InitialPrevRender { get; } = new()
        {
            lines = new[] {new RenderedLineData {columns = 0, line = ""}}
        };

        internal static string[] SpacesArr { get; } = new string[80];
        internal readonly IConsole _console = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? PlatformWindows.OneTimeInit(_rl)
            : new VirtualTerminal();
        internal static Renderer Singleton => _s;

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

        internal bool PromptYesOrNo(string s)
        {
            _rl._statusLinePrompt = s;
            Render();

            var key = RL.ReadKey();

            _rl._statusLinePrompt = null;
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
                PreviousRender.bufferWidth = _console.BufferWidth;
                PreviousRender.bufferHeight = _console.BufferHeight;

                var point = ConvertOffsetToPoint(newCursor);
                if (point.Y < 0)
                {
                    RL.Ding();
                    return;
                }

                if (point.Y == _console.BufferHeight)
                {
                    // The cursor top exceeds the buffer height, so adjust the initial cursor
                    // position and the to-be-set cursor position for scrolling up the buffer.
                    InitialY -= 1;
                    point.Y -= 1;

                    // Insure the cursor is on the last line of the buffer prior
                    // to issuing a newline to scroll the buffer.
                    _console.SetCursorPosition(point.X, point.Y);

                    // Scroll up the buffer by 1 line.
                    _console.Write("\n");
                }
                else
                {
                    _console.SetCursorPosition(point.X, point.Y);
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

        private string GetTokenColor(Token token)
        {
            if ((token.TokenFlags & TokenFlags.CommandName) != 0) return _rl.Options._commandColor;

            switch (token.Kind)
            {
                case TokenKind.Comment:
                    return _rl.Options._commentColor;

                case TokenKind.Parameter:
                case TokenKind.Generic when token is StringLiteralToken slt && slt.Text.StartsWith("--"):
                    return _rl.Options._parameterColor;

                case TokenKind.Variable:
                case TokenKind.SplattedVariable:
                    return _rl.Options._variableColor;

                case TokenKind.StringExpandable:
                case TokenKind.StringLiteral:
                case TokenKind.HereStringExpandable:
                case TokenKind.HereStringLiteral:
                    return _rl.Options._stringColor;

                case TokenKind.Number:
                    return _rl.Options._numberColor;
            }

            if ((token.TokenFlags & TokenFlags.Keyword) != 0) return _rl.Options._keywordColor;

            if (token.Kind != TokenKind.Generic && (token.TokenFlags &
                                                    (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator |
                                                     TokenFlags.AssignmentOperator)) != 0)
                return _rl.Options._operatorColor;

            if ((token.TokenFlags & TokenFlags.TypeName) != 0) return _rl.Options._typeColor;

            if ((token.TokenFlags & TokenFlags.MemberName) != 0) return _rl.Options._memberColor;

            return _rl.Options._defaultTokenColor;
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

        private int GenerateRender(string defaultColor)
        {
            var text = _rl.buffer.ToString();
            _rl._Prediction.QueryForSuggestion(text);

            var color = defaultColor;
            var activeColor = string.Empty;
            var afterLastToken = false;
            var currentLogicalLine = 0;
            var inSelectedRegion = false;

            void UpdateColorsIfNecessary(string newColor)
            {
                if (!ReferenceEquals(newColor, activeColor))
                {
                    if (!inSelectedRegion)
                        ConsoleBufferLines[currentLogicalLine]
                            .Append(VTColorUtils.AnsiReset)
                            .Append(newColor);

                    activeColor = newColor;
                }
            }

            void RenderOneChar(char charToRender, bool toEmphasize)
            {
                if (charToRender == '\n')
                {
                    if (inSelectedRegion)
                        // Turn off inverse before end of line, turn on after continuation prompt
                        ConsoleBufferLines[currentLogicalLine].Append(VTColorUtils.AnsiReset);

                    currentLogicalLine += 1;
                    if (currentLogicalLine == ConsoleBufferLines.Count)
                        ConsoleBufferLines.Add(new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth));

                    // Reset the color for continuation prompt so the color sequence will always be explicitly
                    // specified for continuation prompt in the generated render strings.
                    // This is necessary because we will likely not rewrite all texts during rendering, and thus
                    // we cannot assume the continuation prompt can continue to use the active color setting from
                    // the previous rendering string.
                    activeColor = string.Empty;

                    if (_rl.Options.ContinuationPrompt.Length > 0)
                    {
                        UpdateColorsIfNecessary(_rl.Options._continuationPromptColor);
                        ConsoleBufferLines[currentLogicalLine].Append(_rl.Options.ContinuationPrompt);
                    }

                    if (inSelectedRegion)
                        // Turn off inverse before end of line, turn on after continuation prompt
                        ConsoleBufferLines[currentLogicalLine].Append(_rl.Options.SelectionColor);

                    return;
                }

                UpdateColorsIfNecessary(toEmphasize ? _rl.Options._emphasisColor : color);

                if (char.IsControl(charToRender))
                {
                    ConsoleBufferLines[currentLogicalLine].Append('^');
                    ConsoleBufferLines[currentLogicalLine].Append((char) ('@' + charToRender));
                }
                else
                {
                    ConsoleBufferLines[currentLogicalLine].Append(charToRender);
                }
            }

            foreach (var buf in ConsoleBufferLines) buf.Clear();

            var tokenStack = new Stack<SavedTokenState>();
            tokenStack.Push(new SavedTokenState
            {
                Tokens = _rl.Tokens,
                Index = 0,
                Color = defaultColor
            });

            var selectionStart = -1;
            var selectionEnd = -1;
            if (_rl._visualSelectionCommandCount > 0)
            {
                GetRegion(out var regionStart, out var regionLength);
                if (regionLength > 0)
                {
                    selectionStart = regionStart;
                    selectionEnd = selectionStart + regionLength;
                }
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (i == selectionStart)
                {
                    ConsoleBufferLines[currentLogicalLine].Append(_rl.Options.SelectionColor);
                    inSelectedRegion = true;
                }
                else if (i == selectionEnd)
                {
                    ConsoleBufferLines[currentLogicalLine].Append(VTColorUtils.AnsiReset);
                    ConsoleBufferLines[currentLogicalLine].Append(activeColor);
                    inSelectedRegion = false;
                }

                if (!afterLastToken)
                {
                    // Figure out the color of the character - if it's in a token,
                    // use the tokens color otherwise use the initial color.
                    var state = tokenStack.Peek();
                    var token = state.Tokens[state.Index];
                    while (i == token.Extent.EndOffset)
                    {
                        if (state.Index == state.Tokens.Length - 1)
                        {
                            tokenStack.Pop();
                            if (tokenStack.Count == 0)
                            {
                                afterLastToken = true;
                                token = null;
                                color = defaultColor;
                                break;
                            }

                            state = tokenStack.Peek();

                            // It's possible that a 'StringExpandableToken' is the last available token, for example:
                            //   'begin $a\abc def', 'process $a\abc | blah' and 'end $a\abc; hello'
                            // due to the special handling of the keywords 'begin', 'process' and 'end', all the above 3 script inputs
                            // generate only 2 tokens by the parser -- A KeywordToken, and a StringExpandableToken '$a\abc'. Text after
                            // '$a\abc' is not tokenized at all.
                            // We repeat the test to see if we fall into this case ('token' is the final one in the stack).
                            continue;
                        }

                        color = state.Color;
                        token = state.Tokens[++state.Index];
                    }

                    if (!afterLastToken && i == token.Extent.StartOffset)
                    {
                        color = GetTokenColor(token);

                        if (token is StringExpandableToken stringToken)
                            // We might have nested tokens.
                            if (stringToken.NestedTokens != null && stringToken.NestedTokens.Any())
                            {
                                var tokens = new Token[stringToken.NestedTokens.Count + 1];
                                stringToken.NestedTokens.CopyTo(tokens, 0);
                                // NestedTokens doesn't have an "EOS" token, so we use
                                // the string literal token for that purpose.
                                tokens[tokens.Length - 1] = stringToken;

                                tokenStack.Push(new SavedTokenState
                                {
                                    Tokens = tokens,
                                    Index = 0,
                                    Color = color
                                });

                                if (i == tokens[0].Extent.StartOffset) color = GetTokenColor(tokens[0]);
                            }
                    }
                }

                var charToRender = text[i];
                var toEmphasize = i >= EmphasisStart && i < EmphasisStart + EmphasisLength;

                RenderOneChar(charToRender, toEmphasize);
            }

            if (inSelectedRegion)
            {
                ConsoleBufferLines[currentLogicalLine].Append(VTColorUtils.AnsiReset);
                inSelectedRegion = false;
            }

            _rl._Prediction.ActiveView.RenderSuggestion(ConsoleBufferLines, ref currentLogicalLine);
            activeColor = string.Empty;

            if (_rl._statusLinePrompt != null)
            {
                currentLogicalLine += 1;
                if (currentLogicalLine > ConsoleBufferLines.Count - 1)
                    ConsoleBufferLines.Add(new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth));

                color = _rl._statusIsErrorMessage ? _rl.Options._errorColor : defaultColor;
                UpdateColorsIfNecessary(color);

                foreach (var c in _rl._statusLinePrompt) ConsoleBufferLines[currentLogicalLine].Append(c);

                ConsoleBufferLines[currentLogicalLine].Append(_rl._statusBuffer);
            }

            return currentLogicalLine + 1;
        }

        internal void ForceRender()
        {
            var defaultColor = VTColorUtils.DefaultColor;

            // Generate a sequence of logical lines with escape sequences for coloring.
            var logicalLineCount = GenerateRender(defaultColor);

            // Now write that out (and remember what we did so we can clear previous renders
            // and minimize writing more than necessary on the next render.)

            var renderLines = new RenderedLineData[logicalLineCount];
            var renderData = new RenderData {lines = renderLines};
            for (var i = 0; i < logicalLineCount; i++)
            {
                var line = ConsoleBufferLines[i].ToString();
                renderLines[i].line = line;
                renderLines[i].columns = LengthInBufferCells(line);
            }

            // And then do the real work of writing to the screen.
            // Rendering data is in reused
            ReallyRender(renderData, defaultColor);

            // Cleanup some excess buffers, saving a few because we know we'll use them.
            var bufferCount = ConsoleBufferLines.Count;
            var excessBuffers = bufferCount - renderLines.Length;
            if (excessBuffers > 5) ConsoleBufferLines.RemoveRange(renderLines.Length, excessBuffers);
        }

        internal int ConvertLineAndColumnToOffset(Point point)
        {
            int offset;
            var x = InitialX;
            var y = InitialY;

            var bufferWidth = _console.BufferWidth;
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
            var bufferWidth = _console.BufferWidth;
            var bufferHeight = _console.BufferHeight;

            void UpdateColorsIfNecessary(string newColor)
            {
                if (!ReferenceEquals(newColor, activeColor))
                {
                    _console.Write(newColor);
                    activeColor = newColor;
                }
            }

            // In case the buffer was resized
            RecomputeInitialCoords();
            renderData.bufferWidth = bufferWidth;
            renderData.bufferHeight = bufferHeight;

            // Make cursor invisible while we're rendering.
            _console.CursorVisible = false;

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
                if (logicalLine != logicalLineStartIndex) _console.Write("\n");

                var lineData = renderLines[logicalLine];
                _console.Write(lineData.line);

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
                    _console.Write(RL.Spaces(lenToClear));
                }
            }

            UpdateColorsIfNecessary(defaultColor);

            // The last logical line is shorter than our previous render? Clear them.
            for (var currentLines = physicalLine; currentLines < previousPhysicalLine;)
            {
                _console.SetCursorPosition(0, InitialY + currentLines);

                currentLines++;
                var lenToClear = currentLines == previousPhysicalLine ? lenPrevLastLine : bufferWidth;
                if (lenToClear > 0) _console.Write(RL.Spaces(lenToClear));
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

                    _console.Write("\n");

                // No need to write new line if all we need is to clear the extra previous render.
                _console.Write(RL.Spaces(previousRenderLines[line].columns));
            }

            // Preserve the current render data.
            PreviousRender = renderData;

            // If we counted pseudo physical lines, deduct them to get the real physical line counts
            // before updating '_initialY'.
            physicalLine -= pseudoPhysicalLineOffset;

            // Reset the colors after we've finished all our rendering.
            _console.Write(VTColorUtils.AnsiReset);

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
                _console.Write("\n");

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

            _console.SetCursorPosition(point.X, point.Y);
            _console.CursorVisible = true;

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
            var bufferWidth = _console.BufferWidth;

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
            _console.SetCursorPosition(InitialX, InitialY);

            var promptText =
                renderData.errorPrompt && _rl.Options.PromptText.Length == 2
                    ? _rl.Options.PromptText[1]
                    : _rl.Options.PromptText[0];

            // promptBufferCells is the number of visible characters in the prompt
            var promptBufferCells = LengthInBufferCells(promptText);
            var renderErrorPrompt = false;
            var bufferWidth = _console.BufferWidth;

            if (_console.CursorLeft >= promptBufferCells)
            {
                renderErrorPrompt = true;
                _console.CursorLeft -= promptBufferCells;
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

                var diffs = promptBufferCells - _console.CursorLeft;
                var newX = bufferWidth - diffs % bufferWidth;
                var newY = InitialY - diffs / bufferWidth - 1;

                // newY could be less than 0 if 'PromptText' is manually set to be a long string.
                if (newY >= 0)
                {
                    renderErrorPrompt = true;
                    _console.SetCursorPosition(newX, newY);
                }
            }

            if (renderErrorPrompt)
            {
                if (!promptText.Contains("\x1b"))
                {
                    var color = renderData.errorPrompt ? _rl.Options._errorColor : defaultColor;
                    _console.Write(color);
                    _console.Write(promptText);
                    _console.Write(VTColorUtils.AnsiReset);
                }
                else
                {
                    _console.Write(promptText);
                }
            }

            return true;
        }

        internal void CalculateWhereAndWhatToRender(bool cursorMovedToInitialPos, RenderData renderData,
            out LineInfoForRendering lineInfoForRendering)
        {
            var bufferWidth = _console.BufferWidth;
            var bufferHeight = _console.BufferHeight;

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
                            _console.SetCursorPosition(bufferWidth - 1, bufferHeight - 1);
                            _console.Write("\n");
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

                        _console.SetCursorPosition(0, newTop);
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
                    _console.Write("\x1b[2J");
                    _console.SetCursorPosition(0, _console.WindowTop);

                    var newPrompt = RL.Prompt;
                    if (!string.IsNullOrEmpty(newPrompt)) _console.Write(newPrompt);

                    InitialX = _console.CursorLeft;
                    InitialY = _console.CursorTop;
                    PreviousRender = InitialPrevRender;
                }
                else
                {
                    _console.SetCursorPosition(InitialX, InitialY);
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
            if (PreviousRender.bufferWidth != _console.BufferWidth
                || PreviousRender.bufferHeight != _console.BufferHeight)
            {
                // If the buffer width changed, our initial coordinates
                // may have as well.
                // Recompute X from the buffer width:
                InitialX = InitialX % _console.BufferWidth;

                // Recompute Y from the cursor
                InitialY = 0;
                var pt = ConvertOffsetToPoint(Current);
                InitialY = _console.CursorTop - pt.Y;
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

            var bufferWidth = _console.BufferWidth;
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
            var console = _s._console;
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
            var console = _s._console;
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
            var console = _s._console;
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
            var console = _s._console;
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
            _s._console.SetWindowPosition(0, 0);
        }

        /// <summary>
        ///     Scroll the display to the cursor.
        /// </summary>
        public static void ScrollDisplayToCursor(ConsoleKeyInfo? key = null, object arg = null)
        {
            // Ideally, we'll put the last input line at the bottom of the window
            var offset = _rl.buffer.Length;
            var point = _s.ConvertOffsetToPoint(offset);

            var console = _s._console;
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
            internal bool errorPrompt;
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