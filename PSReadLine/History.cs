using System.Text.RegularExpressions;

namespace Microsoft.PowerShell.PSReadLine
{
    public class History
    {
        private static readonly History _s = new();
        public static History Singleton => _s;

        // Pattern used to check for sensitive inputs.
        public static Regex SensitivePattern => s_sensitivePattern;

        public const string _forwardISearchPrompt = "fwd-i-search: ";
        public const string _backwardISearchPrompt = "bck-i-search: ";
        public const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
        public const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";

        private static readonly Regex s_sensitivePattern = new(
            "password|asplaintext|token|apikey|secret",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}