namespace D2XPad.Live;

internal static class ConverterReservation
{
    public const string StopEvent = @"Local\D2XPad.Live.Stop";

    public static EventWaitHandle ReserveStopped(string busyMessage, string name = StopEvent)
    {
        var reservation = new EventWaitHandle(false, EventResetMode.ManualReset, name, out bool created);
        if (created) return reservation;
        reservation.Dispose();
        throw new InvalidOperationException(busyMessage);
    }
}
