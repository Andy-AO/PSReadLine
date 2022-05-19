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
    private int direction;
    private int _currentHistoryIndex;
    private int _searchFromPoint;
}