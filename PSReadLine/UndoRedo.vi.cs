﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private class GroupUndoHelper
        {
            public Action<ConsoleKeyInfo?, object> _instigator;
            public object _instigatorArg;

            public GroupUndoHelper()
            {
                _instigator = null;
                _instigatorArg = null;
            }

            public void StartGroup(Action<ConsoleKeyInfo?, object> instigator, object instigatorArg)
            {
                _instigator = instigator;
                _instigatorArg = instigatorArg;
                Singleton.StartEditGroup();
            }

            public void Clear()
            {
                _instigator = null;
                _instigatorArg = null;
            }

            public void EndGroup()
            {
                if (Singleton._editGroupStart >= 0)
                {
                    Singleton.EndEditGroup(_instigator, _instigatorArg);
                }
                Clear();
            }
        }
        private readonly GroupUndoHelper _groupUndoHelper = new GroupUndoHelper();

        /// <summary>
        /// Undo all previous edits for line.
        /// </summary>
        public static void UndoAll(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (Singleton._undoEditIndex > 0)
            {
                while (Singleton._undoEditIndex > 0)
                {
                    Singleton._edits[Singleton._undoEditIndex - 1].Undo();
                    Singleton._undoEditIndex--;
                }
                _renderer.Render();
            }
            else
            {
                Ding();
            }
        }
    }
}
