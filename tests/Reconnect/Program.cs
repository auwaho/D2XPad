using System.Text.Json;
using D2XPad.Capture;
using D2XPad.Live;

var original = new HidCollection(1, "initial-path", 0x2DC8, 0x2067, 1, 5, "Bluetooth", "selected", 32, [1]);
CalibrationTests.Run();
ControllerLibraryTests.Run();
await PhysicalRumbleTests.Run();
HotProfileTests.Run(original);
Check(RawInput.ClassifyTransport([@"HID\VID_1111", @"USB\VID_1111", @"USB\ROOT_HUB30\1", @"PCI\HOST"]) == "USB", "physical USB ancestry");
Check(RawInput.ClassifyTransport([@"HID\VID_1111", @"BTHLEDEVICE\PAD", @"USB\VID_RADIO", @"USB\ROOT_HUB30\1"]) == "Bluetooth", "Bluetooth radio on USB");
Check(RawInput.ClassifyTransport([@"HID\VID_1111", @"USB\VID_1111", @"ROOT\SYSTEM\0001"]) == "Unknown", "generic root-enumerated virtual USB bus");
Check(RawInput.ClassifyTransport([@"HID\VID_1111", @"USB\VID_1111", @"ROOT\VIGEMBUS"]) == "Virtual", "virtual ancestor above apparent USB child");
Check(RawInput.ClassifyTransport([@"HID\VID_1111", @"SWD\HIDMAESTRO\1"]) == "Virtual", "virtual HIDMaestro output");
var returning = original with { Handle = 2, Path = "new-path" };
var other = original with { Handle = 3, Path = "other-path", ContainerId = "other" };
var map = PadBindings.Load(Path.Combine(AppContext.BaseDirectory, "85hf-bindings.json"));
using var alternateJson = JsonDocument.Parse("""{"L4":"RB","R4":"LB","PL":"X","PR":"None"}""");
var alternate = PadBindings.Parse(alternateJson.RootElement);
var sent = new List<LiveState>();
var feed = new PhysicalSession(original, map, sent.Add, () => sent.Add(LiveState.Neutral));
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidDataException(name);
}
byte[] neutral = new byte[32];
neutral[0] = 1;
neutral[1] = 15;
neutral.AsSpan(2, 4).Fill(128);
byte[] held = (byte[])neutral.Clone();
held[1] = 3; held[2] = 0; held[3] = 255;
held[6] = 96; held[7] = 201;
held[8] = 0x20; held[9] = 0x20; held[10] = 3;
map.TryDecode(held, out LiveState originalHeld, out _);
alternate.TryDecode(held, out LiveState alternateHeld, out _);
Check(originalHeld != alternateHeld, "Profiles must differ for this chord.");

Check(feed.SelectInstance([other, returning]) == returning, "Follow only the original physical container.");
Check(feed.SelectInstance([other]) == null, "Reject another identical model.");
Check(feed.SelectInstance([original with { ContainerId = "other" }]) == null, "Reject a reused path with a conflicting container.");
Check(feed.SelectInstance([returning, returning with { Handle = 4, Path = "also-new" }]) == null,
    "An ambiguous collection in the same container must wait.");
Check(feed.SelectInstance([returning with { Transport = "USB" }, returning with { ProductId = 0x0b13 }]) == null,
    "Reject USB and virtual devices.");
var noId = new PhysicalSession(original with { ContainerId = null }, map, _ => {}, () => {});
Check(noId.SelectInstance([returning, other]) == null && noId.SelectInstance([original]) == original, "Use the original path without an ID.");

feed.ChangeBindings(map);
Check(feed.Connection is { Connected: true, WaitingForNeutral: true, Latest: null, Received: 0 } && sent.Count == 0,
    "Changing profiles before the first report cannot create a physical sample.");
feed.Report(original, neutral);
feed.Report(original, held);
Check(sent.SequenceEqual([originalHeld]), "Forward the full selected state before removal.");
byte[] allHeld = (byte[])held.Clone();
allHeld[4] = 255; allHeld[5] = 0; allHeld[8] = 255; allHeld[9] = 0x7c;
feed.Report(original, allHeld);
var allPressed = new LiveState(0, 255, 255, 0, 201, 96, 3, 0xf7f0);
Check(sent[^1] == allPressed, "All eleven Xbox buttons, four axes, two triggers and the diagonal are active.");
feed.SetDevice(null);
Check(sent.SequenceEqual([originalHeld, allPressed, LiveState.Neutral]) && feed.Connection is
    { Connected: false, Disconnects: 1, WaitingForNeutral: true, Latest: null } && feed.Connection.LastDisconnectedState == allPressed,
    "Detach must release all held axes, triggers, buttons and D-pad and preserve prior evidence.");
long count = feed.Received;
feed.Report(original, held);
feed.Report(other, held);
feed.SetDevice(null);
Check(feed.Received == count && sent.Count == 3 && feed.Connection.Disconnects == 1, "Ignore late packets and duplicate removal.");

feed.ChangeBindings(alternate);
feed.SetDevice(returning);
Check(feed.Connection is { Connected: true, WaitingForNeutral: true, Latest: null } && feed.Received == count,
    "Changing profiles while disconnected and reattaching do not restore stale physical readings.");
feed.Report(returning, held);
feed.Report(original, neutral);
feed.Report(returning, [1, 15]);
Check(feed.Connection is { Connected: true, Reconnects: 1, WaitingForNeutral: true, Latest.Raw: var heldOnReconnect } &&
    heldOnReconnect.SequenceEqual(held) &&
    sent.Count == 3 && feed.Unexpected == 1, "A stale, held or malformed packet cannot arm the new connection.");
feed.Report(returning, neutral);
feed.Report(returning, held);
Check(!feed.Connection.WaitingForNeutral && sent[^1] == alternateHeld, "Fresh input uses the profile selected while offline.");
var beforeHeldSwitch = feed.Latest;
feed.ChangeBindings(map);
Check(feed.Connection is { WaitingForNeutral: true } && feed.Connection.State == LiveState.Neutral &&
    ReferenceEquals(feed.Latest, beforeHeldSwitch) && feed.Received == beforeHeldSwitch?.Number,
    "Switching while held preserves the physical report while releasing the mapped output.");
feed.Report(returning, held);
Check(sent[^1] == LiveState.Neutral && feed.Connection.WaitingForNeutral, "A profile switch cannot generate a second press from a held button.");
feed.Report(returning, neutral);
feed.Report(returning, held);
Check(sent[^1] == originalHeld, "Resumed input uses the switched profile.");
feed.Report(returning, neutral);
var beforeReleasedSwitch = feed.Latest;
feed.ChangeBindings(alternate);
Check(!feed.Connection.WaitingForNeutral && ReferenceEquals(feed.Latest, beforeReleasedSwitch) &&
    feed.Received == beforeReleasedSwitch?.Number && feed.Connection.State == LiveState.Neutral,
    "Switching while released preserves the physical report without issuing a new one.");
feed.Report(returning, held);
Check(sent[^1] == alternateHeld, "Switching from an already released state accepts the next press.");
feed.Report(returning, neutral);
feed.ChangeBindings(map);
feed.Report(returning, allHeld);
int beforeSleep = sent.Count;
long reportsBeforeSleep = feed.Received;
feed.PowerChanged(true);
feed.PowerChanged(true);
feed.Report(returning, neutral);
feed.Report(returning, allHeld);
Check(sent.Count == beforeSleep + 1 && sent[^1] == LiveState.Neutral && feed.Received == reportsBeforeSleep
    && feed.Connection is { Suspended: true, Suspends: 1, Resumes: 0, WaitingForNeutral: true, Latest: null }
    && feed.Connection.LastSuspendedState == allPressed && feed.Connection.LastSuspendDetach == null,
    "Suspend forces neutral and rejects queued input while asleep. An earlier reattached device cannot count as a sleep detach.");
feed.PowerChanged(false);
feed.PowerChanged(false);
Check(sent.Count == beforeSleep + 2 && feed.Connection is { Suspended: false, Resumes: 1, WaitingForNeutral: true, Latest: null }
    && feed.Connection.ReceivedAtResume == reportsBeforeSleep,
    "Wake forces a second neutral even on an unchanged handle. A duplicate wake must preserve the gate.");
feed.Report(returning, held);
Check(sent[^1] == LiveState.Neutral && feed.Connection.WaitingForNeutral, "A still-held input cannot activate on wake.");
feed.Report(returning, neutral);
feed.Report(returning, held);
feed.PowerChanged(false);
Check(sent[^1] == originalHeld && feed.Connection is { Resumes: 1, WaitingForNeutral: false },
    "Fresh neutral and press restore the same profile, including after a late user-wake notification.");

feed.SetDevice(null);
var detached = feed.Connection;
feed.PowerChanged(true);
var evidence = feed.Connection.LastSuspendDetach;
Check(evidence != null && evidence.AtUtc <= feed.Connection.LastSuspendUtc &&
    evidence.HeldState == originalHeld && evidence.Received == detached.Received &&
    evidence.Disconnects == detached.Disconnects && evidence.Reconnects == detached.Reconnects &&
    evidence.Generation == detached.Generation && feed.Connection.LastSuspendedState == LiveState.Neutral,
    "When Bluetooth is removed first, retain the held sample and exact attachment epoch through a genuine suspend.");
feed.PowerChanged(false);
feed.SetDevice(original);
Check(feed.Connection.LastSuspendDetach == evidence, "Retain sleep evidence when the physical pad returns.");
feed.PowerChanged(true);
Check(feed.Connection.LastSuspendDetach == null, "Clear a previous sleep cycle's detach evidence on the next suspend.");
feed.PowerChanged(false);

ulong beforeWrap = (ulong)uint.MaxValue - 5;
ulong afterWrap = beforeWrap + 10;
Check(!RawInput.PostedAfterResume(unchecked((uint)(beforeWrap - 1)), beforeWrap, afterWrap)
    && !RawInput.PostedAfterResume((uint)beforeWrap, beforeWrap, afterWrap)
    && RawInput.PostedAfterResume(unchecked((uint)afterWrap), beforeWrap, afterWrap),
    "Queued input from before wake cannot clear the barrier across a 32-bit tick rollover.");
feed.Stop();
Check(sent[^1] == LiveState.Neutral && feed.Connection.Disconnects == detached.Disconnects,
    "Stopping releases the controls without counting a physical removal.");
Console.WriteLine("Passed device identity, stale report, disconnect/reconnect, full neutral, profile-switch and suspend/resume checks.");
