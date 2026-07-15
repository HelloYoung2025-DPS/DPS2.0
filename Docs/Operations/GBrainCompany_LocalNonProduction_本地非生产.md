# GBrain Company 本地非生产运维

> 文档状态: `Experimental`
>
> 证据状态: `NOT_VERIFIED`
>
> 外部产品信息核验日期: 2026-07-14

本文档定义 DPS 在本机非生产环境中使用 GBrain Company 的最小安全边界. 它不是 F7 验收证明. 只有受信外部 Runner 提交的写入、精确读回、删除/重建、Source 隔离和两部授权非生产手机证据, 才能申请 `DEVICE_VERIFIED`.

## 边界

- 使用独立 `GBRAIN_HOME`, 不复用个人 GBrain 配置.
- 使用 PostgreSQL 18.4, `pgvector` 和 `pg_trgm`; 本机开发数据库只监听 loopback.
- 通用 Soul 记忆 embedding 使用 `voyage:voyage-4-large`, 1024 维. `voyage-code-3` 只用于代码检索.
- Voyage 凭证只由进程环境或受控密钥注入器提供. 不进入 Git, GBrain page, 日志, 截图, 测试证据或命令示例.
- DeepSeek 不得作为 embedding provider. 它若以后用于 chat/expansion, 仍必须独立通过模型、成本、数据处理和权限评审.
- GBrain 不保存命令 lease、审批、速率预算或手机动作成功真相.

## 初始化

下列命令是运维模板, 不得将密钥直接写入 shell history. `GBRAIN_DATABASE_URL` 在本地非生产环境可为无密码 loopback DSN. 生产 DSN 必须在运行时从密钥管理器注入.

```bash
export GBRAIN_HOME=/absolute/path/to/isolated-gbrain-home
export GBRAIN_DATABASE_URL='postgresql://local-user@127.0.0.1:55434/dps_gbrain_company'
# VOYAGE_API_KEY 由受控密钥注入器提供; 不要 echo.

gbrain init \
  --url "$GBRAIN_DATABASE_URL" \
  --non-interactive \
  --embedding-model voyage:voyage-4-large \
  --embedding-dimensions 1024 \
  --schema-pack gbrain-base-v2 \
  --json
```

初始化后必须由操作者在 `conservative`, `balanced`, `tokenmax` 中明确选择 `search.mode`, 并保存成本假设. 自动化不得默认接受 CLI 的暂定值.

## Soul 与 Source 映射

- 每个 Soul 使用独立 Source.
- Source ID 不得包含邮箱、手机号、平台账号或其他可识别信息.
- GBrain 原生 Source ID 由 `gbrain-projector` 唯一生成为 `dps-<28-lowercase-hex>`，共 32 字符；它是 OAuth 写入绑定和精确读回使用的 `logical_source_id`，不得由适配器重新派生.
- F7 外部证据另用 `gs_<16-hex>` 非 PII 短别名：取 `SHA-256("dps-gbrain-external-source/v1\n" + logical_source_id)` 的前 16 位小写十六进制。该别名只用于证据对照，绝不得当作 GBrain 原生 Source 或 OAuth 绑定值；验证器必须重算并检查唯一性.
- Source 和证据别名都不得直接使用 UUID、邮箱、手机号、平台用户名或账号原文.
- OAuth client 只获得自己 Source 的 `read write`; 不授予 `admin`, 不跨 Soul federated read.
- client secret 只显示一次, 必须直接进入权限为 `0600` 的密钥存储, 不得进入 AI 会话输出.

## 本机 HTTP MCP

只有搜索模式、Source 和 OAuth 策略获得确认后才能启动:

```bash
gbrain serve --http --port 3131 --bind 127.0.0.1 --token-ttl 3600
```

本地阶段禁止 `0.0.0.0`, 公网 tunnel, 动态客户端注册和完整参数日志. 公网部署必须另行完成 TLS, reverse-proxy trust, CORS, rate limit, OAuth issuer, 管理员 bootstrap token 和秘密轮换评审.

## 验收阶梯

1. `doctor` 和模型探针只证明服务可达.
2. 写入后精确读回必须重新验证 `soul_id`, Source, Schema, logical revision 和 checksum.
3. Persona current 使用确定性读取, 不使用语义搜索决定当前人格.
4. 两个合成 Soul 必须通过双向跨 Source 拒绝测试.
5. 必须演练导出、纠正、删除、重建和缓存/备份保留策略.
6. Mock, 离线 fixture 和本机健康检查不得升级为 GBrain 实连或真机证据.

官方参考: [Company Brain tutorial](https://github.com/garrytan/gbrain/blob/master/docs/tutorials/company-brain.md), [Embedding providers](https://github.com/garrytan/gbrain/blob/master/docs/integrations/embedding-providers.md), [Install for agents](https://github.com/garrytan/gbrain/blob/master/INSTALL_FOR_AGENTS.md), [HTTP MCP deployment](https://github.com/garrytan/gbrain/blob/master/docs/mcp/DEPLOY.md).
