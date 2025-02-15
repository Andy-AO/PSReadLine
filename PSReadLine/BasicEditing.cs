﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using Microsoft.PowerShell.PSReadLine;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        /// <summary>
        /// Insert the key.
        /// </summary>
        public static void SelfInsert(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue)
            {
                return;
            }

            var keyChar = key.Value.KeyChar;
            if (keyChar == '\0')
                return;

            if (arg is int count)
            {
                if (count <= 0)
                    return;
            }
            else
            {
                count = 1;
            }

            if (_singleton._visualSelectionCommandCount > 0)
            {
                _singleton.GetRegion(out var start, out var length);
                Replace(start, length, new string(keyChar, count));
            }
            else if (count > 1)
            {
                Insert(new string(keyChar, count));
            }
            else
            {
                Insert(keyChar);
            }
        }

        /// <summary>
        /// Reverts all of the input to the current input.
        /// </summary>
        public static void RevertLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._prediction.RevertSuggestion())
            {
                return;
            }

            if (_singleton._statusIsErrorMessage)
            {
                // After an edit, clear the error message
                _singleton.ClearStatusMessage(render: false);
            }

            while (_singleton._undoEditIndex > 0)
            {
                _singleton._edits[_singleton._undoEditIndex - 1].Undo();
                _singleton._undoEditIndex--;
            }
            _singleton.Render();
        }

        /// <summary>
        /// Cancel the current input, leaving the input on the screen,
        /// but returns back to the host so the prompt is evaluated again.
        /// </summary>
        public static void CancelLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.ClearStatusMessage(false);
            _singleton._current = _singleton._buffer.Length;

            using var _ = _singleton._prediction.DisableScoped();
            _singleton.ForceRender();

            _singleton._console.Write("\x1b[91m^C\x1b[0m");

            _singleton._buffer.Clear(); // Clear so we don't actually run the input
            _singleton._current = 0; // If Render is called, _current must be correct.
            _singleton._currentHistoryIndex = _singleton._history.Count;
            _singleton._inputAccepted = true;
        }

        /// <summary>
        /// Like KillLine - deletes text from the point to the end of the input,
        /// but does not put the deleted text in the kill ring.
        /// </summary>
        public static void ForwardDeleteInput(ConsoleKeyInfo? key = null, object arg = null)
        {
            ForwardDeleteImpl(_singleton._buffer.Length, ForwardDeleteInput);
        }

        /// <summary>
        /// Deletes text from the point to the end of the current logical line,
        /// but does not put the deleted text in the kill ring.
        /// </summary>
        public static void ForwardDeleteLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            ForwardDeleteImpl(GetEndOfLogicalLinePos(_singleton._current) + 1, ForwardDeleteLine);
        }

        /// <summary>
        /// Deletes text from the cursor position to the specified end position
        /// but does not put the deleted text in the kill ring.
        /// </summary>
        /// <param name="endPosition">0-based offset to one character past the end of the text.</param>
        private static void ForwardDeleteImpl(int endPosition, Action<ConsoleKeyInfo?, object> instigator)
        {
            var current = _singleton._current;
            var buffer = _singleton._buffer;

            if (buffer.Length > 0 && current < endPosition)
            {
                int length = endPosition - current;
                var str = buffer.ToString(current, length);

                _singleton.SaveEditItem(
                    EditItemDelete.Create(
                        str,
                        current,
                        instigator,
                        instigatorArg: null,
                        !InViEditMode()));

                buffer.Remove(current, length);
                _singleton.Render();
            }
        }

        /// <summary>
        /// Like BackwardKillInput - deletes text from the point to the start of the input,
        /// but does not put the deleted text in the kill ring.
        public static void BackwardDeleteInput(ConsoleKeyInfo? key = null, object arg = null)
        {
            BackwardDeleteSubstring(0, BackwardDeleteInput);
        }

        /// <summary>
        /// Like BackwardKillLine - deletes text from the point to the start of the logical line,
        /// but does not put the deleted text in the kill ring.
        /// </summary>
        public static void BackwardDeleteLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            var position = GetBeginningOfLinePos(_singleton._current);
            BackwardDeleteSubstring(position, BackwardDeleteLine);
        }

        private static void BackwardDeleteSubstring(int position, Action<ConsoleKeyInfo?, object> instigator)
        {
            if (_singleton._current > position)
            {
                var count = _singleton._current - position;

                _singleton.RemoveTextToViRegister(position, count, instigator, arg: null, !InViEditMode());
                _singleton._current = position;
                _singleton.Render();
            }
        }

        /// <summary>
        /// Delete the character before the cursor.
        /// </summary>
        public static void BackwardDeleteChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton._visualSelectionCommandCount > 0)
            {
                _singleton.GetRegion(out var start, out var length);
                Delete(start, length);
                return;
            }

            if (_singleton._buffer.Length > 0 && _singleton._current > 0)
            {
                int qty = arg as int? ?? 1;
                if (qty < 1) return; // Ignore useless counts
                qty = Math.Min(qty, _singleton._current);

                int startDeleteIndex = _singleton._current - qty;

                _singleton.RemoveTextToViRegister(startDeleteIndex, qty, BackwardDeleteChar, arg, !InViEditMode());
                _singleton._current = startDeleteIndex;
                _singleton.Render();
            }
        }

        private void DeleteCharImpl(int qty, bool orExit)
        {
            if (_visualSelectionCommandCount > 0)
            {
                GetRegion(out var start, out var length);
                Delete(start, length);
                return;
            }

            if (_buffer.Length > 0)
            {
                if (_current < _buffer.Length)
                {
                    qty = Math.Min(qty, _singleton._buffer.Length - _singleton._current);

                    RemoveTextToViRegister(_current, qty, DeleteChar, qty, !InViEditMode());
                    if (_current >= _buffer.Length)
                    {
                        _current = Math.Max(0, _buffer.Length + ViEndOfLineFactor);
                    }
                    Render();
                }
            }
            else if (orExit)
            {
                throw new ExitException();
            }
        }

        /// <summary>
        /// Delete the character under the cursor.
        /// </summary>
        public static void DeleteChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            int qty = arg as int? ?? 1;
            if (qty < 1) return; // Ignore useless counts

            _singleton.DeleteCharImpl(qty, orExit: false);
        }

        /// <summary>
        /// Delete the character under the cursor, or if the line is empty, exit the process.
        /// </summary>
        public static void DeleteCharOrExit(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.DeleteCharImpl(1, orExit: true);
        }

        private bool AcceptLineImpl(bool validate)
        {
            using var _ = _prediction.DisableScoped();

            ParseInput();
            if (_parseErrors.Any(e => e.IncompleteInput))
            {
                Insert('\n');
                return false;
            }

            // If text was pasted, for performance reasons we skip rendering for some time,
            // but if input is accepted, we won't have another chance to render.
            //
            // Also - if there was an emphasis, we want to clear that before accepting
            // and that requires rendering.
            bool renderNeeded = _emphasisStart >= 0 || _queuedKeys.Count > 0;

            _emphasisStart = -1;
            _emphasisLength = 0;

            var insertionPoint = _current;
            // Make sure cursor is at the end before writing the line
            _current = _buffer.Length;

            if (renderNeeded)
            {
                ForceRender();
            }

            // Only run validation if we haven't before.  If we have and status line shows an error,
            // treat that as a -Force and accept the input so it is added to history, and PowerShell
            // can report an error as it normally does.
            if (validate && !_statusIsErrorMessage)
            {
                var errorMessage = Validate(_ast);
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    // If there are more keys, assume the user pasted with a right click and
                    // we should insert a newline even though validation failed.
                    if (_queuedKeys.Count > 0)
                    {
                        // Validation may have moved the cursor.  Because there are queued
                        // keys, we need to move the cursor back to the correct place, and
                        // ignore where validation put the cursor because the queued keys
                        // will be inserted in the wrong place.
                        SetCursorPosition(insertionPoint);
                        Insert('\n');
                    }
                    _statusLinePrompt = "";
                    _statusBuffer.Append(errorMessage);
                    _statusIsErrorMessage = true;
                    Render();
                    return false;
                }
            }

            if (_statusIsErrorMessage)
            {
                ClearStatusMessage(render: true);
            }

            // Let public API set cursor to end of line incase end of line is end of buffer
            SetCursorPosition(_current);

            if (_prediction.ActiveView is PredictionListView listView)
            {
                // Send feedback to prediction plugin if a list item is accepted as the final command line.
                listView.OnSuggestionAccepted();
            }

            // Clear the prediction view if there is one.
            _prediction.ActiveView.Clear(cursorAtEol: true);

            _console.Write("\n");
            _inputAccepted = true;
            return true;
        }

        class CommandValidationVisitor : AstVisitor
        {
            private readonly Ast _rootAst;
            internal string detectedError;

            internal CommandValidationVisitor(Ast rootAst)
            {
                _rootAst = rootAst;
            }

            public override AstVisitAction VisitCommand(CommandAst commandAst)
            {
                var commandName = commandAst.GetCommandName();
                if (commandName != null)
                {
                    if (_singleton._engineIntrinsics != null)
                    {
                        var commandInfo = _singleton._engineIntrinsics.InvokeCommand.GetCommand(commandName, CommandTypes.All);
                        if (commandInfo == null && !_singleton.UnresolvedCommandCouldSucceed(commandName, _rootAst))
                        {
                            _singleton._current = commandAst.CommandElements[0].Extent.EndOffset;
                            detectedError = string.Format(CultureInfo.CurrentCulture, PSReadLineResources.CommandNotFoundError, commandName);
                            return AstVisitAction.StopVisit;
                        }
                    }

                    if (commandAst.CommandElements.Any(e => e is ScriptBlockExpressionAst))
                    {
                        if (_singleton._options.CommandsToValidateScriptBlockArguments == null ||
                            !_singleton._options.CommandsToValidateScriptBlockArguments.Contains(commandName))
                        {
                            return AstVisitAction.SkipChildren;
                        }
                    }
                }

                if (_singleton._options.CommandValidationHandler != null)
                {
                    try
                    {
                        _singleton._options.CommandValidationHandler(commandAst);
                    }
                    catch (Exception e)
                    {
                        detectedError = e.Message;
                    }
                }

                return !string.IsNullOrWhiteSpace(detectedError)
                    ? AstVisitAction.StopVisit
                    : AstVisitAction.Continue;
            }
        }

        private string Validate(Ast rootAst)
        {
            if (_parseErrors != null && _parseErrors.Length > 0)
            {
                // Move the cursor to the point of error
                _current = _parseErrors[0].Extent.EndOffset;
                return _parseErrors[0].Message;
            }

            var validationVisitor = new CommandValidationVisitor(rootAst);
            rootAst.Visit(validationVisitor);
            if (!string.IsNullOrWhiteSpace(validationVisitor.detectedError))
            {
                return validationVisitor.detectedError;
            }

            return null;
        }

        private bool UnresolvedCommandCouldSucceed(string commandName, Ast rootAst)
        {
            // This is a little hacky, but we check for a few things where part of the current
            // command defines/imports new commands that PowerShell might not yet know about.
            // There is little reason to go to great lengths at being correct here, validation
            // is just a small usability  tweak to avoid cluttering up history - PowerShell
            // will report errors for stuff we actually let through.

            // Do we define a function matching the command name?
            var fnDefns = rootAst.FindAll(ast => ast is FunctionDefinitionAst, true).OfType<FunctionDefinitionAst>();
            if (fnDefns.Any(fnDefnAst => fnDefnAst.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var cmdAsts = rootAst.FindAll(ast => ast is CommandAst, true).OfType<CommandAst>();
            foreach (var cmdAst in cmdAsts)
            {
                // If we dot source something, we can't in general know what is being
                // dot sourced so just assume the unresolved command will work.
                // If we use the invocation operator, allow that because an expression
                // is being invoked and it's reasonable to just allow it.
                if (cmdAst.InvocationOperator != TokenKind.Unknown)
                {
                    return true;
                }

                // Are we importing a module or being tricky with Invoke-Expression?  Let those through.
                var candidateCommand = cmdAst.GetCommandName();
                if (candidateCommand.Equals("Import-Module", StringComparison.OrdinalIgnoreCase)
                    || candidateCommand.Equals("ipmo", StringComparison.OrdinalIgnoreCase)
                    || candidateCommand.Equals("Invoke-Expression", StringComparison.OrdinalIgnoreCase)
                    || candidateCommand.Equals("iex", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (commandName.Length == 1)
            {
                switch (commandName[0])
                {
                // The following are debugger commands that should be accepted if we're debugging
                // because the console host will interpret these commands directly.
                case 's': case 'v': case 'o': case 'c': case 'q': case 'k': case 'l':
                case 'S': case 'V': case 'O': case 'C': case 'Q': case 'K': case 'L':
                case '?': case 'h': case 'H':
                    // Ideally we would check $PSDebugContext, but it is set at function
                    // scope, and because we're in a module, we can't find that variable
                    // (arguably a PowerShell issue.)
                    // NestedPromptLevel is good enough though - it's rare to be in a nested.
                    var nestedPromptLevel = _engineIntrinsics.SessionState.PSVariable.GetValue("NestedPromptLevel");
                    if (nestedPromptLevel is int)
                    {
                        return ((int)nestedPromptLevel) > 0;
                    }
                    break;
                }
            }

            return false;
        }

        /// <summary>
        /// Attempt to execute the current input.  If the current input is incomplete (for
        /// example there is a missing closing parenthesis, bracket, or quote, then the
        /// continuation prompt is displayed on the next line and PSReadLine waits for
        /// keys to edit the current input.
        /// </summary>
        public static void AcceptLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.AcceptLineImpl(false);
        }

        /// <summary>
        /// Attempt to execute the current input.  If the current input is incomplete (for
        /// example there is a missing closing parenthesis, bracket, or quote, then the
        /// continuation prompt is displayed on the next line and PSReadLine waits for
        /// keys to edit the current input.
        /// </summary>
        public static void ValidateAndAcceptLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.AcceptLineImpl(true);
        }

        /// <summary>
        /// Attempt to execute the current input.  If it can be executed (like AcceptLine),
        /// then recall the next item from history the next time ReadLine is called.
        /// </summary>
        public static void AcceptAndGetNext(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_singleton.AcceptLineImpl(false))
            {
                if (_singleton._currentHistoryIndex < (_singleton._history.Count - 1))
                {
                    _singleton._getNextHistoryIndex = _singleton._currentHistoryIndex + 1;
                }
                else
                {
                    Ding();
                }
            }
        }

        /// <summary>
        /// The continuation prompt is displayed on the next line and PSReadLine waits for
        /// keys to edit the current input.  This is useful to enter multi-line input as
        /// a single command even when a single line is complete input by itself.
        /// </summary>
        public static void AddLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            Insert('\n');
        }

        /// <summary>
        /// A new empty line is created above the current line regardless of where the cursor
        /// is on the current line.  The cursor moves to the beginning of the new line.
        /// </summary>
        public static void InsertLineAbove(ConsoleKeyInfo? key = null, object arg = null)
        {
            // Move the current position to the beginning of the current line and only the current line.
            _singleton._current = GetBeginningOfLinePos(_singleton._current);
            Insert('\n');
            PreviousLine();
        }

        /// <summary>
        /// A new empty line is created below the current line regardless of where the cursor
        /// is on the current line.  The cursor moves to the beginning of the new line.
        /// </summary>
        public static void InsertLineBelow(ConsoleKeyInfo? key = null, object arg = null)
        {
            int i = _singleton._current;
            for (; i < _singleton._buffer.Length; i++)
            {
                if (_singleton._buffer[i] == '\n')
                {
                    break;
                }
            }

            _singleton._current = i;

            Insert('\n');
        }
    }
}
