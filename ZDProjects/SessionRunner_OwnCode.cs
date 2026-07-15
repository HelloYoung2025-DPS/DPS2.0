// =====================================================
// SessionRunner_OwnCode.cs - ZD Own Code 入口
// 复制此代码到 ZennoDroid 的 Own Code 动作块
// ⚠️ 规范: 仅包含模块加载器，业务逻辑在外部 .cs 文件
// =====================================================

// F5 P0 trusted wrapper gate. This value is compiled into the wrapper and is
// never read from project variables, paths, configuration, or model output.
const bool LEGACY_DISABLE_NEW_COMMANDS = true;
const string LEGACY_BAD_END_TOKEN = "ERROR_BRIDGE_REQUIRED";
if (LEGACY_DISABLE_NEW_COMMANDS)
{
    System.Action<string, string> SetGateVariable = (name, value) =>
    {
        try
        {
            project.Variables[name].Value = value;
        }
        catch (System.Exception)
        {
            // Missing optional variables must not skip the remaining cleanup.
        }
    };
    SetGateVariable("zd_step_type", LEGACY_BAD_END_TOKEN);
    SetGateVariable("zd_step_plan", "");
    SetGateVariable("zd_step_index", "0");
    SetGateVariable("zd_step_count", "0");
    SetGateVariable("zd_step_param", "");
    SetGateVariable("zd_selector_key", "");
    SetGateVariable("zd_tap_x1", "");
    SetGateVariable("zd_tap_x2", "");
    SetGateVariable("zd_tap_y1", "");
    SetGateVariable("zd_tap_y2", "");
    SetGateVariable("zd_swipe_x1", "");
    SetGateVariable("zd_swipe_y1", "");
    SetGateVariable("zd_swipe_x2", "");
    SetGateVariable("zd_swipe_y2", "");
    SetGateVariable("zd_swipe_duration", "0");
    SetGateVariable("zd_wait_sec", "0");
    SetGateVariable("zd_found", "false");
    SetGateVariable("zd_verify_rule", "");
    SetGateVariable("zd_safety", "100");
    SetGateVariable("zd_action_result", "UNKNOWN");
    SetGateVariable("zd_action_duration", "0");
    SetGateVariable("zd_action_error_detail", "modern authorization bridge unavailable");
    SetGateVariable("sr_action_count", "0");
    SetGateVariable("sr_success_count", "0");
    SetGateVariable("sr_fail_count", "0");
    SetGateVariable("sr_skip_count", "0");
    SetGateVariable("sr_consecutive_skips", "0");
    SetGateVariable("sr_vision_recovery_count", "0");
    SetGateVariable("sr_orchestrator_state", "");
    SetGateVariable("sr_op_queue", "");
    SetGateVariable("sr_op_queue_index", "0");
    SetGateVariable("sr_current_session_action", "");
    SetGateVariable("sr_current_op_name", "");
    SetGateVariable("sr_memory_entries", "");
    SetGateVariable("sr_memory_file", "");
    SetGateVariable("sr_use_legacy_run", "false");
    SetGateVariable("action_count", "0");
    SetGateVariable("action_attempt_count", "0");
    SetGateVariable("session_successful_actions", "0");
    SetGateVariable("session_failed_actions", "0");
    SetGateVariable("session_skipped_actions", "0");
    SetGateVariable("session_success_rate", "0.0000");
    SetGateVariable("action_result", "ERROR");
    SetGateVariable("current_action", "");
    SetGateVariable("current_intent", "");
    SetGateVariable("execution_proposal", "");
    SetGateVariable("recovery_proposal", "");
    SetGateVariable("onboarding_proposal", "");
    SetGateVariable("ai_direct_execution", "false");
    SetGateVariable("ai_notify_human", "false");
    SetGateVariable("vision_verified", "error_preserved");
    SetGateVariable("sr_legacy_run_completed", "false");
    SetGateVariable("sr_legacy_run_result", "ERROR: modern authorization bridge unavailable");
    SetGateVariable("run_result", "ERROR");
    SetGateVariable("session_result", "ERROR");
    SetGateVariable("last_error", "modern authorization bridge unavailable");
    SetGateVariable("zd_step_type", LEGACY_BAD_END_TOKEN);
    try
    {
        project.SendErrorToLog("[DPS P0] legacy_disable_new_commands is compiled ON; fixed signed ABI/BOM bridge is not installed");
    }
    catch (System.Exception)
    {
        // Logging failure must not bypass the hard return.
    }
    return LEGACY_BAD_END_TOKEN;
}
// ========== 模块加载器（完整依赖加载，与 ModuleLoader.cs 一致） ==========
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    if (!System.IO.File.Exists(filePath)) {
        project.SendErrorToLog("[SessionRunner] 模块不存在: " + filePath);
        return null;
    }
    
    var allCodes = new System.Collections.Generic.List<string>();
    
    // ⚠️ Core/ 引擎文件（ScriptHelpers, HumanizationEngine, UILocator, ErrorRecovery）
    // 是 "Own Code" 风格的裸代码（Func/Action 委托），不能和 class 一起编译。
    // 它们仅供 ZDProjects/*.cs Own Code 动作块直接使用。
    // 编译模块已通过 CoreHelper/SelectorEngine/UIHelper 提供等效功能。

    // 2. 加载 Modules/Core 所有依赖文件
    string coreDir = System.IO.Path.GetDirectoryName(filePath) + "\\Core\\";
    string[] coreFiles = new string[] { "JsonHelper.cs", "CoreHelper.cs", "FileHelper.cs", "IExtension.cs", "Intent.cs", "ZDCommand.cs", "ZDResult.cs", "OperationContext.cs", "ZennoDroidAdapter.cs", "IntentTranslator.cs", "SelectorEngine.cs", "PageDetector.cs", "AIService.cs", "ExtensionManager.cs", "ActionExecutor.cs", "VisionCorrector.cs", "RateLimiter.cs", "NavigationResolver.cs", "AppExplorer.cs", "ManifestLoader.cs", "SmartOrchestrator.cs" };
    foreach (string cf in coreFiles) {
        string cfPath = coreDir + cf;
        if (System.IO.File.Exists(cfPath)) {
            allCodes.Add(System.IO.File.ReadAllText(cfPath, System.Text.Encoding.UTF8));
        }
    }
    
    // 3. 加载 Extensions 目录下所有扩展实现
    string extBaseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Extensions\\";
    if (System.IO.Directory.Exists(extBaseDir)) {
        string[] extFiles = System.IO.Directory.GetFiles(extBaseDir, "*.cs", System.IO.SearchOption.AllDirectories);
        foreach (string extFile in extFiles) {
            allCodes.Add(System.IO.File.ReadAllText(extFile, System.Text.Encoding.UTF8));
        }
    }
    
    // 4. 加载同目录下的同级模块（如 RuleEngine.cs、MemoryManager.cs 等）
    string moduleDir = System.IO.Path.GetDirectoryName(filePath);
    string targetFileName = System.IO.Path.GetFileName(filePath);
    string[] peerFiles = System.IO.Directory.GetFiles(moduleDir, "*.cs", System.IO.SearchOption.TopDirectoryOnly);
    foreach (string peerFile in peerFiles) {
        string peerName = System.IO.Path.GetFileName(peerFile);
        if (peerName != targetFileName) {
            allCodes.Add(System.IO.File.ReadAllText(peerFile, System.Text.Encoding.UTF8));
        }
    }
    
    // 5. 加载目标模块
    allCodes.Add(System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8));
    
    var usings = new System.Collections.Generic.HashSet<string>();
    var codeBody = new System.Text.StringBuilder();
    
    foreach (string fileCode in allCodes) {
        string[] lines = fileCode.Split(new string[] { "\r\n", "\n" }, System.StringSplitOptions.None);
        foreach (string line in lines) {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("using ") && trimmed.EndsWith(";") && !trimmed.Contains("(")) {
                usings.Add(trimmed);
            } else {
                codeBody.AppendLine(line);
            }
        }
    }
    
    var finalCode = new System.Text.StringBuilder();
    foreach (string u in usings) {
        finalCode.AppendLine(u);
    }
    finalCode.AppendLine();
    finalCode.Append(codeBody.ToString());
    
    string code = finalCode.ToString();
    
    var provider = new Microsoft.CSharp.CSharpCodeProvider();
    var param = new System.CodeDom.Compiler.CompilerParameters();
    param.ReferencedAssemblies.Add("System.dll");
    param.ReferencedAssemblies.Add("System.Core.dll");
    param.ReferencedAssemblies.Add("System.Data.dll");
    param.ReferencedAssemblies.Add("System.Xml.dll");
    param.ReferencedAssemblies.Add("Microsoft.CSharp.dll");
    param.GenerateInMemory = true;
    
    var result = provider.CompileAssemblyFromSource(param, code);
    
    if (result.Errors.HasErrors) {
        foreach (System.CodeDom.Compiler.CompilerError e in result.Errors) {
            project.SendErrorToLog(string.Format("[SessionRunner] 编译错误 行{0}: {1}", e.Line, e.ErrorText));
        }
        return null;
    }
    
    string className = System.IO.Path.GetFileNameWithoutExtension(filePath);
    System.Type t = result.CompiledAssembly.GetType(className);
    if (t == null) {
        var types = result.CompiledAssembly.GetExportedTypes();
        if (types.Length > 0) t = types[0];
    }
    
    if (t == null) {
        project.SendErrorToLog("[SessionRunner] 找不到类: " + className);
        return null;
    }
    
    var method = t.GetMethod(methodName);
    if (method == null) {
        project.SendErrorToLog("[SessionRunner] 找不到方法: " + methodName);
        return null;
    }
    
    return method.Invoke(null, args);
};

// ========== 主逻辑 ==========
try {
    string root = project.Variables["project_root"].Value;
    if (string.IsNullOrEmpty(root)) {
        project.SendErrorToLog("[SessionRunner] project_root 未设置");
        return "ERROR: project_root 未设置";
    }
    if (!root.EndsWith("\\")) root += "\\";
    
    string modulePath = root + "Modules\\SessionRunner.cs";
    project.SendInfoToLog("[SessionRunner] 加载模块: " + modulePath);
    
    // BUG-01 fix: 同时传递 instance，供 CoreHelper.InitInstance 注入 DroidInstance
    object result = RunModule(modulePath, "Run", new object[] { project, instance });
    return result != null ? result.ToString() : "ERROR: 模块执行失败";
}
catch (System.Exception ex) {
    project.SendErrorToLog("[SessionRunner] 异常: " + ex.Message);
    return "ERROR: " + ex.Message;
}
