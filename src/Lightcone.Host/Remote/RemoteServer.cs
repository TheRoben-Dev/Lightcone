using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lightcone.Host.Remote;

/// <summary>What the phone remote shows: what's playing and where.</summary>
public sealed record RemoteState(string Screen, string? Title, string? Subtitle, double Position, double Duration, bool Playing, bool Paused);

/// <summary>
/// The phone remote: a tiny web server on this network (off by default) serving one page with a
/// D-pad and playback buttons. Every request carries the PIN shown in Settings; wrong PINs are
/// slowed down. Commands queue up for the app to take on its main thread (<see cref="TryTake"/>).
/// A plain TcpListener, so it needs no admin rights or URL reservations on any platform.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    public const int DefaultPort = 8642;

    public static readonly string[] Commands =
    [
        "up", "down", "left", "right", "accept", "back", "menu", "options", "playpause", "skipback", "skipforward",
        "prev", "next", "volup", "voldown", "mute", "tabprev", "tabnext", "home",
    ];

    private readonly string _pin;
    private readonly Func<RemoteState> _state;
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private int _failures;
    private DateTime _lockedUntil;

    public RemoteServer(string pin, Func<RemoteState> state)
    {
        _pin = pin;
        _state = state;
    }

    public int Port { get; private set; }

    /// <summary>Starts listening on <paramref name="port"/> (or the next free one); returns false if none could be opened.</summary>
    public bool Start(int port = DefaultPort)
    {
        for (var p = port; p < port + 10; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, p);
                l.Start();
                _listener = l;
                Port = p;
                _ = Task.Run(AcceptLoop);
                AppLog.Info($"phone remote: listening on port {p}");
                return true;
            }
            catch (SocketException e)
            {
                AppLog.Info($"phone remote: port {p}: {e.Message}");
            }
        }
        return false;
    }

    public bool TryTake(out string command) => _commands.TryDequeue(out command!);

    /// <summary>Addresses on this network a phone can open (LAN IPv4 only).</summary>
    public IReadOnlyList<string> Urls() => LanAddresses().Select(a => $"http://{a}:{Port}/").ToList();

    public static IReadOnlyList<string> LanAddresses()
    {
        var list = new List<(string Ip, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }
                    var b = a.Address.GetAddressBytes();
                    var rank = b[0] == 192 && b[1] == 168 ? 0 : b[0] == 10 ? 1 : b[0] == 172 && b[1] is >= 16 and < 32 ? 2 : -1;
                    if (rank >= 0)
                    {
                        list.Add((a.Address.ToString(), rank));
                    }
                }
            }
        }
        catch (NetworkInformationException e)
        {
            AppLog.Info($"phone remote: no network list: {e.Message}");
        }
        return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 10000).ToString("0000");

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested && _listener is { } l)
        {
            TcpClient client;
            try
            {
                client = await l.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var head = await ReadHead(stream, cts.Token);
            if (head == null)
            {
                return;
            }
            var (status, type, body) = Handle(head.Value.Method, head.Value.Target, head.Value.Headers);
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cts.Token);
            await stream.WriteAsync(bytes, cts.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // phone went away
        }
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadHead(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var used = 0;
        while (used < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(used), ct);
            if (n == 0)
            {
                return null;
            }
            used += n;
            var text = Encoding.ASCII.GetString(buf, 0, used);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return ParseHead(text[..end]);
            }
        }
        return null;
    }

    internal static (string Method, string Target, Dictionary<string, string> Headers)? ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2)
        {
            return null;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0)
            {
                headers[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }
        return (first[0], first[1], headers);
    }

    internal static Dictionary<string, string> Query(string target)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = target.IndexOf('?');
        if (i < 0)
        {
            return q;
        }
        foreach (var part in target[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            q[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return q;
    }

    /// <summary>Answers one request: (status line, content type, body).</summary>
    internal (string, string, string) Handle(string method, string target, IReadOnlyDictionary<string, string> headers)
    {
        var path = target.Split('?')[0];
        if (path is "/" or "/index.html")
        {
            return ("200 OK", "text/html; charset=utf-8", RemotePage.Html);
        }
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return ("404 Not Found", "text/plain", "not found");
        }
        var q = Query(target);
        if (DateTime.UtcNow < _lockedUntil)
        {
            return ("429 Too Many Requests", "application/json", "{\"error\":\"wait\"}");
        }
        var pin = headers.TryGetValue("X-Lightcone-Pin", out var h) ? h : q.GetValueOrDefault("pin");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin ?? ""), Encoding.UTF8.GetBytes(_pin)))
        {
            if (Interlocked.Increment(ref _failures) % 5 == 0)
            {
                _lockedUntil = DateTime.UtcNow.AddSeconds(30);
                AppLog.Info("phone remote: several wrong PINs, waiting 30 s");
            }
            return ("401 Unauthorized", "application/json", "{\"error\":\"pin\"}");
        }
        _failures = 0;
        switch (path)
        {
            case "/api/state":
                return ("200 OK", "application/json", JsonSerializer.Serialize(_state(), JsonOptions));
            case "/api/cmd" when method == "POST" && q.GetValueOrDefault("c") is { } c && Commands.Contains(c):
                if (_commands.Count < 50)
                {
                    _commands.Enqueue(c);
                }
                return ("200 OK", "application/json", "{\"ok\":true}");
            default:
                return ("400 Bad Request", "application/json", "{\"error\":\"command\"}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }
        _listener = null;
        AppLog.Info("phone remote: stopped");
    }
}
