using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PowerShell.PSReadLine.History;

public class InteractiveSearcherReadLine
{
    public enum HistoryMoveCursor
    {
        ToEnd,
        ToBeginning,
        DontMove
    }

    private const string _forwardISearchPrompt = "fwd-i-search";
    private const string _backwardISearchPrompt = "bck-i-search";
    private const string _failedForwardISearchPrompt = "failed-fwd-i-search";
    private const string _failedBackwardISearchPrompt = "failed-bck-i-search";

    static InteractiveSearcherReadLine()
    {
        Singleton = new InteractiveSearcherReadLine();
    }

    private static InteractiveSearcherModel _model => InteractiveSearcherModel.Singleton;

    private HistoryMoveCursor _moveCursor => _rl.Options.HistorySearchCursorMovesToEnd
        ? HistoryMoveCursor.ToEnd
        : HistoryMoveCursor.DontMove;

    private PSKeyInfo key { get; set; }

    public static InteractiveSearcherReadLine Singleton { get; }
    private Action<ConsoleKeyInfo?, object> function { get; set; }

    private static string ForwardISearchPrompt => _forwardISearchPrompt + PromptSuffix;

    private static string PromptSuffix
    {
        get
        {
            var result = _rl.Options.InteractiveHistorySearchStrategy switch
            {
                SearchStrategy.MultiKeyword => "(MultiKeyword)",
                _ => ""
            };
            return result + ": ";
        }
    }

    private static string BackwardISearchPrompt => _backwardISearchPrompt + PromptSuffix;

    private static string FailedForwardISearchPrompt => _failedForwardISearchPrompt + PromptSuffix;

    private static string FailedBackwardISearchPrompt => _failedBackwardISearchPrompt + PromptSuffix;

    /// <summary>
    ///     Perform an incremental backward search through history.
    /// </summary>
    public static void ReverseSearchHistory(ConsoleKeyInfo? key = null, object arg = null)
    {
        _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.SingleKeyword;
        Singleton.InteractiveHistorySearch(-1);
    }

    public static void ReverseSearchHistoryMultiKeyword(ConsoleKeyInfo? key = null, object arg = null)
    {
        _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.MultiKeyword;
        Singleton.InteractiveHistorySearch(-1);
    }

    /// <summary>
    ///     Perform an incremental forward search through history.
    /// </summary>
    public static void ForwardSearchHistory(ConsoleKeyInfo? key = null, object arg = null)
    {
        _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.SingleKeyword;
        Singleton.InteractiveHistorySearch(+1);
    }

    public static void ForwardSearchHistoryMultiKeyword(ConsoleKeyInfo? key = null, object arg = null)
    {
        _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.MultiKeyword;
        Singleton.InteractiveHistorySearch(+1);
    }

    //start
    private void InteractiveHistorySearch(int direction)
    {
        using var _ = _rl._Prediction.DisableScoped();
        CurrentLineCache.Cache();
        _model.direction = direction;
        UpdateStatusLinePrompt(direction, AppendUnderline: true);
        _renderer.Render(); // Render prompt
        HandleUserInput();

        EP.EmphasisInit();
        // Remove our status line, this will render
        _rl.ClearStatusMessage(true);
    }

    private static void UpdateStatusLinePrompt(int direction, bool IsFailedPrompt = false,
        bool AppendUnderline = false)
    {
        // Add a status line that will contain the search prompt and string
        if (IsFailedPrompt)
            _renderer.StatusLinePrompt = direction > 0 ? FailedForwardISearchPrompt : FailedBackwardISearchPrompt;
        else
            _renderer.StatusLinePrompt = direction > 0 ? ForwardISearchPrompt : BackwardISearchPrompt;

        if (AppendUnderline) _renderer.StatusBuffer.Append("_");
    }

    private void HandleUserInput()
    {
        _model.InitData();
        while (true)
        {
            key = PSConsoleReadLine.ReadKey();
            _rl._dispatchTable.TryGetValue(key, out var handler);
            function = handler?.Action;

            if (function == ReverseSearchHistory)
            {
                _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.SingleKeyword;
                _model.direction = -1;
                UpdateHistory();
            }

            else if (function == ReverseSearchHistoryMultiKeyword)
            {
                _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.MultiKeyword;
                _model.direction = -1;
                UpdateHistory();
            }
            else if (function == ForwardSearchHistory)
            {
                _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.SingleKeyword;
                _model.direction = 1;
                UpdateHistory();
            }
            else if (function == ForwardSearchHistoryMultiKeyword)
            {
                _rl.Options.InteractiveHistorySearchStrategy = SearchStrategy.MultiKeyword;
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
        if (_hs.CurrentHistoryIndex == _hs.Historys.Count)
            CurrentLineCache.Restore();
        else
            CurrentLineCache.Restore(_hs.Historys[_hs.CurrentHistoryIndex]);
    }

    private void HandleBackward()
    {
        var whenSuccessful = () =>
        {
            UpdateBufferFromHistory(_moveCursor);
            var ranges = _model.GetRanges(_rl.buffer.ToString());
            if (ranges.Any())
                UpdateBuffer(ranges);
        };

        Action whenFailed = PSConsoleReadLine.Ding;
        _model.Backward(whenSuccessful, whenFailed);
    }

    private void UpdateHistory()
    {
        Action whenNotFound = () =>
        {
            EP.EmphasisInit();
            UpdateStatusLinePrompt(_model.direction, true);
            _renderer.Render();
        };
        _model.SearchInHistory(ranges =>
        {
            UpdateStatusLinePrompt(_model.direction);
            SetRenderData(ranges, CursorPosition.Start);
            UpdateBufferFromHistory(_moveCursor);
        }, whenNotFound);
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
        var ranges = _model.GetRanges(_rl.buffer.ToString());
        if (ranges.Any())
            UpdateBuffer(ranges);
        else
            UpdateHistory();
        _model.searchPositions.Push(_hs.CurrentHistoryIndex);
    }

    private void UpdateBuffer(IEnumerable<EmphasisRange> ranges)
    {
        UpdateStatusLinePrompt(_model.direction);
        Emphasis(ranges);
    }


    private void Emphasis(IEnumerable<EmphasisRange> ranges)
    {
        SetRenderData(ranges, CursorPosition.Start);
        _renderer.Render();
    }

    private void SetRenderData(IEnumerable<EmphasisRange> ranges, CursorPosition p)
    {
        EP.SetEmphasisData(ranges);
        _renderer.Current = p switch
        {
            CursorPosition.Start => ranges.First().Start,
            CursorPosition.End => ranges.Last().End,
            _ => throw new ArgumentException(@"Invalid enum value for CursorPosition", nameof(p))
        };
    }

    private static void GoToEndOfHistory()
    {
        _hs.ResetCurrentHistoryIndex(false);
        SearcherReadLine.UpdateBufferFromHistory(HistoryMoveCursor.ToEnd);
    }
}

public enum CursorPosition
{
    Start,
    End
}