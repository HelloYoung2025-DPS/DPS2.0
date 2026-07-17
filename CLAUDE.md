# DSP_ZD 会话规则（Claude Code）

先读根 `AGENTS.md`（项目边界、代码发现、架构红线的权威）。本文件只补充重构施工期的会话纪律。

## 施工权威

- 施工唯一权威：`Docs/RebuildPlan_重构计划书.md`。按 §16 排定的 PR 顺序施工，从尚未合入的最早批次开始（进度以 dps2 远程的已合入 PR 为准，本文件不记进度）。
- 每个会话只做一个批次；批次退出条件以计划书 §10 里程碑表为准，required check 只有 `PASS` 算通过。
- 开工输入三件套：计划书对应小节 + 涉及模块 `AGENTS.md` + 上一批验收证据（见 `Docs/Operations/ExternalReview_外审机制.md` §四）。

## 外审（必须）

- 会话收尾：Codex review gate 已启用，stop 前必须有新鲜 review；若本会话未生效，先运行 `/codex:setup --enable-review-gate`。
- 批次合入前：双复核（Codex + 与 diff 作者不同族的第二异构 reviewer），程序见 `Docs/Operations/ExternalReview_外审机制.md`。单一 Codex PASS 不满足合入条件。

## 硬规则

- 推送目标锁定 `https://github.com/HelloYoung2025/DPS2.0.git`（本机 checkout 中别名为 `dps2`；新 clone 若无此别名先 `git remote add dps2 <该 URL>`）。指向 `HelloYoung2025/DPS.git` 的远程是旧仓库，禁推。
- 触碰候选门禁信任根的批次按计划书 §4.5 两段式取证；触碰 Legacy 字节基线/anchor 保护文件的批次按 §11 同批重签 anchor。
- 不翻转 legacy 三道锁；不新建工厂/仓内任务状态/进度账本；证据不入 Git（Reports/ 被 ignore 是设计）。
- 已有红项（如受信环境缺 `DPS_LEGACY_BASELINE_ANCHOR`）单独记录，不与当前批次混批修复。
