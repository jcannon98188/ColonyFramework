---
status: accepted
---
# v1 is CPU-only; GPU compute is a later optional package

Prototypes showed GPU gains only for dense GPU-authoritative Fields (about 15x) and shared-goal pathfinding (about 2x); a CPU-authoritative mirror never won (PCIe-bound), per-unit GPU path queries lost 17-20x to CPU HPA*, and float Kernels differed up to 5 ULP between Vulkan and DX12. That did not justify bindings, a second Kernel implementation, an authority model, device-loss handling and a test matrix before a real workload needs them, so v1 ships no GPU Backend.

## Considered options

- **GPU as a v1 Backend (wgpu-native, WGSL Kernels with CPU reference):** designed and prototyped, then dropped from v1.
- **Backend interface in the spec without an implementation:** rejected as cost without a user.

## Consequences

Kept as seams: Fields behind an interface with the CPU implementation as oracle, integer/fixed-point formulations, the Exact/Approximate Kernel rule (only Exact Kernels in deterministic Worlds). The research reports and the superseded GPU Backend ticket are the starting point for a future `Colony.Gpu.*` package.
