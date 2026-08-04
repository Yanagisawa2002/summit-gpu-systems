# Migration manifest

## Source snapshots

The standalone repository was assembled on 2026-08-04 from validated, clean SUMMIT worktrees:

- Primary integrated GPU/cinematic snapshot: `codex/gpu-cinematic-hero-benchmark` at `732bc37ffbcf13d33d69db27da195608440872c9`.
- Final adaptive spatial backend overlay: `codex/gpu-adaptive-spatial-backend` at `a881f5bec7f099a4d581ca5fe534c5d0b043dc7e`.

The adaptive overlay is explicit because that completed worktree was intentionally independent and was not an ancestor of the cinematic branch.

## Included

- Eight `com.summit.gpu-*` UPM packages, including source, compute shaders, package tests, READMEs, Unity metadata, and the D3D12 timestamp native source/binary.
- Six procedural benchmark hosts under `Assets/Gpu*Benchmark`.
- Generic benchmark runners, summarizers, provenance checks, autotuning utilities, and native build automation.
- GPU engineering plans, reports, portfolio notes, and the spatial-index report artifact.
- Project-authored NYCGIS/BFP2 GPU integration code under `Integrations/NYCGIS`.
- Small structured sensor benchmark CSVs used by the NYCGIS case study.

## Excluded

- City geometry, maps, point clouds, orthophotos, texture arrays, facade images, vegetation source assets, BFP2 payloads, scenes, and generated Unity assets.
- Unity `Library`, `Temp`, `Logs`, `UserSettings`, generated players, build outputs, RGP/RGA/Pix captures, screenshots, and raw multi-hundred-megabyte benchmark sessions.
- Third-party packages and source code not authored as part of the GPU work.
- Unrelated SUMMIT gameplay, networking, UI, weather, mission, and GIS implementation.

The original SUMMIT repository and worktrees remain unchanged as migration sources. This repository is the maintenance destination for subsequent GPU systems work; consumer migration is a separate change.
