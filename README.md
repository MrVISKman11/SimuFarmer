# SimuFarmer

A dedicated, lightweight, high-performance Simulacrum farming plugin for **ExileApi** (Path of Exile 3.25+ / Settlers of Kalguur / Standard).

`SimuFarmer` is purpose-built to run Simulacrums end-to-end with zero bloated dependencies, providing reliable combat circuiting, intelligent ground loot collection, hideout automation, and obstacle jumping.

---

## Features

- **Autonomous End-to-End Farming Loop**:
  - **Hideout Automation**: Automatically navigates to Stash, dumps loot using `Ctrl+Shift+Click` (bypassing full/affinity tabs and protecting Simulacrums), walks to Map Device, inserts a Simulacrum, activates it, and enters the portal.
  - **Arena Initialization**: Grace period breaking and initial aura activation (`Space`).
  - **Wave Triggering**: Walks in range and starts waves via the Afflictionator monolith.
  - **Combat Circuiting**: Continuously holds combat skill (`E`) and Left Click while dynamically following recorded arena circuits with configurable jitter.
  - **Movement Skill Unstuck (`R`)**: Detects wall/balustrade blockages (e.g. Sarn circular gazebo) and automatically casts movement skill (`Flame Dash`, `Leap Slam`, `Dash`) towards destination to jump over obstacles.
  - **Settle & Ground Looting**: Waits for wave rewards to drop, gathers at the configured loot center, and reliably collects ground items with UI-boundary collision prevention and stuck-label recovery (`Z` double-tap).
  - **Town Portal Exit**: Upon completing Wave 15, casts town portal (`T`), clicks it, and returns to hideout to repeat.

- **Pre-Configured Arena Profiles**:
  Includes calibrated Monolith positions, Stash locations, Loot Centers, and circuit loops for all 5 Simulacrum arenas:
  - *Lunacy's Watch*
  - *Oriath Delusion*
  - *The Bridge Enraptured*
  - *The Syndrome Encampment* (with automatic balustrade jump-crossing)
  - *Hysteriagate*

- **UI & Movement Safety**:
  - Ground movement clicks are strictly confined to a safe open-ground radius around the character's feet, completely preventing clicks on Life/Mana globes, Flasks, Delirium rewards bar, Skill bar, Buff icons, or HUD overlays.
  - Collapsible and hideable in-game ImGui HUD with click-through support during active farming.
  - Built-in live circuit recorder and position calibrator.

---

## Keybinds & Default Configuration

| Key | Function |
| --- | --- |
| **`F9`** | Toggle Bot Active / Paused |
| **`F12`** | Reload ExileApi Plugins |
| **`E`** | Combat Skill (Held continuously during waves) |
| **`R`** | Movement Skill (Flame Dash / Leap Slam / Dash to jump obstacles) |
| **`Space`** | Initial Aura / Buff Key |
| **`T`** | Town Portal Key (Wave 15 completion) |

All keybinds, delays, jitter radius, and stash tab indices can be adjusted directly in the ExileApi Plugin Settings menu.

---

## Installation

1. Clone or copy this repository into your ExileApi directory:
   ```bash
   git clone https://github.com/<your-username>/SimuFarmer.git "Plugins/Source/SimuFarmer"
   ```
2. Build the plugin in Release mode:
   ```bash
   dotnet build "Plugins/Source/SimuFarmer/SimuFarmer.csproj" -c Release
   ```
3. Copy the compiled DLL to `Plugins/Compiled/SimuFarmer/SimuFarmer.dll` (or let ExileApi compile it on startup).
4. Launch `Loader.exe`, enable **SimuFarmer** in the menu, and press **`F9`** in your hideout or inside a Simulacrum.
