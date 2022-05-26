using System.Linq;
using System;
using System.Collections.Generic;


namespace Microsoft.PowerShell.PSReadLine;

public readonly record struct EmphasisRange
{
    private static readonly int MinimumValue = -1;
    public readonly int End;
    public readonly int Start;

    public EmphasisRange(int start, int end)
    {
        End = end;
        Start = start;
        IsValid(start, end);
    }

    public void IsValid()
    {
        IsValid(Start, End);
    }

    private void IsValid(int start, int end)
    {
        var state = $"\nstart is {start}, end is {end}.";
        if (start > end) throw new ArgumentException("The start must be less than the end." + state);

        if (start < MinimumValue || end < MinimumValue)
            throw new ArgumentException($"Index must be greater than or equal to {MinimumValue}" + state);
    }

    public bool IsIn(int index)
    {
        return Start <= index && index < End;
    }
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
        _ranges = new List<EmphasisRange> {new(startIndex, endIndex)};
    }

    public static bool IsNotEmphasisEmpty() => _ranges.Any();
}