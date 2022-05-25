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
    public static bool ToEmphasize(int index)
    {
        return index >= _renderer.EmphasisStart &&
               index < _renderer.EmphasisStart + _renderer.EmphasisLength;
    }
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
        get => _model.CurrentHistoryIndex;
        set => _model.CurrentHistoryIndex = value;
    }

    public void ResetCurrentHistoryIndex(bool ToBegin = false)
    {
        _model.ResetCurrentHistoryIndex(ToBegin);
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
        _model.SaveCurrentLine();
        _model.direction = direction;
        UpdateStatusLinePrompt(direction, AppendUnderline: true);
        _renderer.Render(); // Render prompt
        HandleUserInput();
        logger.Debug("CurrentHistoryIndex is " + _model.CurrentHistoryIndex + ", When HandleUserInput is return.");
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
        _model.InitData();
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

    public void SaveCurrentLine()
    {
        _model.SaveCurrentLine();
    }

    public void ClearSavedCurrentLine()
    {
        _model.ClearSavedCurrentLine();
    }

    public void UpdateBufferFromHistory(HistoryMoveCursor moveCursor)
    {
        _model.SaveToBuffer();

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

    private void HandleBackward()
    {
        var whenSuccessful = () =>
        {
            UpdateBufferFromHistory(_moveCursor);
            var startIndex = _model.GetStartIndex(_rl.buffer.ToString());
            if (startIndex >= 0)
                UpdateBuffer(startIndex);
        };

        Action whenFailed = PSConsoleReadLine.Ding;
        _model.Backward(whenSuccessful,whenFailed);
    }

    private void UpdateHistory()
    {
        Action whenNotFound = () =>
        {
            _renderer.EmphasisInit();
            UpdateStatusLinePrompt(_model.direction, true);
            _renderer.Render();
        };
        _model.SearchInHistory(startIndex =>
        {
            UpdateStatusLinePrompt(_model.direction);
            SetEmphasisData(startIndex);
            _model.SaveSearchFromPoint();
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
        var startIndex = _model.GetStartIndex(_rl.buffer.ToString());
        if (startIndex >= 0)
            UpdateBuffer(startIndex);
        else
            UpdateHistory();
        _model.searchPositions.Push(_model.CurrentHistoryIndex);
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
        _model.ResetCurrentHistoryIndex(false);
        SearcherReadLine.UpdateBufferFromHistory(HistoryMoveCursor.ToEnd);
    }
}