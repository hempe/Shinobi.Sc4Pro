namespace Sc4Pro.Bluetooth;

/// <summary>
/// Abstraction over a BLE transport. Implementations handle scanning, connecting,
/// sending raw packets, and firing notifications received from the device.
/// </summary>
public interface IBleChannel : IAsyncDisposable
{
    /// <summary>Fired for every raw notification received from the device.</summary>
    event Func<byte[], Task>? Received;

    /// <summary>Scans, connects, and subscribes for notifications. Returns the device name.</summary>
    Task<string> ConnectAsync(string serviceUuid, string txUuid, string rxUuid);

    /// <summary>Writes a raw packet to the device.</summary>
    Task SendAsync(byte[] packet);

    /// <summary>Reads all readable characteristics and returns a UUID → raw-bytes map.</summary>
    Task<IReadOnlyDictionary<string, byte[]>> ReadConfigAsync();
}
