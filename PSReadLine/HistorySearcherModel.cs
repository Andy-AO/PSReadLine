using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine;

public class HistorySearcherModel
{
    // When cycling through history, the current line (not yet added to history)
    // is saved here so it can be restored.
    public readonly HistoryItem _savedCurrentLine = new();
    public int direction;
    public int _currentHistoryIndex;
    private int _searchFromPoint;
    public int searchFromPoint
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

    public Stack<int> searchPositions { get; set; }
    public StringBuilder toMatch { get; set; }

    static HistorySearcherModel()
    {
        Singleton = new HistorySearcherModel();
    }

    public static HistorySearcherModel Singleton { get; }
}