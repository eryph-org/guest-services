namespace Eryph.GuestServices.Pty;

public class PtyException(string message, int result) : Exception(message)
{
    public int Result { get; } = result;
}
