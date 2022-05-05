using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Microsoft.PowerShell.PSReadLine
{
    public class History
    {
        private static readonly PSConsoleReadLine _rl = PSConsoleReadLine.Singleton;

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
                        isSensitive = PSConsoleReadLine.IsOnLeftSideOfAnAssignment(innerAst, out var rhs)
                                      && rhs is not PipelineAst;

                        if (!isSensitive) match = match.NextMatch();
                        break;

                    case StringConstantExpressionAst strConst:
                        // If it's not a command name, or it's not one of the secret management commands that
                        // we can ignore, we consider it sensitive.
                        isSensitive = !PSConsoleReadLine.
                            IsSecretMgmtCommand(strConst, out var command);

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

                        var arg = PSConsoleReadLine.GetArgumentForParameter(param);
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
    }
}