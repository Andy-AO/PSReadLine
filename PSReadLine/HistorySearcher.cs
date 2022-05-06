using System;
using System.Text;

namespace Microsoft.PowerShell.PSReadLine
{
    public class HistorySearcher
    {
        public int searchPositions { get; set; }
        public int SearchFromPoint { get; set; }
        public StringBuilder toMatch { get; set; }
        public PSKeyInfo key { get; set; }
        public Action<ConsoleKeyInfo?, object> function { get; set; }
    }
}