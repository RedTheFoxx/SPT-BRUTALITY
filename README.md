# Brutality : A Tushonka Biomechanical Ragdolls modification

[![Brutality Formal Proofs](https://github.com/RedTheFoxx/SPT-BRUTALITY/actions/workflows/verify.yml/badge.svg)](https://github.com/RedTheFoxx/SPT-BRUTALITY/actions/workflows/verify.yml)

Client-side BepInEx plugin for **SPT 4.1.x** that replaces Tarkov's fixed 150 N·s puppet ragdoll with a real-time biomechanical and neural simulation. At death, the nervous system shuts down according to the injury, muscles lose tone or lock, joints resist extreme ranges of motion, and the body settles within **10 seconds**.

<p align="center">
  <a href="docs/EUPHORIA.md"><img src="assets/euphoria.svg" alt="Euphoria © (NaturalMotion / Delirium)" height="64"></a>
  &nbsp;&nbsp;&nbsp;
  <a href="docs/SCIENCE.md"><img src="assets/physx.svg" alt="NVIDIA PhysX" height="64"></a>
</p>

---

## How It Works

Brutality replaces EFT's death dynamics with three coupled layers running before each `Physics.Simulate` step:

- **Passive Biomechanics (PhysX)** — Per-segment masses and inertias (de Leva 1996) plus carried gear (armor, rig, backpack, helmet). EFT joints are swapped for `ConfigurableJoint`s calibrated to ISB anatomical frames and soft exponential limits, with biarticular coupling and ground friction.
- **Hill-Type Muscle Drives** — Torque-angle-velocity actuators with 15 ms / 50 ms activation dynamics, driven as implicit PhysX acceleration drives for jitter-free stability. Destroyed limbs go limp; spinal cord hits cause localized paralysis.
- **Euphoria © Falls & Agony** — A port of [Delirium](https://github.com/PVCPC/delirium-physics) (NaturalMotion Euphoria © behaviors such as `staggerFall`, `shotFallToKnees`, `balancerCollisionsReaction`, `highFall`, `onFire`, `armsWindmill`, `grab`, and `rollDownStairs`) paired with weapon-calibrated hit physics (`NmWeaponClass`), injury-mechanism timelines (brain, spinal, vascular, thoracic, blast, melee, etc.), anti-repetition memory, and real ballistic momentum (`p = m·v`).

---

## Installation

100% **client-side** (no server mod required). With **Fika**, install on the host and ideally on clients.

- **Manual (Release):** Extract `SPT-BRUTALITY-1.1.2.zip` into your SPT root so the DLL sits at `BepInEx/plugins/Brutality/Brutality.dll`.
- **Build from source:**
  ```bash
  dotnet build src/Brutality/Brutality.csproj -c Release -p:Install=true
  ```
  *(Pass `-p:SptGame=/path/to/SPT` for a custom path, or `-p:Package=true` to generate `dist/SPT-BRUTALITY-1.1.2.zip`.)*

**Configuration:** `BepInEx/config/com.brutality.ragdoll.cfg` — editable in-game via `F12` (ConfigurationManager).

---

## Mod Compatibility

| Mod | Status | Notes |
|---|---|---|
| **Smoothie** | Compatible | Early corpse freeze is neutralized; 60 Hz cap is respected. |
| **Fika** | Compatible | Host simulates death; clients animate locally and sync final pose. |
| **ReClassic** | Compatible | Unconscious bodies that die are taken over by Brutality. |
| **HollywoodFX** | Partial | Keep *Enable Ragdoll Effects* and *Enable Cinematic Ragdolls* **disabled**; blood/decals remain. |
| **Rupture** | Incompatible | Both mods drive ragdolls — disable Rupture if installed. |

---

## Documentation & Verification

| Resource | Description |
|---|---|
| [`docs/SCIENCE.md`](docs/SCIENCE.md) | Mapping from the [scientific corpus](Scientific%20Corpus%20%E2%80%94%20Biomechanics%20for%20an%20Ultra-Realistic%20Ragdoll.md) to the C# implementation |
| [`docs/EUPHORIA.md`](docs/EUPHORIA.md) | Delirium / Euphoria © behavior port architecture |
| [`docs/VERIFICATION.md`](docs/VERIFICATION.md) | Formal proof sheet (**91 / 91** off-game checks, CI-verified) |
| [`demo/test-bench.html`](demo/test-bench.html) | Interactive 3D demo studio |

Run the standalone off-game test benches (no game DLLs required):

```bash
dotnet run --project tests/Brutality.KinematicsTests
dotnet run --project tests/Brutality.DeliriumTests
```

---

## License & Trademarks

- **Code & Original Assets:** Everything original in this repository is licensed under the [MIT License](LICENSE).
- **Trademarks:**
  - **Euphoria** is a trademark and property of **NaturalMotion**.
  - **Escape from Tarkov** / **Tarkov** is a trademark and property of **Battlestate Games**.
  - Any other trademarks, logos, or brand names appearing in this project (such as NVIDIA PhysX, Unity, BepInEx, SPT, etc.) are the property of their respective owners.
- This project is an independent mod and is not affiliated with, endorsed by, or sponsored by NaturalMotion or Battlestate Games.
