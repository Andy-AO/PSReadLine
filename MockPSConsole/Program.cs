using System;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using Microsoft.PowerShell;
using Microsoft.PowerShell.PSReadLine;

namespace MockPSConsole
{
    public enum StandardHandleId : uint
    {
        Error  = unchecked((uint)-12),
        Output = unchecked((uint)-11),
        Input  = unchecked((uint)-10),
    }

    class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetConsoleMode(IntPtr hConsoleOutput, out uint dwMode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetConsoleMode(IntPtr hConsoleOutput, uint dwMode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetStdHandle(uint handleId);

        static void CauseCrash(ConsoleKeyInfo? key = null, object arg = null)
        {
            throw new Exception("intentional crash for test purposes");
        }

        public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x04;
        public const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

        [STAThread]
        static void Main()
        {
            var handle = GetStdHandle((uint)StandardHandleId.Output);
            GetConsoleMode(handle, out var mode);
            var vtEnabled = SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);

            var iss = InitialSessionState.CreateDefault2();
            var rs = RunspaceFactory.CreateRunspace(iss);
            rs.Open();
            Runspace.DefaultRunspace = rs;

            PSConsoleReadLine.SetOptions(new SetPSReadLineOption
            {
                EditMode = EditMode.Emacs,
                HistoryNoDuplicates = false,
            });

            if (vtEnabled)
            {
                var options = PSConsoleReadLine.GetOptions();
                options.CommandColor = "#8181f7";
                options.StringColor = "\x1b[38;5;100m";
            }
            PSConsoleReadLine.SetKeyHandler(new[] {"Ctrl+LeftArrow"}, PSConsoleReadLine.ShellBackwardWord, "ShellBackwardWord", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"Ctrl+RightArrow"}, PSConsoleReadLine.ShellNextWord, "ShellNextWord", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"F4"}, PSConsoleReadLine.HistorySearchBackward, "HistorySearchBackward", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"F5"}, History.HistorySearchForward, "HistorySearchForward", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"Ctrl+d,Ctrl+c"}, PSConsoleReadLine.CaptureScreen, "CaptureScreen", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"Ctrl+d,Ctrl+p"}, PSConsoleReadLine.InvokePrompt, "InvokePrompt", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"Ctrl+d,Ctrl+x"}, CauseCrash, "CauseCrash", "Throw exception to test error handling");
            PSConsoleReadLine.SetKeyHandler(new[] {"F6"}, PSConsoleReadLine.PreviousLine, "PreviousLine", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"F7"}, PSConsoleReadLine.NextLine, "NextLine", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"F2"}, PSConsoleReadLine.ValidateAndAcceptLine, "ValidateAndAcceptLine", "");
            PSConsoleReadLine.SetKeyHandler(new[] {"Enter"}, PSConsoleReadLine.AcceptLine, "AcceptLine", "");

            using (var ps = PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                var executionContext = ps.AddScript("$ExecutionContext").Invoke<EngineIntrinsics>().First();

                // Detect if the read loop will enter VT input mode.
                var vtInputEnvVar = Environment.GetEnvironmentVariable("PSREADLINE_VTINPUT");
                var stdin = GetStdHandle((uint)StandardHandleId.Input);
                GetConsoleMode(stdin, out var inputMode);
                if (vtInputEnvVar == "1" || (inputMode & ENABLE_VIRTUAL_TERMINAL_INPUT) != 0)
                {
                    Console.WriteLine("\x1b[33mDefault input mode = virtual terminal\x1b[m");
                }
                else
                {
                    Console.WriteLine("\x1b[33mDefault input mode = Windows\x1b[m");
                }

                // This is a workaround to ensure the command analysis cache has been created before
                // we enter into ReadLine.  It's a little slow and infrequently needed, so just
                // uncomment if you hit a hang, run it once, then comment it out again.
                //ps.Commands.Clear();
                //ps.AddCommand("Get-Command").Invoke();

                executionContext.InvokeProvider.Item.Set("function:prompt", ScriptBlock.Create("'TestHostPS> '"));

                while (true)
                {
                    ps.Commands.Clear();
                    Console.Write(string.Join("", ps.AddCommand("prompt").Invoke<string>()));

                    var line = PSConsoleReadLine.ReadLine(rs, executionContext, lastRunStatus: null);
                    Console.WriteLine(line);
                    line = line.Trim();
                    if (line.Equals("exit"))
                        Environment.Exit(0);
                    if (line.Equals("cmd"))
                        PSConsoleReadLine.SetOptions(new SetPSReadLineOption {EditMode = EditMode.Windows});
                    if (line.Equals("emacs"))
                        PSConsoleReadLine.SetOptions(new SetPSReadLineOption {EditMode = EditMode.Emacs});
                    if (line.Equals("vi"))
                        PSConsoleReadLine.SetOptions(new SetPSReadLineOption {EditMode = EditMode.Vi});
                    if (line.Equals("nodupes"))
                        PSConsoleReadLine.SetOptions(new SetPSReadLineOption {HistoryNoDuplicates = true});
                    if (line.Equals("vtinput"))
                        Environment.SetEnvironmentVariable("PSREADLINE_VTINPUT", "1");
                    if (line.Equals("novtinput"))
                        Environment.SetEnvironmentVariable("PSREADLINE_VTINPUT", "0");
                }
            }
        }
    }
}
