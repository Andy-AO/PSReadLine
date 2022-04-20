using System;
using System.Globalization;

namespace Microsoft.PowerShell
{
    internal struct Point
    {
        public int X;
        public int Y;

        public override string ToString()
        {
            return String.Format(CultureInfo.InvariantCulture, "{0},{1}", X, Y);
        }
    }
}