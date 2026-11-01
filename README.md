# RobustoClient

[](https://discord.gg/XkujzBJSkz)

Based on the original [ArabicaClient](https://github.com/noverd/ArabicaCliento) by noverd. Massive thanks for the foundational framework.

RobustoClient is a heavily modified, advanced open-source cheat client for Space Station 14. It features a completely rewritten architecture for critical systems, including a mathematical AutoChem solver, an automated construction and wire-hacking engine, precise combat prediction, and advanced entity detection.

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
git clone --recurse-submodules -j4 https://github.com/Gerkada/RobustoClient.git
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

- **Smart Auto-Hacking & Wire Panel Solver (NEW)** – An intent-driven wire discovery and hacking state machine (`RobustaWiresSolver` & `RobustaHackingSystem`).
  - **One-Key Airlock & Vending Machine Hacking (`Shift + H`)**: Point at any door or vending machine and press `Shift + H` to automatically open the maintenance panel, probe unknown wires by analyzing status light deltas, unbolt locked airlocks (`Mend` + `Pulse`), cut `Log`, `AI Interact`, and `Power` wires, and pry the door open—or cut `Access`, `Manager`, and `Log` wires on vending machines.
  - **High-Security Shielding Removal**: Automatically detects reinforced and high-security airlock shielding (`WiresPanelSecurityComponent`) and delegates multi-step construction graph traversal (`maxSecurity` -> `highSecurity` -> `airlock`) to `RobustaAutoConstructSystem` before hacking the internal wires.
  - **Station-Wide Wire Layout Caching**: Memorizes discovered wire layouts (`Color` + `Letter` -> `WireFunction`) per prototype Group ID so subsequent airlocks and machines of the same type are hacked immediately without exploratory probing.
- **Smart AutoConstruct & Blueprint System (NEW)** – A BFS construction graph solver (`RobustaConstructionSolver`) and state-machine builder/deconstructor (`RobustaAutoConstructSystem`).
  - **Context-Aware Build & Deconstruct (`V` / `N`)**: Short-tap `V` to automatically build any construction ghost or unfinished frame under your cursor; double-tap `V`, hold `V`, or press `N` to deconstruct structures step-by-step (automatically invoking the wire solver on airlocks to unbolt and cut all internal wires safely). Fetches required materials, boards, parts, and tools from hands, utility belt, backpack, or nearby tiles, ignites/extinguishes welders, pauses during player movement to prevent interrupted `DoAfter` drops, and returns tools to their original storage slot.
  - **Multi-Tile Blueprints & Multi-Layer Piping**: Export any grid area into a reusable JSON blueprint via a 2-click selection right from the native Construction Menu (`G`), preview and rotate (`R`) multi-tile ghost overlays, and automatically construct complex setups in Z-layer order (`Subfloor` -> `Structure` -> `Fixtures`) with full support for `Primary`, `Secondary`, and `Tertiary` atmospheric pipe layers.
- **Smart AutoChem System** – A state-machine-driven chemistry AI. Uses a Top-Down Float solver (Recursive LCM) and Yield-Skewed Verification to brew complex multi-stage recipes without overflowing beakers or wasting reagents.
- **Smart AutoReload System** – A state-machine-driven weapon reload AI. Features automatic ammunition detection (magazines, loose shells, and boxes), support for two-handed wielding mechanics, and intelligent chamber monitoring to prevent unnecessary bolt racking.
- **Upgraded Aimbot (Melee & Ranged)** – Features advanced relative velocity (`relVel`) prediction, Throw Aimbot support, and optimized O(1) weapon resolution via `SharedHandsSystem` for accurate tracking.
- **Advanced Syndicate Detector** – Bypasses basic PVS restrictions. Analyzes `ContrabandComponent.Severity` to identify actual threats while ignoring sponsor items or civilian contraband.
- **Friend-or-Foe System (Robusta Friend)** – Aimbot recognizes friends and enemies, focusing only on opponents. Add your friends by username.
- **ClickGui & Dynamic Multi-Key Keybinds (`F4`)** – A sleek interface for toggling cheat functions and customizing keybinds on the fly, with full support for modifier combinations (`Shift`/`Ctrl`/`Alt` + Key) and persistent `config.json` storage.
- **Anti-Slip** – Protection from slipping on soap, water, and other surfaces.
- **Auto-Spin & Auto-Zoom** – Continuous spinning effect and optimal FOV/Zoom adjustments.

## Additional Features

- **Admin Menu** – Access to advanced administrative settings and panels.
- **Health Bars** – Displays real-time health levels of other players.
- **Chemical Solution Scanner** – Instantly analyze chemical solutions inside any beaker or machine.
- **Security Icons** – Overlay icons for simplified interaction with security systems.
- **Command Permissions Patch** – Patch for working with local command privileges.
- **FOV Toggler** – Bindable key to quickly toggle Field of View.

## Community & Support
Need help with installation, want to report a bug, or just chat with other users?  
**[Join the official Discord server!](https://discord.gg/XkujzBJSkz)**

## Contributions
I welcome any contributions to the client. If you have ideas for improvements, new features, or find a bug, please open a ticket in the Issues section on GitHub or discuss it in our Discord.

## License
This project is licensed under the [GPL-3.0 License](https://www.google.com/search?q=LICENSE).
