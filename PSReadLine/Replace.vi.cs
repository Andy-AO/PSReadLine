/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Text;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private static void ViReplaceUntilEsc(ConsoleKeyInfo? key, object arg)
        {
            if (Singleton._current >= Singleton._buffer.Length)
            {
                Ding();
                return;
            }

            int startingCursor = Singleton._current;
            StringBuilder deletedStr = new StringBuilder();

            var nextKey = ReadKey();
            while (nextKey != Keys.Escape && nextKey != Keys.Enter)
            {
                if (nextKey == Keys.Backspace)
                {
                    if (Singleton._current == startingCursor)
                    {
                        Ding();
                    }
                    else
                    {
                        if (deletedStr.Length == Singleton._current - startingCursor)
                        {
                            Singleton._buffer[Singleton._current - 1] = deletedStr[deletedStr.Length - 1];
                            deletedStr.Remove(deletedStr.Length - 1, 1);
                        }
                        else
                        {
                            Singleton._buffer.Remove(Singleton._current - 1, 1);
                        }
                        Singleton._current--;
                        Singleton.Render();
                    }
                }
                else
                {
                    if (Singleton._current >= Singleton._buffer.Length)
                    {
                        Singleton._buffer.Append(nextKey.KeyChar);
                    }
                    else
                    {
                        deletedStr.Append(Singleton._buffer[Singleton._current]);
                        Singleton._buffer[Singleton._current] = nextKey.KeyChar;
                    }
                    Singleton._current++;
                    Singleton.Render();
                }
                nextKey = ReadKey();
            }

            if (Singleton._current > startingCursor)
            {
                Singleton.StartEditGroup();
                string insStr = Singleton._buffer.ToString(startingCursor, Singleton._current - startingCursor);
                Singleton.SaveEditItem(EditItemDelete.Create(
                    deletedStr.ToString(),
                    startingCursor,
                    ViReplaceUntilEsc,
                    arg,
                    moveCursorToEndWhenUndo: false));

                Singleton.SaveEditItem(EditItemInsertString.Create(insStr, startingCursor));
                Singleton.EndEditGroup();
            }

            if (nextKey == Keys.Enter)
            {
                ViAcceptLine(nextKey.AsConsoleKeyInfo());
            }
        }

        private static void ViReplaceBrace(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceBrace, arg);
            ViDeleteBrace(key, arg);
            ViInsertMode(key, arg);
        }

        private static void ViBackwardReplaceLineToFirstChar(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViBackwardReplaceLineToFirstChar, arg);
            DeleteLineToFirstChar(key, arg);
            ViInsertMode(key, arg);
        }

        private static void ViBackwardReplaceLine(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViBackwardReplaceLine, arg);
            BackwardDeleteLine(key, arg);
            ViInsertMode(key, arg);
        }

        private static void BackwardReplaceChar(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(BackwardReplaceChar, arg);
            BackwardDeleteChar(key, arg);
            ViInsertMode(key, arg);
        }

        private static void ViBackwardReplaceWord(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViBackwardReplaceWord, arg);
            BackwardDeleteWord(key, arg);
            ViInsertMode(key, arg);
        }

        private static void ViBackwardReplaceGlob(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViBackwardReplaceGlob, arg);
            ViBackwardDeleteGlob(key, arg);
            ViInsertMode(key, arg);
        }

        private static void ViReplaceToEnd(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceToEnd, arg);
            DeleteToEnd(key, arg);
            Singleton.MoveCursor(Math.Min(Singleton._buffer.Length, Singleton._current + 1));
            ViInsertMode(key, arg);
        }

        /// <summary>
        /// Erase the entire command line.
        /// </summary>
        public static void ViReplaceLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceLine, arg);
            DeleteLine(key, arg);
            ViInsertMode(key, arg);
        }

        private static void ViReplaceWord(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceWord, arg);
            Singleton._lastWordDelimiter = char.MinValue;
            Singleton._shouldAppend = false;
            DeleteWord(key, arg);
            if (Singleton._current < Singleton._buffer.Length - 1)
            {
                if (char.IsWhiteSpace(Singleton._lastWordDelimiter))
                {
                    Insert(Singleton._lastWordDelimiter);
                    Singleton.MoveCursor(Singleton._current - 1);
                }
                Singleton._lastWordDelimiter = char.MinValue;
            }
            if (Singleton._current == Singleton._buffer.Length - 1
                && !Singleton.IsDelimiter(Singleton._lastWordDelimiter, Singleton.Options.WordDelimiters)
                && Singleton._shouldAppend)
            {
                ViInsertWithAppend(key, arg);
            }
            else
            {
                ViInsertMode(key, arg);
            }
        }

        private static void ViReplaceGlob(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceGlob, arg);
            ViDeleteGlob(key, arg);
            if (Singleton._current < Singleton._buffer.Length - 1)
            {
                Insert(' ');
                Singleton.MoveCursor(Singleton._current - 1);
            }
            if (Singleton._current == Singleton._buffer.Length - 1)
            {
                ViInsertWithAppend(key, arg);
            }
            else
            {
                ViInsertMode(key, arg);
            }
        }

        private static void ViReplaceEndOfWord(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceEndOfWord, arg);
            DeleteEndOfWord(key, arg);
            if (Singleton._current == Singleton._buffer.Length - 1)
            {
                ViInsertWithAppend(key, arg);
            }
            else
            {
                ViInsertMode(key, arg);
            }
        }

        private static void ViReplaceEndOfGlob(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ViReplaceEndOfGlob, arg);
            ViDeleteEndOfGlob(key, arg);
            if (Singleton._current == Singleton._buffer.Length - 1)
            {
                ViInsertWithAppend(key, arg);
            }
            else
            {
                ViInsertMode(key, arg);
            }
        }

        private static void ReplaceChar(ConsoleKeyInfo? key, object arg)
        {
            Singleton._groupUndoHelper.StartGroup(ReplaceChar, arg);
            ViInsertMode(key, arg);
            DeleteChar(key, arg);
        }

        /// <summary>
        /// Replaces the current character with the next character typed.
        /// </summary>
        private static void ReplaceCharInPlace(ConsoleKeyInfo? key, object arg)
        {
            var nextKey = ReadKey();
            if (Singleton._buffer.Length > 0 && nextKey.KeyStr.Length == 1)
            {
                Singleton.StartEditGroup();
                Singleton.SaveEditItem(EditItemDelete.Create(
                    Singleton._buffer[Singleton._current].ToString(),
                    Singleton._current,
                    ReplaceCharInPlace,
                    arg,
                    moveCursorToEndWhenUndo: false));

                Singleton.SaveEditItem(EditItemInsertString.Create(nextKey.KeyStr, Singleton._current));
                Singleton.EndEditGroup();

                Singleton._buffer[Singleton._current] = nextKey.KeyChar;
                Singleton.Render();
            }
            else
            {
                Ding();
            }
        }

        /// <summary>
        /// Deletes until given character.
        /// </summary>
        public static void ViReplaceToChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            var keyChar = ReadKey().KeyChar;
            ViReplaceToChar(keyChar, key, arg);
        }

        private static void ViReplaceToChar(char keyChar, ConsoleKeyInfo? key = null, object arg = null)
        {
            int initialCurrent = Singleton._current;

            Singleton._groupUndoHelper.StartGroup(ReplaceChar, arg);
            ViCharacterSearcher.Set(keyChar, isBackward: false, isBackoff: false);
            if (ViCharacterSearcher.SearchDelete(keyChar, arg, backoff: false, instigator: (_key, _arg) => ViReplaceToChar(keyChar, _key, _arg)))
            {
                if (Singleton._current < initialCurrent || Singleton._current >= Singleton._buffer.Length)
                {
                    ViInsertWithAppend(key, arg);
                }
                else
                {
                    ViInsertMode(key, arg);
                }
            }
        }

        /// <summary>
        /// Replaces until given character.
        /// </summary>
        public static void ViReplaceToCharBackward(ConsoleKeyInfo? key = null, object arg = null)
        {
            var keyChar = ReadKey().KeyChar;
            ViReplaceToCharBack(keyChar, key, arg);
        }

        private static void ViReplaceToCharBack(char keyChar, ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton._groupUndoHelper.StartGroup(ReplaceChar, arg);
            if (ViCharacterSearcher.SearchBackwardDelete(keyChar, arg, backoff: false, instigator: (_key, _arg) => ViReplaceToCharBack(keyChar, _key, _arg)))
            {
                ViInsertMode(key, arg);
            }
        }

        /// <summary>
        /// Replaces until given character.
        /// </summary>
        public static void ViReplaceToBeforeChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            var keyChar = ReadKey().KeyChar;
            ViReplaceToBeforeChar(keyChar, key, arg);
        }

        private static void ViReplaceToBeforeChar(char keyChar, ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton._groupUndoHelper.StartGroup(ReplaceChar, arg);
            ViCharacterSearcher.Set(keyChar, isBackward: false, isBackoff: true);
            if (ViCharacterSearcher.SearchDelete(keyChar, arg, backoff: true, instigator: (_key, _arg) => ViReplaceToBeforeChar(keyChar, _key, _arg)))
            {
                ViInsertMode(key, arg);
            }
        }

        /// <summary>
        /// Replaces until given character.
        /// </summary>
        public static void ViReplaceToBeforeCharBackward(ConsoleKeyInfo? key = null, object arg = null)
        {
            var keyChar = ReadKey().KeyChar;
            ViReplaceToBeforeCharBack(keyChar, key, arg);
        }

        private static void ViReplaceToBeforeCharBack(char keyChar, ConsoleKeyInfo? key = null, object arg = null)
        {
            Singleton._groupUndoHelper.StartGroup(ReplaceChar, arg);
            if (ViCharacterSearcher.SearchBackwardDelete(keyChar, arg, backoff: true, instigator: (_key, _arg) => ViReplaceToBeforeCharBack(keyChar, _key, _arg)))
            {
                ViInsertMode(key, arg);
            }
        }


    }
}
