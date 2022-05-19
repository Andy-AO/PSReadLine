using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine;

public class HistorySearcher
{
    public enum HistoryMoveCursor
    {
        ToEnd,
        ToBeginning,
        DontMove
    }

    private const string _forwardISearchPrompt = "fwd-i-search: ";
    private const string _backwardISearchPrompt = "bck-i-search: ";
    private const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
    private const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";

    // When cycling through history, the current line (not yet added to history)
    // is saved here so it can be restored.
    private readonly HistoryItem _savedCurrentLine = new();
    private int direction;
    private int _currentHistoryIndex;
    private int _searchFromPoint;

    static HistorySearcher()
    {
        Singleton = new HistorySearcher();
    }

    private Stack<int> searchPositions { get; set; }

    private int searchFromPoint
    {
        get => _searchFromPoint;
        set
        {
            // Make sure we're never more than 1 away from being in range so if they
            // reverse direction, the first time they reverse they are back in range.
            if (value < 0)
                value = -1;
            else if (value >= _hs.Historys.Count)
                value = _hs.Historys.Count;
            _searchFromPoint = value;
        }
    }

    private StringBuilder toMatch { get; set; }
    private PSKeyInfo key { get; set; }

    public void ResetCurrentHistoryIndex(bool ToBegin = false)
    {
        const int InitialValue = 0;
        if (ToBegin)
        {
            _searcher.CurrentHistoryIndex = InitialValue;
        }
        else
        {
            _searcher.CurrentHistoryIndex = _hs?.Historys?.Count ?? InitialValue;
        }
    }

    public static HistorySearcher Singleton { get; }
    private Action<ConsoleKeyInfo?, object> function { get; set; }

    public int CurrentHistoryIndex
    {
        get => _currentHistoryIndex;
        set { _currentHistoryIndex = value; }
    }

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
        SaveCurrentLine();
        this.direction = direction;
        UpdateStatusLinePrompt(direction, AppendUnderline: true);
        _renderer.Render(); // Render prompt
        HandleUserInput();
        logger.Debug("CurrentHistoryIndex is " + CurrentHistoryIndex + ", When HandleUserInput is return.");
        _renderer.EmphasisInit();
        // Remove our status line, this will render
        _rl.ClearStatusMessage(true);
    }

    private static void UpdateStatusLinePrompt(int direction, bool IsFailedPrompt = false,
        bool AppendUnderline = false)
    {
        // Add a status line that will contain the search prompt and string
        if (IsFailedPrompt)
            _renderer.StatusLinePrompt = direction > 0 ? _failedForwardISearchPrompt : _failedBackwardISearchPrompt;
        else
            _renderer.StatusLinePrompt = direction > 0 ? _forwardISearchPrompt : _backwardISearchPrompt;

        if (AppendUnderline) _renderer.StatusBuffer.Append("_");
    }

    private void HandleUserInput()
    {
        InitData();
        while (true)
        {
            key = PSConsoleReadLine.ReadKey();
            _rl._dispatchTable.TryGetValue(key, out var handler);
            function = handler?.Action;

            if (function == ReverseSearchHistory)
            {
                direction = -1;
                UpdateHistory();
            }
            else if (function == ForwardSearchHistory)
            {
                direction = 1;
                UpdateHistory();
            }
            else if (function == PSConsoleReadLine.BackwardDeleteChar
                     || key == Keys.Backspace
                     || key == Keys.CtrlH)
            {
                HandleBackward();
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
                if (AddCharOfSearchKeyword())
                    break;
            }
        }
    }

    private void InitData()
    {
        logger.Debug("CurrentHistoryIndex is " + CurrentHistoryIndex +
                     ", When searchFromPoint is initializing in front of all code of HandleUserInput.");
        logger.Debug("searchPositions is " + ObjectDumper.Dump(searchPositions) +
                     ", When searchFromPoint is initializing in front of all code of HandleUserInput.");

        RecoverSearchFromPoint();
        searchPositions = new Stack<int>();
        searchPositions.Push(CurrentHistoryIndex);
        if (_rl.Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
        toMatch = new StringBuilder(64);
    }

    private void RecoverSearchFromPoint()
    {
        searchFromPoint = CurrentHistoryIndex;
    }

    public void SaveCurrentLine()
    {
        // We're called before any history operation - so it's convenient
        // to check if we need to load history from another sessions now.
        _hs.MaybeReadHistoryFile();

        _hs.AnyHistoryCommandCount += 1;
        if (_savedCurrentLine.CommandLine == null)
        {
            _savedCurrentLine.CommandLine = _rl.buffer.ToString();
            _savedCurrentLine._edits = _rl._edits;
            _savedCurrentLine._undoEditIndex = _rl._undoEditIndex;
            _savedCurrentLine._editGroupStart = _rl._editGroupStart;
        }
    }

    public void ClearSavedCurrentLine()
    {
        _savedCurrentLine.CommandLine = null;
        _savedCurrentLine._edits = null;
        _savedCurrentLine._undoEditIndex = 0;
        _savedCurrentLine._editGroupStart = -1;
    }

    public void UpdateFromHistory(HistoryMoveCursor moveCursor)
    {
        string line;
        if (CurrentHistoryIndex == _hs.Historys.Count)
        {
            line = _savedCurrentLine.CommandLine;
            _rl._edits = new List<EditItem>(_savedCurrentLine._edits);
            _rl._undoEditIndex = _savedCurrentLine._undoEditIndex;
            _rl._editGroupStart = _savedCurrentLine._editGroupStart;
        }
        else
        {
            line = _hs.Historys[CurrentHistoryIndex].CommandLine;
            _rl._edits = new List<EditItem>(_hs.Historys[CurrentHistoryIndex]._edits);
            _rl._undoEditIndex = _hs.Historys[CurrentHistoryIndex]._undoEditIndex;
            _rl._editGroupStart = _hs.Historys[CurrentHistoryIndex]._editGroupStart;
        }

        _rl.buffer.Clear();
        _rl.buffer.Append(line);

        switch (moveCursor)
        {
            case HistoryMoveCursor.ToEnd:
                _renderer.Current = Math.Max(0, _rl.buffer.Length + PSConsoleReadLine.ViEndOfLineFactor);
                break;
            case HistoryMoveCursor.ToBeginning:
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

    private int HandleBackward()
    {
        if (toMatch.Length > 0)
        {
            toMatch.Remove(toMatch.Length - 1, 1);
            _renderer.StatusBuffer.Remove(_renderer.StatusBuffer.Length - 2, 1);
            searchPositions.Pop();
            searchFromPoint = searchPositions.Peek();
            SaveSearchFromPoint();
            var moveCursor = _rl.Options.HistorySearchCursorMovesToEnd
                ? HistoryMoveCursor.ToEnd
                : HistoryMoveCursor.DontMove;
            UpdateFromHistory(moveCursor);

            if (_hs.HashedHistory != null)
                // Remove any entries with index < searchFromPoint because
                // we are starting the search from this new index - we always
                // want to find the latest entry that matches the search string
                foreach (var pair in _hs.HashedHistory.ToArray())
                    if (pair.Value < searchFromPoint)
                        _hs.HashedHistory.Remove(pair.Key);

            var startIndex = GetStartIndex(_rl.buffer.ToString());
            if (startIndex >= 0)
                UpdateBuffer(startIndex);
        }
        else
        {
            PSConsoleReadLine.Ding();
        }

        return searchFromPoint;
    }

    private void UpdateHistory()
    {
        FindInHistory(startIndex =>
        {
            UpdateStatusLinePrompt(direction);
            SetEmphasisData(startIndex);
            SaveSearchFromPoint();
            var moveCursor = _rl.Options.HistorySearchCursorMovesToEnd
                ? HistoryMoveCursor.ToEnd
                : HistoryMoveCursor.DontMove;
            UpdateFromHistory(moveCursor);
        }, () =>
        {
            _renderer.EmphasisInit();
            UpdateStatusLinePrompt(direction, true);
            _renderer.Render();
        });
    }

    private void FindInHistory(Action<int> whenFound, Action whenNotFound = default)
    {
        searchFromPoint += direction;
        for (; searchFromPoint >= 0 && searchFromPoint < _hs.Historys.Count; searchFromPoint += direction)
        {
            var line = _hs.Historys[searchFromPoint].CommandLine;
            var startIndex = GetStartIndex(line);
            if (startIndex >= 0)
            {
                if (_rl.Options.HistoryNoDuplicates)
                {
                    if (!_hs.HashedHistory.TryGetValue(line, out var index))
                        _hs.HashedHistory.Add(line, searchFromPoint);
                    else if (index != searchFromPoint) continue;
                }

                whenFound?.Invoke(startIndex);
                return;
            }
        }

        whenNotFound?.Invoke();
    }

    private void SaveSearchFromPoint()
    {
        CurrentHistoryIndex = searchFromPoint;
    }

    private int GetStartIndex(string line)
    {
        return line.IndexOf(toMatch.ToString(), _rl.Options.HistoryStringComparison);
    }


    private bool AddCharOfSearchKeyword()
    {
        var toAppend = key.KeyChar;
        if (char.IsControl(toAppend))
        {
            _rl.PrependQueuedKeys(key);
            return true;
        }

        toMatch.Append(toAppend);
        _renderer.StatusBuffer.Insert(_renderer.StatusBuffer.Length - 1, toAppend);
        Update();
        return false;
    }

    private void Update()
    {
        var startIndex = GetStartIndex(_rl.buffer.ToString());
        if (startIndex >= 0)
            UpdateBuffer(startIndex);
        else
            UpdateHistory();
        searchPositions.Push(CurrentHistoryIndex);
    }

    private void UpdateBuffer(int startIndex)
    {
        UpdateStatusLinePrompt(direction);
        Emphasis(startIndex);
    }

    private void Emphasis(int startIndex)
    {
        SetEmphasisData(startIndex);
        _renderer.Render();
    }

    private void SetEmphasisData(int startIndex)
    {
        _renderer.Current = startIndex;
        _renderer.EmphasisStart = startIndex;
        _renderer.EmphasisLength = toMatch.Length;
    }

    private static void GoToEndOfHistory()
    {
        _searcher.ResetCurrentHistoryIndex();
        _searcher.UpdateFromHistory(HistoryMoveCursor.ToEnd);
    }
}