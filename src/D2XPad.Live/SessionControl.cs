using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace D2XPad.Live;

internal sealed record ProfileSelection(string Selector, ControllerProfile Profile)
{
    public static string Normalize(string value) => Path.IsPathFullyQualified(value)
        || value.IndexOfAny(['/', '\\']) >= 0 || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? Path.GetFullPath(value) : value;

    public static ProfileSelection Load(ProfileCatalog catalog, string value)
    {
        string selector = Normalize(value);
        var profile = catalog.Resolve(selector);
        return new(Path.IsPathFullyQualified(selector) ? selector : profile.Name, profile);
    }
}

internal sealed record SessionStatus(string SessionId, int Slot, string Profile, bool Connected,
    bool WaitingForNeutral, int Disconnects, int Reconnects, LiveState? LastDisconnectedState,
    LiveState State, long Received, long Generation, ExtraButtons Extras, bool Suspended, int Suspends, int Resumes,
    LiveState? LastSuspendedState, DateTimeOffset? LastSuspendUtc, DateTimeOffset? LastResumeUtc,
    long ReceivedAtResume, SuspendDetachEvidence? LastSuspendDetach, LiveState? Physical = null, string? DevicePath = null,
    int ProcessId = 0, long ProfileRevision = 0, string? LayoutId = null,
    bool VibrationEnabled = false, string? VibrationProblem = null, long VibrationEffects = 0);

// This pipe belongs to the standard-user converter. The elevated driver host accepts only gamepad states.
internal sealed class SessionControl : IAsyncDisposable
{
    private sealed record Request(string Command, string? Value, string? ManagedPath = null, string? Revision = null,
        string? ExpectedSession = null);
    private sealed record Reply(SessionStatus? Status, string? Error);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource stop = new();
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly PhysicalFeed feed;
    private readonly ProfileCatalog catalog;
    private readonly int slot;
    private ProfileSelection active;
    private long profileRevision;
    public Task Completion { get; }

    private static string PipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"D2XPad.Control.{identity.User!.Value}.{process.SessionId}";
    }

    public SessionControl(PhysicalFeed feed, int slot, ProfileCatalog catalog, ProfileSelection profile)
    {
        this.feed = feed;
        this.slot = slot;
        this.catalog = catalog;
        active = profile;
        Completion = ServeAsync(stop.Token);
    }

    private SessionStatus Snapshot()
    {
        var state = feed.Connection;
        LiveState? physical = state.Latest is { } latest && feed.Layout.TryDecodePhysical(latest.Raw, out var native, out _)
            ? native : null;
        return new(id, slot, active.Selector, state.Connected, state.WaitingForNeutral,
            state.Disconnects, state.Reconnects, state.LastDisconnectedState, state.State, state.Received,
            state.Generation, state.Latest?.Extras ?? ExtraButtons.None, state.Suspended, state.Suspends, state.Resumes,
            state.LastSuspendedState, state.LastSuspendUtc, state.LastResumeUtc, state.ReceivedAtResume, state.LastSuspendDetach,
            physical, state.DevicePath, Environment.ProcessId, profileRevision, feed.Layout.Id,
            feed.VibrationEnabled, feed.VibrationProblem, feed.VibrationEffects);
    }

    private void UseProfile(ProfileSelection next)
    {
        feed.ChangeBindings(next.Profile.Bindings);
        active = next;
        profileRevision++;
        Console.WriteLine($"Active profile: {active.Selector}.");
    }

    private void DeleteProfile(string name, string path, string revision)
    {
        var selected = catalog.OpenEditor(name);
        if (!selected.CanReplace || !string.Equals(selected.ManagedPath, path, StringComparison.OrdinalIgnoreCase)
            || selected.Revision != revision)
            throw new IOException("The selected profile changed or belongs to another catalog. Reopen it before deleting.");

        var previous = active;
        bool inUse = string.Equals(previous.Selector, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(previous.Selector, path, StringComparison.OrdinalIgnoreCase);
        try
        {
            // Commands run serially. No other switch can reactivate this file between fallback and removal.
            catalog.Delete(selected, () =>
            {
                if (inUse) UseProfile(ProfileSelection.Load(catalog, feed.Layout.DefaultProfileName));
            });
        }
        catch
        {
            // An I/O failure after the switch leaves the original profile on disk and playable.
            if (active != previous) UseProfile(previous);
            throw;
        }
    }

    private async Task ServeAsync(CancellationToken token)
    {
        using var pipe = new NamedPipeServerStream(PipeName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        while (true)
        {
            await pipe.WaitForConnectionAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                Reply reply;
                try
                {
                    var request = await ReadAsync<Request>(pipe, timeout.Token);
                    switch (request)
                    {
                        case { Command: "status", Value: null }: break;
                        case { Command: "switch", Value: { Length: > 0 } value }:
                            UseProfile(ProfileSelection.Load(catalog, value));
                            break;
                        case { Command: "delete", Value: { Length: > 0 } name, ManagedPath: { } path, Revision: { } revision }:
                            DeleteProfile(name, path, revision);
                            break;
                        case { Command: "vibration", Value: "on" or "off" }:
                            if (request.ExpectedSession != id)
                                throw new InvalidOperationException("The active controller session changed. Try again.");
                            feed.SetVibration(request.Value == "on");
                            break;
                        default: throw new ArgumentException("Unknown session command or missing profile.");
                    }
                    reply = new(Snapshot(), null);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException
                    or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or FormatException)
                {
                    reply = new(null, ex.Message);
                }
                await WriteAsync(pipe, reply, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                // A client can close before reading its response. Reuse the server for the next command.
            }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }

    public static Task<SessionStatus> SendAsync(string command, string? value, CancellationToken token,
        int connectTimeoutMs = 2000) => ExchangeAsync(new(command, value), token, connectTimeoutMs);

    public static Task<SessionStatus> DeleteAsync(ProfileEditSnapshot selected, CancellationToken token) =>
        ExchangeAsync(new("delete", selected.Profile.Name, selected.ManagedPath, selected.Revision), token, 2000);

    public static Task<SessionStatus> SetVibrationAsync(string expectedSession, bool enabled, CancellationToken token) =>
        ExchangeAsync(new("vibration", enabled ? "on" : "off", ExpectedSession: expectedSession), token, 2000);

    private static async Task<SessionStatus> ExchangeAsync(Request request, CancellationToken token, int connectTimeoutMs)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(connectTimeoutMs, token); }
        catch (TimeoutException ex) { throw new IOException("No playable D2XPad session is listening. Start play, or finish check --keep-playing.", ex); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        await WriteAsync(pipe, request, timeout.Token);
        var reply = await ReadAsync<Reply>(pipe, timeout.Token);
        return reply.Status ?? throw new InvalidOperationException(reply.Error ?? "Invalid response from D2XPad.");
    }

    private const int MaxMessage = 65536;
    private static async Task<T> ReadAsync<T>(PipeStream pipe, CancellationToken token) where T : class
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaxMessage) throw new InvalidDataException("Invalid session message length.");
        var data = new byte[size];
        await pipe.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("Empty session message.");
    }

    private static async Task WriteAsync<T>(PipeStream pipe, T message, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (data.Length > MaxMessage) throw new InvalidDataException("Session message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(data, token);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await Completion; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { stop.Dispose(); }
    }
}
