using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace D2XPad.SeriesProbe;

internal sealed record BrowserPad(int Index, string Id, string Mapping, double[] Axes, double[] Buttons);
internal sealed record BrowserSnapshot(double Time, BrowserPad[] Pads, bool Visible, bool Focused, string UserAgent);

// The browser receives a sample number only. Expected axis/button values stay in the runner.
internal sealed class BrowserProbe : IAsyncDisposable
{
    private readonly byte[] page;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finishSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly string prefix = "/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "/";
    private readonly Task acceptLoop;
    private TaskCompletionSource<BrowserSnapshot[]>? pending;
    private long generation;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Uri Url { get; }
    public string? UserAgent { get; private set; }
    public string Prompt { get => Volatile.Read(ref prompt); set => Volatile.Write(ref prompt, value); }
    public BrowserPad? SelectedPad { get => Volatile.Read(ref selectedPad); set => Volatile.Write(ref selectedPad, value); }
    private string prompt = "";
    private string? finalMessage;
    private BrowserPad? selectedPad;
    private string pageState = "Waiting for the browser page to request a sample.";

    public BrowserProbe()
    {
        using var resource = typeof(BrowserProbe).Assembly.GetManifestResourceStream("D2XPad.SeriesProbe.gamepad.html")
            ?? throw new FileNotFoundException("The embedded Gamepad API page (gamepad.html) is missing.");
        using var content = new MemoryStream();
        resource.CopyTo(content);
        page = content.ToArray();
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Url = new Uri($"http://127.0.0.1:{port}{prefix}");
        acceptLoop = ServeAsync(shutdown.Token);
    }

    public Task WaitForPageAsync(CancellationToken token) => ready.Task.WaitAsync(token);

    public async Task FinishAsync(string message, CancellationToken token)
    {
        Volatile.Write(ref finalMessage, message);
        // A closed tab must not keep the game and virtual controller alive.
        try { await finishSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        catch (TimeoutException) { }
    }

    public async Task<BrowserSnapshot[]> SampleAsync(CancellationToken token)
    {
        TaskCompletionSource<BrowserSnapshot[]> request;
        lock (gate)
        {
            if (pending != null) throw new InvalidOperationException("A browser sample request is already pending.");
            generation++;
            request = pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { return await request.Task.WaitAsync(TimeSpan.FromSeconds(10), token); }
        catch (TimeoutException ex) { throw new TimeoutException($"The browser did not return any samples within 10 seconds. {pageState}", ex); }
        finally { lock (gate) pending = null; }
    }

    private static bool Same(BrowserPad a, BrowserPad b) => a.Index == b.Index && a.Id == b.Id;
    private static BrowserPad? Find(BrowserSnapshot f, BrowserPad pad) => f.Pads.FirstOrDefault(p => Same(p, pad));
    private static bool Stable(BrowserSnapshot[] frames, Func<BrowserSnapshot, bool> test) =>
        frames.Length >= 3 && frames.TakeLast(3).All(f => f.Visible && test(f));

    // Some browsers reveal the controller only after the very first button press.
    public static BrowserPad? Identify(BrowserSnapshot[] released, BrowserSnapshot[] pressed, BrowserSnapshot[] releasedAgain)
    {
        static bool Released(BrowserPad p) => p.Buttons.Length >= 1 && p.Buttons[0] < .05;
        var candidates = (pressed.LastOrDefault()?.Pads ?? []).Where(p => p.Mapping == "standard"
            && Stable(released, f => Find(f, p) is not { } before || Released(before))
            && Stable(pressed, f => Find(f, p) is { Buttons.Length: > 0 } down && down.Buttons[0] > .95)
            && Stable(releasedAgain, f => Find(f, p) is { } after && Released(after))).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    // lx, ly, rx, ry use browser conventions. LT/RT are separate analog button values.
    public static (bool Passed, BrowserPad? Actual) Match(BrowserSnapshot[] frames, BrowserPad identity,
        double leftTrigger, double rightTrigger, double lx, double ly, double rx, double ry, bool a = false)
    {
        static bool Close(double v, double expected) => double.IsFinite(v) && Math.Abs(v - expected) <= .02;
        bool passed = Stable(frames, f => Find(f, identity) is { Mapping: "standard", Axes.Length: >= 4, Buttons.Length: >= 8 } p
            && Close(p.Buttons[0], a ? 1 : 0) && Close(p.Buttons[6], leftTrigger) && Close(p.Buttons[7], rightTrigger)
            && Close(p.Axes[0], lx) && Close(p.Axes[1], ly) && Close(p.Axes[2], rx) && Close(p.Axes[3], ry));
        return (passed, frames.Length == 0 ? null : Find(frames[^1], identity));
    }

    private async Task ServeAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = HandleAsync(client, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        using var connection = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        CancellationToken token = timeout.Token;
        try
        {
            using var stream = client.GetStream();
            var (method, path, body) = await ReadRequestAsync(stream, token);
            string status = "200 OK";
            string mime = "text/plain; charset=utf-8";
            byte[] response = [];
            if (method == "GET" && path == prefix)
            {
                response = page;
                mime = "text/html; charset=utf-8";
            }
            else if (method == "GET" && path.StartsWith(prefix + "next?", StringComparison.Ordinal))
            {
                pageState = "Last reported tab state: " + path[(path.IndexOf('?') + 1)..];
                ready.TrySetResult();
                long id;
                lock (gate) id = pending is null ? 0 : generation;
                if (path.StartsWith(prefix + "next?live=1&", StringComparison.Ordinal))
                {
                    var selected = SelectedPad;
                    var final = Volatile.Read(ref finalMessage);
                    response = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        generation = id, prompt = final ?? Prompt, finished = final != null,
                        selectedIndex = selected?.Index, selectedId = selected?.Id
                    });
                }
                else response = JsonSerializer.SerializeToUtf8Bytes(id);
                mime = "application/json";
            }
            else if (method == "POST" && path == prefix + "finished" && Volatile.Read(ref finalMessage) != null)
                finishSeen.TrySetResult();
            else if (method == "POST" && path == prefix + "samples")
            {
                var message = JsonSerializer.Deserialize<SampleReply>(body, Json);
                lock (gate)
                {
                    if (message?.Generation == generation && pending != null)
                    {
                        if (!string.IsNullOrEmpty(message.Error))
                            pending.TrySetException(new InvalidOperationException(message.Error));
                        else if (message.Frames is { Length: >= 3 and <= 12 } frames
                            && frames.All(f => f is { Pads.Length: <= 32 } && f.Pads.All(p =>
                                p is { Id: not null, Axes.Length: <= 128, Buttons.Length: <= 256 })))
                        {
                            UserAgent = frames[0].UserAgent;
                            pending.TrySetResult(frames);
                        }
                        else pending.TrySetException(new InvalidDataException("The browser returned an incomplete set of samples."));
                    }
                }
            }
            else status = "404 Not Found";
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {mime}\r\nContent-Length: {response.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token);
            await stream.WriteAsync(response, token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or JsonException or InvalidDataException) { }
    }

    private sealed record SampleReply(long Generation, BrowserSnapshot[]? Frames, string? Error);

    // Only three fixed routes, bound to loopback. Parse a length-delimited fetch request
    // so the probe requires neither a reserved HTTP.SYS prefix nor an ASP.NET runtime.
    private static async Task<(string Method, string Path, byte[] Body)> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        int used = 0, split;
        while ((split = buffer.AsSpan(0, used).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (used == buffer.Length) throw new InvalidDataException("Header too large.");
            int n = await stream.ReadAsync(buffer.AsMemory(used), token);
            if (n == 0) throw new EndOfStreamException();
            used += n;
        }
        string[] headers = Encoding.ASCII.GetString(buffer, 0, split).Split("\r\n");
        string[] request = headers[0].Split(' ');
        if (request.Length != 3) throw new InvalidDataException("Invalid HTTP request.");
        int length = 0;
        foreach (string header in headers.Skip(1))
        {
            const string name = "Content-Length:";
            if (header.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(header.AsSpan(name.Length), out length) || length is < 0 or > 131072))
                throw new InvalidDataException("Invalid content length.");
        }
        byte[] body = new byte[length];
        int copied = Math.Min(used - split - 4, length);
        buffer.AsMemory(split + 4, copied).CopyTo(body);
        if (copied < length) await stream.ReadExactlyAsync(body.AsMemory(copied), token);
        return (request[0], request[1], body);
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        listener.Stop();
        await acceptLoop;
        shutdown.Dispose();
    }
}
