// =====================================================
// DPS_DecideAction_OwnCode.cs - ZD Own Code 入口（动作决策）
// v4.7.0: ZD 外层流程编排 - 循环体第 1 步
// 复制此代码到 ZennoDroid 的 C# code 动作块
// 调用 SessionRunner.DecideNextAction() 决定下一个操作
// 返回操作名称或 "END" 表示会话结束
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
// ========== 模块加载器（完整依赖加载） ==========
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    if (!System.IO.File.Exists(filePath)) {
        project.SendErrorToLog("[DPS_Decide] 模块不存在: " + filePath);
        return null;
    }
    
    var allCodes = new System.Collections.Generic.List<string>();
    
    string coreDir = System.IO.Path.GetDirectoryName(filePath) + "\\Core\\";
    string[] coreFiles = new string[] { "JsonHelper.cs", "CoreHelper.cs", "FileHelper.cs", "IExtension.cs", "Intent.cs", "ZDCommand.cs", "ZDResult.cs", "OperationContext.cs", "ZennoDroidAdapter.cs", "IntentTranslator.cs", "SelectorEngine.cs", "PageDetector.cs", "AIService.cs", "ExtensionManager.cs", "ActionExecutor.cs", "VisionCorrector.cs", "RateLimiter.cs", "NavigationResolver.cs", "AppExplorer.cs", "ManifestLoader.cs", "SmartOrchestrator.cs" };
    foreach (string cf in coreFiles) {
        string cfPath = coreDir + cf;
        if (System.IO.File.Exists(cfPath)) {
            allCodes.Add(System.IO.File.ReadAllText(cfPath, System.Text.Encoding.UTF8));
        }
    }
    
    string extBaseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Extensions\\";
    if (System.IO.Directory.Exists(extBaseDir)) {
        string[] extFiles = System.IO.Directory.GetFiles(extBaseDir, "*.cs", System.IO.SearchOption.AllDirectories);
        foreach (string extFile in extFiles) {
            allCodes.Add(System.IO.File.ReadAllText(extFile, System.Text.Encoding.UTF8));
        }
    }
    
    string moduleDir = System.IO.Path.GetDirectoryName(filePath);
    string targetFileName = System.IO.Path.GetFileName(filePath);
    string[] peerFiles = System.IO.Directory.GetFiles(moduleDir, "*.cs", System.IO.SearchOption.TopDirectoryOnly);
    foreach (string peerFile in peerFiles) {
        string peerName = System.IO.Path.GetFileName(peerFile);
        if (peerName != targetFileName) {
            allCodes.Add(System.IO.File.ReadAllText(peerFile, System.Text.Encoding.UTF8));
        }
    }
    
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
            project.SendErrorToLog(string.Format("[DPS_Decide] 编译错误 行{0}: {1}", e.Line, e.ErrorText));
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
        project.SendErrorToLog("[DPS_Decide] 找不到类: " + className);
        return null;
    }
    
    var method = t.GetMethod(methodName);
    if (method == null) {
        project.SendErrorToLog("[DPS_Decide] 找不到方法: " + methodName);
        return null;
    }
    
    return method.Invoke(null, args);
};

// ========== 主逻辑 ==========
try {
    string root = project.Variables["project_root"].Value;
    if (string.IsNullOrEmpty(root)) {
        project.SendErrorToLog("[DPS_Decide] project_root 未设置");
        return "ERROR: project_root 未设置";
    }
    if (!root.EndsWith("\\")) root += "\\";
    
    string modulePath = root + "Modules\\SessionRunner.cs";
    
    // 调用 SessionRunner.DecideNextAction(project, instance)
    // 返回操作名称（如 "browse", "open_post"）或 "END" 表示会话结束
    object result = RunModule(modulePath, "DecideNextAction", new object[] { project, instance });
    
    string resultStr = result != null ? result.ToString() : "END";
    
    // 写入 ZD 变量供后续 If-Else / Switch 使用
    project.Variables["next_action"].Value = resultStr;
    
    return resultStr;
}
catch (System.Exception ex) {
    project.SendErrorToLog("[DPS_Decide] 异常: " + ex.Message);
    project.Variables["next_action"].Value = "END";
    return "ERROR: " + ex.Message;
}
