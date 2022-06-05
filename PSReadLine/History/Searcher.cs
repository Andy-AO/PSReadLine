using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine.History;

public class Searcher
{
    public static Searcher Singleton { get; }
    public string SearchHistoryPrefix { get; set; }
    public int SearchHistoryCommandCount { get; set; }
    static Searcher()
    {
        Singleton = new Searcher();
    }

    private static void SetCursorPosition(int startIndex, int length, CursorPosition p)
    {
        var endIndex = startIndex + length;
        _renderer.Current = p switch
        {
            CursorPosition.Start => startIndex,
            CursorPosition.End => endIndex,
            _ => throw new ArgumentException(@"Invalid enum value for CursorPosition", nameof(p))
        };
    }

    private void HistorySearch(int direction)
    {
        CurrentLineCache.Cache();
        if (searcher.SearchHistoryCommandCount == 0)
        {
            if (_renderer.LineIsMultiLine())
            {
                _rl.MoveToLine(direction);
                return;
            }

            searcher.SearchHistoryPrefix = _rl.buffer.ToString(0, _renderer.Current);

            EP.SetEmphasisData(new EmphasisRange[] {new(0, _renderer.Current)});
            SetCursorPosition(0, _renderer.Current, CursorPosition.End);

            if (_rl.Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
        }

        searcher.SearchHistoryCommandCount = searcher.SearchHistoryCommandCount + 1;

        var count = Math.Abs(direction);
        direction = direction < 0 ? -1 : +1;
        var newHistoryIndex = _hs.CurrentHistoryIndex;
        while (count > 0)
        {
            newHistoryIndex += direction;
            if (newHistoryIndex < 0 || newHistoryIndex >= _hs.Historys.Count) break;

            if (_hs.Historys[newHistoryIndex].FromOtherSession && searcher.SearchHistoryPrefix.Length == 0) continue;

            var line = _hs.Historys[newHistoryIndex].CommandLine;
            if (line.StartsWith(searcher.SearchHistoryPrefix, _rl.Options.HistoryStringComparison))
            {
                if (_rl.Options.HistoryNoDuplicates)
                {
                    if (!_hs.HashedHistory.TryGetValue(line, out var index))
                    {
                        _hs.HashedHistory.Add(line, newHistoryIndex);
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

        if (newHistoryIndex >= 0 && newHistoryIndex <= _hs.Historys.Count)
        {
            // Set '_current' back to where it was when starting the first search, because
            // it might be changed during the rendering of the last matching history command.
            // _renderer.Current = HistorySearcherReadLine.EmphasisLength;
            _hs.CurrentHistoryIndex = newHistoryIndex;
            var moveCursor = RL.InViCommandMode()
                ? InteractiveSearcherReadLine.HistoryMoveCursor.ToBeginning
                : _rl.Options.HistorySearchCursorMovesToEnd
                    ? InteractiveSearcherReadLine.HistoryMoveCursor.ToEnd
                    : InteractiveSearcherReadLine.HistoryMoveCursor.DontMove;
            SearcherReadLine.UpdateBufferFromHistory(moveCursor);
        }
    }

    /// <summary>
    ///     Replace the current input with the 'next' item from PSReadLine history
    ///     that matches the characters between the start and the input and the cursor.
    /// </summary>
    public static void HistorySearchForward(ConsoleKeyInfo? key = null, object arg = null)
    {
        PSConsoleReadLine.TryGetArgAsInt(arg, out var numericArg, +1);
        if (RL.UpdateListSelection(numericArg)) return;
        searcher.HistorySearch(numericArg);
    }


    /// <summary>
    ///     Replace the current input with the 'previous' item from PSReadLine history
    ///     that matches the characters between the start and the input and the cursor.
    /// </summary>
    public static void HistorySearchBackward(ConsoleKeyInfo? key = null, object arg = null)
    {
        RL.TryGetArgAsInt(arg, out var numericArg, -1);
        if (numericArg > 0) numericArg = -numericArg;
        if (RL.UpdateListSelection(numericArg)) return;
        searcher.HistorySearch(numericArg);
    }

}

