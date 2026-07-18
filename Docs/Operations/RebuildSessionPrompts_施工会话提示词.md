# 重构施工会话提示词库

> 文档状态: `Current`（随重构计划书 v4 使用；计划书升版时同步校订）  
> 用法: 每批一个会话，完全掌控——按下方「执行顺序（依赖 DAG）」挑对应 T# 模板，在 `DSP_ZD` 目录开 Claude Code 会话整块粘贴、不改字。**T 编号只是模板索引，不是执行顺序**（计划书 §16 只钉 R0 段 PR1–4 顺序；完整 T 级 DAG 以下方那节为准）；每个模板自带开工前置门，前置未满足会让你停下（例如 T10 会要求先完成 T15 探针、T8 会要求先合入 T10）。
> 本文件是零填空模板库（执行顺序见下方依赖 DAG，不是 T 编号字面顺序），不记录进度；进度唯一事实源是 dps2 远程的已合入 PR。

每个模板自带防呆：会话开工前会先核对 dps2 已合入 PR——前序批次没合入会停下，本批次已合入会提示你换下一个模板。

三条硬规则（模板文本已内置，此处声明其地位）：

1. **审计模板（T4/T13/T19）是强制门，当前是程序性门、非机器防篡改门**：外审裁决写入对应审计 PR 描述以遵 `AGENTS.md:90`（审计不改产品码，用空提交/最小 marker PR 承载，裁决落 PR body、不写受跟踪文件）。该记录**仅是可见的程序性审计记录，不是不可篡改证据、也不是机器放行信号**——PR 描述可被事后编辑，故解除冻结与下一批开工放行**以用户人工把关为准**：后续施工模板"核验对应审计已完成且无未决 critical"是提示用户据该已合入审计 PR 复核，不是机器可信判定。**审计输出契约（裁决字段）**：T4/T13/T19 必须在审计 PR 描述开头写两行——`总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目清单或 无>`。**写 `PASS` 的充要条件**：全部退出条件逐条核验通过（**按 §5.4 等计划书条款显式且标注正确的 DEFERRED 项视为该条通过**，其条目同时列入 `未决 DEFERRED` 行）、§4.5 两段式证据齐全、无未决 critical；**未标注/标注错误的缺失、§4.5 证据不全、或无法核验，一律写 `FAIL`/`UNAVAILABLE`、不得写 `PASS`**。裁决表每条判"通过"的退出条件须注明其证据锚点（合入提交 D + 以 --base D 的门禁输出 PR/链接，或对应可执行门证据 ref）；被视为通过的 DEFERRED 项归入 `未决 DEFERRED` 行、不在此列、不要求现给锚点。下游"可核验的 PASS"即指：`总裁决` 存在且为 `PASS`、且用户人工确认（a）PR body 记录一致、未被事后编辑成与裁决表矛盾，（b）核验裁决表引用的证据锚点真实存在可追溯——其中 §4.5 第 2 步 clean 证据类锚点（每个触碰信任根的已合入批次一个，属 hard-rule #3 不可跳过的有界集合）须**逐条**确认真实含 --base D 门禁输出、非空壳锚，缺一即非可核验 PASS；其余普通退出条件锚点可抽验。`PASS` 可与非空 `未决 DEFERRED` 清单并存（合法 DEFERRED 不判为记录矛盾），但清单内每个 DEFERRED 项须在其阻塞里程碑取得样本后由后续审计一次性回补核验，终局 T19 对任何仍未回补的 DEFERRED 记 `FAIL`。**fail-closed 语义（保留）**：`总裁决` 为 `FAIL`/`UNAVAILABLE`、缺该裁决字段、证据锚点缺失/不可追溯、或记录不一致/无法核验时，一律冻结、拒绝开工（由用户人工执行；下游 T5/T9/T14 前置门同此，不因未标 critical 而放行）。将裁决绑定到不可篡改外部 check/签名证据/merge 制品的机器级审计门，明确延期至 **R0 后的 §9.1 专门批次**；本文不宣称当前已具备防篡改审计保证。
2. **信任根批次的正式取证在合入之后、由本批自己闭环**（计划书 §4.5 第 2 步）：触碰信任根的批次，会话在你确认合入后不结束——在合入提交的后继提交上取正式 clean 证据、贴回本批 PR，本批才算关闭（main 暂无后继提交时，先开一个空提交 PR 作证据锚）。兜底分工（取证与裁决分离，对应计划书 D-05）：漏了取证时，用补证模板 **T0** 单独补齐；下一个施工模板开工前也会检查并补齐；收口审计（T4/T13）**只核验不补证**，缺证即判 FAIL 并指回 T0。合入不等于取证完成。
3. **信任根批次严格串行取证（硬顺序，2026-07-17 外审 F1 采纳）**：上一个触碰信任根的批次未取得 §4.5 第 2 步正式 clean 证据前，**禁止合入下一个触碰信任根的批次**——一旦下一批合入，`CANDIDATE_TRUST_PATHS` 字节已变，旧批次的证据将**永久无法补取**（候选门禁要求当前信任根与 `--base D` 逐字节一致）。T0 只能补"信任根未再变动"窗口内的缺证；若发现某批证据已因后续信任根合入而不可补，属于重基线决策，停下转用户裁决，不得静默跳过。

---

## 执行顺序（依赖 DAG，手动挑批次照此；T 编号只是模板索引，不是执行顺序）

- **R0 段严格串行**：R0-B(T1) → R0-C(T2) → R0-D(T3) → M1 收口审计(T4)。
- **M1C 合入且 T4 无未决 critical 后**，两轨并行、各轨内部按序：M2 轨 T5→T6→T7→T8；M3 轨 T9→T10→T11→T12。（⚠️ 并行轨的安全并行合并编排属 R0 后 §9.1 专门批次、尚未建成；到达本段时若该批次未就绪，退化为串行推进——见外审机制 §三-5。）
- **跨轨/探针前置**（前置未满足不得开工，模板自带前置门会自行停下）：T15(WP 探针) 先于 T10(M3-2)；T10(M3-2) 先于 T8(M2-4)。故 M3 轨就绪序为 T9→**T15**→T10→T11→T12，且 **T8 必须等 T10 合入后才可开**。
- **汇合段串行**：T13(M2/M3 收口审计) → T14(M4) → T16(M5) → T17(M6)。
- **可插入 / 收尾**：T15(WP 探针) M0 后任意时点可跑；T18(LC Legacy 物删) WP 完成后可重复插入、每次单独批准合入；T19 终局 DoD 审计最后。T0(§4.5 补证) 按需随时插入。

> 每个 T# 模板内已内置自己的前置门（会先核对 dps2 已合入 PR），照此 DAG 挑批次即可；两轨并行时各开一个会话。

---

## 通用模板

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
用 workflow 对里程碑 M1（M1A/M1B/M1C 三批）做退出评审：交叉核验全部已合入批次与 Docs/RebuildPlan_重构计划书.md §4/§10 的一致性，逐条核验退出条件与 §4.5 两段式证据是否齐全，产出对照裁决表。只审不改、不补证：任何已合入信任根批次缺 §4.5 第 2 步正式 clean 证据，即在裁决表记 FAIL 并停下，让我先用 T0 补证后重跑本审计。把对照裁决表（每条判"通过"的退出条件须注明其 §4.5 证据锚点/门证据 ref）写进审计 PR 的描述，并在描述开头写两行 `总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目/无>`——仅当 M1 全部退出条件逐条核验通过（按计划书条款显式且正确标注的 DEFERRED 项视为该条通过、并列入未决 DEFERRED 行）、§4.5 两段式证据齐全、无未决 critical 时才写 `PASS`；未标注/错标的缺失、§4.5 证据不全、或无法核验一律写 `FAIL` 或 `UNAVAILABLE`（空提交/最小 marker PR，两行裁决与裁决表落 PR body、不写受跟踪文件，遵 AGENTS.md 门禁状态不入库），开到 dps2 等我合入；后续 T5/T9 据该已合入 PR 描述开头的 `总裁决` 字段核验（为 `PASS`、记录一致可核验、且证据锚点真实存在可追溯——§4.5 第 2 步 clean 证据类锚点逐条确认真实含 --base D 门禁输出、非空壳，其余可抽验——才放行，非 `PASS` 一律停）。发现未决 critical 就停下告诉我，不得进入 M2/M3。
```

---

## 第二段：M2 Soul 轨 ∥ M3 执行轨（M1 收口后可两轨并行开两个会话；两轨各自内部按序）

并行规则（§3.3）：两轨只碰各自模块，公共合同 landing 与合入由 merge queue 串行。

### T5 · M2-1 Persona 投影链

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-1 Persona 投影链（§5.1）。
开工前先核对 dps2 已合入 PR：M1C 未合入、或 M1 收口审计（T4）的裁决不是可核验的 PASS（未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical，均属非 PASS，一律停），则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次（Persona v1 保持、persona outbox -> GBrain projector -> SoulMemory adapter -> exact readback 链及其独立测试），不触碰 M3 轨模块。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
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
开工前先核对 dps2 已合入 PR：M1C 未合入、或 M1 收口审计（T4）的裁决不是可核验的 PASS（未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical，均属非 PASS，一律停），则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：operation-compiler 拥有 app.package/v1 与 canonical 包、app_onboarder 只产隔离候选、至少两个非 IG fixture，不触碰 M2 轨模块。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
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
用 workflow 对里程碑 M2 与 M3 做退出评审：交叉核验两轨全部已合入批次与 Docs/RebuildPlan_重构计划书.md §3.4/§5/§6/§7/§10 的一致性，逐条核验 M2、M3 退出条件（含双 Soul 正反例、信封参数逐 step 生效、旧入口不可达、两种非 IG fixture），产出对照裁决表。只审不改、不补证：任何已合入信任根批次缺 §4.5 第 2 步正式 clean 证据，即在裁决表记 FAIL 并停下，让我先用 T0 补证后重跑本审计。把对照裁决表（每条判"通过"的退出条件须注明其 §4.5 证据锚点/门证据 ref）写进审计 PR 的描述，并在描述开头写两行 `总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目/无>`——仅当 M2/M3 全部退出条件逐条核验通过（按 §5.4 等计划书条款显式且正确标注的 DEFERRED 项视为该条通过、并列入未决 DEFERRED 行）、§4.5 两段式证据齐全、无未决 critical 时才写 `PASS`；未标注/错标的缺失、§4.5 证据不全、或无法核验一律写 `FAIL` 或 `UNAVAILABLE`（空提交/最小 marker PR，两行裁决与裁决表落 PR body、不写受跟踪文件，遵 AGENTS.md 门禁状态不入库），开到 dps2 等我合入；后续 T14 据该已合入 PR 描述开头的 `总裁决` 字段核验（为 `PASS`、记录一致可核验、且证据锚点真实存在可追溯——§4.5 第 2 步 clean 证据类锚点逐条确认真实含 --base D 门禁输出、非空壳，其余可抽验——才放行，非 `PASS` 一律停）。发现未决 critical 就停下告诉我，不得进入 M4。
```

---

## 第三段：汇合与验证（串行）

### T14 · M4 组合与滚动门

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M4 组合与滚动门（§8/§9）。
开工前先核对 dps2 已合入 PR：M2、M3 未全部合入、或 M2/M3 收口审计（T13）的裁决不是可核验的 PASS（未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical，均属非 PASS，一律停），则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：composition root、attempt/receipt/eligibility 合同、session+command 双 300 滚动门、runtime kill switch 与工程 freeze、通知 outbox。退出条件以 §10 M4 行为准（含 kill-notify suite 的未授权清除负例）。上一批证据以 dps2 最新已合入 PR 为准，自行获取；若上一合入批次触碰候选门禁信任根（含 catalog/DAG/compatibility 等合同登记文件）且尚无合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证，再开工本批。
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
用 workflow 对 Docs/RebuildPlan_重构计划书.md §14 Definition of Done 做终局评审：逐条核验十项完成事实，核对当前声明的证据等级与实际取得的可执行门证据一致，并逐条核对历史各里程碑遗留的 `未决 DEFERRED` 项是否均已在其阻塞里程碑取得样本后回补核验通过，产出对照裁决表（每条判"通过/一致"的完成事实须注明其可执行门证据 ref）与残留缺口清单。只审不改。把对照裁决表与残留缺口清单写进审计 PR 的描述，并在描述开头写两行 `总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目清单或 无>`——仅当 §14 十项完成事实与证据等级逐条核验一致、无残留缺口、且所有历史 `未决 DEFERRED` 项均已回补核验通过（即 `未决 DEFERRED: 无`）时才写 `PASS`，任一未回补/证据等级不符/有残留缺口一律写 `FAIL` 或 `UNAVAILABLE`（空提交/最小 marker PR，两行裁决、裁决表与缺口清单落 PR body、不写受跟踪文件，遵 AGENTS.md 门禁状态不入库），开到 dps2 等我合入。
```

---

## 万能兜底模板（不确定该用哪个时）

```text
按 Docs/RebuildPlan_重构计划书.md 与 dps2 已合入 PR 推导下一个未合入批次并施工它；范围只做该批次。开工前三项前置缺一即停：①上一合入批次若触碰候选门禁信任根且缺合入后正式 clean 证据，先在当前 HEAD 以 --base <其合入提交> 按 §4.5 第 2 步补跑门禁取证；②跨里程碑边界时确认对应收口审计（M1 用 T4、M2/M3 用 T13）的裁决是可核验的 PASS——未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical 均属非 PASS，任一即停；③把你推导出的批次名和依据告诉我。完成后跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准。若本批触碰候选门禁信任根：我确认合入后不结束会话，按 §4.5 第 2 步在合入提交的后继提交上取正式 clean 证据（main 暂无后继提交时，先开一个空提交 PR 作证据锚、经我合入后再取证），结果贴回本批 PR，完成后本批才算关闭。
```
