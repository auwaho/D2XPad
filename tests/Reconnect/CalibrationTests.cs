using D2XPad.Capture;
using D2XPad.Live;

internal static class CalibrationTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Reject(Action operation, string message)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException(message);
    }

    // One signed 12-bit axis, an unaligned 10-bit trigger, a switched trigger
    // and an analog trigger mirror, plus a one-based clockwise hat and a paddle.
    private static byte[] Packet(int y = -12, int lt = 1023, int rt = 0, int hat = 0, int a = 0, int paddle = 0)
    {
        byte[] report = [9, 0, 0, 0, 0, 0, 0, 0];
        void Put(int at, int width, int value)
        {
            for (int i = 0; i < width; i++)
                report[(at + i) / 8] |= (byte)(((value >> i) & 1) << ((at + i) % 8));
        }
        Put(11, 12, y); Put(25, 10, lt); Put(45, 1, rt); Put(46, 1, lt < 900 ? 1 : 0);
        Put(48, 4, hat); Put(56, 1, a); Put(59, 1, paddle);
        return report;
    }

    public static void Run()
    {
        string? originalDir = Environment.GetEnvironmentVariable("D2XPAD_LAYOUT_DIR");
        var temporary = Directory.CreateTempSubdirectory("D2XPad-Calibration-");
        string layouts = Path.Combine(temporary.FullName, "layouts");
        try
        {
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", layouts);
            var device = new HidCollection(10, "gamepad-path", 0x4321, 0x1020, 1, 4, "USB", "pad-one", 8, [9],
                [new(9, 1, 0x31, 11, 12, -2048, 2047), new(9, 2, 0xc4, 25, 10, 0, 1023), new(9, 1, 0x39, 48, 4, 1, 8)]);
            Check(!ControllerCalibration.IsCandidate(device with { Transport = "Virtual" }) &&
                !ControllerCalibration.IsCandidate(device with { Usage = 6 }) &&
                !ControllerCalibration.IsCandidate(device with { Path = "SWD\\HIDMAESTRO_123" }), "Virtual and non-controller sources");
            Reject(() => new ControllerCalibration(device with { InputReportIds = [1, 9] }, "Split"), "Split reports accepted");
            CheckRestingTelemetry(device);
            var opaque = new ControllerCalibration(device with
            {
                InputReportLength = 5, InputReportIds = [0], InputValues = [new(0, 2, 0xc5, 32, 8, 0, 1)]
            }, "Trigger pad");
            opaque.AddNeutral([0, 0, 0, 0, 0]);
            opaque.AddNeutral([0, 0, 0, 0, 0]);
            Check(!opaque.IsNeutral([0, 0, 0, 0, 1]), "Wide digital trigger was treated as neutral jitter");
            var uncertain = new ControllerCalibration(device with
            {
                InputReportLength = 5, InputReportIds = [0], InputValues = [new(0, 2, 0xc5, 32, 8, 0, 1)]
            }, "Analog and mirror");
            uncertain.AddNeutral([0, 0, 0, 0, 0]);
            uncertain.AddNeutral([0, 1, 0, 0, 0]);
            Check(uncertain.IsNeutral([0, 1, 0, 0, 0]) && !uncertain.IsNeutral([0, 0x80, 0, 0, 0]),
                "An extra sharing its byte with a changing status bit must remain recordable");
            uncertain.BindExtra("EXT", [0, 0x80, 0, 0, 0], [0, 0x81, 0, 0, 0]);
            Reject(() => uncertain.BindTrigger("RT", [0, 0, 255, 0, 1], [0, 1, 255, 0, 1],
                [0, 1, 255, 0, 1], [0, 0, 255, 0, 1]), "Unclear hidden analog travel was downgraded to declared digital mirror");
            // A payload with no report IDs retains Windows' leading zero byte.
            Reject(() => opaque.BindTrigger("LT", [0, 1, 120, 123, 0], [0, 1, 125, 120, 0],
                [0, 1, 255, 250, 0], [0, 1, 255, 250, 0]), "Ambiguous hidden analog fields accepted");
            opaque.BindTrigger("LT", [0, 1, 120, 0, 0], [0, 1, 125, 0, 0], [0, 1, 255, 0, 0], [0, 1, 255, 0, 0]);
            opaque.BindTrigger("RT", [0, 0, 0, 0, 1], [0, 0, 0, 0, 1], [0, 0, 0, 0, 1], [0, 0, 0, 0, 1]);
            Check(opaque.Preview().TryDecodePhysical([0, 1, 83, 0, 1], out var hidden, out _) &&
                hidden.Lt == 83 && hidden.Rt == 255, "Hidden analog travel or wide binary trigger lost");
            var fourWay = new ControllerCalibration(device with
            {
                InputReportLength = 2, InputValues = [new(9, 1, 0x39, 8, 4, 0, 3)]
            }, "Four-way hat");
            fourWay.AddNeutral([9, 15]); fourWay.AddNeutral([9, 15]);
            byte[][] reversed = [[9, 2], [9, 1], [9, 0], [9, 3]];
            fourWay.BindDpad(reversed, reversed);
            Check(fourWay.Preview().TryDecodePhysical([9, 3], out var west, out _) && west.Hat == 6, "Rotated reversed four-way hat");
            var discrete = new ControllerCalibration(device with { InputReportLength = 2, InputValues = [] }, "Four switches");
            discrete.AddNeutral([9, 0]); discrete.AddNeutral([9, 0]);
            byte[][] switches = [[9, 8], [9, 32], [9, 4], [9, 64]];
            discrete.BindDpad(switches, switches);
            var plus = discrete.Preview();
            Check(plus.TryDecodePhysical([9, 40], out var northeast, out _) && northeast.Hat == 1 &&
                !plus.IsPhysicallyReleased([9, 12]), "D-pad chord and neutral gate for opposite switches");
            var capture = new ControllerCalibration(device, "USB Arcade");
            capture.AddNeutral(Packet());
            capture.AddNeutral(Packet(y: -10));
            Check(capture.IsNeutral(Packet(y: -9)) && !capture.IsNeutral(Packet(a: 1)), "Released pose and analog jitter");
            Reject(() => capture.BindAxis("LY", Packet(), Packet(), Packet(), Packet()), "Neutral axis evidence accepted");
            capture.BindAxis("LY", Packet(y: 2047), Packet(y: 2030), Packet(y: -2048), Packet(y: -2044));
            Reject(() => capture.BindTrigger("LT", Packet(lt: 0), Packet(lt: 0), Packet(lt: 0), Packet(lt: 0)),
                "An unverified analog trigger was downgraded to its digital mirror");
            capture.BindTrigger("LT", Packet(lt: 530), Packet(lt: 540), Packet(lt: 0), Packet(lt: 0));
            capture.BindTrigger("RT", Packet(rt: 1), Packet(rt: 1), Packet(rt: 1), Packet(rt: 1));
            capture.BindButton("A", Packet(a: 1), Packet(a: 1));
            Reject(() => capture.BindExtra("A", Packet(paddle: 1), Packet(paddle: 1)), "Reserved extra name accepted");
            Reject(() => capture.BindExtra("LONGPAD", Packet(paddle: 1), Packet(paddle: 1)), "Long extra name accepted");
            Reject(() => capture.BindExtra("PAD", Packet(a: 1), Packet(a: 1)), "Duplicate physical field accepted");
            Reject(() => capture.BindExtra("PAD", Packet(a: 1, paddle: 1), Packet(a: 1, paddle: 1)), "Ambiguous press accepted");
            capture.BindExtra("P1", Packet(paddle: 1), Packet(paddle: 1));
            byte[][] directions = [Packet(hat: 1), Packet(hat: 3), Packet(hat: 5), Packet(hat: 7)];
            capture.BindDpad(directions, directions);
            var preview = capture.Preview();
            Check(PhysicalSession.LayoutFor(device) == null && !Directory.Exists(layouts), "Preview committed layout");
            var saved = capture.Save();
            Check(preview.Id == saved.Id && saved.ExtraSources.Single() == new ExtraSource("bit-59", "P1", 1), "Unstable physical identity");
            // A process that has never opened this catalog must recover the layout from JSON.
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", Path.Combine(temporary.FullName, "other"));
            Check(PhysicalSession.LayoutFor(device) == null, "Cache crossed layout directories");
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", layouts);
            var layout = PhysicalSession.LayoutFor(device) ?? throw new InvalidDataException("Saved layout missing");
            var changed = device with { InputValues = [new(9, 1, 0x31, 12, 12, -2048, 2047)] };
            Check(PhysicalSession.LayoutFor(changed) == null && PhysicalSession.LayoutFor(device with { Transport = "Bluetooth" }) == null &&
                PhysicalSession.LayoutFor(device with { InputReportIds = [0] }) == null, "Wrong report collection accepted");
            var catalog = new ProfileCatalog(Path.Combine(temporary.FullName, "profiles"));
            var profile = catalog.OpenEditor(saved.DefaultProfileName);
            Check(!profile.CanReplace && profile.Profile.Bindings.Layout.Id == layout.Id &&
                profile.Profile.Bindings.Sources.Any(s => s.Id == "LeftStickUp") &&
                !profile.Profile.Bindings.Sources.Any(s => s.Id == "LeftStickRight"), "Incorrect sources in learned default profile");
            Check(layout.TryDecodePhysical(Packet(y: 2047, lt: 510, rt: 1, hat: 8, a: 1, paddle: 1), out var applied, out var extras) &&
                applied == new LiveState(128, 0, 128, 128, 128, 255, 7, 0x1000) && (ulong)extras == 1, "Normalized physical controls");
            Check(!layout.TryDecodePhysical([1], out _, out _) && !layout.IsPhysicallyReleased(Packet(paddle: 1)), "Invalid or held report accepted");
            LiveState output = LiveState.Neutral;
            var session = new PhysicalSession(device, profile.Profile.Bindings, s => output = s, () => output = LiveState.Neutral);
            session.Report(device, Packet(y: 2047, a: 1));
            Check(output == LiveState.Neutral && session.Connection.WaitingForNeutral, "Opened on held controls");
            session.Report(device, Packet());
            session.Report(device, Packet(y: -2048, lt: 510, a: 1));
            Check(output == (LiveState.Neutral with { Ly = 255, Lt = 128, Buttons = 0x1000 }), "Live identity decoding");
            session.SetDevice(null);
            Check(output == LiveState.Neutral, "Learned controller failed to release on disconnect");
            var returning = device with { Handle = 22, Path = "return-path" };
            Check(session.SelectInstance([changed, returning]) == returning, "Lost calibrated identity on reconnect");
            session.SetDevice(returning);
            session.Report(returning, Packet(paddle: 1));
            Check(session.Connection.WaitingForNeutral, "An unassigned held paddle bypassed release gate");
            session.Report(returning, Packet());
            session.Report(returning, Packet(a: 1, rt: 1));
            Check(output == (LiveState.Neutral with { Buttons = 0x1000, Rt = 255 }), "Control after reconnect");
            Console.WriteLine("Passed calibrated descriptor, signed packed axis, analog/digital triggers, hat, fixed extras, storage, profile and reconnect checks.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("D2XPAD_LAYOUT_DIR", originalDir);
            temporary.Delete(recursive: true);
        }
    }

    private static void CheckRestingTelemetry(HidCollection device)
    {
        var collection = device with
        {
            InputReportLength = 5,
            InputValues = [new(9, 1, 0x30, 8, 8, 0, 255)],
            InputButtons = [new(9, 3, 0x80)]
        };
        var draft = new ControllerCalibration(collection, "Telemetry pad");
        draft.AddNeutralSamples([[9, 128, 0, 0, 0], [9, 128, 1, 1, 0], [9, 128, 2, 2, 0]]);
        Reject(() => draft.AddNeutralSamples([[9, 128, 0, 0x80, 0]]),
            "A held declared button was accepted in a resting sample");
        draft.AddNeutralSamples([[9, 128, 1, 0, 0], [9, 128, 2, 1, 0]]);
        foreach (byte counter in new byte[] { 0, 1, 2, 255 })
            Check(draft.IsNeutral([9, 128, counter, (byte)(counter & 0x7f), 0]),
                $"Idle counter {counter} was treated as control movement");
        Check(!draft.IsNeutral([9, 128, 255, 0xff, 0]) && !draft.IsNeutral([9, 255, 255, 0x7f, 0]) &&
            !draft.IsNeutral([9, 128, 255, 0x7f, 4]), "A mixed declared button, axis or opaque extra bypassed the release gate");
        Reject(() => draft.BindExtra("NOISE", [9, 128, 255, 0x7f, 0], [9, 128, 255, 0x7f, 0]),
            "Varying opaque telemetry was learned as an extra button");
        draft.BindButton("A", [9, 128, 100, 0x81, 0], [9, 128, 255, 0xfe, 0]);
        draft.BindExtra("P1", [9, 128, 2, 0x7c, 4], [9, 128, 255, 0x7f, 4]);
        Check(draft.Preview().TryDecodePhysical([9, 128, 255, 0xff, 4], out var physical, out var extras) &&
            physical.Buttons == 0x1000 && (ulong)extras == 1, "Telemetry masked a genuine captured input");

        var original = new ControllerEntry(draft.Preview(), false, true, false, "fixture");
        var recalibration = new ControllerCalibration(collection, "Telemetry pad", original);
        recalibration.AddNeutralSamples([[9, 128, 0, 0, 0], [9, 128, 1, 1, 1], [9, 128, 2, 2, 2]]);
        recalibration.AddNeutral([9, 128, 0, 0, 0]);
        Check(recalibration.IsNeutral([9, 128, 255, 0x7f, 3]) && !recalibration.IsNeutral([9, 128, 255, 0x7f, 7]),
            "Idle telemetry masked a previously measured extra");
    }
}
