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
    }
}