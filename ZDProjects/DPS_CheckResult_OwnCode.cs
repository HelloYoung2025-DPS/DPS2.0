// =====================================================
// DPS_CheckResult_OwnCode.cs - ZD Own Code 入口（结果评估）
// v4.7.0: ZD 外层流程编排 - 循环体第 3 步
// 复制此代码到 ZennoDroid 的 C# code 动作块
// 调用 SessionRunner.EvaluateActionResult() 评估 ZD 原生动作块的执行结果
// 返回 "CONTINUE" / "RETRY" / "END"
// ⚠️ 规范: 仅包含模块加载器，业务逻辑在外部 .cs 文件
// =====================================================

// ========== 模块加载器（完整依赖加载） ==========
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    if (!System.IO.File.Exists(filePath)) {
        project.SendErrorToLog("[DPS_Check] 模块不存在: " + filePath);
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
            project.SendErrorToLog(string.Format("[DPS_Check] 编译错误 行{0}: {1}", e.Line, e.ErrorText));
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
        project.SendErrorToLog("[DPS_Check] 找不到类: " + className);
        return null;
    }
    
    var method = t.GetMethod(methodName);
    if (method == null) {
        project.SendErrorToLog("[DPS_Check] 找不到方法: " + methodName);
        return null;
    }
    
    return method.Invoke(null, args);
};

// ========== 主逻辑 ==========
try {
    string root = project.Variables["project_root"].Value;
    if (string.IsNullOrEmpty(root)) {
        project.SendErrorToLog("[DPS_Check] project_root 未设置");
        return "ERROR: project_root 未设置";
    }
    if (!root.EndsWith("\\")) root += "\\";
    
    string modulePath = root + "Modules\\SessionRunner.cs";
    
    // 调用 SessionRunner.EvaluateActionResult(project, instance)
    // ZD 原生动作块执行完毕后调用，评估执行结果
    // 返回: "CONTINUE"=继续循环, "RETRY"=重试当前操作, "END"=结束会话
    object result = RunModule(modulePath, "EvaluateActionResult", new object[] { project, instance });
    
    string resultStr = result != null ? result.ToString() : "END";
    
    // 写入 ZD 变量供后续 If-Else 判定
    project.Variables["eval_result"].Value = resultStr;
    
    return resultStr;
}
catch (System.Exception ex) {
    project.SendErrorToLog("[DPS_Check] 异常: " + ex.Message);
    project.Variables["eval_result"].Value = "END";
    return "ERROR: " + ex.Message;
}
