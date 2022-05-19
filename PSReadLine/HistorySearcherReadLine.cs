using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine;

public class HistorySearcherReadLine
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

    static HistorySearcherReadLine()
    {
        Singleton = new HistorySearcherReadLine();
    }

    private static HistorySearcherModel _model => HistorySearcherModel.Singleton;

    private HistoryMoveCursor _moveCursor => _rl.Options.HistorySearchCursorMovesToEnd
        ? HistoryMoveCursor.ToEnd
        : HistoryMoveCursor.DontMove;

    private PSKeyInfo key { get; set; }

    public static HistorySearcherReadLine Singleton { get; }
    private Action<ConsoleKeyInfo?, object> function { get; set; }

    public int CurrentHistoryIndex
    {
        get => _model._currentHistoryIndex;
        set => _model._currentHistoryIndex = value;
    }

    public void ResetCurrentHistoryIndex(bool ToBegin = false)
    {
        const int InitialValue = 0;
        if (ToBegin)
            SearcherReadLine.CurrentHistoryIndex = InitialValue;
        else
            SearcherReadLine.CurrentHistoryIndex = _hs?.Historys?.Count ?? InitialValue;
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
        _model.direction = direction;
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
                _model.direction = -1;
                UpdateHistory();
            }
            else if (function == ForwardSearchHistory)
            {
                _model.direction = 1;
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
        logger.Debug("searchPositions is " + ObjectDumper.Dump(_model.searchPositions) +
                     ", When searchFromPoint is initializing in front of all code of HandleUserInput.");

        RecoverSearchFromPoint();
        _model.searchPositions =  new Stack<int>();
        _model.searchPositions.Push(CurrentHistoryIndex);
        if (_rl.Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
        _model.toMatch = new StringBuilder(64);
    }

    private void RecoverSearchFromPoint()
    {
        _model.searchFromPoint = CurrentHistoryIndex;
    }

    public void SaveCurrentLine()
    {
        // We're called before any history operation - so it's convenient
        // to check if we need to load history from another sessions now.
        _hs.MaybeReadHistoryFile();

        _hs.AnyHistoryCommandCount += 1;
        if (_model._savedCurrentLine.CommandLine == null)
        {
            _model._savedCurrentLine.CommandLine = _rl.buffer.ToString();
            _model._savedCurrentLine._edits = _rl._edits;
            _model._savedCurrentLine._undoEditIndex = _rl._undoEditIndex;
            _model._savedCurrentLine._editGroupStart = _rl._editGroupStart;
        }
    }

    public void ClearSavedCurrentLine()
    {
        _model._savedCurrentLine.CommandLine = null;
        _model._savedCurrentLine._edits = null;
        _model._savedCurrentLine._undoEditIndex = 0;
        _model._savedCurrentLine._editGroupStart = -1;
    }

    public void UpdateBufferFromHistory(HistoryMoveCursor moveCursor)
    {
        SaveToBuffer();

        _renderer.Current = moveCursor switch
        {
            HistoryMoveCursor.ToEnd => Math.Max(0, _rl.buffer.Length + PSConsoleReadLine.ViEndOfLineFactor),
            HistoryMoveCursor.ToBeginning => 0,
            _ => _renderer.Current > _rl.buffer.Length
                ? Math.Max(0, _rl.buffer.Length + RL.ViEndOfLineFactor)
                : _renderer.Current
        };

        using var _ = _rl._Prediction.DisableScoped();
        _renderer.Render();
    }

    private void SaveToBuffer()
    {
        var historyItem = CurrentHistoryIndex == _hs.Historys.Count
            ? _model._savedCurrentLine
            : _hs.Historys[CurrentHistoryIndex];

        _rl._edits = new List<EditItem>(historyItem._edits);
        _rl._undoEditIndex = historyItem._undoEditIndex;
        _rl._editGroupStart = historyItem._editGroupStart;

        _rl.buffer.Clear();
        _rl.buffer.Append(historyItem.CommandLine);
    }

    private void HandleBackward()
    {
        var whenSuccessful = () =>
        {
            UpdateBufferFromHistory(_moveCursor);
            var startIndex = GetStartIndex(_rl.buffer.ToString());
            if (startIndex >= 0)
                UpdateBuffer(startIndex);
        };

        Backward(whenSuccessful, PSConsoleReadLine.Ding);
    }

    private void Backward(Action whenSuccessful, Action whenFailed)
    {
        if (_model.toMatch.Length > 0)
        {
            _model.toMatch.Remove(_model.toMatch.Length - 1, 1);
            _renderer.StatusBuffer.Remove(_renderer.StatusBuffer.Length - 2, 1);
            _model.searchPositions.Pop();
            int val = _model.searchPositions.Peek();
            _model.searchFromPoint = val;
            SaveSearchFromPoint();

            if (_hs.HashedHistory != null)
                // Remove any entries with index < searchFromPoint because
                // we are starting the search from this new index - we always
                // want to find the latest entry that matches the search string
                foreach (var pair in _hs.HashedHistory.ToArray())
                    if (pair.Value < _model.searchFromPoint)
                        _hs.HashedHistory.Remove(pair.Key);
            whenSuccessful?.Invoke();
        }
        else
        {
            whenFailed?.Invoke();
        }
    }

    private void UpdateHistory()
    {
        FindInHistory(startIndex =>
        {
            UpdateStatusLinePrompt(_model.direction);
            SetEmphasisData(startIndex);
            SaveSearchFromPoint();
            UpdateBufferFromHistory(_moveCursor);
        }, () =>
        {
            _renderer.EmphasisInit();
            UpdateStatusLinePrompt(_model.direction, true);
            _renderer.Render();
        });
    }

    private void FindInHistory(Action<int> whenFound, Action whenNotFound = default)
    {
        _model.searchFromPoint = _model.searchFromPoint + _model.direction;
        for (; _model.searchFromPoint >= 0 && _model.searchFromPoint < _hs.Historys.Count; _model.searchFromPoint = _model.searchFromPoint + _model.direction)
        {
            var line = _hs.Historys[_model.searchFromPoint].CommandLine;
            var startIndex = GetStartIndex(line);
            if (startIndex >= 0)
            {
                if (_rl.Options.HistoryNoDuplicates)
                {
                    if (!_hs.HashedHistory.TryGetValue(line, out var index))
                        _hs.HashedHistory.Add(line, _model.searchFromPoint);
                    else if (index != _model.searchFromPoint) continue;
                }

                whenFound?.Invoke(startIndex);
                return;
            }
        }

        whenNotFound?.Invoke();
    }

    private void SaveSearchFromPoint()
    {
        CurrentHistoryIndex = _model.searchFromPoint;
    }

    private int GetStartIndex(string line)
    {
        return line.IndexOf(_model.toMatch.ToString(), _rl.Options.HistoryStringComparison);
    }


    private bool AddCharOfSearchKeyword()
    {
        var toAppend = key.KeyChar;
        if (char.IsControl(toAppend))
        {
            _rl.PrependQueuedKeys(key);
            return true;
        }

        _model.toMatch.Append(toAppend);
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
        _model.searchPositions.Push(CurrentHistoryIndex);
    }

    private void UpdateBuffer(int startIndex)
    {
        UpdateStatusLinePrompt(_model.direction);
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
        _renderer.EmphasisLength = _model.toMatch.Length;
    }

    private static void GoToEndOfHistory()
    {
        SearcherReadLine.ResetCurrentHistoryIndex();
        SearcherReadLine.UpdateBufferFromHistory(HistoryMoveCursor.ToEnd);
    }
}