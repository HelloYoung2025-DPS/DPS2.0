# 重构施工会话提示词库

> 文档状态: `Current`（随重构计划书 v4 使用；计划书升版时同步校订）  
> 用法: **F3 档起**每批一个会话（当前档位判定与 F0–F2 的用法见硬规则 3 与 `TR` 模板），完全掌控——按下方「执行顺序（依赖 DAG）」挑对应 T# 模板，在 `DSP_ZD` 目录开 Claude Code 会话整块粘贴、不改字。**T 编号只是模板索引，不是执行顺序**（计划书 §16 只钉 R0 段 PR1–4 顺序；完整 T 级 DAG 以下方那节为准）；每个模板自带开工前置门，前置未满足会让你停下（例如 T10 会要求先完成 T15 探针、T8 会要求先合入 T10）。
> 本文件是零填空模板库（执行顺序见下方依赖 DAG，不是 T 编号字面顺序），**不记录进度，也不在仓内维护任何进度账本**（`CLAUDE.md` 硬规则、计划书 §9.2 与 `AGENTS.md:90` 同此）。dps2 远程的已合入 PR 只记录「某个批次是否完成」，**不等同于项目进度**：批次可以全部合入而 `Docs/Architecture/TargetArchitecture_目标架构.md` §必须通过的验收 那 10 条一条未动，治理/文档/门禁类批次天然如此。**要判断项目进度，就对着那 10 条、从当前 head 的代码与门禁输出现场重算；不得引用任何仓内快照，本文件也不提供这样的快照。**

每个**批次模板（T1–T19）**自带防呆：会话开工前会先核对 dps2 已合入 PR——前序批次没合入会停下，本批次已合入会提示你换下一个模板。该核对判定的是**批次完成度与依赖顺序**，不是项目进度；进度按上文口径从当前 head 现场重算。（F0–F2 档的 `TR` 模板不做该核对，因为它的工作单位不是批次。）

三条硬规则（第 1–2 条模板文本已内置，此处声明其地位；第 3 条是 F 级严格度分级与红线，管前两条的**执行强度**、不改其定义；2026-07-25 修订：原第 2 条随计划书 §4.5 第 2 步废止而删除，原第 3 条改写为轻量版顺移为第 2 条，废止理由见计划书 §4.5 废止记录）。**本次 2026-07-25 修订整体以计划书 §4.5「生效条件」成立（§4.6 (a) 已取得平台可见的 Owner 批准并签署）为前提**：(a) 未签署期间，下列三条按修订前条文执行——原第 2 条「信任根批次两段式取证硬顺序」继续有效，本文件中一切「合入前单段式取证」「无后续取证动作」的表述一律不生效（fail-closed 回落，回落条文逐字见计划书 §4.5「生效条件」）：

1. **审计模板（T4/T13/T19）是强制门，当前是程序性门、非机器防篡改门**：外审裁决写入 dps2 仓库的**审计 issue** 以遵 `AGENTS.md:90`（审计不改产品码、不写受跟踪文件，也不开零 diff / marker PR——裁决落 issue 正文）。该记录**仅是可见的程序性审计记录，不是不可篡改证据、也不是机器放行信号**——issue 内容可被事后编辑，故解除冻结与下一批开工放行**以用户人工把关为准**：后续施工模板"核验对应审计已完成且无未决 critical"是提示用户据该审计 issue 复核，不是机器可信判定。**审计输出契约（裁决字段）**：T4/T13/T19 必须在审计 issue 正文开头写两行——`总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目清单或 无>`。**写 `PASS` 的充要条件**：全部退出条件逐条核验通过（**按 §5.4 等计划书条款显式且标注正确的 DEFERRED 项视为该条通过**，其条目同时列入 `未决 DEFERRED` 行）、§4.5 合入前取证证据齐全、无未决 critical；**任何 Owner 签署都不改变本充要条件——`RISK_ACCEPTED`（计划书 §4.6 (e)）不折算为「证据齐全」、不计为退出条件通过、不作为证据引用**；**未标注/标注错误的缺失、§4.5 合入前证据不全、或无法核验，一律写 `FAIL`/`UNAVAILABLE`、不得写 `PASS`**。裁决表每条判"通过"的退出条件须注明其证据引用（批次 PR 及其合入前 required 静态门 CI run/artifact 引用，或对应可执行门证据 ref）；被视为通过的 DEFERRED 项归入 `未决 DEFERRED` 行、不在此列、不要求现给证据引用。历史批次（至 PR #16）的合入程序缺陷定性以 PR-A 历史收口文书（`Docs/Operations/HistoricalClosure_历史收口_2026-07-26.md`；该文书的合入以 Owner 在 PR-A 上给出平台可见、绑定精确 head 的批准为前提，核验时确认该批准存在）为准，不重复裁决。**其取证缺口一律据实裁决、不因任何签署而改记**：裁决表对相应条目记 `UNAVAILABLE`（历史 `overall_status == FAIL` 的运行据实记 `FAIL`）。计划书 §4.6 (e) 签「风险接受」时，仅在该条目上附加 `RISK_ACCEPTED` 标注（含义：Owner 知悉并接受该缺口、不再要求补证）——该标注**不满足 `PASS` 的充要条件**，不得计为退出条件通过、不得作为证据引用、不得使 `总裁决` 变为 `PASS`，此时「§4.5 合入前取证证据齐全」一项计为不满足。(e) 签「等价重验」时，重验完成并留存可核验 CI 记录后，条目仍记 `UNAVAILABLE`，可附 `REVALIDATED@<CI run ref>` 作为补充记录——该记录**不折算为**「§4.5 合入前取证证据齐全」，重验前后该项对 §4.6 (e) 所指的历史批次**一律计为不满足**，不得计为退出条件通过、不得作为证据引用、不得使 `总裁决` 变为 `PASS`。解除冻结与开工放行仍按本条 fail-closed 由用户人工把关，模型不得自行判定解冻已发生。下游"可核验的 PASS"即指：`总裁决` 存在且为 `PASS`、且用户人工确认（a）审计 issue 记录一致、未被事后编辑成与裁决表矛盾，（b）核验裁决表引用的证据真实存在可追溯——其中触碰信任根批次的合入前 required 静态门证据须**逐条**确认真实含门禁输出、非空壳，缺一即非可核验 PASS；其余普通退出条件证据引用可抽验。`PASS` 可与非空 `未决 DEFERRED` 清单并存（合法 DEFERRED 不判为记录矛盾），但清单内每个 DEFERRED 项须在其阻塞里程碑取得样本后由后续审计一次性回补核验，终局 T19 对任何仍未回补的 DEFERRED 记 `FAIL`。**fail-closed 语义（保留）**：`总裁决` 为 `FAIL`/`UNAVAILABLE`、缺该裁决字段、证据引用缺失/不可追溯、或记录不一致/无法核验时，一律冻结、拒绝开工（由用户人工执行；下游 T5/T9/T14 前置门同此，不因未标 critical 而放行）。将裁决绑定到不可篡改外部 check/签名证据/merge 制品的机器级审计门，明确延期至 **R0 后的 §9.1 专门批次**；本文不宣称当前已具备防篡改审计保证。**适用强度（按第 3 条分级）**：本条中「每批开工前核验对应收口审计裁决」这一**逐批前置仪式**自 F3 档起生效；F0–F2 档不作为逐批前置。T4/T13/T19 本身不被删除也不被弱化——M1 收口审计取得可核验 `PASS` 是 F0–F2 → F3–F5 的升档触发条件之一，届时必须补做。**裁决字段、`PASS` 的充要条件、证据引用要求与 fail-closed 语义在任何档位一字不变。**
2. **信任根批次单批独审、串行合入（轻量版）**：触碰 `CANDIDATE_TRUST_PATHS` 的改动必须单独成批、独立外审、串行合入，不与其他批次或无关修复混批；**在计划书 §4.5「生效条件」成立（§4.6 (a) 已签署）时**，其 required 静态门证据按 §4.5 于合入前在批次 PR head 取得（CI artifact 自动留档），本条不附带任何额外取证动作；**(a) 未签署时本轻量版不生效，fail-closed 回落原两段式取证**（合入前 `--diagnostic-workspace` 记录性验证 + 合入后在后继提交上以 `--base D` 取首个 clean 候选证据）。该 required 静态门由候选自身携带的校验器执行，其自签发残余见计划书 §4.5「残余披露」——审计与外审不得把该 PASS 当作独立于候选的验证。（沿革：2026-07-17 外审 F1 曾采纳「严格串行取证硬顺序」版本，其取证部分随计划书 §4.5 第 2 步于 2026-07-25 废止；单批独审与串行合入要求保留如上。）
3. **严格度按 F 级分级（F0–F9 路线见 `Docs/Architecture/TargetArchitecture_目标架构.md` §F0-F9 迁移路线、项目技术书 §13）——分级只改「流程仪式的强度」，不改任何「什么算证据、什么算通过」的定义**。

   **当前档位判定（事实，非估计）**：零部署、零真实设备；23 个 `Modules/*/module.yaml` 全部 `releaseEligible=false`；`AGENTS.md:106` 明写当前治理基线下最高只能声称 `REPOSITORY_STATIC_VERIFIED`；`Docs/RebuildPlan_重构计划书.md:6` 记 `当前正式证据等级: NONE`；main tip 的 Phase 0 required 为 overall `FAIL`。**故当前处于 F0–F2 档。**

   | 档位 | 工作单位 | 门禁 | 外审 | 收口审计 |
   |---|---|---|---|---|
   | **F0–F2** | **一处修复 = 一个 commit**，不必凑成批次、不必一批一会话 | lint 与 Phase 0 静态门。required 语义不变：只有 `PASS` 放行 | 会话收尾自动 review gate（外审机制 §二 第 1 行，每次 stop 强制），**并加合入前绑定精确 head/base 的 Codex 一票**（外审机制 §二「批次收尾」行与 §三-5；信任根/anchor/执行链改动用 `adversarial-review`；`FAIL`/`UNAVAILABLE` 即冻结不合入），**外加所有者平台可见、绑定该精确 head 的批准**——每个进入 main 的提交都须被这一票与该批准覆盖，任何档位不降 | 不作为逐批前置（见硬规则 1 的适用强度段） |
   | **F3–F5** | **批次**（一个 PR = 一个批次，按 DAG 挑 T# 模板） | required 全套 + 该批模块 suite | 加批次收尾 Codex 一票（绑定 commit/diff；信任根/anchor/执行链等高危批次用 `adversarial-review`） | 里程碑收口审计 T4/T13 按 DAG 执行 |
   | **F6–F9** | 批次 + §15 条目 4/6 平台授权与具名批准前置 | required + 对应真实环境可执行门（Windows/DEVICE/CANARY/SCALE 各自的门） | 全部启用 | 全部启用，含 T19 终局 DoD |

   **红线：以下内容任何档位全量适用，不因降档弱化半条。** 计划书 §1.2 D-01…D-12 的全部纠正；§2.2 三个权威边界（Control Plane / GBrain Company / ZennoDroid 各自拥有与明确不拥有的东西）；fail-closed 语义（required 只认 `PASS`，`SKIP/PARTIAL/NOT_RUN/INFRA_ERROR/NOT_APPLICABLE` 与缺证据一律阻断；未知合同 major、action、step、selector、policy、身份与结果全部失败关闭）；`AGENTS.md` 的七级证据等级及其逐级签发条件（等级只由对应可执行门签发，模拟不得替代）；「模型只能提议，不能自我批准、不持签名私钥、不能清除冻结」（D-09、技术书 §2 第 5/11 条）；技术书 §2 全部 15 条不可妥协原则；**每一个将要进入 `main` 的提交，合入前都必须有绑定精确 head/base 的外审一票（`FAIL`/`UNAVAILABLE` 即冻结不合入）与所有者平台可见、绑定该精确 head 的批准**（外审机制 §二「批次收尾」行、§三-5，计划书 §4.1 第 6 条；批准专属所有者，合入的执行动作可代做，两者分开留痕）；**任何 Owner 签署或风险接受都不改变「什么算证据、什么算通过」——`RISK_ACCEPTED` 不折算为 PASS、不折算为证据齐全**。**降档只影响批次仪式的节奏——是否把多处修复凑成批次、是否一批一会话、是否逐批做里程碑收口审计与逐批前置核验审计裁决——不影响「合入前是否有绑定 commit 的一票外审与平台可见批准」，也不降低任何一条通过标准；任何档位都不得把 `FAIL` 记成 `PASS`，不得用没跑过的命令冒充证据，不得用「本档不要求」当作缺证据的解释。**

   **与硬规则 2 的关系（不冲突，勿误读为豁免）**：硬规则 2 的适用条件是「本批触碰候选门禁信任根」，与 F 档无关，F0–F2 照常全量适用。因此 F0–F2 的正确做法是：**优先做不触碰 `CANDIDATE_TRUST_PATHS` 的修复**；确需触碰信任根时仍单批独审串行，取证按 §4.5——「生效条件」成立时于合入前在 PR head 取 required 静态门证据，§4.6 (a) 未签署时回落原两段式——合入走计划书 §4.1 第 6 条路径：批准专属所有者且须平台可见、绑定精确 head 留痕，合入的执行动作按该条现行文字办。

   **升级触发条件（只看事实发生，不看时间、不看已合入 PR 数量）**：
   - **F0–F2 → F3–F5**，三条缺一不升：(a) main tip 的 Phase 0 required `overall_status == PASS`（全部存量 `FAIL` 清零、`INFRA_ERROR` 为 0）——判定以**受信环境**的一次完整跑为准（仓库固定 `.venv` 与 pinned node 生效、Legacy anchor 已签发）；裸 worktree 或缺 `.venv`/anchor 的环境会多出 `ERROR: repository .venv command requires the active pinned repository interpreter` 一类失败，属环境噪声，**既不得计入存量红清单，也不得用「换个环境就绿了」当作已清零**；(b) 在该状态下候选门禁 `Tools/ci/run_candidate_gate.py` 能产出 clean 证据（该门写任何证据前要求 Phase 0 `overall_status == PASS`，此项验证其自身恢复可执行；这是能力门槛，不附带任何批次取证义务）；(c) M1 收口审计（T4）补做一次并取得可核验的 `PASS`（定义见硬规则 1，不放宽）。
   - **F3–F5 → F6–F9**，三条缺一不升：(a) WP 探针（T15）§11 清单全部项完成并有原始制品；(b) 目标 Windows + ZennoDroid + 授权真机就位；(c) §15 条目 4/6 的平台授权与具名批准已由所有者给出。缺任一项，F6–F9 保持 `WAITING_EXTERNAL`（技术书 §13），模拟或 Mock 结果不得替代、不得提升证据等级。
   - **降档也需要事实**：出现新增 required `FAIL`、或原本可取得的证据变为不可取得时，退回上一档并记录原因；不得原地降低标准继续推进。

---

## 执行顺序（依赖 DAG，手动挑批次照此；T 编号只是模板索引，不是执行顺序）

- **R0 段严格串行**：R0-B(T1) → R0-C(T2) → R0-D(T3) → M1 收口审计(T4)。
- **M1C 合入且 T4 无未决 critical 后**，两轨并行、各轨内部按序：M2 轨 T5→T6→T7→T8；M3 轨 T9→T10→T11→T12。（⚠️ 并行轨的安全并行合并编排属 R0 后 §9.1 专门批次、尚未建成；到达本段时若该批次未就绪，退化为串行推进——见外审机制 §三-5。）
- **跨轨/探针前置**（前置未满足不得开工，模板自带前置门会自行停下）：T15(WP 探针) 先于 T10(M3-2)；T10(M3-2) 先于 T8(M2-4)。故 M3 轨就绪序为 T9→**T15**→T10→T11→T12，且 **T8 必须等 T10 合入后才可开**。
- **汇合段串行**：T13(M2/M3 收口审计) → T14(M4) → T16(M5) → T17(M6)。
- **可插入 / 收尾**：T15(WP 探针) M0 后任意时点可跑；T18(LC Legacy 物删) WP 完成后可重复插入、每次单独批准合入；T19 终局 DoD 审计最后。

> 每个 T# 模板内已内置自己的前置门（会先核对 dps2 已合入 PR），照此 DAG 挑批次即可；两轨并行时各开一个会话。
>
> **档位提示（硬规则 3）**：本 DAG 与下列 T# 批次模板是 **F3 档起**的工作单位。当前处于 F0–F2 档，工作单位是「一处修复 = 一个 commit」，用下方 `TR` 模板；本 DAG 此刻只作为**目标顺序参考**，不是当前的排程依据。升档触发条件见硬规则 3。

---

## 通用模板

### TR · F0–F2 存量红清零（当前档位的默认模板；一处修复 = 一个 commit）

```text
当前处于 F0–F2 档（判定与红线见本文件硬规则 3）。目标只有一个：把 main tip 的 Phase 0 required 存量 FAIL 清零，让 overall_status 变成 PASS。
做法：一次只修一个失败 check，一处修复就是一个 commit，不要凑批次、不要顺手重构、不要改无关文件。先跑门禁复现该 FAIL 并贴真实输出，再改，再跑同一条命令贴真实输出；跑不通就说跑不通，不得用"应该能过"代替实际跑过。
不得触碰 CANDIDATE_TRUST_PATHS（候选门禁自身信任根）；确需触碰就单批独审串行、先停下告诉我，合入走计划书 §4.1 第 6 条路径（批准专属所有者且须平台可见、绑定精确 head 留痕），取证按计划书 §4.5：§4.6 (a) 已签署时于合入前在 PR head 取 required 静态门证据，(a) 未签署时 fail-closed 回落原两段式（合入前 --diagnostic-workspace 记录性验证 + 合入后在后继提交上以 --base D 取首个 clean 候选证据）。该静态门由候选自身携带的校验器执行，见 §4.5 残余披露，不得当作独立于候选的验证。
本档简化的只有批次仪式：不必把多处修复凑成批次、不必一批一会话、不做里程碑收口审计、不做逐批前置核验审计裁决。合入前的外审与批准一条不减：每个将要进入 main 的提交都必须被一次绑定精确 head/base 的 Codex 外审覆盖（在合入目标 base tip 上 review --base <base tip> --scope branch，信任根/anchor/执行链改动用 adversarial-review，FAIL/UNAVAILABLE 即冻结不合入），并有我本人平台可见、绑定该精确 head 的批准；一个 PR 可以承载多个此类 commit，那一票覆盖该 PR 全部提交即可——简化的是「不必凑批次」，不是「不必有那一票」。合入前你要复核 PR head == 已审 commit、base tip 未变，并用 gh pr merge --match-head-commit <已审-head-oid> 约束 head（外审机制 §三-5）。会话收尾自动 review gate 照常强制。不随档位弱化的红线（D-01…D-12、三条真相边界、fail-closed 语义、七级证据等级、模型不能自我批准/持私钥/清除冻结、技术书 §2 十五条、每个进入 main 的提交都有绑定精确 head/base 的外审与平台可见批准）全量适用。
每个 check 转绿后报告：check 名、修复的根因、跑过的命令与真实输出。全部转绿后停下告诉我——那是 F0–F2 → F3–F5 的升档触发条件之一，由我决定是否升档，你不得自行升档或改用批次模板。
```

---

## 第一段：R0 治理根（严格串行，§16 钉死顺序）

### T1 · R0-B 指令 receipt 迁移

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：R0-B 指令 receipt 迁移（§4.2）。
开工前先核对 dps2 远程已合入 PR：前序批次（M0/R0-A）未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次，退出条件以 §10 M1A 行为准。上一批证据以 dps2 最新已合入 PR 及其门禁/外审记录为准，自行获取。
注意：本批触碰候选门禁信任根（Manifest schema、Tools/ci），取证按计划书 §4.5「生效条件」：§4.6 (a) 已签署时于合入前在 PR head 取 required 静态门证据（CI artifact 自动留档）、合入即本批关闭；(a) 未签署时 fail-closed 回落原两段式——合入前 --diagnostic-workspace 记录性验证 + 合入后在后继提交上以 --base D 取首个 clean 候选证据，且上一个信任根批次未取得该证据前不得合入本批，此时下文「我确认合入即本批关闭」不适用。该 required 静态门由候选自身携带的校验器执行（见 §4.5 残余披露），不得当作独立于候选的验证。改动 legacy-runtime-adapter 的 module.yaml 触发 §11 的 anchor 同批重签——DPS_LEGACY_BASELINE_ANCHOR 未签发就停下告诉我。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T2 · R0-C Release BOM 权威迁移

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：R0-C Release BOM 权威迁移（§4.3）。
开工前先核对 dps2 远程已合入 PR：前序批次（R0-B）未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次，退出条件以 §10 M1B 行为准。上一批证据以 dps2 最新已合入 PR 及其门禁/外审记录为准，自行获取。
注意：本批触碰候选门禁信任根（Tools/ci），取证按计划书 §4.5「生效条件」：§4.6 (a) 已签署时于合入前在 PR head 取 required 静态门证据（CI artifact 自动留档）、合入即本批关闭；(a) 未签署时 fail-closed 回落原两段式——合入前 --diagnostic-workspace 记录性验证 + 合入后在后继提交上以 --base D 取首个 clean 候选证据，且上一个信任根批次未取得该证据前不得合入本批，此时下文「我确认合入即本批关闭」不适用。该 required 静态门由候选自身携带的校验器执行（见 §4.5 残余披露），不得当作独立于候选的验证。Release BOM 由仓外 signer 签发，模型与候选代码不得持签名私钥。本批须以集成测试证明 policy 与 executor 两侧消费代码路径读取同一个 `control-plane-host` 实例的同一 generation/token（仅合同层同源不可替代，见 §4.3 与 M4 的交付边界）；该证明止于工程/集成测试层面，不建、不声称建成生产环境下的真实接线（该义务属 M4）；本批前涉及模块保持 `releaseEligible=false`、无 production composition entrypoint。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T3 · R0-D 删除 11 个 factory 模块

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：R0-D 删除 11 个 factory 模块（§4.4）。
开工前先核对 dps2 远程已合入 PR：前序批次（R0-B、R0-C）未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：只删 §4.4 列出的 11 个目录及其专属引用（catalog、Manifest、schema、DAG、compatibility、候选测试 policy、CODEOWNERS、CI、README、operations），不得删除已迁出的 receipt/BOM/回滚能力。退出条件以 §10 M1C 行为准（含 module-impact suite 与 merge queue 验证）。上一批证据以 dps2 最新已合入 PR 及其门禁/外审记录为准，自行获取。
注意：本批删除多个信任根文件，取证按计划书 §4.5「生效条件」：§4.6 (a) 已签署时于合入前在 PR head 取 required 静态门证据（CI artifact 自动留档）、合入即本批关闭；(a) 未签署时 fail-closed 回落原两段式——合入前 --diagnostic-workspace 记录性验证 + 合入后在后继提交上以 --base D 取首个 clean 候选证据，且上一个信任根批次未取得该证据前不得合入本批，此时下文「我确认合入即本批关闭」不适用。该 required 静态门由候选自身携带的校验器执行（见 §4.5 残余披露），不得当作独立于候选的验证。这是高危批次，批次收尾外审用 adversarial-review。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T4 · M1 里程碑收口审计（只审不改）

```text
用 workflow 对里程碑 M1（M1A/M1B/M1C 三批）做退出评审：交叉核验全部已合入批次与 Docs/RebuildPlan_重构计划书.md §4/§10 的一致性，逐条核验退出条件与 §4.5 合入前 required 静态门证据是否齐全，并核对计划书 §4.6 Owner 裁决文书 (a)/(d)/(e) 的签署状态与 PR-A 历史收口文书（Docs/Operations/HistoricalClosure_历史收口_2026-07-26.md）是否已合入，产出对照裁决表。先判生效条件：§4.6 (a) 已由所有者本人签署且有平台可见、绑定精确 head 的批准，才按修订后 §4.5 的合入前单段式核验；(a) 未签署则本次修订整体未生效，按 §4.5「生效条件」逐字保留的原两段式条文（含串行硬顺序）核验，且不得援引任何「无后续取证动作」「合入即批次终点」的表述。只审不改：(a) 已签署时历史批次不再核验已废止的原第 2 步，也不新设任何取证动作；历史批次合入程序缺陷的定性以 PR-A 历史收口文书（其合入以 Owner 在 PR-A 上给出平台可见、绑定精确 head 的批准为前提，核验时确认该批准存在）为准，不重复裁决。历史批次的取证缺口一律据实裁决、不因任何签署而改记：相应条目记 UNAVAILABLE（历史 overall_status == FAIL 的运行据实记 FAIL），并计入「§4.5 合入前证据不全」；§4.6 (e) 签「风险接受」时只附加 RISK_ACCEPTED 标注，该标注不满足 PASS 的充要条件，不得计为通过、不得作为证据引用、不得使总裁决变为 PASS；(e) 签「等价重验」且重验完成并留存可核验 CI 记录时，条目仍记 UNAVAILABLE，可附 REVALIDATED@<CI run ref> 作为补充记录，该记录不折算为该项满足——重验前后该项一律计为不满足。触碰信任根批次的 required 静态门由候选自身携带的校验器签发（见 §4.5 残余披露），核验时须如实记该证明力限制，不得表述为独立于候选的验证。把对照裁决表（每条判"通过"的退出条件须注明其合入前门禁证据/可执行门证据 ref）写进 dps2 的审计 issue，并在 issue 正文开头写两行 `总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目/无>`——仅当 M1 全部退出条件逐条核验通过（按计划书条款显式且正确标注的 DEFERRED 项视为该条通过、并列入未决 DEFERRED 行）、§4.5 合入前取证证据齐全（历史批次的缺口据实计入「不齐全」；`RISK_ACCEPTED` 标注与 `REVALIDATED@<CI run ref>` 补充记录一律不折算为齐全）、§4.6 (a)/(d)/(e) 均已签署且 PR-A 文书已合入、无未决 critical 时才写 `PASS`；历史取证缺口无论只带 `RISK_ACCEPTED` 标注、还是另附 `REVALIDATED@<CI run ref>` 补充记录，本项均不满足、总裁决不得为 `PASS`——如实写 `FAIL`/`UNAVAILABLE`，在裁决表标出这些条目与仍未通过项，此时 M1 按计划书 §4.6 (c) 维持冻结，你只报告事实并停下，不得自行放行、不得改判；未标注/错标的缺失、证据不全、或无法核验一律写 `FAIL` 或 `UNAVAILABLE`（两行裁决与裁决表落审计 issue——不开零 diff / marker PR、不写受跟踪文件，遵 AGENTS.md 裁决落 issue 与门禁状态不入库），开好 issue 后停下等我人工复核；后续 T5/T9 据该审计 issue 正文开头的 `总裁决` 字段核验（为 `PASS`、记录一致可核验、且证据引用真实存在可追溯——信任根批次的合入前 required 静态门证据逐条确认真实含门禁输出、非空壳，其余可抽验——才放行，非 `PASS` 一律停）。发现未决 critical 就停下告诉我，不得进入 M2/M3。
```

---

## 第二段：M2 Soul 轨 ∥ M3 执行轨（M1 收口后可两轨并行开两个会话；两轨各自内部按序）

并行规则（§3.3）：两轨只碰各自模块，公共合同 landing 与合入由 merge queue 串行。

### T5 · M2-1 Persona 投影链

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-1 Persona 投影链（§5.1）。
开工前先核对 dps2 已合入 PR：M1C 未合入、或 M1 收口审计（T4）的裁决不是可核验的 PASS（未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical，均属非 PASS，一律停），则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次（Persona v1 保持、persona outbox -> GBrain projector -> SoulMemory adapter -> exact readback 链及其独立测试），不触碰 M3 轨模块。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T6 · M2-2 长期记忆合同 memory.event/v3

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-2 长期记忆合同（§5.2）。
开工前先核对 dps2 已合入 PR：M2-1 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：memory.event/v3 与 gbrain.projection/v3 以 additive major 并存落地，消费者按实际登记的 v1 起步登记 v1/v3 双读（不虚构 v2 消费历史），认知衰减与纠正/删除两条链分开。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（memory-lifecycle suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T7 · M2-3 兴趣算法 interest v2

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-3 兴趣算法 v2（§5.3）。
开工前先核对 dps2 已合入 PR：M2-2 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：interest.snapshot/v2 新 major、写码前冻结 weight/a/b/half_life 与 golden vectors、seed 归 interest-reducer。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（interest-v2 suite 全 golden vectors）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T8 · M2-4 planner 行为分布与参数采样

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M2-4 planner 行为分布与 session nonce 参数采样（§5.4）。
开工前先核对 dps2 已合入 PR：M2-3 未合入则停下告诉我；本批信封绑定 operation.compiled/v2，其 provider 由 M3-2 引入——M3-2 未合入也停下告诉我（两轨并行时本批必须等 M3-2 合同落地，不得在 M2 轨自造 provider）；本批次已合入则告诉我该用哪个模板。范围只做本批次：planner 按 Soul 生成行为分布（含跨会话宏观节律与动作构成比）、独立 nonce 采样、operation.compiled/v2 信封绑定三类 revision；宏观节律样本不足时按 §5.4 显式标注 DEFERRED。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（behavior-params 与 soul-isolation suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T9 · M3-1 APP 包与自动探索安全流

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-1 app.package/v1 与探索安全流（§6.1/§6.2）。
开工前先核对 dps2 已合入 PR：M1C 未合入、或 M1 收口审计（T4）的裁决不是可核验的 PASS（未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical，均属非 PASS，一律停），则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：operation-compiler 拥有 app.package/v1 与 canonical 包、app_onboarder 只产隔离候选、至少两个非 IG fixture，不触碰 M2 轨模块。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（app-package suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T10 · M3-2 operation.compiled/v2 与唯一 ActionExecutor

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-2 operation.compiled/v2 与 ActionExecutor 失败关闭改造（§6.3）。
开工前先核对 dps2 已合入 PR：M3-1 未合入则停下告诉我；WP 探针（T15）未完成也停下告诉我——本批改写受保护 Legacy `ActionExecutor.cs`，按计划书 §11 探针前禁止改写受保护 Legacy C#，CodeDom/Input API 兼容性未知时不得合入该改造；本批次已合入则告诉我该用哪个模板。范围只做本批次：v1/v2 additive 提供、只有 command-orchestrator 登记直接消费、ActionExecutor 空/未知/异常/partial 全失败关闭并逐 step 验证后置条件、信封 delay/typing/trajectory 参数在携带对应参数的 step 上消费生效且越界即拒绝。
注意：本批触碰 79 文件 Legacy 字节基线（ActionExecutor.cs），按 §11 同批重签 anchor——anchor 不可用就停下告诉我。高危批次，外审用 adversarial-review。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（execution-path suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T11 · M3-3 edge.bridge.exchange/v2 与薄 C#5 入口

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-3 brain-to-hand 交接合同与独立 Legacy 入口（§3.4）。
开工前先核对 dps2 已合入 PR：M3-2 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：edge.bridge.exchange/v2、与 SessionRunner.Run 完全分离的薄 C#5 入口（默认落在 Legacy 字节基线作用域之外，如 legacy-runtime-adapter 模块内）、首个 handoff allowlist 只含确定性 primitives、合同测试证明旧入口不可达。不翻转 legacy 三道锁。
注意：高危批次，外审用 adversarial-review；若探针证明入口必须落入受保护作用域，停下告诉我走 anchor 重签决策。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T12 · M3-4 按需视觉提案链

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M3-4 按需视觉纠错（§7）。
开工前先核对 dps2 已合入 PR：M3-3 未合入则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：确定性失败后的诊断/提案链、脱敏截图 capability（绑定作用域/hash/TTL/删除策略）、IModelBroker 窄端口；模型不能直驱设备、不能改配置、不能宣告成功。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（visual-security suite 含 prompt injection 负例）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T13 · M2/M3 里程碑收口审计（只审不改）

```text
用 workflow 对里程碑 M2 与 M3 做退出评审：交叉核验两轨全部已合入批次与 Docs/RebuildPlan_重构计划书.md §3.4/§5/§6/§7/§10 的一致性，逐条核验 M2、M3 退出条件（含双 Soul 正反例、信封参数逐 step 生效、旧入口不可达、两种非 IG fixture）与 §4.5 合入前 required 静态门证据，产出对照裁决表。先判生效条件：§4.6 (a) 已由所有者本人签署且有平台可见、绑定精确 head 的批准，才按修订后 §4.5 的合入前单段式核验；(a) 未签署则按 §4.5「生效条件」逐字保留的原两段式条文（含串行硬顺序）核验。只审不改：(a) 已签署时历史批次不再核验已废止的原第 2 步，也不新设任何取证动作；历史批次合入程序缺陷的定性以 PR-A 历史收口文书（Docs/Operations/HistoricalClosure_历史收口_2026-07-26.md；该文书的合入以 Owner 在 PR-A 上给出平台可见、绑定精确 head 的批准为前提，核验时确认该批准存在）为准，不重复裁决。历史批次的取证缺口一律据实裁决、不因任何签署而改记：相应条目记 UNAVAILABLE 并计入「§4.5 合入前证据不全」；§4.6 (e) 签「风险接受」时只附加 RISK_ACCEPTED 标注，该标注不满足 PASS 的充要条件，不得计为通过、不得作为证据引用、不得使总裁决变为 PASS；(e) 签「等价重验」且重验完成并留存可核验 CI 记录时，条目仍记 UNAVAILABLE，可附 REVALIDATED@<CI run ref> 作为补充记录，该记录不折算为该项满足——重验前后该项一律计为不满足。触碰信任根批次的 required 静态门由候选自身携带的校验器签发（见 §4.5 残余披露），核验时须如实记该证明力限制。把对照裁决表（每条判"通过"的退出条件须注明其合入前门禁证据/可执行门证据 ref）写进 dps2 的审计 issue，并在 issue 正文开头写两行 `总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目/无>`——仅当 M2/M3 全部退出条件逐条核验通过（按 §5.4 等计划书条款显式且正确标注的 DEFERRED 项视为该条通过、并列入未决 DEFERRED 行）、§4.5 合入前取证证据齐全、无未决 critical 时才写 `PASS`；未标注/错标的缺失、证据不全、或无法核验一律写 `FAIL` 或 `UNAVAILABLE`（两行裁决与裁决表落审计 issue——不开零 diff / marker PR、不写受跟踪文件，遵 AGENTS.md 裁决落 issue 与门禁状态不入库），开好 issue 后停下等我人工复核；后续 T14 据该审计 issue 正文开头的 `总裁决` 字段核验（为 `PASS`、记录一致可核验、且证据引用真实存在可追溯——信任根批次的合入前 required 静态门证据逐条确认真实含门禁输出、非空壳，其余可抽验——才放行，非 `PASS` 一律停）。发现未决 critical 就停下告诉我，不得进入 M4。
```

---

## 第三段：汇合与验证（串行）

### T14 · M4 组合与滚动门

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M4 组合与滚动门（§8/§9）。
开工前先核对 dps2 已合入 PR：M2、M3 未全部合入、或 M2/M3 收口审计（T13）的裁决不是可核验的 PASS（未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical，均属非 PASS，一律停），则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：composition root（含 §4.3 交付边界移交的同实例义务：policy 与 gateway 须消费同一个 active-binding provider 实例，覆盖 activation/revocation/rollback、restart、并发读与设备隔离）、attempt/receipt/eligibility 合同、session+command 双 300 滚动门、runtime kill switch 与工程 freeze、通知 outbox。退出条件以 §10 M4 行为准（含 kill-notify suite 的未授权清除负例与上述同实例覆盖）。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 required 门禁（reliability 与 kill-notify suite）、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T15 · WP Windows/Zenno 探针（M0 后任意时点可插入，只探测不改产品码）

```text
按 Docs/RebuildPlan_重构计划书.md 执行 WP 探针（§11）：在 Parallels Windows + 目标 ZennoDroid 上记录 §11 探针清单全部项（版本/CodeDom/OwnCode 清单/加载入口/ADB 授权/adb 37.0.0-14910828 与 pwsh 7.6.2 精确断言/Input API 签名/端口超时/编码 hash/Enterprise 对 AVD 的接受性/AVD snapshot 身份连续性），产出原始探针制品。只探测不改产品代码；Enterprise 不接受 AVD 就停下告诉我走回退决策（Parallels USB 直通真机）。环境未就绪（Parallels/ZennoDroid 安装介质）就停下告诉我需要准备什么。
```

### T16 · M5 模拟闭环

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M5 macOS AVD + Parallels + ZennoDroid 模拟闭环（§10 M5 行）。
开工前先核对 dps2 已合入 PR：M4 未合入或 WP 探针未完成则停下告诉我；本批次已合入则告诉我该用哪个模板。范围只做本批次：搭通模拟闭环并产出原始证据，全部标记 SIMULATION，不提升 Windows/DEVICE 等级。上一批证据以 dps2 最新已合入 PR 为准，自行获取。
完成后：跑 simulation suite、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T17 · M6 授权真机与受限 canary（human-required）

```text
按 Docs/RebuildPlan_重构计划书.md 施工批次：M6 目标 Windows、授权设备、受限 canary（§10 M6 行）。
开工前先核对：M5 未合入则停下告诉我；§15 条目 4/6 的平台授权与具名批准我是否已给你——没有就停下列出缺哪项，不得触碰任何真实平台写操作。证据等级仅由对应可执行门逐级签发。
完成后：按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T18 · LC Legacy 物理删除（可重复使用，每次删一个对象）

```text
按 Docs/RebuildPlan_重构计划书.md 执行一个 Legacy 物理删除批次（§11）：从探针后确认可安全删除的对象中取一个（零入口证明 + Windows 编译/加载原始结果齐备的优先），单独成批：删除 + 验证器清单绑定 + 字节基线制品 + anchor 重签归同一批，附 rollback。WP 探针未完成或 anchor 不可用就停下告诉我。
完成后：跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审（adversarial-review）、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```

### T19 · 终局 DoD 审计（只审不改）

```text
用 workflow 对 Docs/RebuildPlan_重构计划书.md §14 Definition of Done 做终局评审：逐条核验十项完成事实，核对当前声明的证据等级与实际取得的可执行门证据一致，并逐条核对历史各里程碑遗留的 `未决 DEFERRED` 项是否均已在其阻塞里程碑取得样本后回补核验通过，产出对照裁决表（每条判"通过/一致"的完成事实须注明其可执行门证据 ref）与残留缺口清单。凡带 RISK_ACCEPTED 标注的条目（计划书 §4.6 (e)）一律列入残留缺口清单并保留该标注：风险接受不是证据，不得计为完成事实、不得作为证据引用、不得使总裁决变为 PASS；若其后已按 (e) 的等价重验支路完成重验并留存可核验 CI 记录，可在该条目附 REVALIDATED@<CI run ref> 作为补充记录——该记录不改变其定性：条目仍列入残留缺口清单、保留原有标注，不得计为完成事实、不得作为证据引用、不得使总裁决变为 PASS。只审不改。把对照裁决表与残留缺口清单写进 dps2 的审计 issue，并在 issue 正文开头写两行 `总裁决: PASS|FAIL|UNAVAILABLE` 与 `未决 DEFERRED: <条目清单或 无>`——仅当 §14 十项完成事实与证据等级逐条核验一致、无残留缺口、且所有历史 `未决 DEFERRED` 项均已回补核验通过（即 `未决 DEFERRED: 无`）时才写 `PASS`，任一未回补/证据等级不符/有残留缺口一律写 `FAIL` 或 `UNAVAILABLE`（两行裁决、裁决表与缺口清单落审计 issue——不开零 diff / marker PR、不写受跟踪文件，遵 AGENTS.md 裁决落 issue 与门禁状态不入库），开好 issue 后停下等我人工复核。
```

---

## 万能兜底模板（不确定该用哪个时）

```text
先按本文件硬规则 3 判当前档位：若处于 F0–F2（main tip 的 Phase 0 required overall_status 仍是 FAIL），停用本模板、改用 TR，并告诉我为什么。以下只在 F3 档起适用。
按 Docs/RebuildPlan_重构计划书.md 与 dps2 已合入 PR 推导下一个未合入批次并施工它；范围只做该批次。开工前两项前置缺一即停：①跨里程碑边界时确认对应收口审计（M1 用 T4、M2/M3 用 T13）的裁决是可核验的 PASS——未完成、FAIL/UNAVAILABLE、记录不一致或无法核验、有未决 critical 均属非 PASS，任一即停；②把你推导出的批次名和依据告诉我。完成后跑 required 门禁、按 Docs/Operations/ExternalReview_外审机制.md 走批次收尾 Codex 外审、开 PR 到 dps2，然后停下等我批准；我确认合入即本批关闭。
```
