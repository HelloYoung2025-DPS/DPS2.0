# 重构施工会话提示词库

> 文档状态: `Current`（随重构计划书 v4 使用；计划书升版时同步校订）  
> 用法: 两种模式二选一——(A) **半自动**：开一个驱动会话粘 **TA**，它逐批推进、只在高危/异常/物理步骤停下问你（少动手，推荐）；(B) **手动**：按顺序逐个粘 T1-T19（每批一会话，完全掌控）。两者都在 `DSP_ZD` 目录开 Claude Code 会话，整块粘贴不改字。  
> 本文件只是固定顺序的提示词序列，不记录进度；进度唯一事实源是 dps2 远程的已合入 PR。

每个模板自带防呆：会话开工前会先核对 dps2 已合入 PR——前序批次没合入会停下，本批次已合入会提示你换下一个模板。

两条硬规则（模板文本已内置，此处声明其地位）：

1. **审计模板（T4/T13/T19）是强制门，不是可选项**：后续施工模板会核验对应审计是否已完成且无未决 critical，缺审计记录会拒绝开工。
2. **信任根批次的正式取证在合入之后、由本批自己闭环**（计划书 §4.5 第 2 步）：触碰信任根的批次，会话在你确认合入后不结束——在合入提交的后继提交上取正式 clean 证据、贴回本批 PR，本批才算关闭（main 暂无后继提交时，先开一个空提交 PR 作证据锚）。兜底分工（取证与裁决分离，对应计划书 D-05）：漏了取证时，用补证模板 **T0** 单独补齐；下一个施工模板开工前也会检查并补齐；收口审计（T4/T13）**只核验不补证**，缺证即判 FAIL 并指回 T0。合入不等于取证完成。
3. **信任根批次严格串行取证（硬顺序，2026-07-17 外审 F1 采纳）**：上一个触碰信任根的批次未取得 §4.5 第 2 步正式 clean 证据前，**禁止合入下一个触碰信任根的批次**——一旦下一批合入，`CANDIDATE_TRUST_PATHS` 字节已变，旧批次的证据将**永久无法补取**（候选门禁要求当前信任根与 `--base D` 逐字节一致）。T0 只能补"信任根未再变动"窗口内的缺证；若发现某批证据已因后续信任根合入而不可补，属于重基线决策，停下转用户裁决，不得静默跳过。

---

## 通用模板

### TA · 半自动驾驶驱动会话（一次粘贴，自动推进多批，异常/高危/物理步骤才停下问你）

> 这是"少动手"的入口：不用逐个粘 T1-T19，改为开一个驱动会话粘下面这段，它按序逐批推进；只有需要你决策时才停。授权边界见 `Docs/Operations/ExternalReview_外审机制.md` §一之三。驱动会话与被驱动的施工是不同上下文——驱动者为每批派一个 fresh-context 子智能体施工，避免长会话串味。

```text
你是 DPS 2.0 重构的驱动会话，按 Docs/Operations/RebuildSessionPrompts_施工会话提示词.md 的 T1→T19 顺序半自动推进，遵守 Docs/Operations/ExternalReview_外审机制.md §一之三 的授权边界：

循环，从 dps2 已合入 PR 推导的下一个未合入批次开始：
1. 为该批次派一个 fresh-context 子智能体，喂给它对应 T# 模板全文，让它施工到"开 PR 到 dps2"为止（含跑 required 门禁）。
2. 批次收尾 Codex 外审：常规批次前把 ~/.codex/config.toml 的 model_reasoning_effort 设为 high、高危批次（信任根 R0-B/C/D、动 Legacy 字节的 M3-2/M3-3）设为 xhigh，审后复位；绑定该批 commit/diff。
3. 判据：门禁全绿 且 Codex PASS →
   - 常规批次：用 gh pr merge 自动合入，继续下一批。
   - 高危批次 / 里程碑收口批次：停下，把外审结论摆给我，等我点头再合入。
4. 任一 FAIL/UNAVAILABLE、两段式取证缺口、越出批次范围、或需要 sudo anchor 重签 / Parallels 真机环境 / 平台授权 → 停下，明确告诉我卡在哪、需要我做什么。
5. 信任根批次合入后按 §4.5 第 2 步取正式 clean 证据贴回 PR，再进下一批。
6. 里程碑边界（M1 后、M2M3 后、终局）分别跑 T4/T13/T19 审计；审计发现未决 critical 即停下等我。

每批开工前先播报：批次名、依据、本批是否高危、将自动合入还是等我。不要一次跑完就沉默——每完成一批或每次停下都向我汇报。
```

---

### T0 · §4.5 第 2 步补证会话（单一职责，可随时插入，可重复使用）

```text
按 Docs/RebuildPlan_重构计划书.md §4.5 第 2 步，为已合入但缺正式 clean 证据的信任根批次补取证：先核对 dps2 已合入 PR，列出所有缺第 2 步证据的信任根批次；对每一个，在其合入提交 D 的后继提交上以 --base D 跑候选门禁取正式 clean 证据（main 暂无后继提交时，开一个空提交 PR 作证据锚，停下等我合入后再取证），结果贴回该批次的 PR。若某批的证据已因后续信任根批次合入、信任根字节变动而无法补取（门禁对 --base D 逐字节校验必红），不要硬跑——停下报告哪一批、被哪次合入挡死，转我做重基线裁决。本会话不改任何产品代码、不做任何批次施工；全部补齐或确认无缺失后报告并结束。
```

---

## 第一段：R0 治理根（严格串行，§16 钉死顺序）

### T1 · R0-B 指令 receipt 迁移

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：R0-B 指令 receipt 迁移（§4.2）。
开工前先核对 dps2 远程已合入 PR：前序批次（M0/R0-A）未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次，退出条件以 §10 M1A 行为准。上一批证据以 dps2 最新已合入 PR 及其门禁/外审记录为准，自行获取；若上一合入批次触碰候选门禁信任根且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
注意：本批触碰候选门禁信任根（Manifest schema、Tools/ci），按 §4.5 两段式取证；改动 legacy-runtime-adapter 的 module.yaml 触发 §11 的 anchor 同批重签——DPS_LEGACY_BASELINE_ANCHOR 未签发就停下告诉我。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T2 · R0-C Release BOM 权威迁移

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：R0-C Release BOM 权威迁移（§4.3）。
开工前先核对 dps2 远程已合入 PR：前序批次（R0-B）未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次，退出条件以 §10 M1B 行为准。上一批证据以 dps2 最新已合入 PR 及其门禁/外审记录为准，自行获取；若上一合入批次触碰候选门禁信任根且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
注意：本批触碰候选门禁信任根（Tools/ci），按 §4.5 两段式取证；Release BOM 由仓外 signer 签发，模型与候选代码不得持签名私钥。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T3 · R0-D 删除 11 个 factory 模块

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：R0-D 删除 11 个 factory 模块（§4.4）。
开工前先核对 dps2 远程已合入 PR：前序批次（R0-B、R0-C）未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：只删 §4.4 列出的 11 个目录及其专属引用（catalog、Manifest、schema、DAG、compatibility、候选测试 policy、CODEOWNERS、CI、README、operations），不得删除已迁出的 receipt/BOM/回滚能力。退出条件以 §10 M1C 行为准（含 module-impact suite 与 merge queue 验证）。上一批证据以 dps2 最新已合入 PR 及其门禁/外审记录为准，自行获取；若上一合入批次触碰候选门禁信任根且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
注意：本批删除多个信任根文件，按 §4.5 两段式取证。这是高危批次，批次收尾外审用 adversarial-review。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T4 · M1 里程碑收口审计（只审不改）

```text
用 workflow 对里程碑 M1（M1A/M1B/M1C 三批）做退出评审：交叉核验全部已合入批次与 Docs/RebuildPlan_重构计划书.md §4/§10 的一致性，逐条核验退出条件与 §4.5 两段式证据是否齐全，产出对照裁决表。只审不改、不补证：任何已合入信任根批次缺 §4.5 第 2 步正式 clean 证据，即在裁决表记 FAIL 并停下，让我先用 T0 补证后重跑本审计。发现未决 critical 就停下告诉我，不得进入 M2/M3。
```

---

## 第二段：M2 Soul 轨 ∥ M3 执行轨（M1 收口后可两轨并行开两个会话；两轨各自内部按序）

并行规则（§3.3）：两轨只碰各自模块，公共合同 landing 与合入由 merge queue 串行。

### T5 · M2-1 Persona 投影链

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-1 Persona 投影链（§5.1）。
开工前先核对 dps2 已合入 PR：M1C 未合入、或 M1 收口审计（T4）未完成或有未决 critical，则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次（Persona v1 保持、persona outbox -> GBrain projector -> SoulMemory adapter -> exact readback 链及其独立测试），不触碰 M3 轨模块。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T6 · M2-2 长期记忆合同 memory.event/v3

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-2 长期记忆合同（§5.2）。
开工前先核对 dps2 已合入 PR：M2-1 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：memory.event/v3 与 gbrain.projection/v3 以 additive major 并存落地，消费者按实际登记的 v1 起步登记 v1/v3 双读（不虚构 v2 消费历史），认知衰减与纠正/删除两条链分开。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（memory-lifecycle suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T7 · M2-3 兴趣算法 interest v2

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-3 兴趣算法 v2（§5.3）。
开工前先核对 dps2 已合入 PR：M2-2 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：interest.snapshot/v2 新 major、写码前冻结 weight/a/b/half_life 与 golden vectors、seed 归 interest-reducer。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（interest-v2 suite 全 golden vectors）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T8 · M2-4 planner 行为分布与参数采样

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-4 planner 行为分布与 session nonce 参数采样（§5.4）。
开工前先核对 dps2 已合入 PR：M2-3 未合入则停下告诉我；本批信封绑定 operation.compiled/v2，其 provider 由 M3-2 引入——M3-2 未合入也停下告诉我（两轨并行时本批必须等 M3-2 合同落地，不得在 M2 轨自造 provider）；本批次已合入则告诉我该用哪个模板。范围只做本批次：planner 按 Soul 生成行为分布（含跨会话宏观节律与动作构成比）、独立 nonce 采样、operation.compiled/v2 信封绑定三类 revision；宏观节律样本不足时按 §5.4 显式标注 DEFERRED。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（behavior-params 与 soul-isolation suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T9 · M3-1 APP 包与自动探索安全流

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-1 app.package/v1 与探索安全流（§6.1/§6.2）。
开工前先核对 dps2 已合入 PR：M1C 未合入、或 M1 收口审计（T4）未完成或有未决 critical，则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：operation-compiler 拥有 app.package/v1 与 canonical 包、app_onboarder 只产隔离候选、至少两个非 IG fixture，不触碰 M2 轨模块。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（app-package suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T10 · M3-2 operation.compiled/v2 与唯一 ActionExecutor

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-2 operation.compiled/v2 与 ActionExecutor 失败关闭改造（§6.3）。
开工前先核对 dps2 已合入 PR：M3-1 未合入则停下告诉我；WP 探针（T15）未完成也停下告诉我——本批改写受保护 Legacy `ActionExecutor.cs`，按计划书 §11 探针前禁止改写受保护 Legacy C#，CodeDom/Input API 兼容性未知时不得合入该改造；本批次已合入则告诉我该用哪个模板。范围只做本批次：v1/v2 additive 提供、只有 command-orchestrator 登记直接消费、ActionExecutor 空/未知/异常/partial 全失败关闭并逐 step 验证后置条件、信封 delay/typing/trajectory 参数在携带对应参数的 step 上消费生效且越界即拒绝。
注意：本批触碰 79 文件 Legacy 字节基线（ActionExecutor.cs），按 §11 同批重签 anchor——anchor 不可用就停下告诉我。高危批次，外审用 adversarial-review。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（execution-path suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T11 · M3-3 edge.bridge.exchange/v2 与薄 C#5 入口

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-3 brain-to-hand 交接合同与独立 Legacy 入口（§3.4）。
开工前先核对 dps2 已合入 PR：M3-2 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：edge.bridge.exchange/v2、与 SessionRunner.Run 完全分离的薄 C#5 入口（默认落在 Legacy 字节基线作用域之外，如 legacy-runtime-adapter 模块内）、首个 handoff allowlist 只含确定性 primitives、合同测试证明旧入口不可达。不翻转 legacy 三道锁。
注意：高危批次，外审用 adversarial-review；若探针证明入口必须落入受保护作用域，停下告诉我走 anchor 重签决策。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T12 · M3-4 按需视觉提案链

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-4 按需视觉纠错（§7）。
开工前先核对 dps2 已合入 PR：M3-3 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：确定性失败后的诊断/提案链、脱敏截图 capability（绑定作用域/hash/TTL/删除策略）、IModelBroker 窄端口；模型不能直驱设备、不能改配置、不能宣告成功。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（visual-security suite 含 prompt injection 负例）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T13 · M2/M3 里程碑收口审计（只审不改）

```text
用 workflow 对里程碑 M2 与 M3 做退出评审：交叉核验两轨全部已合入批次与 Docs/RebuildPlan_重构计划书.md §3.4/§5/§6/§7/§10 的一致性，逐条核验 M2、M3 退出条件（含双 Soul 正反例、信封参数逐 step 生效、旧入口不可达、两种非 IG fixture），产出对照裁决表。只审不改、不补证：任何已合入信任根批次缺 §4.5 第 2 步正式 clean 证据，即在裁决表记 FAIL 并停下，让我先用 T0 补证后重跑本审计。发现未决 critical 就停下告诉我，不得进入 M4。
```

---

## 第三段：汇合与验证（串行）

### T14 · M4 组合与滚动门

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M4 组合与滚动门（§8/§9）。
开工前先核对 dps2 已合入 PR：M2、M3 未全部合入、或 M2/M3 收口审计（T13）未完成或有未决 critical，则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：composition root、attempt/receipt/eligibility 合同、session+command 双 300 滚动门、runtime kill switch 与工程 freeze、通知 outbox。退出条件以 §10 M4 行为准（含 kill-notify suite 的未授权清除负例）。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 required 门禁（reliability 与 kill-notify suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T15 · WP Windows/Zenno 探针（M0 后任意时点可插入，只探测不改产品码）

```text
按 Docs/RebuildPlan_重构计划书.md 执行 WP 探针（§11）：在 Parallels Windows + 目标 ZennoDroid 上记录 §11 探针清单全部项（版本/CodeDom/OwnCode 清单/加载入口/ADB 授权/adb 37.0.0-14910828 与 pwsh 7.6.2 精确断言/Input API 签名/端口超时/编码 hash/Enterprise 对 AVD 的接受性/AVD snapshot 身份连续性），产出原始探针制品。只探测不改产品代码；Enterprise 不接受 AVD 就停下告诉我走回退决策（Parallels USB 直通真机）。环境未就绪（Parallels/ZennoDroid 安装介质）就停下告诉我需要准备什么。
```

### T16 · M5 模拟闭环

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M5 macOS AVD + Parallels + ZennoDroid 模拟闭环（§10 M5 行）。
开工前先核对 dps2 已合入 PR：M4 未合入或 WP 探针未完成则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：搭通模拟闭环并产出原始证据，全部标记 SIMULATION，不提升 Windows/DEVICE 等级。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
完成后：跑 simulation suite、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T17 · M6 授权真机与受限 canary（human-required）

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M6 目标 Windows、授权设备、受限 canary（§10 M6 行）。
开工前先核对：M5 未合入则停下告诉我；§15 条目 4/6 的平台授权与具名批准我是否已给你——没有就停下列出缺哪项，不得触碰任何真实平台写操作。证据等级仅由对应可执行门逐级签发。
完成后：按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T18 · LC Legacy 物理删除（可重复使用，每次删一个对象）

```text
按 Docs/RebuildPlan_重构计划书.md 执行一个 Legacy 物理删除批次（§11）：从探针后确认可安全删除的对象中取一个（零入口证明 + Windows 编译/加载原始结果齐备的优先），单独成批：删除 + 验证器清单绑定 + 字节基线制品 + anchor 重签归同一批，附 rollback。WP 探针未完成或 anchor 不可用就停下告诉我。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审（adversarial-review）、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```

### T19 · 终局 DoD 审计（只审不改）

```text
用 workflow 对 Docs/RebuildPlan_重构计划书.md §14 Definition of Done 做终局评审：逐条核验十项完成事实，核对当前声明的证据等级与实际取得的可执行门证据一致，产出对照裁决表与残留缺口清单。只审不改。
```

---

## 万能兜底模板（不确定该用哪个时）

```text
按 Docs/RebuildPlan_重构计划书.md 与 dps2 已合入 PR 推导下一个未合入批次并施工它；范围只做该批次。开工前三项前置缺一即停：①上一合入批次若触碰候选门禁信任根且缺合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证；②跨里程碑边界时确认对应收口审计（M1 用 T4、M2/M3 用 T13）已完成且无未决 critical；③把你推导出的批次名和依据告诉我。完成后跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```
