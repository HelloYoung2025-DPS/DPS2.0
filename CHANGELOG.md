# DPS v4.5 更新日志

## [Unreleased] - 2026-07-17

### R0-B historical diagnostic governance rebaseline

#### Changed

- 为唯一的 R0-B 历史 base `8f63593d4f262ec1496b05300da75a71b86eaab4`、head `2ce0d14744ea8d25db2e963d4902cb0430b70cc4`、merge commit `8165fedbd44ecb8388c4dfce5e44e8753af21daf` 增加窄化 §4.5 step 1 再基线规则；仅在原 formal raw/publication 字节持久可读可校验、非 OpenAI 独立外审明确批准且 Owner 合入后记为 `REBASELINED`，不把 formal 改称 diagnostic，不改变原始 `39 PASS / 14 FAIL / 0 INFRA_ERROR`，也不外推其他批次或自动改变 T4/M1 裁决。

### R0-B legacy baseline hosted gate

#### Changed

- Hosted Static CI 现在从受保护的 `DPS_LEGACY_BASELINE_ANCHOR_B64` secret 恢复固定 SHA-256 的仓外 legacy baseline anchor，并把 GitHub 生成的唯一 gate 脚本封装成 root-owned 只读输入，再以无 sudo 权限的专用执行身份运行精确 PR head。Phase 0 仅接受受保护 Manifest 现有的精确 `.venv/bin/python -I` 形状，将无参数 `test_*.py` 作为 isolated unittest 执行，并只向 `legacy-runtime-adapter.static` 转发该 trusted-executor ambient 输入；candidate gate 也只通过专用窄 allowlist 将同一 ambient anchor 传给 cumulative Phase 0 prerequisite。缺失、Manifest 自声明、仓内、可写或同身份控制的 anchor 均失败关闭。此接口只建立 hosted required-path 与本批 §4.5 取证的受支持执行链，不改变产品能力或既有 raw gate 状态。
- 专用 Phase 0 身份在 checkout 内的写入面仅增加由 `Dps.slnx` 与 tracked `.csproj` 精确同集导出的、Git 忽略且初始不存在的逐项目 `bin/`、`obj/` 输出岛；每个输出岛独立为该身份所有且模式为 `0700`，不递归授权，也不放宽项目文件、项目目录或其他 checkout 路径。workflow 在启动 gate 前校验项目集合、canonical 路径、ACL/属主/模式、全仓可写目录精确集合及只读身份下的 clean Git 状态；任何漂移均失败关闭。
- Candidate gate 的 locked Git 在继续禁用 system/global config、固定绝对可执行文件并关闭 hooks/fsmonitor 的前提下，只以 command scope 信任当前 canonical repository root；不存在 wildcard 或 ambient Git config 注入。两个需要临时写入的 R0-B 对抗 fixture 分别使用既有 Git-ignored evidence 目录和仓外私有 `0700` Git object database，真实 checkout、canonical `.git`、refs 与产品文件继续只读；该修正只恢复专用身份下的既有测试语义，不改变产品能力或其余 raw FAIL。

### R0-D factory module retirement

#### Removed

- 删除 `factory-artifact-builder`、`factory-control-plane-host`、`factory-evidence-ledger`、`factory-impact-analyzer`、`factory-instruction-resolver`、`factory-merge-controller`、`factory-release-controller`、`factory-rollback-controller`、`factory-trusted-runner`、`factory-upgrade-intake` 与 `factory-worktree-manager` 十一个模块目录，并从 catalog、Manifest 快照、DAG、compatibility、候选测试策略、CODEOWNERS、CI、README 与 operations 中移除其专属权威引用；保留模块登记收敛为 23 个。

#### Changed

- instruction receipt 解析与候选变更影响计算继续由唯一 Phase 0 gate 提供；候选 Release BOM 校验器、外部 signer 合同、Control Plane active release binding、activation/revocation/rollback 与精确重投恢复能力保留在已迁入的普通工具和保留模块中。静态 CI 在唯一 required job 内新增 fail-closed `module-impact` 步骤，并支持 GitHub `merge_group` 事件。
- 最终异族外审收紧 Release BOM 的 route producer、descriptor producer 与 provenance builder 为当前/历史回滚身份的精确闭集，要求 native-stop trust receipt producer 与唯一 route producer 完全一致；Phase 0 instruction resolver 在 Git index 存在未解决冲突时失败关闭，module-impact 以不同 tree 的先进 base/merge HEAD 验证 consumer 扩展。R0-D 不自行签发新的 deployed trust anchor，相关 policy 与 validator pin 恢复 R0-C 已部署字节。

### R0-C Release BOM authority migration

#### Changed

- 将 `factory-release-controller` 保留的候选 BOM validator 与权威 `Tools/ci` 副本之间的迁移忠实度收紧为 required byte-level 测试：只允许已声明的顶层说明、部署信任策略路径和 `--repo-root` 深度三处精确差异，任何额外源码漂移均失败关闭；相应 Phase 0 adversarial test floor 从 137 提升到 138。
- 旧 F6–F9 外部验证的 ECDSA BOM 改用独立合同 `dps.external-verification-bom/v1` 与签名域 `dps-external-verification-bom/v1\n`；R0-C RSA Release BOM 继续使用 `dps.release-bom/v1` 与 `dps-release-bom/v1\n`。两条协议分别拒绝对方的合同 ID 与签名域，不再以同一身份解释两种算法。
- 候选 validator、仓外 signer payload builder、Release BOM trust verifier 与 Control Plane 激活现在共同要求完整签名 BOM 使用唯一的 sorted/compact canonical wire；候选与 previous BOM 的尾换行、重排、mapping/bytes 漂移、非法 Unicode scalar、非有限数及超限数字均失败关闭。`.NET` canonicalizer 采用 Python `sort_keys=True` 的 Unicode scalar 顺序与浮点格式，两个 C# 消费方共用受候选信任根绑定的跨语言语料；Control Plane 同步执行 4 MiB BOM 上限。Owner fixture 以新 receipt identity 由生产 builder 可复现生成，旧 receipt ID/签名不再复用；Phase 0 adversarial floor 从 138 提升到 145，Control Plane Unit/Contract floors 分别从 62/27 提升到 64/28。
- 候选 `ReleaseTrustPolicy` 对任何带 `bom` purpose 的公钥强制仓外 signer profile：purpose 原始数组必须唯一且恰为 `["bom"]`、算法固定 RSA-PSS/SHA-256、modulus 为无前导零 nibble 的 canonical lowercase unsigned hex 且至少 256 octets、public exponent 恰为 65537。候选与保留模块副本共用生产解析语义和多用途/1024-bit/exponent-3 等负例；完整测试 BOM 改用独立的固定 synthetic 2048-bit 测试专用 key，生产策略、signer 合同与 compatibility corpus 仍只含公开验证材料。
- R0-C 最终测试清单将 Phase 0 minimum 固定为 156，并将 Control Plane Host 的 Unit/Contract/Schema/Integration/Same-instance floors 固定为 `79/31/20/74/4`。Release-binding mutation 在 per-device durable scope 内先同步 journal head：提交应答丢失或 stale authority 的精确重试仅在原 postcondition 仍为当前真相时回放原 receipt；已被后续状态取代的 consumed activation/revocation/rollback 请求一律拒绝，不能再次产生 generation 或 receipt。真实 PostgreSQL 覆盖 activation、revocation、rollback 三类 commit-then-timeout 跨实例恢复。

### M0/R0-A 基线复现（第一个实现批次）

#### Added

- 施工权威文档落库：`Docs/RebuildPlan_重构计划书.md` v4、`Docs/Operations/ExternalReview_外审机制.md`、根 `CLAUDE.md` 会话纪律，以及作为其审查依据的 `Docs/RebuildPlan_审核报告.md`、`Docs/RebuildPlan_交叉审核报告.md` 与被 v4 取代但被 CHANGELOG 历史引用的 `Docs/UpgradePlan_升级方案.md`。此前这些文件仅存在于工作区。
- 按 `toolchain.lock.json` 在当前 macOS 环境恢复固定工具链：CPython 3.12.13（重建断链 `.venv`）、Node 24.18.0、.NET SDK 10.0.301、PowerShell 7.6.2、PostgreSQL 18.4（Postgres.app 2.9.5）、Android Platform Tools 37.0.0-14910828（全部经官方源与校验和/版本断言验证）。
- 在基线 `458f9bd41290` 的干净可丢弃 checkout 与工作区各完成一次 Phase 0 门禁运行，取得可复现的保留侧红项清单；红项 -> 目标批次迁移表随第一个实现 PR 的描述提交，不入库（依 AGENTS.md 任务状态不入仓规则）。
- 外审第二票（DeepSeek）自动化接入并按本批 Codex 复核意见硬化：施工模板补信任根严格串行取证/T10 探针前置/T8 合同前置三道防呆；外审机制登记仓外复核脚本的 SHA-256 指纹、外发前 fail-closed 敏感扫描与"显式 FAIL 不折算 PASS"聚合语义。

#### Changed

- 外审规则经用户裁定更正（同日生效，取代上一条中的"第二票"定位）：**开发期批次合入外审 = Codex 一票 + required 门禁 + 用户批准**；计划书 §9.1 的双异构复核判归项目运行时无人值守安全网（交付物），DeepSeek 脚本降级为 advisory 预备资产——其判决不构成合入条件，外审机制文档不再维护其 SHA-256 指纹（接线批次自行定版）。脚本本体保留敏感扫描（含 `--focus` 参数）与"显式 FAIL 不折算 PASS"语义。施工会话模板（`RebuildSessionPrompts_施工会话提示词.md`）定为**手动逐批**模式：按计划书 §16 依赖顺序挑对应 T# 模板，T 编号仅为索引、非执行顺序，跨轨/探针前置（T15 探针先于 T10、T10 合入后才可开 T8）由各模板自带的开工前置门强制。

- 半自动驾驶驱动（驱动会话模板 TA + 外审机制 §一之三 自动合入授权）经四轮 Codex 外审仍反复暴露调度/合并安全缺陷（审计节点 T4/T13/T19 只审不改无法进入"开 PR→合并"循环而空转、`--match-head-commit` 只绑 head 不绑已审 base/diff、手动序与依赖序不一致），且不属于 M0/R0-A（§10）退出范围、当前无可驱动批次——2026-07-18 用户拍板将其**移出 M0**，留待 R0 段完成后单独批次专门设计。M0 回归"每批一会话、门禁全绿 + Codex 一票 + 用户手动合入"的安全基线。剥离后按 Codex round-5/round-6 外审补齐并校正手动基线三处（原被自动驾驶层遮盖）：施工模板补完整执行依赖 DAG 节并清理 `Docs/README.md` 等入口的"按序粘贴"陈述；合入下限与审计门经 round-6/round-7 外审及一次 Codex 收口咨询（裁定采纳 A）定为**诚实下限 + 串行兜底 + 显式延期**——外审机制 §三-5 采程序性合入下限：`PASS` 仅对『已审 head + 已审 base 的组合』有效、合入前用户人工复核 head==已审 commit 且 base tip 未变（任一不符即冻结重审）、`gh pr merge --match-head-commit` 只绑 head；R0 依 §16 天然串行（R0-D 用 merge queue 做单 PR 冲突/合并验证，非并行合入），并行轨（M2∥M3）的安全并行合并编排作为**声明式延期**显式归 §9.1 专门批次、未就绪则退化串行（DAG 并行段同注此延期）；硬规则#1 审计门为程序性门（裁决落审计 PR 描述遵 `AGENTS.md:90`、可编辑不构成放行、由用户人工把关）。原子 head/base/merge-group 绑定与不可篡改审计门均显式延期至 R0 后 §9.1 专门批次，本文不宣称机器级保证已存在。（round-8 采纳后:去掉与计划书 §10 M1C 冲突的"不启用 merge queue"禁令、把并行前置门收敛为声明式延期,避免向 T5/T9 等模板传播半成品治理规则。round-9 恢复被过度精简掉的"审计裁决 FAIL/UNAVAILABLE 或记录不一致/无法核验即冻结"语义并传播至 T5/T9/T14；round-10 两条经用户拍板收口:外审机制 §三-5 不再复述 R0-D 退出门构成、明确 module-impact 必需套件与外部配置的 merge queue 为**并列独立**的强制验证且"真实合入串行"不折抵任一项,施工模板 T5/T9/T14/兜底 的开工前置门由"拒绝 FAIL/UNAVAILABLE"改为"要求可核验的 PASS"白名单、非 PASS 一律冻结。剩余的原子 head/base/merge-group 机器绑定与防篡改审计门深度仍显式归 R0 后 §9.1 专门批次,本文不宣称机器级保证已存在。）会话收尾 stop-gate 复审再指出 round-10 的 PASS 白名单是**单侧**的（消费侧 T5/T9/T14/兜底 要求 PASS,生产侧 T4/T13/T19 未产出定义明确的 PASS 令牌）——补齐**审计输出契约**:T4/T13/T19 在审计 PR 描述开头产出 `总裁决: PASS|FAIL|UNAVAILABLE` + `未决 DEFERRED` 两行令牌、裁决表每条"通过"引用证据锚点、下游对 §4.5 信任根锚点逐条核验其余抽验;并修复由此暴露的两个真缺口——(i) round-10"一律要求 PASS"把计划书 §5.4 **强制**的合法 DEFERRED 也判非 PASS、在 M2/M3→M4 造成环形死锁（DEFERRED 豁免"视为该条通过"+ 终局 T19 一次性回补问责修复）;(ii) 空提交证据锚被误当 clean 证据的误放行窗口（证据锚点引用 + 下游逐条核验收窄）。经内部对抗验证（Claude 多代理 freeze/false-pass/framing 三视角复测）确认 freeze 死锁闭合、越权为空。审计员主观误判 + 人工漏检的不可约内核仍属 §9.1 机器门、显式延期。

#### Known issues

- 基线 Phase 0 门禁存在既有红项（门禁自身裁决，非本批次引入）：`module-governance` 合同图互惠边缺失；候选测试 policy 与 Manifest 清单不一致；`Dps.slnx` 缺 `factory-release-controller` contracts 项目；`evidence-service`/`executor-gateway`/`memory-event-ledger`/`operation-compiler`/`soul-memory-adapter` 测试编译失败；`legacy-runtime-adapter` 四个 required 套件声明的 `.venv/bin/python` 不在门禁 `PYTHON_NAMES` 信任集内；`factory-*` 两个 `contract` 套件类型不被 Phase 0 接受。各红项归属批次见第一个实现 PR 的迁移表。
- 受信环境缺 `DPS_LEGACY_BASELINE_ANCHOR` 外部只读 anchor（计划书 §15 条目 7 用户前置事项），5 个 adversarial 测试因此失败。
- GitHub Free 计划私有仓库不支持分支保护与 rulesets（API 403），计划书 §4.1 第 5 条的仓库保护规则待用户决策（转公开或升级 GitHub Pro）后启用。

### Rebuild plan v4 (三轮交叉审核修订)

#### Changed

- `Docs/RebuildPlan_重构计划书.md` v3 -> v4。按三轮交叉审核的确认项修订：对 v2 多智能体交叉审核报告中仍适用于 v3 的残留项、对 v3 的异构模型终审（零 blocker，未引入修改）、以及对 v3 本体的补充完备性批判；方向与边界不变，仍为 `Proposed`、正式证据等级 `NONE`。明确 §4.5 信任根再基线化两段式取证、§11 Legacy anchor 重签枚举与 ZennoDroid/AVD 环境接受性探针、§15 用户一次性前置事项清单。取代 v3 作为最新施工提案。

### Rebuild plan v3 (多角色对抗审查重写)

#### Changed

- `Docs/RebuildPlan_重构计划书.md` v2 -> v3。六类独立审查角色与一轮 `code-reviewer` 终审从架构治理、Soul/认知、Legacy/执行、安全和可靠性统计等角度复核同一基线，并阻断 v2 直接施工。v3 保留“11 个 `factory-*` 全删”和“99% 使用滚动窗口”两项用户拍板，同时纠正：计划状态降为 `Proposed`；删除非法 `EMULATOR_VERIFIED`；Factory 删除改为 receipt 治理迁移、Release BOM 权威迁移、目录删除三个独立审查批次；模型仅保留 advisory/veto 权；tracked kill-switch/通知文件改为 Control Plane 与受保护 CI 外部状态；Legacy 物理删除推迟到目标 ZennoDroid 探针后；Persona v1、memory.event v3、gbrain.projection v3、interest v2、由 operation-compiler 拥有的 APP package、operation.compiled v2、edge handoff v2、独立 Legacy 入口、唯一 ActionExecutor、按需视觉提案链，以及 session/command 两套 300 滚动健康门和正式统计声明门分别定义。此前 Rebuild v2 与 UpgradePlan v3 仅保留为审查历史，不再作为最新施工提案。

### Rebuild plan v2 (取代 v1 与 UpgradePlan v3)

#### Changed

- `Docs/RebuildPlan_重构计划书.md` v1 → v2，依据用户四条硬要求（Soul 化拟人化 / 精简 / 单独并行升级 / 回归八条设计初衷）+ 五路实证盘点 + **Codex(GPT xhigh) 异构交叉复核** + 用户两项拍板（factory 删 11 全清、会话门控 99% 滚动窗口）修订. 核心变化：确立"删 11 factory 重资产 + 解锁两道 fail-closed 锁 + 经授权桥接线 + 六层 Soul 化 + 补四缺口"主线；§3.4 factory 删除手术补入 Codex 发现的四个隐藏依赖（run_candidate_gate 代码级 import、candidate_bom_validator 的 trust-policy 依赖、保留模块悬空边、运行时 active Release BOM 权威悬空）并加 R0-0 抛弃-worktree dry-run 前置；视觉验证上移大脑侧新增 request_vision_verdict exchange kind（边缘不持 AI 凭证）；桥 kind 改为 exchange v2 schema 并存（非改 v1 枚举）+ 修 denied 边界；persona 扩展定性纠正为 persona.revision v2 破坏性升级；legacy 物理删除补第五件 external trusted anchor 重签 runbook；并行升级如实定性为"开发并行、落地串行"+ landing 协议；§11 完整记录 Claude 对抗评审与 Codex 交叉复核的 14 项议题裁决. 该文件是施工方案，不改变任何模块验证等级.

#### Changed

- `Docs/UpgradePlan_升级方案.md` v2 → v3，依据用户 2026-07-16 苏格拉底审题十九问答复（方案 §8 为授权记录）与四路实证调研修订：A9 重释为"用户驱动的外部 AI 会话执行模块化升级"（不建自主编排流水线）；原 M3 瘦身为 M3'（升级门禁 CI 化 + 会话规程，Host v2 接线与 factory-evidence-ledger 通电标 DEFERRED，九锚点验收缩至六锚点）；新增 MV 虚拟化环境里程碑（Windows 电脑与安卓真机当前缺位，过渡拓扑 = macOS AVD + Parallels Windows 11 ARM + ZennoDroid Enterprise + 网络 ADB，新增 EMULATOR_VERIFIED 证据等级、F7 仍需真机）；M0 新增 M0-P 桥可行性探针工作包与初始授权锚点；安全网具体化（异构模型交叉复核 + 埋错演练上岗、governance/KILL_SWITCH 冻结开关、计数型硬停止、通知义务、进度账本转强制、会话启动协议）；选择器校准升级为任意 APP 通用工作流并加 SELECTOR_STALE 失效循环. 该文件是施工方案，不改变任何模块验证等级.

### Governance reset and modernization baseline

#### Removed

- 删除 `.omo/`, `.omo.conf` 和旧 `Tools/omo_guard/` 自定义工作流机制.
- 删除 AGENTS 中 Preflight, Advance, Postflight 和 L1-L4 本地 Gate 依赖.

#### Added

- `control-plane-host` 新增真实的 Policy submission lifecycle 双向边界：直接引用 Policy 公共严格合同包，实现独立 reconciliation/human-recovery 签名能力、分离端口和签名 state consumer；精确绑定 Soul/device/account/trace/idempotency、attempt/lease、Release BOM、intent/native request、predecessor/evidence，未知或伪造输入失败关闭。该切片仍为 proposed，未获得 PostgreSQL/Windows/真机或生产发布证据.
- `control-plane-host` 将通用 lifecycle signer 收紧为独立 reconciliation 与强类型 human-recovery approval capability，增加精确端口权限范围、不同凭证权威指纹和最多五秒的失败关闭超时；当前 `in-process-api` 仅是 proposed 合同 facade，不宣称已具备认证传输、独立进程或生产凭证隔离.
- lifecycle authority 改为可取消异步能力并加入最多五秒硬超时、外部调用前后取消检查及 quarantine：不服从取消的调用不会并行重入，私有 canonical 副本保留到迟到结果完成后，迟到签名和副本均被丢弃并清零.

- 重写 `AGENTS.md`, 使用普通 Git, versioned contracts, automated tests and CI 作为工程约束. 逻辑 module root 固定为 `modules/<module-id>/`; 保守迁移期因大小写不敏感文件系统使用物理 `Modules/<module-id>/`, 避免搬动 legacy runtime.
- 新增 `Docs/Architecture/TargetArchitecture_目标架构.md`, 定义 DPS Control Plane, GBrain Company 和 ZennoDroid Thin Executor 边界.
- 新增 `Docs/EngineeringStandards_工程标准.md`, 定义 build, test, CI, security, observability, release and device gates.
- 新增 `.editorconfig` 和 `.gitattributes`; 新式模块使用 UTF-8/LF, 现有 `Core/`, `Modules/`, `ZDProjects/`, `Extensions/` C# 保持原始 bytes, BOM and line endings.
- 新增 dependency-free repository validator 和 Hosted Static CI, 覆盖 JSON, Python, Markdown links and removed workflow paths.
- 新增 Company GBrain 本地非生产运维文档, 锁定 PostgreSQL/pgvector, Voyage embedding, 非 PII Soul-to-Source 映射, loopback HTTP/OAuth 和 F7 证据边界; 不将本机安装冒充 GBrain 实连或真机验收.
- 新增 GBrain 0.42.42.0 本机兼容性探测记录：用禁用 embedding 的一次性 PGLite brain 验证两个 Source 的同 slug 隔离、精确读取与软删除边界，并锁定原生 32 字符 Source ID 限制；该记录仅是诊断事实，不提升正式验证等级，也未读取用户 API key 文件.
- 工程标准新增跨模块 JSON hardening：安全字符串必须精确长度并绝对终止，owner/consumer 共用对抗 corpus，UTC 做 Schema/DTO/DB parity；Int64 golden corpus 禁止经过 JavaScript `Number` 等 IEEE-754 路径重写，防止边界值静默舍入.
- 新增受治理模块目录, 每个都有唯一 `AGENTS.md`, `module.yaml`, 版本合同, 测试, 迁移与运维边界. 模块、合同和套件数量由唯一门禁从当前 Manifest 现场重建，不在文档中维护可漂移的计数副本. 除 `legacy-runtime-adapter` 为 transitional 外, 新模块均保持 `proposed` 且 `releaseEligible=false`.
- 新增 pinned .NET/Python/Node/PowerShell/ADB 工具链, `Dps.slnx`, 锁文件, PostgreSQL 集成测试基础, Factory 受信门与 F6-F9 外部证据验证器. 这些都是待前置门和外部环境证明的 candidate/foundation, 不是生产发布宣告.
- 公共合同必须有唯一 provider 模块和必需 Contract 套件覆盖；新增唯一 unsigned Candidate Runner 和固定 Candidate Test Policy，每次从 Manifest 重建精确测试目标、测试树摘要、Factory Instruction Receipt、UpgradeIntent、累计 Phase 0 与写后工作区边界，并拒绝策略或计数漂移.
- Candidate evidence Schema 强制 `candidate_verification_level=null`, `verification_level=null`, `signed=false`, `formal_evidence_eligible=false`; 本地 Runner 永不签发正式等级.
- Contract 与 Integration Candidate 的 Phase 0 伴随证据现写入目标目录下保留的 `phase0-prerequisites/` 子目录，禁止候选占用该命名空间，避免候选文件与伴随文件互相覆盖；证据输出强制小写 ASCII `.json`, 逐段拒绝符号链接, 并通过固定目录句柄与 `O_NOFOLLOW` 的 `0600` 原子写入阻断目录替换; 并行目录创建竞态也会安全重开且继续拒绝链接。缺少这些安全原语的平台会失败关闭, 需在 F6 实现 Windows 安全句柄写入器后才能签发证据.
- Phase 0 与 Candidate 受信执行环境不再采用 ambient PATH/HOME/TMPDIR：.NET/Node 只从固定候选探测，每次运行使用私有 `0700` HOME、CLI cache、scratch 与 temp，保留不可由 Manifest 覆盖的沙箱标记，关闭 MSBuild server/node reuse，并在有效 restore argv 中强制 locked mode、仓库 `NuGet.Config`、static graph 与 NuGetAudit `true/all`。CSSM、离线漏洞源、网络不可达与超时按 `INFRA_ERROR` 失败关闭，不通过关闭审计制造成功.
- Phase 0 evidence 与 instruction receipt 只能写入 Git-ignored `Reports/ci` 下不同的小写 ASCII JSON 路径，不能覆盖 tracked 文件；候选 JSON 使用单一 `O_NOFOLLOW` 文件描述符完成读取和 SHA-256 绑定，正式证据写后再次核对 HEAD、工作区摘要与 clean 状态，期间发生变化即撤销正式等级.
- Phase 0 与 Candidate 默认证据改为由 Python runner 生成的唯一 run-id 目录；`release.sh` 未显式指定路径时复用该安全默认，显式路径通过 Bash 数组传递并继续由 runner 校验。Hosted CI 现上传完整 canonical 证据目录，使 payload、marker 和任何 quarantine claim 共同传输；claim unlink 会 fsync 父目录，失败时恢复 claim 并继续失败关闭.
- Integration 套件显式区分真实 PostgreSQL、真实本地子进程、确定性模拟和外部设备；当前受管沙箱不能启动独立 PostgreSQL，所有必需数据库套件在本轮均按 `INFRA_ERROR/NOT_RUN` 失败关闭. Candidate 门禁从当前 Manifest 动态重算累计覆盖缺口，任一缺口关闭前都必须失败.
- Candidate Test Policy 进一步将证据类型、测试类别和环境要求做成不可拆分的组合：真实 PostgreSQL 只能运行 `Integration` 且必须声明数据库连接环境，真实本地进程不得接收数据库环境，模拟不得接收运行时秘密，`SecuritySimulation` 只能标记为模拟；错误组合在执行前由 Schema 失败关闭.
- Candidate Runner 补齐对抗式可信边界：前代基线现在同时钉住根指令、核心治理快照、.NET/NuGet/solution 构建输入、验证器和 release 入口；PostgreSQL 18.4 版本由锁定 `psycopg` 对每个声明 DSN 直接查询，不再相信任意 `psql` stdout；任何必需环境缺失都在测试进程启动前记为 `INFRA_ERROR`，suite 与顶层环境键必须精确互证。Zenno 特例被锁定为无秘密的 `SIMULATION/SecuritySimulation`，UpgradeIntent 必须按当前仓库事实全对象重建，普通执行异常逐套件隔离继续而不吞进程控制异常，日志中的私钥 PEM 会被清除。该本地 runner 仍无进程级网络/文件能力沙箱，因此证据继续保持 unsigned、不可正式签发，真实凭证只允许专用非生产测试用途.
- Candidate Runner 进一步在任何标准库导入前移除脚本目录，并从活动解释器预加载及验证锁定的 `psycopg`, PyYAML 与 Schema 依赖；本地 DPS 模块加载后立即再次移除 `Tools/ci`，并拒绝该目录中的源码、package、extension 或 sourceless pyc 依赖影子。Git 真相固定使用 `/usr/bin/git`、空 system/global config 与隔离 `GIT_*` 环境。`Directory.Build.targets` 现在属于前代信任根。任意测试 stdout/stderr 不再嵌入可携带的 candidate JSON，只保留输出摘要及 SHA-256，避免测试读取非生产私钥后通过分片、hex 或 URL 解码变体泄漏。该 SHA-256 仍不是签名或原始证据，candidate PASS 仍禁止作为自动发布授权；恶意测试弱化与可重写 unsigned JSON 必须由只读 checkout、隔离 Trusted Runner、不可变原始证据、独立签发和发布审批闭环.

#### Changed

- README 明确当前仓库级正式签发证据为 `NONE`; 定向测试通过不会在唯一 Phase 0, 干净检出可复现性与独立证据签发前自动升级仓库状态.
- 模块目录或文档存在不再视为实现证明; 状态必须由 Manifest, 源码, required tests and raw evidence 共同支持.
- Docs 允许按 Architecture, Testing, Operations and Security 等长期主题演进, 不再使用固定文件白名单.
- Git workflow 改为 scoped staging, 不再推荐 release automation 使用 `git add .`.
- Release helper 改为纯验证入口: 工作区非干净时失败关闭, 只运行唯一 Phase 0 和 candidate Release BOM 验证; 不 commit, tag, push, sign, deploy 或 approve.
- 修复 `Configs/Manifests/instagram.json` 和 `Configs/Manifests/template.json` 的 JSON 语法错误, 不改变配置语义.
- `gbrain.projection/v1` 成为唯一的离线 Source/revision/checksum 语义; `soul-memory-adapter` 改为直接消费该投影并做精确读回校验, 删除第二套平行 ID/revision 算法.
- 区分 GBrain 原生 OAuth/Source 绑定与 F7 证据别名：真实 `logical_source_id` 只能是 `gbrain-projector` 生成的 `dps-<28hex>`；`gs_<16hex>` 只是由验证器重算的非 PII 外部证据别名，不得作为 GBrain Source 或 OAuth 权限值.
- 修正 F7 外部证据合同的 revision 漂移：`projection_checks[].projection_revision` 现在强制为与 `gbrain.projection/v1` 和 `soul.memory.readback/v1` 相同的 64 位小写 SHA-256；可选 `external_revision` 只保存平台元数据，不能替代 DPS 逻辑修订。此变更仅修复验证合同与门禁，不生成或声称任何 GBrain 真机证据.
- F7 外部输入迁移到 `dps.device-gbrain-verification-input/v2`：强制 canonical Soul/device/account ID、逻辑 Source 到确定性非 PII 短别名映射，并让每条 projection check 绑定版本化 raw JSON artifact 的 ID、SHA-256 和完整 scope/revision/checksum tuple；门禁从两份 canonical `gbrain.projection/v1` bytes 独立重算 checksum、核对 scope、按现有 C# canonicalizer 重算 DTO checksum 并要求 exact readback，且拒绝超出 System.Decimal 范围/scale 的非生产 DTO 数字。每个 Soul 还必须绑定独立的版本化 Search 回读制品，保存 canonical 查询/响应 bytes；门禁从 bytes 重算摘要、解析 `gbrain.search-result/v1` 并显式核对 Source、Soul、schema、source-scoped provenance、不超过 300 秒的新鲜度及每条结果的 scope。通用外部 ID 改为字母前缀 opaque 值，拒绝纯数字手机号形态。v1 仅保留作迁移记录且门禁拒绝。全部 F6-F9 environment 改为阶段固定字段及逐字段格式，trust policy 只能固定值而不能扩展自由字段；嵌套值、敏感字段和值失败关闭。POSIX trust policy 改为单一 no-follow FD 链读取，拒绝父目录 symlink 与检查后路径替换；缺少等价安全原语的平台保持 `WAITING_EXTERNAL`。此项仍只增强候选门禁，不生成 `DEVICE_VERIFIED` 证据.
- 补全 F6/F9 executable external contracts：F6 将 Windows、ZennoDroid、.NET Framework、C#、CodeDom、GAC、DLL/Zenno project load、ADB authorization、Bridge ABI、固定 loopback port、fail-closed timeout、native error 与连接连续性全部纳入 trust-policy exact binding，并使 process observation 精确绑定签名测量窗口；F9 必须验证由可信 CANARY issuer 签名并绑定精确 commit/BOM/candidate 的 F8 receipt，从 BOM-hashed raw Manifests 重建 dependency DAG 和 compatibility matrix，拒绝 evidence 自报缺边，并证明最多四条 rollout line 的传递依赖独立性与普通 module 五分钟回滚。100 sustained、200 burst、400 simulated 三类 run 改用不同的 versioned raw JSON，由门禁重算匿名 actor cardinality、并发、时间/时长、72 小时覆盖、每窗口最老积压不超过 120 秒与结束后 backlog recovery，不再接受 marker bytes 或 summary 自述。F7 另以临时测试密钥完成完整 envelope 与 BOM 的真实 P-256/P1363 正向及篡改负测；这些 synthetic tests 不生成任何外部等级证据.
- Hosted CI 的 runner 标签改为 `ubuntu-24.04`, 所有 GitHub Actions 改为官方版本对应的完整 commit SHA; 新增 CODEOWNERS 基线与仓库外两人审批/受保护 workflow 运维要求.

#### Runtime impact

- 现有 legacy 业务主干保持不动; `Modules/SessionRunner.cs` 字节哈希被冻结。除两个 `Configs/` manifest 语法修复外, F1 只改动 `ZDProjects/RuntimeTestRunner.cs`, `ZDProjects/TestRunner.cs` 和 `ZDProjects/Tests/Reddit_IntegrationTest.cs` 以清除固定 PASS, 失败不传播和 Mock 冒充 Integration. 新 `Modules/<module-id>/` 代码未接入现有 ZennoDroid 运行路径.
- F5 的真实 SessionRunner 绞杀接线尚未完成；F6 Windows A/B、F7 GBrain 与两部授权非生产手机、F8 三十台灰度和 F9 两百台/72 小时规模证据仍为外部阻断，不能由本地测试替代.
- 历史 CHANGELOG 中的 `.omo` 路径保留为当时版本事实.


## [4.6.1] - 2026-03-14

### SmartOrchestrator 智能编排器实现 (Phase 1)

新增 `Modules/Core/SmartOrchestrator.cs` 智能编排器子模块，并集成到 SessionRunner 的 `ExecuteWithUnifiedEngine` 方法中。

#### 新增
- **SmartOrchestrator.cs** (Modules/Core/) — 智能编排器核心模块
  - `EvaluateResult()`: 双层成功判定（执行成功 + 业务成功），检出假成功
  - `DecideRecovery()`: 分级恢复决策（Retry → LocalRecovery → VisionAssist → FallbackScript → Abort）
  - `RecordSuccess/Failure()`: 操作级与会话级状态追踪
  - `LoadConfig()`: 从 BehaviorConfig.json 的 `smart_orchestrator` 节加载恢复预算
  - `GetSessionSummary()`: 会话级恢复统计摘要

#### 改动
- **SessionRunner.cs** (Modules/) — 集成 SmartOrchestrator
  - `ExecuteWithUnifiedEngine` 操作循环改造: 散落的 SUCCESS/ERROR/SKIP 判断 → SmartOrchestrator.EvaluateResult 双层判定
  - 恢复逻辑改造: 硬编码 Vision 验证 → SmartOrchestrator.DecideRecovery 分级恢复循环
  - 会话结束时输出编排器统计摘要（恢复总次数、视觉调用、假成功检出数）

#### 向后兼容
- SessionRunner.Run() 签名与返回值不变
- 不配置 `smart_orchestrator` 节时使用默认恢复预算（retry=2, localRecovery=2, vision=3, fallback=1）
- 现有 VisionCorrector 和 PostOperationHealthCheck 行为保留

#### 受影响文件
- `Modules/Core/SmartOrchestrator.cs` — 新增
- `Modules/SessionRunner.cs` — 修改
- `.omo/modules/SessionRunner.md` — 任务追踪同步


## [4.6.0] - 2026-03-14

### SessionRunner 执行层向 ZennoDroid 原生动作迁移 (架构设计)

以 Reddit 为试点，设计执行层从 C# 直接驱动向 ZennoDroid 原生拟人动作迁移的四层架构。

#### 架构决策 (ADR-015)
- **ZD 原子动作层**: tap、swipe、input_text 等最小执行单元由 ZD 动作块承载，Switch 编号分发
- **ZD 组合动作层**: open_post、comment 等业务子流程，复用现有 Operations JSON 定义
- **智能编排层**: SessionRunner 保留编排、恢复、日志；新增智能编排器子模块（先内嵌后独立）
- **AI 视觉纠偏层**: 分级恢复失败后介入，权限限于功能块或小流程级别临时修正
- **双层成功判定**: ZD 判执行成功，编排器判业务成功
- **回滚策略**: feature-flag 实现新旧路径并存

#### SessionRunner 职责边界梳理
- 保留: 会话生命周期总控、策略编排与门控、统一意图执行链、恢复编排、成功门控
- 候选下沉: 评论回退文案、帖子上下文构建、遮挡物处理细节、自动接入桥接

#### 受影响文件
- `.omo/current-task/plan.md` — 迁移任务计划
- `.omo/modules/SessionRunner.md` — 模块追踪更新
- `.omo/decisions_架构决策.md` — ADR-015 新增
- `.omo/layers/l1-project.yaml` — 编码规则与技术债登记
- `.omo/layers/l2-module.yaml` — 模块级编码规则追加
- `.omo/layers/l3-operation.yaml` — 操作级编码规则追加
- `.omo/layers/l4-step.yaml` — 步骤级编码规则追加
- `Docs/TechManual_技术手册.md` — 版本历史更新


## [4.5.24] - 2026-03-11

### SessionRunner v4.5.8 会话成功门控升级

实现 CHANGELOG v4.5.8 中声明但未落地的会话成功门控逻辑，使代码与文档完全一致。

#### 修改内容
- **成功门控阈值**: 从 `failedActions <= actionCount/2` (50%) 改为 `sessionSuccessRate >= 0.95` 且 `successfulActions >= min_successful_actions`（默认 6）
- **分类计数器**: 新增 `successfulActions`、`skippedActions` 与 `failedActions` 三路分类计数（SKIP 不计入分母）
- **门控阈值可配置**: 从 `BehaviorConfig.json` 的 `session_gate` 节读取 `min_success_rate` 和 `min_successful_actions`，不配置时使用默认值
- **输出变量补齐**: `action_count`（=成功数）、`action_attempt_count`（=总尝试数）、`session_successful_actions`、`session_failed_actions`、`session_skipped_actions`、`session_success_rate`
- **`action_count` 语义修正**: 从"每轮累加（=总尝试数）"改为"仅成功动作数"，与 v4.5.8 文档定义一致
- **StateSaver.cs 兼容**: `total_actions` 累计统计现在正确计入成功动作数

#### 受影响文件
- `Modules/SessionRunner.cs` — 计数逻辑 + 门控逻辑 + 输出变量
- `.omo/modules/SessionRunner.md` — 任务追踪同步
- `.omo/modules/index.md` — 模块索引同步


## [4.5.23] - 2026-03-08

### SessionRunner 无限循环 Bug 修复与导航恢复机制

修复 E2E 测试中发现的 50 动作无限循环 Bug（`back_to_feed` 假阳性验证 + 过期 UI 签名 + 无导航恢复），并新增三级导航恢复机制。

#### Bug Fix 1: Reddit UI 签名更新（实机验证）
- `Config/PlatformsConfig.json`
  - **feed 页面签名**: 替换 5 个全部失效的信号（`bottom_nav` 等），更新为实机验证通过的 `post_unit`、`home_screen_surface`、`feed_lazy_column`、`post_footer`、`post_header`
  - **post_detail 页面签名**: 替换失效的 `bottom_controls`、`action_bar`，更新为 `fbp_screen`、`voteButtonGroup`、`fbp_back_button`、`Back`（content-desc）、`comment_button`
  - **post_unit 选择器**: fallback 从 `class=android.widget.FrameLayout`（所有页面匹配，导致假阳性）改为 `resource-id=promoted_post_unit`（feed 独有）
  - **新增选择器**: `feed_container`、`post_header`、`post_overflow`、`join_button`、`back_button`、`vote_group`、`vote_upvote`、`vote_downvote`、`detail_screen`

#### Bug Fix 2: back_to_feed 验证逻辑
- `Config/Operations/reddit_operations.json`
  - verify 步骤 selector 从 `post_unit` 改为 `feed_container`（映射到 `feed_lazy_column`，无 fallback，无假阳性风险）
  - 新增 `require` 步骤 `page=feed, on_fail=abort`，确保页面检测也通过

#### Bug Fix 3: 导航恢复机制
- `Modules/SessionRunner.cs`
  - 新增 `_consecutiveSkips` 计数器和 `MAX_CONSECUTIVE_SKIPS=3` 阈值
  - 主循环中跟踪连续 SKIP：连续 3 次触发 `ForceNavigateToFeed()`
  - `ForceNavigateToFeed()` 三级恢复策略:
    1. 点击 Reddit `fbp_back_button`（最可靠）
    2. Android 返回键连续按压（最多 5 次），每次检测页面状态
    3. `am force-stop` + `monkey` 重启 APP（最后手段）
  - 每级恢复后检测页面状态，成功即停止

### 影响范围
- 层级: L3（跨文件配置 + 逻辑修复）
- 功能影响: 修复运行时无限循环，新增导航恢复，不改变正常流程
- 向后兼容: 所有修改仅影响 Reddit 平台配置和 SessionRunner 恢复逻辑

## [4.5.22] - 2026-03-08

### App Onboarder 智能升级（v1.1）

升级 `Tools/app_onboarder/` 工具模块，使其在打开 APP 时智能检测当前页面状态，动态规划探索流程，并在每个阶段使用视觉验证检查点。增强手机适配能力。

#### Feature 1: 智能页面状态检测
- `ui_analyzer.py`
  - 新增 `detect_app_state()` 方法：综合分析当前 APP 页面状态（home/feed/post_detail/profile/unknown），返回状态、置信度和判断依据
- `app_explorer.py`
  - 新增 `detect_current_state()` 方法：在探索开始前检测 APP 当前页面
- `main.py`
  - 新增 `step_detect_state()` 步骤：在探索前报告当前页面状态

#### Feature 2: 动态剧本规划
- `app_explorer.py`
  - 新增 `plan_exploration(current_state)` 方法：根据检测到的状态动态规划探索阶段
    - feed → 跳过 Phase 1/2，直接 Feed 分析
    - post_detail / profile / unknown → 先导航回首页，再全流程
    - home → 正常全流程
  - 新增 `navigate_to_home()` 方法：从任意页面导航回首页（Back 键 + 重启兜底）
  - 修改 `run()` 方法：集成状态检测 → 剧本规划 → 按计划执行，支持 Phase 跳过

#### Feature 3: 步骤级视觉验证
- `app_explorer.py`
  - 新增 `visual_checkpoint(phase_name)` 方法：每个 Phase 后截图并记录到 `app_map["visual_checkpoints"]`
- `config_generator.py`
  - 在 open_post/like/comment 操作后插入 `visual_verify` 日志步骤
- `test_runner.py`
  - 新增 `_capture_phase_screenshot(phase)` 方法：E2E 测试每个失败 Phase 后截图保存
  - 测试报告新增 `phase_screenshots` 字段

#### Feature 4: 手机适配增强
- `adb_controller.py`
  - 新增 `get_device_info()` 方法：返回完整设备信息（型号、品牌、Android 版本、SDK 版本、分辨率、DPI、序列号）
- `main.py`
  - 启动时显示完整设备信息（型号、Android 版本、DPI）
  - 版本号升级至 1.1

### 影响范围
- 层级: L2（模块增强）+ L4（新增步骤）
- 功能影响: 增强探索智能化，不改变现有生成配置的格式
- 向后兼容: 无参数调用行为不变，新增功能为增量特性

## [4.5.21] - 2026-03-08

### SessionRunner 初始页面检测与剧本规划

- `Modules/SessionRunner.cs`
  - 在 `Run()` 方法初始化阶段（平台配置加载后、主循环前）新增初始页面检测与剧本规划：
    1. `DetectInitialPage()` — 调用 `PageDetector.Detect()` 判断 APP 当前所处页面（feed/post_detail/comment/profile/unknown）
    2. `PlanPreSessionActions()` — 根据检测到的页面状态规划预设动作序列：
       - **feed（首页）**: 无需预设动作，直接开始正常会话
       - **post_detail（帖子详情页）**: 先阅读当前帖子 → 返回首页 → 正常会话
       - **其他页面 / unknown**: 先返回首页 → 正常会话
    3. `ExecutePreSessionActions()` — 在主循环前执行规划的预设动作序列，每步更新页面状态
  - 替换原有的 `_currentPage = "unknown"` 硬编码初始化为实际页面检测
  - 新增 ZD 变量输出 `initial_page`，记录 APP 启动时的页面状态

### 影响范围
- 层级: L4（局部代码新增）
- 功能影响: 仅新增前置检测与预设动作，不改变现有主循环逻辑
- 向后兼容: 检测失败时退化为 "unknown"，行为与修改前完全一致

## [4.5.20] - 2026-03-08

### SessionRunner 设备连接检测

- `Modules/SessionRunner.cs`
  - 在 `Run()` 方法初始化阶段（`CoreHelper.Init` 之后、业务逻辑之前）新增三级设备连接检测：
    1. `CoreHelper.HasInstance()` — 验证 instance 对象存在
    2. `CoreHelper.GetDroid()` — 验证 DroidInstance 可用
    3. `CoreHelper.GetLayout()` — 尝试获取 UI 层级，验证设备真实连接
  - 任一检测失败立即返回明确 ERROR 信息并记录日志，避免后续操作随机失败
  - 所有检测通过后输出「设备连接检测通过」日志

### 影响范围
- 层级: L4（局部代码新增）
- 功能影响: 仅新增前置检测，不改变现有业务逻辑

## [4.5.19] - 2026-03-08

### SessionRunner 编译错误修复（第二轮）

- `Modules/UIHelper.cs`（删除）
  - 该文件已标注 [已废弃]，依赖 System.Xml.Linq（不在 CSharpCodeProvider 引用列表中）
  - 功能已被 `Modules/Core/SelectorEngine.cs` 完全替代（纯字符串解析，无外部依赖）
  - 无任何其他模块调用 UIHelper 的方法
  - 因位于 Modules/ 目录被同级扫描自动加入编译，导致 15 处 System.Xml.Linq 引用错误

### 影响范围
- 层级: L4（废弃文件清理）
- 功能影响: 无（已废弃且无调用者）

## [4.5.18] - 2026-03-08

### SessionRunner 编译错误修复

- `ZDProjects/SessionRunner_OwnCode.cs`
  - 移除 Core/ 引擎文件加载块（ScriptHelpers/HumanizationEngine/UILocator/ErrorRecovery）
  - 这些文件是 Own Code 风格的裸 Func/Action 代码，无法与 class 风格模块一起通过 CSharpCodeProvider 编译
  - 与 ModuleLoader.cs 已有的修复保持一致（v4.5.2 已移除，但 SessionRunner_OwnCode.cs 未同步）
  - 修复 CS0116/CS1518/CS1022 三个编译错误

### 影响范围
- 层级: L4（局部代码修复）
- 仅改动 ZDProjects 入口脚本，Modules/SessionRunner.cs 模块代码本身结构正确无需修改

## [4.5.17] - 2026-03-08

### Docs 施工图修正（ZennoDroid 新人搭建）

- `.omo/decisions_架构决策.md`
  - 为 `ADR-014` 补充约束：施工图必须标明“ZennoDroid 施工图 / 模块内部逻辑图”，并为每个分支写清变量依据、返回值和条件表达式
- `.omo/layers/l1-project.yaml`
  - 将“文档体系治理”扩展到“分支依据缺失”场景，并补充施工图标注规则
- `Docs/ConfigGuide_配置指南.md`
  - 重写 `4.2 首次运行流程`，改成可直接照着在 ZennoDroid 中搭建的最小闭环施工图
  - 新增“每个分支的依据是什么”“在 ZennoDroid 里如何实现”“这张图不包含什么”，避免把模块内部逻辑图误当成外层接线图
  - 重写 `4.4`，明确 Reddit / Instagram 当前走的是 `SessionRunner + IntentMappings + Operations + ActionExecutor` 主链，不再误导新人去外层拆平台动作块
- `Docs/TechManual_技术手册.md`
  - 修正旧模块回退、RateLimiter 和 SessionRunner 的文档口径，使之与当前统一主链一致
- `Docs/README.md`
  - 明确新手第一次搭建应优先阅读 `ConfigGuide` 的施工章节，并提醒不要把 `4.3 / 4.4` 内部逻辑图拆成额外动作块

### 架构遗留清理（第一轮 + 第二轮收尾）

经全面验证确认项目主执行链已完全切换到 `ActionExecutor + operations.json` 路径后，清除所有遗留的旧平台模块架构残留。

**删除**:
- `Platforms/Reddit/RedditModule.cs` — 不被主链加载的旧 C# 平台模块（~700 行）
- `Platforms/Instagram/InstagramModule.cs` — 不被主链加载的旧 C# 平台模块（~600 行）
- `Core/PlatformBase.cs` — 旧平台基类接口，不被编译/加载
- `Modules/SessionRunner.cs` 中的 `LoadPlatformModule()` 死方法 — 永远返回 ERROR 的回退分支

**重写**:
- `ZDProjects/Tests/MultiPlatform_IntegrationTest.cs` — 移除所有对已删除 `Platforms/` 文件的断言，改为测试 JSON 配置驱动流程（operations.json 结构验证、intent mappings 存在性、PlatformsConfig 速率限制配置）
  - 新增 `operations_structure` 测试场景：验证 reddit/instagram/babycenter 三个平台的 operations JSON 结构
  - 修正 ErrorRecovery 路径：从 `Core\ErrorRecovery.cs` 改为 `Modules\Core\ErrorRecovery.cs`

**文档清理**:
- `Docs/TechManual_技术手册.md` — 移除 PlatformBase.cs 架构图引用、LoadPlatformModule() 流程节点、术语表中的 Platform Base/Platform Module 条目
- `Modules/README.md` — 移除 PlatformBase.cs 和 Platforms/ 子目录引用，更新 v4.5 特性描述为配置驱动
- `ZDProjects/README.md` — 移除 RedditModule_OwnCode.cs 和 InstagramModule_OwnCode.cs 文件列表条目，更新 v4.5 特性描述
- `.omo/context.md` — 清除幽灵文件引用（BabyCenterModule.cs 等）
- `Modules/Core/README.md` — 移除 PlatformBase 引用

**保留**:
- `Modules/Core/RateLimiter.cs` — 通用速率限制模块，已在 ModuleLoader 编译列表中就绪，待未来接入主链

## [4.5.16] - 2026-03-07

### 📚 Docs 文档体系优化

- `.omo/current-task/plan.md`
  - 登记本次 `Docs` 文档体系优化计划，明确 L1 文件顺序、验证顺序与 Gate 命令
- `.omo/decisions_架构决策.md`
  - 新增 `ADR-014`，明确 `ConfigGuide` 作为新人施工文档、`TechManual` 作为架构参考文档的双层职责
- `.omo/layers/l1-project.yaml`
  - 补充 `文档体系治理` 项目级操作，并更新项目层最后更新时间
- `Docs/DOCS_RULES.md`
  - 同步 `Platforms/Reddit_TestGuide_Reddit测试指南.md` 到文档清单
  - 新增 README 清单同步与“施工文档必须与真实代码一致”的约束
- `Docs/README.md`
  - 重写文档导航定位，区分新人施工、架构参考、平台指南和测试指南
  - 补上 Reddit 测试指南索引与更适合新人的阅读顺序
- `Docs/ConfigGuide_配置指南.md`
  - 重写 ZennoDroid 新人施工路径：最小变量集、必建动作块、复制来源文件、条件表达式与首次运行闭环
  - 修正文档口径：`current_platform` 为运行时输出，`SessionRunner` 成功返回值为 `SUCCESS`
  - 修正帮助链接、变量说明和 `action_count` 语义，删除失真的旧变量/旧死链口径
- `Docs/TechManual_技术手册.md`
  - 修正历史来源说明、Docs 文件结构、SessionRunner 主链说明和输出变量说明
  - 明确旧模块模式不再作为成功回退路径，并修正测试章节编号重复问题
- `Docs/PlatformTemplate_平台模块模板.md`
  - 改为“平台接入文档模板”，强调配置驱动优先与 `Docs/Platforms/` 命名规则
  - 修正 intent 映射示例、回退链示例与相关链接
- `Docs/Platforms/BabyCenter_APP_Guide_平台指南.md`
  - 明确 BabyCenter 当前以配置驱动接入为主
  - 修正核心模块文档链接到 `Docs/TechManual_技术手册.md`
- `Docs/Platforms/Reddit_TestGuide_Reddit测试指南.md`
  - 修正 `Reddit_IntegrationTest.cs` 的真实路径为 `ZDProjects/Tests/Reddit_IntegrationTest.cs`
  - 标明其定位为测试/验证文档
- `Docs/GitWorkflow_Git工作流.md`
  - 补充 Windows PowerShell 环境下的 `Copy-Item` 与 release 脚本调用方式

## [4.5.15] - 2026-03-07

### 📗 根目录说明书补充层级对照表

- `OpenCode_工作流说明书.md`
  - 增加 `L1 / L2 / L3 / L4` 对照表
  - 明确每一层在 DPS_v4.5 中对应什么对象、目录和典型修改
  - 补充快速判断法与常见例子，方便按主模块 / 主 L3 任务使用

## [4.5.14] - 2026-03-07

### 📘 根目录开发说明书

- `OpenCode_工作流说明书.md`
  - 新增根目录说明书，面向日常 OpenCode 开发使用
  - 说明“用户怎么提需求、AI 应怎么执行、Gate 如何参与、一次 session 如何完整走完”
  - 覆盖单主模块 / 单主 L3 的实际开发流程与常用提示词
- `Tools/omo_guard/Invoke-OmoGate.ps1`
  - 修复 `Advance` 阶段写入 `advanced_at_utc` 时因属性缺失导致的状态记录错误

## [4.5.13] - 2026-03-06

### 🚧 .omo Gate 脚本级强制

- `Tools/omo_guard/Invoke-OmoGate.ps1`
  - 新增脚本级 Gate，支持 `Preflight`、`Advance`、`Postflight`
  - `Preflight` 校验 `plan.md`、模块追踪文件、协议引用与顺序约束
  - `Advance` 按 `plan.md` 的文件顺序逐项打卡，阻止乱序修改
  - `Postflight` 校验全部文件已打卡，并可统一执行计划中的验证命令
- `.omo/current-task/plan.md`
  - 升级为 Gate 落地计划，新增 `主模块` 与 `强制运行命令`
- `.omo/layers/EXECUTION_PROTOCOL.md`
  - 增加脚本级 Gate 章节，要求所有任务先跑 `Preflight`、逐项 `Advance`、最后 `Postflight`
- `.omo/modules/WORKFLOW.md`
  - 增加 Gate 执行要求，模块任务必须记录并运行脚本级 Gate
- `.omo/modules/TEMPLATE.md`
  - 新增 `强制运行命令` 与 `Gate 当前状态`
- `.omo/layers/l1-project.yaml`
  - 为 L1 增加 `gate_script`、`gate_phases` 与 Gate 停止条件
- `.omo/layers/l2-module.yaml`
  - 为 L2 增加 `gate_script`、`gate_phases` 与 Gate 停止条件
- `.omo/layers/l3-operation.yaml`
  - 为 L3 增加 `gate_script`、`gate_phases` 与 Gate 停止条件
- `.omo/layers/l4-step.yaml`
  - 为 L4 增加 `gate_script`、`gate_phases` 与 Gate 停止条件
- `.omo.conf`
  - 增加 `mandatory_gate_script`、`mandatory_gate_phases` 与 Gate 强制开关
- `AGENTS.md`
  - 改为强制要求运行 Gate 脚本，不再只靠文档约束

## [4.5.12] - 2026-03-06

### 🧭 .omo 分层治理强制化

- `AGENTS.md`
  - 新增 L1/L2/L3/L4 强制执行规则
  - 明确主层级判定、修改顺序、验证顺序、配置优先与 `CHANGELOG.md` 最后更新
- `.omo.conf`
  - 增加 layered workflow 强制配置，要求先更新层级登记再改实现
- `.omo/current-task/plan.md`
  - 改为本次分层治理落地计划，写明主层级、受影响层级、文件顺序与验证顺序
- `.omo/layers/EXECUTION_PROTOCOL.md`
  - 新增统一执行协议，规定 `L1 -> L2 -> L3 -> L4` 修改顺序和 `L4 -> L3 -> L2 -> L1` 验证顺序
- `.omo/modules/WORKFLOW.md`
  - 重写为模块追踪与分层落地流程，要求先计划、后登记、再实现
- `.omo/modules/TEMPLATE.md`
  - 扩展模块模板，加入主层级、受影响层级、强制文件顺序、强制验证顺序
- `.omo/layers/l1-project.yaml`
  - 补充 L1 级修改/验证顺序与架构硬规则
- `.omo/layers/l2-module.yaml`
  - 补充 L2 级模块追踪、修改顺序、验证顺序与编码边界
- `.omo/layers/l3-operation.yaml`
  - 补充 L3 级 action / intent / operation 契约修改顺序与验证要求
- `.omo/layers/l4-step.yaml`
  - 补充 L4 级 step / primitive 修改顺序、回写要求与最小改动原则

## [4.5.11] - 2026-03-06

### 🔧 CODEX 审核修复（编译链路与运行一致性）

- `Modules/Core/ZennoDroidAdapter.cs`
  - 重建损坏的适配器源码，恢复统一执行、重试、截图与批量命令接口
- `Modules/Core/ZDResult.cs`
  - 补充 `ScreenshotPath` 兼容属性，修复适配器与视觉验证链路的数据契约
- `Modules/Core/ActionExecutor.cs`
  - 移除对 `ScriptHelpers` 编译期静态调用的依赖，改为本地拟人化辅助方法
  - `find` 新增真实重试支持，`input_text` 兼容 `type`，`swipe` 支持 `start_x/end_x` 别名
  - 选择器解析支持步骤内联 `strategy/value`，修复 BabyCenter 配置无法执行的问题
- `Modules/Core/Intent.cs`
  - 补齐 `IsValid`、`GetParameter`、回退链与便捷构造函数，修复 `IntentTranslator` 契约漂移
- `Modules/SessionRunner.cs`
  - 评论动作执行前自动补齐 `ai_comment_text/comment_text`，避免统一评论链路因变量缺失直接失败
  - `current_post_id` 改为优先使用帖子语义字段构建，降低基于屏幕 bounds 的误判与碰撞
  - `current_post_json` 补充 `body` 等语义字段，`device_app_mapping.json` 支持 `default_platform`
  - `UserStrategy.json` 改读 `decision_balance`，并尊重 `ai_control.enabled`
  - 旧平台模块回退路径改为显式报错，避免缺失 operations 配置时出现“静默成功”
- `Modules/Core/AIService.cs`
  - `ExtractText/ExtractJson` 增加 Gemini/OpenAI 响应包裹自动识别，修复主模型失败后备用模型响应被误解析
- `Modules/Core/AppExplorer_v2.cs`
  - 清理残留噪声标记，修复额外的源码语法损坏
  - AI 分析结果改为先解包文本再抽取 JSON，修复备用 provider 响应下的状态解析失败
- `Modules/Core/JsonHelper.cs`
  - `GetArray()` 支持直接解析数组字符串，修复 `AppExplorer_v2` 等场景传入裸数组时取值为空
- `Modules/WeeklyEvolve.cs`
  - 先提取 AI 文本再抽取 JSON，兼容重试链路返回的不同 provider 响应格式
- `Modules/Core/VisionCorrector.cs`
  - 图像分析响应改为先解包文本再抽取 JSON，提升视觉验证稳定性
- `Modules/Main.cs`
  - `ClearRuntimeData()` 递归清理并备份 `Memory/<device>/<app>/interactions.json`，覆盖结构化记忆目录
- `Modules/MemoryManager.cs`
  - 兼容 `JsonHelper.GetArray()` 的数组返回值，修复结构化记忆读写的编译契约问题
- `Modules/RuleEngine.cs`
  - 兼容 `JsonHelper.GetArray()` 的数组返回值，修复兴趣词/触发词读取的编译契约问题
- `Modules/Extension.cs`
  - 改为按类型名反射注册内置扩展，消除对 `Extensions/DataSources/*.cs` 的编译期硬依赖

## [4.5.10] - 2026-03-04

### 🆕 App Onboarder — 新平台自动接入工具

新增独立 Python CLI 工具 `Tools/app_onboarder/`，用于自动化新 APP 平台接入流程。

#### 1) 工具架构（6 个模块）
- `adb_controller.py` (300 行) — ADB 命令封装：设备连接、UI dump、截图、点击、滑动、拟人化延迟
- `ui_analyzer.py` (483 行) — UI Dump XML 解析引擎：元素查找、底部导航检测、Feed 类型判断、WebView 检测、帖子容器识别、页面分类
- `app_explorer.py` (699 行) — 5 阶段自主探索引擎：首页扫描 → 导航探索 → Feed 分析 → 帖子详情分析 → 交互按钮发现。支持 WebView accessibility nodes 深度分析，启发式失败时截图向用户提问
- `config_generator.py` (1812 行) — 配置生成器：基于探索结果自动生成 PlatformsConfig.json 平台条目、{platform}_operations.json 操作定义、{platform}_e2e_test.ps1 端到端测试脚本
- `test_runner.py` — 测试运行器：执行 E2E 测试 → 解析结果 → 分析失败 → 自动修复 → 重试。支持 5 种修复策略（延迟增加、选择器切换、滚动调整、坐标校准、WebView 等待增加）
- `main.py` — CLI 入口：支持交互模式和命令行参数模式（`--package`/`--key`/`--skip-test`）

#### 2) 核心特性
- **全自动探索**: 无需手动查看 XML dump，工具自主导航 APP 发现 UI 结构
- **WebView 感知**: 自动检测 WebView 页面并分析 accessibility nodes（如 BabyCenter 的点赞/评论按钮）
- **双 Feed 类型**: 支持 ViewPager 水平滑动和 RecyclerView 垂直滚动两种 feed 模式
- **自动修复循环**: E2E 测试失败后自动分析原因并尝试修复配置，最多 3 轮
- **零第三方依赖**: 仅使用 Python 标准库

#### 3) 使用方式
```bash
# 交互模式
python Tools/app_onboarder/main.py

# 命令行模式
python Tools/app_onboarder/main.py --package com.example.app --key example

# 跳过测试
python Tools/app_onboarder/main.py --package com.example.app --key example --skip-test
```

#### 4) 验证
- 使用 BabyCenter 模拟数据通过集成测试
- 生成的 PlatformsConfig.json 条目包含 12 个字段、17 个 UI 选择器
- 生成的 operations.json（示例）可覆盖 WebView 场景，含 `scroll_to_reactions` 等关键步骤
- 生成的 E2E 测试脚本可直接由 PowerShell 执行并进入自动修复循环

#### 5) 实现计划文档
- 新增 `Docs/plans/2026-03-04-app-onboarder.md` — 完整实现计划

### 📚 Docs 一致性审核（2026-03-05）

- 审核并更新 `Docs/` 目录核心说明书，统一到 v4.5.10 基线
- 修复文档索引失效链接与不存在路径引用（如 `子项目调用架构.md`、历史错误文件名链接）
- 校正文档与代码不一致项：BabyCenter 为配置驱动模式、行为档案命名（`speed_demon/casual/deep_reader/distracted`）、`Config/IntentMappings` 路径
- 补充历史文档说明：`FIX_REPORT_2026-02-27.md` 中旧绝对路径属于历史快照，不代表当前仓库路径约定

---

## [4.5.9] - 2026-02-28

### 🔧 代码重构优化

#### 1) SessionRunner 帖子 JSON 构建逻辑封装
- `Modules/SessionRunner.cs`
  - 新增方法 `BuildAndSetPostJsonFromContext()` - 封装原本在 `ExecuteWithUnifiedEngine()` 中的 24 行复杂逻辑
  - `ExecuteWithUnifiedEngine()` 方法简化，调用新方法替代内联代码
  - 提高代码可维护性和可读性
  - 行数变化: 1492 → 1501 (+9 行)

#### 2) .omo 2.0 模块追踪系统
- 新增 `.omo/modules/SessionRunner.md` - SessionRunner 模块追踪文件
- 新增 `.omo/modules/TEMPLATE.md` - 模块追踪模板
- 新增 `.omo/modules/index.md` - 活跃模块索引
- 新增 `.omo/PLANS.md` - 优化计划文档
- 完善 L3/L4 层级操作映射
#### 3) 文件 I/O 错误处理增强
- `Modules/SessionRunner.cs`
  - 为 `LoadUserStrategy()` 方法添加 try-catch 错误处理
  - 为 `LoadIntentMapping()` 方法添加 try-catch 错误处理
  - 确保文件读取失败时使用默认值，不中断流程
  - 符合 .omo 规范：所有 I/O 操作必须包含 try-catch

#### 4) 编译错误修复
- `Modules/SessionRunner.cs`
  - 修复 `LoadUserStrategy()` 方法结构损坏：恢复 `File.Exists()` 条件检查，补全 `balanceJson`/`aiControl` 变量声明
  - 修复 `LoadIntentMapping()` 方法结构损坏：恢复 `File.Exists()` 条件检查，使 try-catch 可达
  - 修复视觉验证块中 `effectiveIntent` 变量未定义的编译错误（CS0103），简化为快速模式逻辑
  - 修复 `ResolveIntentForAction()` 默认映射：`like` 从错误的 `open_post` 修正为 `like_content`

#### 5) L3/L4 映射文档完善
- `.omo/layers/l3-operation.yaml`
  - 新增 op-sr-008 (build-post-json) 操作定义
- `.omo/modules/SessionRunner.md`
  - 更新模块追踪状态为 stable，进度 100%

---


## [4.5.8] - 2026-02-27

### 🔧 CODEX 复扫修复（逻辑与流程）

#### 1) 会话成功判定升级（目标对齐 >95%）
- `Modules/SessionRunner.cs`
  - 新增 `successfulActions` / `skippedActions` 计数
  - 成功判定改为：`success_rate >= 0.95` 且 `successfulActions >= min_successful_actions(默认6)`
  - 新增输出变量：`session_success_rate`, `session_successful_actions`, `session_failed_actions`, `session_skipped_actions`, `action_attempt_count`
  - `action_count` 改为记录成功动作数（不再混入 SKIP）

#### 2) 帖子语义链路修复（ActionExecutor -> SessionRunner -> RuleEngine）
- `Modules/Core/ActionExecutor.cs`
  - `find` 步骤新增节点语义提取（`*_text`, `*_desc`）
  - 新增语义别名同步：`title/caption/body/...` -> `post_title/post_body/post_subreddit/post_upvotes/post_comment_count/post_timestamp`
  - 新增数字标准化（支持 `1.2k`, `3,421`）
- `Modules/SessionRunner.cs`
  - 构建 `current_post_json` 时增加多级兜底字段读取（`post_title`, `*_text` 等）
  - 新增 `body` 字段写入
  - 对 `upvotes/comment_count` 做标准化后再写入 JSON，避免无效数字污染
- `Config/Operations/reddit_operations.json`
  - `like` 增加 `post_title` 采集
  - `open_post` 统一使用 `post_title`
  - `read_post` 统一使用 `post_body`
- `Config/Operations/instagram_operations.json`
  - `like` 增加 `post_title`（来自 `caption_text`）采集
  - `view_post` 语义字段改为 `post_title`
- `Config/Operations/babycenter_operations.json`
  - `open_post` 统一使用 `post_title`
  - `read_post` 统一使用 `post_body`
  - `like` 增加 `post_title` 采集

#### 3) 安全与配置完整性修复
- `Modules/Main.cs`
  - 新增 `device_id` 安全校验（`ValidateDeviceId`）
  - 配置检查从单文件扩展为 `AIConfig + StageConfig + BehaviorConfig`
  - 增加配置读取为空时的错误返回

#### 4) I/O 错误处理与维护鲁棒性
- `Modules/Core/CoreHelper.cs`
  - `EnsureDir/ReadFile/WriteFile/AppendFile` 增加 try-catch 与日志
- `Modules/Main.cs`
  - `ClearRuntimeData` 中的备份/删除改为单文件异常隔离，避免一处失败中断全流程
- `Modules/Maintenance.cs`
  - 日志/记忆/备份清理循环增加单文件 try-catch
  - 磁盘空间检查失败不再静默吞掉，改为告警日志
  - `DriveInfo` 路径获取增强（优先 `Path.GetPathRoot`）

#### 5) 返回码与意图映射一致性
- `Modules/ReportGen.cs`
  - `SKIPPED` 统一为 `SKIP`
- `Modules/SessionRunner.cs`
  - 默认意图映射修复：`like -> like_content`（原为错误的 `open_post`）

#### 6) 源文件可编译性修复
- `Modules/Core/AppExplorer_v2.cs`
  - 清理行前缀噪声（`#XX|`），恢复为可读源码文本

#### 7) 文档与上下文同步（v4.5.8）
- 同步更新 `README.md`、`UPGRADE.md`、`Docs/README.md`、`Docs/CopyPaste_Setup.md`、`Docs/DPS_v4.5_完整配置手册.md`、`Docs/FIX_REPORT_2026-02-27.md`、`Docs/QuickSetup_Flowchart.md`、`Docs/BabyCenterModule.md`、`FINAL_DELIVERY_REPORT.md`
- 修正文档口径：`action_count` 为成功动作数，新增 `action_attempt_count/session_success_*` 统计变量说明
- `SKIPPED` 相关文档表述统一为 `SKIP`
- `.omo/decisions.md` 清理前缀噪声，`.omo/context.md` / `.omo/PROJECT.md` 同步到 v4.5.8 状态



## [4.5.7] - 2026-02-27

### 🔧 CODEX 修复 - 运行时稳定性与平台接入

#### 修复内容（由 CODEX 完成）

#### 修复点 1: ActionExecutor 运行稳定性修复
- **新增**: `refresh_layout` 步骤已添加到 `ExecuteStep` 分发（第186行）
  - 修复浏览/返回流程中的布局刷新步骤未执行的问题
- **修复**: `call_operation` 递归时上下文仅在顶层（CallDepth==0）清空
  - 解决递归调用链丢失步骤上下文的问题
- **新增**: `SyncLegacyContext` 方法（第1123行）
  - 将 `OperationContext.Variables` 同步回静态 `_context`
  - 修复 `SessionRunner` 无法稳定读取 `ActionExecutor` 上下文的问题
- **清理**: 移除非当前运行链的 Intent 执行段
  - 降低动态编译失败概率，保持现有流程稳定

#### 修复点 2: SessionRunner 评估门控修复
- **修复**: 空语义帖子处理（第898-931行）
  - 仅在存在语义字段（title/subreddit/upvotes/comments/timestamp）时写入 `current_post_json`
  - 避免无内容帖子被 `RuleEngine` 误判为低分并持续拒绝互动

#### 修复点 3: Reddit / Instagram 意图映射修复
- **新增**: `like_content` 意图定义
  - 修复 `like` 映射到 `like_content`（之前错误映射到 `open_post`）
  - 行为现在符合"值得点赞再点赞"的目标
- **新增**: `follow_entity` 意图定义
  - 补充缺失的 `follow_entity` 映射到 `follow` 操作
- **新增**: `share_content` 意图定义
  - 补充缺失的 `share_content` 映射到 `share` 操作

#### 修复点 4: Instagram 点赞回退链增强
- **修改**: `instagram_operations.json` 中 `like` 操作配置
  - 使用 `if_exists(like_button)` 检查点赞按钮是否存在
  - `else` 分支调用 `call_operation(double_tap_like)` 作为回退
  - UI 变化时容错能力提升
- **验证**: `double_tap_like` 操作存在且配置正确
  - 支持双击图片点赞（Instagram 特有功能）

#### 修复点 5: BabyCenter 三件套接入
- **新增**: `PlatformsConfig.json` 中 `babycenter` 平台配置（第287行）
  - package: `com.babycenter.pregnancytracker`
  - ui_selectors: post_unit, post_title, post_body, like_button, comment_button, etc.
  - rate_limits: 80/hour, 24 likes/hour, 12 comments/hour
  - page_signatures: feed, post_detail, comment
- **新增**: `Config/Operations/babycenter_operations.json`
  - 6 个操作: browse, open_post, read_post, like, comment, back_to_feed
  - 所有操作包含 refresh_layout 步骤和 humanized 参数
- **新增**: `Config/IntentMappings/babycenter_intents.json`
  - 意图映射: browse_feed, open_post, read_post, like_content, reply_post
  - action_to_intent: browse, read_post, like, comment, post, follow, share
- **修改**: `Apps.json` 中 `babycenter.enabled = true`
  - data_path: `Data/BabyCenter`
  - primary_communities: Pregnancy, Baby, Toddler
- **新增**: `device_app_mapping.json` 中 `device_004 -> babycenter` 映射

#### 修复点 6: ZDProjects 兼容性修复
- **修复**: 所有 `*_OwnCode.cs` 文件添加 `OperationContext.cs` 依赖
  - DailyUpdate_OwnCode.cs
  - Extension_OwnCode.cs
  - Initializer_OwnCode.cs
  - Main_OwnCode.cs
  - Maintenance_OwnCode.cs
  - PersonaCreate_OwnCode.cs
  - ReportGen_OwnCode.cs
  - SessionRunner_OwnCode.cs
  - StateSaver_OwnCode.cs
  - WeeklyEvolve_OwnCode.cs
  - 减少动态编译时"类型找不到"的风险

### 📝 新增测试脚本
- **新增**: `ZDProjects/Tests/Reddit_E2E_Test.cs` - Reddit E2E 测试脚本
  - 验证 refresh_layout 步骤
  - 验证 like_content/follow_entity/share_content 意图
  - 完整工作流模拟
- **新增**: `ZDProjects/Tests/Instagram_E2E_Test.cs` - Instagram E2E 测试脚本
  - 验证 if_exists(like_button) 配置
  - 验证 call_operation(double_tap_like) 回退链
  - 验证 double_tap_like 操作
- **新增**: `ZDProjects/Tests/BabyCenter_E2E_Test.cs` - BabyCenter E2E 测试脚本
  - 验证平台配置、操作配置、意图映射
  - 验证应用开关和设备映射

### ⏳ 待用户验证（运行时测试）
- ZennoDroid 实机/模拟器 E2E 测试
  - Reddit: 首页 → 选帖 → 阅读 → 判断点赞 → 点赞 → 返回
  - Instagram: 同上 + like 回退链测试
  - BabyCenter: selectors 与页面签名需要按实际 UI 微调

### ✅ 验证结果
- 所有 JSON 文件通过语法校验
- 关键映射一致性检查通过
- 代码验证通过（C# 5.0 兼容）
- 状态: ⏳ READY - 等待 ZennoDroid 运行时测试

---




## [4.5.6] - 2026-02-27

### 🚀 Universal Framework - 架构完整重构

#### 核心理念
- **DPS (大脑)**: 决策层 - 感知、记忆、决策、验证
- **ZennoDroid (手)**: 执行层 - 元素定位、拟人化执行、异常处理
- **翻译层**: IntentTranslator 将高层意图翻译为物理命令

### 🛠️ 架构重构 - DPS 与 ZennoDroid 分层

#### Phase 1: 重构 ActionExecutor.cs
- **新增**: `Modules/Core/Intent.cs` - 操作意图抽象类
  - 定义高层操作意图，描述"做什么"而不是"怎么做"
  - 支持从 JSON 步骤构造 Intent 对象
  - 168 行，C# 5.0 兼容
- **新增**: `ActionExecutor.ExecuteIntent()` 方法
  - 基于 Intent 对象的执行方法
  - 复用现有的 Step* 方法，保持向后兼容
  - 渐进式重构策略：保留现有执行逻辑，只改变输入格式
- **新增**: `ActionExecutor.GetContext()` 方法
  - 供 SessionRunner 读取上下文变量（如 post_title, post_subreddit）
  - 兼容现有代码调用

#### Phase 2: 简化 Manifest - 删除物理参数
- **修改**: `Config/Operations/reddit_operations.json`
  - 删除 `duration` 参数（物理参数）
  - 保留 `selector`, `direction`, `distance`（翻译层需要）
- **修改**: `Configs/Manifests/instagram.json`
  - 删除所有 `duration_ms` 参数（4 处）
  - 保留选择器和高层参数

#### Phase 3: 添加视觉验证层
- **新增**: `SessionRunner.cs` 视觉验证逻辑
  - 在关键操作（like, comment）执行后添加截图验证
  - 调用 `VisionCorrector.AnalyzeAndRecover()` 验证结果
  - 验证失败时标记操作为失败，记录失败原因
  - 通过 `behavior_config_json` 中的 `vision_verification_enabled` 控制开关

#### Phase 4: 设计新的 Manifest 格式
- **新增**: `Configs/Manifests/manifest_schema.yaml` - Manifest Schema v2.0
  - 定义 `capabilities`：APP 能做什么
  - 定义 `states`：如何识别当前状态（视觉 + UI 特征）
  - 定义 `intent_mappings`：意图翻译为操作
  - 定义 `rate_limits`：速率限制
  - 91 行，详细注释
- **新增**: `Configs/Manifests/instagram_v2.yaml` - Instagram Manifest v2.0
  - 11 个 capabilities（browse_feed, like_post, comment, 等）
  - 7 个 states（home_feed, post_detail, profile, 等）
  - 每个 state 包含 visual_markers + ui_signatures + gemini_prompt
  - 11 个 intent_mappings，包含 fallback_intents
  - 8 个 rate_limits，包含 per_hour + cooldown_seconds + per_day
  - 223 行，完整实现

#### Phase 5: DPS 架构完整实现
- **新增**: `Modules/Core/ZDCommand.cs` - ZennoDroid 命令类
  - 定义物理操作的命令类型（Tap, Swipe, SendText, 等）
  - 封装坐标、持续时间、文本内容等物理参数
  - 支持人性化执行标志和重试配置
  - 333 行，C# 5.0 兼容
- **新增**: `Modules/Core/ZDResult.cs` - ZennoDroid 执行结果类
  - 统一的执行结果格式（Success, FailedRetryable, FailedFatal, Skipped）
  - 支持扩展数据和错误追踪
  - 提供与旧格式的兼容转换方法
  - 311 行，C# 5.0 兼容
- **新增**: `Modules/Core/ZennoDroidAdapter.cs` - ZennoDroid API 适配器
  - 封装所有 ZennoDroid API 调用（Input.Tap, Input.Swipe, 等）
  - 统一错误处理和重试机制
  - 支持 GetLayout 和 Screenshot 操作
  - 408 行，C# 5.0 兼容
- **新增**: `Modules/Core/IntentTranslator.cs` - 意图翻译器
  - 将 Intent 翻译为 ZDCommand
  - 保留元素定位逻辑（SelectorEngine）和坐标计算逻辑（ParseBounds）
  - 支持回退链翻译
  - 支持从 stepJson 快速翻译
  - 472 行，C# 5.0 兼容
- **修改**: `ZDProjects/ModuleLoader.cs`
  - 添加新文件到编译列表：Intent.cs, ZDCommand.cs, ZDResult.cs, ZennoDroidAdapter.cs, IntentTranslator.cs
  - 确保所有新文件被动态编译加载

#### 架构图
```
DPS (大脑层)
├─ Intent ("我想点赞这个帖子")
├─ IntentTranslator (翻译：意图 → 命令)
│   └─ 输出: ZDCommand ("点击坐标 (540, 1800)")
└─ VisionCorrector (视觉验证)

ZennoDroid (手层)
├─ ZennoDroidAdapter (API 封装)
│   ├─ 输入: ZDCommand
│   └─ 输出: ZDResult
├─ SelectorEngine (元素定位)
└─ ScriptHelpers (人性化执行)
```

#### 关键改进
1. **清晰的架构边界**: DPS 不再直接调用 ZennoDroid API
2. **可测试性**: Intent/ZDCommand/ZDResult 都是纯数据结构
3. **可扩展性**: 新增平台只需定义 Manifest 和翻译规则
4. **向后兼容**: 保留 ActionExecutor 的旧接口，渐进式迁移
5. **C# 5.0 兼容**: 所有新代码使用 C# 5.0 语法

### 📝 架构决策
- **ADR-011**: Intent-Based Execution
  - 状态: 已接受
  - 决策: 引入 Intent 抽象层，分离决策与执行
  - 理由: 提高代码可读性，便于未来添加视觉验证层
  - 后果: 保持向后兼容，现有代码无需修改
- **ADR-012**: Vision Verification Layer
  - 状态: 已接受
  - 决策: 在关键操作后添加 Gemini Flash 截图验证
  - 理由: 提高操作可靠性，及时发现执行失败
  - 后果: 增加每次操作 2-3 秒延迟，但显著提高成功率
- **ADR-013**: Manifest v2.0 Format
  - 状态: 已接受
  - 决策: 重新设计 Manifest 格式，分离语义和物理参数
  - 理由: 现有格式混合了 DPS 层和 ZennoDroid 层的信息
  - 后果: 旧格式仍然支持，新格式逐步迁移

### ✅ 验证结果
- **代码结构**: Intent.cs 创建成功，168 行
- **向后兼容**: 现有 Execute() 方法保留，新增 ExecuteIntent() 方法
- **配置简化**: 删除所有 duration/duration_ms 参数
- **视觉验证**: SessionRunner 集成 VisionCorrector
- **新格式**: manifest_schema.yaml + instagram_v2.yaml 创建成功
- **状态**: ✅ READY - 可以进入运行时测试

### 📚 文档更新
- **新增**: `ARCHITECTURE_REFACTORING_REPORT.md` - 架构重构报告
  - 详细分析当前架构错误
  - 提供 7 个 Phase 的重构计划
  - 428 行，包含代码示例和流程图

---

## [4.5.5] - 2026-02-27

### 🐛 关键修复 - 编译错误修复

#### ActionExecutor.cs
- **修复**: 删除第279-308行重复的 StepTap 代码块（CS0116 错误）
- **修复**: 删除第342-367行重复的 StepSwipe 代码块（CS0116 错误）
- **修复**: 删除第408-443行重复的 StepScroll 代码块（CS0116 错误）
- **修复**: 添加第1116行 `public static string GetContextVariable(string key)` 方法签名
- **修复**: 添加第1132行 `public static void SetContextVariable(string key, string value)` 方法签名
- **修复**: 添加第1145行 `public static void ClearContext()` 方法签名
- **结果**: 所有 CS0116 编译错误已解决，代码结构完整

#### ModuleLoader.cs
- **修复**: 在 coreFiles 数组中添加 `RateLimiter.cs`
- **结果**: 所有新模块现已包含在动态编译系统中

#### AppExplorer.cs
- **修复**: 第720行 `input.PressBack()` 改为 `input.Shell("input keyevent 4")`
- **结果**: 使用 ZennoDroid 标准 API，避免 CS0117 错误

#### NavigationResolver.cs
- **修复**: 第94行 `JsonHelper.GetJsonValue` 改为 `JsonHelper.Get`
- **修复**: 第102-103行 `JsonHelper.ParseJsonArray` 改为 `JsonHelper.GetArray`
- **结果**: 使用存在的 JsonHelper API，避免 CS0117 错误

#### AIConfig.json
- **修复**: 模型名称从 `gemini-3-flash-preview` 改为 `gemini-3-flash`
- **结果**: 使用正确的 Gemini API 模型名称

#### Initializer.cs
- **新增**: 在项目初始化时调用 `VisionCorrector.Init`
- **新增**: 自动创建 `Screenshots/` 目录
- **结果**: VisionCorrector 模块现已正确初始化，可以使用

#### instagram.json
- **修复**: 添加导航路径 `home → notifications`（第197行）
- **修复**: 添加导航路径 `home → direct_messages`（第198行）
- **修复**: `like_feed_posts` 速率限制从 15/min 调整为 30/hour（第233行）
- **修复**: `cooldown_seconds` 从 5秒调整为 120秒
- **结果**: 符合 Instagram 安全限制，所有屏幕可达

### ✅ 验证结果
- **编译验证**: 所有 11 个高优先级修复项已通过验证
- **代码结构**: ActionExecutor.cs 共 28 个方法签名，无孤立代码块
- **API 兼容性**: 所有 API 调用已修正为 ZennoDroid 标准 API
- **配置完整性**: 所有配置文件已修正，符合框架规范
- **状态**: ✅ READY - 可以进入 ZennoDroid 运行时测试

---

## [4.5.4] - 2026-02-27

### ✨ 新功能 - Universal APP Automation Framework

#### 核心模块扩展
- **新增**: `NavigationResolver.cs` - BFS 最短路径导航算法
  - 根据 Manifest navigation.edges 计算页面间最短路径
  - 支持图结构加载、路径查询、直接到达检查
  - 321 行，C# 5.0 兼容

#### 模块集成
- **更新**: `ModuleLoader.cs` coreFiles 数组
  - 添加: ManifestLoader.cs, NavigationResolver.cs, VisionCorrector.cs, AppExplorer.cs, RateLimiter.cs
  - 所有新核心模块现已纳入动态编译系统

#### 配置文件
- **新增**: `Configs/Manifests/instagram.json` - Instagram 完整 Manifest
- **新增**: `Configs/Manifests/reddit.json` - Reddit 完整 Manifest
- **新增**: `Configs/Manifests/template.json` - Manifest 模板

### 📝 技术细节
- **ActionExecutor.cs** 新原语已验证: call_operation, if_exists, foreach, random_pick
- **C# 5.0 语法兼容性验证通过** - 无 $"", ?., nameof 等现代语法
- 所有核心模块已集成到动态编译系统

---

## [4.5.4] - 2026-02-27

### 🔧 稳定性修复

#### ActionExecutor.cs
- **修复** `refresh_layout` 步骤未分发问题（operations 中该步骤此前会被当作未知动作跳过）
- **修复** 递归 `call_operation` 时上下文被误清空的问题（仅顶层执行清理 context）
- **修复** 旧接口兼容：执行后同步 `OperationContext` 到静态上下文，恢复 `SessionRunner` 的 `ActionExecutor.GetContext()` 读取能力
- **清理** 移除不在当前编译链中的 Intent 相关执行段，避免运行时动态编译缺失依赖

#### SessionRunner.cs
- **修复** 当 ActionExecutor 未提供语义字段时，`current_post_json` 不再写入空内容对象，避免 RuleEngine 将“空帖子”误判并持续拒绝互动

### 🌐 多平台配置完善

#### IntentMappings
- **修复** `reddit_intents.json` / `instagram_intents.json` 中 `follow_entity` 与 `share_content` 未定义导致的意图回退问题
- **调整** `like` 动作映射到 `like_content`（直接执行 like 操作，不再错误映射为 open_post）
- **新增** `babycenter_intents.json`（支持 browse/read/like/comment 主链路）

#### Operations
- **增强** `instagram_operations.json` 的 like 操作：新增 `if_exists + call_operation(double_tap_like)` 回退链
- **新增** `babycenter_operations.json`（browse/open_post/read_post/like/comment/back_to_feed）

#### Platforms / Apps / Device Mapping
- **新增** `PlatformsConfig.json` 的 `babycenter` 平台配置（selectors/page_signatures/rate_limits）
- **更新** `Apps.json` 中 babycenter `enabled: true`
- **新增** `device_app_mapping.json` 示例映射 `device_004 -> babycenter`

### 🧩 ZDProjects 兼容性
- **更新** 各 `*_OwnCode.cs` 核心依赖列表，补充 `OperationContext.cs`，避免 `ActionExecutor` 新签名在不同入口编译失败

## [4.5.3] - 2026-02-26

### ✨ 新功能

#### ActionExecutor.cs - call_operation 原语实现
- **新增**: `call_operation` 原语，支持操作组合和递归调用
  - 在 ExecuteStep 方法的 switch-case 中新增 `case "call_operation"`
  - 实现 ExecuteCallOperation 方法，支持递归调用其他操作
  - 递归深度限制为 5 层（通过 OperationContext.CanEnterCall 检查）
  - 使用 context.EnterCall() 和 context.ExitCall() 管理递归深度
  - 异常处理确保递归深度正确退出
- **重构**: Execute 方法签名更新
  - 新增 OperationContext 参数，替代静态 _context 字典
  - 支持多设备并发安全执行
- **重构**: 所有步骤方法更新以使用 OperationContext
  - StepFind: 使用 context.SetVariable 存储查找结果
  - StepTap: 使用 context 参数传递
  - StepSetVar: 使用 context.GetVariable 读取上下文变量
  - ExecuteForeach: 使用 context.SetVariable 设置循环变量
  - ExecuteIfExists: 传递 context 到分支执行
  - ExecuteRandomPick: 传递 context 到子步骤
  - ResolveTapTarget: 使用 context.GetVariable 读取坐标
- **兼容性**: 保留静态 GetContext/SetContext/ClearContext 方法（已标记为废弃）

### 📝 技术细节
- 符合 C# 5.0 语法要求（无 $""、?.、nameof）
- 遵循现有代码风格（JsonHelper 用法、错误处理模式）
- 详细注释说明递归机制和深度控制

---

## [4.5.2] - 2026-02-17

### 🐛 Bug 修复 (P0 级别)

#### ModuleLoader.cs - 缓存管理优化
- **修复**: 缓存失效不检测依赖文件删除
  - 引入 `CacheEntry` 结构化缓存条目（方法 + 依赖快照 + 访问时间）
  - 实现依赖文件删除检测（对比缓存快照与当前文件列表）
  - 添加路径规范化函数避免大小写/分隔符导致重复键
- **修复**: 缓存无界增长导致内存泄漏
  - 实现 LRU 缓存淘汰机制（上限 32 个条目）
  - 添加 `EvictOldestCacheEntry` 自动清理旧缓存

#### SessionRunner.cs - 并发安全性
- **修复**: 静态状态跨会话串扰
  - 移除静态 `_random`，改用 `[ThreadStatic]` 的 `_threadRandom`
  - 添加 `GetRandom()` 方法（线程安全的随机数生成器）
  - 创建 `SessionState` 类封装疲劳模型状态
  - 所有疲劳变量改为 `SessionState` 实例字段
- **修复**: 配置异常导致运行时崩溃
  - `GetActionDelay` 添加 min/max 边界校验
  - 添加溢出保护（上限 3600 秒）
  - 自动修正 min/max 颠倒情况

#### MemoryManager.cs - 并发写入保护
- **修复**: 并发写入导致数据丢失
  - 添加文件级锁机制（`GetFileLock` 按路径获取锁对象）
  - `RecordInteractionWithScore` 使用 `lock` 包裹读改写操作

#### DailyUpdate.cs - 数据完整性
- **修复**: 未来 conception_date 产生负孕周
  - 添加 `totalDays < 0` 检测（跳过更新并记录错误）
  - 添加负值保护（weeks/days 归零）
  - 修改正则表达式支持负数匹配
- **新增**: 产后阶段转换逻辑
  - PP0 → PP1（产后 3 个月）
  - PP1 → NP（产后 12 个月）
  - 基于 `delivery_date` 自动计算并转换阶段

### 📝 技术细节
- 所有修复严格遵守 C# 5.0 语法约束
- 保持原有代码风格和注释规范
- 通过语法验证和关键点验证

---

## [4.5.1] - 2026-02-13

### 🔧 Config-Driven Selectors (Phase 3)
- **修复** `RedditModule.cs` / `InstagramModule.cs` - 嵌套 JSON selector 对象解析 bug（`GetJsonValue` 无法解析嵌套对象，导致始终使用默认值）
- **新增** `GetSelectorValue` 辅助函数 - 正确提取 `PlatformsConfig.json` 中 `ui_selectors` 的嵌套 `value` 字段
- **新增** `RedditModule.cs` 导出 selector 变量到 ZD 变量 (`reddit_sel_*`)，供 ZDProjects 脚本使用
- **修复** `InstagramModule.cs` Like 操作中硬编码的 `media_image` fallback，改为 `cfg_mediaImage` 从配置读取
- **修改** ZDProjects 脚本 (`Reddit_Browse.cs`, `Reddit_Like.cs`, `Reddit_Comment.cs`, `Reddit_ReadPost.cs`) - 从 ZD 变量读取 selectors，不再硬编码

### ✅ Extension Integration (Phase 4 验证)
- **确认** `Extension.cs` 已完全重构为使用 `ExtensionManager`（`RegisterBuiltinExtensions` + `LoadFromRegistry` + `RunCategory`）
- **确认** `ExtensionManager.cs`, `IExtension.cs`, `ExtensionsRegistry.json` 完整且集成
- **确认** `IPLocationExtension.cs`, `WeatherExtension.cs` 独立扩展类正常工作

---

## [4.5.0] - 2026-02-07

### 🌐 多平台支持
- **新增** Reddit 平台支持 (`Platforms/Reddit/RedditModule.cs`)
- **新增** Instagram 平台支持 (`Platforms/Instagram/InstagramModule.cs`)
- **新增** 平台配置文件 `Config/PlatformsConfig.json`
- **新增** 设备应用映射 `Config/device_app_mapping.json`

### 🧩 Core Modules
- **新增** `Core/HumanizationEngine.cs` - 人性化行为引擎 (4 种配置文件)
- **新增** `Core/UILocator.cs` - 多策略 UI 元素定位器
- **新增** `Core/ErrorRecovery.cs` - 错误恢复机制 (指数退避)
- **新增** `Core/PlatformBase.cs` - 平台基类接口

### 📚 Documentation
- **新增** `Docs/GETTING_STARTED.md` - 新人入门指南
- **新增** `Docs/QuickSetup_Flowchart.md` - 快速配置流程图
- **新增** `Docs/CopyPaste_Setup.md` - 复制粘贴配置手册
- **新增** `Docs/MultiPlatformFramework.md` - 多平台框架文档
- **新增** `Docs/PersonaSchema_MultiPlatform.md` - 多平台画像 Schema
- **更新** 所有文档版本号升级至 v4.5

### 🔧 架构改进
- **新增** 混合架构模式 - 共享核心框架 + 平台独立模块
- **新增** 相对坐标系统 (百分比) - 多分辨率适配
- **新增** 速率限制系统 - Reddit 120/小时, Instagram 60/小时

---

## [4.1.0] - 2026-02-05

### 🚀 性能优化

#### ModuleLoader.cs
- **新增** 静态编译缓存机制，避免重复编译
- **新增** 文件时间戳检测，仅在源码变更时重新编译
- **新增** 线程安全的缓存访问 (`lock`)
- **性能** 第二次运行从 ~500ms 降至 <10ms

### 🔧 架构改进

#### JsonHelper.cs (完全重写)
- **重写** 使用栈式状态机实现健壮的 JSON 解析器
- **修复** 嵌套对象中同名键的正确匹配（深度感知）
- **修复** 转义字符处理（包括 `\"` 和 `\\`）
- **修复** Unicode 转义序列 `\uXXXX` 完整支持
- **新增** `GetArrayElement(arrayJson, index)` - 按索引获取数组元素
- **新增** `IsValidJson(json)` - JSON 格式验证
- **新增** `CreateArray(values)` - 创建 JSON 数组

#### CoreHelper.cs
- **重构** `JGet/JGetNested/JSet` 现在委托给 `JsonHelper`
- **移除** 重复的 JSON 解析逻辑

#### AIService.cs
- **改进** 使用 `JsonHelper` 解析 API 响应
- **新增** API 错误检测（检查响应中的 `error` 字段）
- **改进** Gemini/OpenAI 响应解析更加健壮

### 📊 测试验证

所有修改通过以下测试用例：
- 嵌套对象同名键: `{"data": {"name": "inner"}, "name": "outer"}` → 正确返回 `"outer"`
- 转义引号: `{"msg": "He said \"hello\""}` → 正确解析
- Unicode: `{"text": "\u0048\u0065\u006c\u006c\u006f"}` → 返回 `"Hello"`
- 嵌套路径: `user.profile.name` → 正确遍历

---

## [4.0.2] - 2026-02-04

### 🐛 Bug 修复

#### JsonHelper.cs
- **修复** `Get` 方法现在是上下文感知的，不会错误匹配字符串值中的键名
- **修复** `Unescape` 方法现在支持 Unicode 转义序列 `\uXXXX`

#### CoreHelper.cs
- **修复** `WriteFileAtomic` 添加异常处理，当 `.bak` 文件被锁定时回退到直接覆盖
- **新增** `CountOccurrences(text, pattern)` - 统一的字符串计数方法
- **新增** `ValidateDeviceId(deviceId)` - 防止路径遍历攻击的安全验证
- **新增** `GetSafeDeviceId(deviceId, defaultValue)` - 安全获取设备ID

#### WeeklyEvolve.cs
- **修复** AI 返回的进化建议现在会实际应用到画像
- **新增** 解析 `changes` 数组并应用字段修改
- **新增** 进化前自动备份画像
- **新增** 设备ID安全验证
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### Extension.cs
- **修复** 配置检查逻辑，正确读取 `extensions.ip_location.enabled` 和 `extensions.weather.enabled`
- **修复** 使用 `JsonHelper.ExtractObject` 替代不可靠的 `JGet` 检查

#### ReportGen.cs
- **修复** 文件名一致性：检查和保存都使用 `{date}_weekly.json`
- **新增** 设备ID安全验证
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### Maintenance.cs
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### StateSaver.cs
- **新增** 设备ID安全验证
- **移除** 未使用的 `SaveMemory` 方法（记忆由 SessionRunner 保存）
- **修复** 路径拼接一致性

---

## [4.0.1] - 2026-01-31

### 🔧 动态配置支持
所有模块已更新为从配置文件动态读取参数，不再使用硬编码值。

#### AIService.cs
- **新增** `CallWithRetry(prompt, aiConfigJson)` - 自动重试 + 备用模型
- **新增** `CallPrimary/CallFallback/CallBackup` - 分别调用三个模型
- **新增** `CallOpenAICompatible` - 支持自定义 base_url
- **修改** 所有参数从 `AIConfig.json` 动态读取：
  - model, api_key, base_url
  - timeout_ms, max_tokens, temperature

#### JsonHelper.cs
- **新增** `ExtractObject(json, key)` - 提取嵌套对象
- **新增** `ExtractArray(json, key)` - 提取数组

#### PersonaCreate.cs
- **修改** 使用 `AIService.CallWithRetry` 替代 `CallGemini`
- **修改** 自动从文件加载 AI 配置（如变量为空）
- **删除** 废弃的 `ExtractApiKey` 方法

#### WeeklyEvolve.cs
- **修改** 使用 `AIService.CallWithRetry` 替代 `CallGemini`
- **修改** 自动从文件加载 AI 配置（如变量为空）
- **删除** 废弃的 `ExtractApiKey` 方法

#### SessionRunner.cs
- **修改** 动作权重从 `BehaviorConfig.json` 读取
- **修改** 打字速度从配置的 typing 节读取
- **修改** 动作延迟从配置的 duration_sec_min/max 读取
- **修改** 会话时长限制从配置读取

#### Maintenance.cs
- **新增** 支持从 `MaintenanceConfig.json` 读取保留期限
- **修改** 日志/记忆/备份保留天数可配置

---

### ⬆️ 版本升级支持（增强版）
新增 `force_regenerate` 变量，解决源码更新后运行时数据不同步问题。

#### Main.cs
- **新增** 读取 `force_regenerate` 变量（true/1/yes）
- **新增** `ClearRuntimeData()` 方法，统一清理所有运行时数据
- **新增** 启用时清理以下内容：
  - 画像文件 `Persons/{device_id}.json`
  - 记忆文件 `Memory/{device_id}/*.json`
  - 报告文件 `Reports/{device_id}/*.json`
- **新增** 所有文件备份到 `Backups/Upgrade_{date}/`
- **新增** 清空缓存变量 `persona_json`, `session_plan_json`
- **新增** 强制执行每日更新
- **新增** 执行完成后自动重置 `force_regenerate = false`

#### 备份目录结构
```
Backups/
└── Upgrade_2026-01-31/
    ├── persona_device_001.json
    ├── Memory_device_001/
    │   ├── 2026-01-30.json
    │   └── 2026-01-29.json
    └── Reports_device_001/
        └── 2026-01-31_weekly.json
```

---

### 📄 新增配置文件

#### Config/MaintenanceConfig.json
```json
{
    "log_retention_days": 30,
    "memory_retention_days": 180,
    "backup_retention_days": 30
}
```

---

### 📋 ZennoDroid 变量更新

新增需要在 ZD 中创建的变量：

| 变量名 | 类型 | 初始值 | 用途 |
|--------|------|--------|------|
| `force_regenerate` | 文本 | `false` | 设为 `true` 强制重新生成所有内容 |

---

## 升级指南

### 从旧版本升级

1. **复制最新代码**
   - 将 `Modules/` 目录下所有 `.cs` 文件覆盖
   - 将 `Config/MaintenanceConfig.json` 复制到项目

2. **更新 ZD 变量**
   - 在 ZennoDroid 中新增 `force_regenerate` 变量，初始值 `false`

3. **强制重新生成（可选）**
   - 如需重新生成画像等内容，设置 `force_regenerate = true`
   - 运行 Main 模块，系统会自动备份旧内容并重新生成
   - 完成后变量自动重置为 `false`

### 模块加载器 (ZDProjects/*_OwnCode.cs)

这些文件**无需更新**，除非日志显示编译错误。模块加载器只负责编译外部文件，业务逻辑更新会自动生效。

---

## [4.0.0] - 2026-01-30

### 初始版本
- 动态编译架构
- 模块化设计
- AI 画像生成
- 会话模拟
- 每日/每周更新

---
