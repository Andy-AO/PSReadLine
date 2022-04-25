/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.PowerShell.Internal;
using static Microsoft.PowerShell.Renderer;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private static readonly Renderer _renderer = Renderer.Singleton;

        private List<StringBuilder> ConsoleBufferLines => _renderer.ConsoleBufferLines;

        private static string[] _spaces => SpacesArr;

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

        private bool WaitingToRender
        {
            get => _renderer.WaitingToRender;
            set => _renderer.WaitingToRender = value;
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
            using var _ = _prediction.PauseQuery();
            Render();
        }

        private void Render()
        {
            // If there are a bunch of keys queued up, skip rendering if we've rendered
            // recently.
            if (_queuedKeys.Count > 10 && (_renderer._lastRenderTime.ElapsedMilliseconds < 50))
            {
                // We won't render, but most likely the tokens will be different, so make
                // sure we don't use old tokens, also allow garbage to get collected.
                WaitingToRender = true;
                return;
            }

            ForceRender();
        }

        private void ForceRender()
        {
            var defaultColor = VTColorUtils.DefaultColor;

            // Generate a sequence of logical lines with escape sequences for coloring.
            int logicalLineCount = GenerateRender(defaultColor);

            // Now write that out (and remember what we did so we can clear previous renders
            // and minimize writing more than necessary on the next render.)

            var renderLines = new RenderedLineData[logicalLineCount];
            var renderData = new RenderData {lines = renderLines};
            for (var i = 0; i < logicalLineCount; i++)
            {
                var line = ConsoleBufferLines[i].ToString();
                renderLines[i].line = line;
                renderLines[i].columns = _renderer.LengthInBufferCells(line);
            }

            // And then do the real work of writing to the screen.
            // Rendering data is in reused
            _renderer.ReallyRender(renderData, defaultColor);

            // Cleanup some excess buffers, saving a few because we know we'll use them.
            var bufferCount = ConsoleBufferLines.Count;
            var excessBuffers = bufferCount - renderLines.Length;
            if (excessBuffers > 5)
            {
                ConsoleBufferLines.RemoveRange(renderLines.Length, excessBuffers);
            }
        }

        private int GenerateRender(string defaultColor)
        {
            var text = buffer.ToString();
            _prediction.QueryForSuggestion(text);

            string color = defaultColor;
            string activeColor = string.Empty;
            bool afterLastToken = false;
            int currentLogicalLine = 0;
            bool inSelectedRegion = false;

            void UpdateColorsIfNecessary(string newColor)
            {
                if (!ReferenceEquals(newColor, activeColor))
                {
                    if (!inSelectedRegion)
                    {
                        ConsoleBufferLines[currentLogicalLine]
                            .Append(VTColorUtils.AnsiReset)
                            .Append(newColor);
                    }

                    activeColor = newColor;
                }
            }

            void RenderOneChar(char charToRender, bool toEmphasize)
            {
                if (charToRender == '\n')
                {
                    if (inSelectedRegion)
                    {
                        // Turn off inverse before end of line, turn on after continuation prompt
                        ConsoleBufferLines[currentLogicalLine].Append(VTColorUtils.AnsiReset);
                    }

                    currentLogicalLine += 1;
                    if (currentLogicalLine == ConsoleBufferLines.Count)
                    {
                        ConsoleBufferLines.Add(new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth));
                    }

                    // Reset the color for continuation prompt so the color sequence will always be explicitly
                    // specified for continuation prompt in the generated render strings.
                    // This is necessary because we will likely not rewrite all texts during rendering, and thus
                    // we cannot assume the continuation prompt can continue to use the active color setting from
                    // the previous rendering string.
                    activeColor = string.Empty;

                    if (Options.ContinuationPrompt.Length > 0)
                    {
                        UpdateColorsIfNecessary(Options._continuationPromptColor);
                        ConsoleBufferLines[currentLogicalLine].Append(Options.ContinuationPrompt);
                    }

                    if (inSelectedRegion)
                    {
                        // Turn off inverse before end of line, turn on after continuation prompt
                        ConsoleBufferLines[currentLogicalLine].Append(Options.SelectionColor);
                    }

                    return;
                }

                UpdateColorsIfNecessary(toEmphasize ? _options._emphasisColor : color);

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

            foreach (var buf in ConsoleBufferLines)
            {
                buf.Clear();
            }

            var tokenStack = new Stack<SavedTokenState>();
            tokenStack.Push(new SavedTokenState
            {
                Tokens = Tokens,
                Index = 0,
                Color = defaultColor
            });

            int selectionStart = -1;
            int selectionEnd = -1;
            if (_visualSelectionCommandCount > 0)
            {
                GetRegion(out int regionStart, out int regionLength);
                if (regionLength > 0)
                {
                    selectionStart = regionStart;
                    selectionEnd = selectionStart + regionLength;
                }
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (i == selectionStart)
                {
                    ConsoleBufferLines[currentLogicalLine].Append(Options.SelectionColor);
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
                            else
                            {
                                state = tokenStack.Peek();

                                // It's possible that a 'StringExpandableToken' is the last available token, for example:
                                //   'begin $a\abc def', 'process $a\abc | blah' and 'end $a\abc; hello'
                                // due to the special handling of the keywords 'begin', 'process' and 'end', all the above 3 script inputs
                                // generate only 2 tokens by the parser -- A KeywordToken, and a StringExpandableToken '$a\abc'. Text after
                                // '$a\abc' is not tokenized at all.
                                // We repeat the test to see if we fall into this case ('token' is the final one in the stack).
                                continue;
                            }
                        }

                        color = state.Color;
                        token = state.Tokens[++state.Index];
                    }

                    if (!afterLastToken && i == token.Extent.StartOffset)
                    {
                        color = GetTokenColor(token);

                        if (token is StringExpandableToken stringToken)
                        {
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

                                if (i == tokens[0].Extent.StartOffset)
                                {
                                    color = GetTokenColor(tokens[0]);
                                }
                            }
                        }
                    }
                }

                var charToRender = text[i];
                var toEmphasize = i >= EmphasisStart && i < (EmphasisStart + EmphasisLength);

                RenderOneChar(charToRender, toEmphasize);
            }

            if (inSelectedRegion)
            {
                ConsoleBufferLines[currentLogicalLine].Append(VTColorUtils.AnsiReset);
                inSelectedRegion = false;
            }

            _prediction.ActiveView.RenderSuggestion(ConsoleBufferLines, ref currentLogicalLine);
            activeColor = string.Empty;

            if (_statusLinePrompt != null)
            {
                currentLogicalLine += 1;
                if (currentLogicalLine > ConsoleBufferLines.Count - 1)
                {
                    ConsoleBufferLines.Add(new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth));
                }

                color = _statusIsErrorMessage ? Options._errorColor : defaultColor;
                UpdateColorsIfNecessary(color);

                foreach (char c in _statusLinePrompt)
                {
                    ConsoleBufferLines[currentLogicalLine].Append(c);
                }

                ConsoleBufferLines[currentLogicalLine].Append(_statusBuffer);
            }

            return currentLogicalLine + 1;
        }

        private string GetTokenColor(Token token)
        {
            if ((token.TokenFlags & TokenFlags.CommandName) != 0)
            {
                return _options._commandColor;
            }

            switch (token.Kind)
            {
                case TokenKind.Comment:
                    return _options._commentColor;

                case TokenKind.Parameter:
                case TokenKind.Generic when token is StringLiteralToken slt && slt.Text.StartsWith("--"):
                    return _options._parameterColor;

                case TokenKind.Variable:
                case TokenKind.SplattedVariable:
                    return _options._variableColor;

                case TokenKind.StringExpandable:
                case TokenKind.StringLiteral:
                case TokenKind.HereStringExpandable:
                case TokenKind.HereStringLiteral:
                    return _options._stringColor;

                case TokenKind.Number:
                    return _options._numberColor;
            }

            if ((token.TokenFlags & TokenFlags.Keyword) != 0)
            {
                return _options._keywordColor;
            }

            if (token.Kind != TokenKind.Generic && (token.TokenFlags &
                                                    (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator |
                                                     TokenFlags.AssignmentOperator)) != 0)
            {
                return _options._operatorColor;
            }

            if ((token.TokenFlags & TokenFlags.TypeName) != 0)
            {
                return _options._typeColor;
            }

            if ((token.TokenFlags & TokenFlags.MemberName) != 0)
            {
                return _options._memberColor;
            }

            return _options._defaultTokenColor;
        }

        private void GetRegion(out int start, out int length)
        {
            if (_mark < Current)
            {
                start = _mark;
                length = Current - start;
            }
            else
            {
                start = Current;
                length = _mark - start;
            }
        }

        private void MoveCursor(int newCursor)
        {
            // Only update screen cursor if the buffer is fully rendered.
            if (!WaitingToRender)
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

        [ExcludeFromCodeCoverage]
        void IPSConsoleReadLineMockableMethods.Ding()
        {
            switch (Options.BellStyle)
            {
                case BellStyle.None:
                    break;
                case BellStyle.Audible:
                    if (Options.DingDuration > 0)
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            Console.Beep(Options.DingTone, Options.DingDuration);
                        }
                        else
                        {
                            Console.Beep();
                        }
                    }

                    break;
                case BellStyle.Visual:
                    // TODO: flash prompt? command line?
                    break;
            }
        }

        /// <summary>
        /// Notify the user based on their preference for notification.
        /// </summary>
        public static void Ding()
        {
            Singleton._mockableMethods.Ding();
        }

        private bool PromptYesOrNo(string s)
        {
            _statusLinePrompt = s;
            Render();

            var key = ReadKey();

            _statusLinePrompt = null;
            Render();
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