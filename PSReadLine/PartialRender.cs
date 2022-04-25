/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.PowerShell.Internal;
using static Microsoft.PowerShell.Renderer;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private static readonly Renderer _renderer = Renderer.Singleton;

        private RenderData PreviousRender
        {
            get => _renderer.PreviousRender;
            set => _renderer.PreviousRender = value;
        }

        private static RenderData InitialPrevRender => Renderer.InitialPrevRender;

        private int InitialX
        {
            get => _renderer.InitialX;
            set => _renderer.InitialX = value;
        }

        private int InitialY
        {
            get => _renderer.InitialY;
            set => _renderer.InitialY = value;
        }

        private int Current
        {
            get => _renderer.Current;
            set => _renderer.Current = value;
        }

        private int EmphasisStart
        {
            get => _renderer.EmphasisStart;
            set => _renderer.EmphasisStart = value;
        }

        private int EmphasisLength
        {
            get => _renderer.EmphasisLength;
            set => _renderer.EmphasisLength = value;
        }


        private void RenderWithPredictionQueryPaused()
        {
            // Sometimes we need to re-render the buffer to show status line, or to clear
            // the visual selection, or to clear the visual emphasis.
            // In those cases, the buffer text is unchanged, and thus we can skip querying
            // for prediction during the rendering, but instead, use the existing results.
            using var _ = _Prediction.PauseQuery();
            _renderer.Render();
        }

        /// <summary>
        /// Returns the logical line number under the cursor in a multi-line buffer.
        /// When rendering, a logical line may span multiple physical lines.
        /// </summary>
        private int GetLogicalLineNumber()
        {
            var current = Current;
            var lineNumber = 1;

            for (int i = 0; i < current; i++)
            {
                if (buffer[i] == '\n')
                {
                    lineNumber++;
                }
            }

            return lineNumber;
        }

        /// <summary>
        /// Returns the number of logical lines in a multi-line buffer.
        /// When rendering, a logical line may span multiple physical lines.
        /// </summary>
        private int GetLogicalLineCount()
        {
            var count = 1;

            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private bool LineIsMultiLine()
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\n')
                    return true;
            }

            return false;
        }
        private bool PromptYesOrNo(string s)
        {
            _statusLinePrompt = s;
            _renderer.Render();

            var key = ReadKey();

            _statusLinePrompt = null;
            _renderer.Render();
            return key.KeyStr.Equals("y", StringComparison.OrdinalIgnoreCase);
        }
    }
}