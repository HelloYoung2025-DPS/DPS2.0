# 重构外审机制

> 文档状态: `Current`（机制已配置并可运行）  
> 证据状态: `NONE`（本文档描述审查流程本身，不签发任何验证等级）  
> 上游依据: `Docs/RebuildPlan_重构计划书.md` §9.1（异构模型复核）、§13（停止条件）

本文档定义**重构施工期**的外审程序（用户拍板，2026-07-17）：**批次收尾外审 = Codex 一票**（最新最强模型）。两条核心约束：

1. **模型只有 advisory/veto 权**——Codex `FAIL/UNAVAILABLE` 即候选冻结，由用户在合入前程序性执行（见 §三-4）；`PASS` 不能替代 required 门禁和具名批准。
2. **层级不要混淆**：计划书 §9.1 的"每个候选 diff 至少两个异构 reviewer（DeepSeek/GLM）"说的是**项目自身无人值守升级的运行时安全网**——那是要在相应里程碑接线的交付物，其凭证（§15 条目 2）供系统运行时使用。它不是本开发流程的合入门；开发期合入门只有 Codex 一票 + required 门禁 + 用户批准。

## 一、工具与配置（已就绪）

- 审查载体：Claude Code 的 openai-codex 插件（codex-cli，ChatGPT 账号登录）。
- 模型：由 `~/.codex/config.toml` 的 `model` 字段决定（当前 `gpt-5.6-sol`，`model_reasoning_effort = "xhigh"`，2026-07-17 实测可用）。升级到更新模型只改这一行；companion 的 `review` 子命令无独立模型参数，跟随全局配置。
- 项目信任：`/Users/younghu/Documents/ZennoDroid_DSP/DSP_ZD` 已登记为 trusted。
- 会话收尾强制门（review gate）：已对 ZennoDroid_DSP 项目启用——施工会话每次 stop 前必须有新鲜 Codex review。开关命令：

```text
/codex:setup --enable-review-gate   # 启用
/codex:setup --disable-review-gate  # 关闭（问答类会话嫌吵时）
```

## 一之二、DeepSeek 复核脚本（运行时安全网预备资产，不是开发期合入门）

**定位更正（2026-07-17 用户拍板）**：DeepSeek/GLM 属于计划书 §9.1 运行时安全网的异构 reviewer，凭证与脚本为该交付物预备；**开发期批次合入不要求 DeepSeek 出票**。脚本可随时手动调用取额外意见（advisory only），其判决不构成合入条件、也不因缺席而冻结。

资产位置：脚本 `~/dps-authority/second_review_deepseek.py`（model `deepseek-v4-pro`，判决绑定 head_oid/diff_sha256，外发前 fail-closed 敏感扫描，显式 FAIL 不折算 PASS，超限分块、不完整不发 PASS）；API key 在仓外 `../Deepseek_DPS2_API.txt`（相对仓库父目录；不入 Git）。§9.1 安全网在相应里程碑接线时以此为起点，接线批次自行定版并纳入门禁。调用方式：

```text
python3 ~/dps-authority/second_review_deepseek.py \
  --root ~/Documents/ZennoDroid_DSP/DSP_ZD \
  --pr <N> --repo HelloYoung2025/DPS2.0 \
  --focus "<本批次范围一句话>"
```

## 二、三个触发点

| 层级 | 触发时机 | 动作 | 失败语义 |
|---|---|---|---|
| 会话收尾 | 施工会话每次 stop（review gate 自动强制） | Codex 对 working-tree 变更 review。这是便利层，不是 §9.1 门 | 未通过则不视为本段工作完成 |
| 批次收尾 | 每个批次门禁全绿后、合入前（计划书 §16 的每个 PR） | **Codex 一票，绑定 commit/diff**：`review --base <上一批合入提交>`（高危批次——信任根、Legacy anchor、执行链——改用 `adversarial-review`） | Codex `FAIL/UNAVAILABLE` = 冻结不合入（程序性规则，由用户在合入前执行） |
| 里程碑收尾 | M0–M6 每个退出条件评定前 | 多智能体 workflow 交叉审核 + Codex 深审双轨，产出对照裁决表 | 未决 critical 未修订前不得进入下一里程碑 |

异构性由施工分工天然保证：diff 由 Claude 会话产出、Codex（OpenAI）审——作者与审查者不同族。若某批 diff 改由 Codex 产出，则该批外审换 Claude 或其他非 OpenAI 模型担任，作者不自审。

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

5. **合入前绑定已审提交（程序性合入下限；合入是用户/具名批准者的动作）**：当前阶段采程序性合入下限，不宣称已具备 §9.1 的确定性控制器能力。外审 `PASS` **仅对『已审 head commit + 已审 base tip 的组合』有效**——合入前由用户人工复核 PR head==已审 commit、base tip 未发生变化；任一不符即使原 `PASS` 失效，冻结合入、重新生成集成 diff 并复审。合入时用 `gh pr merge --match-head-commit <已审-head-oid>` 约束 head。
   - **能力界定与延期（声明，非本文新增操作门）**：`--match-head-commit` 只绑 head，**不构成 base OID / merge-group 的机器绑定**。R0 段按计划书 §16 一次只推进一个施工 PR（真实施工串行、无并行 base-race；R0-D 依 §10 M1C 仍须跑 module-impact 必需套件里的 merge queue 验证——含模拟并行冲突与 merge-HEAD 重跑,那是合成测试场景、强制退出条件,与本节"真实合入串行"不冲突,细节以计划书/T3 为准）。**并行轨（M2∥M3）的安全并行合并编排与原子 head/base/merge-group fail-closed 绑定，属 R0 后 §9.1 专门批次的交付物，尚未建成;本文档不宣称其存在**——到达 M2∥M3 阶段时按该批次是否就绪裁定能否并行，未就绪则退化为串行推进。

## 四、给每个施工会话的固定开工输入

每个批次开一个独立会话，输入固定为三件：

1. 重构计划书对应小节（含该批次退出条件原文）；
2. 涉及模块的 `AGENTS.md`（`receiptRequired` 机制会强制核对）；
3. 上一批次的验收证据（门禁输出 + 外审结论）。

会话产出合入前必须走"批次收尾"外审；推送目标锁定 `dps2` 远程。
