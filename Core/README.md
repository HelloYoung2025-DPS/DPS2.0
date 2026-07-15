# Core/ 目录 — ZD 运行时引擎层

> **注意**: 此目录与 `Modules/Core/` 是不同的层！
>
> - **Core/** (本目录): 运行时引擎，直接与 ZennoDroid API 交互
>   - `ScriptHelpers.cs` — ZD API 封装
>   - `UILocator.cs` — UI 元素定位引擎
>   - `HumanizationEngine.cs` — 人类行为模拟
>   - `ErrorRecovery.cs` — 错误恢复策略
>
> - **Modules/Core/** (另一个目录): 可编译的业务逻辑库
>   - `CoreHelper.cs` — 核心工具函数
>   - `JsonHelper.cs` — JSON 解析
>   - `AIService.cs` — AI API 调用
>   - `FileHelper.cs` — 文件操作
>   - `SelectorEngine.cs` — 智能元素选择
>   - `PageDetector.cs` — 页面识别
>   - `ActionExecutor.cs` — JSON 步骤执行引擎
>   - `IExtension.cs` / `ExtensionManager.cs` — 扩展系统
