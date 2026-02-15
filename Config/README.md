# Config 配置文件

## 概述

本目录包含 DPS v4.5 的所有配置文件。

## 文件列表

| 文件 | 说明 |
|------|------|
| AIConfig.json | AI服务配置（API Key、模型、超时） |
| StageConfig.json | 7阶段行为参数 |
| BehaviorConfig.json | 通用行为参数 |
| ExtensionsConfig.json | 扩展属性配置 |
| EvolutionRules.json | 画像进化规则 |
| ValidationRules.json | 画像验证规则 |
| Apps.json | APP定义（Reddit、BabyCenter） |
| PersonaPrompt.txt | 画像生成母模板 v2.8 |

## 配置优先级

1. ZD变量中的配置 (最高)
2. 本目录的配置文件
3. 代码中的默认值 (最低)

## 修改配置

1. 使用文本编辑器打开对应 JSON 文件
2. 修改需要的参数
3. 保存文件（确保 UTF-8 编码）
4. 重新运行项目生效

## AI 密钥配置（Git 协作）

- 共享模板：`AIConfig.template.json`
- 本地私有：`AIConfig.json`（包含真实 API Key，不进入 Git）

## 注意事项

- JSON 文件必须是有效的 JSON 格式
- 不要删除必需字段
- API Key 需要替换为你自己的密钥
- 修改后建议保留备份
