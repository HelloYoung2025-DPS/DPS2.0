# 重构计划书 v2 多智能体交叉审核报告

> 审核对象：`Docs/RebuildPlan_重构计划书.md`（Rebuild Plan v2，基线 458f9bd）
> 审核日期：2026-07-16
> 审核方式：多智能体 workflow 交叉审核——11 个维度并行审查（四条硬要求各一 + 三组 `文件:行号` 锚点逐条实测核验 + 内部一致性 + 红线/既往约束合规 + Simplicity First + 虚拟化/桥可行性）→ 74 条原始发现 → 每条一名对抗复核员（默认立场：驳倒，须独立重测证据）→ 主会话对关键条目二次抽查。共 87 个智能体、1300+ 次工具调用。
> 结果：**46 条确认（合并重复后 40 条：2 critical / 25 important / 13 minor）、24 条驳回、4 条未复核**。

---

## 0. 总体结论

**计划书的事实底座异常扎实，方向没有被推翻；但按图施工会在两处撞墙（2 条 critical），且"拟人化最后一公里"和"解锁即恢复"两个核心叙事存在成片失真。建议先按 §5 优先级清单修订计划书，再开工 R0。**

- **锚点可信**：三组锚点核验共 70+ 项，~90% 行号级精确命中（legacy C# 25 项中 22 项 VERIFIED、治理 Python 26 项中 24 项、v2 模块 20 项中 16 项），包括最难蒙对的硬数字（quarantine-only 恰 14 条、`EXPECTED_LEGACY_CSHARP_COUNT=79`、`:1957` TypeError 亲测复现）。"全部结论带文件:行号证据"的自我要求基本兑现——这份计划书的实证盘点是真做过的。
- **两条 critical**：① 候选门禁的 **trust-anchor 语义**被完全遗漏——R0-2 出口判据"删除后重跑两级门禁全绿"在 clean 模式机械不可达，R3 CI 对 P2/P3 类 PR 必红；② **翻转三道锁不会恢复任何执行能力**——基线上 6 处执行胶水已被摘除为 proposal-only stub，原函数体只存在于历史提交 cac7ccb，解锁 runbook 缺一整项工作。两条均经对抗复核 + 主会话抽查双重坐实。
- **四条硬要求的达成度**（10 分制）：要求 1 = 6.5、要求 2 = 7.5、要求 3 = 6、**要求 4 = 5.5（最弱）**。要求 1 的兴趣/记忆层会真实分化，但执行层（IG 操作面 0 个 humanized 标志、误触不在活跃路径、宏观节律无落点）可能完全测不出"两个人"；要求 4 的"6 条被锁、解锁+接线即恢复"叙事有两处关键失真（stub + IntentTranslator 链从未接线运行）。
- **24 条驳回同样有价值**：对抗复核证明计划书在这些点上其实是对的（如 R3 时序自洽、GBrain 本地降级路径存在、"删 8 会悬空"论断成立、零工期是用户拍板而非遗漏）——见 §4。

### 审核可信度声明（Fail Loudly）

- 去重 agent 因超输出上限失败，未做机器去重；本报告由主会话手工合并了 6 条跨维度重复项。
- 5 名对抗复核员与完备性批判员因会话额度耗尽失败：**4 条发现未经复核**（列于 §3，只有 finder 单方证据），**完备性批判（找"所有人都漏掉的问题"）整体缺位**——本报告的覆盖面没有经过最后一道补漏检查。
- 数名复核员运行期间安全分类器不可用；其中承重的 5 条结论（trust-anchor、stub、R5 幽灵里程碑、resolver schema const、v3 USB caveat）已由主会话在仓库中二次抽查坐实。

## 1. 十一维度评分与裁决

| 维度 | 评分 | 一句话裁决 |
|---|---|---|
| 硬要求1·Soul分化拟人化 | 6.5 | 计划书对现状的事实定性经逐项实测全部属实（六层分化表、三道锁、persona 封闭词表、静态权重、门控双实现等约 15 个锚点无一错误），机制设计在"昨天记得/上月淡忘"（三因子检索 7 天半衰期 + 分层携带）和"慢启动→提升→平台期"（logistic）两条上方向成立。 |
| 硬要求2·精简生死簿 | 7.5 | 生死簿在文件层面高度准确：全仓 213,735 行在"排除 obj/ 生成码"口径下精确复现；factory 依赖封闭子图（0 条跨界边）、AppExplorer_v2 零编译清单引用（792 行精确）、WeeklyEvolve 除壳零调用者、ActionCatalog/StepPlans/instagram_v2/L。 |
| 硬要求3·单独/并行升级 | 6 | 计划书 §6 的如实定性经逐条实测全部成立：候选门禁确无模块 scope 参数、治理路径改动确扩全模块 scope、P3 字节基线确是全局单点、候选门禁确是全仓聚合、"须重写而非改 manifest"的代码级 import 根因引用精确——"开发可并行、落地必串行"的定性诚实不夸大，phase0 全仓锁定构建 + 消费。 |
| 硬要求4·设计初衷八条 | 5.5 | §2 的证据锚点绝大多数属实且精确（恒 false 锁 :173-176、7 个 OwnCode 常量、加权随机 :869-928、门控双实现 :1026-1067/:2057-2098 及 SKIP 不入分母、0.95 默认且 BehaviorConfig 无 session_gate 节、SwipeCurved 唯。 |
| 锚点核验·legacy C# | 9 | legacy C# 锚点质量极高：清单内 25 项逐条打开实测，22 项 VERIFIED（行号与描述精确吻合，含恒 false 函数、门控双实现、SKIP 分母、advisory 裁决、7 处 OwnCode 常量、AppExplorer_v2 零引用、SwipeCurved 唯一实现等关键论断全部成立），无一处 N。 |
| 锚点核验·治理Python | 9 | 治理侧 Python/YAML 锚点维度上，这份计划书的事实底座异常扎实：26 项锚点检查中 24 项 VERIFIED 且多数行号精确命中（含最难核的三条——:1957 TypeError 断裂经 InstructionResolver 现签名亲测成立、phase0 收据链确与 factory 零耦合、candida。 |
| 锚点核验·v2模块 | 7.5 | modern v2 锚点整体质量高：20 项锚点中 16 项行号级精确吻合（含很难蒙对的硬数字——quarantine-only v1 边基线恰好 14 条、AppExplorer_v2 恰 792 行、CapabilityProbe 三阈值、persona 三处封闭词表、双 BOM 锚点、v1/v2 断层全部实测成立。 |
| 内部一致性 | 6.5 | 计划书整体自洽度中上：红线清单、34→23 门禁面、两级门禁口径、§11 裁决表与正文映射、附录 A 证据锚点（抽查 SessionRunner.cs:173-176 恒 false、EXPECTED_LEGACY_CSHARP_COUNT=79、run_phase0_gate --receipt-in/:122-12。 |
| 红线约束合规 | 6.5 | 红线合规整体扎实：append-only、无自主流水线、通用性、secret 注入、fail-closed 翻转时序、误触先例等主要论断经逐条实测全部属实，引用的文件:行号准确率很高。 |
| Simplicity审查 | 7 | 计划书在 Simplicity 维度整体纪律良好：实测证实多处"复用而非新造"的裁决成立——digest 固化器经我按真实 schema 估算（ProjectionEventV1≈267B+evidence≈258B，64KiB 单页装 124–245 事件，2 设备 10–30 事件/天下 4–25 天压顶）确属必需。 |
| 可行性 | 6 | MV 拓扑本身逐环节评估基本成立：AVD arm64 成熟、socat 网络 ADB 可行、ZD-under-Prism 被正确标为最大单点风险且有 fallback（ZennoClub 有 ZennoPoster 在 M1 Parallels 跑通的先例但性能差）；GBrain NAS 步骤有仓内固定 commit 。 |

<details><summary>各维度完整裁决（点开）</summary>

**硬要求1·Soul分化拟人化（6.5/10）**：计划书对现状的事实定性经逐项实测全部属实（六层分化表、三道锁、persona 封闭词表、静态权重、门控双实现等约 15 个锚点无一错误），机制设计在"昨天记得/上月淡忘"（三因子检索 7 天半衰期 + 分层携带）和"慢启动→提升→平台期"（logistic）两条上方向成立。但最大的问题是：计划书在"拟人化真正生效的最后一公里"——活跃统一引擎路径——存在成片盲区：ActionExecutor 有第二张与 §4.3 基线分叉的硬编码画像表、拟人化是 per-step opt-in 而 Instagram 操作面 0 个 humanized 标志、误触行为根本不在活跃路径上（§4.4 的"既有行为"定性对活跃引擎不成立）；同时宏观行为分化（活跃时段/会话节律/动作构成比）无任何落点，"爱好消退"存在 logistic 底板参数陷阱。按现计划做完，两个 Soul 在兴趣/记忆层会真实分化，微观延迟会有差异，但在 IG 上点击/滑动/误触层面可能完全测不出差异，宏观作息上仍像同一个人开两个号。

**硬要求2·精简生死簿（7.5/10）**：生死簿在文件层面高度准确：全仓 213,735 行在"排除 obj/ 生成码"口径下精确复现；factory 依赖封闭子图（0 条跨界边）、AppExplorer_v2 零编译清单引用（792 行精确）、WeeklyEvolve 除壳零调用者、ActionCatalog/StepPlans/instagram_v2/LastLayoutXml 零引用、ZDProjects/Tests 10+1 件恰好 1,794 行——逐条实测全部成立；§3.4 八步手术的行号锚点（run_candidate_gate :148-156/:175-177/:1818/:1858-1987、phase0 :4275-4300/:4538-4543、release.sh :134-136、三快照再生范围）全部命中，KEEP-SLIM 四项裁决（control-plane-host/Manifest 物理保留/MemoryManager :897+:1693 延后/planner 消费边）事实依据均经得起推敲，反向检查（playwright 在 CI 活用、run.droid 为 ZD 工程包、node_modules/.venv/pyc 未入库）也未发现明显漏删大项。最大的问题是：external_gate 通用信封层对 F6/F7 也强制 factory_binding 收据链字段——这是独立于已补两处 BOM 的第三处同类漏网，会卡 R2 验收；其次 §2 与 §3.1 对 AppExplorer v1 的处置自相矛盾，以及"删 11 无悬空边"论断与 23 个保留 manifest 的 resolver 幽灵引用不符。删除本身删得动，anchor 重签路径已排布。

**硬要求3·单独/并行升级（6/10）**：计划书 §6 的如实定性经逐条实测全部成立：候选门禁确无模块 scope 参数、治理路径改动确扩全模块 scope、P3 字节基线确是全局单点、候选门禁确是全仓聚合、"须重写而非改 manifest"的代码级 import 根因引用精确——"开发可并行、落地必串行"的定性诚实不夸大，phase0 全仓锁定构建 + 消费方绑定生产方 schema 的机器链对编译级/合同级跨模块破坏真能拦住。但计划书完全漏掉了候选门禁的 candidate-trust-anchor 语义（CANDIDATE_TRUST_PATHS 含 Tools/ci 四脚本、候选策略、治理快照、根构建文件，clean 候选要求它们与 baseline 逐字节一致）——导致 R0-2 自己的出口判据"删除后立即全量重跑两级门禁全绿"在 clean 模式机械不可达、R3 CI 对最危险的 P2/P3 类 PR 必红；landing 协议（rebase 后重跑、先到先得排队）没有任何机器挡板工作项（现状 CI 只有 phase0 一个 job，收据不因 main 前进失效）；且配置面（Config/Data/operations——初衷 2/3 的核心载体）在"改 A 不坏 B"机器链上是完全盲区。

**硬要求4·设计初衷八条（5.5/10）**：§2 的证据锚点绝大多数属实且精确（恒 false 锁 :173-176、7 个 OwnCode 常量、加权随机 :869-928、门控双实现 :1026-1067/:2057-2098 及 SKIP 不入分母、0.95 默认且 BehaviorConfig 无 session_gate 节、SwipeCurved 唯一实现 :119、误触参数先例 :37-80、边缘持明文 AI key、advisory 裁决 :941-945、reddit 102/babycenter 35/instagram 9 操作数、humanization_profile 无 setter、NavigationResolver 生产零职能因此 §3.2 处置成立——这些计划书都写对了），90%+ 数字不复验不承诺、并行升级如实定性也诚实。但"6 条被锁、解锁+接线即恢复"的核心叙事在基线 458f9bd 上有两处关键失真：一是执行胶水（ExecuteWithUnifiedEngine、恢复阶梯执行分支等 6 处）已被摘除为 proposal-only stub，函数体只存在于历史提交 cac7ccb，按计划书的三锁翻转 runbook 执行后所有动作仍返回 ERROR，功能不会恢复；二是初衷 #1 的 IntentTranslator→ZDCommand→ZennoDroidAdapter 链全仓含全部 git 历史零调用，从未接线运行，"LOCKED"定性不实。另有曲线滑动收敛点选在零调用的 adapter 而漏掉生产滑动路径、request_vision_verdict 缺截图传输/时延/回退三件硬设计、"消灭双大脑"与 A4 红线的边界未定义、单文件加 APP 漏 Keywords 活触点等执行方案缺口。

**锚点核验·legacy C#（9/10）**：legacy C# 锚点质量极高：清单内 25 项逐条打开实测，22 项 VERIFIED（行号与描述精确吻合，含恒 false 函数、门控双实现、SKIP 分母、advisory 裁决、7 处 OwnCode 常量、AppExplorer_v2 零引用、SwipeCurved 唯一实现等关键论断全部成立），无一处 NOT_FOUND、无语义曲解。仅 1 处 OFFSET（SessionRunner.cs:2464-2514 标"意图回退链"，该区间实为 LoadIntentMapping+ResolveIntentForAction，真正的 ResolveIntentWithFallback 在 :2519-2552，内容存在仅行号偏移）和 2 处 minor 数字不准（WeeklyEvolve 行数、ActionExecutor 指令数）。计划书"全部结论带文件:行号证据"的自我要求基本兑现。

**锚点核验·治理Python（9/10）**：治理侧 Python/YAML 锚点维度上，这份计划书的事实底座异常扎实：26 项锚点检查中 24 项 VERIFIED 且多数行号精确命中（含最难核的三条——:1957 TypeError 断裂经 InstructionResolver 现签名亲测成立、phase0 收据链确与 factory 零耦合、candidate BOM 固定 SHA 硬读归属正确），"governance .yaml 是 JSON / Configs/Manifests 是真 YAML"的反直觉断言逐文件核实为真。仅有的问题是两处删除范围/爆炸半径的低估：F8/F9 分支实际专属代码约 2,200 行（计划书写约 1,200，按字面范围执行会遗留约 850 行 f9 孤儿辅助函数死码），以及配置死物清理漏计两处次要触点。

**锚点核验·v2模块（7.5/10）**：modern v2 锚点整体质量高：20 项锚点中 16 项行号级精确吻合（含很难蒙对的硬数字——quarantine-only v1 边基线恰好 14 条、AppExplorer_v2 恰 792 行、CapabilityProbe 三阈值、persona 三处封闭词表、双 BOM 锚点、v1/v2 断层全部实测成立），说明计划书的实证盘点是真做过的。最大问题有两个：一是全部 23 个保留模块 module.yaml 的 agents.resolver 字段与 module-manifest.schema.json:325 的 const 把 "factory-instruction-resolver" 钉死，删 11 后成为全仓悬空引用且 phase0 门禁照样绿（R0-0 dry-run 兜不住），§3.4 "全删后无悬空问题"的论断在此维度不成立；二是 R2-6 选择器修复锚点 instagram.json:88/:35/:141 指向的是被 §3.2 逻辑退役的 Manifest 体系文件、其中 :35 还是合法原始用法，而真正带"从别处照抄嫌疑"选择器的 Config/PlatformsConfig.json instagram 段（计划书自己定义的单一事实源）反而没有被指向。

**内部一致性（6.5/10）**：计划书整体自洽度中上：红线清单、34→23 门禁面、两级门禁口径、§11 裁决表与正文映射、附录 A 证据锚点（抽查 SessionRunner.cs:173-176 恒 false、EXPECTED_LEGACY_CSHARP_COUNT=79、run_phase0_gate --receipt-in/:122-127 等均实测属实）都经得起核对；必查项 (c) 也能自圆其说——既往约束定义的"无人工干预"是"不靠实时盯"而非"永不停摆"，§7 硬停止+通知+保底 ATTENTION_REQUIRED.md 与之不矛盾。但里程碑依赖图和验收条款是重灾区：R3 被画成 R0 后的轻量旁支，实际其验收（六锚点含候选 integration 证据）依赖 R1、而 R2-2 又依赖 R3，真实关键路径是 R0→R1→R3→R2，图上两条边全缺；此外存在 R5/R2-3 等悬空引用、F8/F9 既删又缓的自相矛盾、R1 验收考 R2 才接线的功能、"七项桥"在本文件内无枚举等一批可执行性断裂。方向和事实底座扎实，但按图施工会在 R2-2 和多个验收门处撞墙返工。

**红线约束合规（6.5/10）**：红线合规整体扎实：append-only、无自主流水线、通用性、secret 注入、fail-closed 翻转时序、误触先例等主要论断经逐条实测全部属实，引用的文件:行号准确率很高。最大的问题是"治理不自批"的执行载体建错了模型——legacy trusted anchor 在仓库里是"外部 UID 隔离的只读 JSON 文件"（无任何 RSA-PSS 签名），计划书却按"RSA-PSS 权威重签 + 异构复核会话扮 independent authority"来写 runbook，机械上不可行且掩盖了真正的独立性来源（用户/另一 OS 身份）；其次 anchor 重签排程引用了不存在的里程碑且遗漏多个 legacy 字节改动批次，A4"收编=不重写"与 A12"统计可区分"的边界论证均缺机器可验判据，视觉上移的截图通道与 soul_id 不下边缘两处方案内部矛盾未闭环。

**Simplicity审查（7/10）**：计划书在 Simplicity 维度整体纪律良好：实测证实多处"复用而非新造"的裁决成立——digest 固化器经我按真实 schema 估算（ProjectionEventV1≈267B+evidence≈258B，64KiB 单页装 124–245 事件，2 设备 10–30 事件/天下 4–25 天压顶）确属必需而非装饰；半衰期衰减已存在于 InterestReducer.cs:141-163 只是参数化复用；outbox 表/合同已在 memory-event-ledger（migrations/002:113-145）故"最小轮询投递器"确实薄；SwipeCurved/四档画像/误触参数均实锤存在；R0-P 的 ABI 探针对应 CapabilityProbe.cs:158 硬编码 Require(false) 的真实阻塞。主要问题是三处：§2"全计划唯一新建 <2.5k 行"与计划书自己在 §4.6/R1-3/R1-4/R1-6/§4.2 明写的多项新建自相矛盾、按仓内同类模块规模加总实际约 4–6k 行；instagram 兴趣种子/触发词数据缺失被错误归类为"不阻塞"待实测项，实为硬要求 1 的 R2 验收前置；persona v2 双方案在关键路径上不拍板，违背 Simplicity 与用户"选一个并解释"的原则。

**可行性（6/10）**：MV 拓扑本身逐环节评估基本成立：AVD arm64 成熟、socat 网络 ADB 可行、ZD-under-Prism 被正确标为最大单点风险且有 fallback（ZennoClub 有 ZennoPoster 在 M1 Parallels 跑通的先例但性能差）；GBrain NAS 步骤有仓内固定 commit 与探测文档支撑；Release BOM/CapabilityProbe 等代码锚点亲测属实。但可行性维度有三个系统性问题：外部签名权威无默认托管方案且实际阻塞点在 R0-2（字节基线含 3 个待删文件）而非计划书标注的 R2，并与异构复核接入时序自相矛盾；全文零工期估计导致 14 天 demo/试用窗口与里程碑无法对齐；外部依赖盘点有漏（Voyage key、Google 账号、NAS 平台兼容、IG 封号应对），§10 待实测清单未覆盖数个高危未知。

</details>


---

## 2. 确认的问题（经对抗复核坐实）

### 2.1 Critical（开工前必须修订计划书）

### C1. 计划书完全遗漏候选门禁 trust-anchor 语义，R0-2 出口判据在 clean 模式机械不可达，R3 CI 对 P2/P3 类 PR 必红

**级别**：🔴 Critical　**来源维度**：硬要求3·单独/并行升级

**问题**：run_candidate_gate.py 定义了 CANDIDATE_TRUST_PATHS（约 40 个文件：candidate-test-policy.yaml、Tools/ci 四脚本自身、Tests/ci 三测试、governance 三快照与 schema、Directory.Build.props、.editorconfig、Dps.slnx、toolchain.lock.json、static-ci.yml 等），clean 候选运行要求全部信任根与显式 baseline 逐字节一致且 baseline!=HEAD 且为祖先，否则 candidate-trust-anchor 判 FAIL；--base 缺省=HEAD 时必 FAIL。而 §3.4 的 factory 删除批次恰好改写 run_candidate_gate.py、candidate-test-policy.yaml、三快照、Tests/ci 三文件——全是信任根。因此 R0-2 '删除后立即全量重跑两级门禁证明全绿'在 clean 模式对任何 pre-deletion baseline 必 FAIL：正式取证必须以删除提交 D 为 --base、且在 D 之上再有一个后继提交，计划书从头到尾没写这个再基线化步骤。同理 R3-1 'CI 候选 contract job --base <PR base> 每次升级必过门'对一切触碰 governance/Tools/ci/工作流/根构建文件的 PR（正是 §6 表中 P2/P3 类）按设计必红，计划书未给任何豁免或两段式取证程序。§6 只写了这些路径'扩全模块 scope'，漏掉了它们同时是信任根这一更强的机器语义。

**证据**：run_candidate_gate.py:157-207（CANDIDATE_TRUST_PATHS 清单，:190-193 含 Tools/ci 四脚本自身）、:2063-2091（_trust_anchor_inventory 逐文件对比 baseline blob 哈希）、:2981-3006（trust_eligible = baseline != head and ancestor and trust_match，clean 模式不满足即 FAIL）、:2900-2904（--base 缺省解析为 HEAD）；计划书 §5-R0-2'删除后立即全量重跑两级门禁'、§3.4 步骤 8、R3-1 原文均无信任根处理

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：我以驳倒为目标逐条实测，结论是该发现的机器语义部分全部坐实，且计划书全文确实未处理。实测坐实的链条：(1) CANDIDATE_TRUST_PATHS 共 47 个文件，§3.4 删除批次要改写的 run_candidate_gate.py、phase0.py、run_phase0_gate.py、candidate-test-policy.yaml、三快照、Tests/ci 三测试、scripts/release.sh 至少 9 项全在其中；(2) clean 模式要求 baseline != HEAD 且为祖先且全部信任根与 baseline blob 逐字节一致，否则 candidate-trust-anchor 判 FAIL，而 overall_status 要求所有 check 全 PASS（:3207-3210），FAIL 即 exit 1（:3342）；--base 缺省解析为 HEAD（:2903），baseline==head 直接不满足 trust_eligible；(3) 因此对删除提交本身，以任何 pre-deletion 提交为 --base 必 FAIL（信任根已变），以自身为 base 也必 FAIL（baseline==head）——clean 候选 PASS 只能在 D 的后继提交上以 --base D 取得，这正是发现所称"再基线化"步骤；(4) 诊断模式虽可全绿（trust_status 在 diagnostic_run 下 PASS），但证据 mode=WORKSPACE_DIAGNOSTIC_ONLY、formal_evidence_eligible=False、commit_sha=None，验证器明确"diagnostic candidate cannot attribute a tested commit"且"clean PASS candidate lacks a stable predecessor trust anchor"会抛错（:2512-2530），不能充当正式证据。计划书方面：§3.4 步骤 2 仅提":17……

**复核员独立实测证据**：我亲测：Tools/ci/run_candidate_gate.py:157-207（CANDIDATE_TRUST_PATHS 47 项：:158 candidate-test-policy.yaml、:162 static-ci.yml、:167 AGENTS.md、:168 Directory.Build.props、:171 Dps.slnx、:182-184 三快照、:185-186 risk/compatibility-policy、:187-189 Tests/ci 三测试、:190-193 Tools/ci 四脚本、:202 scripts/release.sh、:206 toolchain.lock.json）；:2063-2091（_trust_anchor_inventory 逐文件 sha256 对比 baseline blob，任一不匹配 all_match=False）；:2900-2904（baseline=rev-parse(arguments.base or "HEAD")）；:2910-2916（diagnostic_run=flag or dirt……

**修改建议**：采纳原建议并收窄 P3 表述：①§3.4 增补"信任根再基线化 runbook"：删除批次合入提交 D 前用 --diagnostic-workspace 取记录性验证，D 合入后在其后继提交（必要时空提交）上以 --base D 取首个 clean 候选证据写入 evidence-anchors；②§5-R3-1 对触碰 CANDIDATE_TRUST_PATHS 的 PR 明确两段式：PR 阶段 CI 跑 --diagnostic-workspace 记录性验证，合入后在后继提交上以 --base <合入提交> 正式取证；③§6 并行判定表增"信任根类"一行，注明其机器语义为"当次变更提交自身无法取得 clean 候选证据，须后继提交再基线化"，适用范围写为 P2 全部 + 触碰 Tools/ci、.github、根 AGENTS.md、根构建文件的改动（纯 P3 字节基线改动不在此列，因 legacy-runtime-adapter 路径不在信任根清单）。

---

### C2. 翻转三道锁不会恢复任何执行能力：6 处执行胶水在基线上已被摘除为 proposal-only stub，函数体只存在于 git 历史，计划书完全未列此项工作

**级别**：🔴 Critical　**来源维度**：硬要求4·设计初衷八条

**问题**：计划书 §2 断言"legacy 4.x 已把 8 条中的 6 条写成可用代码……只是被三道锁锁死"，§5-R2-2⑥ 的解锁 runbook 仅翻转"7 个 OwnCode 常量 + SessionRunner 恒 false 函数 + 源码文本断言测试"。实测基线 458f9bd 上除三道锁外还有第四层：ExecuteWithUnifiedEngine（统一引擎调 ActionExecutor 的入口）、EvaluateActionResult 的恢复阶梯执行分支（Retry/LocalRecovery/VisionAssist/FallbackScript 全部）、DismissOverlay、PostOperationHealthCheck、ForceNavigateToFeed、ExecutePreSessionActions、RunAppOnboarder 的函数体已被替换为无条件返回 ERROR/false 的提案 stub，原实现只存在于前一个提交 cac7ccb。按 runbook 翻锁后 Run() 会进入主循环，但每个动作经 :928 调 stub 得 ERROR，门控必 FAIL——初衷 #1/#2/#5/#8 一条都不会恢复，R2 EMULATOR_VERIFIED（浏览 ≥10 帖）必然失败返工。且文本断言测试恰好钉死这些 stub（test_legacy_executor_is_proposal_only 断言 ExecuteWithUnifiedEngine 体内含 "Execution proposal blocked"），恢复函数体是与翻锁同批的 P3 字节基线变更，计划书的工作量口径（"其余全是解锁+接线+配置合并"）与 anchor 重签批次（§7-2）都没有为它留位置。

**证据**：SessionRunner.cs:2390-2400（ExecuteWithUnifiedEngine 无条件返回 "ERROR:authorized_execution_bridge_required"）；:1919-1930（失败分支无条件 "recovery proposal blocked" + StopUnverifiedLegacyExecution，视觉/重试/回退全被摘除）；:3127-3134/:3140-3152/:3157-3166/:3259-3266/:3351-3358（其余 5 处 stub）；git show cac7ccb:Modules/SessionRunner.cs 第 2630 行起有完整原实现（PageDetector→GetOperationsByIntent→ActionExecutor.Execute→分级恢复循环）；Modules/legacy-runtime-adapter/tests/test_sessionrunner_fail_closed_p0.py:196-200 断言 stub 文本存在；计划书 §2 首段、§5-R2-2⑥ 无一字提及 stub 恢复

**复核员独立实测证据**：实测：Modules/SessionRunner.cs:2390-2400（ExecuteWithUnifiedEngine 无条件返回 ERROR:authorized_execution_bridge_required）、:1919-1930（失败分支无条件 recovery proposal blocked→StopUnverifiedLegacyExecution）、:3127-3134（DismissOverlay 恒 false）、:3140-3152（PostOperationHealthCheck 前台失败即 blocked）、:3160-3166（ForceNavigateToFeed 空提案）、:3260-3267（ExecutePreSessionActions 只计数）、:3351-3358（RunAppOnboarder 恒 false）、:928（主循环唯一执行路径调 stub）、:173-176（恒 false 锁）；Modules/legacy-runtime-adapter/tests/test_sessionrunner_fail_closed_p0……

**修改建议**：最小修改三处：① §2 首段定性改为"6 条的能力代码经 CHANGELOG 迭代验证，但基线 458f9bd 已将 6 处 SessionRunner 执行胶水摘除为 proposal-only stub（原实现在前一提交 cac7ccb），实际封存 = 三道锁 + 函数体摘除四层"，并在对账表相应行（#1/#2/#5/#8 及恢复阶梯）注明"执行胶水待恢复"；② §5-R2-2⑥ 解锁 runbook 扩为"三锁翻转 + 6 处 stub 函数体按 cac7ccb 恢复并与本批次其他 legacy 改动（VerifyError 翻案分支删除、门控双实现收敛、决策上移边界）调和后，同一具名审批原子批次更新 legacy-csharp-bytes.v1.json"，文本断言测试的修复清单同步覆盖 :178-192 全文件禁字与 :196-202 stub 断言；③ §2 行 42 的工作量口径补一句"含约 1.1k 行历史函数体的恢复与调和（非新建）"。无需增加 §7-2 anchor 重签次数（恢复属 approved repair，可并入既列的 R2 flip 批次）。

---

### 2.2 Important（对应里程碑开工前必须解决）

### I1. Instagram 操作面 0 个 humanized 标志：统一引擎拟人化是 per-step opt-in，SoulBehaviorPacket 在目标平台可能完全失效

**级别**：🟠 Important　**来源维度**：硬要求1·Soul分化拟人化

**问题**：ActionExecutor 只在步骤 JSON 显式带 "humanized":"true" 时才走画像化 tap/swipe/delay 路径，否则直接 input.Tap 死点中心 + 直线 Swipe。reddit_operations.json 有 370 处该标志，而 Phase-1 目标平台 instagram_operations.json 的 9 个操作一个都没有。计划书 §2-2 只说"Instagram 操作面补齐"、§4.3 只说下发 packet + 补 setter，从未提及 humanized per-step 开关这一生效前提——不补标志，R2 在 IG 上验收"双 Soul 行为可辨（点偏移/弯曲度）"时 packet 根本不进拟人化分支。

**证据**：Modules/Core/ActionExecutor.cs:355-358（bool humanized = JsonHelper.Get(stepJson,"humanized")=="true" 才 GetVar humanization_profile）；grep -c '"humanized"' Config/Operations/instagram_operations.json = 0，reddit_operations.json = 370；instagram_operations.json 仅 9 个操作

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现所引证据全部实测成立，且我额外核验了第二条执行路径也无法救场：(1) ActionExecutor 四处（tap/swipe/scroll/delay）均为 per-step opt-in，"humanized"!="true" 时直走死点中心 tap + 直线 swipe；(2) instagram_operations.json 0 个 humanized 标志、仅 9 操作，reddit 370 个；(3) 意图链同样门控——Intent/ZDCommand 默认 Humanized=false，ZennoDroidAdapter 抖动全在 if(command.Humanized) 内，两条路径同一语义；(4) 计划书全文无一处提及 humanized per-step 开关，§4.3 声称补 setter 后"ActionExecutor.cs:358 断层就此闭合"在 IG 上是错误断言（:358 位于 humanized 分支内，0 标志下永不执行），§2-2/§5-R2 验收均无覆盖率前置；(5) 唯一缓解是 SessionRunner.cs:989/:2143 的会话级 GetActionDelay 覆盖不受该门控，延迟分布或有微弱差异，但点偏移/弯曲度在 IG 上零方差，R2"双 Soul 行为可辨"三指标废二。不触红线、不阻 R0/R1，故维持 important 而非 critical。

**复核员独立实测证据**：Modules/Core/ActionExecutor.cs:355-370（humanized 门控+死点 input.Tap）、:393-403（直线 input.Swipe）、:433-441、:458-465、:1070-1102（GetHumanizationProfile 含 tap_offset_max/swipe_jitter）；grep -c '"humanized"' Config/Operations/instagram_operations.json=0、reddit_operations.json=370、babycenter_operations.json=142；instagram_operations.json 实测 9 操作；Modules/Core/Intent.cs:64（默认 false）/:88（同一 opt-in 语义）；Modules/Core/ZDCommand.cs:116（默认 false）；Modules/Core/ZennoDroidAdapter.cs:168-270（抖动全在 if(command.Humanized) 内）；……

**修改建议**：三处最小修改：① §4.3 末句改为"补 setter 仅闭合变量断层；生效还需步骤级 humanized 开关，Phase-1 须消除该 opt-in（统一引擎默认 humanized、配置显式豁免）或为 IG 全部操作步骤补齐标志——二选一，倾向前者（Simplicity First，免得每加一个 APP 重蹈覆辙）"；② §2-2"Instagram 操作面补齐"括注"含 humanized 覆盖（当前 0/9 操作）"；③ §5-R2 EMULATOR_VERIFIED"双 Soul 行为可辨"验收前加前置检查项"目标平台操作面 humanized 生效覆盖率 100%（或默认开启已落地）"。

---

### I2. 活跃路径存在第二张硬编码画像表，与 §4.3 引用的 ScriptHelpers 基线表键集/数值分叉，SoulBehaviorPacket 落点未指明

**级别**：🟠 Important　**来源维度**：硬要求1·Soul分化拟人化

**问题**：ActionExecutor.GetHumanizationProfile（活跃统一引擎路径）自带一张四档硬编码表，键集与 §4.3 引用的 ScriptHelpers.cs:37-80 不同：用 swipe_jitter（端点抖动）而非 swipe_bending_range（弯曲度），且完全没有 prob_* 误触键。计划书通篇只引用 ScriptHelpers 表作 archetype 基线，未提两表分叉的事实。若 SoulBehaviorPacket 按 ScriptHelpers 表的键生成扰动，活跃引擎读的是自己那张表，分化不生效；且 §3.1 退役 Reddit_*.cs 双路径后 ScriptHelpers 表将无运行时消费者。

**证据**：Modules/Core/ActionExecutor.cs:1070-1102（GetHumanizationProfile 四档，键为 base_delay_mult/delay_variance/tap_offset_max/swipe_jitter）对比 Core/ScriptHelpers.cs:37-80（含 swipe_bending_range 与 4 个 prob_* 键）；ActionExecutor.cs:1153-1168 ApplyHumanizedSwipe 用 swipe_jitter 且仍调直线 input.Swipe

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的全部事实链经我独立实测坐实：(1) 活跃统一引擎 ActionExecutor 自带第二张四档硬编码画像表，键集与计划书通篇引用的 ScriptHelpers 基线表分叉（swipe_jitter vs swipe_bending_range，且完全无 prob_* 误触键）；(2) 计划书全文 grep swipe_jitter/GetHumanizationProfile/ApplyHumanizedSwipe 零命中，确未提两表分叉；(3) §4.3 声称补 humanization_profile setter"断层就此闭合"是机制错述——该变量只是画像名，选中的是 ActionExecutor 自己的硬编码表，packet 的点偏移/弯曲度扰动无消费点；(4) 我补充实测两点加重：ActionExecutor.StepSwipe 直驱 CoreHelper.GetInput()，不经 ZennoDroidAdapter，故 §5-R2-4(a) 的 SwipeCurved 统一（只写进 ZennoDroidAdapter.ExecuteSwipe）覆盖不到活跃引擎的直线滑动路径；且 R2 EMULATOR_VERIFIED 验收把"弯曲度/误触率"列为双 Soul 可辨指标，而活跃路径既无弯曲度键也无误触机制（prob_* 唯一消费者 Reddit_*.cs 已列退役），验收指标与执行方案内部矛盾；(5) ActionExecutor.cs 实测在字节基线 79 文件清单内，改表需字节审批但计划书未排期。最宽容读法（"下发单个 JSON"隐含全量注入）也无法解释计划书对两表分叉的零处理与"就此闭合"的错述。维持 important：可修复但不修则要求 1 核心交付不生效。

**复核员独立实测证据**：Modules/Core/ActionExecutor.cs:1070-1102（GetHumanizationProfile 四档，键仅 base_delay_mult/delay_variance/tap_offset_max/swipe_jitter）；ActionExecutor.cs:1153-1169（ApplyHumanizedSwipe 读 swipe_jitter 端点抖动后调直线 input.Swipe）；ActionExecutor.cs:355-359/396-398/436-438/462-463（humanized 分支经 GetVar("humanization_profile","casual") 选自家硬编码表）；ActionExecutor.cs:379-398（StepSwipe 直驱 CoreHelper.GetInput()，不经 ZennoDroidAdapter）；Core/ScriptHelpers.cs:37-80（含 swipe_bending_range 与 prob_accidental_back/prob_scroll_back……

**修改建议**：§4.3 增补：明确两张画像表分叉的事实，指定单一事实源——废除 ActionExecutor.cs:1070-1102 硬编码表，改由 SoulBehaviorPacket JSON 全量注入（此为字节保护文件改动，与 :1381 语义别名外置同批列入字节审批）；packet 键 schema 以活跃引擎实际消费为准，其中 swipe_jitter 升级为弯曲度参数并注明 ActionExecutor 的 StepSwipe 直驱 Input、不经 ZennoDroidAdapter，须在 R2-4(a) 同批单独接线；同时裁决 prob_* 误触机制在活跃路径的落点（迁入 ActionExecutor humanized 分支或从 R2"误触率可辨"验收指标中删除），避免验收指标空转。

---

### I3. 误触行为不存在于活跃引擎路径：§4.4"既有行为保留"的现状定性对统一引擎不成立，R2"误触率"指标失去载体

**级别**：🟠 Important　**来源维度**：硬要求1·Soul分化拟人化

**问题**：prob_accidental_back/prob_scroll_back/prob_double_tap 及其触发逻辑 ShouldTriggerProbabilistic 全仓只存在于 Core/ScriptHelpers.cs 和被绕开、§2-2 已判退役的 ZDProjects/Reddit_*.cs 路径；活跃的 ActionExecutor 画像表无任何 prob_* 键、无概率触发逻辑。§4.4 称误触是"既有行为，非新造"对参数存在性成立、对活跃运行时行为不成立；把误触移植进统一引擎是一项计划未列出的新建工作，且按 §4.4 自己定的规则（超出既有的拟人化扩项须单独过异构复核+A12 论证），"向活跃路径新增误触执行逻辑"本身可能就该触发该流程——计划书未论证。

**证据**：grep 全仓 prob_accidental_back|ShouldTriggerProbabilistic 仅命中 Core/ScriptHelpers.cs:45-76,123、Core/HumanizationEngine.cs（37 行纯文档壳）、ZDProjects/Reddit_*.cs；Modules/Core/ActionExecutor.cs:1070-1102 无 prob_* 键；SessionRunner.cs:249 仅为注释中的 accidentally

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的三个核心论断全部实测成立：(1) prob_* 误触参数与 ShouldTriggerProbabilistic 触发逻辑全仓仅存在于 Core/ScriptHelpers.cs 和计划书 §2-2 自判"运行时无入口、退役"的 ZDProjects/Reddit_*.cs（HumanizationEngine.cs 实测 37 行纯注释壳）；(2) 活跃引擎 Modules/Core/ActionExecutor.cs 的 GetHumanizationProfile 只有 4 个非误触键，整个 Modules/ 无任何概率触发或误触执行代码，故 §4.4/§2-6 "既有行为"的定性对活跃运行时不成立；(3) 计划书对同样只活在旧路径的曲线滑动在 R2-4(a) 专门立了移植项，对误触逻辑却零工作项，而第 201 行 EMULATOR_VERIFIED 又把"误触率"列为双 Soul 可辨指标——不移植则误触率恒 0，指标无载体，属计划书内部矛盾。唯一削弱点：发现末句"移植可能须触发异构复核+A12 流程"偏弱，因 §4.4 允许集已显式含既有误触参数，沿用既有语义量级只换载体可论证不超出既有参数空间；但此为次要推论，不影响核心成立。严重级别维持 important：影响 R2（全案最高风险里程碑）验收条款的可执行性，但修复面小（改定性 + 增一工作项或删一指标）。

**复核员独立实测证据**：Core/ScriptHelpers.cs:37-80（GetProfileConfig 四档含 prob_accidental_back/prob_scroll_back/prob_double_tap）、:123-129（ShouldTriggerProbabilistic）；Core/HumanizationEngine.cs 全文 37 行均为注释（wc -l 实测）；Modules/Core/ActionExecutor.cs:358/396/436/462（GetVar humanization_profile）、:1070-1102（GetHumanizationProfile 仅 base_delay_mult/delay_variance/tap_offset_max/swipe_jitter，无 prob_*）；grep Modules/ 全目录无 prob_/Probabilistic，唯一命中 Modules/SessionRunner.cs:249 为注释单词 accidentally；计划书 Docs/RebuildPlan_重构计划书.md:34（§2-2……

**修改建议**：§4.4 与 §2-6 现状定性改为"误触参数与触发逻辑存在于将退役的 ScriptHelpers/Reddit 旧脚本路径，活跃 ActionExecutor 路径无误触行为"；二选一：(A) 在 R2-4 缺口清单显式增列"误触触发逻辑（ShouldTriggerProbabilistic 等价物 + prob_* 键）移植进统一引擎"工作项，并注明沿用既有参数语义与量级、落在 §4.4 既有允许集内无须扩项流程；或 (B) 不移植，则从第 201 行 EMULATOR_VERIFIED 双 Soul 可辨指标中删除"误触率"。

---

### I4. logistic 底板与 ε=0.05 阈值冲突：参数不当时"爱好消退"永不发生

**级别**：🟠 Important　**来源维度**：硬要求1·Soul分化拟人化

**问题**：§4.2 用 logistic 1/(1+e^(-a(S-b))) 替换 Sum+Min(1,·)。当某兴趣证据经 30 天半衰期完全衰减（S→0）时，logistic 输出常数底板 1/(1+e^(ab))>0。若 a·b < ln19 ≈ 2.94，底板 > 0.05 = ε 阈值，则任何出现过哪怕一次的兴趣永远不会跌出投影——用户三条原话之一"爱好随经历消退"在纸面公式层面就可能失效。§10 只说"a、b 首版拍板，靠 R2 对照日志回调"，没有记录这个硬约束，回调时也未必会往这个方向查。

**证据**：计划书 §4.2：logistic 公式 + "ε 阈值 strength<0.05 不进投影"；§10 "logistic 参数 a、b 首版拍板"；现状 InterestReducer.cs:129-135（Sum+Min(1,·)）、:141-163（半衰期把 DecayedConfidence 推向 0，logistic(0) 即底板）；数学：1/(1+e^(ab))<0.05 ⟺ a·b>ln19≈2.944

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：尝试驳倒失败，三条驳倒路径全部走不通。(1) "计划书别处已覆盖"——不成立：全文 grep "strength" 仅 §4.2 一处（行 112），ε=0.05 与 logistic 的耦合约束在 §4.2、§5-R1-2（行 176 "设计先行：logistic 参数 a/b 定稿"只要求定稿、未记录任何约束）、§10（行 288 "a、b 首版拍板，靠 R2 对照日志回调"）均未出现；且 R2 对照日志的可证伪目标是"记忆是否改变判断"（行 115、201），R1/R2 验收（行 184、201-202）没有任何"单事件兴趣 N 天后跌出投影"的用例——即参数踩坑后计划书自带的校准回路确实查不到。(2) "证据与仓库不符"——不成立：我亲自打开 InterestReducer.cs，:129-135 确为 Sum + Math.Min(1m,·)，:141-163 确为半衰期指数衰减把 DecayedConfidence 推向 0，与发现所引完全一致。(3) "数学错误或吹毛求疵"——不成立：我实算验证 1/(1+e^(ab))<0.05 ⟺ ab>ln19≈2.944 正确，且 a=4,b=0.5 这类完全合理的首版拍板值给出底板 0.119>0.05，此时任何出现过一次的兴趣永不跌出投影，"ε 遗忘"机制（行 98 计划书自己列为兴趣动态三件之一）纸面失效，直接削弱用户硬要求 1 的"爱好随经历消退"。唯一可能的驳倒角度是 ε 或按施加在 logistic 之前的 decayed S 上（此时可正常跌出），但计划书未做此规定，"strength" 无定义，行 111 明确写 logistic 替换 Min(1,·) 饱和，最自然读法就是 strength=logistic 输出——歧义本身也需要发现建议的那一行澄清。发现自身措辞审慎（"参数不当时""可能失效"），未夸大。轻微保留：即便踩坑，strength 仍会衰减趋近底板，"减弱"部分成立，失效的只是完全跌出投影的 ε 遗忘；但 ε 遗忘是计划书明列机制且验收无覆盖，维持 important。

**复核员独立实测证据**：计划书 /Users/younghu/Documents/ZennoDroid_DSP/DSP_ZD/Docs/RebuildPlan_重构计划书.md：行 111（logistic 替换 Sum+Min(1,·)）、行 112（"ε 阈值 strength<0.05 不进投影"，全文唯一 strength 出现处，无定义）、行 176（设计先行仅要求 a/b 定稿）、行 288（"a、b 首版拍板，靠 R2 对照日志回调"）、行 115/184/201-202（对照日志与 R1/R2 验收均无兴趣跌出投影用例）。代码 /Users/younghu/Documents/ZennoDroid_DSP/DSP_ZD/Modules/interest-reducer/src/Dps.InterestReducer/InterestReducer.cs：行 129-135（orderedEvidence.Sum + Math.Min(1m, decayedConfidence)）、行 141-163（Decay：Math.Pow(0.5, age/halfLife) 乘 confidence，……

**修改建议**：在 §4.2 行 112 或 §5-R1-2"设计先行"定稿项中追加一句硬约束："logistic 参数须满足 a·b > ln19≈2.944（保证 S→0 时 logistic 输出低于 ε=0.05），或等价地对 logistic 输出做底板平移重标定 (f(S)-f(0))/(1-f(0))，或 S 低于下限时特判输出 0"；并在 R1 验收（行 184）增加一条可测用例："仅含单次证据的兴趣在半衰期衰减 N 天后 strength<ε、跌出投影"。改动约两行，不影响其他章节。

---

### I5. 宏观行为分化（活跃时段/会话节律/动作构成比）无落点，"Soul 差异化节奏"名实不符

**级别**：🟠 Important　**来源维度**：硬要求1·Soul分化拟人化

**问题**：§1/§2 的缺口预算把"Soul 差异化节奏"记在 SoulBehaviorPacket 名下，但 §4.3 枚举的扰动维度只有延迟倍率/点偏移/滑动弯曲度/打字速度四个微观维度。实测会话时长来自 session_plan 配置（session_duration_minutes）、动作类型权重 weight_base 来自全局 BehaviorConfig，§4.1 六层无一层覆盖"何时上线、每次玩多久、爱评论还是潜水"这些两个真人之间最可观察的宏观差异。即使 §2-1 把加权随机收编为 planner 策略插件，计划也没说该插件的动作权重变为 per-Soul。做完计划，两个 Soul 的宏观作息与动作构成仍完全同分布。

**证据**：计划书 §2 缺口预算"Soul 差异化节奏（SoulBehaviorPacket）"vs §4.3 仅四个微观维度；grep 计划书全文无"活跃时段/作息/会话时长分化"条目；Modules/SessionRunner.cs:806（session_duration_minutes 全局配置）、:826-829（weight_base 全局 BehaviorConfig）、:874（WeightedChoice）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：对抗复核未能驳倒该发现。实测证实：①计划书第 42 行把"Soul 差异化节奏"记在 SoulBehaviorPacket 名下，但第 120 行 §4.3 该 Packet 只扰动延迟倍率/点偏移/滑动弯曲度/打字速度四个微观维度，与 Core/ScriptHelpers.cs:37-80 四档画像参数（全为微观执行参数）一致；②§4.1 六层表与第 201 行 R2"双 Soul 行为可辨"验收指标（延迟分布/弯曲度/误触率/兴趣评分差异）均不含活跃时段、会话时长、动作构成比任何宏观维度，全文 grep 活跃时段/作息/session_duration/schedule/节律零命中，也未标 DEFERRED；③代码证据成立：会话时长来自 session_plan（SessionRunner.cs:806），动作权重 weight_base 逐动作取自全局 BehaviorConfig（:818-830），WeightedChoice 在 :875；④§4.1 ⑥ 的 per-Soul overlay 经实测只覆盖 RuleEngine.cs:33-35 的帖子评分 composite 权重（hot/activity/relevance），不覆盖动作类型权重。反驳尝试均失败：兴趣分化经 RuleEngine 门控降级（SessionRunner.cs:906-910）确会让实际动作构成因内容匹配度略有差异（"完全同分布"对动作构成措辞稍过），但这是内容条件性差异而非"爱评论还是潜水"的倾向性差异，且对作息/时长零影响，核心缺口成立。另发现加重事实：legacy 现有宏观节律通道（Main.cs:152-184 按 persona 的 stage_code 从 Config/StageConfig.json 派生 sessions_per_day/avg_session_minutes/night_activity_probability）在计划书 persona v2（§4.6 只枚举 interest_seeds/写作风格/behavior_archetype/de……

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:21,42（"Soul 节奏/Soul 差异化节奏"承诺）、:91-102（§4.1 六层无宏观维度）、:120（§4.3 仅四微观维度）、:201（可辨指标无宏观项）、:135（§4.6 persona v2 枚举无节律字段）；全文 grep 活跃时段/作息/session_duration/schedule/节律零命中。Modules/SessionRunner.cs:806（session_duration_minutes 取自 session_plan_json）、:818-830（weight_base 取自全局 BehaviorConfig）、:875（WeightedChoice）、:906-910（RuleEngine 拒绝降级 browse）。Core/ScriptHelpers.cs:37-80（四档画像全微观参数）。Modules/RuleEngine.cs:33-35（0.3/0.2/0.5 为帖子评分权重非动作权重）。Modules/Main.cs:152-184 + Config/StageConfig.j……

**修改建议**：二选一的最小修改：(A) §4.3 SoulBehaviorPacket 增加宏观维度——per-Soul 会话时长分布参数、动作权重 overlay（叠加在全局 weight_base 上，与 §4.1 ⑥ 决策权重 overlay 同机制同批 R2-3 实现）、活跃时段窗口；同时在 §4.6 或 §4.3 明确 legacy StageConfig 节律通道（Main.cs:152-184）的继任归属；R2 验收"双 Soul 可辨"指标补至少一项宏观指标。(B) 若判定 2 设备低频样本下宏观分化统计上不可辨（§10 已有此顾虑），则在 §4 显式增一行"宏观节律分化 DEFERRED（理由：样本量不足以可辨验收）"，并把 §1/§2 的"Soul 差异化节奏"措辞改为"Soul 差异化执行风格"，消除名实不符。

---

### I6. 草稿生成组件全计划缺位，persona v2 的"写作风格"字段无消费路径；观点漂移 DEFERRED 的理由与草稿链自相矛盾

**级别**：🟠 Important　**来源维度**：硬要求1·Soul分化拟人化

**问题**：R1-4 定义 speech.draft/v1 状态机、R2 验收要求"产出 ≥1 草稿"且用户抽查"相关/自然"，但全计划没有任何小节定义草稿正文由哪个组件生成、用什么模型、如何注入 per-Soul 写作风格——§4.1 ③ 让 persona v2 承载"写作风格"，却无任何消费方接线描述。两个 Soul 若用同一提示词生成草稿会同声同气，直接违背"像两个不同的人"。同时 §4.2 把观点漂移 DEFERRED 的理由是"Phase-1 零对外写=观点无表达出口"，但草稿本身就是表达出口（还进用户抽查），理由不自洽——推迟结论可以成立，理由需要重写。

**证据**：计划书 §5-R1-4（speech.draft 只有合同与状态机，无生成器）、§5-R2 验收（"草稿质量抽查……相关/自然"）、§4.1 ③（persona v2 承载写作风格，无消费者）、§4.2 观点漂移行（"零对外写=观点无表达出口，纯装饰"）；grep 计划书全文无"草稿生成/起草组件/drafter"条目

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：对抗复核未能驳倒该发现。三条反驳路径全部失败：(a) 计划书全文 grep 确认"写作风格"仅在 §4.1 ③（第 99 行）出现一次，无消费方；草稿相关条目（第 178/180/201 行）只有合同状态机、传输 kind 和验收要求，无任何工作项定义草稿正文的生成组件、模型选择或 per-Soul 风格注入。(b) 仓库实测无现成生成器可沿用：legacy 唯一评论文本路径是 SessionRunner 的固定模板回退（"Thanks for sharing this." 等三条，ai_comment_text 全仓无其他 setter），与 Soul/persona 零关联；planner 模块只有 ShadowActionPlanner.cs 无生成能力；而目标架构第 304 行禁止在 ZennoDroid（边缘）生成评论，草稿只能大脑侧生成——大脑侧却无此组件且计划未新建。(c) 不属可推迟细节：R2 EMULATOR_VERIFIED 验收硬性要求"产出 ≥1 草稿"且用户抽 10 条按"相关/自然"评 ≥7，固定模板无法满足，这是验收依赖缺口。附带坐实：第 201 行"双 Soul 行为可辨"指标（延迟分布/弯曲度/误触率/兴趣评分差异）确实不含草稿风格维度，硬要求 1"像两个不同的人"在言语维度无落地路径。第二子项（第 113 行观点漂移 DEFERRED 理由"零对外写=观点无表达出口"）与第 201 行把草稿纳入表达质量抽查确有张力，发现已克制地只要求重写理由不推翻结论，无夸大。严重级别维持 important：不触红线、单个增补工作项可修，但不修则 R2 验收无法达成。

**复核员独立实测证据**：计划书 Docs/RebuildPlan_重构计划书.md:99（persona v2 承载写作风格，全文唯一出现，无消费方）、:178（R1-4 仅草稿合同与状态机）、:180（save_draft 仅为 exchange kind 传输通道）、:201（R2 验收要求 ≥1 草稿 + 相关/自然 ≥7 抽查；双 Soul 可辨指标不含草稿风格）、:113（观点漂移 DEFERRED 理由）。仓库：Modules/SessionRunner.cs:2835-2886（EnsureCommentTextAvailable + BuildFallbackCommentText 固定模板是唯一评论文本来源，ai_comment_text 无其他 setter，grep 全仓零命中）；Modules/planner/src/Dps.Planner/ 仅含 ShadowActionPlanner.cs，grep 无 prompt/openai/gemini/Http；Docs/Architecture/TargetArchitecture_目标架构.md:304（禁止在 ZennoDroid ……

**修改建议**：在 §5-R2 增补"草稿生成器"工作项：明确生成归属大脑侧（modern 侧新建或扩展现有模块）、AI 调用复用"AI 密钥唯一驻留大脑侧"的凭证边界、提示词模板注入 persona v2 写作风格 + 兴趣上下文（与 §4.6 二选一方案联动）；把"两 Soul 草稿风格可区分"加入 R2 第 201 行双 Soul 可辨指标集；§4.2 第 113 行观点漂移 DEFERRED 理由改写为"Phase-1 草稿量少且零平台发布、无公开反馈回路，漂移无校准信号"。

---

### I7. "删 11 全清后无悬空问题"论断不完整：全部 23 个保留模块 manifest 声明已删除的 factory-instruction-resolver，schema 还以 const 钉死

**级别**：🟠 Important　**来源维度**：锚点核验·v2模块、硬要求2·精简生死簿

**问题**：§3.4 步骤 5（Codex 补 #3）称删 11 后"无悬空问题（这是删 11 优于删 8 的关键）"。该论断对 module.yaml 通信边（receipt/command 边）成立（实测依赖图 factory 子图确实封闭），但对 agents 元数据不成立：23 个保留模块的 module.yaml 全部声明 "resolver": "factory-instruction-resolver"，且 module-manifest.schema.json:325 以 const 将该字段钉死为这个已删模块名。删 11 后门禁不会炸（phase0 不解析该字段指向的模块、schema const 仍满足——已核实 phase0.resolve_instruction_receipt 不用 agents.resolver），但结果是全仓治理元数据永久指向幽灵模块，与要求 2 的精简精神相悖；日后清理 = 同改 23 个 manifest + governance schema 的 P2 全局串行变更 + 三快照再生。八步手术对此零提及，执行者会原样保留。

**证据**：grep -l '"resolver": "factory-instruction-resolver"' Modules/*/module.yaml | grep -vc factory → 23（如 Modules/audit-metrics/module.yaml:30、Modules/legacy-runtime-adapter/module.yaml:234）；governance/schemas/module-manifest.schema.json:325 "resolver": { "const": "factory-instruction-resolver" }，:277 track 枚举含 "factory"；Tools/ci/phase0.py:3534 起的 resolve_instruction_receipt 构造收据不引用 agents.resolver（:3590-3650 实测）。

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的全部技术论断经我独立实测坐实：(1) 全部 23 个保留模块的 module.yaml 声明 "resolver": "factory-instruction-resolver"（34 个 manifest 中 11 个 factory、23 个保留，grep 命中恰为 23）；(2) schema 以 const 钉死该值，且 agents 为顶层 required、resolver 为块内 required、additionalProperties:false——清理必须同改 schema；(3) phase0 确实不读 agents.resolver（全文 resolver 引用仅 yaml.resolver，resolve_instruction_receipt 用独立字符串常量），门禁不炸，与发现自述一致；(4) generate_governance.py 零处引用 resolver 或该 schema，八步中的三快照再生不会自愈，幽灵引用在八步手术后永久留存；(5) 通读计划书全文 351 行，§3.4-5 "全删后无悬空问题（这是删 11 优于删 8 的关键）"是无限定绝对表述，八步无一步触及此项，§11 第 12 行重复同一论断，别处无覆盖。加重因素：§3.4-2 已把收据规程改挂 phase0 自有收据，使 agents.resolver 声明语义作废，构成计划书内部矛盾；schema const 还会强迫未来新模块继续声明幽灵 resolver。该项目升级方法论（§7-1、R3-3）以"先读 module.yaml"为机器强制前提，23 个 manifest 携带指向已删模块的治理声明会误导后续 AI 升级会话，与要求 2 精简精神相悖。延后修复代价真实（schema 属 governance/ 变更，按 §6 所引 phase0.py:3547-3552 收据 scope 扩全模块；改 manifest 丧失 P0 并行资格），而并入本就全局串行的 R0-2 批次几乎零成本。曾尝试以"§3.4-5 仅指通信边"驳倒，但原文措辞无此限定且执行……

**复核员独立实测证据**：grep -l '"resolver": "factory-instruction-resolver"' Modules/*/module.yaml → 34 个中 23 个为非 factory（全部保留模块），含 Modules/audit-metrics/module.yaml:30、Modules/legacy-runtime-adapter/module.yaml:234（行号亲验一致）；governance/schemas/module-manifest.schema.json:325 "resolver": { "const": "factory-instruction-resolver" }，:277 track 枚举含 "factory"（实测仅 11 个 factory-* 模块使用 track:factory，保留模块零使用），:7-25 顶层 required 含 "agents"，agents 块 required 含 "resolver" 且 additionalProperties:false；Tools/ci/phase0.py:3534 resolve_……

**修改建议**：在 §3.4 二选一写明：(a)【推荐】R0-2 原子批次内同改：module-manifest.schema.json 的 agents.resolver const 改为 "phase0-instruction-resolver"（或放宽为 minLength:1 字符串）+ 顺手删 :277 track 枚举中删后无人使用的 "factory" 值 + 23 个保留 manifest 的 agents.resolver 同步改写 + 三快照再生（该批次本就是 P2/P3 全局串行独占 + 异构复核，边际成本≈0）；或 (b) 显式豁免：把 §3.4-5 改写为"通信边（receipt/command）无悬空；23 个保留 manifest 的 agents.resolver 及 schema const 的幽灵引用暂留，随 R3 收据规程落地统一清理"，消除"无悬空"的绝对表述并在 R3 节增加对应清理项。

---

### I8. landing 协议无任何机器挡板：现状 CI 只有 phase0 一个 job，计划未含 required checks/强制 rebase/合并 HEAD 重跑的任何机器化工作项

**级别**：🟠 Important　**来源维度**：硬要求3·单独/并行升级

**问题**：§6 landing 协议的三条纪律（合并后在合并 HEAD 全量重跑两级门禁并重取收据、第二分支必须 rebase 后重跑、两个 P0 同周落地先到先得排队）目前全部是口头纪律：.github/workflows/static-ci.yml 是唯一 workflow，只跑 run_phase0_gate.py（PR + push main），候选门禁完全不在 CI；R3 计划新增的两个候选 job 也只是'加 job'，全案没有一处提到 dps2 仓库的 ruleset/branch protection、required status checks、strict up-to-date（或 merge queue）——没有 strict 模式，第二分支不 rebase 也能合入，PR 检查在陈旧 base 上绿过即算数；候选门禁连 push-to-main 触发都没有规划，合并 HEAD 的'全量重跑'无人强制。收据机制也不兜底：validate_instruction_receipt 只对 receipt 自身 baseline 重算比对，phase0/候选门禁只要求 baseline 是 HEAD 祖先，main 前进不会使旧收据失效。dps2 是全新仓库，保护规则从零开始，而 CODEOWNERS 文件自己都写明'不能替代 required GitHub ruleset'。另外 §7 Kill switch 设计'KILL_SWITCH 文件存在则 CI 门禁 FAIL'与'置位提交需经门禁合入'相矛盾，隐含置位走直推 main——恰好印证没有分支保护挡板。

**证据**：.github/workflows/static-ci.yml:1-63（唯一 workflow，仅 phase0，无候选门禁 job、无 required-check/strict 配置）；.github/CODEOWNERS:1-3（'This file does not replace the required GitHub ruleset'）；phase0.py:3667-3690（validate_instruction_receipt 仅对 receipt 自身 baseline 重算）；run_candidate_gate.py:2982-2985（仅要求 baseline 为 ancestor，不要求为 main HEAD）；计划书 §6 landing 协议、§5-R3 原文均无 ruleset/required-check/merge-queue 工作项

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：逐项实测后无法驳倒该发现，核心指控全部坐实。(1) .github/workflows/ 下确实只有 static-ci.yml 一个 workflow，单一 phase0 job，触发为 pull_request + push main，候选门禁完全不在 CI。(2) 计划书全文 grep "ruleset/分支保护/required/strict/merge queue/直推" 仅命中 §7-6"required 检查只认 PASS"与硬停止条款——这两处是解读检查结果的纪律，预设 required checks 存在，但全案没有任何一处工作项去在 dps2（全新仓库，推送目标 github.com/HelloYoung2025/DPS2.0，保护规则从零开始）配置 ruleset/required checks/strict up-to-date；R0-5"锁定推送 dps2"是锁 push 目标非分支保护；R3 两个 CI job 只写"加 job"且 contract job 用 --base <PR base>（PR 触发语义），无 required 标记、无 push-to-main 触发规划。§6 landing 协议三条（合并 HEAD 重跑、rebase 后重跑、先到先得排队）确为纯口头纪律。(3) 收据不兜底属实：phase0.py:3667-3701 validate_instruction_receipt 只按 receipt 自带 baseline_commit 重算比对；run_candidate_gate.py:2982-2985 仅要求 baseline 是 HEAD 祖先（merge-base --is-ancestor），main 前进不会使陈旧 base 上的绿证据失效。(4) Kill switch 张力属实：§7 定义 KILL_SWITCH 为"受跟踪文件、存在则 CI 门禁 FAIL"，若未来配了 required checks，置位提交自身 CI 必红无法经 PR 合入，隐含依赖直推 main 常开——恰与缺挡板互证。(……

**复核员独立实测证据**：实测：.github/workflows/static-ci.yml:1-63（唯一 workflow，jobs 仅 phase0，on: pull_request + push main，步骤只跑 Tools/ci/run_phase0_gate.py）；.github/CODEOWNERS:1-3（"This file does not replace the required GitHub ruleset"原文在）；Tools/ci/phase0.py:3667-3701（validate_instruction_receipt 以 receipt.get("baseline_commit") 调 resolve_instruction_receipt 重算后逐键比对，无"baseline 须为 main HEAD"约束）；Tools/ci/run_candidate_gate.py:2982-2985（trust_eligible = baseline != head and merge-base --is-ancestor and trust_match，仅祖先要求）；pha……

**修改建议**：在 R0-5（锁定推送 dps2 同批）增补工作项：①dps2 配置 ruleset——main 禁直推/强推，required checks = phase0 + R3 候选 contract job，勾选 require branches up to date（或启用 merge queue），配置动作与截图/API 输出落 evidence-anchors；②显式定义 KILL_SWITCH 置位路径（ruleset 中唯一 bypass 例外或专用置位分支+专用轻检查），避免与 required checks 死锁；③R3-1/R3-2 明确候选 contract job 同时挂 push:main 触发，使"合并 HEAD 全量重跑"至少机器化为合并后自动检出；④裁决 Docs/Operations/RepositoryProtection_仓库保护.md 的去留——其"两名人类审批"条款与异构 AI 复核替代方案冲突，须按计划书 §11-3 口径修订该文档并在 dps2 落地其余可行条款，或如实标注单人仓库降级并给本地等效（合流前置脚本强制在合并 HEAD 重跑两级门禁）。

---

### I9. 配置面是'改 A 不坏 B'机器链的盲区，且 Config/Data/onboarder 单一 owner 使'开发可并行'的实际空间远小于表面

**级别**：🟠 Important　**来源维度**：硬要求3·单独/并行升级

**问题**：两点叠加：①Config/**、Configs/Manifests/**、Data/**、Tools/app_onboarder/** 全部归 legacy-runtime-adapter 一个模块所有，计划书 R1/R2 的主力工作流（R2-6 选择器校准写 PlatformsConfig、R2-3 session_gate 写 BehaviorConfig、R2-4c 单文件加 APP Loader 配置、R2-4d 探索入库、Data/Keywords 兴趣种子）scope 全部相交于这一个模块——按 §6 自己'scope 不相交才可并行开发'的口径，这些工作流之间实际全不可并行，计划书未披露该结构性事实；②该模块的 tests.suites 只有 static/unit（字节基线与源码断言，仅覆盖 .cs）和 command:null 的 windows/device 占位，无任何 contract/integration 套件，字节基线 _is_legacy_csharp_path 只认 .cs 后缀——即 operations.json/PlatformsConfig/BehaviorConfig 的内容改动不被 phase0（只查 ownership 与结构）、字节基线、候选门禁任何一级校验。两条并行会话各自改配置、git 无文本冲突时，语义冲突（如同改一个 operation 定义）零机器兜底，直接违背硬要求 3 的核心承诺，而配置面恰是初衷 2/3（统一编排、配置加 APP）的载体。

**证据**：Modules/legacy-runtime-adapter/module.yaml:37-43（ownership 含 Config/**、Configs/Manifests/**、Data/**、Tools/app_onboarder/**）、:155-204（suites 仅 static/unit/windows/device，无 contract/integration）；verify_sessionrunner_baseline.py:388-401（_is_legacy_csharp_path 仅 .cs）；phase0.py:89-101（Config 等仅作 KNOWN_RUNTIME_ROOTS 做 ownership 检查）；candidate-test-policy.yaml 全文无任何 Config 校验套件

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的核心论断②经实测完全坐实：Config/**、Configs/Manifests/**、Data/**、Tools/app_onboarder/** 全归 legacy-runtime-adapter 单一模块，该模块 suites 仅有针对 .cs 的 static/unit 与 command:null 占位，字节基线 _is_legacy_csharp_path 只认 .cs 后缀，phase0 对这些路径只做 ownership 归属检查（KNOWN_RUNTIME_ROOTS 全文件唯一用途在 _runtime_scope_files），candidate-test-policy.yaml 无任何配置或 legacy-runtime-adapter 套件——即纯配置改动在计划书 §6 宣称的'改 A 不坏 B'机器链（phase0+候选门禁）上零内容校验。计划书仅有运行时安全网（SELECTOR_STALE、未知 selector fail-closed）和人工审批（探索入库），无门禁级机器挡板；而 R2-4c 单文件加 APP 恰把配置变成主变更面，PlatformsConfig.json 还是多平台共用单文件，一处语法错误即全平台失效。我尝试用两条路径驳倒：①'未披露单 owner 串行'确实偏弱（§6 并行表 P0 要求'两个无依赖模块'，同模块改动按表隐含串行，R1/R2 本就顺序推进），该部分应视为未披露的结构性后果而非错误；②配置面门禁盲区无法驳倒——计划书全文（含 §5-R2-6、§7 施工纪律、§10 待实测项）没有任何条目为配置内容增加机器校验。②单独即支撑发现成立，severity 维持 important。

**复核员独立实测证据**：Modules/legacy-runtime-adapter/module.yaml:39-42（owned 含 Config/**、Configs/Manifests/**、Data/**、Tools/app_onboarder/**）；module.yaml:155-204（suites 仅 4 个 .cs 向 static/unit + :192/:200 两个 command:null 占位，无 contract/integration）；verify_sessionrunner_baseline.py:388-401（:390 suffix != ".cs" 即 False）；Tools/ci/phase0.py:89-101（KNOWN_RUNTIME_ROOTS）与 :2043-2055（唯一使用点 _runtime_scope_files，仅路径归属）；governance/policies/candidate-test-policy.yaml grep "legacy|config|operations|platforms" 零命中；Tools/ci/ 全目录 gre……

**修改建议**：①§6 并行判定表后加一行披露：凡触 Config/**、Configs/Manifests/**、Data/**、Tools/app_onboarder/** 的工作流 scope 均落 legacy-runtime-adapter 单一模块，彼此按 P0 口径不可并行开发，配置面工作流一律串行排队；②在 §5-R2-4c（单文件加 APP Loader）或 §6 新增一个轻量配置校验套件工作项：JSON Schema 校验 Config/**（JSON 可解析 + operations 步骤指令在 ActionExecutor 13 指令集内 + operation id 唯一 + PlatformsConfig selector 必填字段齐全 + 跨文件引用完整性），以 static 套件挂入 legacy-runtime-adapter module.yaml 并登记 candidate-test-policy.yaml，随 R2-4c 同批交付——几百行以内，使配置成为主变更面的同时被'改 A 不坏 B'机器链覆盖。

---

### I10. 初衷 #1"意图驱动大脑-手分层"定性 LOCKED 不实：IntentTranslator→ZDCommand→ZennoDroidAdapter 链全仓含全部 git 历史零调用，从未接线、从未运行验证

**级别**：🟠 Important　**来源维度**：硬要求4·设计初衷八条

**问题**：计划书 §2 行 1 以 IntentTranslator.cs:37-104 为证据把初衷 #1 定性为 LOCKED（"已写成可用代码并经 CHANGELOG 迭代验证"，动作="解锁走七项桥"）。实测：全仓（含 cac7ccb 等全部历史提交）没有任何代码调用 IntentTranslator.Translate 或 ZennoDroidAdapter.Execute*，两文件只出现在 OwnCode coreFiles 编译清单字符串里；生产回路的"意图"只是字符串映射（action→intent 名→按意图选 operations 序列），执行层由 ActionExecutor 直驱 DroidInstance.Input，从不经过 Intent/ZDCommand 对象链。因此"解锁走七项桥"不会让大脑-手分层运转——该层需要的是首次接线设计（R2 桥世界里 TAP_SELECTOR 等步骤命令由谁翻译执行、IntentTranslator/ZennoDroidAdapter 是收编还是死码），而计划书既无接线方案也未将其列入生死簿。

**证据**：grep 全仓 "IntentTranslator.Translate|ZennoDroidAdapter.Execute" 零命中（仅两文件自身）；git grep cac7ccb 与 git log --all -S "IntentTranslator.Translate" 均空；ZDProjects/*_OwnCode.cs:104 等仅在 coreFiles 字符串数组中含文件名；SessionRunner.cs:875-876/:2491-2514 意图仅为字符串映射；cac7ccb 的 ExecuteWithUnifiedEngine 原实现直接调 ActionExecutor.Execute，不经 ZDCommand

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的核心事实全部由我独立实测坐实：(1) IntentTranslator/ZennoDroidAdapter 两类的全部公开方法在全部 38 个历史提交中代码层零调用；(2) 两文件仅以 coreFiles 编译清单字符串存在；(3) 生产回路实为字符串映射直驱 ActionExecutor；(4) 三道锁门禁的是 legacy 入口，解锁后对象链依然无调用者；(5) 计划书全文无该链处置裁决，与 AppExplorer_v2 列删、ManifestLoader 显式逻辑退役的判例不一致。连带伤：R2-4(a) 指名的 ExecuteSwipe 是私有方法且入口从未被调，初衷 #6 引用的拟人化代码同样从未运行。发现唯一偏强处是"解锁不会让大脑-手分层运转"——字符串映射版分层解锁后确会运行，且 R2 目标架构不结构性依赖对象链，故不升 critical；但初衷 #1 主证据不实进入事实底座（§7-10 施工会话"信账本"）且违反硬要求 2 生死簿完备性，高于 minor，维持 important。

**复核员独立实测证据**：全仓 grep：IntentTranslator 自身文件外仅 coreFiles 清单（ZDProjects/SessionRunner_OwnCode.cs:107、ZDProjects/ModuleLoader.cs:48,182 等 11 处）与测试文件名列表（ZDProjects/Tests/MultiPlatform_IntegrationTest.cs:393）；ZennoDroidAdapter 成员调用零命中。git rev-list --all（38 提交）逐方法 grep 全零；git log --all -S "IntentTranslator.Translate" 仅命中 README.md:102（示例代码）与 .omo/layers/*.yaml。生产路径：Modules/SessionRunner.cs:874-875（WeightedChoice→ResolveIntentForAction）、:2492-2514（字符串 action_to_intent 映射）、:928→:2390（ExecuteWithUnifiedEngine→ActionEx……

**修改建议**：最小修改三处：① §2 初衷 #1 现状列拆为两态："意图字符串映射链（SessionRunner.cs:874-875/:2492-2514→ActionExecutor）LOCKED（曾运行、CHANGELOG 验证）；IntentTranslator→ZDCommand→ZennoDroidAdapter 对象链 DISCONNECTED（全历史零调用，仅存于 coreFiles 编译清单）"；② §3.2 增一行处置裁决：对象链五文件（Intent/ZDCommand/ZDResult/IntentTranslator/ZennoDroidAdapter 中未被收编部分）比照 ManifestLoader 先例逻辑退役、物理保留（物理删除需改全部 OwnCode 清单 + 字节基线 anchor 重签，对惰性文件不值），若 R2-4(a) 决定收编 ZennoDroidAdapter 则仅收编该文件并注明是首次接线；③ R2-4(a) 补一句：ZennoDroidAdapter.ExecuteSwipe 为私有方法且 Adapter 从未接线，曲线滑动统一二选一——首次接线 ActionExecutor→ZDCommand→Adapter，或把 SwipeCurved 直接做进 ActionExecutor.ApplyHumanizedSwipe（后者更符 Simplicity First）。同步把 §2 初衷 #6 的 ZennoDroidAdapter.cs:168-270 证据改标"未运行代码"。

---

### I11. request_vision_verdict 只有 kind 名，缺三件硬设计：截图字节如何到达大脑侧、往返时延/超时语义、verdict 超时后恢复阶梯怎么走

**级别**：🟠 Important　**来源维度**：红线约束合规、硬要求4·设计初衷八条

**问题**：视觉验证上移大脑侧后，大脑要看到像素才能出 verdict。实测现有 exchange v1 是 poll-only 有界 JSON 信封（text/native_detail ≤4096 字符、timeoutMs 15000），全部边缘合同（zenno-bridge/windows-edge-worker/supervisor/evidence-service）没有任何截图/blob 传输通道；计划书只说"边缘只传截图引用"，未定义引用指向哪个存储、大脑侧凭什么取到字节（边缘 denied gbrain，截图在 Windows 边缘本地盘）。恢复阶梯 4-5 次触发 VisionAssist 原本是边缘直调 AI（默认 60s 超时），改为桥往返后：verdict 超时算不算一次 VisionAssist 消耗、阶梯是否降级到 FallbackScript、会话是否阻塞等待——全部未定义；§10 待实测项列了记忆链 NAS P95 却没有 vision verdict 往返时延。90%+ 数字不复验不承诺的处理是对的，但恢复阶梯的时延与流畅性成立与否，取决于这三件未设计的事。

**证据**：Modules/zenno-bridge/contracts/provided/edge.bridge.exchange.v1.schema.json:43（kind 仅 POLL/NATIVE_RESULT）、:53-61（selector/text/native_detail 上限 2048/4096）；zenno-bridge/module.yaml exchange 边 timeoutMs 15000、"bounded versioned JSON envelope"；ls 三个边缘模块 contracts/provided 无任何 blob/screenshot 合同；SmartOrchestrator.cs:229-235（VisionAssist 档）；AIService.cs timeoutMs 默认 60000；计划书 §2-5/§5-R1-6/§10 均无截图通道与 verdict 时延条目

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：试图驳倒失败，发现的三项缺失全部实测坐实。(1) 截图通道确实不存在：exchange v1 schema 的 exchange_kind 仅 POLL/NATIVE_RESULT，全部字符串字段有界（selector 2048、text/native_detail 4096），module.yaml 两条 exchange 边 timeoutMs 均为 15000 且信任边界明写 "bounded versioned JSON envelope"；我 grep 了 zenno-bridge/windows-edge-worker/windows-edge-supervisor/evidence-service/edge-local-journal 五个模块的全部 contracts，screenshot/image/blob/base64 命中项全部是 RSA 签名的 canonical Base64 校验，无一是图像传输字段；windows-edge-worker 的 contracts/provided 只有 README。计划书 §2-5 说"边缘只传截图引用"、§5-R1-6 只列 kind 名、§5-R2-3 重复"只传截图不持 AI 凭证"，全文确实未定义引用指向何存储、大脑侧（按 R0-1 在 Mac，截图在 Windows 边缘本地盘）凭什么取到字节。(2) 超时/阶梯语义缺失且存在内在张力：SmartOrchestrator.cs:229-235 确为第 4-5 次失败触发 VisionAssist，AIService.cs:76/:181 默认 60000ms——大脑侧 AI 调用可长达 60s，而桥信封 timeoutMs 只有 15s，verdict 超时是否计一次 VisionAssist、是否降级 FallbackScript、会话是否阻塞，全文无一字。尤其有力的反证驳斥失败点：计划书对同形态问题 query_memories 明确定义了超时语义（第 115 行"超时/失败→无记忆滑行，记 MEMORY_MISS，毫秒级预算不等满 15s"……

**复核员独立实测证据**：/Users/younghu/Documents/ZennoDroid_DSP/DSP_ZD/Modules/zenno-bridge/contracts/provided/edge.bridge.exchange.v1.schema.json:43（exchange_kind 仅 POLL/NATIVE_RESULT）、:53-54/:61（selector 2048、text 4096、native_detail 4096）；Modules/zenno-bridge/module.yaml:75,89（timeoutMs 15000）、:113（bounded versioned JSON envelope）；grep 实测 zenno-bridge/windows-edge-worker/windows-edge-supervisor/evidence-service/edge-local-journal 五模块 contracts 无任何截图/blob 字段（base64 命中全为签名校验，如 DrainDirectiveContracts.cs:419-427）；window……

**修改建议**：在 §5-R1-6 增补三项并与既有条目对齐：① 截图传输设计——定义截图引用的存储归属与大脑侧取字节协议（候选：supervisor/evidence 侧上传通道或引用+拉取，含大小上限与保留策略），并核对不污染 F6/F7 证据束（与该条已有的 privacy_class 条款同批设计）；② request_vision_verdict 超时语义——超时=计一次 VisionAssist 失败、按 SmartOrchestrator 既有阶梯降级 FallbackScript、会话不无限阻塞（仿照 §4.2 query_memories 的 MEMORY_MISS 模式），并明确 15s 桥信封与 60s AI 调用的预算关系；③ §10 待实测项加一条"vision verdict 端到端往返 P95（Mac 大脑侧↔Windows 边缘）"。

---

### I12. 曲线滑动收敛点选错：生产滑动路径是 ActionExecutor.ApplyHumanizedSwipe（直线 input.Swipe），只改零调用的 ZennoDroidAdapter.ExecuteSwipe 不会让任何生产滑动变曲线

**级别**：🟠 Important　**来源维度**：硬要求4·设计初衷八条

**问题**：计划书 §2 行 6 说"统一用 ZD 原生 SwipeCurved 进 ZennoDroidAdapter.ExecuteSwipe，消除直线/曲线双路径"。实测滑动实为三路径：①Core/ScriptHelpers.cs:119 SwipeCurved（曲线，被绕开旧路径）；②ZennoDroidAdapter.ExecuteSwipe:238 input.Swipe（直线，但该文件全仓零调用者，见发现 2）；③ActionExecutor.ApplyHumanizedSwipe:1168 input.Swipe（直线）——而 ③ 才是统一引擎 StepSwipe/StepScroll 的生产滑动执行点（:398/:438），browse 场景刷 feed 的高频拟人化手势全走这里。按计划书写法施工后，生产路径滑动仍是直线，初衷 #6 的曲线滑动落空。另外 SwipeCurved(x1,y1,bending,x2,y2) 本身就是两点+弯曲度，两点场景无需"多段拼接"兜底；§10 的多点待实测只对路径型滑动有意义。

**证据**：ActionExecutor.cs:1153-1169（ApplyHumanizedSwipe 结尾 input.Swipe(x1,y1,x2,y2,duration) 直线）、:398/:438（StepSwipe/StepScroll 调用点）；ZennoDroidAdapter.cs:238（input.Swipe 直线）且全仓 grep ZennoDroidAdapter.Execute 零调用；Core/ScriptHelpers.cs:119（SwipeCurved 全仓唯一出现）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的三个核心论断全部经我独立实测坐实：(1) 生产滑动执行点是 ActionExecutor.StepSwipe/StepScroll→ApplyHumanizedSwipe，结尾 input.Swipe 直线（reddit_operations.json 19 处 swipe/scroll 步骤全走此路径）；(2) ZennoDroidAdapter.Execute/ExecuteSwipe 全仓零调用（grep 零命中，仅出现在编译清单与注释），其上游 IntentTranslator.Translate 同样零调用，整条 Intent→ZDCommand→Adapter 链休眠，而计划书全文无任何把 ActionExecutor 滑动步骤接到 adapter 的工作项；(3) 内部矛盾成立——§4.3 SoulBehaviorPacket 把"滑动弯曲度"经 humanization_profile 下发，该 profile 唯一消费者是 ActionExecutor，但 §2 行 6/§5-R2-4(a) 却把 SwipeCurved 收敛进零调用的 ZennoDroidAdapter.ExecuteSwipe，按文施工弯曲度永不生效，初衷 #6 落空。我尝试的最强反驳（"目标架构解锁后会路由经 adapter"）被 §2 行 2 否决：ActionExecutor 是保留的唯一统一解释器且计划书无接通 adapter 的施工项。次要论点（SwipeCurved 两点+bending 已是曲线）经 ScriptHelpers.cs:119 签名证实，但 §10 属待实测清单，该部分仅是收窄建议非独立缺陷。维持 important：不触红线，R2 验收"弯曲度可辨"指标可能晚期兜底，但已是里程碑末期返工。

**复核员独立实测证据**：Modules/Core/ActionExecutor.cs:398/:438（StepSwipe/StepScroll 调 ApplyHumanizedSwipe）、:403/:443（非 humanized 直接 input.Swipe）、:1153-1168（ApplyHumanizedSwipe 结尾 input.Swipe 直线，仅端点抖动）、:396/:436（humanization_profile 唯一消费点）；Modules/Core/ZennoDroidAdapter.cs:214（ExecuteSwipe）、:238（input.Swipe 直线），grep "ZennoDroidAdapter.Execute" 全仓零命中（仅 OwnCode coreFiles 编译清单与 ZDCommand.cs:11-12/ZDResult.cs:7 注释）；IntentTranslator.Translate 全仓零调用者；Config/Operations/reddit_operations.json:13 等 19 处 swipe/scroll 步骤 humanize……

**修改建议**：最小修改两处：① §2 行 6 与 §5-R2-4(a) 的收敛点由"ZennoDroidAdapter.ExecuteSwipe"改为"ActionExecutor 滑动步骤（StepSwipe/StepScroll 的 ApplyHumanizedSwipe 换 ZD 原生 SwipeCurved，bending 从 humanization_profile 读取，与 §4.3 SoulBehaviorPacket 弯曲度扰动对齐）"——或者若坚持收敛进 adapter，则显式新增"ActionExecutor 滑动步骤下沉调用 ZennoDroidAdapter.ExecuteSwipe"工作项并列入 R2 批次；② §10 待实测项收窄为"路径型多点滑动是否支持"，并注明两点+bending 场景 SwipeCurved 已满足、无需多段拼接兜底。

---

### I13. 单文件加 APP 的触点收敛清单不全：漏掉活的 Data/Keywords/{platform}/interests.json+triggers.json，反把零调用的 Manifest 计为触点，且 §2 与附录 B 计数互相矛盾

**级别**：🟠 Important　**来源维度**：硬要求4·设计初衷八条

**问题**：§2 行 3 称加 APP 需 5 处触点（ManifestLoader.cs:140 + PlatformsConfig/operations/intents/device_app_mapping），app_onboarder 自动生成①②③、④一行、⑤一次审批。实测：ManifestLoader.Load 全仓零调用（死触点，与 §3.2 自己的"惰性文件"裁决矛盾）；而 SessionRunner 每平台还强制加载 Data/Keywords/{platform}/interests.json 和 triggers.json（6 处调用点），这是 RuleEngine relevance 的输入，且 §4.2 明确保留静态兴趣作为 MEMORY_MISS 回退——即新 APP 在记忆动态接通前后都需要种子文件。该活触点不在 Loader 的 ①-⑤ 覆盖面里，§10 虽自认"instagram 兴趣种子需补"却没有回填到收敛设计。附录 B 又写"加 APP 需 6 文件"，与 §2 的"5 处触点"计数不一致。结果是"配置加新 APP=单文件"的承诺对任何新 APP 都不成立。

**证据**：SessionRunner.cs:496/:501/:781/:793/:1369/:1380（interests/triggers 每平台加载）；ls Data/Keywords/ 仅 reddit；grep "ManifestLoader.Load|Validate" 全仓零调用；计划书 §2 行 3（5 触点含 ManifestLoader）vs 附录 B"加 APP 需 6 文件"；§4.2 行 3"保留为 MEMORY_MISS 回退"

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：我以驳倒为目标逐条实测，核心事实全部坐实，且通读计划书全文未见任何章节回填此缺口。(1) Keywords 是活触点：SessionRunner 在 3 处会话路径按平台拼路径加载 Data/Keywords/{platform}/interests.json 与 triggers.json（共 6 个调用点，行号与 finder 所引完全一致）；加载虽是 File.Exists 容错（缺文件仅 LogWarn+置空，"强制加载"措辞略有夸大），但 RuleEngine.CalculateRelevanceScore 直接消费这两个 JSON——缺文件时 keywordMatch/subredditMatch/topicSim 全为 0，relevance 分骨架化，计划书 §10 自己也承认"否则 RuleEngine relevance 缺输入"。且 §4.2 明确把静态兴趣"保留为 MEMORY_MISS 回退"，即记忆动态接通后该文件依然是必需回退输入；triggers.json 承载 sensitive_topics/avoid_topics 内容安全词表，根本不是"兴趣"，persona v2 interest_seeds（§4.1③/§4.6）无法承载它。(2) 收敛设计确实漏了它：§2 行 35 的 ①-⑤ 触点、§5-R2-4(c) 单文件 Loader（只提删 :3339 包名兜底）、app_onboarder 输出面（grep Tools/ 全目录零处生成 Data/Keywords 文件）均不含 keywords；§10 只把 instagram 种子当一次性数据补齐，未作为任意新 APP 的结构性触点回填设计。(3) ManifestLoader 确为死触点：全仓 *.cs 中"ManifestLoader."调用为零，仅出现在 OwnCode coreFiles 编译清单字符串里（编译但永不调用），与 §3.2"惰性文件、零运行成本"的自我裁决矛盾，却被 §2 计入"现需 5 处触点"。(4) 计数不一致属实但最弱：附录 B"加 APP 需 ……

**复核员独立实测证据**：我本人实测：SessionRunner.cs:496-505（LoadConfigs 容错加载 interests/triggers）、:781-803（会话主路径，缺文件 LogWarn 置空）、:1369-1389（InitSession 同款）、:3339-3342（reddit/instagram/babycenter/tiktok 包名硬编码兜底）；RuleEngine.cs:235-301（CalculateRelevanceScore 消费 interestsJson/triggersJson，:279-281 读 engagement_boosters/sensitive_topics/avoid_topics）；ls Data/Keywords/ 仅 reddit/{interests,triggers}.json；grep "ManifestLoader\." 全仓 *.cs 零调用点，仅 ZDProjects/*_OwnCode.cs 与 ModuleLoader.cs:48,182 的 coreFiles 字符串清单含文件名（编译不调用）；grep "Data/……

**修改建议**：最小修订三处：① §2 行 35 触点清单据实改写——移除已死的 ManifestLoader 触点（与 §3.2 惰性裁决对齐），加入 Data/Keywords/{platform}/interests.json 与 triggers.json 两个活触点；② §2 行 35 或 §5-R2-4(c) 的单文件 Loader/app_onboarder 输出面补一句处置决定：interests 种子改由 persona v2 interest_seeds（或 §4.6 替代方案）派生并同步改 RuleEngine/SessionRunner 读取路径，triggers（内容安全词表）作为平台配置段并入单文件 YAML 或由 app_onboarder 模板生成——二选一在 R1 定稿；③ 附录 B"6 文件"行加注计数口径（或统一为与 §2 相同口径），§10 的"instagram 种子需补"改指向上述结构性处置而非一次性数据补齐。

---

### I14. R1 验收要求"对照日志能观察记忆是否改变判断"，但决策点接线是 R2-5 的工作，R1 阶段无生产者

**级别**：🟠 Important　**来源维度**：内部一致性

**问题**：§4.2 定义对照日志为"每决策点记'无记忆基线分 vs 记忆加权分'"，决策点在 RuleEngine/SessionRunner（legacy，R2-2 翻锁前全部锁死）或 planner（三因子检索+去重决策的接入在 R2-5）。R1 的 8 个条目（v2 链路闭合、logistic、outbox、草稿合同、NAS、桥 v2 schema、Soul 隔离、端点注入）没有任何一条把 query_memories 接进决策评分点。R1 验收却要求"对照日志能观察'记忆是否改变判断'"——该验收条款在 R1 时点无判据、不可执行；同样内容在 R2 EMULATOR_VERIFIED 里再次出现（"对照日志有'记忆改变决策'实例"），那才是正确位置。

**证据**：Docs/RebuildPlan_重构计划书.md:184（R1 验收"对照日志能观察'记忆是否改变判断'"）；:115（§4.2 对照日志定义在"每决策点"）；:194（R2-5 "RuleEngine/SessionRunner 评分点前插 query_memories…planner 结合三因子检索"）；:175-182（R1 条目 1-8 无决策点接线）；:201（R2 验收重复同项）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：尝试驳倒失败，该发现成立。1) 计划书原文核对无误：:184 R1 验收确含"对照日志能观察'记忆是否改变判断'"；:115 定义对照日志记在"每决策点"；:175-182 的 R1 条目 1-8（v2 链路、logistic、outbox、草稿合同、NAS、桥 v2 schema、Soul 隔离、端点注入）没有任何一条把记忆检索接进决策评分点——R1-6 只是新增 query_memories 这个 exchange kind 的通道，不含消费方；:194 明确 RuleEngine/SessionRunner 评分点前插 query_memories 与 planner 三因子检索接入均在 R2-5。2) 仓库实测封死所有反驳路径：全仓 grep query_memories 零命中（纯未来新建）；Modules/SessionRunner.cs:173-176 HasVerifiedModernExecutionBridge() 恒 return false（legacy 决策点锁死到 R2-2 翻锁，且 MV 里程碑明示"不碰 DPS 锁"）；Modules/planner/src/Dps.Planner/ShadowActionPlanner.cs 全文 59 行，grep memory/interest/gbrain 零命中——即 R1 时点不存在任何"用记忆的决策"生产者，"记忆是否改变判断"无判据可观察。3) 计划书内部还有加重内证：:288（§10）自己写"logistic 参数 a、b 首版拍板，靠 R2 对照日志回调"，即计划书别处已把对照日志归为 R2 产物，与 :184 的 R1 验收自相矛盾；:201 R2 EMULATOR_VERIFIED 已含同项且带完整判据（"有实例或明确报告无差异触发 A3 复审"），是正确位置。曾考虑善意解读":184 只要求'能观察'即日志机制就绪"——不成立：R1 条目里连对照日志本身的建设都没有排（它定义在决策点上，而决策点 R1 不动），且按 §7 硬停止纪律（任一验收非 PASS 即停），一条无判据条款会……

**复核员独立实测证据**：复核员实测：Docs/RebuildPlan_重构计划书.md:184（R1 验收含对照日志条款）、:115（对照日志定义在每决策点）、:175-182（R1 八条目无决策点接线）、:194（R2-5 才接 query_memories/planner 三因子）、:201（R2 验收同项含完整判据）、:288（§10 自称"R2 对照日志"，内证矛盾）；仓库：全仓 grep "query_memories" 零命中；Modules/SessionRunner.cs:173-176（HasVerifiedModernExecutionBridge 恒 return false，legacy 决策点锁死）；Modules/planner/src/Dps.Planner/ShadowActionPlanner.cs（全文仅 59 行，grep memory/interest/gbrain 零命中，planner 今日不消费记忆）。

**修改建议**：最小修改：从 :184 R1 验收中删除"对照日志能观察'记忆是否改变判断'"（该项保留在 :201 R2 EMULATOR_VERIFIED 即可）；若 R1 需要留拟人化记忆的可验判据，替换为与 R1-2"设计先行"对齐的离线判据，如"给定固定合成事件集，三因子检索评分与 R1 定稿的评分公式/logistic 参数一致（离线单测 PASS）"。顺带可把 :115 的"可证伪"句标注归属 R2，与 :288 口径对齐。

---

### I15. R0-0 dry-run 范围只覆盖 §3.4 factory 八步，但 R0-2 落地批同批包含字节基线 legacy 删除，anchor 重签这条最高危路径未被演练

**级别**：🟠 Important　**来源维度**：内部一致性

**问题**：R0-0 定义为"在一次性抛弃 worktree 里执行 §3.4 全部八步，跑通两级门禁全绿"——只含 factory 手术。而 R0-2 把"死物清理（§3.1）"与 factory 批"同批"落地，§3.1 的 AppExplorer_v2.cs、WeeklyEvolve.cs、WeeklyEvolve_OwnCode.cs、ZDProjects/Tests 均实测在 legacy-csharp-bytes.v1.json 字节基线清单内，按 §7-2 其物理删除需要外部 trusted anchor 重签（EXPECTED_LEGACY_CSHARP_COUNT=79 绑 anchor，实测 verify_sessionrunner_baseline.py:39 属实）。也就是说真实 R0-2 批会触发 dry-run 从未演练的 anchor 重签+基线校验失败路径，R0-0 "防返工"的目的对同批一半的内容失效。

**证据**：Docs/RebuildPlan_重构计划书.md:162（R0-0 只指 §3.4 八步）；:164（R0-2 "+ 死物清理（§3.1）同批"）；:242（§7-2 legacy 物理删除需第五件 anchor 重签）；实测 Modules/legacy-runtime-adapter/operations/strangler/legacy-csharp-bytes.v1.json:98/:398/:788（AppExplorer_v2.cs、WeeklyEvolve.cs、WeeklyEvolve_OwnCode.cs 在清单内）、verify_sessionrunner_baseline.py:39（EXPECTED_LEGACY_CSHARP_COUNT = 79）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的事实链全部实测成立，且计划书全文未覆盖该缺口。(1) R0-0 范围确为"§3.4 全部八步"（:162），§10 :285 进一步确认 dry-run 只验证"factory 删除后 generate_governance --write 一次转绿"；(2) R0-2 确将"死物清理（§3.1）"与 factory 批同批（:164）；(3) §3.1 所列 AppExplorer_v2.cs、WeeklyEvolve.cs、WeeklyEvolve_OwnCode.cs、ZDProjects/Tests 15 件实测均在字节基线清单内，其物理删除必触发 verifier 双向清单严格相等失败，必须走 §7-2 的外部 trusted anchor 重签（计划书 :242 自己也把 R0-2 计入"全案 3–4 次重签"账）。我尝试的三条反驳均失败：§7-2 只要求"写 runbook"（纸面流程，非演练，保障强度低于计划书自定的"dry-run 跑通全绿再落主干"标准）；anchor 校验实测是纯机械哈希绑定+环境挂载，抛弃 worktree 里完全可演练，不存在"无法演练"的豁免理由；该 verifier 处于两级门禁覆盖内，真实 R0-2 批的门禁行为与 dry-run 演练过的行为必然不同。计划书自己承认"新 baseline_commit 锚定时序"是需 runbook 厘清的难点，把这条时序敏感、校验极端严格（exact keys、三处 count 强校验、canonical JSON 哈希自绑定）、从未跑过的路径放进全案第一个原子落地批，一旦失败在主干上迭代，每轮治理提交都需异构复核——正是 R0-0 声称要防止的返工。属于计划书内部不一致且直接影响 R0 执行。唯一瑕疵：发现说"同批一半的内容"按行数夸张（§3.1 约 4k 行 vs factory 约 5 万行），但按风险路径计不失实，不影响裁决。维持 important：有原子回滚（§8）和硬停止兜底，不是安全性问题，但命中全案首个里程碑的落地成本，修复代价一行。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:162（R0-0 = "执行 §3.4 全部八步"）、:164（R0-2 "+ 死物清理（§3.1）同批"）、:242（§7-2 anchor 重签 + "全案至少 3–4 次重签（R0-2 删除批…）" + 仅要求"R0 前先写 runbook"）、:285（§10 dry-run 只验 factory 转绿）；Modules/legacy-runtime-adapter/operations/strangler/legacy-csharp-bytes.v1.json:98（Modules/Core/AppExplorer_v2.cs）、:398（Modules/WeeklyEvolve.cs）、:788（ZDProjects/WeeklyEvolve_OwnCode.cs）、:628-768（ZDProjects/Tests/ 15 件含 BabyCenter_E2E_Test.cs）；Modules/legacy-runtime-adapter/operations/strangler/verify_sessionrunner_……

**修改建议**：最小修改（二选一，倾向前者以保持 R0-2 原子性）：① 改 :162 R0-0 定义为"在一次性抛弃 worktree 里执行 §3.4 全部八步 + §3.1 全部字节基线内文件删除，并按 §7-2 runbook 生成候选 anchor（异构会话扮 independent authority）完成一次重签演练，跑通两级门禁全绿"，同步在 §10 :285 加"anchor 重签流程经 R0-0 演练"；② 或将 :164 R0-2 拆为两批：factory 批（不触字节基线，R0-0 已演练）先落，§3.1 legacy 死物批单独走 anchor 重签后落，并把 §7-2 的"3–4 次重签"账相应改为对应批次。

---

### I16. 里程碑引用体系存在悬空与错位：不存在的"R5"、无对应物的"R3 五文件批"、多处 "R2-3" 与 §5 条目对不上，且 R2/R3 同时是风险级名与里程碑名

**级别**：🟠 Important　**来源维度**：内部一致性、硬要求2·精简生死簿、Simplicity审查

**问题**：§7-2 列举重签批次"（R0-2 删除批、R2 flip、R3 五文件批、R5 Reddit 退役）"：全文只有 MV/R0/R1/R2/R3 五个里程碑，R5 不存在，Reddit_*.cs 退役（§2-2）也未列入任何里程碑；R3 的 5 个条目（CI 两 job、会话脚本、异构复核、实弹演练）没有任何 legacy 文件改动，"R3 五文件批"无对应物。"R2-3"被多处当交叉引用使用但语义错位：§3.2/§7-11 说 MemoryManager 退役在"R2-3 记忆接线后"，而记忆接线是 R2 条目 5；§4.1 把 SoulBehaviorPacket（⑤）和决策权重 overlay（⑥）归"R2-3"，而 §5 中 SoulBehaviorPacket 在 R2 条目 4(b)、per-Soul 决策权重 overlay 在 R2 条目清单中根本未出现。另外 risk-policy.yaml 实测存在 R2/R3 风险级（:12/:16），与里程碑 R2/R3 同名，附录 B 第 3 行"R2/R3 强制人工批准"未加限定词，易误读。

**证据**：Docs/RebuildPlan_重构计划书.md:242（"R3 五文件批、R5 Reddit 退役"）；:34（Reddit 退役无里程碑归属）；:204-214（R3 条目无 legacy 文件改动）；:65/:251（MemoryManager "R2-3"）；:101-102（§4.1 ⑤⑥ 归 R2-3）；:193（SoulBehaviorPacket 实际在 R2-4(b)）；:339（附录 B 第 3 行）；实测 governance/policies/risk-policy.yaml:12/:16（风险级 "R2"/"R3" 存在）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：四项子主张全部经我亲手实测坐实：(1) :242 的"R5 Reddit 退役"在全文里程碑体系（MV/R0-R3）和风险级体系（risk-policy.yaml 实测只到 R4）中均无对应；且 Reddit_*.cs 退役全文仅 :34 与 :242 两处提及，§3.1 生死簿与 §5 里程碑均未排期，而这批文件实存于 ZDProjects/ 且属字节基线，物理删除按 :242 自身规则需外部 anchor 重签（P3 全局串行）——一项重操作被排进不存在的里程碑。(2) :204-214 R3 五条目无任何 legacy C# 改动，"五文件批"全文仅此一处、无定义，无法与"R5 Reddit 退役"合并自洽（二者在 :242 并列为独立批次；即便 Reddit 相关恰为 5 文件也救不回）。(3) "R2-3"语义错位实锤：:131 的用法与 R2 条目 3（:192 门控收敛）精确匹配，证明文档用"R2-条目号"记法；以此标准 :65/:251 MemoryManager"R2-3 记忆接线"指错（记忆接线在 R2 条目 5，:194），且 :65 自称时序红线；:101 ⑤ 实际在 R2-4(b)（:193）；:102 ⑥ per-Soul 决策权重 overlay 在 §5 全部条目中零出现——"2 层新建"之一、直接服务硬要求 1 的交付物在施工路线图无落点，属实质遗漏。(4) risk-policy.yaml:12/:16 确有 R2/R3 tier 与里程碑同名，:167 已加限定而 :339 未加——此子项单独看偏吹毛求疵，是发现中最弱一环。我尝试的两条驳倒路径（"R2-3=R2~R3 区间"宽容读法、"五文件批=Reddit 5 文件"合并读法）均被 :131 的精确条目匹配和 :242 的并列句式否掉。该文档以冷启动可交接为设计目标（R0-6）、重签批次数是 R0 前 anchor runbook 的输入，悬空引用会直接传导进执行，故非纯措辞问题。不触红线、不改方向，维持 important 不升级。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:242（"全案至少 3–4 次重签（R0-2 删除批、R2 flip、R3 五文件批、R5 Reddit 退役）"，grep"R5|五文件"全文仅此一处）；:141-144（里程碑仅 MV/R0/R1/R2/R3）；:34（Reddit_*.cs 唯一处置描述，"删…退役为参照物"自相矛盾且无里程碑归属）；:52-59（§3.1 DELETE 表无 Reddit）；:204-214（R3 五条目无 legacy 文件改动）；:65/:251（MemoryManager"R2-3 记忆接线"，实际记忆接线在 :194 R2 条目 5）；:101-102（⑤⑥ 归"R2-3"）；:193（SoulBehaviorPacket 实在 R2 条目 4(b)）；:131（"R2-3 门控合并批次"与 :192 R2 条目 3 精确匹配，证明条目记法）；grep"overlay|决策权重"确认层⑥在 §5 零出现；:339 vs :167（后者有"risk-policy"限定，前者无）；实测 governance/policies/risk-po……

**修改建议**：最小修改五处：① :242 重签批次表改为真实排期，如"（R0-2 删除批、R2 三锁 flip+VisionCorrector 批、R2 后 MemoryManager+Reddit 退役批）"，并把 Reddit_*.cs 退役显式写入 §3.1 生死簿或某 R2 后清理条目（删除"R5"与"R3 五文件批"）；② :65/:251 "R2-3"改"R2-5"；③ :101 ⑤ 归属改"R2-4(b)"；④ :102 ⑥ 在 §5-R2 补一条落点（建议并入 R2 条目 3 或 5 并显式写"per-Soul 决策权重 overlay"），归属栏改指该条目；⑤ :339 附录 B 第 3 行"R2/R3"前加"risk-policy"限定词。可选：§5 内条目显式编号（R2.1…R2.7）以固化交叉引用规范。

---

### I17. 99% 滚动窗口门控的冷启动语义未定义，且 R2 EMULATOR 验收规定的样本量填不满 ≥300 有效动作窗口，"门控生效"无判据

**级别**：🟠 Important　**来源维度**：内部一致性

**问题**：§4.5 定义门控为"最近 ≥300 个有效动作的滚动成功率 ≥0.99"，但没有定义窗口未满 300 时门控输出什么（fail-closed 则冷启动全部阻断、系统永远起不来；放行则门控在最初阶段形同虚设）——这恰是每次冷启动/每台新设备必然经过的状态。R2 EMULATOR_VERIFIED 验收要求"AVD 连续 3 会话，浏览 ≥10 帖…门控 99% 滚动窗口生效"：3 会话 × ≥10 帖的动作量大概率远低于 300，验收现场窗口根本未满，"生效"二字无法测——这条验收怎么测、判据是什么，计划书没有答案。

**证据**：Docs/RebuildPlan_重构计划书.md:130（§4.5 "最近 ≥300 个有效动作的滚动成功率 ≥ 0.99"，无冷启动定义）；:201（R2 验收"AVD 连续 3 会话，浏览 ≥10 帖…门控 99% 滚动窗口生效"）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：核心成立：①我通读计划书全文并 grep "冷启动/滚动窗口"，§4.5 确实只写"最近 ≥300 个有效动作的滚动成功率 ≥0.99"，未定义窗口未满时门控输出什么，全文其他章节（含 §10 待实测项）均未覆盖——且 §10:293 对"双 Soul 行为可辨"明确警告了同类小样本验收问题（"否则验收形同虚设"），对 300 窗口却只字未提，佐证是疏漏而非已覆盖；②R2 EMULATOR_VERIFIED 验收（:201）"3 会话 × 浏览 ≥10 帖 + 平台零写"的有效动作量算术上填不满 300 窗口，"门控 99% 滚动窗口生效"既没说验"机制接通"还是"阈值达标"，按字面无法执行，这是全案最高风险里程碑的验收判据缺失。但发现的灾难化分支被我实测驳回：SessionRunner 两处门控（:1026-1068/:2056-2098）都是会话结束后的事后标签（只设 run_result/session_result 并返回 SUCCESS/ERROR），不阻止后续会话启动；legacy 本就有 min_successful_actions=6 样本下限、不足即标 FAIL 而系统照常跑，因此"fail-closed 则冷启动全部阻断、系统永远起不来"不成立——最坏后果是冷启动期会话被标 ERROR、窗口满后自然可判。核心缺陷（冷启动语义未定义 + R2 验收无判据）真实存在且未被处理，维持 important；不因论证瑕疵驳倒，也不因灾难化措辞升级。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:130（§4.5 滚动窗口定义原文，无冷启动分支）；:201（R2 验收原文"连续 3 会话，浏览 ≥10 帖……平台零写……门控 99% 滚动窗口生效"）；:168（全文唯一"冷启动"指文档交接，与门控无关）；:293（§10 对双 Soul 可辨小样本已警告，对门控窗口未提）；Modules/SessionRunner.cs:1026-1043、:1054-1068（门控为会话末事后判定，min_successful_actions 默认 6，FAIL 仅设 ERROR 标签返回）；:2056-2098（第二处同式实现，同为事后标签）

**修改建议**：§4.5 补一行冷启动语义（建议沿用 legacy 既有形态：窗口 <300 时门控仅记录不判 99% 阈值，会话仍按 legacy min_successful_actions fail-closed 标签；或单会话内连续 N 次 POSTCONDITION_FAILED 触发保守停摆）；R2 EMULATOR 验收把"门控 99% 滚动窗口生效"改为可测判据："新门控函数在合成收据流上（<300 与 ≥300 两态）输出符合 §4.5 定义 + 真实 3 会话的 gateway 收据正确累积进窗口且日志可见窗口计数"——不要求 3 会话内窗口满。改动约 3-4 行，不触碰用户 99% 滚动窗口拍板本身。

---

### I18. anchor 重签机制被错误建模为'RSA-PSS 权威签发'，'异构复核会话扮 independent authority'机械上不可行

**级别**：🟠 Important　**来源维度**：红线约束合规

**问题**：实测 verify_sessionrunner_baseline.py：trusted anchor 的 20 个键（TRUSTED_ANCHOR_KEYS，:41-60）不含任何签名字段，anchor_id 只是记录自身的 SHA-256；其独立性完全由 OS 层强制——anchor 文件必须在仓库外、无写权限位（:312）、文件与父目录的 st_uid 都必须不同于验证器进程 euid（:319-323）、父目录不可被验证器身份写（:325-327）。这意味着：任何以用户 UID 运行的 AI 会话（无论 Claude 施工还是 DeepSeek/GLM 异构复核）都不可能自己签发有效 anchor——同 UID 写的文件会被验证器直接拒绝；每次重签实际需要用户以另一 OS 身份（root/第二账户，需 sudo）落盘。计划书 §7-2 把 runbook 写成'签发主体=异构复核会话扮 independent authority 的具体操作流'，§9 把人工前置写成'外部 RSA-PSS 权威……的密钥托管方式'——对 legacy anchor 而言根本没有密钥可托管，真正缺的人工项（用户以特权身份放置/chown 只读 anchor 文件）没有进 §9 清单。这直接影响'治理变更不得自我批准'红线的实质：异构会话只能出具复核报告与 anchor 内容草稿，最终裁决载体是持有特权文件系统操作权的用户；把独立性归功于异构模型是实质自批风险的错误定性。R0-2 首个删除批就需要重签（79 文件清单变更），此错误在第 0 个里程碑就会撞上。

**证据**：verify_sessionrunner_baseline.py:37 TRUSTED_ISSUER="DPS_INDEPENDENT_RELEASE_AUTHORITY"（全仓唯一定义处，grep 实测）；:305-327 os.fstat/st_uid/os.access 身份隔离检查（亲自打开确认无签名校验）；operations/README.md:13 'The verifier cannot approve itself … controlled by an identity different from the verifier'；对照计划书 §7-2 与 §9 表'外部 RSA-PSS 权威（legacy anchor + 运行时 Release BOM 同一 authority）的密钥托管方式'。RSA-PSS 实测只存在于 windows-edge-supervisor 的 Release BOM/worker 信任链与 factory-artifact-builder，运行时命令链是 ecdsa-p256-sha256（ExecutionAuthorizationV1.cs:24）。

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的三层主张全部被我独立实测坐实：(1) legacy trusted anchor 机制确实没有任何密码学签名——TRUSTED_ANCHOR_KEYS 20 键无签名字段，anchor_id 仅是记录自身 SHA-256 自绑定，独立性完全靠 OS 层 UID 隔离强制（文件/父目录 st_uid 均须异于验证器 euid、无写权限位、仓库树外、无符号链）；(2) 因此任何以用户 UID 运行的 AI 会话（含 Claude 施工会话）写出的 anchor 会被验证器直接拒绝，而计划书 R3 设计中的 DeepSeek/GLM 异构复核本身是 API 报告生成器、无文件系统能力，"签发主体=异构复核会话扮 independent authority"（计划书:242）机械上不可行；(3) 计划书 §9（:279）与 §5 R2 pre-work（:188）把 legacy anchor 归入"外部 RSA-PSS 权威的密钥托管"——RSA-PSS 实测只存在于 factory-artifact-builder 与 windows-edge-supervisor 合同、运行时命令链是 ecdsa-p256-sha256，legacy anchor 根本无密钥可托管；真实的循环性人工依赖（用户以 root/第二 OS 账户特权落盘只读 anchor，全案 3–4 次，首次即 R0-2 删除批）在 §9 人工前置清单中缺位，全文亦无任何章节提到 UID 隔离要求。驳倒尝试失败：§9 虽有一行以"legacy 删除"为阻塞，但其内容按写执行产不出有效 anchor，等于未覆盖。严重级别下调为 important 而非 critical：验证器 fail-closed，最坏结果是 R0-2 响亮卡死（WAITING_EXTERNAL/identity 错误）而非安全突破或静默自批；修复是纯文档级最小改动，无架构变更；但它是真实事实错误+遗漏的用户阻塞依赖，撞在首个里程碑，必须在 R0 前修正。

**复核员独立实测证据**：Modules/legacy-runtime-adapter/operations/strangler/verify_sessionrunner_baseline.py:37（TRUSTED_ISSUER，grep 全仓代码唯一定义处）、:42-63（TRUSTED_ANCHOR_KEYS 20 键无签名字段）、:227-230 与 :640-646（anchor_id=自身 SHA-256 自绑定）、:295-296（须在仓库树外）、:312-313（拒写权限位）、:319-324（文件与父目录 st_uid 须异于验证器 euid）、:325-329（父目录不可被验证器写）；全文件 grep rsa/pss/signature 无密码学校验。Modules/legacy-runtime-adapter/operations/README.md:13（"The verifier cannot approve itself…identity different from the verifier"、WAITING_EXTERNAL）。RSA-PSS 实测分布：Modules/facto……

**修改建议**：最小修改三处：(1) §7-2 anchor 重签 runbook 括注改写为——机制如实描述为"UID 隔离的仓库外只读 JSON（无密码学签名，anchor_id 自摘要）"；签发落盘主体如实写为"用户以第二 OS 身份（root/独立账户）放置只读 anchor 文件（文件与父目录属主须异于验证器运行身份）并配置 DPS_LEGACY_BASELINE_ANCHOR 指向"；异构复核会话角色定性为"重签前对 anchor 内容草稿与 79 文件清单变更的独立复核比对"。(2) §9 表该行拆分：legacy anchor 一行改为"用户以特权身份落盘/更新只读 anchor（循环性，每次重签一次，首次阻塞 R0-2）"；RSA-PSS 密钥托管仅保留给运行时 Release BOM 权威，阻塞项保持 R2 pre-work。(3) §5 R2 pre-work 括注"与 legacy trusted anchor 同一 authority"改为"与 legacy anchor 同一组织身份（DPS_INDEPENDENT_RELEASE_AUTHORITY），但机制不同：anchor 为 UID 隔离外挂文件、BOM 为密码学签名"。

---

### I19. anchor 重签排程自相矛盾：预算引用不存在的里程碑，且多个 legacy 字节改动批次未归批

**级别**：🟠 Important　**来源维度**：红线约束合规

**问题**：§7-2 预算'全案至少 3–4 次重签（R0-2 删除批、R2 flip、R3 五文件批、R5 Reddit 退役）'——但 §5 里程碑路线只有 MV/R0/R1/R2/R3，不存在 R5；R3 的五项内容（CI job、会话脚本、异构复核、演练）也没有任何'五文件批'。同时，计划实际安排的 legacy 字节改动远不止两批：§4.2 RuleEngine.cs:466-468 替换（归属'R1 + R2-3'，若落 R1 则 R1 需一次未预算的重签）、§4.5 门控双实现收敛+0.95 常量清除（SessionRunner.cs）、R2-4a SwipeCurved 统一（ZennoDroidAdapter.cs）、R2-4b humanization_profile setter（ActionExecutor.cs:358）、R2-4c :3339 兜底删除（SessionRunner.cs）、R2-5 评分点前插 query_memories（RuleEngine/SessionRunner）、§7-11 MemoryManager.cs 物理退役（删除类，按计划书自己的规则必须重签）——实测这些文件全部在 79 文件字节基线内（legacy-csharp-bytes.v1.json entries 逐一确认），且验证器硬编码 EXPECTED_APPROVED_REPAIR_COUNT=4/EXPECTED_LEGACY_CSHARP_COUNT=79，任何一批都要改验证器常量+重生成基线+重签 anchor。计划书只把 VerifyError 删除显式列入'R2 三锁字节审批批次'，其余改动无归批说明；每次重签是 P3 全局串行+用户特权操作，漏排会造成串行瓶颈，或诱使施工时把不相干改动塞进同一巨型批次，与 §7-4'提交拆小、治理与业务不混'冲突。

**证据**：计划书 §7-2 原文'R3 五文件批、R5 Reddit 退役'（grep 全文 R5 仅此一处，§5 无 R5 定义）；legacy-csharp-bytes.v1.json entry_count=79，实测含 Modules/Core/ActionExecutor.cs、Core/ScriptHelpers.cs、Modules/RuleEngine.cs、Modules/SessionRunner.cs、Modules/MemoryManager.cs、Modules/Core/ZennoDroidAdapter.cs、Modules/Core/VisionCorrector.cs；verify_sessionrunner_baseline.py:40-41 EXPECTED_LEGACY_CSHARP_COUNT=79 / EXPECTED_APPROVED_REPAIR_COUNT=4 硬编码。

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：三条核心断言全部实测坐实：(1) §7-2 第 242 行确有"R3 五文件批、R5 Reddit 退役"，全文 grep 确认 R5/五文件仅此一处，§5 无 R5、R3 无字节批次，引用悬空。(2) 验证器实测证实成本模型比发现所述更严：任何 legacy 字节修改都需改验证器硬编码绑定+重生成基线+重签 anchor（anchor 绑精确 working_sha256、变更集严格相等、验证器与 baseline JSON 自身受 anchor 保护），计划书"仅删除才重签"的区分也不成立。(3) 所列 7 文件全在 79 文件基线内，R2-3/4/5 与 MemoryManager 退役等多个字节批次未进重签预算。唯一小瑕疵：签发主体是异构复核会话扮 independent authority 而非"用户特权"，不影响结论。属排程关键路径的真实执行缺陷，非措辞问题；可用对照表修复，不触红线，维持 important。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:242（"全案至少 3–4 次重签（R0-2 删除批、R2 flip、R3 五文件批、R5 Reddit 退役）"，grep 全文 R5/五文件仅此一处）；:141-145（里程碑仅 MV/R0/R1/R2/R3）；:204-214（R3 五项内容无 legacy 字节批）；:34（Reddit_*.cs 退役无里程碑）；:112（RuleEngine 替换归属"R1 + R2-3"）；:131（0.95 随"R2-3 门控合并批次"清除，该批次不在重签预算）；:192（仅 VerifyError 显式列入 R2 三锁批次）；:65/:251（MemoryManager R2-3 物理退役）。verify_sessionrunner_baseline.py:39-41（COUNT=79/REPAIR=4 硬编码）、:97-114（EXPECTED_APPROVAL_BINDINGS 仅 4 固定路径）、:783-786/:796（anchor 恰绑 4 条且路径须命中硬编码表）、:996-1019（变更文件须匹配 anchor 精确 ……

**修改建议**：在 §7-2 增加"legacy 字节改动 → 审批批次 → 重签次数"对照表：逐项列出 R1/R2 触碰 79 文件基线的工作项（RuleEngine 兴趣接线、门控收敛+0.95 清除、VerifyError 删除、SwipeCurved 统一、humanization_profile setter、:3339 兜底删除、query_memories 插入、MemoryManager 退役、Reddit_*.cs 退役），明确每项归入哪个原子批次并重算重签总数；删除"R5"悬空引用（为 Reddit 退役补真实里程碑归属，建议并入 R0-2 死物批或 R2 批）；"R3 五文件批"改为其实际所指（疑为 R2-3）或删除；同时修正 §7-2 的"改动仅具名审批、删除才需 anchor 重签"表述——按验证器实测，任何字节修改批同样需要改验证器绑定+重签 anchor，重签 runbook 应按此口径编写。

---

### I20. A4'收编/收缩不算重写'缺少机器可验的等价判据，红线在方案里不可检验

**级别**：🟠 Important　**来源维度**：红线约束合规

**问题**：A4 红线是'沿用现有决策代码不重写'。但 §2-1 的'legacy 加权随机收编为 planner 确定性策略插件'意味着在 planner 模块新写一份 AdjustWeightsForFatigue+WeightedChoice 的等价实现（原代码在字节冻结的 SessionRunner.cs:869-928 里动不了，'收编'只能是重新实现）；§4.2 的三因子检索评分自述'与 RuleEngine 既有 hot/activity/relevance 同构'——'同构'同样是新代码；§5-R2-5 把去重从 MemoryManager.IsDuplicate 换成'账本 event_id 幂等'也是替换实现。整个决策管线实质上在大脑侧重建了一份'受 legacy 启发'的新实现，而计划书对 A4 的全部论证只有一句'A4 逻辑保留、载体归一'，没有给出任何可验证的等价标准：不要求权重表原样迁移、不要求种子化 RNG 下与 legacy WeightedChoice 输出一致、不要求黄金决策轨迹对照（仓里明明有 golden-traces 机制可复用）。边界论证不够，A4 沦为口头背书。

**证据**：SessionRunner.cs:869-928 实测确认加权随机循环（AdjustWeightsForFatigue/WeightedChoice/MemoryManager 去重/RuleEngine 门控）位于字节基线保护文件内；计划书 §2-1'legacy 加权随机收编为其确定性策略插件（A4 逻辑保留、载体归一）'、§4.2'与 RuleEngine 既有 hot/activity/relevance 同构（A4）'、§5-R2-5；legacy-runtime-adapter/operations/strangler/golden-traces/ 目录存在（TRUSTED_PROTECTED_PATHS 列表实测）。

**复核员独立实测证据**：Modules/SessionRunner.cs:869-928（主循环：AdjustWeightsForFatigue :872、WeightedChoice :875、MemoryManager.IsDuplicate :897、EvaluatePostForAction :906）；:2113/:2173（两方法均 private static）；legacy-csharp-bytes.v1.json 含 Modules/SessionRunner.cs；verify_sessionrunner_baseline.py:76-87（TRUSTED_PROTECTED_PATHS 含 golden-traces 两文件）；golden-trace.schema.json:70-77（method 枚举含 DecideNextAction）、:24-28（fixture_kind 仅 SYNTHETIC_FORMAT_EXAMPLE）；计划书第 33 行（§2-1"A4 逻辑保留、载体归一"）、第 110 行（§4.2"同构（A4）"）、第 65/194 行（去重迁账本幂等）、第 1……

**修改建议**：在 §2-1 与 §5-R2 增补 A4 等价验收判据：planner 策略插件必须原样迁移 legacy 权重表与 AdjustWeightsForFatigue 疲劳调权公式；以固定种子跑决策轨迹对照（同输入序列→同动作序列）作为候选门禁用例，可扩展既有 golden-trace schema（新增 fixture_kind 或 v2 版本以允许非合成对照轨迹，method 枚举已含 DecideNextAction）；§4.2 三因子评分列出与 RuleEngine hot/activity/relevance 的逐项映射表并标注有意偏离；做不到等价的部分（如随机→确定性）如实定性为新决策代码并单独论证，不再引用 A4 背书；将"加权随机收编"列为 R2 显式工作项并挂上述验收。

---

### I21. 'soul_id 不进边缘变量/日志'与既有 exchange 合同及 §4.1 层④注入方案三方矛盾

**级别**：🟠 Important　**来源维度**：红线约束合规

**问题**：§5-R1-6 要求'soul_id 不进边缘变量/日志——否则污染 F6/F7 证据束'。但实测 edge.bridge.exchange.v1 schema 把 soul_id 列为 required 字段（pattern ^soul_[a-f0-9]{64}$），producer_module 恒为 zenno-bridge，而 zenno-bridge 的 runtime 声明是 'C# 5 / legacy-host'——即 soul_id 在既有合同设计里本来就由边缘进程持有并出现在每条回环消息中；计划书的 v2 是'与 v1 并存'（additive），v1 继续存活则 soul_id 照样过边缘。更矛盾的是 §4.1 层④明确要求 R2'桥装配时把 SoulResolved 三元组注入 legacy 会话'——legacy 会话就跑在边缘 ZD 进程里，注入三元组几乎必然落 ZD 变量（这正是 §4.1 表里'④ 执行 soul 上下文'的落点）。三处互相打架，计划书没有给出边缘身份最小化方案（例如边缘只持 device_binding_id/platform_account_id，soul_id 由 modern 侧在 worker 终结点回填），也没有承认要动 v1 的 required 集（那是它自己定性的 P2 破坏性变更）。

**证据**：edge.bridge.exchange.v1.schema.json required 数组实测含 "soul_id"（pattern ^soul_[a-f0-9]{64}$）；zenno-bridge/module.yaml:7-11 'Frozen C# 5 compatible loopback bridge'、runtime.processBoundary="legacy-host"、entrypoints=LoopbackBridgeClient.cs；计划书 §5-R1-6'soul_id 不进边缘变量/日志'与 §4.1 表'④ 桥装配时把 SoulResolved 三元组注入 legacy 会话'。

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：对抗复核未能驳倒该发现，三条证据链全部实测坐实。(1) v1 合同侧：edge.bridge.exchange.v1.schema.json 把 soul_id 列为 required（每条 POLL/NATIVE_RESULT 消息必带），producer_module 恒为 zenno-bridge，而 zenno-bridge 是跑在 ZD legacy-host 内的 C# 5 边缘进程，其源码 BridgeContracts.cs 将 SoulId 声明为 IsRequired DataMember、BridgeProtocolValidator.cs 在边缘进程内校验并回显——soul_id 在既有设计中天然驻留边缘；计划书 §5-R1-6 自己拍板"v2 与 v1 并存、不动 v1 枚举（P2 禁区）"，即 v1 含 soul_id 的 envelope 继续存活。(2) 计划书内部矛盾：§4.1 层④要求 R2"桥装配时把 SoulResolved 三元组注入 legacy 会话"，实测 SoulResolved.cs 三元组首元即 soul_id，且 §4.3 明写桥向 legacy 下发的机制就是"写入 ZD 变量"——按计划书自身设计 soul_id 必进边缘变量，与同文件 §5-R1-6"soul_id 不进边缘变量/日志——否则污染 F6/F7 证据束"直接对撞。(3) 全文检索确认计划书无任何章节给出边缘身份最小化裁决或承认需动 v1 required 集；§11 裁决表第 7 行反而坐实 v1 保留。我尝试的窄义解读（"边缘变量"仅指 ZD 变量存储非协议内存）最多豁免 v1 传输，救不了层④注入这一处。severity 维持 important：不动摇总体路线，但不裁决则 R1-6 承诺不可达、F6/F7 证据束口径自相矛盾，实现者在 R1/R2 必然卡壳。

**复核员独立实测证据**：Modules/zenno-bridge/contracts/provided/edge.bridge.exchange.v1.schema.json:11（required 含 "soul_id"）、:35（pattern ^soul_[a-f0-9]{64}$）、:34（producer_module const zenno-bridge）；Modules/zenno-bridge/module.yaml:7（Frozen C# 5 loopback bridge）、:41（processBoundary "legacy-host"）、:42（entrypoint LoopbackBridgeClient.cs）；Modules/zenno-bridge/src/BridgeContracts.cs:20-21/:92-93（SoulId 为 IsRequired=true DataMember，边缘进程持有）；Modules/zenno-bridge/src/BridgeProtocolValidator.cs:48/:133/:149（边缘内校验并回显 SoulId）；Modu……

**修改建议**：在 §4.1 与 §5-R1-6 之间做一次显式的"边缘可见身份集合"裁决，三选一并写明代价：(a) 边缘只持 device_binding_id + platform_account_id，soul_id 由 windows-edge-worker/modern 侧在终结点回填——此路须承认 v1 exchange 的 required soul_id 要动（P2 破坏性变更）或 v1 退役计划；(b) 接受 soul_id 过边缘（维持 v1 与层④注入），把 §5-R1-6 的承诺改写为可达口径"soul_id 不落边缘持久化日志/不进 F6-F7 证据束导出物"，并给出机器检查点（如 windows_gate_launcher 证据打包器的字段剔除断言）；(c) 层④改注 soul_seed/behavior packet 等派生量而非原始三元组（与 §4.3 种子化一致），v1 envelope 中的 soul_id 定性为协议传输不落变量/日志。任选其一后同步修订 §11 第 7 行裁决记录。

---

### I22. Kill switch '仅用户可解除'没有任何机器强制，安全网关键约束是口头的

**级别**：🟠 Important　**来源维度**：红线约束合规

**问题**：计划书的安全网设计以'完全无人工干预、不靠用户实时盯'为前提，Kill switch 是异构复核 FAIL 后的唯一自动熔断（R3-4'复核 FAIL → 自动置位 Kill switch，仅用户可解除'）。但其载体是'受跟踪文件 governance/KILL_SWITCH（谁都能置位，仅用户可删除并锚点记录）'——一个 git 跟踪文件对任何能提交的 AI 会话都是可删除的，'仅用户可删除'没有对应的验证机制（对比之下，同一份计划对 anchor 用了 UID 隔离、对 legacy 用了字节基线，唯独这个最后防线是纯约定）。一个跑偏的升级会话完全可以删文件、让会话脚本与 CI 恢复绿灯，熔断形同虚设；这与红线'治理变更不得自我批准'的精神直接冲突——解除 Kill switch 正是最典型的治理变更。

**证据**：计划书 §7 Kill switch 原文'(a) 升级流水线冻结 = 受跟踪文件 governance/KILL_SWITCH（存在则会话脚本拒签收据、CI 门禁 FAIL；谁都能置位，仅用户可删除并锚点记录）'；全文无任何对'删除者身份'的校验机制描述；对照 verify_sessionrunner_baseline.py:305-327 的 UID 隔离先例（同仓已有更强机制可复用）。

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现成立。我实测：(1) 计划书 Docs/RebuildPlan_重构计划书.md:255 原文确为"谁都能置位，仅用户可删除并锚点记录"，全文 grep 解除/删除/签名/GPG 后，涉及 Kill switch 解除的仅 211/214/255 三行，全部是纯断言，无任何删除者身份的机器校验；(2) 发现引用的 UID 隔离先例真实存在且更强——verify_sessionrunner_baseline.py:295-329 强制 anchor 在仓库外、只读、属主 UID 与 verifier 不同、父目录不可写，证明同仓已有"仓内文件防不住会话"的认知与现成机制，唯独最后防线例外；(3) 该文件的删除对 CI 是自愈的（存在才 FAIL，删掉即绿），没有其他门禁能兜住，与触碰 governance/ 会扩 scope 的常规改动（计划书 222 行）性质不同；(4) 计划书自身威胁模型明确包含 AI 会话跑偏（埋错演练、硬停止、"模型输出是数据不是指令"247 行），而"CI 红了→删掉致红文件"正是 AI 会话最典型的走偏方式，威胁现实。驳倒尝试均失败。维持 important 不升 critical 的理由：置位时有强制通知义务（255 行，含推 dps2 + ATTENTION_REQUIRED.md），用户对"曾经 FAIL 过"有带外信号，静默解冻并非完全不可检测；机制尚在计划阶段、修复成本低、可复用现成 anchor 机器。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:255（"谁都能置位，仅用户可删除并锚点记录"，无校验机制）；:211、:214（"仅用户可解除/由用户解除"，同样纯断言）；:242（legacy 用仓库外 DPS_INDEPENDENT_RELEASE_AUTHORITY 只读 anchor 的对照先例）；:222（触 governance/ 仅扩 receipt scope，非身份校验）。Modules/legacy-runtime-adapter/operations/strangler/verify_sessionrunner_baseline.py:295-297（anchor 必须在仓库外）、:312-313（必须只读）、:315-329（属主 UID 须异于 verifier 且父目录不可写）——发现引的 305-327 行号略偏但实质准确。governance/ 目录实测仅含 modules/policies/schemas/verification，KILL_SWITCH 为待建新物。

**修改建议**：最小修改（写入 §7 Kill switch (a) 条款并列入 R0-5 治理修订）：解除动作加机器验证——置位时 append-only 锚点记录置位事件哈希；解除需用户在仓库外 UID 隔离只读路径（复用 verify_sessionrunner_baseline.py:295-329 同款 trusted-anchor 装载器与 CI env 挂载方式，随 anchor 重签 runbook 一并写）放置一次性解除令牌，内容绑定该置位事件哈希；CI 门禁改为校验"存在未匹配解除令牌的置位锚点即 FAIL"，而非仅看 KILL_SWITCH 文件是否存在（否则删文件即自愈、校验被绕过）。同时给"解除"事件补进 255 行通知义务清单。

---

### I23. "全计划唯一新建 <2.5k 行"与计划书自身多处明写的新建项自相矛盾，实际新建量约为其 2 倍以上

**级别**：🟠 Important　**来源维度**：Simplicity审查

**问题**：§2 预算句只列五项（SwipeCurved≈0、SoulBehaviorPacket、单文件 Loader、探索闭环、视觉 verdict）并断言"其余全是解锁+接线+配置合并"。但计划书自己写明的新建还有：§4.6"新建 persona.revision.v2.schema.json + v2 C# record + 新 HMAC 域 + 双版本 readback + 迁移测试"、R1-6"新增 edge.bridge.exchange.v2.schema.json"及四个 kind 的边缘+modern 双侧处理、R1-3 outbox 投递器、R1-4 speech.draft/v1 合同、§4.2 digest 固化器+三因子检索+分层携带 schema+logistic 合同升版、R2 pre-work 的 active Release BOM 签发方、§4.5 gateway 收据分类 schema、R3 脚本。按仓内同类模块实测规模加总（zenno-bridge v1 仅 2 个 kind 就是 src 1,003 + tests 351 + schema 111 ≈ 1,465 行；PersonaStore.cs 800 行 + PersonaRevisionV1.cs 217 行；InterestSnapshotV1 单合同 record 270+ 行，且本仓合同一律带 canonicalizer/corpus/测试仪式），全计划新建现实估计 4–6k 行。预算失真会让要求 2 的精简跟踪失效——按 2.5k 立项、实写 5k 时范围蔓延无人察觉。

**证据**：计划书 :42（预算句"全计划唯一'新建'，估 <2.5k 行"）vs :135（§4.6 新建清单）、:177-180（R1-3/4/6 新建）、:110（digest 固化器）、:188（BOM pre-work）；实测 `wc -l` Modules/zenno-bridge/src/*.cs=1,003、tests=351、exchange.v1.schema.json=111（仅 POLL/NATIVE_RESULT 两 kind，schema :43）；PersonaStore.cs=800、PersonaRevisionV1.cs=217

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：发现的核心——:42"全计划唯一'新建'，估 <2.5k 行……其余全是解锁+接线+配置合并"与计划书自身多处明写的新建项自相矛盾——被文档字面坐实：§4.6(:135) 明写"新建 persona.revision.v2.schema.json + v2 C# record + 新 HMAC 域 + 迁移测试"（Simplicity 备选同样要新建 soul-registry 新表/独立合同）；R1-6(:180) 明写"新增 edge.bridge.exchange.v2.schema.json"且四 kind 中仅 request_vision_verdict 被预算句覆盖；R1-3 outbox 投递器(:177)、R1-4 speech.draft/v1 合同(:178)、§4.2 digest 固化器（自称"非装饰"）+ 分层携带 schema + logistic/v2 升版(:110-111)、R2 pre-work active Release BOM 签发方(:188) 均为计划书自定义的新建工作项，却被"其余全是解锁+接线+配置合并"一句否认。全文含 §11 裁决表、附录 B 均未修正该句。finder 引用的仓库数字我逐一 wc -l 复测全部正确，同类模块规模外推有依据。唯一削弱点：finder"4–6k"含测试与 JSON schema，与计划书 §3.1"代码/含测试"分列口径不完全对齐，故"2 倍以上"是合理估计而非坐实；但"唯一新建"定性被文档自身字面证伪，与总量无关。严重级别维持 important 下限：该句是 §1 要求 2 映射与"这不是重写"定性的事实支点，失真会让精简跟踪失去诚实基线，但它不是任何门禁的机器口径，不阻塞执行，不到 critical。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:42（"全计划唯一'新建'，估 <2.5k 行……其余全是解锁+接线+配置合并"）、:135（§4.6"新建 persona.revision.v2.schema.json + v2 C# record"）、:177（outbox 投递器）、:178（speech.draft/v1）、:180（"新增 edge.bridge.exchange.v2.schema.json"四 kind）、:110（digest 固化器"非装饰"）、:188（R2 pre-work active Release BOM）、:210（start-upgrade.sh）。仓库实测：Modules/zenno-bridge/src/*.cs=1,003 行（152+385+347+119）、tests=351（145+206）、contracts/provided/edge.bridge.exchange.v1.schema.json=111 行且 :43 enum 仅 ["POLL","NATIVE_RESULT"]；Modules/persona-sto……

**修改建议**：改写 §2 预算段（:42）：删去"全计划唯一'新建'"与"其余全是解锁+接线+配置合并"两处定性；将 <2.5k 收窄为"四缺口本体（SwipeCurved≈0 / SoulBehaviorPacket / 单文件 Loader / 探索闭环接线 / 视觉 verdict）"口径；另列一张全量新建清单（persona v2 或其备选、exchange v2 schema+四 kind 双侧、outbox 投递器、speech.draft/v1、digest 固化器+分层携带 schema+logistic v2、active Release BOM 签发、gateway 收据分类、R3 轻脚本），按计划书既有"代码/含测试"双口径给各项行数区间与合计。一段文字修改即可，不动任何里程碑。

---

### I24. IG 模拟器风控风险定级不足：R2 Phase-1 验收单点押在 IG 容忍 AVD 上且无替代路径

**级别**：🟠 Important　**来源维度**：可行性

**问题**：计划书对 IG 上模拟器只写'预期触发本人验证，风险知情承担'。现实风险谱更宽：Play Integrity 将模拟器判为 risky 交互，IG 对模拟器+自动化行为可反复 checkpoint、限流乃至永久封号；一旦账号被锁，R2 EMULATOR_VERIFIED 验收（AVD 连续 3 会话、浏览 ≥10 帖、产出草稿）整体不可达，而计划书没有任何应对：无备用账号策略、无替代平台（仓内 reddit 有 100 操作可先走通管线）、无'封号→提前真机门'的切换条款。另外 MV-4 'Play 商店装 Instagram' 需要先在 AVD 上登录一个 Google 账号，这一外部前置不在 §9 人工前置清单。

**证据**：计划书 §5-MV-4('预期触发本人验证，风险知情承担')、§5-R2 EMULATOR_VERIFIED 验收段、§9（仅'提供第一阶段自有 Instagram 账号'，无 Google 账号、无封号应对）；developer.android.com Play Integrity verdicts（模拟器不过 deviceIntegrity、判 risky）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：尝试驳倒失败，发现的三个断言全部实测坐实。(1) 计划书对 IG-on-AVD 账号风险的全部处理仅为"非主力+风险知情承担"两处措辞，全文 grep 封号/checkpoint/备用/Google 零命中，无备用账号条款、无平台切换条款、无封号应对，第 253 行硬停止条件清单也无"账号被锁"项。(2) R2 Phase-1 验收确实单点押在该 IG 账号：§9 将其阻塞位标为 MV-4/R2，MV 验收（:158）要求 IG 可登录浏览+IG dump，且 :202 DEVICE_VERIFIED 还要求"真机与模拟器同账号 dump 对比"——账号被封连真机门对比项也失效，比发现所述更严重。(3) 内部不一致：MV-1 同为单点风险有显式 fallback（:151"失败改二手 Windows 小主机+真机"），IG 账号单点却无——计划书自己的风险处理标准未一贯执行。替代路径可行性实测成立：reddit_operations.json 实有 102 操作、Data/Keywords 仅 reddit 有数据，reddit 先走通 EMULATOR_VERIFIED 管线仓库层面完全可行（R5 才退役 reddit）。Google 账号遗漏属实：MV-4 明写"Play 商店装 Instagram"+MV-2 用 Play 镜像，登录 Google 账号是 AI 无法自做的用户动作，按 §9 表自身定义应入表而缺失。驳倒角度均不成立："非主力即应对"只覆盖主力账号不受损、不覆盖验收不可达；"依赖 IG 未来行为"不适用，因发现指控的是文档当下缺失应对条款，可直接证实。severity 维持 important：不触红线、修复便宜，但 R2 是计划书自认"全案最高风险"里程碑，其验收单点无 fallback 是真实执行缺口。

**复核员独立实测证据**：Docs/RebuildPlan_重构计划书.md:154（MV-4 仅"非主力自有账号…预期触发本人验证，风险知情承担"）；:158（MV 验收要求 IG 可登录浏览+IG dump）；:201（EMULATOR_VERIFIED 验收：AVD 连续 3 会话、浏览 ≥10 帖、≥1 草稿）；:202（DEVICE_VERIFIED 要求真机与模拟器同账号 dump 对比）；:272（§9 仅一行 IG 账号，无 Google 账号、无封号应对）；:151（MV-1 有显式 fallback，对照缺失）；:253（硬停止条件无账号被锁项）；全文 grep 封号|checkpoint|备用|Google 除上述行外零命中。仓库实测：Config/Operations/reddit_operations.json=102 操作、instagram_operations.json=9 操作；Data/Keywords/ 仅 reddit；:242 R5 才退役 Reddit（R2 阶段 reddit 仍在册可作管线验证平台）。

**修改建议**：最小修改三处：(1) §5-MV 段将 IG-on-AVD 账号风险升格为与 MV-1 并列的显式风险项，写入切换阶梯：账号 checkpoint 反复触发或被封 → 留证后（a）换备用非主力账号，或（b）EMULATOR_VERIFIED 的管线/记忆/双 Soul 验收改在 reddit（仓内 102 操作+Keywords 数据齐备）完成，IG 专项（选择器校准、instagram.json 修错、兴趣种子）押后至真机门；(2) §9 补一行"AVD 上可登录的 Google 账号（Play 商店装 IG 前置）| MV-4"（或将 MV-4 改为 apk 旁加载以消除该前置，二选一写明）；(3) §7 硬停止条件或 §5-R2 验收段补一句：Phase-1 账号不可用时验收平台切换需锚点留痕，不算验收失败。

---

### I25. MV-3（Enterprise 接受网络 ADB 挂载的 AVD）风险被降格：官方口径 USB 真机 only，v2 还删掉了 v3 的诚实 caveat

**级别**：🟠 Important　**来源维度**：可行性

**问题**：实测外部资料：ZennoDroid Enterprise 首发公告明确'仅物理设备，不支持模拟器'；官方连接文档只描述 USB 连接流程；网络 ADB（adb tcpip/connect）仅有 ZennoClub 社区 adb.exe 指南称'Wi-Fi 连接仅 ZDE 可用'支撑；'AVD 作为设备被 Enterprise 接受'在官方文档与论坛均无先例。UpgradePlan v3 原有 caveat'Enterprise 官方口径只承诺真机+BlueStacks，AVD 可用性必须实测'，v2 压缩 MV 段时删掉了它，且未给 MV-3 标注失败后果——实际上 MV-3 失败与 MV-1 失败同等致命（整条虚拟化线作废），但计划书只把 MV-1 标为'最大单点风险'。

**证据**：zenno.club 'ZennoDroid Enterprise — Final Release' 帖（仅物理设备/USB，明确排除模拟器）；zennolab.atlassian.net 'Connecting a real device to ZennoDroid'（纯 USB 流程，无网络接入）；zenno.club adb.exe 指南（Wi-Fi 仅 ZDE）；UpgradePlan_升级方案.md:127（v3 caveat 原文）vs 计划书 §5-MV-3（caveat 消失）

**对抗复核意见**（复核员默认立场为驳倒，仍判 CONFIRMED）：核心事实全部实测坐实，且计划书 v2 全文确无别处覆盖：(1) UpgradePlan v3:127 确有 caveat"（Enterprise 官方口径只承诺真机+BlueStacks，AVD 可用性必须实测）"，v3:123 还有总则"每步失败即停并转用户决策"；RebuildPlan v2 §5-MV 第 3 步（:153）两者皆删。(2) v2 头部（:3）明确"取代……UpgradePlan_升级方案.md v3"，故 caveat 留在 v3 里不构成"别处已覆盖"——现行权威文档里该诚实声明已消失。(3) v2:149 断言"唯一在 Apple 芯片 Mac 走得通的拓扑"，:151 只给 MV-1 标"最大单点风险"+fallback；MV-3 无失败后果标注。全文 grep "Enterprise/BlueStacks/必须实测" 零命中；更硬的旁证是 §10《待实测项》(:283-293) 连 SwipeCurved 这种小得多的未知都列了，却不列"Enterprise 是否接受 AVD"这个更大的未验证外部行为——对一份自称"全部结论带证据"的文档是实质性诚实回退。外部证据（zenno.club/官方文档）无需独立复核：项目自己的 v3 已承认官方口径只承诺真机+BlueStacks。两点修正：(a) "与 MV-1 同等致命"言过其实——MV-3 失败后 MV-1 已证的 Parallels VM + USB 直通真机（v2:202、v3 边界段均确认此路）仍活，fallback 是"买真机"而非"买 Windows 小主机+真机"，只作废 AVD 开发/校准半条线（但 EMULATOR_VERIFIED、R2 Phase-1 验收、R2-6 选择器 dump 采集全押在 AVD 上，计划时序仍需重排）；(b) 建议中"把 MV-3 提前"不成立——MV-3 已是第 3 步，依赖步骤 1-2，且已排在 IG 环节（第 4 步）之前，本就是最早可验证位置。执行面上 MV-3 本身仍是"验证"步骤、MV 验收（:158）会兜住失败，故不算无管理风险；……

**复核员独立实测证据**：实测：Docs/UpgradePlan_升级方案.md:123（"每步失败即停并转用户决策"）、:127（"Enterprise 官方口径只承诺真机+BlueStacks，AVD 可用性必须实测"原文在案）；Docs/RebuildPlan_重构计划书.md:3（"取代……UpgradePlan_升级方案.md v3"）、:149（"唯一……走得通的拓扑"无 caveat）、:151（仅 MV-1 标"最大单点风险"+fallback）、:153（MV-3 无 caveat 无失败后果）、:158（MV 验收）、:202（Parallels USB 直通真机）、:283-293（§10 待实测项列 SwipeCurved 却不列 AVD 接受性）；grep 全文 "Enterprise|BlueStacks|必须实测|失败即停" 在 v2 中除上述行外零命中。

**修改建议**：最小修改两处：(1) §5-MV 第 3 步末尾恢复一句 caveat："Enterprise 官方口径只承诺真机+BlueStacks，AVD 可用性必须实测；失败则 AVD 开发/校准线作废，回退 = 提前购真机走 Parallels USB 直通（MV-1 已证 VM 可用时）或二手 Windows 小主机（MV-1 同败时），转用户决策"；(2) §10 待实测项补一条"ZennoDroid Enterprise 经网络 ADB 接受 AVD 为设备（官方无先例，MV-3 实测）"。无需调整 MV 步骤顺序（已是最早可验证位置）。

---

### 2.3 Minor（修订计划书时顺手改）

**M1. §2 与 §3.1 对 AppExplorer v1 处置自相矛盾：一处说删 v1+v2，生死簿只删 v2**（硬要求2·精简生死簿）
- 问题：§2 初衷 4 行的重构动作写"收敛为 app_onboarder 唯一探索器，删 C# AppExplorer/_v2"，而 §3.1 DELETE 表只列 AppExplorer_v2.cs（792 行）。实测 AppExplorer.cs（v1，771 行）在全部 ZDProjects OwnCode coreFiles 编译装载清单内（每个入口启动都会编译它）且在字节基线中。若执行者按 §2 删 v1：所有 ZD 入口编译失败，需连改十余个 OwnCode 文件（本身也是字节基线文件）+ anchor 重签，成本远超 §3.1 预算。若不删 v1："唯一探索器"只在逻辑层成立，771 行已被 app_onboarder 取代的死职能代码继续全量编译装载，且计划书未像对 ManifestLoader 那样（§3.2 "逻辑退役物理保留"）给出明确 disposition 与理由。两节……
- 证据：Docs/RebuildPlan_重构计划书.md §2 表初衷 4 行"删 C# AppExplorer/_v2" vs §3.1 只列"AppExplorer_v2.cs + instagram_v2.yaml"；实测 Modules/Core/AppExplorer.cs = 771 行；ZDProjects/ModuleLoader.cs:48,182、Main_OwnCode.cs:102、DPS_Init_OwnCode.cs:104 等全部 coreFiles 数组均含 "AppE……
- 建议：在 §3.2 补一行：AppExplorer.cs (v1) 比照 ManifestLoader"逻辑退役、物理保留"（在全部 coreFiles 装载清单+字节基线内，职能已由 app_onboarder 取代，运行成本为零）；或若选择物理删除，将其并入 §3.1 与 v2 同批的字节基线具名审批，并同批处理仅有的两个调用方 RedditExplorerTest.cs/RuntimeTestRunner.cs（coreFiles 清单因 File.Exists 守卫无需改动）。同时把 §2 行36 措辞改为与所选裁决一致（如"删 AppExplorer_v2，v1 逻辑退役物理保留"），消除……

**M2. 生死簿多处行数与实测不符（方向偏保守，但与总数口径自相矛盾）**（硬要求2·精简生死簿）
- 问题：计划书总数 213,735 在"C#/Python 排除 obj/ 生成码"口径下精确复现（实测 218,650 − obj 4,915 = 213,735），但同一口径下：11 个 factory 目录 = 42,082 行 ≠ 声称 40,501（差 1,581，含测试口径 50,046 亦无法复现，最接近为 cs+py+json+yaml+sql=49,152）；WeeklyEvolve.cs 实测 271 行 ≠ 声称 331；external_gate F8/F9 仅具名函数就 ≥2,135 行 ≠ 声称约 1,200（差 ~78%）；planner 实测 2,783 ≠ 3,053。按生死簿全部删项计算，处置后约 16.7 万行（约 -21~22%），比声称的 172,400/-19% 更多——结论方向成立且偏保守，但"全部结论带证据"的文档不应有无法复现的关键数字。
- 证据：实测命令：find（排除 node_modules/.venv/.git）+ wc -l。全仓 cs/py=218,650，obj/ 生成码=4,915（差值精确等于 213,735）；factory 11 目录 cs/py 排除 obj=42,082；wc -l Modules/WeeklyEvolve.cs=271；external_gate.py f8/f9 具名函数逐个测量合计 2,135 行（_validate_f9_rollout_lines 单个 465 行）；planner cs……
- 建议：在 §3 首句注明行数口径（C#/Python、排除 obj/ 生成码、wc -l、基线 458f9bd），并修正：factory 40,501→约 42,100（含测试口径 50,046 删除或改为可复现口径）、WeeklyEvolve 331→271（+壳 48）、F8/F9 约 1,200→约 2,000+（另含 9 个 schema 文件与测试段）、planner 3,053→2,783（保留结论不变）、处置后 172,400/-19%→约 16.7 万/-22%。

**M3. §3.1 "配置死物"行爆炸半径漏报：删 LastLayoutXml 是字节基线级手术，不是改 README**（锚点核验·治理Python、硬要求2·精简生死簿）
- 问题：§3.1 把 OperationContext.LastLayoutXml 死字段与 ActionCatalog/StepPlans/空目录并列在"配置死物"行，爆炸半径写"改 README + phase0.py:90-101 白名单"。但 LastLayoutXml 位于 Modules/Core/OperationContext.cs（该文件在字节基线保护清单内），删字段=修改 legacy 保护文件，需走 §7.2 的具名审批更新 legacy-csharp-bytes.v1.json（P3 全局串行），与该行声称的成本量级完全不同。另删 Modules/Decision/Persona/Report 三目录除 :90-101 的 KNOWN_RUNTIME_ROOTS 外，phase0.py:103 的 LEGACY_UNREGISTERED_MODULE_DIRECTORIES……
- 证据：Modules/Core/OperationContext.cs:44 "public string LastLayoutXml;"、:82 初始化，全仓无其他读写（grep 实测，死字段属实）；legacy-csharp-bytes.v1.json:198 含 "Modules/Core/OperationContext.cs"；Tools/ci/phase0.py:89-101 KNOWN_RUNTIME_ROOTS、:103 LEGACY_UNREGISTERED_MODULE_DIREC……
- 建议：§3.1:59 把 OperationContext.LastLayoutXml 从"配置死物"行拆出，标注"legacy 字节基线文件内修改：走 §7-2 具名审批更新 legacy-csharp-bytes.v1.json（P3 全局串行），建议与某次已排期的 legacy 审批批次（如 R0-2 删除批或 R2-3 记忆接线批）合并顺手删，不必单独占一次 P3 串行窗口；系修改非物理删文件，不需 anchor 重签"。phase0 白名单引用可顺带改为 :89-103 并注明系删目录后的清洁度修边（非 CI 必改项）。

**M4. 八步手术遗漏两处 factory 引用点：.github/CODEOWNERS 与根 README**（硬要求2·精简生死簿）
- 问题：自行全仓 grep 对照八步清单，发现两处未列入：(1) .github/CODEOWNERS:13 的 /Modules/factory-*/ 归属规则——删 11 后成死规则；且按 phase0 规则改 .github/ 下任何文件会令收据 scope 扩为全模块，事后单独清理成本不成比例，应并入 R0-2 同批。(2) 根 README.md 大篇幅描述 AI Factory 能力（F3-F4 章节等），删除后即成误导性文档；R0-6 只补 AGENTS.md/Docs 指针，不覆盖 README 的 factory 章节。
- 证据：.github/CODEOWNERS:13 "/Modules/factory-*/ @HelloYoung2025"；README.md:35 "DPS AI Factory --upgrade artifacts and evidence--> DPS Control Plane"、:56 "F3-F4 AI Factory | 已实现…"；计划书 §3.4 八步与 §5-R0 均未提及这两个文件（README 仅在 ActionCatalog 行提及 Config/README.md）。
- 建议：§3.4 步骤 1 或 8 增补半句：同批删除 .github/CODEOWNERS 第 13 行 factory 归属规则（利用 R0-2 本就全模块 scope 的收据与异构复核，避免事后单独治理批次）；§5-R0-6 文档批次增补：根 README.md 删改 AI Factory 相关章节（35 行架构图、F3-F4 行等），与 AGENTS.md/Docs 指针同批。两处合计约两行改动，不影响其余批次结构。

**M5. §6'机器强制读 AGENTS.md→改→门禁验证'表述夸大：收据可事后补签，时序不可机器证明**（硬要求3·单独/并行升级）
- 问题：计划书 §6/§7-1 称单独升级的规程'机器强制 = R3-3'。实测收据机制：resolve_instruction_receipt 只绑定 baseline、scope 内 AGENTS.md/manifest/合同的文件哈希与 diff 指纹，validate_instruction_receipt 只重算比对这些内容——没有任何时序约束。先写完代码再跑 start-upgrade.sh 补签收据，与先签后写产出的收据完全同构，门禁 --receipt-in 复验同样通过。机器能强制的是'存在一份与改动 scope 一致、绑定了应读文件哈希的收据'，不能强制'升级前先读'。鉴于'升级前先读 AGENTS.md'是用户既往确认的硬性约束，计划书把纪律说成机器保证属于实质性夸大。
- 证据：phase0.py:3534-3664（resolve_instruction_receipt：仅绑定文件哈希/diff 指纹/scope，无签发时点与工作树状态的先后关系约束）、:3667-3690（validate 仅内容重算比对）；run_phase0_gate.py:122-127（--receipt-in 仅触发同一验证）；计划书 §6'R3-3 会话脚本 + phase0 收据强制…'、§7-1'机器强制 = R3-3'原文
- 建议：§6 末句与 §7-1 括注改为："机器验证的是收据与最终改动 scope/应读文件哈希的一致性并入证据链（且因 diff 指纹绑定终态，收据必然在代码定稿后签发/重签）；'先读后写'的时序属会话纪律，由 R3-3 规程 + R3-4 异构复核兜底"。同时在 §5-R3-4 的 checklist 中显式加入复核项："diff 是否体现对目标模块 AGENTS.md 约定的遵守"，使纪律兜底可操作。

**M6. ActionExecutor "13 指令"计数不实（实为 17 个动作字符串/16 个处理器），另有个别锚点漂移**（锚点核验·legacy C#、硬要求4·设计初衷八条）
- 问题：§2 行 2 称 ActionExecutor 是"13 指令"的 JSON 解释器。实测调度表为 find/tap/swipe/scroll/delay/type/input_text/verify/require/refresh_layout/foreach/back/log/set_var/call_operation/if_exists/random_pick 共 17 个动作字符串（type 与 input_text 共用处理器，计 16 个处理器），怎么合并都到不了 13。1589 行、:1381 语义别名唯一平台特例、reddit 102（计划书写 100，可接受）/babycenter 35/instagram 9 等其余数字均属实。另 §2 行 1 把 SessionRunner.cs:2464-2514 标注为"意图回退链"，实际 ResolveIntentWithFa……
- 证据：ActionExecutor.cs:223-239（17 个 if (action == ...) 调度分支）；SessionRunner.cs:2516-2529（ResolveIntentWithFallback 实际起点）
- 建议：计划书行 34 将"13 指令"改为"17 个动作指令（13 个基本步骤 + 4 个控制流构造 foreach/call_operation/if_exists/random_pick；type/input_text 共用处理器，计 16 个处理器）"，明确口径；行 33 将 SessionRunner.cs:2464-2514 校准为 :2463-2560 并标注为"意图映射加载 + 动作→意图解析 + 回退链"，或分别标注 :2463-2513（映射与解析）和 :2519-2551（ResolveIntentWithFallback 回退链）。

**M7. WeeklyEvolve.cs 行数写错：计划书称 331 行，实测 271 行**（锚点核验·legacy C#）
- 问题：§3.1 DELETE 表将 WeeklyEvolve.cs 规模写为"331 行 + 壳"，实测 Modules/WeeklyEvolve.cs 为 271 行、ZDProjects/WeeklyEvolve_OwnCode.cs 壳为 48 行（合计 319，也凑不出 331）。删除结论本身不受影响（零调用者、双轨演化重复均实测成立），但该文件走字节基线具名审批删除，行数会进入删除批次的核对材料；且计划书自称"全部结论带文件:行号证据"，数字错误削弱证据链可信度。
- 证据：wc -l Modules/WeeklyEvolve.cs → 271；wc -l ZDProjects/WeeklyEvolve_OwnCode.cs → 48；grep 全仓 WeeklyEvolve 引用仅命中其 OwnCode 壳（零外部调用者，计划书此点正确）。计划书原文 §3.1："WeeklyEvolve.cs（+OwnCode 壳）| 331 行 + 壳"
- 建议：Docs/RebuildPlan_重构计划书.md:56 将"331 行 + 壳"改为"271 行 + 48 行壳"。仅此一处，无需改动删除流程或其他章节。

**M8. external_gate F8/F9 删除量低估约一倍，字面范围会遗留约 850 行 f9 孤儿辅助函数**（锚点核验·治理Python）
- 问题：计划书 §3.1 写 "external_gate F8/F9 分支 | 约 1,200 行"，§3.1 处置语句为"只动 _validate_f8/f9 分支与 schema/测试"。实测 _validate_f8 为 115 行（:3734-3848）、_validate_f9* 命名函数群约 1,270 行（:4774-6047），合计约 1,385 行与"约 1,200"吻合；但另有一批仅被 f9 链调用的辅助函数——_f9_module_contract_mode/_f9_canonical_communication_edge/_f9_communication_pair_sha256/_f9_route_details/_f9_runtime_routes_for_major/_build_f9_compatibility_artifact（:3888-4502，约 615 ……
- 证据：实测 Tools/verification/external_gate.py（全文件 6,200 行）：grep 顶层 def 得函数边界；逐一 grep 各辅助函数调用点确认全部落在 f8/f9 链内（如 _build_f9_compatibility_artifact 唯一调用 :5457 在 _validate_f9_rollout_lines 内；_validate_ordered_waves 仅 :3751/:5899 两处调用）。计划书原文 §3.1："external_gate F……
- 建议：§3.1 external_gate 行改为：'external_gate F8/F9 分支及其专属辅助函数 | 约 2.3k 行'，处置语句改为：'删除集 = _validate_f8/_validate_f9* 主分支 + 仅被该链调用的辅助函数（_validate_ordered_waves、_dependency_waves、_f9_module_contract_mode、_f9_canonical_communication_edge、_f9_communication_pair_sha256、_f9_route_details、_f9_runtime_routes_for_maj……

**M9. R2-6 选择器修复锚点指向被 §3.2 逻辑退役的 Manifests/instagram.json，且 :35 是合法原始用法而非复制错误**（锚点核验·v2模块）
- 问题：§5-R2-6 写"AI 校准 PlatformsConfig→…修 instagram.json:88/:35/:141 复制错误"。实测：(1) 全仓唯一 instagram.json 在 Configs/Manifests/ 下，属于计划书 §3.2 自己裁定"合同层废止、逻辑退役"的 Manifest 体系——在单一事实源已定为 PlatformsConfig 体系（§2 初衷 3）的前提下，把 R2 校准工作量花在退役文件上是内部矛盾；(2) 三处行号共享选择器 com.instagram.android:id/row_feed_button_comment，其中 :35 是 tap_comment_button 动作对评论按钮的合法原始用法，真正的复制错误只有 :88（tap_post"点击打开帖子"误用评论按钮）和 :141（Reels comment 误用 feed 评论按……
- 证据：Configs/Manifests/instagram.json:35（tap_comment_button→row_feed_button_comment，语义正确）、:88（tap_post→row_feed_button_comment，错）、:141（reels comment→row_feed_button_comment，可疑）；find 实测 Config/ 下无 instagram.json，仅 Config/PlatformsConfig.json（instagram 段 :6……
- 建议：R2-6 该从句最小修改：删去"修 instagram.json:88/:35/:141 复制错误"，改为"Config/PlatformsConfig.json instagram 段（:662 起）选择器随 dump 校准核正（其中 like_button/comment_button 等通用命名 id 重点比对真机 dump）；退役参照物 Configs/Manifests/instagram.json 不列修复项（若坚持留档修正，行号只留 :88/:141，:35 为合法原始用法）"。

**M10. R0-3 "迁移 14 条 quarantine-only v1 边"与 R0-2 删 factory 的时序自相矛盾：删除后只剩 6 条**（锚点核验·v2模块）
- 问题：基线上 quarantine-only v1 合同对应的 module.yaml 通信边确实恰好 14 条（计数精确），但其中 8 条全部落在 factory 模块内部（factory-control-plane-host×4、factory-rollback-controller×1、factory-trusted-runner×1、factory-worktree-manager×2，涉及 instruction.receipt/module.change.plan/rollout.event/upgrade.intent），而计划书把 factory 删除排在 R0-2、边迁移排在 R0-3——执行到 R0-3 时这 8 条边已随目录删除消失，实际待迁移的只有 6 条（evidence-service←gbrain-projector 与 ←memory-event-ledger、g……
- 证据：脚本枚举 Modules/*/module.yaml：quarantine-only 合同对 10 个（action.proposal/1、gbrain.projection/1、memory.event/1、memory.outbox/1、native.stop.proof/1 + 5 个 factory 合同），命中通信边共 14 条，其中 8 条 consumer 为 factory-* 模块；计划书 §5-R0 第 2/3 步顺序原文。
- 建议：将 R0-3（Docs/RebuildPlan_重构计划书.md:165）改写为："迁移 6 条 quarantine-only v1 边到 v2（基线共 14 条，其中 8 条为 factory 模块内部声明，随 R0-2 删除自然消失）"，并附 6 条边清单：evidence-service←memory-event-ledger(memory.event)、evidence-service←gbrain-projector(gbrain.projection)、gbrain-projector←memory-event-ledger(memory.event)、interest-redu……

**M11. control-plane-host 9,152 行与 planner 3,053 行两个精确数字均不可复现**（锚点核验·v2模块）
- 问题：§3.2 以精确到个位的行数作为保留裁决依据。实测任何常规口径都对不上：control-plane-host .cs（排 obj/bin）8,576 行、含 obj 8,847、cs+json 8,879、全文件 10,338，均非 9,152；planner .cs 2,570、含 obj 2,840、cs+json 3,357、全文件 3,929，均非 3,053。结论方向不受影响（两模块该保留、planner 删除收益即便按 2.6k 算也小于改造成本），但计划书通篇以"全部结论带文件:行号证据"自我背书，两个假精确数字损害其余锚点的可信度，且 §3.2 的"删除收益 3k"成本核算建立在偏大 19% 的数字上。
- 证据：实测命令 find Modules/control-plane-host -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | xargs wc -l → 8,576；同法 planner → 2,570；另测含 obj、cs+json、全文件三种口径均无一命中 9,152/3,053。计划书 §3.2 原文"control-plane-host（9,152 行）""planner（3,053 行）"。
- 建议：将 Docs/RebuildPlan_重构计划书.md:63 与 :66 两处改为可复现口径并注明方法："control-plane-host（.cs 源码排 obj/bin 8,576 行）"、"planner（.cs 源码排 obj/bin 2,570 行）"，:66 的"删除收益 3k"同步改为"约 2.6k"。结论（两模块保留）无需改动。

**M12. 数字口径前后不一："两道 fail-closed 锁" vs 三道锁；"补四个真缺口" vs 两处列出五项；行数账三种数字互相打架**（内部一致性）
- 问题：(1) 一句话定性说"解锁两道 fail-closed 锁"，§2 同一句里先说"双层锁死"后列"三道锁"，R2-2 与附录 A 均为三道锁（实测 7 个 OwnCode 文件含 LEGACY_DISABLE_NEW_COMMANDS + SessionRunner.cs:173 恒 false + 文本断言测试，确为三道）。(2) 标题口径是"四个真缺口"（R2-4 列 a-d 四项），但 §1 要求 4 行与 §2 缺口预算都列了五项（多出"视觉验证上移大脑侧"），视觉验证到底算缺口新建还是解锁项，两种口径并存。(3) 行数账：213,735→172,400 隐含 -41,335，但 §3.1 删除清单自述之和 ≈50,046(factory 含测试)+792+331+1,794+1,200≈54,163，§3.4 又说"约 -5.0 万行"，三者互斥；实测全仓 .cs/.py 218……
- 证据：Docs/RebuildPlan_重构计划书.md:10（两道锁、四个真缺口）；:29（"双层锁死…三道锁"）；:191/:324（三道锁）；:21/:42（五项缺口清单）；:19/:48（-19%/172,400）；:54（50,046）；:85（"约 -5.0 万行"）；实测：grep -rln LEGACY_DISABLE_NEW_COMMANDS ZDProjects = 7 文件；全仓 wc -l = 218,650，Modules/factory-* = 42,150
- 建议：最小修改三处：(1) :10 "两道 fail-closed 锁"改"三道 fail-closed 锁"，:29 删"双层锁死"改"三道锁锁死"；(2) 缺口口径二选一并全文统一——保持"四缺口"则 :21 把"视觉验证上移大脑侧"移出缺口列表、标注"归解锁改造（R2-3），其新建代码计入 §2 预算"；(3) §3 行数账注明统计口径（.cs/.py、是否含根 Tests/ 与 factory 模块内测试），并让 213,735/172,400 与 §3.1 清单加总一致（按代码口径约 -4.5 万 → 约 169,100 行，或如实写区间），§3.4 "-5.0 万"旁标注"仅 facto……

**M13. 附录 B "加 APP 需 6 文件 → 单文件 YAML" 与正文 "5 处触点" 及单一事实源 PlatformsConfig（JSON）双重不对应**（内部一致性）
- 问题：§2-3 说加一个 APP "现需 5 处触点"，附录 B 对应行写"加 APP 需 6 文件"，数量对不上。更实质的是格式：正文裁决"单一事实源 = PlatformsConfig 体系"且"Manifest 体系（JSON+YAML+ManifestLoader）合同层废止"，实测 Config/PlatformsConfig.json 是 JSON；附录 B 却把目标态写成"单文件 YAML + 单一 Loader"——若照附录 B 做成 YAML，等于在刚废止 YAML Manifest 体系后再引入一个新 YAML 事实源，与正文方向相反（R2-4(c) 的"单文件加 APP Loader"本身未写格式，无从仲裁）。
- 证据：Docs/RebuildPlan_重构计划书.md:35（§2-3 "5 处触点"、"单一事实源 = PlatformsConfig 体系"、Manifest JSON+YAML 废止）；:342（附录 B "加 APP 需 6 文件 | 单文件 YAML + 单一 Loader"）；:193（R2-4(c) 未定格式）；实测 ./Config/PlatformsConfig.json 存在且为 JSON
- 建议：附录 B :342 行改为："加 APP 需多文件多触点（§2-3：5 处触点） | 单文件（PlatformsConfig 体系内，JSON）+ 单一 Loader | 要求 4 通用 APP"；同时在 R2-4(c)（:193）补一句明确新 APP 配置的格式（JSON）与落点（PlatformsConfig 体系），杜绝施工时按附录 B 造出第二种格式。


---

## 3. 未复核项（复核员因会话额度失败，仅 finder 单方证据，采信前需自行核验）

**U1. (important) GBrain NAS 部署依赖盘点不完整：LAN 暴露边界、NAS 运行时兼容、Voyage embedding 凭证三处缺口**（可行性）
- 计划书 R1-5 说'OAuth/Source 隔离按本地非生产边界'，但被引用的边界文档强制 `gbrain serve --bind 127.0.0.1` 且'本地阶段禁止 0.0.0.0、公网 tunnel'，并要求公网部署另行完成 TLS/reverse-proxy/OAuth issuer 评审——GBrain 装到 NAS 后 modern 侧必然跨 LAN 访问，处于两个边界之间，传输方式（LAN 直连 bind？SSH 隧道？TLS？）计划书未定义。其次 GBrain 是 Bun 应用（固定 commit 4ee530f3）且需 PostgreSQL 18.4+pgvector，用户 NAS 的架构/运行时能否满足未列入 §10 待实测。第三，R1 验收含'搜索命中再 exact 复核'，运维文档规定 Soul 记忆 embedding 用 voyage-4-large（需……
- 证据：Docs/Operations/GBrainCompany_LocalNonProduction_本地非生产.md:55-58（--bind 127.0.0.1、禁 0.0.0.0/tunnel、公网另评审）、:15（voyage-4-large）；Docs/Platforms/GBrainCompany_Compatibility.md:15（Bun lock 固定 commit）、:81（'Voyage embeddings and DeepSeek calls remain unconfi

**U2. (minor) v2 取代 v3 时丢失两条 load-bearing 的 MV/R2 施工细节**（可行性）
- v2 声明取代 UpgradePlan v3，但 MV 段压缩时丢了两条会直接导致 R2 门禁失败或部署错误的细节：(i) VM 内须按 CapabilityProbe 精确版本安装 adb 37.0.0-14910828 与 pwsh 7.6.2——CapabilityProbe.cs:135 对 adb 版本做精确相等断言，版本漂移 F6 探测直接 FAIL；(ii) 'ZennoDroid + supervisor/worker + adb server 必须共置同一 Windows 实例'（BridgeLoopbackHost fail-closed 双向 loopback 校验，不得跨 VM 拆分）。这两条在现行权威文档中已不存在。
- 证据：计划书头部（'取代…UpgradePlan_升级方案.md v3'）与 §5-MV-5（无 adb/pwsh 版本、无共置约束）；UpgradePlan_升级方案.md:121（共置约束）/:129（pwsh 7.6.2 与 adb 37.0.0-14910828）；CapabilityProbe.cs:135（Require(snapshot.AdbVersion == "37.0.0-14910828")）

**U3. (minor) §7-2 引用不存在的里程碑：'R5 Reddit 退役'与'R3 五文件批'无对应任务**（可行性）
- §7-2 排定 anchor 重签次数为'全案至少 3–4 次重签（R0-2 删除批、R2 flip、R3 五文件批、R5 Reddit 退役）'，但 §5 里程碑只有 MV/R0/R1/R2/R3，不存在 R5；R3 的任务清单（CI 化+会话规程）也没有任何触碰字节基线的'五文件批'。重签排期基于幽灵里程碑，Reddit 双路径退役（§2 初衷 2 提到'退役为参照物'）在整个 §5 路线中没有落点。
- 证据：计划书 §7-2（'R3 五文件批、R5 Reddit 退役'）vs §5（无 R4/R5；R3 任务 1-5 均不触字节基线）

**U4. (minor) §10 漏列：AVD 重启/snapshot 后设备身份与 ZD 重连稳定性未列入待实测**（可行性）
- R2 派单链按 device_binding_id 键控（executor-gateway 的 ActiveReleaseBomBindingV1 以 DeviceBindingId 为轴），而 MV 拓扑下'设备'= 经 socat 转发的网络 ADB 端点。AVD 冷启动/snapshot 恢复后 adbd 身份、ip:port、boot ID 可能变化，ZennoDroid Device Manager 是否稳定重连、绑定链是否需要重建，直接影响 R2 EMULATOR_VERIFIED 的'连续 3 会话'验收，§10 待实测清单未覆盖（只覆盖了网络 ADB 一般稳定性）。
- 证据：VerifiedExecutorGateway.cs:13-27（ActiveReleaseBomBindingV1 含 DeviceBindingId）；计划书 §10（仅'PowerShell E2E…网络 ADB 稳定性'一条，无 AVD snapshot/设备身份项）、§5-R2 EMULATOR_VERIFIED（连续 3 会话）


---

## 4. 已驳回的疑虑（对抗复核证明计划书是对的——修订时不要"顺手修"这些）

| 疑虑 | 驳回理由（摘要） |
|---|---|
| 双 Soul 行为可辨验收的统计功效问题只被推迟、未被解决，且与 A12 扰动幅度约束互相顶牛 | 发现的三个支柱逐一被实测推翻。(1) "3 会话样本锁死可辨性检验"是误读：计划书 201 行 R2 验收中"双 Soul 行为可辨"条目自带"+ 最小样本量，R3 验收前定死"的参数化写法，3 会话是其他条目的下限而非可辨检验的样本上限；阈值未定稿前该条目自阻塞无法评定，定稿后验收按定死样本量执行，AVD 环境加跑会话近乎零成本（§10 的"样本小"针对 2 台真机低频场景）。(2) 时序不空转：191 行 R2-2 前置"R3 异构…… |
| persona.revision v2 与替代省法二选一悬置：无决策标准、无最迟决策点，阻塞链未管理 | 见上…… |
| §2 缺口新建预算清单遗漏记忆动态新组件，与 §4.2/§5-R1 内部矛盾 | 该发现的三个支柱经实测有两个不成立、一个只剩措辞残渣。(1) outbox 投递器"必须新写、仓库无任何实现"不实：memory-event-ledger 已有完整 outbox 机器——v2 合同、outbox_v2/outbox_delivery_v2 表、事件与 outbox 同事务写入（InsertOutboxAsync）、ReadPendingOutboxAsync 按序读 pending。§5-R1-3 自述为"最小轮询（l…… |
| external_gate 通用信封层强制 factory_binding，是 §3.4 漏掉的第三处 factory 硬依赖，会卡 R2 的 F6/F7 验收 | 发现引用的代码事实属实（通用信封层确实对所有 stage 强制 factory_binding + 验签 release_bom），但其核心结论"删 11 后信封造不出来、硬阻塞 R2 的 F6/F7 验收"被实测驳倒，且真正硬的部分计划书已覆盖：(1) factory_binding 六字段在 external_gate 中仅做格式校验——全文件对 upgrade_stream_id/instruction_receipt_id/so…… |
| R0→R1'记忆链三模块候选门禁允许暂红'与 landing 协议内在矛盾，且'暂红'在机器上不可表达 | 发现的核心前提"'允许三模块暂红'没有任何机器表达方式"不成立。实测：run_candidate_gate.py 有必填 --level {contract,integration} 参数，contract 级运行的套件清单只选 type=contract（discover_candidate_inventory :483-487），integration 套件根本不进 suite_results，因此 integration 红对 c…… |
| §6 并行判定表缺行：同一模块双分支、Docs/ 等无主路径、'共享根'未定义 | 发现所引代码证据全部实测属实（治理前缀、空 impacted 回落、信任根清单及 clean FAIL 机制均核对无误），但其核心断言"执行者按现表分类会对这三类做出错误的并行判断"不成立：判定表是白名单，未列场景不获并行授权，默认退回同节"落地必串行"，缺行是保守失效而非错误放行。①同模块双分支不满足 P0 行"两个无依赖模块"条件，不会被误判；即便并行开发，:234 landing 协议（rebase+合并 HEAD 全量重跑）机器…… |
| "消灭双大脑：加权随机收编为 planner 确定性策略插件"与 A4 红线的边界未定义——收编实质是在 planner 新写决策代码 | 裁决理由见上。…… |
| "新增第 4 个 exchange kind"与 v1 schema 实测不符：v1 只有 2 个 kind | 发现的实测数据无误，但结论不成立，属于不影响执行的措辞吹毛求疵。我亲测确认：v1 schema 的 exchange_kind 枚举确实只有 POLL 和 NATIVE_RESULT 两个值（finder 引用的行号准确）。但驳倒理由有三：(1) 计划书从未断言"v1 已有 3 个 kind"——§2 初衷 5 原句"新增第 4 个 exchange kind request_vision_verdict"在同一括号内紧跟"见 §5-R…… |
| R3 实际卡在 R1→R2 关键路径上，路线图把它画成 R0 后的可选旁支，图/文/验收三者不自洽 | 发现的行号引用全部属实，但其三条核心推理均被计划书正文自身击破，剩余部分只是 ASCII 简图的呈现问题：(1)"R3→R2 依赖边缺失"不成立——该依赖在正文以加粗形式明写于 R2-2（:191"前置：R3 异构复核机制已通电且埋错演练通过（封控制空窗）"），且是 fail-closed 前置（不满足则 R2-2 无法推进），执行者不可能因简图漏边而误闯；把"计划书已写对的东西"报成发现违反审核准则。且 :191 要求的只是"复核机制…… |
| R1 验收被 GBrain NAS 用户人工项单点阻塞，无降级路径——与 MV-1 有 fallback 的做法不一致 | 发现的行号引用属实，但三个支柱实测后均不成立或严重缩水：(1) "R1 无法推进"不实——被 NAS 阻塞的只有最终验收盖章，R1 全部 8 个工作项可用仓库既有成文的本地 GBrain 路径推进：Docs/Operations/GBrainCompany_LocalNonProduction_本地非生产.md 完整定义 Mac 本地部署（独立 GBRAIN_HOME、loopback PG、gbrain init/serve 命令模板…… |
| §8 回滚对 GBrain 已写入事件只有一个词"compensation"，机制完全未定义；NAS schema 迁移的回滚未覆盖 | 发现的三个支柱经实测均不成立或大幅高估。(1) "compensation 完全没有定义"对枚举的两类副作用之一（草稿）直接失实：R1-4（:178）已定义 speech.draft/v1 合同与含 discarded 的完整状态机，弃分支草稿的补偿=追加 discarded 迁移，合同归属与审计语义有落点。(2) "幽灵权重污染要求 1"的危害推理与计划书设计模型自相矛盾：GBrain 事件是真实发生行为的观察记录（:180/:194…… |
| "七项授权桥"在本计划书内从未枚举出七项，"七项证据齐"验收无法核对；权威枚举只存在于被本文件取代的 UpgradePlan v3 | 发现所引行号属实但结论错误。七项的权威定义在 ProjectTechnicalBook_项目技术书.md:511（§9.1），该文档状态 Current、未被 RebuildPlan 取代；UpgradePlan:174 自己注明出处就是"技术书 §9.1"。"六项已实现"与"七项"自洽（六项有代码 + Worker ABI 纯缺失 = R2-2 ①）。映射除"幂等"一词缺席外均可在 RebuildPlan 内完成（§4.5 :128 …… |
| F8/F9 既在 §3.1 列为 DELETE（独立小提交），又在 R0-5/附录 B 标"DEFERRED + 删除触发条件"——删还是缓删自相矛盾，批次归属也冲突 | 发现所引三处原文与代码位置均属实，但"既说删又说缓"的矛盾建立在把"删除触发条件"当成含混新词的误读上。实测前文档 UpgradePlan v3（计划书声明取代的对象，词汇被 v2 直接继承）第 67 行以加粗定义了该机制："F8、F9 标注 DEFERRED；删除触发条件（v3）：M2 真机验收通过后的首次治理修订中删除其 schema/验证器，期间冻结不维护，若治理快照重生成因其报错则即时删除"（:238 表格同款）。按此定义，v2…… |
| RuleEngine 评分职责的目标归属在 §2-1、§4.2、R2-5 三处描述不闭合：留手层、改手层代码、还是收编进大脑侧 planner 插件？ | 裁决理由见 reasoning 字段。…… |
| A12 论证未覆盖 SoulBehaviorPacket 的'统计可区分'目标本身，且扰动无参数空间上限 | 三个子命题逐一驳倒。(A) §4.4 拟人化允许集逐项列出的维度（延迟分布/点偏移/滑动弯曲度/打字速度）与 §4.3 SoulBehaviorPacket 扰动维度完全一致，§4.4 本身就是对 SBP 的 A12 清关；"统计可区分"目标是用户硬要求 1 的直译，度量落点在 §5-R2 自有遥测（对照日志/预定义指标），计划书从未提议以平台检测信号为度量，红线两处（:23/:124）已封死规避方向——要求为未提议的行为写免责声明属吹…… |
| instagram 兴趣种子与触发词数据缺失被降级为"不阻塞起步"待实测项，实为硬要求 1 的 R2 验收硬前置 | 发现的核心因果链（"缺 Data/Keywords/instagram → 兴趣分化/relevance/interest_signals 全链无输入 → R2 兴趣评分差异验收空转"）在架构上不成立。实测 SessionRunner.cs:496-504/:781-802：Data/Keywords/{platform}/interests.json 按平台加载、与 soul_id 无关，是平台级单例，架构上不可能提供 per-Sou…… |
| persona.revision v2 与"改存 soul-registry 侧"两方案挂在关键路径上不拍板，违背 Simplicity First 与用户"选一个并解释"原则 | finder 引用的仓库证据全部属实（我逐一实测确认），但三个核心推论均不成立：  1)"关键路径上不拍板"夸大了。该项归属是"R1→R2 前置"（计划书:99），即只需在 R2 授权桥装配前完成；而决策点定在"R1 定稿时"（:135），且 R1-2 明确"设计先行……在 R1 开工前定稿锚点留痕"（:176）——决策时点严格早于依赖时点，不阻塞 R0 与 R1 启动。更关键的是：计划在拍板前不为 v2 路线投入任何实现工作（R1-7…… |
| MV-5 治理配套（EMULATOR_VERIFIED 等级 + canary 措辞 + CapabilityProbe 参数化）排期过早，且与新证据等级构成双机制冗余 | 该发现的代码证据本身属实（我实测确认 CapabilityProbe.cs:150-152 硬编码三阈值、:157 恒 Require(false)），但三条核心论证全部不成立。(1)"排在第 0 天"是误读：计划书 :144 说的是 MV **轨道**第 0 天启动，轨道内六步有明确编号顺序，MV-5 是第 5 步，排在 MV-1（Parallels demo 激活）至 MV-4 之后；§9 :270 也只把 MV-1 标为"第 0 …… |
| SoulBehaviorPacket 的种子派生扰动在 2 Soul 规模下是多余抽象，且反而削弱"双 Soul 行为可辨"验收 | 该发现把"双 Soul 可辨性"错误地压在种子扰动一层上，忽略了计划书的两级结构。实测计划书：§4.3（:120）明确写扰动是"在 behavior_archetype 基线（四档之一）上"生成的二级差异；而 behavior_archetype 本身是 per-Soul 的显式人工配置字段——§4.1 表格③（:99）与 §4.6 写明它由 persona.revision v2 承载，每个 Soul 独立赋值。四档基线的参数差距是构…… |
| speech.draft/v1 预冻结 approved/published/failed 三个属于未设计流程的状态，属投机性合同设计 | 发现的前提——"speech.draft/v1 预冻结 approved/published/failed 进封闭枚举，迫使为不可达状态造 corpus fixture"——是 finder 对计划书 :178 括号内容的脑补 schema 设计，计划书从未承诺，且被三层实测证据反驳：(1) 五状态并非"未设计流程的猜测"，而是设计初衷文档 TargetArchitecture:195-198 的原生事件分类（speech.drafte…… |
| ε 阈值与分层携带的"远期留强者"是同一投影点上的两套强度过滤，可合并为一 | 发现的前提"同一投影点上的两套强度过滤"不成立。实测 GBrainProjectionV2 投影含 Events 与 Interests 两个独立数组：ε 阈值过滤的是兴趣主题（聚合衰减置信度，InterestReducer 对多条证据 Sum 后截断的量），64KiB 分层携带过滤的是单条记忆事件（按 importance/三因子口径）。两个"strength"量纲不同、对象不同：事件没有兴趣强度，兴趣是跨时间聚合体没有单一时间戳，无…… |
| 外部签名权威无默认方案，且实际阻塞点在 R0-2 而非 R2，并与异构复核接入时序矛盾 | 发现引用的仓库事实全部属实（三文件确在 79 文件基线内、双向严格相等、删除必须重签 anchor），但三条核心论断都被计划书原文或仓库实测驳倒。(1)"无默认方案"不成立：§7-2（242 行）明确规定"R0 前先写 anchor 重签 runbook"，且逐项列出 runbook 必含内容——签发主体具体操作流、anchor 存放与 CI env 挂载、新 baseline_commit 锚定时序——正是发现声称"计划书没写"的三件…… |
| 异构复核（DeepSeek/GLM）未接入时的降级路径缺失，R2 翻锁可能死锁 | 发现的三个支柱全部不成立。(1)"无降级路径"：实测现行 risk-policy.yaml R2/R3 均为 human-required，人工批准是在位机制而非待增写的回退；R0-5(:167) 的替代明确以"埋错演练上岗"为生效条件，R3-4(:211) 明确"未命中则复核不上岗、A8 替代暂缓"——API 未接入⇒不上岗⇒替代暂缓⇒人工批准继续在位，finder 建议增写的降级路径正是计划书状态机的缺省态。(2)"隐性单点/死锁"…… |
| 全文零工期/工作量估计，14 天 demo 与试用窗口无法与里程碑对齐 | 该发现的三个支柱实测均不成立或已被计划书/授权记录覆盖。(1) "零工期估计是遗漏"——实测 UpgradePlan_升级方案.md §8 十九问答复第 2 题：用户明确答复"剔除，做好即用，与时间无关"，v3 落点为"不加时间型产品约束"；该答复表被 RebuildPlan §5-R0-5（:167）和 §9（:269）指定为初始授权锚点（审计链根）。计划书不写工期不是遗漏，而是执行一条已锚定的用户拍板——按该发现的建议加 T 恤码工…… |

---

## 5. 修订优先级清单

**P0——修订计划书本体，R0 开工前完成：**

1. **补"信任根再基线化 runbook"**（C1）：§3.4 增补——凡触碰 `CANDIDATE_TRUST_PATHS` 的批次，合入提交 D 前用 `--diagnostic-workspace` 取记录性验证，D 合入后在后继提交上以 `--base D` 取首个 clean 候选证据；§5-R3-1 CI 对信任根 PR 明确两段式取证；§6 并行判定表增"信任根类"一行。
2. **补"6 处执行胶水恢复"工作项**（C2）：§2 首段定性改为"三道锁 + 函数体摘除四层封存"；§5-R2-2⑥ 解锁 runbook 扩为"三锁翻转 + 6 处 stub 按 cac7ccb 恢复调和，同批具名审批更新字节基线"；工作量口径补"约 1.1k 行历史函数体恢复"。
3. **§3.4 补第 9 步**（I-resolver）：34 个 module.yaml 的 `agents.resolver` 与 `module-manifest.schema.json:325` 的 const 同批处理（改 schema + 23 个保留 manifest），否则删 11 后全仓悬空引用且 dry-run 不报错。
4. **修正里程碑引用账**（I-悬空引用 + I-anchor排程）：删除/替换幽灵"R5 Reddit 退役"与"R3 五文件批"，anchor 重签次数按真实批次重列（R0-2 删除批 / R2 flip 批含 VerifyError+stub 恢复+门控收敛）。
5. **改正 anchor 重签机制模型**（I-RSA-PSS）：legacy anchor 实为"自绑定 SHA-256 + 外部只读挂载"，无 RSA-PSS；runbook 按真实机制重写，独立性来源明确为用户/外部 OS 身份而非"异构会话扮演"。
6. **修正 §2 事实失真**（I-初衷#1）：初衷 #1 定性从 LOCKED 改为"已实现未接线（含全部 git 历史零调用）"，接线工作显式入 R2。

**P1——对应里程碑开工前解决（写入计划书相应章节）：**

- 拟人化最后一公里（R2-3/R2-4 前）：IG 操作面 humanized 标志补齐方案、SoulBehaviorPacket 落点统一到活跃路径画像表（处理第二张硬编码表）、误触参数迁入活跃引擎、宏观节律（活跃时段/会话节律/动作构成比）给落点或显式 DEFERRED 并改验收指标、logistic 底板与 ε 阈值参数约束。
- request_vision_verdict 三件硬设计（R1-6 前）：截图字节通道与鉴权、往返时延/超时语义、verdict 超时后恢复阶梯走向；同批解决 soul_id 不下边缘与层④注入的三方矛盾。
- 曲线滑动收敛点改为生产路径 `ActionExecutor.ApplyHumanizedSwipe`（R2-4a）。
- 单文件加 APP 触点清单修正：补 `Data/Keywords/{platform}/`，剔除零调用 Manifest 计数（R2-4c）。
- 99% 滚动窗口冷启动语义 + EMULATOR 验收样本量与 ≥300 窗口的匹配（R2 验收前）。
- landing 协议机器挡板：required checks / 合并 HEAD 重跑的 CI 工作项（R3-1/R3-2）。
- Kill switch"仅用户可解除"的机器强制方案（R0-5）。
- R1 验收"对照日志"条目移到 R2（生产者是 R2-5）或 R1 只验记录管道。
- 草稿生成组件归属与 persona"写作风格"消费路径（R1-4 前拍板）；"<2.5k 行新建"口径按 ~4-6k 修正。
- MV/IG 风险重定级：MV-3 网络 ADB 官方口径 caveat 恢复、IG-on-AVD 验收替代路径（如换测试 APP 保 EMULATOR_VERIFIED 流程验证）。

**建议补做**：完备性批判（本次因额度失败缺位）可在额度恢复后单独补跑一轮。
