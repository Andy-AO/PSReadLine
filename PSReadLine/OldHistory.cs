﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.PowerShell.PSReadLine;

namespace Microsoft.PowerShell
{
    /// <summary>
    ///     FNV-1a hashing algorithm: http://www.isthe.com/chongo/tech/comp/fnv/#FNV-1a
    /// </summary>
    internal class FNV1a32Hash
    {
        // FNV-1a algorithm parameters: http://www.isthe.com/chongo/tech/comp/fnv/#FNV-param
        private const uint FNV32_PRIME = 16777619;
        private const uint FNV32_OFFSETBASIS = 2166136261;

        internal static uint ComputeHash(string input)
        {
            char ch;
            uint hash = FNV32_OFFSETBASIS, lowByte, highByte;

            for (var i = 0; i < input.Length; i++)
            {
                ch = input[i];
                lowByte = (uint) (ch & 0x00FF);
                hash = unchecked((hash ^ lowByte) * FNV32_PRIME);

                highByte = (uint) (ch >> 8);
                hash = unchecked((hash ^ highByte) * FNV32_PRIME);
            }

            return hash;
        }
    }

    public partial class PSConsoleReadLine
    {
        private static History _hs = History.Singleton;

        public void HistorySearch(int direction)
        {
            if (_hs.SearchHistoryCommandCount == 0)
            {
                if (_renderer.LineIsMultiLine())
                {
                    MoveToLine(direction);
                    return;
                }

                _hs.SearchHistoryPrefix = buffer.ToString(0, _renderer.Current);
                _renderer.EmphasisStart = 0;
                _renderer.EmphasisLength = _renderer.Current;
                if (Options.HistoryNoDuplicates) _hs.HashedHistory = new Dictionary<string, int>();
            }

            _hs.SearchHistoryCommandCount += 1;

            var count = Math.Abs(direction);
            direction = direction < 0 ? -1 : +1;
            var newHistoryIndex = _hs.CurrentHistoryIndex;
            while (count > 0)
            {
                newHistoryIndex += direction;
                if (newHistoryIndex < 0 || newHistoryIndex >= _hs.Historys.Count) break;

                if (_hs.Historys[newHistoryIndex].FromOtherSession && _hs.SearchHistoryPrefix.Length == 0) continue;

                var line = _hs.Historys[newHistoryIndex].CommandLine;
                if (line.StartsWith(_hs.SearchHistoryPrefix, Options.HistoryStringComparison))
                {
                    if (Options.HistoryNoDuplicates)
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
                _renderer.Current = _renderer.EmphasisLength;
                _hs.CurrentHistoryIndex = newHistoryIndex;
                var moveCursor = InViCommandMode()
                    ? History.HistoryMoveCursor.ToBeginning
                    : Options.HistorySearchCursorMovesToEnd
                        ? History.HistoryMoveCursor.ToEnd
                        : History.HistoryMoveCursor.DontMove;
                _hs.UpdateFromHistory(moveCursor);
            }
        }
    }
}