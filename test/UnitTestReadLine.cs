﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Subsystem.Prediction;
using System.Threading.Tasks;
using Microsoft.PowerShell.Internal;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Test
{

    public class MockedMethods : IPSConsoleReadLineMockableMethods
    {
        internal bool didDing;
        internal IReadOnlyList<string> commandHistory;
        internal bool? lastCommandRunStatus;
        internal Guid acceptedPredictorId;
        internal string acceptedSuggestion;
        internal string helpContentRendered;
        internal Dictionary<Guid, Tuple<uint, int>> displayedSuggestions = new Dictionary<Guid, Tuple<uint, int>>();

        internal void ClearPredictionFields()
        {
            commandHistory = null;
            lastCommandRunStatus = null;
            acceptedPredictorId = Guid.Empty;
            acceptedSuggestion = null;
            displayedSuggestions.Clear();
        }

        public void Ding()
        {
            didDing = true;
        }

        public CommandCompletion CompleteInput(string input, int cursorIndex, Hashtable options, PowerShell powershell)
        {
            return ReadLine.MockedCompleteInput(input, cursorIndex, options, powershell);
        }

        public bool RunspaceIsRemote(Runspace runspace)
        {
            return false;
        }

        public Task<List<PredictionResult>> PredictInputAsync(Ast ast, Token[] tokens)
        {
            var result = ReadLine.MockedPredictInput(ast, tokens);
            var source = new TaskCompletionSource<List<PredictionResult>>();

            source.SetResult(result);
            return source.Task;
        }

        public void OnCommandLineAccepted(IReadOnlyList<string> history)
        {
            commandHistory = history;
        }

        public void OnCommandLineExecuted(string commandLine, bool success)
        {
            lastCommandRunStatus = success;
        }

        public void OnSuggestionDisplayed(Guid predictorId, uint session, int countOrIndex)
        {
            displayedSuggestions[predictorId] = Tuple.Create(session, countOrIndex);
        }

        public void OnSuggestionAccepted(Guid predictorId, uint session, string suggestionText)
        {
            acceptedPredictorId = predictorId;
            acceptedSuggestion = suggestionText;
        }

        public object GetDynamicHelpContent(string commandName, string parameterName, bool isFullHelp)
        {
            return ReadLine.GetDynamicHelpContent(commandName, parameterName, isFullHelp);
        }

        public void RenderFullHelp(string content, string regexPatternToScrollTo)
        {
            helpContentRendered = content;
        }
    }

    public enum TokenClassification
    {
        None,
        Comment,
        Keyword,
        String,
        Operator,
        Variable,
        Command,
        Parameter,
        Type,
        Number,
        Member,
        Selection,
        InlinePrediction,
        ListPrediction,
        ListPredictionSelected,
    }

    public enum KeyMode
    {
        Cmd,
        Emacs,
        Vi
    };

    public class en_US_Windows : Test.ReadLine, IClassFixture<ConsoleFixture>
    {
        public en_US_Windows(ConsoleFixture fixture, ITestOutputHelper output)
            : base(fixture, output, "en-US", "windows")
        {
        }
    }

    public class fr_FR_Windows : Test.ReadLine, IClassFixture<ConsoleFixture>
    {
        public fr_FR_Windows(ConsoleFixture fixture, ITestOutputHelper output)
            : base(fixture, output, "fr-FR", "windows")
        {
        }

        // I don't think this is actually true for real French keyboard, but on my US keyboard,
        // I have to use Alt 6 0 for `<` and Alt 6 2 for `>` and that means the Alt+< and Alt+>
        // bindings can't work.
        internal override bool KeyboardHasLessThan => false;
        internal override bool KeyboardHasGreaterThan => false;

        // These are most likely an issue with .Net on Windows - AltGr turns into Ctrl+Alt and `]` or `@`
        // requires AltGr, so you can't tell the difference b/w `]` and `Ctrl+]`.
        internal override bool KeyboardHasCtrlRBracket => false;
        internal override bool KeyboardHasCtrlAt => false;
    }
}
