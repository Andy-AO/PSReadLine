/********************************************************************++
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
    class ExitException : Exception { }

    public partial class PSConsoleReadLine : IPSConsoleReadLineMockableMethods
    {
        private const int ConsoleExiting = 1;

        private const int CancellationRequested = 2;

        // *must* be initialized in the static ctor
        // because the static member _clipboard depends upon it
        // for its own initialization
        internal static PSConsoleReadLine Singleton
        {
            get
            {
                if (_singleton == null)
                {

                    Singleton = new PSConsoleReadLine();
                }

                return _singleton;
            }
            private set => _singleton = value;
        }
        public Token[] GetCloneToken()
        {
            return (Token[])Tokens.Clone();
        }
        private Token[] Tokens
        {
            get => _tokens;
            set => _tokens = value;
        }

        private static readonly CancellationToken _defaultCancellationToken = new CancellationTokenSource().Token;

        // This is used by PowerShellEditorServices (the backend of the PowerShell VSCode extension)
        // so that it can call PSReadLine from a delegate and not hit nested pipeline issues.
#pragma warning disable CS0649
        private static Action<CancellationToken> _handleIdleOverride;
#pragma warning restore CS0649

        private bool _delayedOneTimeInitCompleted;

        private IPSConsoleReadLineMockableMethods _mockableMethods;
        private IConsole _console;
        private ICharMap _charMap;
        private Encoding _initialOutputEncoding;
        private bool _skipOutputEncodingChange;
        private EngineIntrinsics _engineIntrinsics;
        private Thread _readKeyThread;
        private AutoResetEvent _readKeyWaitHandle;
        private AutoResetEvent _keyReadWaitHandle;
        private CancellationToken _cancelReadCancellationToken;
        internal ManualResetEvent _closingWaitHandle;
        private WaitHandle[] _threadProcWaitHandles;
        private WaitHandle[] _requestKeyWaitHandles;

        private readonly StringBuilder _buffer;
        private readonly StringBuilder _statusBuffer;
        private bool _statusIsErrorMessage;
        private string _statusLinePrompt;
        private string _acceptedCommandLine;
        private List<EditItem> _edits;
        private int _editGroupStart;
        private int _undoEditIndex;
        private int _mark;
        private bool _inputAccepted;
        private readonly Queue<PSKeyInfo> _queuedKeys;
        private Stopwatch _lastRenderTime;
        private static readonly Stopwatch _readkeyStopwatch = new Stopwatch();

        // Save a fixed # of keys so we can reconstruct a repro after a crash
        private static readonly HistoryQueue<PSKeyInfo> _lastNKeys = new HistoryQueue<PSKeyInfo>(200);
        private static PSConsoleReadLine _singleton;

        // Tokens etc.
        // private List<Token> _tokens;
        private Token[] _tokens;
        private Ast _ast;
        private ParseError[] _parseErrors;

        bool IPSConsoleReadLineMockableMethods.RunspaceIsRemote(Runspace runspace)
        {
            return runspace?.ConnectionInfo != null;
        }

        private void ReadOneOrMoreKeys()
        {
            _readkeyStopwatch.Restart();
            while (_console.KeyAvailable)
            {
                // _charMap is only guaranteed to accumulate input while KeyAvailable
                // returns false. Make sure to check KeyAvailable after every ProcessKey call,
                // and clear it in a loop in case the input was something like ^[[1 which can
                // be 3, 2, or part of 1 key depending on timing.
                _charMap.ProcessKey(_console.ReadKey());
                while (_charMap.KeyAvailable)
                {
                    var key = PSKeyInfo.FromConsoleKeyInfo(_charMap.ReadKey());
                    _lastNKeys.Enqueue(key);
                    _queuedKeys.Enqueue(key);
                }
                if (_readkeyStopwatch.ElapsedMilliseconds > 2)
                {
                    // Don't spend too long in this loop if there are lots of queued keys
                    break;
                }
            }

            if (_queuedKeys.Count == 0)
            {
                while (!_charMap.KeyAvailable)
                {
                    // Don't want to block when there is an escape sequence being read.
                    if (_charMap.InEscapeSequence)
                    {
                        if (_console.KeyAvailable)
                        {
                            _charMap.ProcessKey(_console.ReadKey());
                        }
                        else
                        {
                            // We don't want to sleep for the whole escape timeout
                            // or the user will have a laggy console, but there's
                            // nothing to block on at this point either, so do a
                            // small sleep to yield the CPU while we're waiting
                            // to decide what the input was. This will only run
                            // if there are no keys waiting to be read.
                            Thread.Sleep(5);
                        }
                    }
                    else
                    {
                        _charMap.ProcessKey(_console.ReadKey());
                    }
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
                int handleId = WaitHandle.WaitAny(Singleton._threadProcWaitHandles);
                if (handleId == 1) // It was the _closingWaitHandle that was signaled.
                    break;

                var localCancellationToken = Singleton._cancelReadCancellationToken;
                ReadOneOrMoreKeys();
                if (localCancellationToken.IsCancellationRequested)
                {
                    continue;
                }

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
            Singleton._readKeyWaitHandle.Set();

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
                    handleId = WaitHandle.WaitAny(Singleton._requestKeyWaitHandles, 300);
                    if (handleId != WaitHandle.WaitTimeout)
                    {
                        break;
                    }

                    if (_handleIdleOverride is not null)
                    {
                        _handleIdleOverride(Singleton._cancelReadCancellationToken);
                        continue;
                    }

                    // If we timed out, check for event subscribers (which is just
                    // a hint that there might be an event waiting to be processed.)
                    var eventSubscribers = Singleton._engineIntrinsics?.Events.Subscribers;
                    if (eventSubscribers?.Count > 0)
                    {
                        bool runPipelineForEventProcessing = false;
                        foreach (var sub in eventSubscribers)
                        {
                            if (sub.SourceIdentifier.Equals(PSEngineEvent.OnIdle, StringComparison.OrdinalIgnoreCase))
                            {
                                // If the buffer is not empty, let's not consider we are idle because the user is in the middle of typing something.
                                if (Singleton._buffer.Length > 0)
                                {
                                    continue;
                                }

                                // There is an OnIdle event subscriber and we are idle because we timed out and the buffer is empty.
                                // Normally PowerShell generates this event, but PowerShell assumes the engine is not idle because
                                // it called PSConsoleHostReadLine which isn't returning. So we generate the event instead.
                                runPipelineForEventProcessing = true;
                                Singleton._engineIntrinsics.Events.GenerateEvent(PSEngineEvent.OnIdle, null, null, null);

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
                                ps.AddScript("0", useLocalScope: true);
                            }

                            // To detect output during possible event processing, see if the cursor moved
                            // and rerender if so.
                            var console = Singleton._console;
                            var y = console.CursorTop;
                            ps.Invoke();
                            if (y != console.CursorTop)
                            {
                                Singleton.InitialY = console.CursorTop;
                                Singleton.Render();
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
                if (Singleton.Options.HistorySaveStyle == HistorySaveStyle.SaveAtExit)
                {
                    Singleton.SaveHistoryAtExit();
                }
                Singleton._historyFileMutex.Dispose();

                throw new OperationCanceledException();
            }

            if (handleId == CancellationRequested)
            {
                // ReadLine was cancelled. Save the current line to be restored next time ReadLine
                // is called, clear the buffer and throw an exception so we can return an empty string.
                Singleton.SaveCurrentLine();
                Singleton._getNextHistoryIndex = Singleton._history.Count;
                Singleton.Current = 0;
                Singleton._buffer.Clear();
                Singleton.Render();
                throw new OperationCanceledException();
            }

            var key = Singleton._queuedKeys.Dequeue();
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
        /// Entry point - called from the PowerShell function PSConsoleHostReadLine
        /// after the prompt has been displayed.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine(Runspace runspace, EngineIntrinsics engineIntrinsics, bool? lastRunStatus)
        {
            // Use a default cancellation token instead of CancellationToken.None because the
            // WaitHandle is shared and could be triggered accidently.
            return ReadLine(runspace, engineIntrinsics, _defaultCancellationToken, lastRunStatus);
        }

        /// <summary>
        /// Entry point - called by custom PSHost implementations that require the
        /// ability to cancel ReadLine.
        /// </summary>
        /// <returns>The complete command line.</returns>
        public static string ReadLine(
            Runspace runspace,
            EngineIntrinsics engineIntrinsics,
            CancellationToken cancellationToken,
            bool? lastRunStatus)
        {
            var console = Singleton._console;

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                // System.Console doesn't handle redirected input. It matches the behavior on Windows
                // by throwing an "InvalidOperationException".
                // Therefore, if either stdin or stdout is redirected, PSReadLine doesn't really work,
                // so throw and let PowerShell call Console.ReadLine or do whatever else it decides to do.
                //
                // Some CI environments redirect stdin/stdout, but that doesn't affect our test runs
                // because the console is mocked, so we can skip the exception.
                if (!IsRunningCI(console))
                {
                    throw new NotSupportedException();
                }
            }

            var oldControlCAsInput = false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                PlatformWindows.Init(ref Singleton._charMap);
            }
            else
            {
                try
                {
                    oldControlCAsInput = Console.TreatControlCAsInput;
                    Console.TreatControlCAsInput = true;
                }
                catch { }
            }

            if (lastRunStatus.HasValue)
            {
                Singleton.ReportExecutionStatus(lastRunStatus.Value);
            }

            bool firstTime = true;
            while (true)
            {
                try
                {
                    if (firstTime)
                    {
                        firstTime = false;
                        Singleton.Initialize(runspace, engineIntrinsics);
                    }

                    Singleton._cancelReadCancellationToken = cancellationToken;
                    Singleton._requestKeyWaitHandles[2] = Singleton._cancelReadCancellationToken.WaitHandle;
                    return Singleton.InputLoop();
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
                        string.Format(CultureInfo.CurrentUICulture, PSReadLineResources.OopsCustomHandlerException, e.InnerException.Message));
                    console.ForegroundColor = oldColor;

                    var lineBeforeCrash = Singleton._buffer.ToString();
                    Singleton.Initialize(runspace, Singleton._engineIntrinsics);
                    InvokePrompt();
                    Insert(lineBeforeCrash);
                }
                catch (Exception e)
                {
                    // If we're running tests, just throw.
                    if (Singleton._mockableMethods != Singleton)
                    {
                        throw;
                    }

                    while (e.InnerException != null)
                    {
                        e = e.InnerException;
                    }
                    var oldColor = console.ForegroundColor;
                    console.ForegroundColor = ConsoleColor.Red;
                    console.WriteLine(PSReadLineResources.OopsAnErrorMessage1);
                    console.ForegroundColor = oldColor;
                    var sb = new StringBuilder();
                    for (int i = 0; i < _lastNKeys.Count; i++)
                    {
                        sb.Append(' ');
                        sb.Append(_lastNKeys[i].KeyStr);

                        if (Singleton._dispatchTable.TryGetValue(_lastNKeys[i], out var handler) &&
                            "AcceptLine".Equals(handler.BriefDescription, StringComparison.OrdinalIgnoreCase))
                        {
                            // Make it a little easier to see the keys
                            sb.Append('\n');
                        }
                    }

                    var psVersion = PSObject.AsPSObject(engineIntrinsics.Host.Version).ToString();
                    var ourVersion = typeof(PSConsoleReadLine).Assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>().First().InformationalVersion;
                    var osInfo = RuntimeInformation.OSDescription;
                    var bufferWidth = console.BufferWidth;
                    var bufferHeight = console.BufferHeight;

                    console.WriteLine(string.Format(CultureInfo.CurrentUICulture, PSReadLineResources.OopsAnErrorMessage2,
                        ourVersion, psVersion, osInfo, bufferWidth, bufferHeight,
                        _lastNKeys.Count, sb, e));
                    var lineBeforeCrash = Singleton._buffer.ToString();
                    Singleton.Initialize(runspace, Singleton._engineIntrinsics);
                    InvokePrompt();
                    Insert(lineBeforeCrash);
                }
                finally
                {
                    try
                    {
                        // If we are closing, restoring the old console settings isn't needed,
                        // and some operating systems, it can cause a hang.
                        if (!Singleton._closingWaitHandle.WaitOne(0))
                        {
                            console.OutputEncoding = Singleton._initialOutputEncoding;

                            bool IsValid(ConsoleColor color)
                            {
                                return color >= ConsoleColor.Black && color <= ConsoleColor.White;
                            }
                            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                Console.TreatControlCAsInput = oldControlCAsInput;
                            }
                        }
                    }
                    catch { }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        PlatformWindows.Complete();
                    }
                }
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
                ProcessOneKey(key, _dispatchTable, ignoreIfNoAction: false, arg: null);
                if (_inputAccepted)
                {
                    _acceptedCommandLine = _buffer.ToString();
                    MaybeAddToHistory(_acceptedCommandLine, _edits, _undoEditIndex);

                    _prediction.OnCommandLineAccepted(_acceptedCommandLine);
                    return _acceptedCommandLine;
                }

                if (killCommandCount == _killCommandCount)
                {
                    // Reset kill command count if it didn't change
                    _killCommandCount = 0;
                }
                if (yankCommandCount == _yankCommandCount)
                {
                    // Reset yank command count if it didn't change
                    _yankCommandCount = 0;
                }
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
                        EmphasisStart = -1;
                        EmphasisLength = 0;
                        RenderWithPredictionQueryPaused();
                    }
                    _searchHistoryCommandCount = 0;
                    _searchHistoryPrefix = null;
                }
                if (recallHistoryCommandCount == _recallHistoryCommandCount)
                {
                    _recallHistoryCommandCount = 0;
                }
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
                    RenderWithPredictionQueryPaused();  // Clears the visual selection
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

        T CallPossibleExternalApplication<T>(Func<T> func)
        {
            try
            {
                _console.OutputEncoding = _initialOutputEncoding;
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? PlatformWindows.CallPossibleExternalApplication(func)
                    : func();
            }
            finally
            {
                if (!_skipOutputEncodingChange)
                {
                    _console.OutputEncoding = Encoding.UTF8;
                }
            }
        }

        void CallPossibleExternalApplication(Action action)
        {
            CallPossibleExternalApplication<object>(() => { action(); return null; });
        }

        void ProcessOneKey(PSKeyInfo key, Dictionary<PSKeyInfo, KeyHandler> dispatchTable, bool ignoreIfNoAction, object arg)
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
            {
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
            }

            if (handler != null)
            {
                if (handler.ScriptBlock != null)
                {
                    CallPossibleExternalApplication(() => handler.Action(consoleKey, arg));
                }
                else
                {
                    handler.Action(consoleKey, arg);
                }
            }
            else if (!ignoreIfNoAction)
            {
                SelfInsert(consoleKey, arg);
            }
        }

        static PSConsoleReadLine()
        {
            _viRegister = new ViRegister(Singleton);
        }

        private PSConsoleReadLine()
        {
            _mockableMethods = this;
            _console = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? PlatformWindows.OneTimeInit(this)
                : new VirtualTerminal();
            _charMap = new DotNetCharMap();

            _buffer = new StringBuilder(8 * 1024);
            _statusBuffer = new StringBuilder(256);
            _savedCurrentLine = new HistoryItem();
            _queuedKeys = new Queue<PSKeyInfo>();

            string hostName = null;
            // This works mostly by luck - we're not doing anything to guarantee the constructor for our
            // singleton is called on a thread with a runspace, but it is happening by coincidence.
            using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                try
                {
                    var results = ps.AddScript("$Host", useLocalScope: true).Invoke<PSHost>();
                    PSHost host = results.Count == 1 ? results[0] : null;
                    hostName = host?.Name;
                }
                catch
                {
                }
            }
            if (hostName == null)
            {
                hostName = PSReadLine;
            }
            _options = new PSConsoleReadLineOptions(hostName);
            _prediction = new Prediction(this);
            SetDefaultBindings(_options.EditMode);
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

            PreviousRender = InitialPrevRender;
            PreviousRender.bufferWidth = _console.BufferWidth;
            PreviousRender.bufferHeight = _console.BufferHeight;
            PreviousRender.errorPrompt = false;
            _buffer.Clear();
            _edits = new List<EditItem>();
            _undoEditIndex = 0;
            _editGroupStart = -1;
            Current = 0;
            _mark = 0;
            EmphasisStart = -1;
            EmphasisLength = 0;
            _ast = null;
            Tokens = null;
            _parseErrors = null;
            _inputAccepted = false;
            InitialX = _console.CursorLeft;
            InitialY = _console.CursorTop;
            _statusIsErrorMessage = false;

            _initialOutputEncoding = _console.OutputEncoding;
            _prediction.Reset();

            // Don't change the OutputEncoding if already UTF8, no console, or using raster font on Windows
            _skipOutputEncodingChange = _initialOutputEncoding == Encoding.UTF8
                || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    && PlatformWindows.IsConsoleInput()
                    && PlatformWindows.IsUsingRasterFont());

            if (!_skipOutputEncodingChange)
            {
                _console.OutputEncoding = Encoding.UTF8;
            }

            _lastRenderTime = Stopwatch.StartNew();

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
                    if (Options.HistoryNoDuplicates)
                    {
                        _hashedHistory = new Dictionary<string, int>();
                    }
                }
            }
            else
            {
                _currentHistoryIndex = _history.Count;
                _searchHistoryCommandCount = 0;
            }
            if (_previousHistoryItem != null)
            {
                _previousHistoryItem.ApproximateElapsedTime = DateTime.UtcNow - _previousHistoryItem.StartTime;
            }
        }

        private void DelayedOneTimeInitialize()
        {
            // Delayed initialization is needed so that options can be set
            // after the constuctor but have an affect before the user starts
            // editing their first command line.  For example, if the user
            // specifies a custom history save file, we don't want to try reading
            // from the default one.

            if (_options.MaximumHistoryCount == 0)
            {
                // Initialize 'MaximumHistoryCount' if it's not defined in user's profile.
                var historyCountVar = _engineIntrinsics?.SessionState.PSVariable.Get("MaximumHistoryCount");
                _options.MaximumHistoryCount = (historyCountVar?.Value is int historyCountValue)
                    ? historyCountValue
                    : PSConsoleReadLineOptions.DefaultMaximumHistoryCount;
            }

            if (_options.PromptText == null &&
                _engineIntrinsics?.InvokeCommand.GetCommand("prompt", CommandTypes.Function) is FunctionInfo promptCommand)
            {
                var promptIsPure = null ==
                    promptCommand.ScriptBlock.Ast.Find(ast => ast is CommandAst ||
                                                      ast is InvokeMemberExpressionAst,
                                               searchNestedScriptBlocks: true);
                if (promptIsPure)
                {
                    var res = promptCommand.ScriptBlock.InvokeReturnAsIs(Array.Empty<object>());
                    string evaluatedPrompt = res as string;
                    if (evaluatedPrompt == null && res is PSObject psobject)
                    {
                        evaluatedPrompt = psobject.BaseObject as string;
                    }
                    if (evaluatedPrompt != null)
                    {
                        int i;
                        for (i = evaluatedPrompt.Length - 1; i >= 0; i--)
                        {
                            if (!char.IsWhiteSpace(evaluatedPrompt[i])) break;
                        }

                        if (i >= 0)
                        {
                            _options.PromptText = new[] { evaluatedPrompt.Substring(i) };
                        }
                    }
                }
            }

            _historyFileMutex = new Mutex(false, GetHistorySaveFileMutexName());

            _history = new HistoryQueue<HistoryItem>(Options.MaximumHistoryCount);
            _recentHistory = new HistoryQueue<string>(capacity: 5);
            _currentHistoryIndex = 0;

            bool readHistoryFile = true;
            try
            {
                if (_options.HistorySaveStyle == HistorySaveStyle.SaveNothing && Runspace.DefaultRunspace != null)
                {
                    using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
                    {
                        ps.AddCommand("Microsoft.PowerShell.Core\\Get-History");
                        foreach (var historyInfo in ps.Invoke<HistoryInfo>())
                        {
                            AddToHistory(historyInfo.CommandLine);
                        }
                        readHistoryFile = false;
                    }
                }
            }
            catch
            {
            }

            if (readHistoryFile)
            {
                ReadHistoryFile();
            }

            _killIndex = -1; // So first add indexes 0.
            _killRing = new List<string>(Options.MaximumKillRingCount);

            Singleton._readKeyWaitHandle = new AutoResetEvent(false);
            Singleton._keyReadWaitHandle = new AutoResetEvent(false);
            Singleton._closingWaitHandle = new ManualResetEvent(false);
            Singleton._requestKeyWaitHandles = new WaitHandle[] { Singleton._keyReadWaitHandle, Singleton._closingWaitHandle, _defaultCancellationToken.WaitHandle };
            Singleton._threadProcWaitHandles = new WaitHandle[] { Singleton._readKeyWaitHandle, Singleton._closingWaitHandle };

            // This is for a "being hosted in an alternate appdomain scenario" (the
            // DomainUnload event is not raised for the default appdomain). It allows us
            // to exit cleanly when the appdomain is unloaded but the process is not going
            // away.
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
            {
                AppDomain.CurrentDomain.DomainUnload += (x, y) =>
                {
                    Singleton._closingWaitHandle.Set();
                    Singleton._readKeyThread.Join(); // may need to wait for history to be written
                };
            }

            Singleton._readKeyThread = new Thread(Singleton.ReadKeyThreadProc) { IsBackground = true, Name = "PSReadLine ReadKey Thread" };
            Singleton._readKeyThread.Start();
        }

        private static void Chord(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (Singleton._chordDispatchTable.TryGetValue(PSKeyInfo.FromConsoleKeyInfo(key.Value), out var secondKeyDispatchTable))
            {
                var secondKey = ReadKey();
                Singleton.ProcessOneKey(secondKey, secondKeyDispatchTable, ignoreIfNoAction: true, arg: arg);
            }
        }

        /// <summary>
        /// Abort current action, e.g. incremental history search.
        /// </summary>
        public static void Abort(ConsoleKeyInfo? key = null, object arg = null)
        {
        }

        /// <summary>
        /// Start a new digit argument to pass to other functions.
        /// </summary>
        public static void DigitArgument(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (!key.HasValue || char.IsControl(key.Value.KeyChar))
            {
                Ding();
                return;
            }

            if (Singleton._options.EditMode == EditMode.Vi && key.Value.KeyChar == '0')
            {
                BeginningOfLine();
                return;
            }

            bool sawDigit = false;
            Singleton._statusLinePrompt = "digit-argument: ";
            var argBuffer = Singleton._statusBuffer;
            argBuffer.Append(key.Value.KeyChar);
            if (key.Value.KeyChar == '-')
            {
                argBuffer.Append('1');
            }
            else
            {
                sawDigit = true;
            }

            Singleton.RenderWithPredictionQueryPaused(); // Render prompt
            while (true)
            {
                var nextKey = ReadKey();
                if (Singleton._dispatchTable.TryGetValue(nextKey, out var handler))
                {
                    if (handler.Action == DigitArgument)
                    {
                        if (nextKey.KeyChar == '-')
                        {
                            if (argBuffer[0] == '-')
                            {
                                argBuffer.Remove(0, 1);
                            }
                            else
                            {
                                argBuffer.Insert(0, '-');
                            }
                            Singleton.RenderWithPredictionQueryPaused(); // Render prompt
                            continue;
                        }

                        if (nextKey.KeyChar >= '0' && nextKey.KeyChar <= '9')
                        {
                            if (!sawDigit && argBuffer.Length > 0)
                            {
                                // Buffer is either '-1' or '1' from one or more Alt+- keys
                                // but no digits yet.  Remove the '1'.
                                argBuffer.Length -= 1;
                            }
                            sawDigit = true;
                            argBuffer.Append(nextKey.KeyChar);
                            Singleton.RenderWithPredictionQueryPaused(); // Render prompt
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
                {
                    Singleton.ProcessOneKey(nextKey, Singleton._dispatchTable, ignoreIfNoAction: false, arg: intArg);
                }
                else
                {
                    Ding();
                }
                break;
            }

            // Remove our status line
            argBuffer.Clear();
            Singleton.ClearStatusMessage(render: true);
        }


        /// <summary>
        /// Erases the current prompt and calls the prompt function to redisplay
        /// the prompt.  Useful for custom key handlers that change state, e.g.
        /// change the current directory.
        /// </summary>
        public static void InvokePrompt(ConsoleKeyInfo? key = null, object arg = null)
        {
            var console = Singleton._console;
            console.CursorVisible = false;

            if (arg is int newY)
            {
                console.SetCursorPosition(0, newY);
            }
            else
            {
                newY = Singleton.InitialY - Singleton._options.ExtraPromptLineCount;

                console.SetCursorPosition(0, newY);

                // We need to rewrite the prompt, so blank out everything from a previous prompt invocation
                // in case the next one is shorter.
                var spaces = Spaces(console.BufferWidth);
                for (int i = 0; i < Singleton._options.ExtraPromptLineCount + 1; i++)
                {
                    console.Write(spaces);
                }

                console.SetCursorPosition(0, newY);
            }

            string newPrompt = GetPrompt();

            console.Write(newPrompt);
            Singleton.InitialX = console.CursorLeft;
            Singleton.InitialY = console.CursorTop;
            Singleton.PreviousRender = InitialPrevRender;

            Singleton.Render();
            console.CursorVisible = true;
        }

        private static string GetPrompt()
        {
            string newPrompt = null;

            try
            {
                if (Singleton._runspace?.Debugger != null && Singleton._runspace.Debugger.InBreakpoint)
                {
                    // Run prompt command in debugger API to ensure it is run correctly on the runspace.
                    // This handles remote runspace debugging and nested debugger scenarios.
                    PSDataCollection<PSObject> results = new PSDataCollection<PSObject>();
                    var command = new PSCommand();
                    command.AddCommand("prompt");
                    Singleton._runspace.Debugger.ProcessCommand(
                        command,
                        results);

                    if (results.Count == 1)
                        newPrompt = results[0].BaseObject as string;
                }
                else
                {
                    var runspaceIsRemote = Singleton._mockableMethods.RunspaceIsRemote(Singleton._runspace);

                    System.Management.Automation.PowerShell ps;
                    if (!runspaceIsRemote)
                    {
                        ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace);
                    }
                    else
                    {
                        ps = System.Management.Automation.PowerShell.Create();
                        ps.Runspace = Singleton._runspace;
                    }

                    using (ps)
                    {
                        ps.AddCommand("prompt");
                        var result = ps.Invoke<string>();
                        if (result.Count == 1)
                        {
                            newPrompt = result[0];

                            if (runspaceIsRemote)
                            {
                                if (!string.IsNullOrEmpty(Singleton._runspace?.ConnectionInfo?.ComputerName))
                                {
                                    newPrompt = "[" + (Singleton._runspace?.ConnectionInfo).ComputerName + "]: " + newPrompt;
                                }
                            }
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

        internal static bool IsRunningCI(IConsole console)
        {
            Type consoleType = console.GetType();
            return consoleType.FullName == "Test.TestConsole"
                || consoleType.BaseType.FullName == "Test.TestConsole";
        }
    }
}
