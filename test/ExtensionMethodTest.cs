using System;
using System.Linq;
using Xunit;
using UnitTestPSReadLine;

namespace Test
{
    public class ExtensionMethodTest
    {
        [SkippableFact]
        public void TestShowContext()
        {
            string[] context = new[] { "DSadf_L03ad2KF", @"[adj] 公众的，大众的" };
            foreach (string contextItem in context) {
                CHAR_INFO[] arr = GetCHAR_INFOArray(contextItem);
                Assert.Equal(contextItem, arr.ShowContext());
            }
        }

        private CHAR_INFO[] GetCHAR_INFOArray(string context)
        {
            return context.Select(x => new CHAR_INFO(x, ConsoleColor.Black, ConsoleColor.White))
                .ToArray();
        }
    }
}