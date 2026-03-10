<p align="center">
  <img src="icon.png" width="96" height="96" alt="Shinobi.Sc4Pro Logo">
</p>

# Shinobi.Sc4Pro

A .NET BLE client for the **SC4Pro golf launch monitor** (by Voice Caddie), part of the [Shinobi](https://github.com/hempe) library family. Connects directly to the device over Bluetooth Low Energy, replicates the official app's GATT handshake, and streams typed shot data.

The core is **netstandard2.1** — suitable for use in Unity projects on Windows, Linux, and Android.

## Projects

| Project | TFM | Description |
|---|---|---|
| `Shinobi.Sc4Pro.Bluetooth` | `netstandard2.1` | `IBleChannel` transport abstraction |
| `Shinobi.Sc4Pro.Packets` | `netstandard2.1` | Typed records for every device packet |
| `Shinobi.Sc4Pro.Protocol` | `netstandard2.1` | `PacketBuilder` / `PacketParser` |
| `Shinobi.Sc4Pro.Logic` | `netstandard2.1` | `Sc4ProClient` and `Sc4ProDevice` |
| `Shinobi.Sc4Pro.Linux` | `net10.0` | `BleChannel` via Linux BlueZ D-Bus |
| `Shinobi.Sc4Pro.Windows` | `net10.0-windows` | `BleChannel` via Windows.Devices.Bluetooth |
| `Shinobi.Sc4Pro.Android` | `net10.0-android` | `BleChannel` via Android.Bluetooth |
| `Shinobi.Sc4Pro.StartUp` | `net10.0` | Executable — connects and streams shot events |
| `Shinobi.Sc4Pro.Analyze` | `net10.0` | Executable — replays a captured GATT session |

## Usage

Instantiate the appropriate `BleChannel` for your platform and pass it to `Sc4ProDevice`:

```csharp
// Linux
await using var device = new Sc4ProDevice(new Shinobi.Sc4Pro.Bluetooth.BleChannel());
await device.ConnectAsync();

device.PacketReceived += pkt =>
{
    Console.WriteLine(pkt);
    return Task.CompletedTask;
};
```

For Unity, reference the four `netstandard2.1` core projects and provide your own `IBleChannel` implementation using your preferred Unity BLE plugin.

## Running

**Connect and stream shots (Linux):**
```bash
dotnet run --project Shinobi.Sc4Pro.StartUp
```

**Replay a captured session:**
```bash
dotnet run --project Shinobi.Sc4Pro.Analyze
```

## Requirements

- .NET 10 SDK (for the platform projects and executables)
- Platform-specific:
  - **Linux**: BlueZ with D-Bus access
  - **Windows**: Windows 10 1803+ (BLE GATT client APIs)
  - **Android**: API 21+, `BLUETOOTH_SCAN` and `BLUETOOTH_CONNECT` permissions

## Architecture

```
Shinobi.Sc4Pro.Bluetooth/
└── IBleChannel              — scan, connect, send, receive, dispose

Shinobi.Sc4Pro.Packets/
├── Sc4ProPacket             — base record (Cmd + Raw)
├── ShotPacket               — wrapper for the 6 shot sub-packets
├── ShotMetadata             — seq=1: timestamp, club, loft
├── ShotBallSpeed            — seq=2: pressure, temperature, ball speed
├── ShotClubCarry            — seq=3: club speed, launch angle, carry
├── ShotDistanceApex         — seq=4: total distance, apex, total spin
├── ShotDirection            — seq=5: launch direction, tilt
├── ShotSpinDetails          — seq=6: back/side spin, attack angle, club path
├── SyncAck                  — serial number from the device
├── DeviceSetting1Ack        — ack for mode/club/loft commands
├── DeviceSetting2Ack        — ack for volume/display commands
├── EqSettingAck             — ack for EQ command
├── ShotReadyAck             — device armed for next shot
├── RemoteControlPacket      — hardware remote button press
└── ClubType / DS1Flags / DS2Flags

Shinobi.Sc4Pro.Protocol/
├── PacketBuilder            — serialises outgoing command packets (20 bytes)
└── PacketParser             — deserialises incoming BLE notifications

Shinobi.Sc4Pro.Logic/
├── Sc4ProClient             — typed async commands, ack routing, event dispatch
└── Sc4ProDevice             — connects, reads GATT config, runs handshake

Shinobi.Sc4Pro.Linux/
└── BleChannel               — Linux BlueZ D-Bus implementation

Shinobi.Sc4Pro.Windows/
└── BleChannel               — Windows WinRT implementation

Shinobi.Sc4Pro.Android/
└── BleChannel               — Android Bluetooth GATT implementation
```

## Protocol

All command packets are 20 bytes:

```
[0x53][cmd][16 content bytes][0x45][checksum]
```

Checksum = `(-sum of bytes 0–18) & 0xFF`. The device acks every command with the same `cmd` byte.

### Connection handshake

```
→ Sync           (sends current datetime)
← SyncAck        (device replies with serial number)
→ DeviceSetting2 (volume, appIndex)
← DeviceSetting2Ack
→ DeviceSetting1 (mode, club)
← DeviceSetting1Ack
→ EqSetting
← EqSettingAck
```

### Changing club

```
→ DeviceSetting1 (DS1Flags.Club, club=<type>)
← DeviceSetting1Ack
→ ShotReady
← ShotReadyAck   ← device is now armed
```

### Shot data

Each shot produces six sequential `ShotPacket` notifications sharing the same `Index`, with `Seq` 1–6 carrying different payloads (see `Packets/` above).

### GATT characteristics

| UUID suffix | Direction | Purpose |
|---|---|---|
| `50340002` | Host → Device | TX: write commands here |
| `50340003` | Device → Host | RX: subscribe for notifications |
