# 重构外审机制

> 文档状态: `Current`（机制已配置并可运行）  
> 证据状态: `NONE`（本文档描述审查流程本身，不签发任何验证等级）  
> 上游依据: `Docs/RebuildPlan_重构计划书.md` §9.1（异构模型复核）、§13（停止条件）

本文档把重构计划书 §9.1 的"异构模型复核"落成当前工具链下可执行的操作程序。两条核心约束不变：

1. **模型只有 advisory/veto 权**——任何 `FAIL/UNAVAILABLE/分歧` 都必须导致候选冻结；这是 §9.1 的目标语义，在其确定性控制器建成前由用户程序性执行（见 §三-4），当前没有任何自动冻结机制。`PASS` 不能替代 required 门禁和具名批准。
2. **每个候选 diff 至少两个独立异构 reviewer**（§9.1 原文）——Codex 只是其中一个。单一 Codex review `PASS` 不满足批次合入条件；本文档任何表述不得被解读为把双复核门降级为单复核。

## 一、工具与配置（已就绪）

- 审查载体：Claude Code 的 openai-codex 插件（codex-cli，ChatGPT 账号登录）。
- 模型：由 `~/.codex/config.toml` 的 `model` 字段决定（当前 `gpt-5.6-sol`，`model_reasoning_effort = "xhigh"`，2026-07-17 实测可用）。升级到更新模型只改这一行；companion 的 `review` 子命令无独立模型参数，跟随全局配置。
- 项目信任：`/Users/younghu/Documents/ZennoDroid_DSP/DSP_ZD` 已登记为 trusted。
- 会话收尾强制门（review gate）：已对 ZennoDroid_DSP 项目启用——施工会话每次 stop 前必须有新鲜 Codex review。开关命令：

```text
/codex:setup --enable-review-gate   # 启用
/codex:setup --disable-review-gate  # 关闭（问答类会话嫌吵时）
```

## 一之二、第二异构复核（已自动化，2026-07-17 起）

第二票由 DeepSeek（`deepseek-v4-pro`）担任，脚本在仓外 `~/dps-authority/second_review_deepseek.py`，API key 在仓外 `../Deepseek_DPS2_API.txt`（相对仓库父目录；不入 Git）。批次收尾命令：

```text
python3 ~/dps-authority/second_review_deepseek.py \
  --root ~/Documents/ZennoDroid_DSP/DSP_ZD \
  --pr <N> --repo HelloYoung2025/DPS2.0 \
  --focus "<本批次范围一句话>"
```

exit 0 = PASS；exit 1 = FAIL（冻结不合入）；exit 2 = UNAVAILABLE（同样冻结，对应计划书 §13 "reviewer 不可用"）。判决 JSON 贴回 PR。与 Codex 票合计：**两票均 PASS 才满足合入前双复核**；任一 FAIL/UNAVAILABLE/两票矛盾即冻结。

该脚本的三条不可弱化语义（2026-07-17 外审 F4/F5/F6 采纳）：

1. **外发前 fail-closed 敏感扫描**：diff 与 PR 描述先过确定性密钥/凭证模式扫描（私钥块、AWS/GitHub/OpenAI/Slack token、JWT、bearer 头、凭证赋值），任一命中即 exit 2 冻结，**一个字节都不发出**。命中样本只截前 12 字符入报告。
2. **可执行文件钉版本**：合入门所依赖的脚本按 SHA-256 钉死，当前为 `d1729c2baad4e8a72c0c4b7137d954aa2abba2018df91136d47d445338c57402`（model `deepseek-v4-pro`，输出 schema `{verdict, blocking_findings[], advisory_notes[]}` + 绑定 head_oid/diff_sha256）。改脚本＝改合入程序，须在本文件同步更新指纹并走批次外审；采信任何一票前先核对脚本实际指纹与本行一致。
3. **显式 FAIL 永不折算成 PASS**：任何 chunk 显式 `verdict:FAIL` 都保留——有 blocking_findings 则总裁决 FAIL（exit 1）；FAIL 而 findings 为空属于歧义裁决，按 UNAVAILABLE（exit 2）冻结。

## 二、三个触发点

| 层级 | 触发时机 | 动作 | 失败语义 |
|---|---|---|---|
| 会话收尾 | 施工会话每次 stop（review gate 自动强制） | Codex 对 working-tree 变更 review。这是便利层，不是 §9.1 门 | 未通过则不视为本段工作完成 |
| 批次收尾 | 每个批次门禁全绿后、合入前（计划书 §16 的每个 PR） | **双复核，绑定同一 commit/diff**：① Codex `review --base <上一批合入提交>`（高危批次——信任根、Legacy anchor、执行链——改用 `adversarial-review`）；② 至少一个与 diff 作者模型不同族的第二异构复核（见下"第二复核员"） | 任一 reviewer `FAIL/UNAVAILABLE`、两者分歧或输入 hash 不一致 = 冻结不合入（程序性规则，由用户在合入前执行；对应计划书 §9.1/§13 的目标语义） |
| 里程碑收尾 | M0–M6 每个退出条件评定前 | 多智能体 workflow 交叉审核 + Codex 深审双轨，产出对照裁决表 | 未决 critical 未修订前不得进入下一里程碑 |

**第二复核员的选取**：异构 = 与该批次 diff 的作者模型不同厂商。diff 由 Claude 会话产出时，第二复核员用 DeepSeek/GLM（凭证见计划书 §15 条目 2，未接入前可临时用其他非 Anthropic 模型）——作者不能自审，Claude 系模型不算该批次的独立复核；diff 由 Codex 会话产出时，Codex review 不计为独立复核，改由 Claude + 第三家模型构成双复核。计划书 §9.1 的确定性控制器（自动比对 schema 输出并置位冻结）落地前，双复核结果由用户在合入前人工核对，缺一不合。

批次收尾命令（在仓库内执行；companion 脚本路径中的版本号随插件升级变化，以实际安装为准）：

```text
node ~/.claude/plugins/cache/openai-codex/codex/<版本>/scripts/codex-companion.mjs \
  review --wait --base <ref> --scope branch
```

在 Claude Code 会话内可直接用插件命令触发，无需记路径。

## 三、审核意见的处置纪律

1. **版本绑定**：每份外审报告开头必须记录被审文档/代码的版本与 commit。采信任何意见前先核对它审的是不是当前版本——v2/v3 版本错位曾导致两条 critical 中的一条（"恢复 stub"）变成反向建议。
2. **逐条裁决**：意见只有四种处置——采纳（给最小编辑）/ 已覆盖（引现文行号）/ 失效（针对已废弃机制，说明为何不采纳）/ 驳回（对仓库事实不成立，给证据）。不许"顺手改"驳回项。
3. **证据要求**：采纳与驳回都必须给 `文件:行号` 级证据；无法核验的意见按"未复核"单列，采信前自行核验。
4. **权限边界与冻结的现状**：目前只有会话收尾的 review gate 是机器强制（review 不过，会话无法正常收尾）；批次级"冻结不合入"是程序性规则，由用户在合入前人工执行——计划书 §9.1 的确定性控制器（自动比对 schema 判决并置位冻结）尚未建成，本文档不得被解读为该控制器已存在。解除冻结、合入批准、里程碑评定始终是用户/具名批准者的动作；模型不持密钥、不能自批准（§9.1）。

## 四、给每个施工会话的固定开工输入

每个批次开一个独立会话，输入固定为三件：

1. 重构计划书对应小节（含该批次退出条件原文）；
2. 涉及模块的 `AGENTS.md`（`receiptRequired` 机制会强制核对）；
3. 上一批次的验收证据（门禁输出 + 外审结论）。

会话产出合入前必须走"批次收尾"外审；推送目标锁定 `dps2` 远程。
