using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Microsoft.PowerShell.PSReadLine
{
    public class History
    {
        private static readonly History _s = new();
        public static History Singleton => _s;

        // Pattern used to check for sensitive inputs.
        public static Regex SensitivePattern => s_sensitivePattern;

        public static HashSet<string> SecretMgmtCommands => s_SecretMgmtCommands;

        public int AnyHistoryCommandCount
        {
            get => _anyHistoryCommandCount;
            set => _anyHistoryCommandCount = value;
        }

        public const string _forwardISearchPrompt = "fwd-i-search: ";
        public const string _backwardISearchPrompt = "bck-i-search: ";
        public const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
        public const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";

        private static readonly HashSet<string> s_SecretMgmtCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "Get-Secret",
            "Get-SecretInfo",
            "Get-SecretVault",
            "Register-SecretVault",
            "Remove-Secret",
            "Set-SecretInfo",
            "Set-SecretVaultDefault",
            "Test-SecretVault",
            "Unlock-SecretVault",
            "Unregister-SecretVault"
        };

        public int CurrentHistoryIndex
        {
            get => _currentHistoryIndex;
            set => _currentHistoryIndex = value;
        }

        // History state
        public HistoryQueue<HistoryItem> Historys
        {
            get => _history;
            set => _history = value;
        }

        // When cycling through history, the current line (not yet added to history)
        // is saved here so it can be restored.
        public readonly HistoryItem _savedCurrentLine = new HistoryItem();

        private static readonly Regex s_sensitivePattern = new(
            "password|asplaintext|token|apikey|secret",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private int _anyHistoryCommandCount;
        private int _currentHistoryIndex;
        private int _getNextHistoryIndex;
        private HistoryQueue<HistoryItem> _history;
        private Dictionary<string, int> _hashedHistory;
        private int historyErrorReportedCount;
        private long _historyFileLastSavedSize;
        private Mutex _historyFileMutex;

        public int GetNextHistoryIndex
        {
            get => _getNextHistoryIndex;
            set => _getNextHistoryIndex = value;
        }

        public Dictionary<string, int> HashedHistory
        {
            get => _hashedHistory;
            set => _hashedHistory = value;
        }

        public long HistoryFileLastSavedSize
        {
            get => _historyFileLastSavedSize;
            set => _historyFileLastSavedSize = value;
        }

        public Mutex HistoryFileMutex
        {
            get => _historyFileMutex;
            set => _historyFileMutex = value;
        }

        public HistoryItem PreviousHistoryItem
        {
            get => _previousHistoryItem;
            set => _previousHistoryItem = value;
        }

        public int RecallHistoryCommandCount
        {
            get => _recallHistoryCommandCount;
            set => _recallHistoryCommandCount = value;
        }

        public HistoryQueue<string> RecentHistory
        {
            get => _recentHistory;
            set => _recentHistory = value;
        }

        public int SearchHistoryCommandCount
        {
            get => _searchHistoryCommandCount;
            set => _searchHistoryCommandCount = value;
        }

        public string SearchHistoryPrefix
        {
            get => _searchHistoryPrefix;
            set => _searchHistoryPrefix = value;
        }

        public int HistoryErrorReportedCount
        {
            get => historyErrorReportedCount;
            set => historyErrorReportedCount = value;
        }

        private HistoryItem _previousHistoryItem;
        private int _recallHistoryCommandCount;
        private HistoryQueue<string> _recentHistory;
        private int _searchHistoryCommandCount;
        private string _searchHistoryPrefix;
    }
}