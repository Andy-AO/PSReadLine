using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Microsoft.PowerShell.PSReadLine
{
    public class History
    {
        private static readonly History _s = new();
        public static History Singleton => _s;

        // Pattern used to check for sensitive inputs.
        public static Regex SensitivePattern => s_sensitivePattern;

        public static HashSet<string> SecretMgmtCommands => s_SecretMgmtCommands;

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

        // When cycling through history, the current line (not yet added to history)
        // is saved here so it can be restored.
        public readonly HistoryItem _savedCurrentLine = new HistoryItem();

        private static readonly Regex s_sensitivePattern = new(
            "password|asplaintext|token|apikey|secret",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}