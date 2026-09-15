# SUMMIT 完整任务实验审阅：Linux + RTX 5090

日期：2026-09-15。状态：**只读审阅；尚不具备直接启动正式实验的条件。**

结论：分子动力学单步任务有真实的结果消费者，任务定义可以保留；当前
四臂草案尚不足以证明合理的最终方案价值。缺少 GPU 驻留的力/更新消费者、
同设备 ArborX CUDA 与合适的常规实现，且全部新 C++/C# 代码尚未编译。
不能直接把当前 `freeze.py` 的 64 进程草案搬到 5090 开跑。

本任务没有登录服务器、配置环境、上传文件或启动任何硬件进程。
依照协调任务最新要求，保留草稿并暂停构建、安装、实验；不写虚假的终态交棒。

## 1. 目前真正完成了什么

| 项目 | 已有证据 | 能支持的结论 |
| --- | --- | --- |
| 公开源版本与隔离 | 本地 HEAD/main `b8d63bff657bae54eaca09864f92fb065becb30c`；工作分支 `codex/summit-whole-task-20260915` | 新工作位于独立公开仓库，未修改公司 SUMMIT 或旧证据 |
| 旧调用方阅读 | `PublicBenchmarks/External/Actual/ExternalReplayPlayer.cs:121`；`PublicBenchmarks/External/Boids/BoidsSphereConsumer.cs:46`、`:105`、`:167` | 旧 replay 是 CSR checksum；Boids 是额外 Entity 观察功能，不是原版 flocking 的输入 |
| 原始应用源码与提取 | `Docs/whole-task-md-20260915/sources/example_molecular_dynamics.cpp`；`Artifacts/whole-task-md-20260915/generated-preflight/extraction.json` | 已核对源码 SHA，并成功提取原始初始化、力与更新代码块；仅是源代码准备 |
| 静态解析 | 本轮工具输出：PowerShell 源码解析与 Python AST 解析通过；提取 JSON 记录生成头文件 SHA | 没有语法级 Python/PowerShell 解析错误；不证明 C++、C#、HLSL 或运行行为正确 |
| 本轮实际实验 | `Artifacts/whole-task-md-20260915` 当前只有 `generated-preflight/` | **构建 0；数值正确性实验 0；GPU 诊断 0；正式性能进程 0** |

原始源码 SHA256：`fc292b286270367980fbb1cc99a4944230138b064d4075c264472b7690828b16`。
提取头文件 SHA256：`840eca5957eca7087af3bd684fc4de7fca66d42302139d753b53ba07f5c24708`。
没有输入 `.bin`、参考 `.csr/.state`、新 Player/native executable、正式冻结或统计结果。

`Docs/SPHERE_REUSE_RESULTS_2026-09-10.md` 和
`Docs/EXTERNAL_ACTUAL_RESULTS_2026-09-10.md` 保留历史 R9700/Serial replay 证据。
其中 44 项 GPU API 检查、完整 CSR 验证及 784.168→135.981/43.748 ms 是历史报告，
本轮没有重跑或逐个重新核验旧原始制品，**不能视作 MD/5090 已通过**。

## 2. 最小完整任务与最终输出

来源是固定 ArborX `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90` 的
[molecular_dynamics 示例](https://github.com/arborx/ArborX/blob/375875dfb6b2e7631b1ba599cd26ee5c1e68ab90/examples/molecular_dynamics/example_molecular_dynamics.cpp)。
本地保留源文件的有效位置：初始化 66–117 行，邻域查询 120–126 行，力 128–165 行，
速度/位置更新 196–207 行；能量诊断的赋值在第 190 行。

完整依赖链：**同一 CPU 粒子/速度快照 → 邻域搜索 → 完整成员消费计算力 →
更新速度 → 更新位置 → 后端完成**。默认初始化保持 10³×4=4,000 粒子、间距 1.7、
原 XorShift64 seed 5374857、半径 3、按原始 ID 排除自身、float 运算、质量 1、dt 0.005。
必须共用同一份规范化初始化快照；不能让各 GPU 后端各自并行生成不同随机速度。

最终输出是全部力、更新后速度和位置，在该后端内存中完成并可供下一步使用。
这是**快照到单步状态的 API 边界**，不包含文件解码或共有的初始化生产者，也不是
CPU-return 延迟、长期模拟或生产物理精度。另报 owner 分配与第一次使用的连续时长；
每次任务均重建当前点集索引，不把不变初始快照冒充可无限复用的运动索引。

原生示例直接在执行空间内用 CSR 计算力，**没有完整 CPU CSR 的必然需求**。
当前 `MolecularPlayer.cs:195` 调用的是 CPU `State.Consume`，因此 legacy/reuse 的
必要回读必须保留在其成本内；它们目前没有检验 GPU 消费融合的价值。
GPU 候选应在 GPU 上消费邻居并更新状态；完整验证回读放在计时之外，并单独记录成本。
如果最终选择要求 CPU 状态的应用，需另定义 CPU-return 边界，让所有方案承担所需回读，
不能把两种输出边界的数据混在一起。

原例额外计算的 `potential_energy` 没有被读取，而且循环中使用赋值而非累加。
本轮选取力到状态的最小依赖片段，一致排除该未用诊断；不修写上游源，不声称完整原 main
或能量守恒已被验证。此范围需要在最终标题和结果中继续明确。

## 3. 必补实验，而非重复目标

### A. 先补能决定是否值得继续的对照

| 对照/候选 | 当前情况 | 正式对照前缺什么 |
| --- | --- | --- |
| 原始 ArborX BVH + 原力/更新 | 提取头文件硬编码 `Kokkos::Serial`（`extract_upstream.py:37`）；未构建 | 先完成 Serial oracle；Linux 上补合理 CPU 并行预算与 CUDA 后端。CUDA 不是改一行类型：输入/验证应使用 host mirror 与显式 deep_copy/fence |
| 常规 CPU cell-list + 同力/更新 | `Native/main.cpp:39` 的两遍 CSR 草案；单线程 | 编译、完整正确性、资源预算与相称的 CPU 优化；Serial 与预算内并行分别呈现，不能把 208 个可见逻辑 CPU 当独占资源 |
| 当前 SUMMIT reuse → CPU 消费 | 未编译的 Unity 草案；128 查询/批，仍同步回读 | 在实际支持的后端完成验证；至少一次分段诊断。仅与旧 rebuild 比较不足以证明最终竞争力 |
| SUMMIT GPU 驻留消费候选 | **未实现** | 查询结果在 GPU 上真正用于力和状态更新；受限批次或流式融合，保持输出成员和精度，避免 `N*Q` 全任务最坏分配 |
| 同设备常规 GPU 实现 | **没有** | 至少一个适用的 tiled all-pairs neighbor+force 或普通 GPU cell-list 路径；不要故意让常规方案做无用 CPU CSR 往返 |
| ArborX CUDA 完整单步 | **没有** | 同一 5090、同一输入与输出边界、相同精度契约，计入必要 host/device 准备及完成；这是外部 GPU 主对照 |

优先级最高的实现选择是：保留 Unity/HLSL 并迁到 Vulkan，还是限定为新的原生后端
评估。后者的结果不能替代现有 Unity/D3D12 实现的验收。若 CPU 方案足够好，允许据
同机完整任务证据推荐 CPU；没有必要为使用租来的 GPU 强行移植全部路径。

### B. 正确性与规模

1. 原始 4,000 粒子必须保留。单独发现集 2,048；确认扩展为 864、32,000、
   4,000 点轻微非平衡扰动。扰动与扩展明确标注，不改名为原始应用数据。
2. 全部 offsets、逐行完整 ID 多重集、自碰撞排除；全部 force/velocity/position 分量。
   小/原始规模 brute-force oracle，较大规模独立 cell-list 与 ArborX 交叉验证。
3. 边界夹具：精确半径、相邻 float、负坐标、不同 ID 的重合坐标；重合点只验成员，
   原力律零距离奇异，不拿非有限力结果当通过。再补无邻居、非整批尾部和状态替换。
4. 草稿容差（位置 abs 2e-5/rel 2e-6；速度 2e-5/2e-5；力 2e-3/2e-5）尚未运行验证。
   先用小例和独立计算确认适用性，再冻结；不根据快方案的失败事后放宽。
5. 对新增 GPU owner/API 必须验证未提交/丢弃命令、重复或换代查询、错误路径和完成后释放。
   主分支历史检查不能自动覆盖新 GPU 消费接口或 Vulkan/CUDA 后端。

### C. 分段与最终成本

单独诊断 CPU 编码/验证/分配/上传、CPU 录制与提交、设备 index/query/force/update、
完成等待、实际需要的回读、转换与聚合。设备执行与 host wait 存在重叠，不能相加；
post-fence readback 也不是纯 PCIe 时间。历史 125.82 ms 不能被新机器诊断追溯拆分。

完整任务主时钟包括每方实际必要的准备、传输、查询、消费和完成。
另报首次使用、稳态、host/VRAM 峰值与显式容量；不把 buffer 容量当内存峰值计数。
设备 timing/profiler 与无侵入正式计时分开，不能用 CPU 提交间隔冒充 GPU 完成。

### D. 当前草稿需要修补的验收问题

* `freeze.py:28` 之后对目录直接 `rglob`：缺少必需输入、Player、native 可执行文件的
  存在/完整性断言。目录不存在可能只产生空列表。正式 freeze 必须拒绝这种状态。
* 冻结了源码与二进制还不够：当前没有校验构建时源码清单、实际 staged source 与
  当前源码完全一致。需把构建前后的源哈希、编译命令和二进制绑定起来。
* 当前统计草案是每臂 4 个独立进程、每进程 3 warmup+5 measured，尚未冻结或执行。
  应根据独立发现阶段确定足够长的采样量，再冻结；不能把五次短任务当作已覆盖环境漂移。
  主任务建议 6 个平衡独立进程区组，扩展至少 4 个；内部重复数不是独立 n。
* 新后台采样器使用 Win32/PDH，并且尚未运行检验。Linux 必须另行验证指标与采样范围；
  缺失指标保持 unavailable。不能把容器外 208 核的低总体百分比当作 25 核额度的空闲。
* 必须预先冻结噪声/漂移标准、测前和测中后台负载、进程顺序与失败规则。已写入草稿的
  5 秒静态窗口和 250 ms 采样只是初稿；Linux 上要结合 cgroup throttling、其他 GPU
  进程与实际短任务时段验证覆盖。任何失稳保留全组并标不具性能资格，不挑选好样本。

## 4. 新服务器能承接什么

以下服务器情况来自**协调任务已完成的只读核验**，本任务没有再次登录核实：
Ubuntu 22.04.5 Docker、RTX 5090 32607 MiB、驱动 580.76.05、当前 GPU 空闲；
CPU 时间额度 25 核，cpuset 0–207；RAM 上限 90 GiB，系统盘 30 GiB、数据盘 50 GiB。
有 NVIDIA Vulkan ICD 与 GLX/EGL 驱动库，尚无 DISPLAY/X 服务或图形能力测试。
GCC 11.4/CMake 3.22.1 在 PATH；dotnet/dxc/nvcc/Unity 未在 PATH 找到，不等于全机不存在。

| 部分 | Linux/5090 判断 | 最小变更与结论限制 |
| --- | --- | --- |
| Native Serial/grid | 源代码具备移植基础；尚未编译 | GCC/CMake + 固定 ArborX/Kokkos；替换 Windows launcher，明确 GCC float/FP-contraction 选项。原有 `/fp:strict` 只在 MSVC 分支设置 |
| ArborX CUDA | 合理且必要的同卡外部基线，尚未实现 | 固定支持 SM120 的 toolkit、Kokkos CUDA 与数据驻留实现；先小正确性。服务器 driver/ICD 存在不是 CUDA toolkit/编译成功证明 |
| 当前 Unity Player | **Windows .exe/D3D12 不能直接当 Linux 原后端运行** | `BuildMolecularPlayer.cs:20–26` 固定 Windows/D3D12。若保留 Unity，需 Linux Player/Vulkan 构建支持、授权和可用图形上下文的最小验证；有 ICD 不能证明无显示环境能启动该 Player |
| SUMMIT HLSL/index/adapter | 可调查 Unity/Vulkan；也可做新原生 Vulkan/CUDA 移植 | 后者涉及 host buffer/descriptor、barrier、提交/同步、原子及精确 predicate 的重新验证，属于新后端结果；不是只换 GPU，也不能宣称复现了旧 D3D12 时间 |
| 设备诊断 | 当前插件不支持 Linux | `GpuTimestampSession.cs:91–104` 拒绝非 Windows/非 D3D12。Vulkan 时间戳或 CUDA event/合适 profiler 需新校验；不可用时保留 unavailable，不安装/修改驱动来绕过 |
| 运行与环境监测 | 当前脚本不具 Linux 可运行性 | `run_process.py:21/73` 与 `background.py:34–35` 依赖 kernel32/PDH/STARTUPINFO；替换为 Linux 进程与 cgroup/NVML 观察，验证容器 PID 对应及指标权限 |

RTX 5090 是 CC 12.0；固定 Kokkos 源的 `kokkos_arch.cmake` 已有 `BLACKWELL120`。
CUDA 12.8 release notes 明确包含 SM_120 编译支持，因而可作为候选最低 toolkit 版本；
实际编译器/驱动/容器组合仍需审阅后的能力验证。依据：
[NVIDIA GPU 表](https://developer.nvidia.com/cuda/gpus)、
[固定 Kokkos 架构定义](https://github.com/kokkos/kokkos/blob/6739bc623081648af9e752b616d9671527922cbf/cmake/kokkos_arch.cmake#L96-L97)、
[CUDA 12.8 官方发行说明](https://docs.nvidia.com/cuda/archive/12.8.0/cuda-toolkit-release-notes/index.html#new-features)。

CPU 并行预算应固定且不超过 25 核时间额度，给驱动/监测留余量；需保存 cgroup
`cpu.stat` 的限流情况及分配事实。25 核额度不保证 25 个独占物理核。
下载、构建、缓存与证据优先规划在数据盘；先做分项空间预算，避免克隆庞大的 Unity
应用或同时保留多套 SDK。不预设全量 Unity 恢复在当前容量内一定可行。

## 5. 可删减项与建议顺序

**保留：**任务/输出契约、Serial oracle、完整状态验证、合理 CPU 基线、ArborX CUDA、
适用的 GPU 常规实现、单独分段诊断、原始/小/大/扰动确认、全成本与全部失败记录。

**可删减或后置：**旧 50k/20k random replay 重跑、Cabana/Boids 场景、legacy rebuild
在所有规模上的正式计时、广泛参数扫描、多个近似等价 profiler、长时模拟/渲染展示。
旧 legacy 路径留一组正确性与诊断即可解释历史改动；不占用大量正式进程只证明胜过旧代码。

建议审阅后的顺序（本文件不授权或启动执行）：

1. 确认单步 snapshot API 与驻留边界，决定是否保留 Unity 后端。先修冻结/构建身份缺口。
2. 最小原生工具链能力检查 → Serial 完整 oracle 与 CPU conventional 正确性。
3. 同卡 ArborX CUDA + 常规 GPU 小规模完整状态检查；明确选择 GPU 候选是否有必要。
4. 若要验证现有 SUMMIT，实现并验证 GPU 消费接口及 Linux 图形/后端路径；若该移植
   成本不适合当前目标，保留 Windows 原实现的待验项，并将 Linux 原生研究结果单列。
5. 用单独发现集做诊断，确定有限候选和预算；修订并冻结新协议。
6. 在可观测且稳定的资源条件下做独立确认；否则交付有证据的条件不足/NO-GO。

**审阅意见：任务定义可采纳；当前执行草案不通过直接开跑审查。先补消费者、适用基线
与平台/验收缺口，不为已租的 5090 勉强制造移植或性能胜出。**
