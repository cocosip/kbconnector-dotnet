namespace KingbaseConnector;

/// <summary>A non-durable notification received from the active PG-wire session.</summary>
public sealed class KingbaseNotificationEventArgs : EventArgs
{
    internal KingbaseNotificationEventArgs(int processId, string channel, string payload)
    { ProcessId = processId; Channel = channel; Payload = payload; }
    public int ProcessId { get; }
    public string Channel { get; }
    /// <summary>Server-supplied application data; may contain sensitive information.</summary>
    public string Payload { get; }
}
