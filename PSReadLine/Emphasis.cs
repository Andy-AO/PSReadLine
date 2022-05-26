using System;

namespace Microsoft.PowerShell.PSReadLine;

public static class Emphasis
{
    public static bool ToEmphasize(int index)
    {
        return index >= EmphasisStart &&
               index < EmphasisStart + EmphasisLength;
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