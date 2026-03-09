# Sc4Pro

A Linux BLE client for the **SC4Pro golf launch monitor** (by Voice Caddie). Connects directly to the device over Bluetooth Low Energy, replicates the official app's GATT handshake, and streams typed shot data to stdout as JSON.

## Projects

| Project | Type | Description |
|---|---|---|
| `Sc4Pro` | Class library | BLE transport, protocol, packets, device logic |
| `Sc4Pro.StartUp` | Executable | Connects to the device and streams shot events |
| `Sc4Pro.Analyze` | Executable | Replays a captured GATT session for protocol verification |

## Requirements

- Linux with BlueZ (D-Bus)
- .NET 10 SDK
- A paired SC4Pro device

## Running

**Connect and stream shots:**
```bash
dotnet run --project Sc4Pro.StartUp
```

Output: one JSON object per received packet (shots, remote button presses).

**Replay a captured session:**
```bash
dotnet run --project Sc4Pro.Analyze
```

Connects to the device, replays the full sequence from `gatt_clicks.txt`, and saves the result to `dump_test.json` for comparison with the reference dump.

## Architecture

```
Sc4Pro/
├── Bluetooth/
│   ├── IBleChannel          — transport abstraction (scan, connect, send, notify)
│   └── BleChannel           — Ble cChannel
│       └── BleChannel.Linux        — Linux.BlueZ D-Bus implementation
│       └── BleChannel.Android      — Android.Bluetooth D-Bus implementation
│       └── BleChannel.Windows      — Windows.Devices.Bluetooth D-Bus implementation
├── Logic/
│   ├── Sc4ProClient         — typed async commands, ack routing, event dispatch
│   └── Sc4ProDevice         — connects, reads GATT config, runs handshake
├── Packets/                 — typed records for every packet the device sends or receives
│   ├── Sc4ProPacket         — base record (Cmd + Raw)
│   ├── ShotPacket           — wrapper for the 6 shot sub-packets
│   ├── ShotMetadata         — seq=1: timestamp, club, loft
│   ├── ShotBallSpeed        — seq=2: pressure, temperature, ball speed
│   ├── ShotClubCarry        — seq=3: club speed, launch angle, carry
│   ├── ShotDistanceApex     — seq=4: total distance, apex, total spin
│   ├── ShotDirection        — seq=5: launch direction, tilt
│   ├── ShotSpinDetails      — seq=6: back/side spin, attack angle, club path
│   ├── SyncAck              — serial number from the device
│   ├── DeviceSetting1Ack    — ack for mode/club/loft commands
│   ├── DeviceSetting2Ack    — ack for volume/display commands
│   ├── EqSettingAck         — ack for EQ command
│   ├── ShotReadyAck         — device armed for next shot
│   ├── RemoteControlPacket  — hardware remote button press
│   └── ClubType / DS1Flags / DS2Flags — enums
└── Protocol/
    ├── PacketBuilder        — serialises outgoing command packets (20 bytes)
    └── PacketParser         — deserialises incoming BLE notifications
```

## Protocol

All command packets are 20 bytes:

```
[0x53][cmd][16 content bytes][0x45][checksum]
```

Checksum = `(-sum of bytes 0–18) & 0xFF`. The device acks every command with the same `cmd` byte.

### Connection handshake

```
→ Sync          (sends current datetime)
← SyncAck       (device replies with serial number)
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
