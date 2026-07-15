# Docs 目录规则

## 目标

文档必须帮助开发者判断三件事:

1. 当前代码真实支持什么.
2. 目标架构准备变成什么.
3. 一项能力经过了哪一级验证.

不再通过固定文件白名单或隐藏工作流目录限制文档演进.

## 目录职责

```text
Docs/
├── Architecture/                 # 目标架构和长期技术决策
├── Platforms/                    # 平台特定行为和验证证据
├── ConfigGuide_配置指南.md        # 当前配置和部署指南
├── EngineeringStandards_工程标准.md
├── GitWorkflow_Git工作流.md
├── PlatformTemplate_平台模块模板.md
├── TechManual_技术手册.md         # 当前实现参考
└── README.md                      # 文档索引
```

后续可按需要新增 `Testing/`, `Operations/`, `Security/` 等主题目录. 新文档必须是长期有价值的工程资料, 不能只是一次任务过程记录.

## 状态标签

架构和平台文档必须分开标注“文档状态”和“证据状态”. 文档状态使用以下之一:

- `Current`: 已在当前源码中实现并通过相应验证.
- `Proposed`: 目标设计, 尚未进入生产主链.
- `Experimental`: 已有代码或试验, 但未通过完整门禁.
- `Deprecated`: 仍可见但不应继续扩展.
- `Removed`: 已从当前版本移除, 只在 Git 历史中保留.

证据状态只能是 `NONE`, `NOT_VERIFIED`, `REPOSITORY_STATIC_VERIFIED`, `CONTRACT_VERIFIED`, `INTEGRATION_VERIFIED`, `WINDOWS_VERIFIED`, `DEVICE_VERIFIED`, `CANARY_VERIFIED` 或 `SCALE_VERIFIED`, 并且必须指向当前可读的原始证据. `Current` 只描述文档覆盖的实现面, 不会自动升级证据等级. 历史报告可保留当时的输出, 但原始证据缺失、`SKIP/PARTIAL` 或现行门禁不满足时必须标为 `NOT_VERIFIED`.

设计文档不能使用未来时能力冒充当前已支持能力.

## 写作要求

- 优先使用简体中文, 专有名词保留英文.
- 路径, 字段, API 和命令必须与仓库真实内容一致.
- 代码示例必须注明是生产代码, 伪代码还是提案.
- 性能数字必须注明测量环境和证据. 未测量时不得写具体提升百分比.
- 外部产品事实要链接官方资料并记录核验日期.
- 涉及 ZennoDroid 的结论必须区分静态审计和 Windows 真机验证.
- 涉及 GBrain 的结论必须区分 health, write, read-back, search, embedding 和 source isolation.

## 文件命名

- 面向用户的文档可以使用 `English_中文.md`.
- 标准约定文件可以使用 `README.md`, `SECURITY.md` 等通用名称.
- 文件名应稳定, 避免加入临时日期或版本号. 版本变化写在正文和 Git 历史中.

## 变更规则

- 架构变化同步更新 `Docs/Architecture/` 和 `CHANGELOG.md`.
- 运行行为变化同步更新 `TechManual_技术手册.md` 或对应平台指南.
- 新文档加入 `Docs/README.md` 索引.
- 删除文档前先修复所有活动链接. 历史 `CHANGELOG.md` 引用不必改写.
- 一次性审计和临时计划保留在任务或 Pull Request 中, 不写入仓库隐藏目录.
