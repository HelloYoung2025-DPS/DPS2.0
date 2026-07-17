# DPS 升级总方案（v3）

> 文档状态：`Deprecated`（历史方案，已被 `Docs/RebuildPlan_重构计划书.md` v4 取代；仅作审查历史保留，**不可按本文件施工**。本文件中的 `EMULATOR_VERIFIED`、`governance/KILL_SWITCH`、进度账本等机制均已被 v4 明确否决）
> 基准提交：`458f9bd`（chore: modernization baseline snapshot (WIP)）
> 制定日期：2026-07-16（经三路事实核查修订）
> v3 修订：2026-07-16 苏格拉底审题十九问用户答复（§8）+ 四路实证调研（虚拟化拓扑 / 仓库设备假设 / M3 瘦身 / 桥探针）。核心变化：A9 重释（用户驱动的 AI 升级，非自主流水线）、新增 MV 虚拟化环境里程碑（Windows/真机当前不具备）、M3 瘦身为 M3'、安全网具体化（异构模型复核 + Kill switch 落地 + 计数型停止）
> 推送目标：`dps2`（github.com/HelloYoung2025/DPS2.0）——**不要推送 `origin`（旧仓库）**
> 取代：外部文件 PLAN 1.md（Codex 2026-07 初版蓝图，已执行并被超越，归档不再执行）

本方案基于 2026-07-16 对 PLAN 1.md 的多智能体审核、用户逐条确认的需求、以及对本方案草稿的三路对抗核查编写。发生冲突时，以根 `AGENTS.md`、各模块 `module.yaml` 与可执行门禁为准；本方案的任务清单与验收条款作为施工顺序权威。

---

## 1. 锁定假设（用户 2026-07-16 确认，修改需用户批准）

| # | 假设 | 来源 |
|---|---|---|
| A1 | 规模：2 部安卓设备；设备数量**不是硬指标**，不为规模扩张建设 | 用户十问答复 + 追加确认 |
| A2 | 头号产品目标：**在安卓上运行**；第一阶段 Instagram 只做浏览 / 记忆 / 草稿（零对外写） | 用户确认 |
| A3 | 浏览、记忆、草稿全部保存入 GBrain；**每次行为判断先查 GBrain 再做逻辑判断** | 用户确认 |
| A4 | 判断逻辑归属：**沿用现有决策代码**（RuleEngine 系），只新增"判断前取回记忆"一步 | 用户确认 |
| A5 | GBrain 部署在**独立服务器/NAS**，Mac 与 Windows 均可访问 | 用户确认 |
| A6 | Windows 电脑与安卓真机**当前均不具备**（v3 实况）；开发/校准期以"Windows VM + 安卓模拟器"过渡（见 MV），最终验收仍需真机。ZD 与 Windows 由人类管理；**"不重启 ZD"= 软件不得要求/主动重启**；ZD 自身崩溃 → 安全停摆 + 通知用户，人工恢复后从 UNKNOWN_OUTCOME 对账续跑 | 用户确认 + 2026-07-16 补充 |
| A7 | 设备驱动（ZD/Windows/手机基础设施）由用户管理，本方案不设计设备驱动机制 | 用户确认 |
| A8 | 升级审批：**默认不需要人工审核**；要求可追查、可复盘，事后由 AI 复盘日志 | 用户确认 |
| A9 | **"AI 升级"= 用户驱动的外部 AI 编码会话（Codex/Claude 等）执行升级**；硬需求：模块化升级、改 A 不坏 B、升级前必读根与目标模块 `AGENTS.md`。**不建自主编排流水线**（Host 状态机自动升级不做，见 M3' 瘦身） | 用户核心需求，2026-07-16 澄清 |
| A10 | 首期不做任意热替换、不开放第三方插件；Bridge 稳定运行 30 天后再评估模块级热替换 | 用户十问答复 |
| A11 | 断网行为：继续观察不写外部 + 已批准命令本地暂存；断网期间不产生新的自主副作用 | 用户十问答复 |
| A12 | 只操作自有或明确授权的设备、账号与应用；不做检测规避、虚假互动、垃圾信息 | 根 AGENTS.md（不可修改） |
| A13 | 载体：继续以现有 34 模块架构为载体——其合同/门禁体系正是 A9"改 A 不坏 B"的机器保障；但 factory 自主流水线部分按 M3' 瘦身处置，不为流水线自身建设 | 2026-07-16 Q14 委托裁决 |
| A14 | 通用性：观察/选择器/校准体系必须对**任意 APP 通用**（配置驱动，禁止把 IG 特例写进代码）；Instagram 只是第一个应用 | 用户 2026-07-16 要求 |
| A15 | 记忆读路径 = **软增强**：GBrain 不可达/超时 → 无记忆降级继续浏览，决策日志必记 `MEMORY_MISS`；写路径仍走 outbox 滞留补投（A11） | 用户 2026-07-16 选定 |
| A16 | A8 适用边界 = 零对外写阶段；**进入任何对外发布阶段前必须重新评估 A8**（列入 §6 人工前置清单）。复核形态 = 异构模型交叉复核（DeepSeek/GLM 等非同源模型），复核机制须过"埋错演练"方可上岗 | 2026-07-16 Q8/Q11 |

这些假设应同步固化到 `governance/assumptions.yaml`（M0 任务），任何引入设备数/并发阈值的门禁必须引用该文件。

---

## 2. 当前事实（2026-07-16 实测并经独立核查确认，接手 AI 不得跳过重算）

- HEAD = `458f9bd`，工作区干净，`main` 跟踪 `dps2/main`。双远程并存：`origin`=旧 DPS.git（弃用），`dps2`=现行。
- 正式验证等级 `NONE`；34 个注册模块全部 `releaseEligible=false`。
- **三层断链使 AI 自动升级今天走不通**（均已实测复现）：
  1. 三个治理快照（module-catalog / dependency-graph / compatibility）stale，`generate_governance.py` 校验退出码 1，静态门禁必失败；
  2. 34 份 module.yaml 中 **14 条通信边**引用 quarantine-only 的 v1 合同（分布：factory-control-plane-host×4、factory-worktree-manager×2、evidence-service×2、factory-trusted-runner / factory-rollback-controller / policy-approval / interest-reducer / gbrain-projector / soul-memory-adapter 各 1），`factory-instruction-resolver` 对任何 resolve 调用 fail-closed（`instruction_resolver.py:1910`）；
  3. `Tools/ci/run_candidate_gate.py:1957` 按旧 API 调用 resolver，实测 `TypeError: missing 'trust_authority'`（门禁将其包装为 Phase0Error 抛出）。
- `factory-worktree-manager/src/worktree_manager.py:275-277` 硬编码只收 `module.change.plan/v1` 与 `instruction.receipt/v1`（均已无运行时路径）——收据死锁。
- 记忆链断层：memory-event-ledger 已禁用 v1 append（`PostgresMemoryEventLedger.cs:103-113` 抛 NotSupportedException），但 interest-reducer / gbrain-projector / soul-memory-adapter 仍消费 v1，且三者的 PostgreSQL 集成测试调用已禁用的 v1 `AppendAsync`，运行必失败。
- 本机 Mac 工具链断链：`.venv/bin/python3` 死链（指向 /Users/young/...）、无 python3.12、无 dotnet；phase0 还要求**固定审批路径上的可信 Node 24.18.0**（不止版本号）。
- 全部门禁证据位于被 `.gitignore` 排除的 `Reports/`，git 历史零证据；唯一 PASS 为 unsigned 且绑定旧基线 `cac7ccb`。
- Instagram 选择器零实机验证（`Config/PlatformsConfig.json` Reddit 段有 45 处 "VERIFIED from device dump" 注释，Instagram 段为 0）；`Configs/Manifests/instagram.json:88`（另 :35、:141）用评论按钮选择器 `row_feed_button_comment` 执行"打开帖子"，复制错误确凿。
- `speech.drafted/approved/published/failed` 草稿合同不存在（全仓零命中；技术书 §4.4 自述）。
- F7 外部门被硬编码 `STALE` 阻断（`Tools/verification/external_gate.py:76`，`:6106` 强制 WAIT），需先独立重冻结 projection/source-binding 合同哈希。
- interest-reducer 的 module.yaml 声称拥有 `postgresql:interest.snapshots` 存储，但无迁移、无实现（清单谎言）。
- Windows Edge 自认阻断：windows-edge-supervisor 声明 "Worker launch/runtime ABI and crash-recovery protocol remain unavailable, so production staging and route transitions are disabled"——**桥接通电前必须先冻结 Worker 启动/运行 ABI**。

---

## 3. 总路线：四个里程碑

```text
M0 地基修复（含 M0-P 桥探针）──→ M3' 升级门禁 CI 化（轻）──→ M1 记忆回路 ──→ M2 上岗（先模拟器后真机）──→ 持续运营
MV 虚拟化环境自举 ─────────────（与上全程并行，第 0 天即可启动，产出供 M2 使用）
```

- M0 对应 F0/F1 收尾；**MV 为 v3 新增里程碑**（Windows/真机缺位下的环境自举，不触碰 manifest/合同）；M1 对应 F2 + F7 准备；M2 对应右尺寸化的 F5/F6/F7；**M3' = 升级门禁链 CI 化 + 会话规程（v3 瘦身，原 M3 自主编排流水线不再建设）**，F3/F4 完整流水线与 F8/F9 同列 `DEFERRED`。
- F8（30 台灰度）、F9（200 台规模）标注 `DEFERRED`；**删除触发条件（v3）**：M2 真机验收通过后的首次治理修订中删除其 schema/验证器，期间冻结不维护，若治理快照重生成因其报错则即时删除。**本项目"生产就绪"顶格 = DEVICE_VERIFIED（F7，2 台真机 + 2 Soul）**；v3 新增中间等级 `EMULATOR_VERIFIED`（见 MV/§4）。

**快照串行纪律（适用于任何多分支场景）**：三个治理快照是全局聚合文件，任一 manifest 变更都会触发全量重生成。快照重生成必须**串行**——只在合流点由一次运行执行 `generate_governance.py --write` 并复跑校验至退出码 0；并行分支**禁止各自提交快照**。v3 后 M3' 已瘦身为轻任务，不再作为与 M1 并行的重改动分支；MV 不触碰 manifest/合同，不占串行窗口。

### M0 地基修复（先做，约束所有后续工作）

**大白话**：把工具箱修好、把三处接错的线接对，让"检查代码是否合格"的机器今天就能跑；同时把新规矩（不需要人工审批、证据不能丢、只推 dps2）写进制度文件。

任务（按序）：

1. **环境自举（Mac）**：安装 CPython 3.12.13，删除死链 `.venv`，`DPS_PYTHON=<绝对路径> ./scripts/bootstrap-ci-python.sh` 重建；安装 .NET SDK 10.0.301、PostgreSQL 18.4（`./scripts/start-test-postgres.sh` 可用）；将可信 Node 24.18.0 安置到 phase0 认可的固定路径（先读 `Tools/ci/phase0.py` 的路径审批逻辑再安装，不要装完再猜）。
2. **原子修复批次**（一次提交，不混业务变更）：
   a. `python3.12 Tools/ci/generate_governance.py --write` 重新生成三个治理快照；
   b. 迁移 §2 所列 14 条 quarantine-only v1 通信边到 v2（**只改 manifest 通信声明**；对应运行时代码迁移：记忆链归 M1，Host 归 M3' 的 DEFERRED 三档处置）；顺序按依赖图：先 Host 的 upgrade.intent / instruction.receipt 边，再 worktree/trusted-runner，最后产品侧 memory.event / gbrain.projection 系；
   c. `worktree_manager.py:275-277` 与 trusted-runner 的收据校验迁到 `instruction.receipt/v2` / `module.change.plan/v2` 能力接口；
   d. 重生成快照后复跑校验直至退出码 0。
   > 警告：a 与 b 互为前置（快照由 manifest 生成，manifest 迁移又改变快照），必须作为一个批次反复"迁移→重生成→校验"直到同时绿，不要分两次提交。
3. **修候选门禁 API**：重写 `run_candidate_gate.py` 工厂绑定段——经 `UpgradeIntentTrustAuthority` 构造 sealed `VerifiedUpgradeIntentV2`，按 resolver v0.4.0 签名调用；`FACTORY_RECEIPT_SCHEMA_PATH`/`UPGRADE_INTENT_SCHEMA_PATH` 指到 v2。
4. **跑门禁**：先 `--diagnostic-workspace` 修到全绿，再在干净提交上跑正式 Phase 0，取得首个绑定当前 HEAD 的 `REPOSITORY_STATIC_VERIFIED` 证据。
5. **治理修订**（各自独立小提交；risk-policy 修订属治理变更，**必须由与实现者不同的 AI 会话/角色复核批准后合入，不得在同一次运行内自我放行**——根 AGENTS.md "治理变更不得自我批准"条款仍然有效）：
   - 新建 `governance/assumptions.yaml`（§1 全表）；
   - 修订 `governance/policies/risk-policy.yaml`：R2/R3 的人工批准替换为"自动放行 + **异构模型交叉复核**（DeepSeek/GLM 等非同源模型，须先过 M3'-4 埋错演练方可上岗）+ append-only 审计 + Kill switch（§6 已具体化）"，证据中如实标注 `HUMAN_APPROVAL_SUBSTITUTED_BY_AI_REVIEW`；同步在技术书 §11 与 `Docs/Operations/RepositoryProtection_仓库保护.md` 加注；
   - **初始授权锚点（v3 新增，审计链根）**：将用户 2026-07-16 十九问答复（本文件 §8）连同本文件 v3 的 SHA-256 作为第一条锚点写入 `governance/evidence-anchors/`——整条 append-only 审计链的根由此指向一次真实的人类批准，此后 A8 下不再需要人工签署；
   - F8/F9 在 `governance/verification/README.md` 与技术书 §12/§13 标注 `DEFERRED（规模扩张前不适用）`；
   - **证据保全（锚点方案）**：不把原始证据 JSON 堆进 git——每次正式门禁运行后，将 `{run-id, 证据文件 SHA-256, 绑定 commit, 结论}` 追加到受跟踪的 `governance/evidence-anchors/`（append-only，只增不改）；原始 JSON 留 `Reports/` 并另做仓库外备份；M3 通电 factory-evidence-ledger 后，账本成为权威证据存储，锚点指向账本事件；
   - 修正 interest-reducer manifest：移除未实现的 `postgresql:interest.snapshots` 存储声明（或降为 planned），消除清单谎言；
   - 锁定推送目标：文档与 CI 中明确只使用 `dps2`，考虑 `git remote rename origin legacy-origin` 防误推。
6. **M0-P 桥可行性探针（v3 新增并行工作包；不触碰 contracts/module.yaml，不占快照串行窗口）**——把全案最高风险（M2-2 桥）的证伪从 M2 提前到 M0：
   - P1【ABI 纸面冻结草案】产出 `Docs/Architecture/EdgeWorkerLaunchABI_Draft.md`（显式标注 non-normative DRAFT）：launch 参数/环境/state-dir 统一约定 + one-use Supervisor 授权 + Job Object 语义 + 崩溃恢复状态机；验收 = 与 supervisor AGENTS.md:30、worker AGENTS.md:33、zenno-bridge 28741 端点三方期望逐条对表**无互斥项**；发现互斥 → 硬停止转用户决策（这正是探针要的早期证伪点）；
   - P2【七项取证成本矩阵】将 M2-2 七项逐项标注取证环境（Mac+PG / Windows / 模拟器 / 真机）与预估工作量，作为附表并入 M2-2；验收 = 七行全填且每行给出证据产出物名称；
   - P3【桥链诊断门禁】M0-4 的 `--diagnostic-workspace` 运行范围显式包含 8 个桥链模块（zenno-bridge、windows-edge-supervisor/worker、edge-local-journal、policy-approval、command-orchestrator、executor-gateway、operation-compiler），缺口清单按 §5 锚点机制留痕，每条缺口标注归属里程碑。
   > 已核实基础：七项中六项"已实现未装配"（身份/审批/lease/幂等/native result/postcondition 均有代码），唯一纯缺失 = Worker 启动/运行 ABI（supervisor 以零参数启动 vs worker 只收 `--production-reconcile --state-dir`，双方明文互斥、约定 WAITING_EXTERNAL 直至共同冻结）。探针只能消除"设计层不可行"；Windows/真机实证仍归 M2（Mac 证据按 worker AGENTS.md:44 只算 simulation evidence）。

**M0 出口标准（显式）**：M0 只保证两件事——静态 Phase 0 绿（REPOSITORY_STATIC_VERIFIED）+ resolver/门禁 API 不再崩。被 M0-2b 前置迁移了 manifest 但代码仍旧口径的模块（记忆链三模块、Host 等），其 candidate contract/integration 门禁**允许暂红**；转绿归属：记忆链三模块 = M1 验收项；Host = M3' 的 DEFERRED 三档处置（暂红若阻塞全仓候选门禁绿，触发三档处置评估，而非判为 M0 失败）。自动执行的 AI 不得把这些暂红误判为 M0 失败。

验收：

```bash
.venv/bin/python --version                          # 3.12.13
.venv/bin/python Tools/ci/generate_governance.py     # 退出码 0
.venv/bin/python Tools/ci/run_phase0_gate.py --diagnostic-workspace   # 全绿
# 干净提交上：
.venv/bin/python Tools/ci/run_phase0_gate.py --base <baseline>        # 签发 REPOSITORY_STATIC_VERIFIED
.venv/bin/python Tools/ci/run_candidate_gate.py --level contract --base <baseline>  # 不再 TypeError（允许暂红项见出口标准）
```

### MV 虚拟化环境自举（v3 新增：硬件缺位下的过渡环境，第 0 天可启动）

**大白话**：现在手头没有 Windows 电脑也没有安卓真机。经调研（官方文档 + 论坛实例 + 仓库实测），唯一在 Apple 芯片 Mac 上走得通的过渡拓扑是：**Mac 本机跑安卓官方模拟器（AVD），Parallels 虚拟机里的 Windows 11 ARM 跑 ZennoDroid Enterprise，两者用网络 ADB 连接**。每一步先用免费/试用版验证再花钱，最先验证最可能失败的一环。

**已核实的关键事实**：

- ZennoDroid Lite/Pro 依赖 MEmu/LDPlayer 且要求硬件虚拟化——Apple 芯片 VM 不支持嵌套虚拟化，**此路死**；Enterprise 版驱动真机/BlueStacks、支持局域网 ADB 扫描（等价 `adb connect ip:5555`）、不要求硬件虚拟化——**唯一活路**（月付 $47，年付折合 $33/月）；
- 官方 FAQ 允许装虚拟机但**排除云平台**（"except cloud platforms"），VPS 路线有许可风险；许可绑定 PC 硬件；
- 仓库侧破坏面小（实测）：28741 回环是 Windows 机内进程间通信、Android 设备不在该链路上；设备身份合同全是 opaque ID（禁存 serial/IMEI），模拟器 serial 不会被 schema 拒绝；选择器体系 resource-id/text/content-desc 与分辨率无关；legacy 测试代码历史上就是按模拟器写的。**ZennoDroid + supervisor/worker + adb server 必须共置同一 Windows 实例**（BridgeLoopbackHost fail-closed 校验双向 loopback），不得跨 VM 拆分。

任务（验证优先，每步失败即停并转用户决策）：

1. 【用户】安装 Parallels（试用版即可）+ Windows 11 ARM；下载 ZennoDroid **14 天 demo** 装进 VM——验证 Prism x64 转译下能启动、demo 激活接受 VM 硬件（**最大单点风险，失败则整条线作废**，改议二手 Windows 小主机 + 真机）；
2. Mac 侧 `sdkmanager` 装 arm64 google_apis_playstore 镜像建 AVD（按目标真机分辨率/DPI 建档，**英文 locale**——PlatformsConfig 的 text/content-desc 选择器全为英文词）；`socat` 把宿主 ADB 端口转发给 VM，VM 内 `adb connect` 验通后在 ZennoDroid Device Manager 用 Search 接入；
3. 验证 ZennoDroid 能 Start 该设备（后台自动安装 io.appium.uiautomator2.server 等 4 个 apk）并取到元素树（Enterprise 官方口径只承诺真机+BlueStacks，AVD 可用性必须实测）；
4. Play 商店装 Instagram、自有账号登录、纯浏览观察风控反应。**如实红线（A12）：不做任何检测规避/指纹伪造**；模拟器登录预期会触发"确认是你本人"类验证；账号风险由用户知情承担，**主力账号不上模拟器**；
5. 治理配套：新增证据等级 `EMULATOR_VERIFIED`（介于 WINDOWS_VERIFIED 与 DEVICE_VERIFIED 之间；模拟器证据不得冒充真机证据，与技术书"Mock、模拟、Windows、真机证据必须明确区分"一致）；修订 zenno-bridge module.yaml:177 canary 措辞（"two non-production phones"）与技术书 §9.4 对应条目；为模拟器环境**显式参数化** CapabilityProbe 的 24h 零掉线/24h soak 阈值（独立阈值，不静默沿用）；VM 内按 CapabilityProbe 精确版本安装 pwsh 7.6.2 与 adb 37.0.0-14910828；
6. 【用户，可选】邮件 support@zennolab.com 确认 Parallels VM 内正式许可激活政策，再决定购买。

MV 验收：ZennoDroid（VM 内）经网络 ADB 稳定驱动 AVD 完成 ≥1 次 30 分钟会话（元素树可取、tap/swipe 可执行——用 demo 工程验证，不触碰 DPS 代码锁）；IG 在 AVD 上可登录可浏览；`app_onboarder` 在 AVD 上完成一次 IG dump 采集。

**定位与边界**：模拟器 = 开发/调试/选择器校准环境（顶格 `EMULATOR_VERIFIED`）；**F7 DEVICE_VERIFIED 仍需真机**——用户须在 M2 真机门前提供 ≥1 台真机（Parallels 支持 USB 直通给 VM，即官方标准用法；第 2 台角色维持 §7 待定）。

### M1 记忆回路（产品核心，A2/A3 的直接实现）

**大白话**：把"手机看到的东西存进 GBrain、判断前先查 GBrain"这条水管修通。管子大部分已造好，要做的是把三节旧口径的管子换成新口径、把 GBrain 装到 NAS 上、再打通 ZD 到记忆链的通道——但 GBrain 的钥匙永远不交给靠近手机的那一层。

任务：

1. **v2 链路闭合**：soul-memory-adapter 迁到 `gbrain.projection/v2` + 新 Source 派生（`dps-` + 28 位十六进制、nonce 0–1023）；interest-reducer 与 gbrain-projector 的输入迁到 `MemoryEventV2`；修复三个下游模块调用已禁用 v1 `AppendAsync` 的集成测试。
2. **v2 运行构造（诚实候选形态）**：memory-event-ledger 的 `CreateProduction` 因等待外部 signer 信任根而 WAITING_EXTERNAL——**不伪造信任根**（候选自签授权根违反 fail-closed 红线）。右尺寸做法：soul-registry 暴露最小 current-resolution capability；记忆链以**明确标注的候选/非生产构造**运行，证据中如实标注 `EXTERNAL_SIGNER_ABSENT`；正式等级如实停留在本地可达级别，未来需要正式生产等级时再引入外部 signer。
3. **outbox 投递器**：outbox_v2 目前只写不投——实现最小轮询投递器（ledger → interest-reducer → gbrain-projector → adapter），断网/GBrain 不可达时事件滞留 outbox，恢复后补投且不重复（A11 的一半）。
4. **草稿合同**：定义 `speech.draft/v1`（drafted → approved → published/failed/discarded 生命周期；本阶段只到 drafted，草稿正文存 GBrain，账本存事件与哈希）；发布验证成功前不得记 `spoken`（用户十问 #9）。
5. **GBrain NAS 部署（硬前置，未过不得解除 F7 STALE）**：
   a. NAS 上将 GBrain 固定到**精确 commit**（当前依赖解析到移动分支 `github:garrytan/gbrain#4ee530f3...`，移动分支不得进入 Release 记录）；
   b. 在 NAS 实例**重跑兼容性探测**（Source 32 字符约束、双 Source 隔离、soft-delete、MCP 能力面，参照 `Docs/Platforms/GBrainCompany_Compatibility.md`）并留证；
   c. 核对 GBrain schema 迁移版本与本机探测基线（version 116）一致，或记录差异影响；
   d. OAuth / Source 隔离按 `Docs/Operations/GBrainCompany_LocalNonProduction_本地非生产.md` 边界配置；`source_bindings` ledger 存 DPS PostgreSQL（既有架构，gbrain-projector 拥有），不依赖 GBrain 侧存活；
   e. 以上全过后重冻结 projection/source-binding 合同哈希，解除 F7 `STALE`。
6. **ZD 记忆通道（不新增直连 GBrain 的模块）**：核查确认 Windows/回环层三模块（zenno-bridge、windows-edge-supervisor、windows-edge-worker）的 permissions.denied 均明确禁止 `gbrain access`——这是刻意红线，**不得**造一个持有 GBrain/PG 凭证的边缘网关。正确形态：
   - 扩展 zenno-bridge 的 exchange 合同，新增三种记忆类 exchange kind：`record_observation` / `query_memories` / `save_draft`（沿用固定端点 127.0.0.1:28741、15s、64KiB 约束）；
   - 请求在 Windows Edge 终结后，由 **modern 侧**组合既有模块完成：写 = memory-event-ledger → gbrain-projector → soul-memory-adapter；读 = soul-memory-adapter（exact readback + 搜索复核）；草稿 = speech.draft 事件 + GBrain 正文；
   - GBrain 凭证只存在于 modern 侧进程；边缘层永远只见 typed exchange。
   - 涉及 zenno-bridge（C#5/net40 字节纪律）与多个 modern 模块的修改，各走各的 AGENTS.md/收据流程。

验收：

- 两个合成 Soul 写入→exact `get_page` 读回→checksum/revision 验证→搜索命中再 exact-read 复核，全链 PASS（在 NAS GBrain 上，非 mock）；
- `speech.draft/v1` schema 校验 + 草稿事件幂等（同一 draft_id 重复提交 no-op）测试 PASS；
- 跨 Soul 泄漏为零；重复 `event_id` 幂等 no-op；
- 断网模拟：GBrain 不可达时事件滞留 outbox，恢复后补投且不重复；
- 三个下游模块的 PostgreSQL 集成测试全绿；
- `run_candidate_gate.py --level integration` 在配好 PostgreSQL 的环境 PASS（M0 暂红项在此转绿）。

### M2 手机上岗（A2 落地：Instagram 浏览/记忆/草稿在真机跑）

**大白话**：在 Windows 装最新版本，用真实机制打开代码里的"锁"（绝不作弊改常量），先在手机上抓一份 Instagram 的真实界面数据校准选择器，然后跑通第一阶段：浏览 → 记下来 → 判断前先查记忆 → 写草稿。

任务：

1. **Windows 环境探测（F6 右尺寸）**：先在 MV 虚拟环境记录 Windows(VM)/ZennoDroid/.NET Framework/最高 C#/DLL 装载/ADB 授权探测结果（技术书 §9.4 清单），真机/最终环境到位后复测；**首期不要求 100 次 A/B 切换与 24h soak**（标 DEFERRED，Bridge 稳定后再做）；"不重启 ZD"约束写入 zenno-bridge 验收，语义按 A6（v3）：软件不得要求/主动重启，ZD 自身崩溃 → 安全停摆 + 通知，人工恢复后从 UNKNOWN_OUTCOME 对账续跑。
2. **最小授权桥（有序子任务，复用既有模块，不重写）**——技术书 §9.1 要求的完整链条是"签名 ABI/BOM、身份、审批、lease、幂等、native result、postcondition"七项，缺一不得翻转遗留常量：
   ① **冻结 Worker 启动/运行 ABI 与崩溃恢复协议**（windows-edge-supervisor/worker 当前自认阻断，这是全桥前置）；
   ② **身份绑定链装配**：device-registry → platform-account-registry → binding（`identity.binding/v1` saga），2 设备 2 Soul 各一条绑定；
   ③ **派单链装配**：operation-compiler → policy-approval（fence/promotion；A8 下审批环节 = 策略自动评估 + AI 复核记录 + 审计落账）→ command-orchestrator（lease + fencing，复用既有 `command_id:lease_id:attempt` 幂等）→ executor-gateway（**native result + 业务后置条件双重成功判定，复用不重写**）；
   ④ **UNKNOWN_OUTCOME 对账回路**：跨 zenno-bridge poll-only 回环的断线/超时/崩溃窗口进对账，禁止盲重试；
   ⑤ **最小签名 ABI/BOM**：桥组件组合以内容寻址摘要 + 本地 BOM 记录固定（signer 缺位如实标注，同 M1-2 原则）；
   ⑥ **最后**才按治理流程翻转 OwnCode 薄壳的 `LEGACY_DISABLE_NEW_COMMANDS`（7 个 `*_OwnCode.cs` 薄壳 + SessionRunner 恒 false 函数 + legacy-runtime-adapter 源码文本断言测试，三道锁一起走流程）——这是对字节保护文件的修改，必须走具名审批修复流程（更新 `legacy-csharp-bytes.v1.json` 的 disposition/approval_ref），禁止在①—⑤未齐时直接改常量；**v3 新增前置：M3'-4 异构复核机制已通电且埋错演练通过**（封堵控制空窗——旧人工控制 M0-5 已拆，新控制必须先于全案最危险动作就位）。
3. **判断前查记忆接线（A3/A4）**：在现有决策代码（RuleEngine / SessionRunner 决策路径）的评分调用点前，插入一步经 zenno-bridge `query_memories` exchange 取回该 Soul 的兴趣/近期记忆作为评分输入；改动保持 C#5 兼容，字节基线走具名审批修复流程。决策逻辑本身不重写（A4）。**v3 补充**：
   - **读路径降级（A15）**：`query_memories` 超时/失败 → 无记忆降级继续浏览（记忆 = 软增强），决策日志记 `MEMORY_MISS`；
   - **对照日志（A3 可证伪性）**：每个决策点同时记录"无记忆基线评分"与"记忆加权后评分"，使"记忆是否改变了判断"可观察；
   - **裁剪与权重设计先行**：64KiB/15s 约束下取哪些记忆、按什么排序裁剪、以什么权重并入既有 hot/activity/relevance 评分体系——作为 M2-3 的显式设计产出（文档先行并入锚点留痕），不得由实现会话隐式发明。
4. **选择器校准（通用工作流，A14）**：校准工作流 = `app_onboarder` dump 采集 → AI 校准 `Config/PlatformsConfig.json` 对应段 → 加 `VERIFIED from device dump` 注释——**对任意 APP 通用（配置驱动，禁止把 IG 特例写进代码）**，Instagram 只是首个应用。v3 起 dump 采集在 MV 模拟器上由 AI 自做（不再阻塞于人工真机会话），真机到位后同账号同版本双端 dump 对比复核；修复 `instagram.json:88/:35/:141` 的 `row_feed_button_comment` 复制错误。**失效检测（运营循环，v3）**：会话中选择器失配率超阈值 → 记 `SELECTOR_STALE` 事件 + 通知用户 + 该 APP 会话 fail-closed 停用；再校准走同一通用工作流（模拟器即可完成，不必等真机）。
5. **Phase-1 闭环**：浏览（滑动 feed、打开帖子）→ `record_observation` → 记忆链 → GBrain；决策前 `query_memories`；生成评论/回复**草稿**（`speech.draft` drafted 态）存 GBrain，不发布。全程零对外写。
6. **断网另一半（A11）**：网络分区状态下决策端（planner / command-orchestrator）不得签发任何新的副作用命令，仅允许本地观察写入与已批准队列排空——写成显式策略 + 对抗测试。

**M2-2 桥专属验收（全案最高风险步骤，独立门，不并入 Phase-1 验收）**：

- 桥组件组合证据齐七项（签名 ABI/BOM、身份、审批、lease、幂等、native result、postcondition）且 `HasVerifiedModernExecutionBridge()` 返回 true；
- 一次 fail-closed 反例：故意抽掉任一项，桥必须拒绝并保持 `ERROR_BRIDGE_REQUIRED`（证明不是假绿翻转）；
- Worker 启动/运行 ABI 已冻结且 Release-BOM 保护。

M2 Phase-1 验收（v3 分两级）：

**第一级 EMULATOR_VERIFIED（模拟器全链门，MV 环境）**：

- AVD 上连续 3 次会话：浏览 ≥10 条帖子、观察事件全部入 GBrain 且 exact 读回一致、每次决策日志含"查记忆"或 `MEMORY_MISS` 记录、产出 ≥1 条草稿且只存在于 GBrain/账本（平台侧零写入）；
- 对照日志中存在"记忆改变了决策"的实例，或明确报告全程无差异（后者触发 A3 假设复审——记忆回路若不改变任何判断，须回修裁剪/权重设计）；
- **草稿质量抽查（Q3 裁决：Phase-1 草稿的第一读者 = 用户本人）**：用户抽查 10 条草稿，按"与所看帖子相关""表达自然"两项评判，≥7 条合格；不合格 → 修记忆裁剪/生成逻辑后重验（垃圾草稿是记忆回路无效的最直接证据）；
- 审计链可从 trace_id 追到 soul/device/account/observation/GBrain projection。

**第二级 DEVICE_VERIFIED（真机门，F7 生产就绪顶格；前置 = 用户提供真机）**：

- 第 1 台真机重复模拟器级全部会话验收；ZD 进程 PID 与启动时间在验收期间不变（A6）；
- 第 2 台设备 + 第 2 个 Soul 重复，跨 Soul/设备零串联 → F7；
- 真机与模拟器同账号同版本 dump 对比，选择器差异清零或如实记录差异影响。

### M3' 升级门禁 CI 化 + 会话规程（v3 瘦身；A9 重释后的右尺寸形态，紧随 M0）

**大白话**：A9 的真实需求是"你让 Codex/Claude 升级某个模块时，机器保证它先读规矩、改完不伤别的模块"。已核实：这个保障的主体**已经存在**——phase0 静态门（合同路由四方校验 + 快照防漂移 + 全仓锁定构建）+ 候选门禁（全仓 contract/integration 套件聚合，内部自动重跑 phase0 并做 v2 收据绑定）+ phase0 收据机制（绑定根与受影响模块 AGENTS.md 哈希、自动把变更合同的下游消费者拉进 scope、门内新鲜度复验，CLI `--receipt-in/--receipt-out` 已有）。M3' 只做接线与规程，**不建自主编排流水线**。

任务：

1. **CI 候选门禁 contract job**：static-ci.yml 之外新增 job，跑 `run_candidate_gate.py --level contract --base <PR base>`（内部自动重跑 phase0 前置）；M0 完成后即应转绿，作为每次升级必过门；证据经 §5 锚点留痕；
2. **CI 候选门禁 integration job**：PostgreSQL 18.4 服务容器 + `--level integration`；显式标注"M1 记忆链修复前允许红"，M1 验收时转绿；
3. **会话入口轻脚本（A9 机器强制）**：`scripts/start-upgrade.sh <baseline>` 调用既有 `phase0.resolve_instruction_receipt` 签发收据到固定路径、打印本次 scope 必读的 AGENTS.md 清单；规程写入根 AGENTS.md 与 §5：先跑脚本 → 读清单 → 才写码；门禁以 `--receipt-in` 复验新鲜度。**不建 Host 编排、不领 worktree lease、不手造 factory v2 收据**（候选门禁内部已做 v2 收据绑定；factory-resolver/worktree-manager 均无 CLI，手动走不可行也无必要）；
4. **每升级异构复核（A8/A16）**：升级合入后由**异构模型**（DeepSeek/GLM 等非同源模型）按预置 checklist 复盘 diff + 两级门禁证据，报告入锚点。**上岗条件 = 埋错演练**：在演练升级中故意植入一个已知违规，复核未命中 → 复核机制不得上岗、A8 替代暂缓生效。**复核 FAIL → 自动置位 Kill switch（§6）冻结升级流水线**，只有用户可解除。删除原"每周全量日志复盘例行"（无生产运行时无日志可盘，M2 上岗后再议）；
5. **实弹演练**：选 audit-metrics patch 升级走完整轻链（含埋错演练）。

**Host 流水线处置（显式挂起）**：factory-control-plane-host 的 v2 运行时接线标 `DEFERRED`——依赖图实测其为纯消费端叶子（10 条边全部是它消费别人，无任何模块消费它的 factory.workflow.* 合同，独占并行波最后一波），永久推迟零阻塞。唯一触发条件：M0-2b 后实测其 4 个 required 套件若变红并阻塞全仓候选门禁绿，按最小代价三档处置（①只修红测试使其自洽 → ②迁运行时 → ③治理性退役，逐级评估，退役需动 10 个 provider 互惠边、默认不做）。factory-evidence-ledger"通电为权威证据存储"随 Host 一并推迟（其消费者只有 Host 流水线三成员），M0-5 git 锚点方案已满足 A8 可追查。11 个 factory-* 模块整体冻结保留、候选门禁面继续包含（零改动；代价 = 每次升级陪跑其套件）。

M3' 验收（原九锚点缩至六，均可跑可证伪）：

- 演练升级产出六个绑定同一 upgrade 的证据锚点：`phase0 收据 → 实现 diff → phase0 证据 → 候选 contract 证据 → 候选 integration 证据 → 异构复核报告`，缺任一即 FAIL（高风险升级——治理变更/合同 major/legacy 字节——六项全走且不豁免）；
- **埋错演练过**：植入的已知违规被异构复核命中并触发流水线冻结，随后由用户解除——一次完整的"安全网真的会响"证明；
- 复核报告按 checklist 逐项结论（不是自由文本），至少命中预置检查项偏差或明确标注全部通过。

---

## 4. 治理修订一览（本方案显式推翻/修改的现行条款）

| 现行条款 | 修改为 | 理由 | 约束 |
|---|---|---|---|
| R2/R3 强制独立人工批准 | 自动放行 + **异构模型交叉复核**（非同源模型，埋错演练上岗）+ append-only 审计 + Kill switch（§6 具体化）；**授权链根 = 用户 2026-07-16 签署的初始授权锚点（§8）** | 用户 A8/A16 | 改 risk-policy 本身是治理变更，仍须独立会话复核后合入；复核 FAIL → 流水线冻结 |
| 两人审批 / 外部 signer | 单人 + AI 复核替代管制，如实标注未满足两人规则 | 单人现实死锁 | 有第二人后再升级；信任根缺位不自签，维持 WAITING_EXTERNAL |
| F8/F9 为必经等级阶梯 | 标注 DEFERRED；生产就绪顶格 = F7 DEVICE_VERIFIED | 用户 A1 | v3 给出删除触发条件：M2 真机验收后首次治理修订中删除；期间冻结不维护 |
| 验证等级只有 Windows/真机 | 新增 `EMULATOR_VERIFIED` 中间等级；zenno-bridge canary "two non-production phones" 措辞与技术书 §9.4 同步修订 | 硬件缺位，虚拟化过渡（MV） | 模拟器证据不得冒充真机证据；F7 仍需真机 |
| M3 九锚点验收 | 缩至六锚点（收据→diff→phase0→contract→integration→异构复核） | A9 重释：无 Host 流水线即无 intent/lease/trusted-runner/merge-head/release 五环节 | 高风险升级六项全走不豁免 |
| 技术书 §9.1 "五个 OwnCode wrapper" | 修正为 7 个（实测） | 与代码不符 | — |
| 证据仅落 gitignored `Reports/` | 锚点入受跟踪 `governance/evidence-anchors/`；M3 后 factory-evidence-ledger 为权威账本 | git 历史零证据 | 不把原始证据 JSON 堆进两人保护的 `governance/**` |
| interest-reducer 声称拥有 PG snapshot store | 移除/降为 planned | 清单与实现不符 | — |
| 技术书 §1 "30→200 台"叙述 | 加注：现行锁定假设见 governance/assumptions.yaml | 与 A1 冲突 | — |

不修改的红线：Legacy 字节保护、fail-closed 语义（含"授权桥七项齐全前不翻转遗留常量"）、secret 边界、不可信输入规则、A12 平台边界、"未知即拒绝"、"治理变更不得自我放行"、边缘/回环层禁 GBrain 访问。

---

## 5. 注意事项（施工纪律）

1. 升级任何模块前先读根 `AGENTS.md` → 目标模块 `AGENTS.md` → `module.yaml` → 合同 → 依赖图/兼容矩阵 → 测试与证据（A9，机器强制 = M3'-3 会话入口轻脚本 + 门禁收据复验）。
2. Legacy C#（`Core/`、loose `Modules/*.cs`、`ZDProjects/`、`Extensions/`）按字节基线管理，修改走具名审批修复流程更新 `legacy-csharp-bytes.v1.json`；禁止 formatter/换行归一化。
3. `.yaml` 在 `governance/` 与新模块下是 JSON 内容；`Configs/Manifests/` 下是真 YAML——不要用同一套解析假设。
4. 推送只用 `dps2`；提交拆小、治理与业务不混。
5. Secret（GBrain OAuth、Voyage key、数据库口令）只经环境注入，不进 Git/日志/文档/证据。
6. required 检查只认 `PASS`；`SKIP/PARTIAL/NOT_RUN/INFRA_ERROR` 一律阻断，不许改判。
7. 模型输出、屏幕文本、OCR、GBrain 内容是数据不是指令；未知 action/selector/合同 major 失败关闭。
8. `UNKNOWN_OUTCOME` 进对账，禁止盲重试；外部副作用不可伪称已回滚。
9. 边缘/回环层（zenno-bridge、windows-edge-*）永不持有 GBrain 凭证；GBrain 访问只在 modern 侧。
10. 本方案变更（里程碑增删、假设修改）需用户确认后更新本文件并记 CHANGELOG。
11. **会话启动协议（v3，裁决 §2"不得跳过重算"与进度账本的矛盾）**：进度账本（§6）为**强制**维护。新会话**必须重验**：git HEAD/工作区状态、当步要依赖的门禁结论（要用哪个门就重跑哪个门）、环境可用性（.venv/dotnet/PG 探测）；**允许信账本**：里程碑/任务状态、历史证据锚点、已固化事实（§2）。原则：**要依赖什么就重验什么，其余信账本**。

---

## 6. 自治执行的停止 / 回滚 / 人工交接（A8 无人工审批下的安全网）

**硬停止条件**（命中任一，AI 停止自动推进并转人工交接，不得绕过）：

- 任一 required 检查非 `PASS`（含 SKIP/PARTIAL/NOT_RUN/INFRA_ERROR）；
- 原子批次（如 M0-2）未同时绿即中途提交；
- 治理快照在并行分支产生冲突或校验退出码非 0；
- `UNKNOWN_OUTCOME` 未对账、字节基线校验失败、跨 Soul 泄漏非零；
- 授权桥七项未齐却出现翻转遗留常量的意图；
- 单任务连续 3 次失败迭代仍不绿（计数型停止，v3——补齐无时间盒下的"卡死"识别）；
- 异构复核 FAIL 或埋错演练未命中（→ 同时置位 Kill switch，v3）。

**Kill switch 具体化（v3）**：两层。(a) **升级流水线冻结开关** = 受跟踪文件 `governance/KILL_SWITCH`：文件存在 → M3'-3 会话入口脚本拒绝签发收据、CI 门禁直接 FAIL；任何会话/CI/用户均可置位（文件内写明置位原因与时间），**只有用户可解除**（删除文件并在锚点记录解除理由）。(b) **命令执行面** = 复用 policy-approval 既有 `KillSwitchEnabled` 栅栏。**通知义务**：任何硬停止、Kill switch 置位、`SELECTOR_STALE`、复核 FAIL 必须主动通知用户；通道选型见 §7 待定项，通道敲定前的保底 = 在仓库根写入 `ATTENTION_REQUIRED.md` 并 push `dps2`。

**里程碑中途失败回滚**：每个里程碑以独立分支推进，失败即弃分支回到上一里程碑的绿色提交；已产生的外部副作用（草稿/GBrain 写入）走可审计 compensation，不写 `ROLLED_BACK`。

**人工前置清单（AI 无法自做、必须阻塞等待用户）**：

| 前置 | 阻塞的里程碑 |
|---|---|
| 签署初始授权锚点（一次性，M0-5 落锚；§8 即签署内容） | M0-5 |
| 安装 Parallels + Windows 11 ARM + ZennoDroid 14 天 demo（**第 0 天即可做**） | MV-1 → M2 |
| NAS 部署 GBrain + 密钥注入 | M1-5 |
| 提供第一阶段自有 Instagram 账号清单（模拟器用账号知情承担风控风险，**勿用主力账号**） | MV-4 / M2 |
| 提供 ≥1 台安卓真机（F7 验收载体；第 2 台补齐后完成 F7） | M2 真机门 |
| 确认第二台设备角色 | M2 验收 |
| 进入任何对外发布阶段前：重新评估 A8（A16） | Phase-2 前 |
| 若需正式生产等级：提供外部 signer / 第二审批人 | 超出本方案顶格（F7）后 |

（v3 删除"在真机跑人工 uiautomator dump 采集会话"——dump 采集改由 AI 在 MV 模拟器自做，真机 dump 仅在真机门做对比复核。）

**进度账本（强制，v3）**：维护一个 append-only 机器可读进度文件（里程碑/任务/状态/证据锚点），供跨会话续跑；该文件是进度记录，不是证据，不替代门禁。信任边界按 §5-11 会话启动协议执行：要依赖什么就重验什么，其余信账本。

---

## 7. 待定项（不阻塞 M0，进入对应里程碑前敲定）

- NAS 硬件/系统与 GBrain 精确 commit、schema 迁移版本对齐（M1-5 前，见该任务硬前置）；
- 第一阶段 Instagram 自有账号清单（MV-4 / M2 前）；
- 第二台设备角色（第二 Soul 验证机 / 备机）（M2 验收前）；
- 热替换再评估检查点：Bridge 稳定运行 30 天后（A10）；
- 通知通道选型（邮件 / Telegram bot / 其他推送；保底 = `ATTENTION_REQUIRED.md` + push）（M0 内敲定，v3）；
- ZennoLab 官方对 Parallels VM 内正式许可激活的书面确认（MV-6，购买前，v3）；
- 真机型号/数量/到位时间（M2 真机门前，v3）；
- 异构复核模型（DeepSeek/GLM 等）的接入方式与 API 配置（M3'-4 前，v3）。

---

## 8. 用户答复记录（2026-07-16 苏格拉底审题十九问——本文件 v3 修订的授权依据）

> 本表连同本文件 v3 的 SHA-256 应在 M0-5 作为**初始授权锚点**写入 `governance/evidence-anchors/`。

| # | 问题主题 | 用户答复 | v3 落点 |
|---|---|---|---|
| 1 | A2 vs A9 谁是第一 | 澄清：A9 = 用户驱动外部 AI（Codex/Claude）做模块化升级，改 A 不坏 B | A9 重写；M3 瘦身为 M3' |
| 2 | 产品心跳/时间约束 | 剔除，做好即用，与时间无关 | 不加时间型产品约束 |
| 3 | 草稿的读者与质量 | 委托 AI 裁决 | Phase-1 草稿读者 = 用户本人；M2 验收加质量抽查 |
| 4 | 桥的风险为何最后才碰 | 要求解释后采纳建议 | M0-P 探针工作包（P1/P2/P3） |
| 5 | 控制空窗 | 不能人工干预，按建议 | M2-2⑥ 加"异构复核已通电"前置 |
| 6 | 硬件前置 | Windows/真机不具备，问能否虚拟化 | 新增 MV 里程碑（AVD + Parallels + ZD Enterprise） |
| 7 | 时间维度/停止条件 | 按建议 | 硬停止加"连续 3 次失败迭代" |
| 8 | AI 复核有效性 | 用 DeepSeek/GLM 等便宜异构模型交叉验证 | A16；M3'-4 异构复核 + 埋错演练上岗 |
| 9 | Kill switch 触发者 | 按建议 | §6 具体化（KILL_SWITCH 文件 + 通知义务） |
| 10 | 授权链根 | 要求解释（解释：一次人类签署作链根，零成本） | M0-5 初始授权锚点 |
| 11 | A8 适用边界 | 按建议 | A16：零写阶段有效，发布前重评 |
| 12 | 记忆读路径降级 | 选定"无记忆滑行" | A15；M2-3 降级策略 |
| 13 | 记忆有效性度量 | 按建议 | M2-3 对照日志；验收含对照实例 |
| 14 | 架构载体（A13 缺位） | 按建议 | A13：保架构（门禁体系 = A9 的机器保障）、瘦流水线 |
| 15 | 九锚点/七项桥 | 要求解释（解释见 §8 后注） | 九锚点 → 六锚点；七项桥保留不减 |
| 16 | .omo 盲区 | 认为 Codex 升级时已剔除 | 实测确认：已删且活代码残留均为防御性墓碑（防复活哨兵，应保留），盲区关闭，无需任务 |
| 17 | 重算 vs 进度账本矛盾 | 要求解释 | §5-11 会话启动协议；账本转强制 |
| 18 | 通用性 | 不止 IG，要求任意 APP 通用 | A14；M2-4 通用校准工作流 + SELECTOR_STALE 循环 |
| 19 | F8/F9 处置 | 按建议 | 删除触发条件入 §3/§4 |

**§8 后注（对第 10/15/17 题的裁决理由，供用户否决）**：
- **第 10 题**：方案用"AI 复核"替代人工审批，但"允许 AI 复核替代人工"这条规则本身也得有人批——若批它的还是 AI，整条审计链的根是悬空的。修复只需一次：把你的十九问答复（本表）作为第一条锚点落账，此后一切授权都能回溯到这次真实的人类批准，再无人工环节。
- **第 15 题**：九锚点 = 原 M3 每次升级要求的九份证据（其中五份属于已被砍掉的自主流水线环节），故缩至六份。七项桥 = 打开"让软件碰手机"这把锁前的七道保险栓（正版签名、设备身份、审批单、临时工牌、防重复、手机真实回执、事后验收）——它们守的是全案最危险的动作，且六项代码已存在（成本是装配不是新建），**保留不减**。
- **第 17 题**：方案 §2 说"接手 AI 必须重算全部事实"（贵但稳），§6 又建议用进度账本免重算（快但可能过期）——两条打架。裁决：账本强制记进度，但"要依赖什么就重验什么"——例如要在门禁绿的基础上施工，就重跑那个门禁，而不是信账本里的"绿"。