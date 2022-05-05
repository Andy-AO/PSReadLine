using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RL = Microsoft.PowerShell.PSConsoleReadLine;

namespace Microsoft.PowerShell.PSReadLine
{
    public class History
    {
        //class start
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

        public void HistorySearch(int direction)
        {
            if (SearchHistoryCommandCount == 0)
            {
                if (_renderer.LineIsMultiLine())
                {
                    _rl.MoveToLine(direction);
                    return;
                }

                SearchHistoryPrefix = _rl.buffer.ToString(0, _renderer.Current);
                _renderer.EmphasisStart = 0;
                _renderer.EmphasisLength = _renderer.Current;
                if (_rl.Options.HistoryNoDuplicates) HashedHistory = new Dictionary<string, int>();
            }

            SearchHistoryCommandCount += 1;

            var count = Math.Abs(direction);
            direction = direction < 0 ? -1 : +1;
            var newHistoryIndex = CurrentHistoryIndex;
            while (count > 0)
            {
                newHistoryIndex += direction;
                if (newHistoryIndex < 0 || newHistoryIndex >= Historys.Count) break;

                if (Historys[newHistoryIndex].FromOtherSession && SearchHistoryPrefix.Length == 0) continue;

                var line = Historys[newHistoryIndex].CommandLine;
                if (line.StartsWith(SearchHistoryPrefix, _rl.Options.HistoryStringComparison))
                {
                    if (_rl.Options.HistoryNoDuplicates)
                    {
                        if (!HashedHistory.TryGetValue(line, out var index))
                        {
                            HashedHistory.Add(line, newHistoryIndex);
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

            if (newHistoryIndex >= 0 && newHistoryIndex <= Historys.Count)
            {
                // Set '_current' back to where it was when starting the first search, because
                // it might be changed during the rendering of the last matching history command.
                _renderer.Current = _renderer.EmphasisLength;
                CurrentHistoryIndex = newHistoryIndex;
                var moveCursor = RL.InViCommandMode()
                    ? History.HistoryMoveCursor.ToBeginning
                    : _rl.Options.HistorySearchCursorMovesToEnd
                        ? History.HistoryMoveCursor.ToEnd
                        : History.HistoryMoveCursor.DontMove;
                UpdateFromHistory(moveCursor);
            }
        }

        /// <summary>
        ///     Perform an incremental backward search through history.
        /// </summary>
        public static void ReverseSearchHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _s.InteractiveHistorySearch(-1);
        }

        /// <summary>
        ///     Replace the current input with the 'previous' item from PSReadLine history
        ///     that matches the characters between the start and the input and the cursor.
        /// </summary>
        public static void HistorySearchBackward(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, -1);
            if (numericArg > 0) numericArg = -numericArg;

            _s.SaveCurrentLine();
            _s.HistorySearch(numericArg);
        }

        /// <summary>
        ///     Replace the current input with the 'previous' item from PSReadLine history.
        /// </summary>
        public static void PreviousHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, -1);
            if (numericArg > 0) numericArg = -numericArg;

            if (RL.UpdateListSelection(numericArg)) return;

            _s.SaveCurrentLine();
            _s.HistoryRecall(numericArg);
        }

        /// <summary>
        ///     Replace the current input with the 'next' item from PSReadLine history.
        /// </summary>
        public static void NextHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            RL.TryGetArgAsInt(arg, out var numericArg, +1);
            if (RL.UpdateListSelection(numericArg)) return;

            _s.SaveCurrentLine();
            _s.HistoryRecall(numericArg);
        }

        /// <summary>
        ///     Return a collection of history items.
        /// </summary>
        public static HistoryItem[] GetHistoryItems()
        {
            return _s.Historys.ToArray();
        }

        /// <summary>
        ///     Move to the first item in the history.
        /// </summary>
        public static void BeginningOfHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _s.SaveCurrentLine();
            _s.CurrentHistoryIndex = 0;
            _s.UpdateFromHistory(History.HistoryMoveCursor.ToEnd);
        }


        /// <summary>
        ///     Clears history in PSReadLine.  This does not affect PowerShell history.
        /// </summary>
        public static void ClearHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _s.Historys?.Clear();
            _s.RecentHistory?.Clear();
            _s.CurrentHistoryIndex = 0;
        }

        /// <summary>
        ///     Move to the last item (the current input) in the history.
        /// </summary>
        public static void EndOfHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            _s.SaveCurrentLine();
            History.GoToEndOfHistory();
        }

        /// <summary>
        ///     Add a command to the history - typically used to restore
        ///     history from a previous session.
        /// </summary>
        public static void AddToHistory(string command)
        {
            command = command.Replace("\r\n", "\n");
            var editItems = new List<EditItem> {RL.EditItemInsertString.Create(command, 0)};
            _s.MaybeAddToHistory(command, editItems, 1, false, false);
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

        public static bool IsSecretMgmtCommand(StringConstantExpressionAst strConst, out CommandAst command)
        {
            var result = false;
            command = strConst.Parent as CommandAst;

            if (command is not null)
                result = ReferenceEquals(command.CommandElements[0], strConst)
                         && History.SecretMgmtCommands.Contains(strConst.Value);

            return result;
        }

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


        private static readonly PSConsoleReadLine _rl = PSConsoleReadLine.Singleton;
        private static readonly Renderer _renderer = Renderer.Singleton;

        public void SaveHistoryAtExit()
        {
            int end = Historys.Count - 1;
            WriteHistoryRange(0, end, true);
        }

        public void ReadHistoryFile()
        {
            if (File.Exists(_rl.Options.HistorySavePath))
            {
                Action action = () =>
                {
                    var historyLines = File.ReadAllLines(_rl.Options.HistorySavePath);
                    UpdateHistoryFromFile(historyLines, false,
                        true);
                    var fileInfo = new FileInfo(_rl.Options.HistorySavePath);
                    HistoryFileLastSavedSize = fileInfo.Length;
                };
                WithHistoryFileMutexDo(1000, action);
            }
        }

        public string GetHistorySaveFileMutexName()
        {
            // Return a reasonably unique name - it's not too important as there will rarely
            // be any contention.
            var hashFromPath = FNV1a32Hash.ComputeHash(
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? _rl.Options.HistorySavePath.ToLower()
                    : _rl.Options.HistorySavePath);
            return "PSReadLineHistoryFile_" + hashFromPath;
        }

        /// <summary>
        ///     Delete the character before the cursor.
        /// </summary>
        public static void BackwardDeleteChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (_rl._visualSelectionCommandCount > 0)
            {
                _renderer.GetRegion(out var start, out var length);
                PSConsoleReadLine.Delete(start, length);
                return;
            }

            if (_rl.buffer.Length > 0 && _renderer.Current > 0)
            {
                var qty = arg as int? ?? 1;
                if (qty < 1) return; // Ignore useless counts
                qty = Math.Min(qty, _renderer.Current);

                var startDeleteIndex = _renderer.Current - qty;

                _rl.RemoveTextToViRegister(startDeleteIndex, qty, BackwardDeleteChar, arg,
                    !(PSConsoleReadLine.InViEditMode()));
                _renderer.Current = startDeleteIndex;
                _renderer.Render();
            }
        }


        public void UpdateHistoryFromFile(IEnumerable<string> historyLines, bool fromDifferentSession,
            bool fromInitialRead)
        {
            var sb = new StringBuilder();
            foreach (var line in historyLines)
                if (line.EndsWith("`", StringComparison.Ordinal))
                {
                    sb.Append(line, 0, line.Length - 1);
                    sb.Append('\n');
                }
                else if (sb.Length > 0)
                {
                    sb.Append(line);
                    var l = sb.ToString();
                    var editItems = new List<EditItem> {PSConsoleReadLine.EditItemInsertString.Create(l, 0)};
                    MaybeAddToHistory(l, editItems, 1, fromDifferentSession, fromInitialRead);
                    sb.Clear();
                }
                else
                {
                    var editItems = new List<EditItem> {PSConsoleReadLine.EditItemInsertString.Create(line, 0)};
                    MaybeAddToHistory(line, editItems, 1, fromDifferentSession, fromInitialRead);
                }
        }

        public bool WithHistoryFileMutexDo(int timeout, Action action)
        {
            var retryCount = 0;
            do
            {
                try
                {
                    if (HistoryFileMutex.WaitOne(timeout))
                        try
                        {
                            action();
                            return true;
                        }
                        catch (UnauthorizedAccessException uae)
                        {
                            ReportHistoryFileError(uae);
                            return false;
                        }
                        catch (IOException ioe)
                        {
                            ReportHistoryFileError(ioe);
                            return false;
                        }
                        finally
                        {
                            HistoryFileMutex.ReleaseMutex();
                        }

                    // Consider it a failure if we timed out on the mutex.
                    return false;
                }
                catch (AbandonedMutexException)
                {
                    retryCount += 1;

                    // We acquired the mutex object that was abandoned by another powershell process.
                    // Now, since we own it, we must release it before retry, otherwise, we will miss
                    // a release and keep holding the mutex, in which case the 'WaitOne' calls from
                    // all other powershell processes will time out.
                    HistoryFileMutex.ReleaseMutex();
                }
            } while (retryCount > 0 && retryCount < 3);

            // If we reach here, that means we've done the retries but always got the 'AbandonedMutexException'.
            return false;
        }

        public void WriteHistoryRange(int start, int end, bool overwritten)
        {
            WithHistoryFileMutexDo(100, () =>
            {
                var retry = true;
                // Get the new content since the last sync.
                var historyLines = overwritten ? null : ReadHistoryFileIncrementally();

                try
                {
                    retry_after_creating_directory:
                    try
                    {
                        using (var file = overwritten
                                   ? File.CreateText(_rl.Options.HistorySavePath)
                                   : File.AppendText(_rl.Options.HistorySavePath))
                        {
                            for (var i = start; i <= end; i++)
                            {
                                var item = Historys[i];
                                item._saved = true;

                                // Actually, skip writing sensitive items to file.
                                if (item._sensitive) continue;

                                var line = item.CommandLine.Replace("\n", "`\n");
                                file.WriteLine(line);
                            }
                        }

                        var fileInfo = new FileInfo(_rl.Options.HistorySavePath);
                        HistoryFileLastSavedSize = fileInfo.Length;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Try making the directory, but just once
                        if (retry)
                        {
                            retry = false;
                            Directory.CreateDirectory(Path.GetDirectoryName(_rl.Options.HistorySavePath));
                            goto retry_after_creating_directory;
                        }
                    }
                }
                finally
                {
                    if (historyLines != null)
                        // Populate new history from other sessions to the history queue after we are done
                        // with writing the specified range to the file.
                        // We do it at this point to make sure the range of history items from 'start' to
                        // 'end' do not get changed before the writing to the file.
                        UpdateHistoryFromFile(historyLines, true, false);
                }
            });
        }

        public void ReportHistoryFileError(Exception e)
        {
            if (HistoryErrorReportedCount == 2)
                return;

            HistoryErrorReportedCount += 1;
            Console.Write(_rl.Options._errorColor);
            Console.WriteLine(PSReadLineResources.HistoryFileErrorMessage, _rl.Options.HistorySavePath, e.Message);
            if (HistoryErrorReportedCount == 2) Console.WriteLine(PSReadLineResources.HistoryFileErrorFinalMessage);
            Console.Write("\x1b0m");
        }

        internal void HistoryRecall(int direction)
        {
            if (RecallHistoryCommandCount == 0 && _renderer.LineIsMultiLine())
            {
                _rl.MoveToLine(direction);
                return;
            }

            if (_rl.Options.HistoryNoDuplicates && RecallHistoryCommandCount == 0)
                HashedHistory = new Dictionary<string, int>();

            var count = Math.Abs(direction);
            direction = direction < 0 ? -1 : +1;
            var newHistoryIndex = CurrentHistoryIndex;
            while (count > 0)
            {
                newHistoryIndex += direction;
                if (newHistoryIndex < 0 || newHistoryIndex >= Historys.Count) break;

                if (Historys[newHistoryIndex].FromOtherSession) continue;

                if (_rl.Options.HistoryNoDuplicates)
                {
                    var line = Historys[newHistoryIndex].CommandLine;
                    if (!HashedHistory.TryGetValue(line, out var index))
                    {
                        HashedHistory.Add(line, newHistoryIndex);
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

            RecallHistoryCommandCount = RecallHistoryCommandCount + 1;
            if (newHistoryIndex >= 0 && newHistoryIndex <= Historys.Count)
            {
                CurrentHistoryIndex = newHistoryIndex;
                var moveCursor = RL.InViCommandMode() && !(_rl.Options.HistorySearchCursorMovesToEnd)
                    ? History.HistoryMoveCursor.ToBeginning
                    : History.HistoryMoveCursor.ToEnd;
                UpdateFromHistory(moveCursor);
            }
        }

        public static AddToHistoryOption GetDefaultAddToHistoryOption(string line)
        {
            if (string.IsNullOrEmpty(line)) return AddToHistoryOption.SkipAdding;

            var sSensitivePattern = History.SensitivePattern;
            var match = sSensitivePattern.Match(line);
            if (ReferenceEquals(match, Match.Empty)) return AddToHistoryOption.MemoryAndFile;

            // The input contains at least one match of some sensitive patterns, so now we need to further
            // analyze the input using the ASTs to see if it should actually be considered sensitive.
            var isSensitive = false;
            var parseErrors = _rl.ParseErrors;

            // We need to compare the text here, instead of simply checking whether or not '_ast' is null.
            // This is because we may need to update from history file in the middle of editing an input,
            // and in that case, the '_ast' may be not-null, but it was not parsed from 'line'.
            var ast = string.Equals(_rl.RLAst?.Extent.Text, line)
                ? _rl.RLAst
                : Parser.ParseInput(line, out _, out parseErrors);

            if (parseErrors != null && parseErrors.Length > 0)
                // If the input has any parsing errors, we cannot reliably analyze the AST. We just consider
                // it sensitive in this case, given that it contains matches of our sensitive pattern.
                return AddToHistoryOption.MemoryOnly;

            do
            {
                var start = match.Index;
                var end = start + match.Length;

                var asts = ast.FindAll(
                    ast => ast.Extent.StartOffset <= start && ast.Extent.EndOffset >= end,
                    true);

                var innerAst = asts.Last();
                switch (innerAst)
                {
                    case VariableExpressionAst:
                        // It's a variable with sensitive name. Using the variable is fine, but assigning to
                        // the variable could potentially expose sensitive content.
                        // If it appears on the left-hand-side of an assignment, and the right-hand-side is
                        // not a command invocation, we consider it sensitive.
                        // e.g. `$token = Get-Secret` vs. `$token = 'token-text'` or `$token, $url = ...`
                        isSensitive = IsOnLeftSideOfAnAssignment(innerAst, out var rhs)
                                      && rhs is not PipelineAst;

                        if (!isSensitive) match = match.NextMatch();
                        break;

                    case StringConstantExpressionAst strConst:
                        // If it's not a command name, or it's not one of the secret management commands that
                        // we can ignore, we consider it sensitive.
                        isSensitive = !IsSecretMgmtCommand(strConst, out var command);

                        if (!isSensitive)
                            // We can safely skip the whole command text.
                            match = sSensitivePattern.Match(line, command.Extent.EndOffset);
                        break;

                    case CommandParameterAst param:
                        // Special-case the '-AsPlainText' parameter.
                        if (string.Equals(param.ParameterName, "AsPlainText"))
                        {
                            isSensitive = true;
                            break;
                        }

                        var arg = GetArgumentForParameter(param);
                        if (arg is null)
                            // If no argument is found following the parameter, then it could be a switching parameter
                            // such as '-UseDefaultPassword' or '-SaveToken', which we assume will not expose sensitive information.
                            match = match.NextMatch();
                        else if (arg is VariableExpressionAst)
                            // Argument is a variable. It's fine to use a variable for a senstive parameter.
                            // e.g. `Invoke-WebRequest -Token $token`
                            match = sSensitivePattern.Match(line, arg.Extent.EndOffset);
                        else if (arg is ParenExpressionAst paren
                                 && paren.Pipeline is PipelineAst pipeline
                                 && pipeline.PipelineElements[0] is not CommandExpressionAst)
                            // Argument is a command invocation, such as `Invoke-WebRequest -Token (Get-Secret)`.
                            match = match.NextMatch();
                        else
                            // We consider all other arguments sensitive.
                            isSensitive = true;
                        break;

                    default:
                        isSensitive = true;
                        break;
                }
            } while (!isSensitive && !ReferenceEquals(match, Match.Empty));

            return isSensitive ? AddToHistoryOption.MemoryOnly : AddToHistoryOption.MemoryAndFile;
        }

        public AddToHistoryOption GetAddToHistoryOption(string line)
        {
            // Whitespace only is useless, never add.
            if (string.IsNullOrWhiteSpace(line)) return AddToHistoryOption.SkipAdding;

            // Under "no dupes" (which is on by default), immediately drop dupes of the previous line.
            if (_rl.Options.HistoryNoDuplicates && Historys.Count > 0 &&
                string.Equals(Historys[Historys.Count - 1].CommandLine, line, StringComparison.Ordinal))
                return AddToHistoryOption.SkipAdding;

            if (_rl.Options.AddToHistoryHandler != null)
            {
                if (_rl.Options.AddToHistoryHandler == PSConsoleReadLineOptions.DefaultAddToHistoryHandler)
                    // Avoid boxing if it's the default handler.
                    return GetDefaultAddToHistoryOption(line);

                var value = _rl.Options.AddToHistoryHandler(line);
                if (value is PSObject psObj) value = psObj.BaseObject;

                if (value is bool boolValue)
                    return boolValue ? AddToHistoryOption.MemoryAndFile : AddToHistoryOption.SkipAdding;

                if (value is AddToHistoryOption enumValue) return enumValue;

                if (value is string strValue && Enum.TryParse(strValue, out enumValue)) return enumValue;

                // 'TryConvertTo' incurs exception handling when the value cannot be converted to the target type.
                // It's expensive, especially when we need to process lots of history items from file during the
                // initialization. So do the conversion as the last resort.
                if (LanguagePrimitives.TryConvertTo(value, out enumValue)) return enumValue;
            }

            // Add to both history queue and file by default.
            return AddToHistoryOption.MemoryAndFile;
        }

        public void IncrementalHistoryWrite()
        {
            var i = CurrentHistoryIndex - 1;
            while (i >= 0)
            {
                if (Historys[i]._saved) break;
                i -= 1;
            }

            WriteHistoryRange(i + 1, Historys.Count - 1, false);
        }

        public void ClearSavedCurrentLine()
        {
            _savedCurrentLine.CommandLine = null;
            _savedCurrentLine._edits = null;
            _savedCurrentLine._undoEditIndex = 0;
            _savedCurrentLine._editGroupStart = -1;
        }

        public string MaybeAddToHistory(
            string result,
            List<EditItem> edits,
            int undoEditIndex,
            bool fromDifferentSession = false,
            bool fromInitialRead = false)
        {
            var addToHistoryOption = GetAddToHistoryOption(result);
            if (addToHistoryOption != AddToHistoryOption.SkipAdding)
            {
                var fromHistoryFile = fromDifferentSession || fromInitialRead;
                PreviousHistoryItem = new HistoryItem
                {
                    CommandLine = result,
                    _edits = edits,
                    _undoEditIndex = undoEditIndex,
                    _editGroupStart = -1,
                    _saved = fromHistoryFile,
                    FromOtherSession = fromDifferentSession,
                    FromHistoryFile = fromInitialRead
                };

                if (!fromHistoryFile)
                {
                    // Add to the recent history queue, which is used when querying for prediction.
                    RecentHistory.Enqueue(result);
                    // 'MemoryOnly' indicates sensitive content in the command line
                    PreviousHistoryItem._sensitive = addToHistoryOption == AddToHistoryOption.MemoryOnly;
                    PreviousHistoryItem.StartTime = DateTime.UtcNow;
                }

                Historys.Enqueue(PreviousHistoryItem);

                CurrentHistoryIndex = Historys.Count;

                if (_rl.Options.HistorySaveStyle == HistorySaveStyle.SaveIncrementally && !fromHistoryFile)
                    IncrementalHistoryWrite();
            }
            else
            {
                PreviousHistoryItem = null;
            }

            // Clear the saved line unless we used AcceptAndGetNext in which
            // case we're really still in middle of history and might want
            // to recall the saved line.
            if (GetNextHistoryIndex == 0) ClearSavedCurrentLine();
            return result;
        }

        public bool MaybeReadHistoryFile()
        {
            if (_rl.Options.HistorySaveStyle == HistorySaveStyle.SaveIncrementally)
            {
                Action action = () =>
                {
                    var historyLines = ReadHistoryFileIncrementally();
                    if (historyLines != null)
                        UpdateHistoryFromFile(historyLines, true,
                            false);
                };
                return WithHistoryFileMutexDo(1000, action);
            }

            // true means no errors, not that we actually read the file
            return true;
        }


        /// <summary>
        ///     Helper method to read the incremental part of the history file.
        ///     Note: the call to this method should be guarded by the mutex that protects the history file.
        /// </summary>
        public List<string> ReadHistoryFileIncrementally()
        {
            var fileInfo = new FileInfo(_rl.Options.HistorySavePath);
            if (fileInfo.Exists && fileInfo.Length != HistoryFileLastSavedSize)
            {
                var historyLines = new List<string>();
                using (var fs = new FileStream(_rl.Options.HistorySavePath, FileMode.Open))
                using (var sr = new StreamReader(fs))
                {
                    fs.Seek(HistoryFileLastSavedSize, SeekOrigin.Begin);

                    while (!sr.EndOfStream) historyLines.Add(sr.ReadLine());
                }

                HistoryFileLastSavedSize = fileInfo.Length;
                return historyLines.Count > 0 ? historyLines : null;
            }

            return null;
        }

        public void SaveCurrentLine()
        {
            // We're called before any history operation - so it's convenient
            // to check if we need to load history from another sessions now.
            MaybeReadHistoryFile();

            AnyHistoryCommandCount = AnyHistoryCommandCount + 1;
            if (_savedCurrentLine.CommandLine == null)
            {
                _savedCurrentLine.CommandLine = _rl.buffer.ToString();
                _savedCurrentLine._edits = _rl._edits;
                _savedCurrentLine._undoEditIndex = _rl._undoEditIndex;
                _savedCurrentLine._editGroupStart = _rl._editGroupStart;
            }
        }

        private static readonly History _s = new();
        public static History Singleton => _s;

        // Pattern used to check for sensitive inputs.
        public static Regex SensitivePattern => s_sensitivePattern;

        public static HashSet<string> SecretMgmtCommands => s_SecretMgmtCommands;

        public int AnyHistoryCommandCount
        {
            get => _anyHistoryCommandCount;
            set => _anyHistoryCommandCount = value;
        }

        public const string _forwardISearchPrompt = "fwd-i-search: ";
        public const string _backwardISearchPrompt = "bck-i-search: ";
        public const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
        public const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";

        private static readonly HashSet<string> s_SecretMgmtCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "Get-Secret",
            "Get-SecretInfo",
            "Get-SecretVault",
            "Register-SecretVault",
            "Remove-Secret",
            "Set-SecretInfo",
            "Set-SecretVaultDefault",
            "Test-SecretVault",
            "Unlock-SecretVault",
            "Unregister-SecretVault"
        };

        public int CurrentHistoryIndex
        {
            get => _currentHistoryIndex;
            set => _currentHistoryIndex = value;
        }

        // History state
        public HistoryQueue<HistoryItem> Historys
        {
            get => _history;
            set => _history = value;
        }

        // When cycling through history, the current line (not yet added to history)
        // is saved here so it can be restored.
        public readonly HistoryItem _savedCurrentLine = new HistoryItem();

        private static readonly Regex s_sensitivePattern = new(
            "password|asplaintext|token|apikey|secret",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private int _anyHistoryCommandCount;
        private int _currentHistoryIndex;
        private int _getNextHistoryIndex;
        private HistoryQueue<HistoryItem> _history;
        private Dictionary<string, int> _hashedHistory;
        private int historyErrorReportedCount;
        private long _historyFileLastSavedSize;
        private Mutex _historyFileMutex;

        public void UpdateFromHistory(HistoryMoveCursor moveCursor)
        {
            string line;
            if (CurrentHistoryIndex == Historys.Count)
            {
                line = _savedCurrentLine.CommandLine;
                _rl._edits = new List<EditItem>(_savedCurrentLine._edits);
                _rl._undoEditIndex = _savedCurrentLine._undoEditIndex;
                _rl._editGroupStart = _savedCurrentLine._editGroupStart;
            }
            else
            {
                line = Historys[CurrentHistoryIndex].CommandLine;
                _rl._edits = new List<EditItem>(Historys[CurrentHistoryIndex]._edits);
                _rl._undoEditIndex = Historys[CurrentHistoryIndex]._undoEditIndex;
                _rl._editGroupStart = Historys[CurrentHistoryIndex]._editGroupStart;
            }

            _rl.buffer.Clear();
            _rl.buffer.Append(line);

            switch (moveCursor)
            {
                case History.HistoryMoveCursor.ToEnd:
                    _renderer.Current = Math.Max(0, _rl.buffer.Length + PSConsoleReadLine.ViEndOfLineFactor);
                    break;
                case History.HistoryMoveCursor.ToBeginning:
                    _renderer.Current = 0;
                    break;
                default:
                    if (_renderer.Current > _rl.buffer.Length)
                        _renderer.Current = Math.Max(0, _rl.buffer.Length + RL.ViEndOfLineFactor);
                    break;
            }

            using var _ = _rl._Prediction.DisableScoped();
            _renderer.Render();
        }

        public void InteractiveHistorySearch(int direction)
        {
            using var _ = _rl._Prediction.DisableScoped();
            SaveCurrentLine();

            // Add a status line that will contain the search prompt and string
            _rl._statusLinePrompt = direction > 0 ? History._forwardISearchPrompt : History._backwardISearchPrompt;
            _rl._statusBuffer.Append("_");

            _renderer.Render(); // Render prompt
            InteractiveHistorySearchLoop(direction);
            _renderer.EmphasisStart = -1;
            _renderer.EmphasisLength = 0;

            // Remove our status line, this will render
            _rl.ClearStatusMessage(true);
        }

        public void UpdateHistoryDuringInteractiveSearch(string toMatch, int direction, ref int searchFromPoint)
        {
            searchFromPoint += direction;
            for (; searchFromPoint >= 0 && searchFromPoint < Historys.Count; searchFromPoint += direction)
            {
                var line = Historys[searchFromPoint].CommandLine;
                var startIndex = line.IndexOf(toMatch, _rl.Options.HistoryStringComparison);
                if (startIndex >= 0)
                {
                    if (_rl.Options.HistoryNoDuplicates)
                    {
                        if (!HashedHistory.TryGetValue(line, out var index))
                            HashedHistory.Add(line, searchFromPoint);
                        else if (index != searchFromPoint) continue;
                    }

                    _rl._statusLinePrompt =
                        direction > 0 ? History._forwardISearchPrompt : History._backwardISearchPrompt;
                    _renderer.Current = startIndex;
                    _renderer.EmphasisStart = startIndex;
                    _renderer.EmphasisLength = toMatch.Length;
                    CurrentHistoryIndex = searchFromPoint;
                    var moveCursor = _rl.Options.HistorySearchCursorMovesToEnd
                        ? History.HistoryMoveCursor.ToEnd
                        : History.HistoryMoveCursor.DontMove;
                    UpdateFromHistory(moveCursor);
                    return;
                }
            }

            // Make sure we're never more than 1 away from being in range so if they
            // reverse direction, the first time they reverse they are back in range.
            if (searchFromPoint < 0)
                searchFromPoint = -1;
            else if (searchFromPoint >= Historys.Count)
                searchFromPoint = Historys.Count;

            _renderer.EmphasisStart = -1;
            _renderer.EmphasisLength = 0;
            _rl._statusLinePrompt =
                direction > 0 ? History._failedForwardISearchPrompt : History._failedBackwardISearchPrompt;
            _renderer.Render();
        }

        private void InteractiveHistorySearchLoop(int direction)
        {
            var searchFromPoint = CurrentHistoryIndex;
            var searchPositions = new Stack<int>();
            searchPositions.Push(CurrentHistoryIndex);

            if (_rl.Options.HistoryNoDuplicates) HashedHistory = new Dictionary<string, int>();

            var toMatch = new StringBuilder(64);
            while (true)
            {
                var key = PSConsoleReadLine.ReadKey();
                _rl._dispatchTable.TryGetValue(key, out var handler);
                var function = handler?.Action;
                if (function == (History.ReverseSearchHistory))
                {
                    UpdateHistoryDuringInteractiveSearch(toMatch.ToString(), -1, ref searchFromPoint);
                }
                else if (function == PSConsoleReadLine.ForwardSearchHistory)
                {
                    UpdateHistoryDuringInteractiveSearch(toMatch.ToString(), +1, ref searchFromPoint);
                }
                else if (function == BackwardDeleteChar
                         || key == Keys.Backspace
                         || key == Keys.CtrlH)
                {
                    if (toMatch.Length > 0)
                    {
                        toMatch.Remove(toMatch.Length - 1, 1);
                        _rl._statusBuffer.Remove(_rl._statusBuffer.Length - 2, 1);
                        searchPositions.Pop();
                        searchFromPoint = CurrentHistoryIndex = searchPositions.Peek();
                        var moveCursor = _rl.Options.HistorySearchCursorMovesToEnd
                            ? HistoryMoveCursor.ToEnd
                            : HistoryMoveCursor.DontMove;
                        UpdateFromHistory(moveCursor);

                        if (HashedHistory != null)
                            // Remove any entries with index < searchFromPoint because
                            // we are starting the search from this new index - we always
                            // want to find the latest entry that matches the search string
                            foreach (var pair in HashedHistory.ToArray())
                                if (pair.Value < searchFromPoint)
                                    HashedHistory.Remove(pair.Key);

                        // Prompt may need to have 'failed-' removed.
                        var toMatchStr = toMatch.ToString();
                        var startIndex = _rl.buffer.ToString().IndexOf(toMatchStr, _rl.Options.HistoryStringComparison);
                        if (startIndex >= 0)
                        {
                            _rl._statusLinePrompt = direction > 0
                                ? History._forwardISearchPrompt
                                : History._backwardISearchPrompt;
                            _renderer.Current = startIndex;
                            _renderer.EmphasisStart = startIndex;
                            _renderer.EmphasisLength = toMatch.Length;
                            _renderer.Render();
                        }
                    }
                    else
                    {
                        PSConsoleReadLine.Ding();
                    }
                }
                else if (key == Keys.Escape)
                {
                    // End search
                    break;
                }
                else if (function == PSConsoleReadLine.Abort)
                {
                    // Abort search
                    History.GoToEndOfHistory();
                    break;
                }
                else
                {
                    var toAppend = key.KeyChar;
                    if (char.IsControl(toAppend))
                    {
                        _rl.PrependQueuedKeys(key);
                        break;
                    }

                    toMatch.Append(toAppend);
                    _rl._statusBuffer.Insert(_rl._statusBuffer.Length - 1, toAppend);

                    var toMatchStr = toMatch.ToString();
                    var startIndex = _rl.buffer.ToString().IndexOf(toMatchStr, _rl.Options.HistoryStringComparison);
                    if (startIndex < 0)
                    {
                        UpdateHistoryDuringInteractiveSearch(toMatchStr, direction, ref searchFromPoint);
                    }
                    else
                    {
                        _renderer.Current = startIndex;
                        _renderer.EmphasisStart = startIndex;
                        _renderer.EmphasisLength = toMatch.Length;
                        _renderer.Render();
                    }

                    searchPositions.Push(CurrentHistoryIndex);
                }
            }
        }

        /// <summary>
        ///     Replace the current input with the 'next' item from PSReadLine history
        ///     that matches the characters between the start and the input and the cursor.
        /// </summary>
        public static void HistorySearchForward(ConsoleKeyInfo? key = null, object arg = null)
        {
            PSConsoleReadLine.TryGetArgAsInt(arg, out var numericArg, +1);

            _s.SaveCurrentLine();
            _s.HistorySearch(numericArg);
        }

        public int GetNextHistoryIndex
        {
            get => _getNextHistoryIndex;
            set => _getNextHistoryIndex = value;
        }

        public Dictionary<string, int> HashedHistory
        {
            get => _hashedHistory;
            set => _hashedHistory = value;
        }

        public long HistoryFileLastSavedSize
        {
            get => _historyFileLastSavedSize;
            set => _historyFileLastSavedSize = value;
        }

        public Mutex HistoryFileMutex
        {
            get => _historyFileMutex;
            set => _historyFileMutex = value;
        }

        public HistoryItem PreviousHistoryItem
        {
            get => _previousHistoryItem;
            set => _previousHistoryItem = value;
        }

        public int RecallHistoryCommandCount
        {
            get => _recallHistoryCommandCount;
            set => _recallHistoryCommandCount = value;
        }

        public HistoryQueue<string> RecentHistory
        {
            get => _recentHistory;
            set => _recentHistory = value;
        }

        public int SearchHistoryCommandCount
        {
            get => _searchHistoryCommandCount;
            set => _searchHistoryCommandCount = value;
        }

        public string SearchHistoryPrefix
        {
            get => _searchHistoryPrefix;
            set => _searchHistoryPrefix = value;
        }

        public int HistoryErrorReportedCount
        {
            get => historyErrorReportedCount;
            set => historyErrorReportedCount = value;
        }

        private HistoryItem _previousHistoryItem;
        private int _recallHistoryCommandCount;
        private HistoryQueue<string> _recentHistory;
        private int _searchHistoryCommandCount;
        private string _searchHistoryPrefix;

        public static void GoToEndOfHistory()
        {
            _s.CurrentHistoryIndex = _s.Historys.Count;
            _s.UpdateFromHistory(HistoryMoveCursor.ToEnd);
        }

        public enum HistoryMoveCursor
        {
            ToEnd,
            ToBeginning,
            DontMove
        }
    }
}