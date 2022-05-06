using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine
{
    public class HistorySearcher
    {
        private Stack<int> searchPositions { get; set; }
        private int searchFromPoint { get; set; }
        private StringBuilder toMatch { get; set; }
        private PSKeyInfo key { get; set; }
        private static HistorySearcher Singleton { get; } = new();
        private Action<ConsoleKeyInfo?, object> function { get; set; }
        private static readonly History _hs = History.Singleton;
        private static readonly PSConsoleReadLine _rl = PSConsoleReadLine.Singleton;
        private static readonly Renderer _renderer = Renderer.Singleton;
        private const string _forwardISearchPrompt = "fwd-i-search: ";
        private const string _backwardISearchPrompt = "bck-i-search: ";
        private const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
        private const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";

        /// <summary>
        ///     Perform an incremental backward search through history.
        /// </summary>
        public static void ReverseSearchHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton.InteractiveHistorySearch(-1);
        }

        /// <summary>
        ///     Perform an incremental forward search through history.
        /// </summary>
        public static void ForwardSearchHistory(ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton.InteractiveHistorySearch(+1);
        }

        //start
        private void InteractiveHistorySearch(int direction)
        {
            using var _ = _rl._Prediction.DisableScoped();
            _hs.SaveCurrentLine();

            // Add a status line that will contain the search prompt and string
            _renderer.StatusLinePrompt_______Old = direction > 0 ? _forwardISearchPrompt : _backwardISearchPrompt;
            _renderer.StatusBuffer.Append("_");

            _renderer.Render(); // Render prompt
            HandleUserInput(direction);
            _renderer.EmphasisStart = -1;
            _renderer.EmphasisLength = 0;

            // Remove our status line, this will render
            _rl.ClearStatusMessage(true);
        }

        private void HandleUserInput(int direction)
        {
            searchFromPoint = _hs.CurrentHistoryIndex;
            searchPositions = new Stack<int>();
            searchPositions.Push(_hs.CurrentHistoryIndex);
            if (_rl.Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
            toMatch = new StringBuilder(64);
            while (true)
            {
                // TODO 在这里开始发挥 UI 的职责，感觉可以将这个类直接拆成两部分，历史记录和历史记录搜索
                // TODO searchFromPoint 耦合程度太高，是否应该消除？
                // TODO ref 和 out 的区别是什么？
                // TODO searchPositions 的作用是什么，为什么 UpdateHistoryDuringInteractiveSearch 和  UpdateHistoryDuringInteractiveSearch 不需要？
                key = PSConsoleReadLine.ReadKey();
                _rl._dispatchTable.TryGetValue(key, out var handler);
                function = handler?.Action;

                if (function == HistorySearcher.ReverseSearchHistory)
                {
                    UpdateHistory(-1);
                }
                else if (function == ForwardSearchHistory)
                {
                    UpdateHistory(+1);
                }
                else if (function == PSConsoleReadLine.BackwardDeleteChar
                         || key == Keys.Backspace
                         || key == Keys.CtrlH)
                {
                    // TODO 这些函数列表之间有很大的相似之处，这个怎么办呢？可不可以改成类？这样就可以直接在 private 域中使用这些变量，省得传递来传递去，不是吗？✔
                    HandleBackward(direction);
                }
                else if (key == Keys.Escape)
                {
                    // End search
                    break;
                }
                else if (function == PSConsoleReadLine.Abort)
                {
                    // Abort search
                    GoToEndOfHistory();
                    break;
                }
                else
                {
                    if (HandleCharOfSearchKeyword(direction))
                        break;
                }
            }
        }

        private int HandleBackward(int direction)
        {
            if (toMatch.Length > 0)
            {
                toMatch.Remove(toMatch.Length - 1, 1);
                _renderer.StatusBuffer.Remove(_renderer.StatusBuffer.Length - 2, 1);
                searchPositions.Pop();
                searchFromPoint = _hs.CurrentHistoryIndex = searchPositions.Peek();
                var moveCursor = _rl.Options.HistorySearchCursorMovesToEnd
                    ? History.HistoryMoveCursor.ToEnd
                    : History.HistoryMoveCursor.DontMove;
                _hs.UpdateFromHistory(moveCursor);

                if (_hs.HashedHistory != null)
                    // Remove any entries with index < searchFromPoint because
                    // we are starting the search from this new index - we always
                    // want to find the latest entry that matches the search string
                    foreach (var pair in _hs.HashedHistory.ToArray())
                        if (pair.Value < searchFromPoint)
                            _hs.HashedHistory.Remove(pair.Key);

                // Prompt may need to have 'failed-' removed.
                var toMatchStr = toMatch.ToString();
                var startIndex = _rl.buffer.ToString().IndexOf(toMatchStr, _rl.Options.HistoryStringComparison);
                if (startIndex >= 0)
                {
                    _renderer.StatusLinePrompt_______Old = direction > 0
                        ? _forwardISearchPrompt
                        : _backwardISearchPrompt;
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

            return searchFromPoint;
        }

        private void UpdateHistory(int direction)
        {
            var toMatch = this.toMatch.ToString();
            searchFromPoint += direction;
            for (; searchFromPoint >= 0 && searchFromPoint < _hs.Historys.Count; searchFromPoint += direction)
            {
                var line = _hs.Historys[searchFromPoint].CommandLine;
                var startIndex = line.IndexOf(toMatch, _rl.Options.HistoryStringComparison);
                if (startIndex >= 0)
                {
                    if (_rl.Options.HistoryNoDuplicates)
                    {
                        if (!_hs.HashedHistory.TryGetValue(line, out var index))
                            _hs.HashedHistory.Add(line, searchFromPoint);
                        else if (index != searchFromPoint) continue;
                    }

                    _renderer.StatusLinePrompt_______Old = direction > 0 ? _forwardISearchPrompt : _backwardISearchPrompt;
                    _renderer.Current = startIndex;
                    _renderer.EmphasisStart = startIndex;
                    _renderer.EmphasisLength = toMatch.Length;
                    _hs.CurrentHistoryIndex = searchFromPoint;
                    var moveCursor = _rl.Options.HistorySearchCursorMovesToEnd
                        ? History.HistoryMoveCursor.ToEnd
                        : History.HistoryMoveCursor.DontMove;
                    _hs.UpdateFromHistory(moveCursor);
                    return;
                }
            }

            // Make sure we're never more than 1 away from being in range so if they
            // reverse direction, the first time they reverse they are back in range.
            if (searchFromPoint < 0)
                searchFromPoint = -1;
            else if (searchFromPoint >= _hs.Historys.Count)
                searchFromPoint = _hs.Historys.Count;

            _renderer.EmphasisStart = -1;
            _renderer.EmphasisLength = 0;
            _renderer.StatusLinePrompt_______Old = direction > 0 ? _failedForwardISearchPrompt : _failedBackwardISearchPrompt;
            _renderer.Render();
        }

        private bool HandleCharOfSearchKeyword(int direction)
        {
            var toAppend = key.KeyChar;
            if (char.IsControl(toAppend))
            {
                _rl.PrependQueuedKeys(key);
                return true;
            }

            toMatch.Append(toAppend);
            _renderer.StatusBuffer.Insert(_renderer.StatusBuffer.Length - 1, toAppend);

            var toMatchStr = toMatch.ToString();
            var startIndex = _rl.buffer.ToString().IndexOf(toMatchStr, _rl.Options.HistoryStringComparison);
            if (startIndex < 0)
            {
                UpdateHistory(direction);
            }
            else
            {
                _renderer.Current = startIndex;
                _renderer.EmphasisStart = startIndex;
                _renderer.EmphasisLength = toMatch.Length;
                _renderer.Render();
            }

            searchPositions.Push(_hs.CurrentHistoryIndex);
            return false;
        }

        private static void GoToEndOfHistory()
        {
            _hs.CurrentHistoryIndex = _hs.Historys.Count;
            _hs.UpdateFromHistory(History.HistoryMoveCursor.ToEnd);
        }
    }
}