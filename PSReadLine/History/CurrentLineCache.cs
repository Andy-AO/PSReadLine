using System.Collections.Generic;

namespace Microsoft.PowerShell.PSReadLine.History;

public static class CurrentLineCache
{
    // When cycling through history, the current line (not yet added to history)
    // is saved here so it can be restored.
    private static readonly LineInfo _cache = new();

    public static void Restore() => Restore(_cache);
    public static void Restore(LineInfo line)
    {
        _rl._edits = new List<EditItem>(line._edits);
        _rl._undoEditIndex = line._undoEditIndex;
        _rl._editGroupStart = line._editGroupStart;

        _rl.buffer.Clear();
        _rl.buffer.Append(line.CommandLine);
    }
    public static void Clear()
    {
        _cache.CommandLine = null;
        _cache._edits = null;
        _cache._undoEditIndex = 0;
        _cache._editGroupStart = -1;
    }
    public static void Cache()
    {
        // We're called before any history operation - so it's convenient
        // to check if we need to load history from another sessions now.
        _hs.MaybeReadHistoryFile();

        _hs.AnyHistoryCommandCount += 1;
        if (_cache.CommandLine == null)
        {
            _cache.CommandLine = _rl.buffer.ToString();
            _cache._edits = _rl._edits;
            _cache._undoEditIndex = _rl._undoEditIndex;
            _cache._editGroupStart = _rl._editGroupStart;
        }
    }
}