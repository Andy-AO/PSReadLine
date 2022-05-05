/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.PowerShell.PSReadLine;

namespace Microsoft.PowerShell
{
    /// <summary>
    ///     FNV-1a hashing algorithm: http://www.isthe.com/chongo/tech/comp/fnv/#FNV-1a
    /// </summary>
    internal class FNV1a32Hash
    {
        // FNV-1a algorithm parameters: http://www.isthe.com/chongo/tech/comp/fnv/#FNV-param
        private const uint FNV32_PRIME = 16777619;
        private const uint FNV32_OFFSETBASIS = 2166136261;

        internal static uint ComputeHash(string input)
        {
            char ch;
            uint hash = FNV32_OFFSETBASIS, lowByte, highByte;

            for (var i = 0; i < input.Length; i++)
            {
                ch = input[i];
                lowByte = (uint) (ch & 0x00FF);
                hash = unchecked((hash ^ lowByte) * FNV32_PRIME);

                highByte = (uint) (ch >> 8);
                hash = unchecked((hash ^ highByte) * FNV32_PRIME);
            }

            return hash;
        }
    }

    public partial class PSConsoleReadLine
    {
        private static History _hs = History.Singleton;



        public static bool IsOnLeftSideOfAnAssignment(Ast ast, out Ast rhs)
        {
            var result = false;
            rhs = null;

            do
            {
                if (ast.Parent is AssignmentStatementAst assignment)
                {
                    rhs = assignment.Right;
                    result = ReferenceEquals(assignment.Left, ast);

                    break;
                }

                ast = ast.Parent;
            } while (ast.Parent is not null);

            return result;
        }

        public static bool IsSecretMgmtCommand(StringConstantExpressionAst strConst, out CommandAst command)
        {
            var result = false;
            command = strConst.Parent as CommandAst;

            if (command is not null)
                result = ReferenceEquals(command.CommandElements[0], strConst)
                         && History.SecretMgmtCommands.Contains(strConst.Value);

            return result;
        }

        public static ExpressionAst GetArgumentForParameter(CommandParameterAst param)
        {
            if (param.Argument is not null) return param.Argument;

            var command = (CommandAst) param.Parent;
            var index = 1;
            for (; index < command.CommandElements.Count; index++)
                if (ReferenceEquals(command.CommandElements[index], param))
                    break;

            var argIndex = index + 1;
            if (argIndex < command.CommandElements.Count
                && command.CommandElements[argIndex] is ExpressionAst arg)
                return arg;

            return null;
        }

        /// <summary>
        ///     Add a command to the history - typically used to restore
        ///     history from a previous session.
        /// </summary>
        public static void AddToHistory(string command)
        {
            command = command.Replace("\r\n", "\n");
            var editItems = new List<EditItem> {EditItemInsertString.Create(command, 0)};
            _hs.MaybeAddToHistory(command, editItems, 1, false, false);
        }

        /// <summary>
        ///     Clears history in PSReadLine.  This does not affect PowerShell history.
        /// </summary>
        public static void ClearHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _hs.Historys?.Clear();
            _hs.RecentHistory?.Clear();
            _hs.CurrentHistoryIndex = 0;
        }

        /// <summary>
        ///     Return a collection of history items.
        /// </summary>
        public static HistoryItem[] GetHistoryItems()
        {
            return _hs.Historys.ToArray();
        }

        public void UpdateFromHistory(History.HistoryMoveCursor moveCursor)
        {
            string line;
            if (_hs.CurrentHistoryIndex == _hs.Historys.Count)
            {
                line = _hs._savedCurrentLine.CommandLine;
                _edits = new List<EditItem>(_hs._savedCurrentLine._edits);
                _undoEditIndex = _hs._savedCurrentLine._undoEditIndex;
                _editGroupStart = _hs._savedCurrentLine._editGroupStart;
            }
            else
            {
                line = _hs.Historys[_hs.CurrentHistoryIndex].CommandLine;
                _edits = new List<EditItem>(_hs.Historys[_hs.CurrentHistoryIndex]._edits);
                _undoEditIndex = _hs.Historys[_hs.CurrentHistoryIndex]._undoEditIndex;
                _editGroupStart = _hs.Historys[_hs.CurrentHistoryIndex]._editGroupStart;
            }

            buffer.Clear();
            buffer.Append(line);

            switch (moveCursor)
            {
                case History.HistoryMoveCursor.ToEnd:
                    _renderer.Current = Math.Max(0, buffer.Length + ViEndOfLineFactor);
                    break;
                case History.HistoryMoveCursor.ToBeginning:
                    _renderer.Current = 0;
                    break;
                default:
                    if (_renderer.Current > buffer.Length)
                        _renderer.Current = Math.Max(0, buffer.Length + ViEndOfLineFactor);
                    break;
            }

            using var _ = _Prediction.DisableScoped();
            _renderer.Render();
        }

        private void HistoryRecall(int direction)
        {
            if (_hs.RecallHistoryCommandCount == 0 && _renderer.LineIsMultiLine())
            {
                MoveToLine(direction);
                return;
            }

            if (Options.HistoryNoDuplicates && _hs.RecallHistoryCommandCount == 0)
                _hs.HashedHistory = new Dictionary<string, int>();

            var count = Math.Abs(direction);
            direction = direction < 0 ? -1 : +1;
            var newHistoryIndex = _hs.CurrentHistoryIndex;
            while (count > 0)
            {
                newHistoryIndex += direction;
                if (newHistoryIndex < 0 || newHistoryIndex >= _hs.Historys.Count) break;

                if (_hs.Historys[newHistoryIndex].FromOtherSession) continue;

                if (Options.HistoryNoDuplicates)
                {
                    var line = _hs.Historys[newHistoryIndex].CommandLine;
                    if (!_hs.HashedHistory.TryGetValue(line, out var index))
                    {
                        _hs.HashedHistory.Add(line, newHistoryIndex);
                        --count;
                    }
                    else if (newHistoryIndex == index)
                    {
                        --count;
                    }
                }
                else
                {
                    --count;
                }
            }

            _hs.RecallHistoryCommandCount = _hs.RecallHistoryCommandCount + 1;
            if (newHistoryIndex >= 0 && newHistoryIndex <= _hs.Historys.Count)
            {
                _hs.CurrentHistoryIndex = newHistoryIndex;
                var moveCursor = InViCommandMode() && !Options.HistorySearchCursorMovesToEnd
                    ? History.HistoryMoveCursor.ToBeginning
                    : History.HistoryMoveCursor.ToEnd;
                UpdateFromHistory(moveCursor);
            }
        }

        /// <summary>
        ///     Replace the current input with the 'previous' item from PSReadLine history.
        /// </summary>
        public static void PreviousHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, -1);
            if (numericArg > 0) numericArg = -numericArg;

            if (UpdateListSelection(numericArg)) return;

            _hs.SaveCurrentLine();
            Singleton.HistoryRecall(numericArg);
        }

        /// <summary>
        ///     Replace the current input with the 'next' item from PSReadLine history.
        /// </summary>
        public static void NextHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, +1);
            if (UpdateListSelection(numericArg)) return;

            _hs.SaveCurrentLine();
            Singleton.HistoryRecall(numericArg);
        }

        public void HistorySearch(int direction)
        {
            if (_hs.SearchHistoryCommandCount == 0)
            {
                if (_renderer.LineIsMultiLine())
                {
                    MoveToLine(direction);
                    return;
                }

                _hs.SearchHistoryPrefix = buffer.ToString(0, _renderer.Current);
                _renderer.EmphasisStart = 0;
                _renderer.EmphasisLength = _renderer.Current;
                if (Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
            }

            _hs.SearchHistoryCommandCount += 1;

            var count = Math.Abs(direction);
            direction = direction < 0 ? -1 : +1;
            var newHistoryIndex = _hs.CurrentHistoryIndex;
            while (count > 0)
            {
                newHistoryIndex += direction;
                if (newHistoryIndex < 0 || newHistoryIndex >= _hs.Historys.Count) break;

                if (_hs.Historys[newHistoryIndex].FromOtherSession && _hs.SearchHistoryPrefix.Length == 0) continue;

                var line = _hs.Historys[newHistoryIndex].CommandLine;
                if (line.StartsWith(_hs.SearchHistoryPrefix, Options.HistoryStringComparison))
                {
                    if (Options.HistoryNoDuplicates)
                    {
                        if (!_hs.HashedHistory.TryGetValue(line, out var index))
                        {
                            _hs.HashedHistory.Add(line, newHistoryIndex);
                            --count;
                        }
                        else if (index == newHistoryIndex)
                        {
                            --count;
                        }
                    }
                    else
                    {
                        --count;
                    }
                }
            }

            if (newHistoryIndex >= 0 && newHistoryIndex <= _hs.Historys.Count)
            {
                // Set '_current' back to where it was when starting the first search, because
                // it might be changed during the rendering of the last matching history command.
                _renderer.Current = _renderer.EmphasisLength;
                _hs.CurrentHistoryIndex = newHistoryIndex;
                var moveCursor = InViCommandMode()
                    ? History.HistoryMoveCursor.ToBeginning
                    : Options.HistorySearchCursorMovesToEnd
                        ? History.HistoryMoveCursor.ToEnd
                        : History.HistoryMoveCursor.DontMove;
                UpdateFromHistory(moveCursor);
            }
        }

        /// <summary>
        ///     Move to the first item in the history.
        /// </summary>
        public static void BeginningOfHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _hs.SaveCurrentLine();
            _hs.CurrentHistoryIndex = 0;
            Singleton.UpdateFromHistory(History.HistoryMoveCursor.ToEnd);
        }

        /// <summary>
        ///     Move to the last item (the current input) in the history.
        /// </summary>
        public static void EndOfHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _hs.SaveCurrentLine();
            History.GoToEndOfHistory();
        }

        /// <summary>
        ///     Replace the current input with the 'previous' item from PSReadLine history
        ///     that matches the characters between the start and the input and the cursor.
        /// </summary>
        public static void HistorySearchBackward(ConsoleKeyInfo? key = null, object arg = null)
        {
            TryGetArgAsInt(arg, out var numericArg, -1);
            if (numericArg > 0) numericArg = -numericArg;

            _hs.SaveCurrentLine();
            Singleton.HistorySearch(numericArg);
        }
       /// <summary>
        ///     Perform an incremental backward search through history.
        /// </summary>
        public static void ReverseSearchHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _hs.InteractiveHistorySearch(-1);
        }
    }
}