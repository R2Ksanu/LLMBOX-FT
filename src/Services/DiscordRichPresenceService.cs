using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlmodelBox.Services
{
    // Dependency-free Discord Rich Presence client. Talks the Discord local IPC protocol
    // directly over the named pipe \\.\pipe\discord-ipc-{0..9} (opcode + length framing),
    // so no NuGet package is required. If Discord is not running, or no valid client ID is
    // supplied, every operation fails silently and the app is unaffected.
    //
    // Requires a Discord Application (client) ID from https://discord.com/developers/applications.
    public sealed class DiscordRichPresenceService : IDisposable
    {
        private const int OpHandshake = 0;
        private const int OpFrame = 1;
        private const int OpClose = 2;

        private readonly object _sync = new();
        private NamedPipeClientStream? _pipe;
        private string _clientId = "";
        private long _startUnix;
        private string _details = "In LlmodelBox";
        private string _state = "Idle";
        private volatile bool _connected;
        private volatile bool _disposed;

        public bool IsConnected => _connected;

        // Opens the pipe, performs the handshake and pushes the first activity. Safe to call
        // when already running (it restarts). Returns false if it could not connect.
        public bool Start(string clientId, string details, string state)
        {
            if (_disposed) return false;
            if (string.IsNullOrWhiteSpace(clientId)) return false;

            Stop();

            lock (_sync)
            {
                _clientId = clientId.Trim();
                _startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _details = details;
                _state = state;

                if (!TryConnect()) return false;
                if (!SendHandshake()) { Cleanup(); return false; }
                _connected = true;
            }

            SendActivity();
            return _connected;
        }

        // Updates the presence lines. No-op (and never throws) if not connected.
        public void Update(string? details, string? state)
        {
            if (!_connected) return;
            lock (_sync)
            {
                if (details != null) _details = details;
                if (state != null) _state = state;
            }
            SendActivity();
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_pipe != null)
                {
                    try
                    {
                        if (_connected && _pipe.IsConnected)
                            WriteFrame(OpClose, "{}");
                    }
                    catch { }
                    Cleanup();
                }
                _connected = false;
            }
        }

        private bool TryConnect()
        {
            for (int i = 0; i <= 9; i++)
            {
                try
                {
                    var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(500);
                    _pipe = pipe;
                    return true;
                }
                catch { /* try next slot */ }
            }
            return false;
        }

        private bool SendHandshake()
        {
            try
            {
                string payload = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
                WriteFrame(OpHandshake, payload);
                // Drain the READY dispatch so it does not block a later read; ignore contents.
                _ = ReadFrameSafe();
                return true;
            }
            catch { return false; }
        }

        private void SendActivity()
        {
            if (_pipe == null) return;
            try
            {
                string details, state;
                long start;
                lock (_sync) { details = _details; state = _state; start = _startUnix; }

                var frame = new
                {
                    cmd = "SET_ACTIVITY",
                    nonce = Guid.NewGuid().ToString(),
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = new
                        {
                            details = Clip(details, 128),
                            state = Clip(state, 128),
                            timestamps = new { start }
                        }
                    }
                };
                lock (_sync)
                {
                    WriteFrame(OpFrame, JsonSerializer.Serialize(frame));
                }
            }
            catch
            {
                // Pipe broke (Discord closed) — mark disconnected so callers stop pushing.
                _connected = false;
                lock (_sync) { Cleanup(); }
            }
        }

        private void WriteFrame(int opcode, string json)
        {
            if (_pipe == null || !_pipe.IsConnected) throw new InvalidOperationException("pipe closed");
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(body.Length).CopyTo(header, 4);
            _pipe.Write(header, 0, header.Length);
            _pipe.Write(body, 0, body.Length);
            _pipe.Flush();
        }

        private string? ReadFrameSafe()
        {
            try
            {
                if (_pipe == null) return null;
                byte[] header = new byte[8];
                int read = _pipe.Read(header, 0, 8);
                if (read < 8) return null;
                int len = BitConverter.ToInt32(header, 4);
                if (len <= 0 || len > 1_000_000) return null;
                byte[] body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = _pipe.Read(body, got, len - got);
                    if (n <= 0) break;
                    got += n;
                }
                return Encoding.UTF8.GetString(body, 0, got);
            }
            catch { return null; }
        }

        private void Cleanup()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return " ";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
