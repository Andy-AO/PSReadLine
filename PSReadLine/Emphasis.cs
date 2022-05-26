using System;
using System.Collections.Generic;
using System.Linq;


namespace Microsoft.PowerShell.PSReadLine;

public record EmphasisRange
{
    private static readonly int MinimumValue = -1;
    public readonly int End;
    public readonly int Start;

    public EmphasisRange(int start, int end)
    {
        IsValid(start, end);
        End = end;
        Start = start;
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
        return Start <= index && index <= End;
    }
}

public static class Emphasis
{
    private static List<EmphasisRange> _ranges = new();

    public static bool ToEmphasize(int index)
    {
        return index >= EmphasisStart &&
               index < EmphasisStart + EmphasisLength;
    }

    private static List<EmphasisRange> Ranges
    {
        get => _ranges.ToList();
        set => _ranges = value.ToList();
    }

    internal static void EmphasisInit()
    {
        EmphasisStart = -1;
        EmphasisLength = 0;
    }

    public static void SetEmphasisData(int startIndex, int length, CursorPosition p)
    {
        _renderer.Current = p switch
        {
            CursorPosition.Start => startIndex,
            CursorPosition.End => startIndex + length,
            _ => throw new ArgumentException(@"Invalid enum value for CursorPosition", nameof(p))
        };

        EmphasisStart = startIndex;
        EmphasisLength = length;
    }

    public static bool IsEmphasisDataValid() => EmphasisStart >= 0;
    private static int EmphasisStart { get; set; }
    private static int EmphasisLength { get; set; }
}