# DPS v4.5 项目架构结构图

本文档提供 DPS v4.5 项目的完整 L1-L4 层级结构图。

## 1. L1 项目级概览图

```mermaid
graph TB
    subgraph L1["L1: 项目层 - DPS v4.5"]
        A[项目元数据]
        B[架构信息]
        C[支持的平台]
        D[目录结构]
        E[项目级操作]

        A --> A1[名称: DPS v4.5]
        A --> A2[类型: C#自动化框架]
        A --> A3[版本: 4.5.8]
        A --> A4[总文件: 77, 总行数: ~24100]

        B --> B1[模式: Intent-Based Execution]
        B --> B2[DPS: 感知/记忆/决策]
        B --> B3[ZennoDroid: 定位/执行/异常]

        C --> C1[Reddit ✅]
        C --> C2[Instagram ✅]
        C --> C3[BabyCenter ✅]
        C --> C4[TikTok ⏳]
        C --> C5[Facebook ⏳]

        D --> D1[.omo/]
        D --> D2[Config/]
        D --> D3[Core/]
        D --> D4[Modules/]
        D --> D5[Platforms/]
        D --> D6[ZDProjects/]

        E --> E1[op-l1-001: 全局架构升级]
        E --> E2[op-l1-002: 性能全面优化]
        E --> E3[op-l1-003: 接入新平台]

        L1 --> L2["L2: 模块层 (32个模块)"]
    end

    style L1 fill:#e1f5ff
    style L2 fill:#fff4e1
```

## 2. L2 模块层分类图

```mermaid
graph TB
    subgraph L2["L2: 模块层 - 32个模块"]
        direction TB

        subgraph CoreTools["核心工具模块 (9个)"]
            M1[JsonHelper<br/>993行, 23方法]
            M2[CoreHelper<br/>470行]
            M3[FileHelper<br/>~300行]
            M4[AIService<br/>~400行]
            M5[ActionExecutor<br/>1401行]
            M6[SelectorEngine<br/>~300行]
            M7[PageDetector<br/>~250行]
            M8[RateLimiter<br/>~200行]
            M9[ExtensionManager<br/>~200行]
        end

        subgraph IntentSystem["Intent系统模块 (5个)"]
            I1[Intent<br/>168行]
            I2[ZDCommand<br/>333行]
            I3[ZDResult<br/>311行]
            I4[ZennoDroidAdapter<br/>408行]
            I5[IntentTranslator<br/>472行]
        end

        subgraph BusinessLogic["业务逻辑模块 (13个)"]
            B1[SessionRunner<br/>1492行]
            B2[RuleEngine<br/>663行]
            B3[MemoryManager<br/>782行]
            B4[PersonaCreate<br/>142行]
            B5[DailyUpdate<br/>219行]
            B6[WeeklyEvolve<br/>~400行]
            B7[StateSaver<br/>~200行]
            B8[ReportGen<br/>~300行]
            B9[Maintenance<br/>~250行]
            B10[Main<br/>307行]
            B11[Initializer<br/>211行]
            B12[UIHelper<br/>~350行]
            B13[Extension<br/>~150行]
        end

        subgraph Platforms["平台模块 (3个)"]
            P1[RedditModule<br/>603行]
            P2[InstagramModule<br/>~600行]
            P3[BabyCenterModule<br/>~500行]
        end

        subgraph ZDEngine["ZD脚本引擎 (5个)"]
            Z1[ScriptHelpers<br/>~400行]
            Z2[HumanizationEngine<br/>~300行]
            Z3[UILocator<br/>~350行]
            Z4[ErrorRecovery<br/>~300行]
            Z5[PlatformBase<br/>182行]
        end

        M5 --> I5
        B1 --> M5
        B1 --> B2
        B1 --> B3
        P1 --> M5
        P2 --> M5
        P3 --> M5
    end

    L2 --> L3["L3: 操作层 (51个操作)"]

    style CoreTools fill:#ffe1e1
    style IntentSystem fill:#e1ffe1
    style BusinessLogic fill:#e1f0ff
    style Platforms fill:#fff4e1
    style ZDEngine fill:#f0e1ff
```

## 3. L3 操作层流程图

```mermaid
graph TB
    subgraph L3["L3: 操作层 - 51个操作"]

        subgraph SessionRunnerOps["SessionRunner 操作 (7个)"]
            S1[op-sr-001: initialize-session]
            S2[op-sr-002: load-behavior-config]
            S3[op-sr-003: execute-action-sequence]
            S4[op-sr-004: apply-fatigue-model]
            S5[op-sr-005: handle-session-error]
            S6[op-sr-006: calculate-session-duration]
            S7[op-sr-007: validate-session-success]
        end

        subgraph RuleEngineOps["RuleEngine 操作 (6个)"]
            R1[op-re-001: parse-rule]
            R2[op-re-002: validate-rule]
            R3[op-re-003: evaluate-post]
            R4[op-re-004: decide-action]
            R5[op-re-005: calculate-hot-score]
            R6[op-re-006: calculate-relevance-score]
        end

        subgraph ActionExecutorOps["ActionExecutor 操作 (7个)"]
            A1[op-ae-001: execute-operation]
            A2[op-ae-002: process-step]
            A3[op-ae-003: handle-find-command]
            A4[op-ae-004: handle-tap-command]
            A5[op-ae-005: handle-swipe-command]
            A6[op-ae-006: handle-verify-command]
            A7[op-ae-007: handle-call-operation]
        end

        subgraph IntentTranslatorOps["IntentTranslator 操作 (4个)"]
            T1[op-it-001: translate-intent]
            T2[op-it-002: resolve-selector]
            T3[op-it-003: calculate-coordinates]
            T4[op-it-004: build-fallback-chain]
        end

        subgraph MemoryManagerOps["MemoryManager 操作 (5个)"]
            M1[op-mm-001: record-interaction]
            M2[op-mm-002: check-duplicate]
            M3[op-mm-003: query-memory]
            M4[op-mm-004: cleanup-old-memory]
            M5[op-mm-005: apply-decay]
        end

        S3 --> A1
        S3 --> R3
        S3 --> M2
        A1 --> A2
        A2 --> A3
        A2 --> A4
        A2 --> A5
        A2 --> A6
        A2 --> A7
    end

    L3 --> L4["L4: 步骤层 (100+步骤)"]
```

## 4. 主流程执行图

```mermaid
flowchart TB
    Start([ZD启动]) --> Init[Initializer.Run<br/>创建目录/验证配置]
    Init --> Main[Main.Run<br/>检查画像状态]

    Main --> NeedCreate{需要创建画像?}
    NeedCreate -->|是| Persona[PersonaCreate.Run<br/>AI生成画像]
    NeedCreate -->|否| NeedUpdate{需要每日更新?}

    NeedUpdate -->|是| Daily[DailyUpdate.Run<br/>更新年龄/孕周]
    NeedUpdate -->|否| Ready[READY]

    Persona --> Daily
    Daily --> Ready

    Ready --> Extension[Extension.Run<br/>IP定位/天气]
    Extension --> Session[SessionRunner.Run<br/>核心执行引擎]

    Session --> LoadConfig[加载配置文件]
    LoadConfig --> MainLoop{主循环<br/>超时/能量耗尽}

    MainLoop --> SelectAction[加权随机选择动作]
    SelectAction --> Evaluate[RuleEngine评估帖子]
    Evaluate --> CheckMemory{已互动过?}
    CheckMemory -->|是| Skip[跳过]
    CheckMemory -->|否| Execute[ActionExecutor执行]

    Execute --> Record[MemoryManager记录]
    Record --> UpdateFatigue[更新疲劳状态]
    UpdateFatigue --> MainLoop

    Skip --> MainLoop
    MainLoop -->|退出| StateSave[StateSaver持久化]
    StateSave --> Report{17:00后?}
    Report -->|是| ReportGen[ReportGen生成报告]
    Report -->|否| Maintenance[Maintenance清理数据]
    ReportGen --> Maintenance
    Maintenance --> End([结束])

    style Start fill:#90EE90
    style End fill:#FFB6C1
    style Session fill:#FFD700
    style MainLoop fill:#87CEEB
```

## 5. 模块依赖关系图

```mermaid
graph TB
    subgraph Dependencies["DPS v4.5 模块依赖关系"]

        %% 核心工具
        JsonHelper[JsonHelper]
        CoreHelper[CoreHelper]
        FileHelper[FileHelper]

        %% Intent 系统
        Intent[Intent]
        ZDCommand[ZDCommand]
        ZDResult[ZDResult]
        ZennoDroidAdapter[ZennoDroidAdapter]
        IntentTranslator[IntentTranslator]

        %% 核心组件
        AIService[AIService]
        SelectorEngine[SelectorEngine]
        ActionExecutor[ActionExecutor]
        PageDetector[PageDetector]
        RateLimiter[RateLimiter]

        %% 业务逻辑
        RuleEngine[RuleEngine]
        MemoryManager[MemoryManager]
        SessionRunner[SessionRunner]

        %% 依赖关系
        ZennoDroidAdapter --> ZDCommand
        ZennoDroidAdapter --> ZDResult
        IntentTranslator --> Intent
        IntentTranslator --> ZDCommand
        IntentTranslator --> ZDResult
        IntentTranslator --> ZennoDroidAdapter
        IntentTranslator --> SelectorEngine

        SelectorEngine --> JsonHelper
        ActionExecutor --> JsonHelper
        ActionExecutor --> SelectorEngine

        AIService --> CoreHelper
        AIService --> JsonHelper

        RuleEngine --> JsonHelper
        RuleEngine --> CoreHelper

        MemoryManager --> JsonHelper
        MemoryManager --> FileHelper

        SessionRunner --> JsonHelper
        SessionRunner --> CoreHelper
        SessionRunner --> ActionExecutor
        SessionRunner --> RuleEngine
        SessionRunner --> MemoryManager
        SessionRunner --> PageDetector
    end

    style JsonHelper fill:#FF6B6B
    style CoreHelper fill:#4ECDC4
    style SessionRunner fill:#FFD93D
    style ActionExecutor fill:#6BCB77
    style IntentTranslator fill:#9D4EDD
```

## 6. L4 步骤类型分布

```mermaid
pie title L4 步骤类型分布
    "函数调用" : 40
    "变量赋值" : 30
    "条件判断" : 15
    "循环迭代" : 10
    "错误处理" : 5
```

## 7. 层级关系总结

| 层级 | 名称 | 数量 | 粒度 | 示例 |
|------|------|------|------|------|
| **L1** | 项目层 | 1 个项目 | 整体 | DPS v4.5 项目 |
| **L2** | 模块层 | 32 个模块 | 功能模块 | SessionRunner, JsonHelper |
| **L3** | 操作层 | 51 个操作 | 业务操作 | execute-action-sequence |
| **L4** | 步骤层 | 100+ 步骤 | 代码语句 | create-droid-instance |

---

**生成日期**: 2026-02-28
**.omo 版本**: 2.0
**项目版本**: DPS v4.5.8
