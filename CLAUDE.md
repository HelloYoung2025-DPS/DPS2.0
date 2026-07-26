# DSP_ZD 会话规则（Claude Code）

先读根 `AGENTS.md`（项目边界、代码发现、架构红线的权威）。本文件只补充重构施工期的会话纪律。

## 施工权威

- 施工唯一权威：`Docs/RebuildPlan_重构计划书.md`。按 §16 排定的 PR 顺序施工，从尚未合入的最早批次开始（进度以 dps2 远程的已合入 PR 为准，本文件不记进度）。
- 每个会话只做一个批次；批次退出条件以计划书 §10 里程碑表为准，required check 只有 `PASS` 算通过。
- 开工输入三件套：计划书对应小节 + 涉及模块 `AGENTS.md` + 上一批验收证据（见 `Docs/Operations/ExternalReview_外审机制.md` §四）。

## 外审（必须）

- 会话收尾：Codex review gate 已启用，stop 前必须有新鲜 review；若本会话未生效，先运行 `/codex:setup --enable-review-gate`。
- 批次合入前：Codex 一票外审（绑定 commit/diff；高危批次用 adversarial-review），程序见 `Docs/Operations/ExternalReview_外审机制.md`；FAIL/UNAVAILABLE 即冻结不合入。**该一票任何档位不降**：F0–F2 档「一处修复 = 一个 commit」只简化批次仪式，每个进入 main 的提交仍须被一次绑定精确 head/base 的外审覆盖，并有所有者平台可见、绑定该精确 head 的批准（批准专属所有者、合入的执行动作可代做，见计划书 §4.1 第 6 条）。DeepSeek/GLM 是计划书 §9.1 运行时安全网的组件，不参与开发期合入投票。

## 硬规则

- 推送目标锁定 `https://github.com/HelloYoung2025/DPS2.0.git`（本机 checkout 中别名为 `dps2`；新 clone 若无此别名先 `git remote add dps2 <该 URL>`）。指向 `HelloYoung2025/DPS.git` 的远程是旧仓库，禁推。
- 触碰候选门禁信任根的批次：**计划书 §4.6 (a) 已由 Owner 签署（即 §4.5「生效条件」成立）时**，按 §4.5 于合入前在批次 PR head 上取 required 静态门证据（CI artifact 自动留档），合入即批次终点、无后续取证动作；**(a) 未签署时 fail-closed 回落 §4.5 原两段式取证**（合入前 `--diagnostic-workspace` 记录性验证 + 合入后在后继提交上以 `--base D` 取首个 clean 候选证据）。该 required 静态门由候选自身携带的校验器执行，其自签发残余见 §4.5「残余披露」，不得当作独立于候选的验证。触碰 Legacy 字节基线/anchor 保护文件的批次按 §11 同批重签 anchor。
- 不翻转 legacy 三道锁；不新建工厂/仓内任务状态/进度账本；证据不入 Git（Reports/ 被 ignore 是设计）。
- 已有红项（如受信环境缺 `DPS_LEGACY_BASELINE_ANCHOR`）单独记录，不与当前批次混批修复。
