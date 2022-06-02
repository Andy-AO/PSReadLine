﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;

namespace Microsoft.PowerShell;

public partial class PSConsoleReadLine
{
    /// <summary>
    ///     Edit the command line in a text editor specified by $env:EDITOR or $env:VISUAL.
    /// </summary>
    public static void ViEditVisually(ConsoleKeyInfo? key = null, object arg = null)
    {
        var editor = GetPreferredEditor();
        if (string.IsNullOrWhiteSpace(editor))
        {
            Ding();
            return;
        }

        if (!(Singleton._engineIntrinsics?.InvokeCommand.GetCommand(editor, CommandTypes.Application) is
                ApplicationInfo editorCommand))
        {
            Ding();
            return;
        }

        var tempPowerShellFile = GetTemporaryPowerShellFile();
        using (var fs = File.OpenWrite(tempPowerShellFile))
        {
            using (TextWriter tw = new StreamWriter(fs))
            {
                tw.Write(Singleton.buffer.ToString());
            }
        }

        editor = editorCommand.Path;
        var si = new ProcessStartInfo(editor, $"\"{tempPowerShellFile}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false
        };
        var pi = Singleton.CallPossibleExternalApplication(() => Process.Start(si));
        if (pi != null)
        {
            pi.WaitForExit();
            InvokePrompt();
            Singleton.ProcessViVisualEditing(tempPowerShellFile);
        }
        else
        {
            Ding();
        }
    }

    private static string GetTemporaryPowerShellFile()
    {
        string filename;
        do
        {
            filename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ps1");
        } while (File.Exists(filename) || Directory.Exists(filename));

        return filename;
    }

    private void ProcessViVisualEditing(string tempFileName)
    {
        string editedCommand;
        using (TextReader tr = File.OpenText(tempFileName))
        {
            editedCommand = tr.ReadToEnd();
        }

        File.Delete(tempFileName);

        if (!string.IsNullOrWhiteSpace(editedCommand))
        {
            while (editedCommand.Length > 0 && char.IsWhiteSpace(editedCommand[editedCommand.Length - 1]))
                editedCommand = editedCommand.Substring(0, editedCommand.Length - 1);
            editedCommand = editedCommand.Replace(Environment.NewLine, "\n");
            Replace(0, buffer.Length, editedCommand);
            _renderer.Current = buffer.Length;
            if (Options.EditMode == EditMode.Vi) _renderer.Current = _renderer.Current - 1;
            _renderer.Render();
        }
    }

    private static string GetPreferredEditor()
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL");
        return !string.IsNullOrWhiteSpace(editor)
            ? editor
            : Environment.GetEnvironmentVariable("EDITOR");
    }
}