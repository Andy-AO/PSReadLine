﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.PowerShell.Commands;
using Microsoft.PowerShell.Internal;
using Microsoft.PowerShell.PSReadLine;

[module: SuppressMessage("Microsoft.Design", "CA1014:MarkAssembliesWithClsCompliant")]
[module: SuppressMessage("Microsoft.Design", "CA1026:DefaultParametersShouldNotBeUsed")]

namespace Microsoft.PowerShell
{
    [SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    [SuppressMessage("Microsoft.Usage", "CA2237:MarkISerializableTypesWithSerializable")]
    internal class ExitException : Exception
    {
    }

    public partial class PSConsoleReadLine : IPSConsoleReadLineMockableMethods
    {
        private const int ConsoleExiting = 1;

        private const int CancellationRequested = 2;

        private static readonly CancellationToken _defaultCancellationToken = new CancellationTokenSource().Token;

        // This is used by PowerShellEditorServices (the backend of the PowerShell VSCode extension)
        // so that it can call PSReadLine from a delegate and not hit nested pipeline issues.
#pragma warning disable CS0649
        private static Action<CancellationToken> _handleIdleOverride;
#pragma warning restore CS0649
        private static readonly Stopwatch _readkeyStopwatch = new();

        // Save a fixed # of keys so we can reconstruct a repro after a crash
        private static readonly HistoryQueue<PSKeyInfo> _lastNKeys = new(200);
        private static PSConsoleReadLine _s;
        private static readonly Renderer _renderer = Renderer.Singleton;
        internal readonly Queue<PSKeyInfo> _queuedKeys;
        internal readonly StringBuilder _statusBuffer;

        public readonly StringBuilder buffer;
        private string _acceptedCommandLine;
        private CancellationToken _cancelReadCancellationToken;

        private ICharMap _charMap;
        internal ManualResetEvent _closingWaitHandle;

        private bool _delayedOneTimeInitCompleted;
        private int _editGroupStart;
        private List<EditItem> _edits;
        private EngineIntrinsics _engineIntrinsics;
        private Encoding _initialOutputEncoding;
        private bool _inputAccepted;
        private AutoResetEvent _keyReadWaitHandle;
        internal int _mark;

        private readonly IPSConsoleReadLineMockableMethods _mockableMethods;
        private Thread _readKeyThread;
        private AutoResetEvent _readKeyWaitHandle;
        private WaitHandle[] _requestKeyWaitHandles;
        private bool _skipOutputEncodingChange;
        internal bool _statusIsErrorMessage;
        internal string _statusLinePrompt;
        private WaitHandle[] _threadProcWaitHandles;
        private int _undoEditIndex;

        static PSConsoleReadLine()
        {
            _viRegister = new ViRegister(_s);
        }

        private PSConsoleReadLine()
        {
            _mockableMethods = this;
            _charMap = new DotNetCharMap();
            buffer = new StringBuilder(8 * 1024);
            _statusBuffer = new StringBuilder(256);
            _savedCurrentLine = new HistoryItem();
            _queuedKeys = new Queue<PSKeyInfo>();
            string hostName = null;
            // This works mostly by luck - we're not doing anything to guarantee the constructor for our
            // _s is called on a thread with a runspace, but it is happening by coincidence.
            using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                try
                {
                    var results = ps.AddScript("$Host", true).Invoke<PSHost>();
                    var host = results.Count == 1 ? results[0] : null;
                    hostName = host?.Name;
                }
                catch
                {
                }
            }

            hostName ??= PSReadLine;

            Options = new PSConsoleReadLineOptions(hostName);
            _Prediction = new Prediction(this);
            SetDefaultBindings(Options.EditMode);
        }

        // *must* be initialized in the static ctor
        // because the static member _clipboard depends upon it
        // for its own initialization
        internal static PSConsoleReadLine Singleton
        {
            get
            {
                if (_s == null) Singleton = new PSConsoleReadLine();

                return _s;
            }
            private set => _s = value;
        }

        internal Token[] Tokens
        {
            get
            {
                Parser.ParseInput(buffer.ToString(), out var _tokens, out _);
                return (Token[]) _tokens.Clone();
            }
        }

        private Ast RLAst => Parser.ParseInput(buffer.ToString(), out _, out _);

        public ParseError[] ParseErrors
        {
            get
            {
                Parser.ParseInput(buffer.ToString(), out _, out var _parseErrors);
                return _parseErrors;
            }
        }

        public IConsole RLConsole => _renderer._console;

        public static string Prompt
        {
            get
            {
                string newPrompt = null;

                try
                {
                    if (_s._runspace?.Debugger != null && _s._runspace.Debugger.InBreakpoint)
                    {
                        // Run prompt command in debugger API to ensure it is run correctly on the runspace.
                        // This handles remote runspace debugging and nested debugger scenarios.
                        var results = new PSDataCollection<PSObject>();
                        var command = new PSCommand();
                        command.AddCommand("prompt");
                        _s._runspace.Debugger.ProcessCommand(
                            command,
                            results);

                        if (results.Count == 1)
                            newPrompt = results[0].BaseObject as string;
                    }
                    else
                    {
                        var runspaceIsRemote = _s._mockableMethods.RunspaceIsRemote(_s._runspace);

                        System.Management.Automation.PowerShell ps;
                        if (!runspaceIsRemote)
                        {
                            ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
                        }
                        else
                        {
                            ps = System.Management.Automation.PowerShell.Create();
                            ps.Runspace = _s._runspace;
                        }

                        using (ps)
                        {
                            ps.AddCommand("prompt");
                            var result = ps.Invoke<string>();
                            if (result.Count == 1)
                            {
                                newPrompt = result[0];

                                if (runspaceIsRemote)
                                    if (!string.IsNullOrEmpty(_s._runspace?.ConnectionInfo?.ComputerName))
                                        newPrompt = "[" + (_s._runspace?.ConnectionInfo).ComputerName + "]: " +
                                                    newPrompt;
                            }
                        }
                    }
                }
                catch
                {
                    // Catching all exceptions makes debugging problems a bit harder, but it avoids some noise if
                    // the remote doesn't define a prompt.
                }

                if (string.IsNullOrEmpty(newPrompt))
                    newPrompt = "PS>";

                return newPrompt;
            }
        }

        bool IPSConsoleReadLineMockableMethods.RunspaceIsRemote(Runspace runspace)
        {
            return runspace?.ConnectionInfo != null;
        }

        [ExcludeFromCodeCoverage]
        void IPSConsoleReadLineMockableMethods.Ding()
        {
            switch (Options.BellStyle)
            {
                case BellStyle.None:
                    break;
                case BellStyle.Audible:
                    if (Options.DingDuration > 0)
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            Console.Beep(Options.DingTone, Options.DingDuration);
                        else
                            Console.Beep();
                    }

                    break;
                case BellStyle.Visual:
                    // TODO: flash prompt? command line?
                    break;
            }
        }

        private void ClearStatusMessage(bool render)
        {
            _statusBuffer.Clear();
            _statusLinePrompt = null;
            _statusIsErrorMessage = false;
            if (render) _renderer.RenderWithPredictionQueryPaused();
        }

        /// <summary>
        ///     Notify the user based on their preference for notification.
        /// </summary>
        public static void Ding()
        {
            _s._mockableMethods.Ding();
        }

        private void ReadOneOrMoreKeys()
        {
            _readkeyStopwatch.Restart();
            while (RLConsole.KeyAvailable)
            {
                // _charMap is only guaranteed to accumulate input while KeyAvailable
                // returns false. Make sure to check KeyAvailable after every ProcessKey call,
                // and clear it in a loop in case the input was something like ^[[1 which can
                // be 3, 2, or part of 1 key depending on timing.
                _charMap.ProcessKey(RLConsole.ReadKey());
                while (_charMap.KeyAvailable)
                {
                    var key = PSKeyInfo.FromConsoleKeyInfo(_charMap.ReadKey());
                    _lastNKeys.Enqueue(key);
                    _queuedKeys.Enqueue(key);
                }

                if (_readkeyStopwatch.ElapsedMilliseconds > 2)
                    // Don't spend too long in this loop if there are lots of queued keys
                    break;
            }

            if (_queuedKeys.Count == 0)
            {
                while (!_charMap.KeyAvailable)
                    // Don't want to block when there is an escape sequence being read.
                    if (_charMap.InEscapeSequence)
                    {
                        if (RLConsole.KeyAvailable)
                            _charMap.ProcessKey(RLConsole.ReadKey());
                        else
                            // We don't want to sleep for the whole escape timeout
                            // or the user will have a laggy console, but there's
                            // nothing to block on at this point either, so do a
                            // small sleep to yield the CPU while we're waiting
                            // to decide what the input was. This will only run
                            // if there are no keys waiting to be read.
                            Thread.Sleep(5);
                    }
                    else
                    {
                        _charMap.ProcessKey(RLConsole.ReadKey());
                    }

                while (_charMap.KeyAvailable)
                {
                    var key = PSKeyInfo.FromConsoleKeyInfo(_charMap.ReadKey());
                    _lastNKeys.Enqueue(key);
                    _queuedKeys.Enqueue(key);
                }
            }
        }

        private void ReadKeyThreadProc()
        {
            while (true)
            {
                // Wait until ReadKey tells us to read a key (or it's time to exit).
                var handleId = WaitHandle.WaitAny(_s._threadProcWaitHandles);
                if (handleId == 1) // It was the _closingWaitHandle that was signaled.
                    break;

                var localCancellationToken = _s._cancelReadCancellationToken;
                ReadOneOrMoreKeys();
                if (localCancellationToken.IsCancellationRequested) continue;

                // One or more keys were read - let ReadKey know we're done.
                _keyReadWaitHandle.Set();
            }
        }

        internal static PSKeyInfo ReadKey()
        {
            // Reading a key is handled on a different thread.  During process shutdown,
            // PowerShell will wait in it's ConsoleCtrlHandler until the pipeline has completed.
            // If we're running, we're most likely blocked waiting for user input.
            // This is a problem for two reasons.  First, exiting takes a long time (5 seconds
            // on Win8) because PowerShell is waiting forever, but the OS will forcibly terminate
            // the console.  Also - if there are any event handlers for the engine event
            // PowerShell.Exiting, those handlers won't get a chance to run.
            //
            // By waiting for a key on a different thread, our pipeline execution thread
            // (the thread ReadLine is called from) avoid being blocked in code that can't
            // be unblocked and instead blocks on events we control.

            // First, set an event so the thread to read a key actually attempts to read a key.
            _s._readKeyWaitHandle.Set();

            int handleId;
            System.Management.Automation.PowerShell ps = null;

            try
            {
                while (true)
                {
                    // Next, wait for one of three things:
                    //   - a key is pressed
                    //   - the console is exiting
                    //   - 300ms timeout - to process events if we're idle
                    //   - ReadLine cancellation is requested externally
                    handleId = WaitHandle.WaitAny(_s._requestKeyWaitHandles, 300);
                    if (handleId != WaitHandle.WaitTimeout) break;

                    if (_handleIdleOverride is not null)
                    {
                        _handleIdleOverride(_s._cancelReadCancellationToken);
                        continue;
                    }

                    // If we timed out, check for event subscribers (which is just
                    // a hint that there might be an event waiting to be processed.)
                    var eventSubscribers = _s._engineIntrinsics?.Events.Subscribers;
                    if (eventSubscribers?.Count > 0)
                    {
                        var runPipelineForEventProcessing = false;
                        foreach (var sub in eventSubscribers)
                        {
                            if (sub.SourceIdentifier.Equals(PSEngineEvent.OnIdle, StringComparison.OrdinalIgnoreCase))
                            {
                                // If the buffer is not empty, let's not consider we are idle because the user is in the middle of typing something.
                                if (_s.buffer.Length > 0) continue;

                                // There is an OnIdle event subscriber and we are idle because we timed out and the buffer is empty.
                                // Normally PowerShell generates this event, but PowerShell assumes the engine is not idle because
                                // it called PSConsoleHostReadLine which isn't returning. So we generate the event instead.
                                runPipelineForEventProcessing = true;
                                _s._engineIntrinsics.Events.GenerateEvent(PSEngineEvent.OnIdle, null, null,
                                    null);

                                // Break out so we don't genreate more than one 'OnIdle' event for a timeout.
                                break;
                            }

                            runPipelineForEventProcessing = true;
                        }

                        // If there are any event subscribers, run a tiny useless PowerShell pipeline
                        // so that the events can be processed.
                        if (runPipelineForEventProcessing)
                        {
                            if (ps == null)
                            {
                                ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
                                ps.AddScript("0", true);
                            }

                            // To detect output during possible event processing, see if the cursor moved
                            // and rerender if so.
                            var console = _s.RLConsole;
                            var y = console.CursorTop;
                            ps.Invoke();
                            if (y != console.CursorTop)
                            {
                                _renderer.InitialY = console.CursorTop;
                                _renderer.Render();
                            }
                        }
                    }
                }
            }
            finally
            {
                ps?.Dispose();
            }

            if (handleId == ConsoleExiting)
            {
                // The console is exiting - throw an exception to unwind the stack to the point
                // where we can return from ReadLine.
                if (_s.Options.HistorySaveStyle == HistorySaveStyle.SaveAtExit) _s.SaveHistoryAtExit();

                _s._historyFileMutex.Dispose();

                throw new OperationCanceledException();
            }

            if (handleId == CancellationRequested)
            {
                // ReadLine was cancelled. Save the current line to be restored next time ReadLine
                // is called, clear the buffer and throw an exception so we can return an empty string.
                _s.SaveCurrentLine();
                _s._getNextHistoryIndex = _s._history.Count;
                _renderer.Current = 0;
                _s.buffer.Clear();
                _renderer.Render();
                throw new OperationCanceledException();
            }

            var key = _s._queuedKeys.Dequeue();
            return key;
        }

        private void PrependQueuedKeys(PSKeyInfo key)
        {
            if (_queuedKeys.Count > 0)
            {
                // This should almost never happen so being inefficient is fine.
                var list = new List<PSKeyInfo>(_queuedKeys);
                _queuedKeys.Clear();
                _queuedKeys.Enqueue(key);
                list.ForEach(k => _queuedKeys.Enqueue(k));
            }
            else
            {
                _queuedKeys.Enqueue(key);
            }
        }

        /// <summary>
        ///     Entry point - called from the PowerShell function PSConsoleHostReadLine
        ///     after the prompt has been displayed.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine(Runspace runspace, EngineIntrinsics engineIntrinsics, bool? lastRunStatus)
        {
            // Use a default cancellation token instead of CancellationToken.None because the
            // WaitHandle is shared and could be triggered accidently.
            return ReadLine(runspace, engineIntrinsics, _defaultCancellationToken, lastRunStatus);
        }

        /// <summary>
        ///     Entry point - called by custom PSHost implementations that require the
        ///     ability to cancel ReadLine.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine(
            Runspace runspace,
            EngineIntrinsics engineIntrinsics,
            CancellationToken cancellationToken,
            bool? lastRunStatus)
        {
            var console = _s.RLConsole;

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                // System.Console doesn't handle redirected input. It matches the behavior on Windows
                // by throwing an "InvalidOperationException".
                // Therefore, if either stdin or stdout is redirected, PSReadLine doesn't really work,
                // so throw and let PowerShell call Console.ReadLine or do whatever else it decides to do.
                //
                // Some CI environments redirect stdin/stdout, but that doesn't affect our test runs
                // because the console is mocked, so we can skip the exception.
                if (!IsRunningCI(console))
                    throw new NotSupportedException();

            var oldControlCAsInput = false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                PlatformWindows.Init(ref _s._charMap);
            else
                try
                {
                    oldControlCAsInput = Console.TreatControlCAsInput;
                    Console.TreatControlCAsInput = true;
                }
                catch
                {
                }

            if (lastRunStatus.HasValue) _s.ReportExecutionStatus(lastRunStatus.Value);

            var firstTime = true;
            while (true)
                try
                {
                    if (firstTime)
                    {
                        firstTime = false;
                        _s.Initialize(runspace, engineIntrinsics);
                    }

                    _s._cancelReadCancellationToken = cancellationToken;
                    _s._requestKeyWaitHandles[2] = _s._cancelReadCancellationToken.WaitHandle;
                    return _s.InputLoop();
                }
                catch (OperationCanceledException)
                {
                    // Console is either exiting or the cancellation of ReadLine has been requested
                    // by a custom PSHost implementation.
                    return "";
                }
                catch (ExitException)
                {
                    return "exit";
                }
                catch (CustomHandlerException e)
                {
                    var oldColor = console.ForegroundColor;
                    console.ForegroundColor = ConsoleColor.Red;
                    console.WriteLine(
                        string.Format(CultureInfo.CurrentUICulture, PSReadLineResources.OopsCustomHandlerException,
                            e.InnerException.Message));
                    console.ForegroundColor = oldColor;

                    var lineBeforeCrash = _s.buffer.ToString();
                    _s.Initialize(runspace, _s._engineIntrinsics);
                    InvokePrompt();
                    Insert(lineBeforeCrash);
                }
                catch (Exception e)
                {
                    // If we're running tests, just throw.
                    if (_s._mockableMethods != _s) throw;

                    while (e.InnerException != null) e = e.InnerException;

                    var oldColor = console.ForegroundColor;
                    console.ForegroundColor = ConsoleColor.Red;
                    console.WriteLine(PSReadLineResources.OopsAnErrorMessage1);
                    console.ForegroundColor = oldColor;
                    var sb = new StringBuilder();
                    for (var i = 0; i < _lastNKeys.Count; i++)
                    {
                        sb.Append(' ');
                        sb.Append(_lastNKeys[i].KeyStr);

                        if (_s._dispatchTable.TryGetValue(_lastNKeys[i], out var handler) &&
                            "AcceptLine".Equals(handler.BriefDescription, StringComparison.OrdinalIgnoreCase))
                            // Make it a little easier to see the keys
                            sb.Append('\n');
                    }

                    var psVersion = PSObject.AsPSObject(engineIntrinsics.Host.Version).ToString();
                    var ourVersion = typeof(PSConsoleReadLine).Assembly
                        .GetCustomAttributes<AssemblyInformationalVersionAttribute>().First().InformationalVersion;
                    var osInfo = RuntimeInformation.OSDescription;
                    var bufferWidth = console.BufferWidth;
                    var bufferHeight = console.BufferHeight;

                    console.WriteLine(string.Format(CultureInfo.CurrentUICulture,
                        PSReadLineResources.OopsAnErrorMessage2,
                        ourVersion, psVersion, osInfo, bufferWidth, bufferHeight,
                        _lastNKeys.Count, sb, e));
                    var lineBeforeCrash = _s.buffer.ToString();
                    _s.Initialize(runspace, _s._engineIntrinsics);
                    InvokePrompt();
                    Insert(lineBeforeCrash);
                }
                finally
                {
                    try
                    {
                        // If we are closing, restoring the old console settings isn't needed,
                        // and some operating systems, it can cause a hang.
                        if (!_s._closingWaitHandle.WaitOne(0))
                        {
                            console.OutputEncoding = _s._initialOutputEncoding;
                            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                Console.TreatControlCAsInput = oldControlCAsInput;
                        }
                    }
                    catch
                    {
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) PlatformWindows.Complete();
                }
        }

        private string InputLoop()
        {
            while (true)
            {
                var killCommandCount = _killCommandCount;
                var yankCommandCount = _yankCommandCount;
                var tabCommandCount = _tabCommandCount;
                var searchHistoryCommandCount = _searchHistoryCommandCount;
                var recallHistoryCommandCount = _recallHistoryCommandCount;
                var anyHistoryCommandCount = _anyHistoryCommandCount;
                var yankLastArgCommandCount = _yankLastArgCommandCount;
                var visualSelectionCommandCount = _visualSelectionCommandCount;
                var moveToLineCommandCount = _moveToLineCommandCount;
                var moveToEndOfLineCommandCount = _moveToEndOfLineCommandCount;

                var key = ReadKey();
                ProcessOneKey(key, _dispatchTable, false, null);
                if (_inputAccepted)
                {
                    _acceptedCommandLine = buffer.ToString();
                    MaybeAddToHistory(_acceptedCommandLine, _edits, _undoEditIndex);

                    _Prediction.OnCommandLineAccepted(_acceptedCommandLine);
                    return _acceptedCommandLine;
                }

                if (killCommandCount == _killCommandCount)
                    // Reset kill command count if it didn't change
                    _killCommandCount = 0;

                if (yankCommandCount == _yankCommandCount)
                    // Reset yank command count if it didn't change
                    _yankCommandCount = 0;

                if (yankLastArgCommandCount == _yankLastArgCommandCount)
                {
                    // Reset yank last arg command count if it didn't change
                    _yankLastArgCommandCount = 0;
                    _yankLastArgState = null;
                }

                if (tabCommandCount == _tabCommandCount)
                {
                    // Reset tab command count if it didn't change
                    _tabCommandCount = 0;
                    _tabCompletions = null;
                }

                if (searchHistoryCommandCount == _searchHistoryCommandCount)
                {
                    if (_searchHistoryCommandCount > 0)
                    {
                        _renderer.EmphasisStart = -1;
                        _renderer.EmphasisLength = 0;
                        _renderer.RenderWithPredictionQueryPaused();
                    }

                    _searchHistoryCommandCount = 0;
                    _searchHistoryPrefix = null;
                }

                if (recallHistoryCommandCount == _recallHistoryCommandCount) _recallHistoryCommandCount = 0;

                if (anyHistoryCommandCount == _anyHistoryCommandCount)
                {
                    if (_anyHistoryCommandCount > 0)
                    {
                        ClearSavedCurrentLine();
                        _hashedHistory = null;
                        _currentHistoryIndex = _history.Count;
                    }

                    _anyHistoryCommandCount = 0;
                }

                if (visualSelectionCommandCount == _visualSelectionCommandCount && _visualSelectionCommandCount > 0)
                {
                    _visualSelectionCommandCount = 0;
                    _renderer.RenderWithPredictionQueryPaused(); // Clears the visual selection
                }

                if (moveToLineCommandCount == _moveToLineCommandCount)
                {
                    _moveToLineCommandCount = 0;

                    if (InViCommandMode() && moveToEndOfLineCommandCount == _moveToEndOfLineCommandCount)
                    {
                        // the previous command was neither a "move to end of line" command
                        // nor a "move to line" command. In that case, the desired column
                        // number will be computed from the current position on the logical line.

                        _moveToEndOfLineCommandCount = 0;
                        _moveToLineDesiredColumn = -1;
                    }
                }
            }
        }

        private T CallPossibleExternalApplication<T>(Func<T> func)
        {
            try
            {
                RLConsole.OutputEncoding = _initialOutputEncoding;
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? PlatformWindows.CallPossibleExternalApplication(func)
                    : func();
            }
            finally
            {
                if (!_skipOutputEncodingChange) RLConsole.OutputEncoding = Encoding.UTF8;
            }
        }

        private void CallPossibleExternalApplication(Action action)
        {
            CallPossibleExternalApplication<object>(() =>
            {
                action();
                return null;
            });
        }

        private void ProcessOneKey(PSKeyInfo key, Dictionary<PSKeyInfo, KeyHandler> dispatchTable,
            bool ignoreIfNoAction,
            object arg)
        {
            var consoleKey = key.AsConsoleKeyInfo();

            // Our dispatch tables are built as much as possible in a portable way, so for example,
            // we avoid depending on scan codes like ConsoleKey.Oem6 and instead look at the
            // PSKeyInfo.Key. We also want to ignore the shift state as that may differ on
            // different keyboard layouts.
            //
            // That said, we first look up exactly what we get from Console.ReadKey - that will fail
            // most of the time, and when it does, we normalize the key.
            if (!dispatchTable.TryGetValue(key, out var handler))
                // If we see a control character where Ctrl wasn't used but shift was, treat that like
                // shift hadn't be pressed.  This cleanly allows Shift+Backspace without adding a key binding.
                if (key.Shift && !key.Control && !key.Alt)
                {
                    var c = consoleKey.KeyChar;
                    if (c != '\0' && char.IsControl(c))
                    {
                        key = PSKeyInfo.From(consoleKey.Key);
                        dispatchTable.TryGetValue(key, out handler);
                    }
                }

            if (handler != null)
            {
                if (handler.ScriptBlock != null)
                    CallPossibleExternalApplication(() => handler.Action(consoleKey, arg));
                else
                    handler.Action(consoleKey, arg);
            }
            else if (!ignoreIfNoAction)
            {
                SelfInsert(consoleKey, arg);
            }
        }

        private void Initialize(Runspace runspace, EngineIntrinsics engineIntrinsics)
        {
            _engineIntrinsics = engineIntrinsics;
            _runspace = runspace;

            if (!_delayedOneTimeInitCompleted)
            {
                DelayedOneTimeInitialize();
                _delayedOneTimeInitCompleted = true;
            }

            var val = Renderer.InitialPrevRender;
            _renderer.PreviousRender = val;
            _renderer.PreviousRender.bufferWidth = RLConsole.BufferWidth;
            _renderer.PreviousRender.bufferHeight = RLConsole.BufferHeight;
            _renderer.PreviousRender.errorPrompt = false;
            buffer.Clear();
            _edits = new List<EditItem>();
            _undoEditIndex = 0;
            _editGroupStart = -1;
            _renderer.Current = 0;
            _mark = 0;
            _renderer.EmphasisStart = -1;
            _renderer.EmphasisLength = 0;
            _inputAccepted = false;
            _renderer.InitialX = RLConsole.CursorLeft;
            _renderer.InitialY = RLConsole.CursorTop;
            _statusIsErrorMessage = false;

            _initialOutputEncoding = RLConsole.OutputEncoding;
            _Prediction.Reset();

            // Don't change the OutputEncoding if already UTF8, no console, or using raster font on Windows
            _skipOutputEncodingChange = _initialOutputEncoding == Encoding.UTF8
                                        || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                                        && PlatformWindows.IsConsoleInput()
                                        && PlatformWindows.IsUsingRasterFont();

            if (!_skipOutputEncodingChange) RLConsole.OutputEncoding = Encoding.UTF8;

            _killCommandCount = 0;
            _yankCommandCount = 0;
            _yankLastArgCommandCount = 0;
            _tabCommandCount = 0;
            _recallHistoryCommandCount = 0;
            _anyHistoryCommandCount = 0;
            _visualSelectionCommandCount = 0;
            _hashedHistory = null;

            if (_getNextHistoryIndex > 0)
            {
                _currentHistoryIndex = _getNextHistoryIndex;
                UpdateFromHistory(HistoryMoveCursor.ToEnd);
                _getNextHistoryIndex = 0;
                if (_searchHistoryCommandCount > 0)
                {
                    _searchHistoryPrefix = "";
                    if (Options.HistoryNoDuplicates) _hashedHistory = new Dictionary<string, int>();
                }
            }
            else
            {
                _currentHistoryIndex = _history.Count;
                _searchHistoryCommandCount = 0;
            }

            if (_previousHistoryItem != null)
                _previousHistoryItem.ApproximateElapsedTime = DateTime.UtcNow - _previousHistoryItem.StartTime;
        }

        private void DelayedOneTimeInitialize()
        {
            // Delayed initialization is needed so that options can be set
            // after the constuctor but have an affect before the user starts
            // editing their first command line.  For example, if the user
            // specifies a custom history save file, we don't want to try reading
            // from the default one.

            if (Options.MaximumHistoryCount == 0)
            {
                // Initialize 'MaximumHistoryCount' if it's not defined in user's profile.
                var historyCountVar = _engineIntrinsics?.SessionState.PSVariable.Get("MaximumHistoryCount");
                Options.MaximumHistoryCount = historyCountVar?.Value is int historyCountValue
                    ? historyCountValue
                    : PSConsoleReadLineOptions.DefaultMaximumHistoryCount;
            }

            if (Options.PromptText == null &&
                _engineIntrinsics?.InvokeCommand.GetCommand("prompt", CommandTypes.Function) is FunctionInfo
                    promptCommand)
            {
                var promptIsPure = null ==
                                   promptCommand.ScriptBlock.Ast.Find(ast => ast is CommandAst ||
                                                                             ast is InvokeMemberExpressionAst,
                                       true);
                if (promptIsPure)
                {
                    var res = promptCommand.ScriptBlock.InvokeReturnAsIs(Array.Empty<object>());
                    var evaluatedPrompt = res as string;
                    if (evaluatedPrompt == null && res is PSObject psobject)
                        evaluatedPrompt = psobject.BaseObject as string;

                    if (evaluatedPrompt != null)
                    {
                        int i;
                        for (i = evaluatedPrompt.Length - 1; i >= 0; i--)
                            if (!char.IsWhiteSpace(evaluatedPrompt[i]))
                                break;

                        if (i >= 0) Options.PromptText = new[] {evaluatedPrompt.Substring(i)};
                    }
                }
            }

            _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());

            _history = new HistoryQueue<HistoryItem>(Options.MaximumHistoryCount);
            _recentHistory = new HistoryQueue<string>(5);
            _currentHistoryIndex = 0;

            var readHistoryFile = true;
            try
            {
                if (Options.HistorySaveStyle == HistorySaveStyle.SaveNothing && Runspace.DefaultRunspace != null)
                    using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
                    {
                        ps.AddCommand("Microsoft.PowerShell.Core\\Get-History");
                        foreach (var historyInfo in ps.Invoke<HistoryInfo>()) AddToHistory(historyInfo.CommandLine);

                        readHistoryFile = false;
                    }
            }
            catch
            {
            }

            if (readHistoryFile) ReadHistoryFile();

            _killIndex = -1; // So first add indexes 0.
            _killRing = new List<string>(Options.MaximumKillRingCount);

            _s._readKeyWaitHandle = new AutoResetEvent(false);
            _s._keyReadWaitHandle = new AutoResetEvent(false);
            _s._closingWaitHandle = new ManualResetEvent(false);
            _s._requestKeyWaitHandles = new[]
                {_s._keyReadWaitHandle, _s._closingWaitHandle, _defaultCancellationToken.WaitHandle};
            _s._threadProcWaitHandles = new WaitHandle[]
                {_s._readKeyWaitHandle, _s._closingWaitHandle};

            // This is for a "being hosted in an alternate appdomain scenario" (the
            // DomainUnload event is not raised for the default appdomain). It allows us
            // to exit cleanly when the appdomain is unloaded but the process is not going
            // away.
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                AppDomain.CurrentDomain.DomainUnload += (x, y) =>
                {
                    _s._closingWaitHandle.Set();
                    _s._readKeyThread.Join(); // may need to wait for history to be written
                };

            _s._readKeyThread = new Thread(_s.ReadKeyThreadProc)
                {IsBackground = true, Name = "PSReadLine ReadKey Thread"};
            _s._readKeyThread.Start();
        }

        private static void Chord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue) throw new ArgumentNullException(nameof(key));

            if (_s._chordDispatchTable.TryGetValue(PSKeyInfo.FromConsoleKeyInfo(key.Value),
                    out var secondKeyDispatchTable))
            {
                var secondKey = ReadKey();
                _s.ProcessOneKey(secondKey, secondKeyDispatchTable, true, arg);
            }
        }

        /// <summary>
        ///     Abort current action, e.g. incremental history search.
        /// </summary>
        public static void Abort(ConsoleKeyInfo? key = null, object arg = null)
        {
        }

        /// <summary>
        ///     Start a new digit argument to pass to other functions.
        /// </summary>
        public static void DigitArgument(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue || char.IsControl(key.Value.KeyChar))
            {
                Ding();
                return;
            }

            if (_s.Options.EditMode == EditMode.Vi && key.Value.KeyChar == '0')
            {
                BeginningOfLine();
                return;
            }

            var sawDigit = false;
            _s._statusLinePrompt = "digit-argument: ";
            var argBuffer = _s._statusBuffer;
            argBuffer.Append(key.Value.KeyChar);
            if (key.Value.KeyChar == '-')
                argBuffer.Append('1');
            else
                sawDigit = true;

            _renderer.RenderWithPredictionQueryPaused(); // Render prompt
            while (true)
            {
                var nextKey = ReadKey();
                if (_s._dispatchTable.TryGetValue(nextKey, out var handler))
                {
                    if (handler.Action == DigitArgument)
                    {
                        if (nextKey.KeyChar == '-')
                        {
                            if (argBuffer[0] == '-')
                                argBuffer.Remove(0, 1);
                            else
                                argBuffer.Insert(0, '-');

                            _renderer.RenderWithPredictionQueryPaused(); // Render prompt
                            continue;
                        }

                        if (nextKey.KeyChar >= '0' && nextKey.KeyChar <= '9')
                        {
                            if (!sawDigit && argBuffer.Length > 0)
                                // Buffer is either '-1' or '1' from one or more Alt+- keys
                                // but no digits yet.  Remove the '1'.
                                argBuffer.Length -= 1;

                            sawDigit = true;
                            argBuffer.Append(nextKey.KeyChar);
                            _renderer.RenderWithPredictionQueryPaused(); // Render prompt
                            continue;
                        }
                    }
                    else if (handler.Action == Abort ||
                             handler.Action == CancelLine ||
                             handler.Action == CopyOrCancelLine)
                    {
                        break;
                    }
                }

                if (int.TryParse(argBuffer.ToString(), out var intArg))
                    _s.ProcessOneKey(nextKey, _s._dispatchTable, false, intArg);
                else
                    Ding();

                break;
            }

            // Remove our status line
            argBuffer.Clear();
            _s.ClearStatusMessage(true);
        }


        /// <summary>
        ///     Erases the current prompt and calls the prompt function to redisplay
        ///     the prompt.  Useful for custom key handlers that change state, e.g.
        ///     change the current directory.
        /// </summary>
        public static void InvokePrompt(ConsoleKeyInfo? key = null, object arg = null)
        {
            var console = _s.RLConsole;
            console.CursorVisible = false;

            if (arg is int newY)
            {
                console.SetCursorPosition(0, newY);
            }
            else
            {
                newY = _renderer.InitialY - _s.Options.ExtraPromptLineCount;

                console.SetCursorPosition(0, newY);

                // We need to rewrite the prompt, so blank out everything from a previous prompt invocation
                // in case the next one is shorter.
                var spaces = Spaces(console.BufferWidth);
                for (var i = 0; i < _s.Options.ExtraPromptLineCount + 1; i++) console.Write(spaces);

                console.SetCursorPosition(0, newY);
            }

            console.Write(Prompt);
            _renderer.InitialX = console.CursorLeft;
            _renderer.InitialY = console.CursorTop;
            var val = Renderer.InitialPrevRender;
            _renderer.PreviousRender = val;

            _renderer.Render();
            console.CursorVisible = true;
        }

        internal static bool IsRunningCI(IConsole console)
        {
            var consoleType = console.GetType();
            return consoleType.FullName == "Test.TestConsole"
                   || consoleType.BaseType.FullName == "Test.TestConsole";
        }
    }
}