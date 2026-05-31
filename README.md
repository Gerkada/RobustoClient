# RobustoClient

![GitHub release](https://flat.badgen.net/github/release/Gerkada/RobustoClient)
![.NET](https://img.shields.io/badge/.NET-10.0-blue?style=flat-square)
![License](https://img.shields.io/badge/license-GPL--3.0-green?style=flat-square)

Based on the original Arabica client by noverd. Massive thanks for the foundational framework.

RobustoClient is a heavily modified, advanced open-source cheat client for Space Station 14. It features a completely rewritten architecture for critical systems, including a flawless mathematical AutoChem solver, precise combat prediction, and advanced entity detection.

## Disclaimer
This project is created strictly for educational purposes. The author is not responsible for how this client is used by others.

## Requirements
To run this, you need [MarseyLoader](https://github.com/ValidHunters/Marseyloader) or any other launcher that supports Marseyloader patches like [SanabiLauncher](https://github.com/LaCumbiaDelCoronavirus/SanabiLauncher).

## Installation
1. Install the latest version of [MarseyLoader for your operating system](https://github.com/ValidHunters/Marseyloader/releases).
2. Go to the [RobustoClient release page](https://github.com/Gerkada/RobustoClient/releases).
3. Download the latest version of RobustoClient (`.dll`).
4. Place the file in the `Marsey/Mods` folder in the MarseyLoader directory.
5. Launch MarseyLoader, go to the *Plugins* section, and activate RobustoClient.
6. Enjoy!

## Building
If you want to build the client from source for development purposes:

1. Clone the repository:
```bash
git clone --recurse-submodules -j4 [https://github.com/Gerkada/RobustoClient.git](https://github.com/Gerkada/RobustoClient.git)
```
2. Enter the directory:
```bash
cd RobustoClient
```
3. Build the project in Release mode:
```bash
dotnet build RobustoClient/RobustoClient.csproj -c Release
```
4. After building, your compiled version will be located at `RobustoClient/bin/Release/net10.0/RobustoClient.dll`. Copy it to the MarseyLoader mods folder.

## Key Features

- **Smart AutoChem System (NEW)** – A flawless, state-machine-driven chemistry AI. Uses a Top-Down Float solver (Recursive LCM) and Yield-Skewed Verification to brew complex multi-stage recipes without overflowing beakers or wasting reagents.
- **Smart AutoReload System (NEW)** – A state-machine-driven weapon reload AI. Features automatic ammunition detection (magazines, loose shells, and boxes), support for two-handed wielding mechanics, and intelligent chamber monitoring to prevent unnecessary bolt racking.
- **Upgraded Aimbot (Melee & Ranged)** – Now features advanced relative velocity (`relVel`) prediction and optimized O(1) weapon resolution via `SharedHandsSystem` for perfect tracking.
- **Advanced Syndicate Detector** – Bypasses basic PVS restrictions. Analyzes `ContrabandComponent.Severity` to identify actual threats while ignoring sponsor items or civilian contraband.
- **Friend-or-Foe System (Robusta Friend)** – Aimbot recognizes friends and enemies, focusing only on opponents. Add your friends by username.
- **ClickGui** – A sleek user interface for toggling cheat functions, activated by F4.
- **Anti-Slip** – Protection from slipping on soap, water, and other surfaces.
- **Auto-Spin & Auto-Zoom** – Continuous spinning effect and optimal FOV/Zoom adjustments.

## Additional Features

- **Admin Menu** – Access to advanced administrative settings and panels.
- **Health Bars** – Displays real-time health levels of other players.
- **Chemical Solution Scanner** – Instantly analyze chemical solutions inside any beaker or machine.
- **Security Icons** – Overlay icons for simplified interaction with security systems.
- **Command Permissions Patch** – Patch for working with local command privileges.
- **FOV Toggler** – Bindable key to quickly toggle Field of View.

## Contributions
I welcome any contributions to the client. If you have ideas for improvements, new features, or find a bug, please open a ticket in the Issues section on GitHub.

## License
This project is licensed under the [GPL-3.0 License](LICENSE).
