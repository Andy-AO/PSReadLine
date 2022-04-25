/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private void WriteBlankLines(int count)
        {
            RLConsole.BlankRestOfLine();
            for (int i = 1; i < count; i++)
            {
                RLConsole.Write("\n");
                RLConsole.BlankRestOfLine();
            }
        }

        private void WriteBlankLines(int top, int count)
        {
            var savedCursorLeft = RLConsole.CursorLeft;
            var savedCursorTop = RLConsole.CursorTop;

            RLConsole.SetCursorPosition(left: 0, top);
            WriteBlankLines(count);
            RLConsole.SetCursorPosition(savedCursorLeft, savedCursorTop);
        }

        private void WriteBlankRestOfLine(int left, int top)
        {
            var savedCursorLeft = RLConsole.CursorLeft;
            var savedCursorTop = RLConsole.CursorTop;

            RLConsole.SetCursorPosition(left, top);
            RLConsole.BlankRestOfLine();
            RLConsole.SetCursorPosition(savedCursorLeft, savedCursorTop);
        }

        internal static string Spaces(int cnt)
        {
            return cnt < Renderer.SpacesArr.Length
                ? (Renderer.SpacesArr[cnt] ?? (Renderer.SpacesArr[cnt] = new string(' ', cnt)))
                : new string(' ', cnt);
        }

        private static string SubstringByCells(string text, int countOfCells)
        {
            return SubstringByCells(text, 0, countOfCells);
        }

        private static string SubstringByCells(string text, int start, int countOfCells)
        {
            int length = SubstringLengthByCells(text, start, countOfCells);
            return length == 0 ? string.Empty : text.Substring(start, length);
        }

        private static int SubstringLengthByCells(string text, int countOfCells)
        {
            return SubstringLengthByCells(text, 0, countOfCells);
        }

        private static int SubstringLengthByCells(string text, int start, int countOfCells)
        {
            int cellLength = 0;
            int charLength = 0;

            for (int i = start; i < text.Length; i++)
            {
                cellLength += _renderer.LengthInBufferCells(text[i]);

                if (cellLength > countOfCells)
                {
                    return charLength;
                }

                charLength++;

                if (cellLength == countOfCells)
                {
                    return charLength;
                }
            }

            return charLength;
        }

        private static int SubstringLengthByCellsFromEnd(string text, int start, int countOfCells)
        {
            int cellLength = 0;
            int charLength = 0;

            for (int i = start; i >= 0; i--)
            {
                cellLength += _renderer.LengthInBufferCells(text[i]);

                if (cellLength > countOfCells)
                {
                    return charLength;
                }

                charLength++;

                if (cellLength == countOfCells)
                {
                    return charLength;
                }
            }

            return charLength;
        }
    }
}