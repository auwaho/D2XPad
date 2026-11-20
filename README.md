# D2XPad

**DirectInput to XInput Converter**

D2XPad turns a compatible Windows gamepad into a virtual Xbox Series X|S controller. It reads independent analog trigger travel from raw HID reports in Bluetooth DirectInput mode, including on setups where other remappers only see on/off trigger buttons.

![D2XPad Converter window with stick, trigger and button testers](docs/images/d2xpad.png)

The **8BitDo Ultimate 3C+ Wireless** is the only fully tested controller so far. Independent analog triggers, extra-button remapping and grip-motor vibration are verified over Bluetooth. More controllers will be tested.

## Requirements

- Windows x64 with the .NET 10 Desktop Runtime (x64). [Download the runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- A compatible USB or Bluetooth HID controller.

Extract all files from the portable ZIP to a writable folder and open `D2XPad.exe`. When you start conversion, Windows may ask for administrator approval for the bundled HIDMaestro driver host. [Download for Windows](https://github.com/auwaho/D2XPad/releases)

[User guide](docs/usage.md) · [Architecture](docs/architecture.md) · [MIT License](LICENSE)
