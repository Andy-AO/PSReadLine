using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine.History;

public class InteractiveSearcherModel
{
    private int _searchFromPoint;
    public int direction;

    static InteractiveSearcherModel()
    {
        Singleton = new InteractiveSearcherModel();
    }

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

    public static InteractiveSearcherModel Singleton { get; }


    public void InitData()
    {
        RecoverSearchFromPoint();
        searchPositions = new Stack<int>();
        searchPositions.Push(_hs.CurrentHistoryIndex);
        if (_rl.Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
        toMatch = new StringBuilder(64);
    }

    public void SearchInHistory(Action<IEnumerable<EmphasisRange>> whenFound, Action whenNotFound = default)
    {
        searchFromPoint = searchFromPoint + direction;
        for (;
             searchFromPoint >= 0 && searchFromPoint < _hs.Historys.Count;
             searchFromPoint = searchFromPoint + direction)
        {
            var line = _hs.Historys[searchFromPoint].CommandLine;
            var ranges = GetRanges(line);
            if (ranges.Any())
            {
                if (_rl.Options.HistoryNoDuplicates)
                {
                    if (!_hs.HashedHistory.TryGetValue(line, out var index))
                        _hs.HashedHistory.Add(line, searchFromPoint);
                    else if (index != searchFromPoint) continue;
                }
                SaveSearchFromPoint();
                whenFound?.Invoke(ranges);
                return;
            }
        }

        whenNotFound?.Invoke();
    }

    public void Backward(Action whenSuccessful, Action whenFailed)
    {
        if (toMatch.Length > 0)
        {
            toMatch.Remove(toMatch.Length - 1, 1);
            _renderer.StatusBuffer.Remove(_renderer.StatusBuffer.Length - 2, 1);
            searchPositions.Pop();
            var val = searchPositions.Peek();
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
        searchFromPoint = _hs.CurrentHistoryIndex;
    }

    private void SaveSearchFromPoint()
    {
        _hs.CurrentHistoryIndex = searchFromPoint;
    }

    public IEnumerable<EmphasisRange> GetRanges(string line)
    {
        if (_rl.Options.InteractiveHistorySearchStrategy == SearchStrategy.MultiKeyword)
            return MultiKeyword(line);
        return SingleKeyword(line);
    }

    private IEnumerable<EmphasisRange> MultiKeyword(string line)
    {
        var keywords = GetKeywords(toMatch.ToString());
        var result = keywords.Select(k =>
        {
            var i = line.IndexOf(k, _rl.Options.HistoryStringComparison);
            if (i > -1) return new EmphasisRange(i, k.Length);
            return EmphasisRange.Empty;
        }).ToArray();
        if (result.Any(r => r.IsEmpty))
            return Array.Empty<EmphasisRange>();
        return result;
    }

    private IEnumerable<EmphasisRange> SingleKeyword(string line)
    {
        var keywords = new[] {toMatch.ToString()};
        return keywords.Select(k =>
        {
            var i = line.IndexOf(k, _rl.Options.HistoryStringComparison);
            if (i > -1) return new EmphasisRange(i, k.Length);

            return EmphasisRange.Empty;
        }).Where(r => !r.IsEmpty);
    }


    private IEnumerable<string> GetKeywords(string toMatchString)
    {
        var keywords = toMatchString.Trim().Split(' ').Where(s => s != "").Distinct();
        return keywords;
    }
}