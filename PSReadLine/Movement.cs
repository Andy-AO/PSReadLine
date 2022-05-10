﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Diagnostics;
using System.Management.Automation.Language;
using Microsoft.PowerShell.Internal;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private int _moveToEndOfLineCommandCount;
        private int _moveToLineCommandCount;
        private int _moveToLineDesiredColumn;

        /// <summary>
        ///     If the input has multiple lines, move to the end of the current line,
        ///     or if already at the end of the line, move to the end of the input.
        ///     If the input has a single line, move to the end of the input.
        /// </summary>
        public static void EndOfLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            var i = _renderer.Current;
            for (; i < Singleton.buffer.Length; i++)
                if (Singleton.buffer[i] == '\n')
                    break;

            _renderer.MoveCursor(i == _renderer.Current ? Singleton.buffer.Length : i);
        }

        /// <summary>
        ///     If the input has multiple lines, move to the start of the current line,
        ///     or if already at the start of the line, move to the start of the input.
        ///     If the input has a single line, move to the start of the input.
        /// </summary>
        public static void BeginningOfLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            var newCurrent = GetBeginningOfLinePos(_renderer.Current);
            newCurrent = newCurrent == _renderer.Current ? 0 : newCurrent;

            _renderer.MoveCursor(newCurrent);
        }

        /// <summary>
        ///     Move the cursor one character to the right.  This may move the cursor to the next
        ///     line of multi-line input.
        /// </summary>
        public static void ForwardChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (TryGetArgAsInt(arg, out var numericArg, 1))
            {
                if (_renderer.Current == Singleton.buffer.Length && numericArg > 0)
                    AcceptSuggestion(key, arg);
                else
                    SetCursorPosition(_renderer.Current + numericArg);
            }
        }

        /// <summary>
        ///     Move the cursor one character to the left.  This may move the cursor to the previous
        ///     line of multi-line input.
        /// </summary>
        public static void BackwardChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (TryGetArgAsInt(arg, out var numericArg, 1)) SetCursorPosition(_renderer.Current - numericArg);
        }

        /// <summary>
        ///     Moves the cursor one character to the right on a single logical line.
        /// </summary>
        public static void ViForwardChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (TryGetArgAsInt(arg, out var numericArg, 1))
            {
                if (InViInsertMode() && _renderer.Current == Singleton.buffer.Length && numericArg > 0)
                    AcceptSuggestion(key, arg);
                else
                    ViOffsetCursorPosition(+numericArg);
            }
        }

        /// <summary>
        ///     Move the cursor one character to the left on a single logical line.
        /// </summary>
        public static void ViBackwardChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (TryGetArgAsInt(arg, out var numericArg, 1)) ViOffsetCursorPosition(-numericArg);
        }

        /// <summary>
        ///     Moves the cursor to the left or right a certain number of characters.
        ///     If the count is negative, moves the cursor in the left direction.
        /// </summary>
        private static void ViOffsetCursorPosition(int count)
        {
            if (count < 0)
            {
                var start = GetBeginningOfLinePos(_renderer.Current);
                var newCurrent = Math.Max(start, _renderer.Current + count);
                if (_renderer.Current != newCurrent) _renderer.MoveCursor(newCurrent);
            }
            else if (_renderer.Current < Singleton.buffer.Length)
            {
                // when in the VI command mode, 'end' is the position of the last character;
                // when in the VI insert mode, 'end' is 1 char beyond the last character.
                var end = GetEndOfLogicalLinePos(_renderer.Current) + 1 + ViEndOfLineFactor;
                var newCurrent = Math.Min(end, _renderer.Current + count);
                if (_renderer.Current != newCurrent) _renderer.MoveCursor(newCurrent);
            }
        }

        public void MoveToLine(int lineOffset)
        {
            if (InViCommandMode())
                ViMoveToLine(lineOffset);
            else
                MoveToLineImpl(lineOffset);
        }

        private void MoveToLineImpl(int lineOffset)
        {
            // Behavior description:
            //  - If the cursor is at the end of a logical line, then 'UpArrow' (or 'DownArrow') moves the cursor up (or down)
            //    'lineOffset' numbers of logical lines, and the cursor is always put at the end of the new logical line.
            //  - If the cursor is NOT at the end of a logical line, then 'UpArrow' (or 'DownArrow') moves the cursor up (or down)
            //    'lineOffset' numbers of physical lines, and the cursor is always placed at the same column as is now, or at the
            //    end of line if that physical line is shorter than the targeted column.

            const int endOfLine = int.MaxValue;

            Point? point = null;
            _moveToLineCommandCount += 1;

            if (_moveToLineCommandCount == 1)
            {
                point = _renderer.ConvertOffsetToPoint(_renderer.Current);
                _moveToLineDesiredColumn =
                    _renderer.Current == buffer.Length || buffer[_renderer.Current] == '\n'
                        ? endOfLine
                        : point.Value.X;
            }

            // Nothing needs to be done when:
            //  - actually not moving the line, or
            //  - moving the line down when it's at the end of the last line.
            if (lineOffset == 0 || lineOffset > 0 && _renderer.Current == buffer.Length) return;

            int newCurrent;
            if (_moveToLineDesiredColumn == endOfLine)
            {
                newCurrent = _renderer.Current;

                if (lineOffset > 0)
                {
                    // Moving to the end of a subsequent logical line.
                    for (var i = 0; i < lineOffset; i++)
                    {
                        for (newCurrent++; newCurrent < buffer.Length && buffer[newCurrent] != '\n'; newCurrent++) ;

                        if (newCurrent == buffer.Length) break;
                    }
                }
                else
                {
                    // Moving to the end of a previous logical line.
                    var lastEndOfLineIndex = _renderer.Current;
                    for (var i = 0; i < -lineOffset; i++)
                    {
                        for (newCurrent--; newCurrent >= 0 && buffer[newCurrent] != '\n'; newCurrent--) ;

                        if (newCurrent < 0)
                        {
                            newCurrent = lastEndOfLineIndex;
                            break;
                        }

                        lastEndOfLineIndex = newCurrent;
                    }
                }
            }
            else
            {
                point = point ?? _renderer.ConvertOffsetToPoint(_renderer.Current);
                var newY = point.Value.Y + lineOffset;

                var newPoint = new Point
                {
                    X = _moveToLineDesiredColumn,
                    Y = Math.Max(newY, _renderer.InitialY)
                };

                newCurrent = _renderer.ConvertLineAndColumnToOffset(newPoint);
            }

            if (newCurrent != -1) _renderer.MoveCursor(newCurrent);
        }

        /// <summary>
        ///     Move the cursor to the previous line.
        /// </summary>
        public static void PreviousLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (TryGetArgAsInt(arg, out var numericArg, 1)) Singleton.MoveToLine(-numericArg);
        }

        /// <summary>
        ///     Move the cursor to the next line.
        /// </summary>
        public static void NextLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (TryGetArgAsInt(arg, out var numericArg, 1)) Singleton.MoveToLine(numericArg);
        }

        /// <summary>
        ///     Move the cursor forward to the start of the next word.
        ///     Word boundaries are defined by a configurable set of characters.
        /// </summary>
        public static void NextWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!TryGetArgAsInt(arg, out var numericArg, 1)) return;

            if (numericArg < 0)
            {
                BackwardWord(key, -numericArg);
                return;
            }

            while (numericArg-- > 0)
                _renderer.MoveCursor(Singleton.FindNextWordPoint(Singleton.Options.WordDelimiters));
        }

        /// <summary>
        ///     Move the cursor forward to the end of the current word, or if between words,
        ///     to the end of the next word.  Word boundaries are defined by PowerShell tokens.
        /// </summary>
        public static void ShellNextWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!TryGetArgAsInt(arg, out var numericArg, 1)) return;

            if (numericArg < 0)
            {
                ShellBackwardWord(key, -numericArg);
                return;
            }

            while (numericArg-- > 0)
            {
                var token = Singleton.FindToken(_renderer.Current, FindTokenMode.Next);

                Debug.Assert(token != null, "We'll always find EOF");

                _renderer.MoveCursor(token.Kind == TokenKind.EndOfInput
                    ? Singleton.buffer.Length
                    : token.Extent.StartOffset);
            }
        }

        /// <summary>
        ///     Move the cursor forward to the end of the current word, or if between words,
        ///     to the end of the next word.  Word boundaries are defined by a configurable
        ///     set of characters.
        /// </summary>
        public static void ForwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!TryGetArgAsInt(arg, out var numericArg, 1)) return;

            if (_renderer.Current == Singleton.buffer.Length && numericArg > 0)
            {
                AcceptNextSuggestionWord(numericArg);
                return;
            }

            if (numericArg < 0)
            {
                BackwardWord(key, -numericArg);
                return;
            }

            while (numericArg-- > 0)
                _renderer.MoveCursor(Singleton.FindForwardWordPoint(Singleton.Options.WordDelimiters));
        }

        /// <summary>
        ///     Move the cursor forward to the start of the next word.
        ///     Word boundaries are defined by PowerShell tokens.
        /// </summary>
        public static void ShellForwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!TryGetArgAsInt(arg, out var numericArg, 1)) return;

            if (numericArg < 0)
            {
                ShellBackwardWord(key, -numericArg);
                return;
            }

            while (numericArg-- > 0)
            {
                var token = Singleton.FindToken(_renderer.Current, FindTokenMode.CurrentOrNext);

                Debug.Assert(token != null, "We'll always find EOF");

                _renderer.MoveCursor(token.Kind == TokenKind.EndOfInput
                    ? Singleton.buffer.Length
                    : token.Extent.EndOffset);
            }
        }

        private static bool CheckIsBound(Action<ConsoleKeyInfo?, object> action)
        {
            foreach (var entry in Singleton._dispatchTable)
                if (entry.Value.Action == action)
                    return true;
            return false;
        }

        /// <summary>
        ///     Move the cursor back to the start of the current word, or if between words,
        ///     the start of the previous word.  Word boundaries are defined by a configurable
        ///     set of characters.
        /// </summary>
        public static void BackwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!TryGetArgAsInt(arg, out var numericArg, 1)) return;

            if (numericArg < 0)
            {
                if (CheckIsBound(ForwardWord))
                    ForwardWord(key, -numericArg);
                else
                    NextWord(key, -numericArg);
                return;
            }

            while (numericArg-- > 0)
                _renderer.MoveCursor(Singleton.FindBackwardWordPoint(Singleton.Options.WordDelimiters));
        }

        /// <summary>
        ///     Move the cursor back to the start of the current word, or if between words,
        ///     the start of the previous word.  Word boundaries are defined by PowerShell tokens.
        /// </summary>
        public static void ShellBackwardWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!TryGetArgAsInt(arg, out var numericArg, 1)) return;

            if (numericArg < 0)
            {
                if (CheckIsBound(ShellForwardWord))
                    ShellForwardWord(key, -numericArg);
                else
                    ShellNextWord(key, -numericArg);
                return;
            }

            while (numericArg-- > 0)
            {
                var token = Singleton.FindToken(_renderer.Current, FindTokenMode.Previous);
                _renderer.MoveCursor(token?.Extent.StartOffset ?? 0);
            }
        }

        /// <summary>
        ///     Go to the matching brace, paren, or square bracket.
        /// </summary>
        public static void GotoBrace(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_renderer.Current >= Singleton.buffer.Length)
            {
                Ding();
                return;
            }

            Token token = null;
            var index = 0;
            for (; index < Singleton.Tokens.Length; index++)
            {
                var tempQualifier = Singleton;
                token = tempQualifier.Tokens[index];
                if (token.Extent.StartOffset == _renderer.Current)
                    break;
            }

            TokenKind toMatch;
            int direction;
            switch (token.Kind)
            {
                case TokenKind.LParen:
                    toMatch = TokenKind.RParen;
                    direction = 1;
                    break;
                case TokenKind.LCurly:
                    toMatch = TokenKind.RCurly;
                    direction = 1;
                    break;
                case TokenKind.LBracket:
                    toMatch = TokenKind.RBracket;
                    direction = 1;
                    break;

                case TokenKind.RParen:
                    toMatch = TokenKind.LParen;
                    direction = -1;
                    break;
                case TokenKind.RCurly:
                    toMatch = TokenKind.LCurly;
                    direction = -1;
                    break;
                case TokenKind.RBracket:
                    toMatch = TokenKind.LBracket;
                    direction = -1;
                    break;

                default:
                    // Nothing to match (don't match inside strings/comments)
                    Ding();
                    return;
            }

            var matchCount = 0;
            var limit = direction > 0 ? Singleton.Tokens.Length - 1 : -1;
            for (; index != limit; index += direction)
            {
                var t = Singleton.Tokens[index];
                if (t.Kind == token.Kind)
                {
                    matchCount++;
                }
                else if (t.Kind == toMatch)
                {
                    matchCount--;
                    if (matchCount == 0)
                    {
                        _renderer.MoveCursor(t.Extent.StartOffset);
                        return;
                    }
                }
            }

            Ding();
        }

        /// <summary>
        ///     Clear the screen and draw the current line at the top of the screen.
        /// </summary>
        public static void ClearScreen(ConsoleKeyInfo? key = null, object arg = null)
        {
            var console = Renderer.Console;
            console.Write("\x1b[2J");
            InvokePrompt(null, console.WindowTop);
        }

        // Try to convert the arg to a char, return 0 for failure
        private static char TryGetArgAsChar(object arg)
        {
            if (arg is char c) return c;

            if (arg is string s && s.Length == 1) return s[0];

            return '\0';
        }

        /// <summary>
        ///     Read a character and search forward for the next occurence of that character.
        ///     If an argument is specified, search forward (or backward if negative) for the
        ///     nth occurence.
        /// </summary>
        public static void CharacterSearch(ConsoleKeyInfo? key = null, object arg = null)
        {
            var occurence = arg as int? ?? 1;
            if (occurence < 0)
            {
                CharacterSearchBackward(key, -occurence);
                return;
            }

            var toFind = TryGetArgAsChar(arg);
            if (toFind == '\0')
                // Should we prompt?
                toFind = ReadKey().KeyChar;
            for (var i = _renderer.Current + 1; i < Singleton.buffer.Length; i++)
                if (Singleton.buffer[i] == toFind)
                {
                    occurence -= 1;
                    if (occurence == 0)
                    {
                        _renderer.MoveCursor(i);
                        break;
                    }
                }

            if (occurence > 0) Ding();
        }

        /// <summary>
        ///     Read a character and search backward for the next occurence of that character.
        ///     If an argument is specified, search backward (or forward if negative) for the
        ///     nth occurence.
        /// </summary>
        public static void CharacterSearchBackward(ConsoleKeyInfo? key = null, object arg = null)
        {
            var occurence = arg as int? ?? 1;
            if (occurence < 0)
            {
                CharacterSearch(key, -occurence);
                return;
            }

            var toFind = TryGetArgAsChar(arg);
            if (toFind == '\0')
                // Should we prompt?
                toFind = ReadKey().KeyChar;
            for (var i = _renderer.Current - 1; i >= 0; i--)
                if (Singleton.buffer[i] == toFind)
                {
                    occurence -= 1;
                    if (occurence == 0)
                    {
                        _renderer.MoveCursor(i);
                        return;
                    }
                }

            Ding();
        }
    }
}