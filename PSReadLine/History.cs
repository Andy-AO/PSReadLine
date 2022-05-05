namespace Microsoft.PowerShell.PSReadLine
{
    public class History
    {

        private static readonly History _s = new();
        public static History Singleton => _s;

        public const string _forwardISearchPrompt = "fwd-i-search: ";
        public const string _backwardISearchPrompt = "bck-i-search: ";
        public const string _failedForwardISearchPrompt = "failed-fwd-i-search: ";
        public const string _failedBackwardISearchPrompt = "failed-bck-i-search: ";
    }
}