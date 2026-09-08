# Fixed-trace capacity and consumer causality

Frozen before running the replay. Baseline is published main
`573c873e672cbb5d6216efd8bc465d4904662a12`. This round owns only index tools,
index package guidance and related documentation. No production candidate is
promoted by a software replay.

Source review identifies two mechanisms. Both incremental shaders reserve
zero words for an empty cell at rebuild. DetectChanges appends reservations
before removals and does not reuse tombstones; any exhausted destination makes
the entire index rebuild. CellSerial walks each cell's complete reserved range;
BatchedPointScanWave launches to its configured entry capacity and processes
the extent, rejecting Invalid IDs. Reserved layout is therefore visible to both
consumers. Replacing it with compact CSR needs maintained live counts, a prefix
scan, allocation and scattering; the previous full-index control paid for a
complete rebuild and was not an isolated holes intervention.

Run one deterministic CPU mechanism audit on the original hotspot and streaming
trajectories: N262144, seed927101, 384 frames, 64 excluded warmup frames. Compile
the unchanged production fixture, trace, sample contracts and CPU oracle sources.
Replay registration/unregistration at the original fixed frames using the exact
ContentSample generator; verify all nine query digests and four metadata words
against the previous independent oracle on all frames. This replay does not
load bundles, render or measure performance; the original real-loading evidence
remains the GPU reference. Record that distinction explicitly.

Model the published append-only reservation state, including pre-removal
reservations, capacity, fragmentation and churn decisions. Compare all 16 state
words and CSR extent/live/Invalid counts to the retained phase-off GPU histories.
Any mismatch stops the analysis. Classify overflowing reservations and distinct
destination cells by zero versus nonzero capacity, plus insertion multiplicity.
Aggregation by cell is independent of atomic insertion ordering.

The sole candidate hypothesis changes the empty-cell reserve from zero to one.
Keep occupied-cell reserves and all thresholds/paths unchanged. Replay its own
state through the whole trace; never apply its decisions to baseline state.
Validate the exact modeled CSR active ID set, positions and cell membership on
every frame. Report every fallback, inserted/removed/physical-hole count, extent,
and query range work. At this fixed N=BinCount, the bound is BinCount+2*N=3*N,
so the candidate fits the existing allocation; this is not a general-capacity
API proof. No adaptive policy, second reserve size, or compact candidate search.

Advance to a separately frozen GPU diagnostic only if the candidate avoids at
least half of streaming steady-state capacity rebuilds and does not enlarge
steady-state total logical consumer range visits in either target trajectory.
These are engineering screening gates, not statistical performance gates.
Otherwise retain the opt-in API and document rejection without a new GPU or
formal confirmation matrix. A possible performance result would additionally
require the parent's frozen five-process paired CI/CV/drift/p95 criteria, with
complete selection/maintenance/conversion costs and equal native-probe boundaries.

Record fixture/input/oracle/retained-history/source hashes, a fresh output folder,
command/runtime identity and all failures. All compilation and execution use
the shared Local\\CodexR9700VNextUnityGpu mutex once. No phase scopes, native
plugin changes, formal CPU timings or hardware-counter claims are involved.
