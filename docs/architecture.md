# D2XPad architecture

D2XPad reads one selected physical controller and presents one virtual Xbox Series X|S controller to Windows. It uses Windows Raw Input to read HID gamepad reports, including analog data that may be unavailable through the physical device's DirectInput mapping.

```mermaid
flowchart LR
    Device["Physical HID controller"] --> Raw["Windows Raw Input"]
    Raw --> Layout["Controller layout"]
    Layout --> Mapping["Mapping profile"]
    Mapping --> Virtual["HIDMaestro Xbox Series X/S"]
    Virtual --> Game["XInput application"]
```

## Input and mappings

- A **controller layout** decodes a particular HID report format into physical button, axis and trigger states. The built-in Bluetooth 85HF layout reads two independent analog trigger fields and four independent extras.
- The calibration wizard learns layouts from a physical USB or Bluetooth gamepad or joystick with one input report ID. Its signature includes VID/PID, transport, usage, report framing and a fingerprint of the HID fields. This matches a saved layout to its report format. The running session also tracks the selected physical instance by interface path and PnP container ID.
- A **profile** belongs to one layout and stores mappings from physical inputs to Xbox actions. One input can contribute to several outputs. Analog actions scale with physical travel and configured strength. Multiple contributions to the same output use the strongest value, and opposing stick directions subtract.
- The WPF desktop can show the decoded physical input before mapping and independently read the resulting virtual controller through XInput.

## Virtual output and session lifecycle

The standard-user converter maintains physical Raw Input and controller state. An elevated HIDMaestro host installs the bundled driver when needed and owns the virtual device. It uses the `xbox-series-xs-bt` profile with independent triggers. A short trigger sequence confirms which XInput slot follows the virtual controller before forwarding physical input. The build pins the SHA-256 of the official HIDMaestro release archive. [HIDMaestro 2.3.0 SDK](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v2.3.0)

By default, the portable package starts that host with Windows elevation for each conversion. Installing the optional **D2XPad Conversion Service** registers an automatic `LocalSystem` service and copies its host and SDK to a protected directory under Program Files. The converter sends Xbox reports through local named pipes. The service restricts access to the installing account in the active Windows session, and the converter verifies the service's process identity before sending reports. Raw Input, calibration, mappings and the physical vibration worker stay in the signed-in user's process.

The service owns one virtual output at a time. Closing the connection or stopping the session releases that output, and the host also has an inactivity timeout. An installed but unavailable service produces an error so the user can diagnose the service state. Removing the optional service restores portable startup.

When **Start conversion automatically** is enabled, the desktop waits for the saved physical controller in the signed-in user's session, including when the window is hidden. It matches the device's layout and physical identity, then starts the converter with the saved profile and explicitly requests service output. Initial waiting uses no virtual device or XInput slot. The same session handles input and reconnection once conversion is running. Stop and Exit cancel the desktop's wait as well as ending active conversion.

A compatible profile switch resets Xbox output to neutral on the existing slot. Disconnect and Windows suspend also neutralize output. Once the same physical device is available and its inputs return to rest, the session can resume on the existing virtual controller. Stopping conversion releases that controller.

## Vibration

The HIDMaestro host forwards supported left and right motor effects through a separate feedback channel. A physical output worker converts those effects to the verified Bluetooth 85HF HID report. The worker clears the active effect when vibration is disabled, the controller detaches, Windows suspends or conversion stops. Trigger motors and other physical transports need their own supported feedback route.

## Storage

The Bluetooth 85HF layout and its presets ship with the app. Saved profiles live in `profiles\` beside the executable. Layouts and desktop preferences live under `%LOCALAPPDATA%\D2XPad\`. Separating these records keeps physical report offsets and calibration tied to the controller and lets several profiles share one layout. [Usage and backup locations](usage.md#back-up-profiles-and-layouts)

**Start with Windows** registers the current portable executable in the Windows startup list for the signed-in user. The last profile selection is saved in the desktop preferences per controller layout.

## Build from source

On Windows x64 with the .NET 10 SDK, run `.\scripts\Build-Desktop.ps1` in PowerShell from the repository root. The build verifies the bundled HIDMaestro dependency and publishes the desktop package to `artifacts/Desktop`. [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

Release builds use the tag as the version source. Pushing a tag such as `v1.2.3` for a commit on `main` builds `D2XPad-v1.2.3-win-x64.zip` and its SHA-256 checksum. The desktop, converter and service setup executables report product version `1.2.3` and file version `1.2.3.0`. Prerelease suffixes appear in the ZIP name and product version.

### Controller diagnostics

Run these commands from the source tree with the .NET 10 SDK.

| Command | Purpose |
| --- | --- |
| `.\Record-Controller.cmd list-raw` | List physical USB and Bluetooth HID gamepad and joystick collections. |
| `.\Record-Controller.cmd record-raw --device 1 --seconds 30` | Save timestamped raw reports from the selected collection with no calibration required. The capture is saved under `captures\` by default. |
| `.\Play-Controller.cmd list` | List connected controllers with a built-in or saved layout. |
| `.\Play-Controller.cmd play --device 1` | Start conversion with the selected controller's default profile. Pass `--profile NAME-OR-FILE` to choose a different compatible profile. |

Numbers are specific to each list. Omitting `--device` selects the only matching item when the list has exactly one entry. To rebuild the console converter after source changes, run `.\scripts\Build-Live.ps1`. The 85HF guided capture (`Record-Controller.cmd record`), capture analyzer and guided live checks use the known 85HF report format and test sequence.

## Acknowledgment

UR-XD provided an early reference for understanding the Bluetooth 85HF's raw reports. D2XPad's built-in layout was verified against its own input captures. [UR-XD v2.2](https://github.com/jesga06/UR-XD/releases/tag/v2.2)
