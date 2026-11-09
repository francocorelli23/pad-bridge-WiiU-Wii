# PadBridge

Use Wii U Pro Controllers and Wii Remotes (alone, + Nunchuk, + Classic / Classic Pro) on Windows as Xbox 360 controllers.
This replaces WiinUSoft.

## Requirements
- Windows 10/11 x64, Bluetooth turned **on**
- [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) driver (v1.17 or newer; tested with 1.21)
- No SCP driver needed

## Use
1. Build it (see below) and run `PadBridge\bin\PadBridge.exe`.
2. **First time only (and once for each controller that was paired before):** click **Pair controller**, then press the red **SYNC** button
   (on the Pro Controller it's next to the charging port; on a Wii Remote it's under the battery cover).
   PadBridge removes any old Windows pairing for that controller and pairs it again correctly.
3. After that, press any button on the controller and it connects by itself. The LED shows the player number.

## What's fixed compared to WiinUSoft
| Problem | Cause | PadBridge |
|---|---|---|
| You had to delete the controller in Windows Settings and pair it again | The controller was paired without the PIN it expects (the PC's Bluetooth address), or the old pairing went stale | It forgets stale pairings automatically and pairs with the correct PIN, so pressing a button reconnects it |
| Sometimes the controller wasn't recognized | Windows keeps sleeping controllers listed as HID devices, and after sleep/resume the HID part can get stuck | It checks that each device really responds, retries, and resets the HID service when Windows says "connected" but no HID device shows up |
| Old, unsigned SCP virtual bus | SCP stopped being maintained in 2015 | Uses ViGEmBus. If the app crashes, the driver removes its virtual pads automatically |

## Mapping (default: "by position")
Wii U Pro / Classic: A→B, B→A, X→Y, Y→X (same positions as an Xbox pad); L/R→LB/RB; ZL/ZR→LT/RT; +→Start; −→Back; Home→Guide.
Switch to **by label** in Options if you want A = A.
Wii Remote alone is held sideways: 2→A, 1→B, D-pad rotated.
Wii Remote + Nunchuk: stick→left stick, A→A, B→RT, Z→LT, C→LB.

## Build
Run `PadBridge\build.cmd`. It needs [Visual Studio 2022 Build Tools](https://visualstudio.microsoft.com/visual-cpp-build-tools/) (the C# compiler, no NuGet packages) and builds `PadBridge\bin\PadBridge.exe` for .NET Framework 4.8.

Run `PadBridge\bin\PadBridge.exe --selftest` to check the ViGEm → XInput path; it writes `padbridge-selftest.txt` next to the exe.
The app log is at `%APPDATA%\PadBridge\log.txt`.

## Tips
- If the controller drops out often, open Device Manager → Bluetooth → your adapter → Power Management and untick
  "Allow the computer to turn off this device to save power".
