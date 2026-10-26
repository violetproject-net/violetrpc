using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Violetproject;

// Minimal Discord Rich Presence client. Talks to the local Discord client over
// its IPC named pipe (no libraries): handshake once, push SET_ACTIVITY frames
// whenever the presence changes, and handle invites: "Ask to Join" requests
// are accepted (ACTIVITY_JOIN_REQUEST) and accepted invites open the game
// (ACTIVITY_JOIN). If Discord is not running it quietly retries in the
// background and the app works normally.
public sealed class DiscordRpc : IDisposable
{
    // The Discord application used for Violetproject (same app as the site's
    // Discord login). The large image is one art asset per game: the asset key
    // is the game's route slug (blackrain, lovedrains, ...); everywhere else
    // (main page and non-game pages) "violetproject" is used.
    private const string AppId = "1560402841066143835";

    private readonly string? pipeOverride;
    private readonly string? debugLog;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly object gate = new();

    private string? pending;
    private readonly Queue<string> commands = new();
    private long startUnixMs;
    private Thread? worker;
    private volatile bool pipeBroken;
    private volatile NamedPipeClientStream? activePipe;

    /// Called from a background thread when an invite to a game was accepted
    /// (the local user used "Ask to Join" or a join invite in chat). The
    /// argument is the join secret the inviting player advertised (vp:&lt;game&gt;).
    public Action<string>? OnJoin { get; set; }

    /// Called from a background thread when somebody used "Ask to Join" on the
    /// activity. The argument is the requesting user's id; accept it with
    /// SendJoinInvite.
    public Action<string>? OnJoinRequest { get; set; }

    public DiscordRpc(string? pipeOverride, string? debugLog = null)
    {
        this.pipeOverride = pipeOverride;
        this.debugLog = debugLog;
    }

    private void Log(string message)
    {
        if (debugLog == null) return;
        try
        {
            File.AppendAllText(debugLog, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    public void Start()
    {
        if (worker != null) return;
        startUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        worker = new Thread(Loop) { IsBackground = true, Name = "discord-rpc" };
        worker.Start();
    }

    public void SetPresence(string details, string siteUrl, string? partyId = null, string? joinSecret = null, string? largeImage = null, string? largeText = null)
    {
        var activity = new Dictionary<string, object?>
        {
            ["details"] = Trim(details, 128),
            ["timestamps"] = new Dictionary<string, object> { ["start"] = startUnixMs },
            ["assets"] = new Dictionary<string, object>
            {
                ["large_image"] = ArtKey(largeImage),
                ["large_text"] = string.IsNullOrWhiteSpace(largeText) ? "Violetproject" : Trim(largeText.Trim(), 128)
            }
        };
        if (!string.IsNullOrEmpty(partyId) || !string.IsNullOrEmpty(joinSecret))
        {
            var party = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(partyId)) party["id"] = partyId;
            if (party.Count > 0) activity["party"] = party;
            if (!string.IsNullOrEmpty(joinSecret))
            {
                activity["secrets"] = new Dictionary<string, object> { ["join"] = joinSecret };
            }
        }
        // Discord rejects activities that carry both "secrets" (join) and
        // "buttons" (error 5005), so while in a game the join secret wins and
        // the button is dropped.
        if (string.IsNullOrEmpty(joinSecret) && siteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                activity["buttons"] = new object[]
                {
                    new Dictionary<string, object> { ["label"] = "Open " + new Uri(siteUrl).Host, ["url"] = siteUrl }
                };
            }
            catch
            {
            }
        }
        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString("N"),
            args = new { pid = Environment.ProcessId, activity }
        });
        lock (gate) { pending = payload; }
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }

    /// Consent to an "Ask to Join" request (docs: SEND_ACTIVITY_JOIN_INVITE).
    /// Discord then sends the requesting user the invite.
    public void SendJoinInvite(string userId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SEND_ACTIVITY_JOIN_INVITE",
            nonce = Guid.NewGuid().ToString("N"),
            args = new { user_id = userId }
        });
        lock (gate) { commands.Enqueue(payload); }
        try { signal.Release(); } catch (SemaphoreFullException) { }
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);

    // Art asset keys must be lowercase [a-z0-9_]; the game slugs already are.
    // Anything unexpected falls back to "violetproject" rather than making
    // Discord reject the whole activity.
    private static string ArtKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "violetproject";
        var s = value.Trim().ToLowerInvariant();
        var chars = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            chars[i] = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' ? c : '_';
        }
        var key = new string(chars).Trim('_');
        return key.Length == 0 ? "violetproject" : key;
    }

    private void Loop()
    {
        Log("loop start (override=" + (pipeOverride ?? "none") + ")");
        while (!cts.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipeBroken = false;
                Log("connecting...");
                pipe = Connect();
                activePipe = pipe;
                Log("connected, sending handshake");
                Handshake(pipe);
                Log("handshake sent");
                _ = ReaderLoopAsync(pipe); // consumes READY + ACTIVITY_JOIN events
                SendLatest(pipe);
                while (!cts.IsCancellationRequested && !pipeBroken && pipe.IsConnected)
                {
                    if (signal.Wait(15000))
                    {
                        while (signal.Wait(0)) { } // drain extras
                        SendLatest(pipe);
                    }
                    else
                    {
                        SendLatest(pipe); // keepalive
                    }
                }
                Log("inner loop exited (cancelled=" + cts.IsCancellationRequested + ", broken=" + pipeBroken + ")");
            }
            catch (Exception e)
            {
                Log("loop error: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                activePipe = null;
                try { pipe?.Dispose(); } catch { } // unblocks the reader thread too
            }
            if (!cts.IsCancellationRequested)
            {
                Log("reconnect in 15s");
                try { cts.Token.WaitHandle.WaitOne(15000); } catch { }
            }
        }
        Log("loop end");
    }

    private NamedPipeClientStream Connect()
    {
        var candidates = pipeOverride != null
            ? new[] { pipeOverride }
            : Enumerable.Range(0, 10).Select(i => "discord-ipc-" + i).ToArray();
        Exception? last = null;
        foreach (var name in candidates)
        {
            try
            {
                // Asynchronous so the reader can use ReadAsync: a synchronous
                // blocking Read on the same handle stalls writes (verified).
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(300);
                return pipe;
            }
            catch (Exception e)
            {
                last = e;
            }
        }
        throw last ?? new IOException("no discord ipc pipe");
    }

    private static void Handshake(Stream pipe)
    {
        // The READY reply (and later events) are consumed by ReaderLoopAsync.
        WriteFrame(pipe, 0, JsonSerializer.Serialize(new { v = 1, client_id = AppId }));
    }

    private async Task ReaderLoopAsync(Stream pipe)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var json = await ReadFrameAsync(pipe);
                if (json == null) break;
                HandleIncoming(json);
            }
        }
        catch
        {
        }
        finally
        {
            pipeBroken = true;
            try { signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private void HandleIncoming(string json)
    {
        try
        {
            Log("incoming " + (json.Length > 400 ? json.Substring(0, 400) + "..." : json));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            // Join events arrive as DISPATCH frames carrying an "evt" property.
            string? name = null;
            if (root.TryGetProperty("evt", out var evt) && evt.ValueKind == JsonValueKind.String) name = evt.GetString();
            else if (root.TryGetProperty("cmd", out var cmd) && cmd.ValueKind == JsonValueKind.String) name = cmd.GetString();
            if (name == "ACTIVITY_JOIN")
            {
                // The local user accepted an invite: open the game it points at.
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.String)
                {
                    var value = secret.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        Log("join accepted: " + value);
                        OnJoin?.Invoke(value);
                    }
                }
            }
            else if (name == "ACTIVITY_JOIN_REQUEST")
            {
                // Somebody used "Ask to Join" on the activity.
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object &&
                    user.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var userId = id.GetString();
                    if (!string.IsNullOrEmpty(userId))
                    {
                        Log("join requested by " + userId);
                        OnJoinRequest?.Invoke(userId);
                    }
                }
            }
            else if (name == "ACTIVITY_INVITE")
            {
                Log("activity invite received");
            }
        }
        catch
        {
        }
    }

    private void SendLatest(Stream pipe)
    {
        while (true)
        {
            string? command;
            lock (gate) { command = commands.Count > 0 ? commands.Dequeue() : null; }
            if (command == null) break;
            Log("sending command (" + command.Length + " bytes)");
            WriteFrame(pipe, 1, command);
            Log("command sent");
        }
        string? payload;
        lock (gate) { payload = pending; }
        if (payload != null)
        {
            Log("sending activity (" + payload.Length + " bytes)");
            WriteFrame(pipe, 1, payload);
            Log("activity sent");
        }
    }

    private static void WriteFrame(Stream pipe, int op, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    private static async Task<string?> ReadFrameAsync(Stream pipe)
    {
        var header = new byte[8];
        if (!await ReadExactlyAsync(pipe, header)) return null;
        var len = BitConverter.ToInt32(header, 4);
        if (len <= 0 || len > 1 << 20) return null;
        var payload = new byte[len];
        if (!await ReadExactlyAsync(pipe, payload)) return null;
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (n <= 0) return false;
            offset += n;
        }
        return true;
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { }
        try { signal.Release(); } catch { }
        try { activePipe?.Dispose(); } catch { } // unblocks reader task + writer thread
        try { worker?.Join(1000); } catch { }
        cts.Dispose();
        signal.Dispose();
    }
}
