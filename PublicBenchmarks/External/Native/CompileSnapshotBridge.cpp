#include "ArborXSnapshotBridge.hpp"
// Compile instantiates the adapter without executing it. The values here are
// type-checking fixtures, never a benchmark or replacement upstream input.
void compile_snapshot_bridge(std::ostream& output)
{
    summit_external::write_arborx_snapshot(output,0,0,0,
        [](std::uint32_t){return summit_external::point{{0,0,0},0};},
        [](std::uint32_t){return std::array<float,4>{0,0,0,1};},
        [](std::uint32_t){return std::uint32_t{0};},
        [](std::uint32_t){return std::uint32_t{0};});
}
