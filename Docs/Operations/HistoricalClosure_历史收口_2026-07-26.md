# 历史收口文书（2026-07-26）

> 文档状态: `Current`（记载已发生的事实与已作出的处置）
>
> 证据状态: **B-1、B-2、B-3 一律 `NOT_VERIFIED`**——三条的**结论**都至少有一个关键前提来自 GitHub 平台托管的可变载体（B-1 的 reviews 记录数、B-2 的时间线事件序列、B-3 的 `GOV_STATUS: BLOCKED` 与两次 `FINAL_VERDICT: BLOCK`），其原始字节按项目边界存于仓外、仓内不可读，依 `Docs/DOCS_RULES.md:39` 必须如此标注。其中 B-3 的**子事实**「merge commit 与被 BLOCK head 的 tree 逐字节相同」是纯 Git oid，任何 checkout 离线可独立复验；但「这些字节是在 BLOCK 状态下被合入的」这一结论不能由 Git 单独支撑，故不因该子事实升级整条的证据状态。
>
> 上游依据: Owner 2026-07-26 决策（三条均追认、追认≠认可、均不回退）

`NOT_VERIFIED` 标注的是**证据在仓内的可读性**，不是对事实真伪的怀疑，也不改变 Owner 的处置：处置是一项决定，不是一项证据主张。任何有仓库读权限的人都可用下文逐条给出的 `gh` 命令向 GitHub 重新取证；但须注意，**后来的一次取证只能证明后来的平台状态，不等于复现了 2026-07-26 当时的返回**——它是另一份另有日期的证据，不构成对本文书历史快照的升级。

## 文书性质

- 本文书是对三条历史程序缺陷（编号 B-1、B-2、B-3）的**追认记录**：记载事实与 Owner 的处置，不创设规则、不约束未来审计（见「效力」节）。
- 授权来源：Owner 2026-07-26 决策——废止案拆两份分开签，本文书为其中的历史收口部分（PR-A）；三条均追认，追认不等于认可当时做法，不回退任何一条。
- 取证方法：下述全部事实均于 2026-07-26 通过 `gh` 对仓库 `HelloYoung2025-DPS/DPS2.0` 的 GitHub API 实查取得（`gh pr view`、`gh api .../pulls/{n}/reviews`、`gh api .../issues/{n}/comments`、`gh api .../issues/{n}/timeline`），字节比对通过本地 `git`（`git cat-file -p`、`git rev-parse ^{tree}`、`git diff --name-status`）完成。所有时间戳为 GitHub API 返回的 UTC 原值。全部 GitHub 侧原始返回已于同日留存为不可变快照并逐份记哈希，见下文「取证快照」节。
- 本文书只记载历史，不创设任何施工义务、门禁检查项或待办事项，也不要求在本文书之外再做任何动作。它不含规范性条款：其全部内容是事实记载与 Owner 已作出的处置，合入后不产生任何到期义务。

---

## B-1：PR #15 的外审 veto 以同账号自述评论标记 CLOSED，GitHub reviews API 无任何 review 记录

### 发生过什么

1. PR #15（https://github.com/HelloYoung2025-DPS/DPS2.0/pull/15 ，标题 `ci: close R0-B protected legacy anchor veto`，作者 `HelloYoung2025`，创建于 2026-07-24T09:46:52Z）的目的即是关闭 R0-B 批次外审 veto（原 review job `review-mrsncfcx-h0jg2y` 的 R0-B HIGH）。
2. 该 veto 的 CLOSED 裁决在 GitHub 上的载体只有 issue comment，共两次、均为同账号评论自述：
   - 较早一次：https://github.com/HelloYoung2025-DPS/DPS2.0/pull/15#issuecomment-5071973874 （id `5071973874`，发布者 `HelloYoung2025`，2026-07-24T16:21:56Z）——自述 review job `review-r0b-pr15-a5996c1-opus48-01`，result SHA-256 `eb1f562e114da300b1eb92a6031a9c9eb3aa91c7671ec6c2f955ed9e7ac5a8cb`，`ORIGINAL_HIGH_DISPOSITION: CLOSED`，绑定较早 head `a5996c185ce2176b565c3d31e2f9a9bb3df8e970`（该 head 因新增 17 项非通过记录被同评论自判 DO NOT MERGE，后被替换）。
   - 终局一次（对合入 head 生效的载体）：https://github.com/HelloYoung2025-DPS/DPS2.0/pull/15#issuecomment-5075770062 （id `5075770062`，发布者 `HelloYoung2025`，2026-07-25T00:14:27Z）——自述 review job `review-r0b-pr15-e671a8f-opus48-01`，reviewer provider `Anthropic`、profile `anthropic.claude-opus-4-8@2`，result SHA-256 `fac290abf4fb3a42eb1c92876eed15a0afbb379bc34f2df08747e3308480dcdc`，结论 `ORIGINAL_HIGH_DISPOSITION: CLOSED`（绑定 exact head `e671a8faafbdd4524b9ced42bf509264e196a851`，即最终合入的 head）。
3. 实查 `gh api repos/HelloYoung2025-DPS/DPS2.0/pulls/15/reviews`（2026-07-26 执行），返回值为 `[]`——PR #15 在 GitHub reviews API 中的 review 数为 **0**。不存在任何 GitHub review 对象承载该 CLOSED 裁决。
4. 实查 `gh api repos/HelloYoung2025-DPS/DPS2.0/issues/15/comments`：PR #15 全部 7 条评论（2026-07-24T09:51:19Z 至 2026-07-25T00:14:27Z）均由 `HelloYoung2025` 发布——与 PR 作者、合入者为同一账号。CLOSED 裁决的全部证据（job 名、result SHA、verdict）均为该账号评论内的自我陈述，无任何第三方账号、bot 或 GitHub review 机制的独立落痕。
5. 程序缺陷定性：外审 veto 的关闭裁决没有以可独立核验的 GitHub review 记录落地，而是以被审查方同一账号的评论自述替代。评论中自报的 result SHA（`fac290ab…`）指向平台外的本地文件，GitHub 侧无法核验其存在与内容。

### 处置

- **追认**：`review-r0b-pr15-e671a8f-opus48-01` 对原 R0-B HIGH 的 `ORIGINAL_HIGH_DISPOSITION: CLOSED` 裁决，以评论 `5075770062` 所载内容为准，追认为既成事实。该 veto 视为已关闭。
- **追认不等于认可当时做法**：以同账号评论自述替代可核验的外审落痕，是程序缺陷。本追认只覆盖裁决结果本身，不认可、不背书这种记录方式，也不将其确立为任何形式的先例。
- **不回退的理由**：回退意味着重新打开一个已被后续仓库状态（PR #15 合入及其后全部提交）实际吸收的 veto，需要重建审查对象、重跑外审、重定基线——这只会制造新的治理工作，不产生任何新的事实增量。依 Owner 2026-07-26 决策，不回退。

---

## B-2：PR #15 在无任何可查的事前 Owner 授权记录的情况下被合入，其自身评论明写「不得标记 Ready、不得合入」

### 发生过什么

1. PR #15 的最后一条评论（https://github.com/HelloYoung2025-DPS/DPS2.0/pull/15#issuecomment-5075770062 ，2026-07-25T00:14:27Z）明写冻结条件（逐字）：「下一步只能由 Owner 对 exact head `e671a8faafbdd4524b9ced42bf509264e196a851` 单独作出明确合入授权；在该授权前不得标记 Ready、不得合入、不得重跑 T4。」
2. 实查 `gh api repos/HelloYoung2025-DPS/DPS2.0/issues/15/timeline`（2026-07-26 执行）所得完整时间线：
   - 2026-07-25T00:14:27Z — 上述冻结评论发布（`HelloYoung2025`）。
   - 2026-07-25T04:05:17Z — `ready_for_review`（操作者 `HelloYoung2025`）。
   - 2026-07-25T04:07:17Z — `added_to_merge_queue`（操作者 `HelloYoung2025`）。
   - 2026-07-25T04:07:34Z — `merged`（merge commit `dd810e48f4c46684f6afeb510c1c6a1169251e1a`，`merged_by` = `HelloYoung2025`）；同刻 `github-merge-queue[bot]` 执行 `removed_from_merge_queue`；同刻 `closed`。
3. 冻结评论与 `ready_for_review` 之间（00:14:27Z → 04:05:17Z，间隔 3 小时 50 分 50 秒），GitHub 时间线上**无任何事件**：无评论、无 review、无任何授权记录。即：冻结条件所要求的那种「在标记 Ready 之前、针对 exact head 单独作出的明确合入授权」，在 GitHub 上不存在任何可查记录。
4. `ready_for_review` 到 `merged` 的间隔为 **2 分 17 秒**；合入通道为 **merge queue**（`added_to_merge_queue` → 17 秒后 merge，merge commit `dd810e4…` 为 GitHub 侧生成的双亲合并提交，父提交 `763281e0def8e72244a34981ea5d0d55ef762309` 与 `e671a8faafbdd4524b9ced42bf509264e196a851`）。
5. 此前两轮审计对此段的记录有出入（一说「2 分 17 秒」，一说「4 小时 / merge queue」）。以本文书实查时间线为准，两说各自描述了同一时间线的不同片段：「2 分 17 秒」对应 `ready_for_review` → `merged`；「约 4 小时」对应冻结评论 → `ready_for_review`（3 小时 50 分 50 秒）；合入通道确为 merge queue。数据来源：`gh api repos/HelloYoung2025-DPS/DPS2.0/issues/15/timeline` 与 `gh pr view 15 --json mergedAt,mergedBy,mergeCommit`，2026-07-26 执行。
6. 程序缺陷定性：需要区分两件事。其一，`ready_for_review`、`added_to_merge_queue`、`merged` 三个操作均由 `HelloYoung2025` 账号（即 Owner 本人账号）亲自执行且 GitHub 可见——合入的**意思表示**本身有平台落痕，并非平台外黑箱。其二，冻结评论要求的不是合入操作本身，而是一份**先于 Ready、针对 exact head 的单独明确授权记录**；该事前记录不存在（见第 3 条）。因此 B-2 的缺陷精确定性为：以 Owner 账号的直接合入操作，替代了其自设冻结条件所要求的事前单独授权程序——缺的是事前授权记录，不是操作者身份或操作可见性。

### 处置

- **追认**：PR #15 于 2026-07-25T04:07:34Z 经 merge queue 合入 `main`（merge commit `dd810e48f4c46684f6afeb510c1c6a1169251e1a`），追认为既成事实。**本追认的效力面严格限于此**：其后基于该 merge commit 的提交，不因「PR #15 缺事前授权记录」这一条而无效。它不对任何后代提交作任何其他背书——每一个后代提交自身的门禁结论、外审结论、证据充分性与缺陷认定，一律按各自现行机制独立成立或独立不成立，不受本追认影响，也不得以本追认为由压制、关闭或降级对后代提交的任何发现。
- **追认不等于认可当时做法**：在自设冻结条件未经可查授权解除的情况下标记 Ready 并合入，是程序缺陷。本追认只覆盖合入这一结果，不认可「先自我冻结、再无记录解冻」的操作方式，也不将平台外口头授权视为可接受的授权形式。
- **不回退的理由（据实计算，不夸大）**：回退的技术形态是 `git revert -m 1 dd810e48f4c46684f6afeb510c1c6a1169251e1a`，即**追加一个反向提交**；它不删除、不失效任何后代提交。实测的区间事实（**区间闭端为 `af4edab6…`，即 2026-07-26T02:20:20Z 合入 PR #17 之后**）：`tree(dd810e48…)` = `tree(f612b253…)` = `tree(65c2f5fe…)` = `tree(af4edab6…)` = `49a542eb9c8ef5fc1494cd6aee6cc1515a067fe8`——在该区间内 `main` 的树未变过一个字节，其间三次合并（PR #16、#17 与其锚点提交）均为零 diff。因此「回退会连带推翻其后全部已验证状态」在该区间内不成立，本文书不采用该说法。

  该区间之后 `main` 继续前移：同日 02:49:25Z 至 02:51:27Z 合入 PR #18、#19、#20（清 required 红与延长 artifact 保留期），共改动 7 个文件，`main` 至 `1d202a582e49758328aeb30ab44cdcfb8fcbe75a`。以该提交为基线重算回退影响（实测，非推断）：这三个 PR 与 PR #15 所改的 6 个文件有且仅有一处重叠——`.github/workflows/static-ci.yml`（PR #20 改其 `retention-days`）。在 `1d202a58…` 上试跑 `git revert -m 1 --no-commit dd810e48…`：**exit 0**，`static-ci.yml` 自动合并成功，无冲突；回退后 PR #20 的 `retention-days: 90` 幸存，受影响文件仍是 PR #15 原本那 6 个。故回退的实际影响与下段所述一致，不因这三次合入而增减。

  回退的**真实成本**是 PR #15 自身那 6 个文件（+1633/−40：`.github/workflows/static-ci.yml`、`Tools/ci/run_phase0_gate.py`、`Tools/ci/run_candidate_gate.py`、`Tests/ci/test_phase0_gate.py`、`Tests/ci/test_r0b_receipt_migration_dual_run.py`、`CHANGELOG.md`）所承载的能力：门禁接受 `.venv/bin/python -I` 声明、legacy baseline anchor 经受信通道注入消费。四条 legacy required 套件（`.static`、`.byte-baseline-adversarial`、`.fail-closed-p0`、`.wrapper-orchestrator-p0`）当前之所以在 hosted CI 中真实执行并 PASS，全部依赖这一批字节；回退即令其退回「parse 阶段即死、从未执行」的状态，并使此后各批次以其为基线取得的 CI 证据需重新取证。

  **定性**：因此 Owner 不回退的决定是**知情的风险接受**——明知 B-2 所载程序缺陷、并在计入上述真实成本后选择保留该合并的字节——而不是「后代提交会被摧毁」这一不实前提下的必然结论。依 Owner 2026-07-26 决策，不回退。

---

## B-3：PR #6 在 GOV_STATUS: BLOCKED、外审 FINAL_VERDICT: BLOCK、正文明写「保持冻结，不合入」的状态下被合入，且合入字节与被 BLOCK 版本逐字相同

### 发生过什么

1. PR #6（https://github.com/HelloYoung2025-DPS/DPS2.0/pull/6 ，作者 `HelloYoung2025`，创建于 2026-07-21T01:45:46Z）为 GOVERNANCE_CHANGE 文档修订（仅 2 个 Markdown 文件）。实查其 PR 正文（`gh pr view 6 --json body`，2026-07-26 执行），正文标题级声明为 `GOV_STATUS: BLOCKED_AUTHORITY_AND_REQUIRED_GATE_REVIEW`，并明写（逐字）：「在此之前本 PR 保持冻结，不合入，不在本 PR 内修复该基线红项。」
2. 实查 `gh api repos/HelloYoung2025-DPS/DPS2.0/issues/6/comments`：PR #6 共三条外审记录评论（均由 `HelloYoung2025` 以 verbatim 转录形式发布）：
   - 初轮（https://github.com/HelloYoung2025-DPS/DPS2.0/pull/6#issuecomment-5029452070 ，2026-07-21T02:15:54Z，job `review-mrtzsg76-enweyr`）：范围限定审查，verdict `approve`，但评论自身声明「仅作历史记录保留，不作为合入放行依据」。
   - 二轮（https://github.com/HelloYoung2025-DPS/DPS2.0/pull/6#issuecomment-5029503575 ，2026-07-21T02:24:54Z，job `review-mru0vmk4-3ecvg4`，无范围抑制）：`FINDING_DISPOSITION: F1=IN_SCOPE_BLOCKER; F2=IN_SCOPE_BLOCKER`，**`FINAL_VERDICT: BLOCK`**。
   - 三轮（https://github.com/HelloYoung2025-DPS/DPS2.0/pull/6#issuecomment-5029560144 ，2026-07-21T02:36:03Z，job `review-mru1box9-bkpfpm`，无范围抑制）：`FINDING_DISPOSITION: F1=已解决；F2=IN_SCOPE_BLOCKER`，**`FINAL_VERDICT: BLOCK`**，并明写结论「`FINAL_VERDICT` 仍为 `BLOCK`」「`GOV_STATUS` 保持 `BLOCKED_AUTHORITY_AND_REQUIRED_GATE_REVIEW`」。
   - 即：两轮无范围抑制外审的终局结论均为 `FINAL_VERDICT: BLOCK`，且在合入前从未被任何后续记录改判。
3. 实查 `gh api repos/HelloYoung2025-DPS/DPS2.0/pulls/6/reviews`：review 数为 **0**。
4. 实查时间线（`gh api repos/HelloYoung2025-DPS/DPS2.0/issues/6/timeline`）：三轮评论之后（2026-07-21T02:36:03Z 之后），时间线上直到 2026-07-22T05:00:14Z 的 `merged` 事件（操作者 `HelloYoung2025`，merge commit `7a0ed9c3b31fbd21922114ce49e3dcf84d41ef05`）之间**无任何事件**——无解冻声明、无改判记录、无授权记录。BLOCK 状态下静置约 26.4 小时后直接合入。
5. 字节一致性实查（本地 `git`，2026-07-26 执行）：
   - merge commit `7a0ed9c3b31fbd21922114ce49e3dcf84d41ef05` 的 tree = `659e360ed029c092c51d3f7808a7c6ca8d3823ce`，双亲为 base `80ccb340f0875a1625bd77372220c60f99f032dd` 与 head `0d31a02ffcbf5ae29a8145498be799262b2ef087`。
   - 被 BLOCK 的 exact head `0d31a02ffcbf5ae29a8145498be799262b2ef087`（即三轮外审绑定的 head）的 tree = `659e360ed029c092c51d3f7808a7c6ca8d3823ce`——与 merge commit 的 tree **完全相同**。
   - `git diff --name-status 0d31a02 7a0ed9c` 输出为空。合入 `main` 的字节与被 `FINAL_VERDICT: BLOCK` 判定的版本**逐字相同**，中间无任何整改提交。
6. 程序缺陷定性：在 PR 正文自我声明冻结、两轮无范围抑制外审均为 BLOCK、且无任何可查改判或授权记录的状态下，将与被 BLOCK 版本逐字相同的内容合入 `main`。

### 处置

- **追认**：PR #6 于 2026-07-22T05:00:14Z 合入 `main`（merge commit `7a0ed9c3b31fbd21922114ce49e3dcf84d41ef05`），其所载两份文档修订（F1 整改已经三轮外审确认解决）追认为既成事实。当时的 F2 阻塞项（required check 在精确 head 上 failure）系与该 PR 文件范围无关的既有基线红项，其定性与处置沿既有账本，不因本追认改变。
- **追认不等于认可当时做法**：在 BLOCK 与自我冻结声明未获任何可查解除记录的情况下合入逐字相同的字节，是程序缺陷。本追认只覆盖合入结果，不认可「以静置代替解冻程序」的做法，外审 BLOCK 结论的严肃性不因本追认而削弱。
- **不回退的理由**：PR #6 的内容本体（文档措辞修订）经三轮外审确认 F1 已解决，实体上无缺陷；回退将把一份实体正确、已被其后全部基线（含 §16 排定后续批次）引用的计划书措辞退回歧义版本，再走一遍完整修订与外审流程——回退制造新治理工作，不产生新的事实增量。依 Owner 2026-07-26 决策，不回退。

---

## 取证快照

上文全部 GitHub 侧事实断言的原始 API 返回，已于 2026-07-26 取证当时原样留存，逐份 SHA-256 如下。证据字节按项目既定边界存放于仓外（`Reports/` 被 `.gitignore` 是设计，证据不入 Git），路径 `dps2-evidence-archive/historical-closure-2026-07-26/`，同目录另存 `MANIFEST.sha256`：

| 文件 | 字节 | SHA-256 |
|------|------|---------|
| `pr15-reviews.json` | 2 | `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945` |
| `pr15-comments.json` | 34269 | `c92606e0403c7d6a5452d830c5b8473141ea4cdcc7bc73754dae4650888253c9` |
| `pr15-timeline.json` | 145953 | `b9ecfa5fadefd341156484d6f06b5ff403ced3e4a1efbed0dd11f3d9d32d402a` |
| `pr15-meta.json` | 20049 | `2498d58ea0ddc77d15eac86f9d824a05a0999346fb928d353079530a6c37ae57` |
| `pr6-reviews.json` | 2 | `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945` |
| `pr6-comments.json` | 16308 | `7739bd91509898d350ee64e981c95dabc865bfe0360ba0a76db77a5540e2226e` |
| `pr6-timeline.json` | 113404 | `1b24a462d614f2c133a0980a01c380368694171774795cac6966fbb48e35861f` |
| `pr6-meta.json` | 25656 | `5e7791520b9fb3daeae242292dee107cd82a991f8641bbc54646278b43ee7f51` |

两份 `*-reviews.json` 均为 2 字节 `[]`（`4f53cda1…` 即空 JSON 数组的 SHA-256），这正是 B-1 与 B-3 所述「reviews API 记录数为 0」的原始字节。Git oid 类事实（commit/tree SHA、`git diff` 空集）不依赖本快照，在仓库内可随时独立复验。

如实标注三条限制，不粉饰：

1. 快照由取证方（本文书起草会话）在 Owner 本机生成，不是第三方公证；它能证明「取证日期读到的就是这些字节」，不能证明 GitHub 侧当时的状态未被更早的编辑影响。
2. **干净 checkout 拿不到这些字节**，只拿得到上表的哈希。这是有意的取舍：项目硬规则是「证据不入 Git」，本文书选择服从该规则、不把 355643 字节的 API 返回塞进仓库，代价就是仓外快照对第三方不可直接检索。这个冲突不缝合——若 Owner 认为治理文书的引证应当例外入库，那是一次明确的规则修改，须另行决定，本文书不擅自开这个口子。
3. GitHub 侧事实的权威来源始终是 GitHub 本身，任何有仓库读权限的人都可用上文逐条给出的 `gh` 命令自行重新取证；快照的作用是**发现分歧**，不是替代重新取证。拿不到快照又无法向 GitHub 取证的审计者，按 `Docs/DOCS_RULES.md:39` 如实标 `NOT_VERIFIED` 即可——本文书不要求任何人把无法核验的事情说成已核验。

---

## 效力

**本文书是记录，不是规则。** 它记载三条历史程序缺陷的事实，以及 Owner 2026-07-26 对它们的处置：三条均追认、追认不等于认可当时做法、均不回退。它不创设任何新的治理规则，**也不约束未来的审计能否重新检视这三条**——需要重查的人尽管重查，事实、取证命令与快照哈希都在上文。本文书要做的只是让重查者不必从零开始，而不是禁止重查。（初稿曾写入「后续审计只引用本文书、不再重查重议」的终局性条款：它不在 Owner 的指令范围内，且把不可逆的治理结论建立在平台托管的可变证据上，反复产生新的治理争点，已整段删除。）

**不是门禁状态，不是豁免函。** 本文书不构成任何 required check、放行判定或证据验收的输入；任何现行或未来的门禁、审计、放行决定，其证据要求一律按各自现行机制独立满足，不得以引用本文书替代。本文书不豁免、不弱化任何现行门槛，也不为未来任何类似做法提供依据——三条所涉程序缺陷均已在上文逐条载明为「不认可」。

**事实可更正。** 全部事实断言均标注了取证命令与取证日期（2026-07-26）。Git oid 类事实（commit/tree SHA、`git diff` 空集）在仓库内可随时独立复验；GitHub 侧记录为平台托管的可变载体，其证据地位以取证日期读到的内容为限。日后若发现所引记录被编辑、删除或与此处记载不符，依当时可得的原始证据更正记载即可；若该更正足以推翻某条处置所依据的前提，连同证据提交 Owner 重裁。按 `Docs/DOCS_RULES.md:39`，原始证据当前不可读时审计者应如实标注 `NOT_VERIFIED`——不必、也不应因为本文书存在而声称已核验。

**签署与执行（如实记载，不粉饰）。** Owner 的处置决定于 2026-07-26 在施工会话内作出；同日 Owner 另行授权由执行方（AI 会话）代为执行合入动作。因此**本 PR 的合入是执行动作，不构成 Owner 的签署**：签署的载体是上述 Owner 决定本身，它记录在会话内，**不在本仓库或 GitHub 上可独立核验**。这与本文书 B-1、B-2 所定性的缺陷属同一类（授权记录不在平台上可独立核验）；Owner 在知悉该性质后仍作此决定，据实记为**知情的风险接受**，**不得引为先例**，也不改变 `AGENTS.md:76`「治理变更不得在同一次动作里批准自己」对未来批次的约束。本文书的事实部分在合入前经官方 Codex adversarial-review 绑定精确 commit 独立审查多轮，起草方与审查方不同。
