using System.Linq;
using System;
using System.Collections.Generic;


namespace Microsoft.PowerShell.PSReadLine;

public readonly record struct EmphasisRange
{
    private static readonly int MinimumValue = -1;
    private readonly int _start;
    private readonly int _end;
    public int Start => _start;
    public int End => _end;

    public EmphasisRange(int start, int length)
    {
        _start = start;
        _end = start + length;
        IsValid();
    }

    private void IsValid()
    {
        var state = $"\nstart is {_start}, end is {_end}.";
        if (_start > _end) throw new ArgumentException("The start must be less than the end." + state);

        if (_start < MinimumValue || _end < MinimumValue)
            throw new ArgumentException($"Index must be greater than or equal to {MinimumValue}" + state);
    }

    public bool IsIn(int index) => _start <= index && index < _end;
}

public static class Emphasis
{
    private static List<EmphasisRange> _ranges = new();


    public static bool ToEmphasize(int index)
    {
        foreach (var r in _ranges)
        {
            if (r.IsIn(index))
            {
                return true;
            }
        }

        return false;
    }


    internal static void EmphasisInit()
    {
        _ranges = new();
    }

    public static void SetEmphasisData(int startIndex, int length, CursorPosition p)
    {
        int endIndex = startIndex + length;
        _renderer.Current = p switch
        {
            CursorPosition.Start => startIndex,
            CursorPosition.End => endIndex,
            _ => throw new ArgumentException(@"Invalid enum value for CursorPosition", nameof(p))
        };
        _ranges = new List<EmphasisRange> { new(startIndex, length) };
    }

    public static bool IsNotEmphasisEmpty() => _ranges.Any();

    public static void SetEmphasisData(IEnumerable<EmphasisRange> ranges)
    {
        _ranges = ranges.ToList();
    }

}