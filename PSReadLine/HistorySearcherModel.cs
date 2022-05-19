using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine;

public class HistorySearcherModel
{
    // When cycling through history, the current line (not yet added to history)
    // is saved here so it can be restored.
    private readonly HistoryItem _savedCurrentLine = new();
    public int direction;
    private int _currentHistoryIndex;
    private int _searchFromPoint;

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

    public Stack<int> searchPositions { get; private set; }
    public StringBuilder toMatch { get; private set; }

    static HistorySearcherModel()
    {
        Singleton = new HistorySearcherModel();
    }

    public static HistorySearcherModel Singleton { get; }

    public int CurrentHistoryIndex
    {
        get => _currentHistoryIndex;
        set => _currentHistoryIndex = value;
    }

    public void ResetCurrentHistoryIndex(bool ToBegin = false)
    {
        const int InitialValue = 0;
        if (ToBegin)
            SearcherReadLine.CurrentHistoryIndex = InitialValue;
        else
            SearcherReadLine.CurrentHistoryIndex = _hs?.Historys?.Count ?? InitialValue;
    }

    public void InitData()
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

    public void SearchInHistory(Action<int> whenFound, Action whenNotFound = default)
    {
        searchFromPoint = searchFromPoint + direction;
        for (;
             searchFromPoint >= 0 && searchFromPoint < _hs.Historys.Count;
             searchFromPoint = searchFromPoint + direction)
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

    public void SaveToBuffer()
    {
        var historyItem = CurrentHistoryIndex == _hs.Historys.Count
            ? _savedCurrentLine
            : _hs.Historys[CurrentHistoryIndex];

        _rl._edits = new List<EditItem>(historyItem._edits);
        _rl._undoEditIndex = historyItem._undoEditIndex;
        _rl._editGroupStart = historyItem._editGroupStart;

        _rl.buffer.Clear();
        _rl.buffer.Append(historyItem.CommandLine);
    }
    public void Backward(Action whenSuccessful, Action whenFailed)
    {
        if (toMatch.Length > 0)
        {
            toMatch.Remove(toMatch.Length - 1, 1);
            _renderer.StatusBuffer.Remove(_renderer.StatusBuffer.Length - 2, 1);
            searchPositions.Pop();
            int val = searchPositions.Peek();
            searchFromPoint = val;
            SaveSearchFromPoint();

            if (_hs.HashedHistory != null)
                // Remove any entries with index < searchFromPoint because
                // we are starting the search from this new index - we always
                // want to find the latest entry that matches the search string
                foreach (var pair in _hs.HashedHistory.ToArray())
                    if (pair.Value < searchFromPoint)
                        _hs.HashedHistory.Remove(pair.Key);
            whenSuccessful?.Invoke();
        }
        else
        {
            whenFailed?.Invoke();
        }
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

    public void SaveSearchFromPoint()
    {
        CurrentHistoryIndex = searchFromPoint;
    }

    public int GetStartIndex(string line)
    {
        return line.IndexOf(toMatch.ToString(), _rl.Options.HistoryStringComparison);
    }
}