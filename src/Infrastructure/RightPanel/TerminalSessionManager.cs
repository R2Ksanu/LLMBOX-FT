using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LlmodelBox.Infrastructure.RightPanel
{
    public enum TerminalShellKind
    {
        PowerShell,
        Cmd,
        GitBash
    }

    public sealed class TerminalSessionInfo
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Terminal";
        public TerminalShellKind Shell { get; init; }
        public string WorkingDirectory { get; init; } = string.Empty;
        public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool UsesConPty { get; internal set; }
        public bool IsRunning { get; internal set; }
        public int? ExitCode { get; internal set; }
        public List<string> CommandHistory { get; } = new();
    }

    /// <summary>
    /// Persistent multi-session terminal host supporting PowerShell, CMD, and Git Bash.
    /// Uses native Windows ConPTY (CreatePseudoConsole) when running in an interactive desktop
    /// session, with seamless fallback to persistent redirected processes if pseudo-console
    /// creation is restricted by the host environment.
    /// </summary>
    public interface ITerminalSessionManager : IDisposable
    {
        IReadOnlyList<TerminalSessionInfo> Sessions { get; }
        event Action<string, string>? OutputReceived;
        event Action<TerminalSessionInfo>? SessionStateChanged;
        TerminalSessionInfo CreateSession(
            TerminalShellKind shell,
            string workingDirectory,
            Dictionary<string, string>? envVars = null,
            bool preferConPty = true);
        Task SendInputAsync(string sessionId, string commandLine);
        void Interrupt(string sessionId);
        void CloseSession(string sessionId);
        string GetTranscript(string sessionId);
        void ClearOutput(string sessionId);
        void Resize(string sessionId, short cols, short rows);
        string? DetectGitBashPath();
    }

    public sealed class TerminalSessionManager : ITerminalSessionManager
    {
        private sealed class ActiveRuntime : IDisposable
        {
            public required TerminalSessionInfo Info { get; init; }
            public StringBuilder Transcript { get; } = new();
            public object Gate { get; } = new();
            public CancellationTokenSource Cts { get; } = new();
            public string EscapeCarry { get; set; } = string.Empty;

            // Standard Process mode
            public Process? Process { get; set; }
            public StreamWriter? ProcessStdin { get; set; }

            // Native Windows ConPTY mode
            public IntPtr PseudoConsoleHandle { get; set; } = IntPtr.Zero;
            public IntPtr NativeProcessHandle { get; set; } = IntPtr.Zero;
            public IntPtr NativeThreadHandle { get; set; } = IntPtr.Zero;
            public int NativeProcessId { get; set; }
            public SafeFileHandle? ConPtyInputWrite { get; set; }
            public SafeFileHandle? ConPtyOutputRead { get; set; }
            public FileStream? ConPtyWriteStream { get; set; }

            public void Dispose()
            {
                try { Cts.Cancel(); } catch { }
                try
                {
                    if (Process != null && !Process.HasExited)
                    {
                        Process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                try { Process?.Dispose(); } catch { }

                try
                {
                    if (NativeProcessHandle != IntPtr.Zero)
                    {
                        ConPtyInterop.TerminateProcess(NativeProcessHandle, 1);
                    }
                }
                catch { }

                if (PseudoConsoleHandle != IntPtr.Zero)
                {
                    try { ConPtyInterop.ClosePseudoConsole(PseudoConsoleHandle); } catch { }
                    PseudoConsoleHandle = IntPtr.Zero;
                }
                if (NativeThreadHandle != IntPtr.Zero)
                {
                    try { ConPtyInterop.CloseHandle(NativeThreadHandle); } catch { }
                    NativeThreadHandle = IntPtr.Zero;
                }
                if (NativeProcessHandle != IntPtr.Zero)
                {
                    try { ConPtyInterop.CloseHandle(NativeProcessHandle); } catch { }
                    NativeProcessHandle = IntPtr.Zero;
                }

                try { ConPtyWriteStream?.Dispose(); } catch { }
                try { ConPtyInputWrite?.Dispose(); } catch { }
                try { ConPtyOutputRead?.Dispose(); } catch { }
                Cts.Dispose();
            }
        }

        private static readonly Regex AnsiRegex = new(
            @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)" +         // OSC sequences (window title, CWD, hyperlinks)
            @"|\x1B[P^_][^\x07\x1B]*(?:\x07|\x1B\\)" +     // DCS / PM / APC sequences
            @"|\x1B\[[0-?]*[ -/]*[@-~]" +                  // CSI sequences (colors, cursor move, erase)
            @"|\x1B[@-OQ-Z\\-]" +                          // 2-byte ESC sequences (explicitly excluding [ ] P ^ _)
            @"|\x9B[0-?]*[ -/]*[@-~]" +                    // 8-bit CSI
            @"|\x9D[^\x07\x9C]*[\x07\x9C]",                // 8-bit OSC
            RegexOptions.Compiled);
        private static readonly Regex InlinePromptConcatRegex = new(@"(?<=\S)(PS [A-Za-z]:\\[^\r\n>]*>)", RegexOptions.Compiled);

        /// <summary>
        /// Strips ANSI, CSI, and OSC ConPTY control sequences cleanly even when split across
        /// async pseudo-console pipe read boundaries.
        /// </summary>
        public static string SanitizeTerminalChunk(string raw, ref string carry)
        {
            if (string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(carry)) return string.Empty;

            string combined = string.IsNullOrEmpty(carry) ? raw : carry + raw;
            carry = string.Empty;

            // Detect trailing incomplete ESC/OSC sequence so partial buffer boundaries don't leak "0;C:\...\powershell.exe"
            int lastEsc = combined.LastIndexOf('\x1B');
            if (lastEsc >= 0 && lastEsc >= combined.Length - 2048)
            {
                string tail = combined.Substring(lastEsc);
                if (!AnsiRegex.IsMatch(tail))
                {
                    bool looksIncomplete =
                        tail.Length == 1 ||
                        tail[1] == ']' ||
                        tail[1] == '[' ||
                        tail[1] == 'P' ||
                        tail[1] == '^' ||
                        tail[1] == '_';
                    if (looksIncomplete)
                    {
                        carry = tail;
                        combined = combined.Substring(0, lastEsc);
                    }
                }
            }

            if (string.IsNullOrEmpty(combined)) return string.Empty;

            string cleaned = AnsiRegex.Replace(combined, string.Empty);

            // Normalize CRLF -> LF, then drop stray CR cursor resets before inserting environment newlines
            cleaned = cleaned.Replace("\r\n", "\n");
            cleaned = cleaned.Replace('\r', '\n');

            // Remove stray C0 control chars except \n and \t
            var sb = new StringBuilder(cleaned.Length);
            foreach (char ch in cleaned)
            {
                if (ch == '\n' || ch == '\t' || (!char.IsControl(ch) && ch != '\x7F'))
                {
                    sb.Append(ch);
                }
            }

            string normalized = sb.ToString();
            // Ensure concatenated prompt after command output sits on a clean line
            normalized = InlinePromptConcatRegex.Replace(normalized, "\n$1");
            // Collapse excessive blank lines generated by ConPTY viewport redraws
            while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
            }

            if (Environment.NewLine != "\n")
            {
                normalized = normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
            }
            return normalized;
        }
        private readonly object _sessionsGate = new();
        private readonly List<ActiveRuntime> _runtimes = new();
        private int _counter = 0;

        public IReadOnlyList<TerminalSessionInfo> Sessions
        {
            get { lock (_sessionsGate) return _runtimes.Select(r => r.Info).ToList(); }
        }

        public event Action<string, string>? OutputReceived;
        public event Action<TerminalSessionInfo>? SessionStateChanged;

        public TerminalSessionInfo CreateSession(
            TerminalShellKind shell,
            string workingDirectory,
            Dictionary<string, string>? envVars = null,
            bool preferConPty = true)
        {
            string cwd = Directory.Exists(workingDirectory) ? Path.GetFullPath(workingDirectory) : Environment.CurrentDirectory;
            int idx = Interlocked.Increment(ref _counter);
            string exe = ResolveShellExecutable(shell, out string args);

            var info = new TerminalSessionInfo
            {
                Title = $"{shell} #{idx}",
                Shell = shell,
                WorkingDirectory = cwd,
                EnvironmentVariables = envVars != null
                    ? new Dictionary<string, string>(envVars, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                IsRunning = true
            };

            var runtime = new ActiveRuntime { Info = info };
            lock (_sessionsGate) _runtimes.Add(runtime);

            bool started = preferConPty && OperatingSystem.IsWindows() && TryStartConPty(runtime, exe, args, cwd);
            if (!started)
            {
                StartStandardProcess(runtime, exe, args, cwd);
            }

            AppendOutput(runtime, $"[Started {info.Shell} ({(info.UsesConPty ? "ConPTY" : "Pipe")}) in {cwd}]{Environment.NewLine}");
            SessionStateChanged?.Invoke(info);
            return info;
        }

        public async Task SendInputAsync(string sessionId, string commandLine)
        {
            var rt = FindRuntime(sessionId);
            if (rt == null || !rt.Info.IsRunning) return;

            string line = commandLine ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(line))
            {
                lock (rt.Gate)
                {
                    if (rt.Info.CommandHistory.Count == 0 || !string.Equals(rt.Info.CommandHistory[^1], line.Trim(), StringComparison.Ordinal))
                    {
                        rt.Info.CommandHistory.Add(line.Trim());
                        if (rt.Info.CommandHistory.Count > 200) rt.Info.CommandHistory.RemoveAt(0);
                    }
                }
            }

            if (rt.Info.UsesConPty && rt.ConPtyWriteStream != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
                await rt.ConPtyWriteStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await rt.ConPtyWriteStream.FlushAsync().ConfigureAwait(false);
            }
            else if (rt.ProcessStdin != null)
            {
                await rt.ProcessStdin.WriteLineAsync(line).ConfigureAwait(false);
                await rt.ProcessStdin.FlushAsync().ConfigureAwait(false);
            }
        }

        public void Interrupt(string sessionId)
        {
            var rt = FindRuntime(sessionId);
            if (rt == null) return;

            if (rt.Info.UsesConPty && rt.ConPtyWriteStream != null)
            {
                try
                {
                    // ETX (Ctrl+C) sent to ConPTY input channel
                    rt.ConPtyWriteStream.WriteByte(0x03);
                    rt.ConPtyWriteStream.Flush();
                    AppendOutput(rt, $"{Environment.NewLine}[Sent Ctrl+C to ConPTY]{Environment.NewLine}");
                    return;
                }
                catch { }
            }

            rt.Info.IsRunning = false;
            rt.Info.ExitCode = -1;
            rt.Dispose();
            AppendOutput(rt, $"{Environment.NewLine}[Session terminated by user]{Environment.NewLine}");
            SessionStateChanged?.Invoke(rt.Info);
        }

        public void CloseSession(string sessionId)
        {
            ActiveRuntime? rt;
            lock (_sessionsGate)
            {
                rt = _runtimes.FirstOrDefault(r => string.Equals(r.Info.Id, sessionId, StringComparison.Ordinal));
                if (rt != null) _runtimes.Remove(rt);
            }
            if (rt != null)
            {
                rt.Info.IsRunning = false;
                rt.Dispose();
                SessionStateChanged?.Invoke(rt.Info);
            }
        }

        public string GetTranscript(string sessionId)
        {
            var rt = FindRuntime(sessionId);
            if (rt == null) return string.Empty;
            lock (rt.Gate) return rt.Transcript.ToString();
        }

        public void ClearOutput(string sessionId)
        {
            var rt = FindRuntime(sessionId);
            if (rt == null) return;
            lock (rt.Gate) rt.Transcript.Clear();
            OutputReceived?.Invoke(sessionId, string.Empty);
        }

        public void Resize(string sessionId, short cols, short rows)
        {
            var rt = FindRuntime(sessionId);
            if (rt == null || !rt.Info.UsesConPty || rt.PseudoConsoleHandle == IntPtr.Zero) return;
            try
            {
                ConPtyInterop.ResizePseudoConsole(rt.PseudoConsoleHandle, new ConPtyInterop.COORD(Math.Max((short)20, cols), Math.Max((short)5, rows)));
            }
            catch { }
        }

        public string? DetectGitBashPath()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        public void Dispose()
        {
            List<ActiveRuntime> snapshot;
            lock (_sessionsGate)
            {
                snapshot = _runtimes.ToList();
                _runtimes.Clear();
            }
            foreach (var rt in snapshot)
            {
                rt.Dispose();
            }
        }

        private ActiveRuntime? FindRuntime(string id)
        {
            lock (_sessionsGate)
            {
                return _runtimes.FirstOrDefault(r => string.Equals(r.Info.Id, id, StringComparison.Ordinal));
            }
        }

        private string ResolveShellExecutable(TerminalShellKind shell, out string args)
        {
            switch (shell)
            {
                case TerminalShellKind.Cmd:
                    args = "/Q /K";
                    return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                case TerminalShellKind.GitBash:
                    string? gitBash = DetectGitBashPath();
                    if (!string.IsNullOrEmpty(gitBash))
                    {
                        args = "--login -i";
                        return gitBash;
                    }
                    args = "-NoLogo -NoExit";
                    return "powershell.exe";
                default:
                    args = "-NoLogo -NoExit";
                    return "powershell.exe";
            }
        }

        private bool TryStartConPty(ActiveRuntime rt, string exe, string args, string cwd)
        {
            SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
            IntPtr hPC = IntPtr.Zero;
            IntPtr attrList = IntPtr.Zero;

            try
            {
                if (!ConPtyInterop.CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) return false;
                if (!ConPtyInterop.CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) return false;

                int hr = ConPtyInterop.CreatePseudoConsole(new ConPtyInterop.COORD(120, 32), inRead, outWrite, 0, out hPC);
                if (hr != 0 || hPC == IntPtr.Zero) return false;

                // Parent drops handles transferred to child pseudo-console
                inRead.Dispose(); inRead = null;
                outWrite.Dispose(); outWrite = null;

                IntPtr lpSize = IntPtr.Zero;
                ConPtyInterop.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                attrList = Marshal.AllocHGlobal(lpSize);
                if (!ConPtyInterop.InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize)) return false;

                if (!ConPtyInterop.UpdateProcThreadAttribute(
                        attrList, 0, (IntPtr)ConPtyInterop.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    return false;
                }

                var siEx = new ConPtyInterop.STARTUPINFOEX();
                siEx.StartupInfo.cb = Marshal.SizeOf<ConPtyInterop.STARTUPINFOEX>();
                siEx.lpAttributeList = attrList;

                string cmdLine = string.IsNullOrWhiteSpace(args) ? $"\"{exe}\"" : $"\"{exe}\" {args}";
                string? envBlock = BuildEnvironmentBlock(rt.Info.EnvironmentVariables);

                bool created = ConPtyInterop.CreateProcessW(
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ConPtyInterop.EXTENDED_STARTUPINFO_PRESENT | ConPtyInterop.CREATE_UNICODE_ENVIRONMENT,
                    envBlock,
                    cwd,
                    ref siEx,
                    out var procInfo);

                ConPtyInterop.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;

                if (!created)
                {
                    return false;
                }

                rt.Info.UsesConPty = true;
                rt.PseudoConsoleHandle = hPC;
                rt.NativeProcessHandle = procInfo.hProcess;
                rt.NativeThreadHandle = procInfo.hThread;
                rt.NativeProcessId = procInfo.dwProcessId;
                rt.ConPtyInputWrite = inWrite; inWrite = null;
                rt.ConPtyOutputRead = outRead; outRead = null;
                rt.ConPtyWriteStream = new FileStream(rt.ConPtyInputWrite, FileAccess.Write, 4096, isAsync: false);

                _ = Task.Run(() => PumpConPtyOutput(rt));
                _ = Task.Run(() => WaitForNativeExit(rt));
                return true;
            }
            catch
            {
                if (attrList != IntPtr.Zero)
                {
                    try { ConPtyInterop.DeleteProcThreadAttributeList(attrList); } catch { }
                    Marshal.FreeHGlobal(attrList);
                }
                if (hPC != IntPtr.Zero)
                {
                    try { ConPtyInterop.ClosePseudoConsole(hPC); } catch { }
                }
                inRead?.Dispose(); inWrite?.Dispose(); outRead?.Dispose(); outWrite?.Dispose();
                return false;
            }
        }

        private void PumpConPtyOutput(ActiveRuntime rt)
        {
            if (rt.ConPtyOutputRead == null) return;
            try
            {
                using var fs = new FileStream(rt.ConPtyOutputRead, FileAccess.Read, 4096, isAsync: false);
                byte[] buffer = new byte[4096];
                var decoder = Encoding.UTF8.GetDecoder();
                char[] chars = new char[4096];

                while (!rt.Cts.IsCancellationRequested)
                {
                    int read = fs.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    int charCount = decoder.GetChars(buffer, 0, read, chars, 0, false);
                    if (charCount > 0)
                    {
                        string carry = rt.EscapeCarry;
                        string text = SanitizeTerminalChunk(new string(chars, 0, charCount), ref carry);
                        rt.EscapeCarry = carry;
                        if (!string.IsNullOrEmpty(text))
                        {
                            AppendOutput(rt, text);
                        }
                    }
                }
            }
            catch { }
        }

        private void WaitForNativeExit(ActiveRuntime rt)
        {
            try
            {
                if (rt.NativeProcessHandle != IntPtr.Zero)
                {
                    ConPtyInterop.WaitForSingleObject(rt.NativeProcessHandle, 0xFFFFFFFF);
                    ConPtyInterop.GetExitCodeProcess(rt.NativeProcessHandle, out uint exitCode);
                    rt.Info.ExitCode = (int)exitCode;
                }
            }
            catch { }
            finally
            {
                rt.Info.IsRunning = false;
                SessionStateChanged?.Invoke(rt.Info);
            }
        }

        private void StartStandardProcess(ActiveRuntime rt, string exe, string args, string cwd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var kv in rt.Info.EnvironmentVariables)
            {
                psi.Environment[kv.Key] = kv.Value;
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) AppendOutput(rt, AnsiRegex.Replace(e.Data, string.Empty) + Environment.NewLine);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) AppendOutput(rt, "[stderr] " + AnsiRegex.Replace(e.Data, string.Empty) + Environment.NewLine);
            };
            proc.Exited += (_, _) =>
            {
                try { rt.Info.ExitCode = proc.ExitCode; } catch { }
                rt.Info.IsRunning = false;
                SessionStateChanged?.Invoke(rt.Info);
            };

            rt.Process = proc;
            proc.Start();
            rt.ProcessStdin = proc.StandardInput;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }

        private void AppendOutput(ActiveRuntime rt, string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            lock (rt.Gate)
            {
                rt.Transcript.Append(chunk);
                if (rt.Transcript.Length > 250_000)
                {
                    rt.Transcript.Remove(0, rt.Transcript.Length - 200_000);
                }
            }
            OutputReceived?.Invoke(rt.Info.Id, chunk);
        }

        private static string? BuildEnvironmentBlock(Dictionary<string, string> overrides)
        {
            if (overrides == null || overrides.Count == 0) return null;
            var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string k && entry.Value is string v) merged[k] = v;
            }
            foreach (var kv in overrides)
            {
                merged[kv.Key] = kv.Value;
            }
            var sb = new StringBuilder();
            foreach (var kv in merged)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            }
            sb.Append('\0');
            return sb.ToString();
        }

        private static class ConPtyInterop
        {
            public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
            public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

            [StructLayout(LayoutKind.Sequential)]
            public struct COORD
            {
                public short X;
                public short Y;
                public COORD(short x, short y) { X = x; Y = y; }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct STARTUPINFO
            {
                public int cb;
                public string lpReserved;
                public string lpDesktop;
                public string lpTitle;
                public int dwX;
                public int dwY;
                public int dwXSize;
                public int dwYSize;
                public int dwXCountChars;
                public int dwYCountChars;
                public int dwFillAttribute;
                public int dwFlags;
                public short wShowWindow;
                public short cbReserved2;
                public IntPtr lpReserved2;
                public IntPtr hStdInput;
                public IntPtr hStdOutput;
                public IntPtr hStdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct STARTUPINFOEX
            {
                public STARTUPINFO StartupInfo;
                public IntPtr lpAttributeList;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_INFORMATION
            {
                public IntPtr hProcess;
                public IntPtr hThread;
                public int dwProcessId;
                public int dwThreadId;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool ResizePseudoConsole(IntPtr hPC, COORD size);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern int ClosePseudoConsole(IntPtr hPC);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool CreateProcessW(
                string? lpApplicationName,
                string lpCommandLine,
                IntPtr lpProcessAttributes,
                IntPtr lpThreadAttributes,
                bool bInheritHandles,
                uint dwCreationFlags,
                string? lpEnvironment,
                string? lpCurrentDirectory,
                [In] ref STARTUPINFOEX lpStartupInfo,
                out PROCESS_INFORMATION lpProcessInformation);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }
}
