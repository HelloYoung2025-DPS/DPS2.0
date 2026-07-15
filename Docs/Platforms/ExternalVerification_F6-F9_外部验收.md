# F6-F9 外部环境硬验收

Validator readiness: `IMPLEMENTED`

External evidence status: `WAITING_EXTERNAL`

Formal repository verification level: `NONE`

No Windows, device, canary, or scale evidence has been issued by this repository.

## 信任链

外部门禁有四层独立真相:

1. Release BOM 的原始文件、SHA-256、模块候选摘要和外部 BOM 签名.
2. Windows、真机或生产 fleet 生成的不可变原始制品及其 size/hash.
3. 外部 runner 对完整输入的 P-256 签名和精确环境指纹.
4. Factory 的 upgrade stream、instruction receipt 与实现者、证据签发者、发布批准者三角色分离.

可信 issuer、公钥路径、公钥摘要、允许签发范围和环境声明由部署时只读
trust policy 注入. 仓库不保存生产公钥列表的当前值，更不保存私钥. 输入中
不允许声明 `verification_level`; runner 根据 `f6` 到 `f9` 的阶段和硬阈值计算
目标等级. 本地 validator 通过只表示
`ELIGIBLE_FOR_EXTERNAL_ISSUANCE`，不会产生签名证据.

`environment` 不是自由扩展字典：F6 必须完整固定 Windows、ZennoDroid、
.NET Framework、C#、CodeDom、GAC、DLL/Zenno project load、ADB、Bridge ABI、
loopback host/port/fixedness、timeout/error semantics 和 connection continuity；F7 固定
Windows+Android、GBrain deployment、parent Windows、Edge/Zenno installation 以及
runner component/version/binary/SBOM；F8-F9 只允许
`environment_id`、`os_family`。对应 trust policy 必须逐项固定同一组键和值，
不能扩展 `runner_note` 等自由字段；每个值还要
符合字段专用格式。嵌套值、自由文本、scope/credential 形状、
secret/key/token/password、Bearer/JWT 等一律失败关闭.

## 进入 F6 前置条件

- 候选变更已形成受保护、可追溯的精确提交.
- Clean-checkout Phase 0 已由受保护工作流重测.
- 适用的 Contract/Integration 证据已由外部独立签发者签发.
- 候选门禁已从当前全部 Manifest 重建 Integration 清单，且累计覆盖缺口为零；任一缺口、`SKIP`、`PARTIAL`、缺证据或基础设施错误均阻断发布.
- SessionRunner 到 Bridge/Edge 的真实迁移路径已由目标 Windows 能力探测确认.

这些条件当前均未形成完整外部证据，因此不得直接进入生产 F6-F9 发布.

## F6 Windows 与 ZennoDroid

必须在目标 Windows/ZennoDroid 环境采集:

- Windows 与 ZennoDroid 精确版本、.NET Framework 与 C# 精确版本能力探测.
- CodeDom 编译、GAC resolution、DLL load 与 Zenno project load 全部 `PASS`.
- ADB 至少一台授权设备且授权探测为 `PASS`; Bridge ABI 必须为版本化
  `dps.zenno-bridge/vN`.
- Bridge 只能使用固定 `127.0.0.1:<bounded-port>`；端口稳定性、最长 300 秒命令
  timeout、`FAIL_CLOSED` timeout semantics、`NATIVE_ERROR_PRESERVED` error
  semantics与连接连续性必须显式通过.
- 至少 100 次交替 A→B/B→A 安装、自检、Shadow、排空、切换和回滚.
- Shadow 真实副作用为零，未知合同和未知 step 均拒绝.
- Crash window、重复投递、离线恢复全部为 `PASS`.
- 连续观察至少 24 小时，回滚最多五分钟.
- ZennoDroid 的 PID 和 process start time 在前后完全相同；两次 process
  observation 必须精确落在签名 measurement window 首尾，未来启动时间失败关闭.

缺 Windows 或原始证据返回 `WAITING_EXTERNAL`; PID/start time 改变、任意
`SKIP/PARTIAL`、签名或 BOM 不匹配均返回 `FAIL`.

## F7 两部非生产手机与 GBrain

2026-07-15 状态：F2 独立审计已使当前 projection/source-binding 候选哈希
`STALE`。现有哈希只用于检测漂移，不是冻结声明，也不能支撑
`DEVICE_VERIFIED`。须等 F2 修复、独立复审并重新冻结后，F7 才能重新绑定。

F7 只接受 `dps.device-gbrain-verification-input/v3`；v1、v2 都是历史记录。
进入前必须验证一个当前、未撤销、独立签名的 F6 `WINDOWS_VERIFIED` receipt。
当前 trust policy 还必须独立固定原始 F6 evidence/environment SHA-256、测量窗口、
Edge 安装和 Zenno 安装。F6 receipt issuer、F7 device issuer、Release BOM signer
使用三把不同公钥。BOM 在同一 runner 模块项同时固定版本、binary 与
SBOM；producer、
Windows+Android/GBrain/parent-Windows/Edge/Zenno 环境必须逐制品完全一致。

必须使用两部自有或明确授权的非生产真机、两个 Soul、两个平台身份和两个
GBrain Source。两个 `soul_id`、`device_binding_id`、`platform_account_id`、物理设备
attestation、OAuth client、credential lease 和 token fingerprint 必须各自唯一。
不得保存 token 原文。重新冻结后，Source ID 使用
`gbrain.source.binding/v1` 算法：

```text
dps- + first28hex(
  SHA256(
    ASCII("dps.gbrain-source-binding/source-id/v1\0")
    || complete ASCII soul_id
    || NUL
    || signed int64 big-endian nonce
  )
)
```

nonce 只能为 0..1023。门禁重算完整 Soul hash、Source ID、binding revision、binding
checksum 和 canonical bytes；旧的 `dps- + first28(soul body)` 截断映射失败关闭。
projection 只接受重新冻结的 `gbrain.projection/v2`，并要求其中的 Source
binding nonce/revision/checksum 与独立 binding bytes、OAuth whoami 返回的真实
GBrain Source ID、adapter alias、Search readback 完全一致。只证明 alias 不足以放行。
`gbrain.projection/v1` 仅 `quarantine-only`，不能满足 F7。

执行时间线必须连续且严格按 Observe → Verify → MemoryEvent → Interest → GBrain
projection → exact read-back → delete/rebuild。每个原始制品绑定同一 run、trace、
Release BOM、scope digest、phase 和 capture window。两个 projection 与两个 Search
制品分别保存并校验 canonical request/write/readback bytes；projection checksum 按
冻结 v2 C# canonicalizer 的字段顺序、UTC 和数组排序重算。Search 结果 freshness
最多 300 秒，每一条结果重新核对 Soul、Source、schema、provenance、revision 和
checksum，不能相信缓存、自报摘要或同 Soul 的陈旧结果。

另需恰好 24 个 semantic artifacts：九类 per-Soul 证明各两份，以及
CROSS_SOUL/CROSS_DEVICE/CROSS_ACCOUNT 各两个方向。每份都包含分别 hash-bound 的
canonical request、response、postcondition bytes，并校验各类证据的 outcome。门禁从绑定
command、idempotency key、scope、attack 和 audit ID 的具体 records、native
receipts、audit events、side-effect receipts、purge rows 和 exact reads 推导结果，
不接受 runner 自报 count 或 `DENY` 字符串。每种跨域攻击只允许改变命名的一个轴，
其余轴保持不变，并必须使用 actor Soul 的已验证 OAuth lease/token fingerprint。

Persona current 必须固定 slug 精确读回；export/deletion scope hash 必须重算；
delete/rebuild 必须连接当前 projection revision/checksum 并验证 page、chunk、
embedding、cache、backup 全部清除后精确重建；duplicate delivery 必须复用 fixture
command/idempotency key；`UNKNOWN_OUTCOME` 必须使用不同命令且只能 exact read
reconcile，禁止盲重试。任何跨 Soul/设备/账号泄漏、未授权或重复副作用、假成功、
spoken 早于已验证 postcondition、混合 run/BOM/环境、额外制品或未知 major 都失败关闭。
所有原始 bytes 属于外部敏感证据，不能进入 Git 或日志。

## F8 三十台生产灰度

波次严格为:

```text
simulator → shadow → test_soul → 1 → 3 → 8 → 15 → 30
```

波次不得重排或重叠. 1、3、8 台各至少 2 小时和 500 条命令；15 台至少
8 小时；30 台至少 24 小时. 同时最多两个已证明互不依赖的模块. 30 台必须
全部可追踪且能查询精确 BOM.

以下计数必须为零: 跨 scope 泄漏、未授权或重复副作用、假成功、未知合同
被接受、Shadow 真实副作用、Zenno 意外重启、审计链缺口. 自动回滚技术阈值
保持主计划数值：连续健康失败最多 2 次，5 分钟错误率增量不超过 2 个百分点
且不得达到稳定版 2 倍，10 分钟 p95 不超过 1.5 倍，增长 backlog 最老记录
不超过 120 秒，GBrain 投影延迟不超过 300 秒. 真实回滚演练必须 `PASS` 且
不超过五分钟.

## F9 两百台与规模

扩容波次严格为 `2 → 10 → 20 → 50 → 100 → 200`. 必须分别提供不同 run ID
和不同 `raw_artifact_id + SHA-256` 的
`dps.f9-load-run-artifact/v1` 原始 JSON 制品:

- 100 台真实持续并发，覆盖至少 72 小时.
- 200 台真实短时并发并自动消除积压.
- 400 台明确标为 `SIMULATED` 的独立两倍容量测试.

每个负载制品必须保存 scoped-HMAC actor set、连续且每段不超过 300 秒的
观测窗口和恢复采样。门禁从原始 bytes 重算真实设备并集、每窗口并发、
首尾时间、精确时长、72 小时覆盖、每窗口最老积压不超过 120 秒，且相邻窗口
不得显示未解决积压单调增长；结束后
120 秒内积压清零及清零后 5 分钟
稳定性。首个 recovery sample 必须在同一时刻与最后窗口的 depth/age tuple
完全一致，且 recovery age 仍不得超过 120 秒。仅有 marker bytes 或自述
summary 不得通过.

还必须有至少两个 Control Plane 实例、200 台注册管理、GBrain 200-device
容量模型、Factory/Control Plane/Edge Worker 强制崩溃恢复、72 小时零泄漏/
未授权/重复/假成功稳定性证据. PostgreSQL 备份恢复必须记录 declared 与
measured RPO/RTO，实测不得超过目标. Site、database、Edge、GBrain、module
五种回滚都要分别 `PASS`; 上一稳定 BOM、制品和兼容 schema 可用. Legacy
adapter 只能是已退休或有明确清单的 compatibility-only.

F9 进入前必须绑定一个真实 F8 `CANARY_VERIFIED` receipt 原始制品，不能用
`canary_passed=true` 等自述布尔值代替。门禁验证 receipt 的 P-256/P1363 签名、
可信 CANARY issuer、F8 stage、`PASS`、baseline commit，并要求 receipt 中的
Release BOM id、BOM 原始 bytes SHA-256 和 candidate digest 与 F9 正在验证的已签名
BOM 完全相同。

200 台阶段最多四条 module rollout line。Release BOM 必须绑定精确
integration commit、全部 module Manifest SHA-256、canonical dependency DAG 和 compatibility
matrix。门禁读取所有 BOM-hashed raw Manifest，重建依赖 edge、parallel wave、合同
owner/consumer 和隐含依赖，再与 BOM 绑定的两个 raw snapshot 精确比较。不能
信任 evidence 自报的 DAG；缺 edge、伪造 edge、Manifest 摘要不一致、隐藏合同依赖、
环或波次不一致均失败关闭。门禁使用重建图计算传递依赖，任意两条 line 之间
存在直接或间接依赖即失败。`module` rollback drill 必须不超过五分钟；其他
scope 不能替代该证明.

## 运行与退出语义

统一入口见 `Tools/verification/run_external_gate.py`:

- 无输入、外部 trust policy、公钥、OpenSSL 或原始制品: `WAITING_EXTERNAL`, exit 3.
- Mock/Hosted/模拟冒充、错误 issuer/key/scope/platform、摘要或签名错误、波次或
  阈值不合格、任意 required 非 `PASS`: `FAIL`, exit 1.
- 所有外部事实通过: `PASS`, exit 0, 但只输出可供独立签发者审查的资格，
  `evidence_receipt_issued` 永远为 `false`.
