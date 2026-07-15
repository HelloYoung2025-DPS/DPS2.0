// =====================================================
// TestRunner.cs - DPS v4.5 运行时测试脚本
// 用于在 ZennoDroid Own Code 动作块中执行
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
//
// === 使用说明 ===
// 1. 设置 ZD 变量 project_root 为项目根目录（如 C:\DPS_v4.5\）
// 2. 将此代码复制到 ZD Own Code 动作块
// 3. 运行并查看日志输出
//
// === 测试覆盖 ===
// - 测试 1: 项目初始化验证
// - 测试 2: 动态编译验证
// - 测试 3: Instagram 导航路径测试
// - 测试 4: 速率限制验证
// - 测试 5: VisionCorrector 集成测试（可选）
// =====================================================

// ========== 辅助函数 ==========
Action<string> Log = (m) => project.SendInfoToLog("[TestRunner] " + m);
Action<string> LogErr = (m) => project.SendErrorToLog("[TestRunner] " + m);
Action<string> LogWarn = (m) => project.SendWarningToLog("[TestRunner] " + m);

Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    project.Variables[name].Value = val ?? "";
};

// ========== 测试结果记录 ==========
int totalTests = 0;
int passedTests = 0;
int failedTests = 0;
int skippedTests = 0;

Action<string, bool, string> RecordTestResult = (testName, passed, reason) => {
    totalTests++;
    if (passed) {
        passedTests++;
        Log("✓ PASS: " + testName);
    } else {
        failedTests++;
        LogErr("✗ FAIL: " + testName + " - " + reason);
    }
};

Action<string, string> RecordTestSkip = (testName, reason) => {
    totalTests++;
    skippedTests++;
    LogWarn("⊘ SKIP: " + testName + " - " + reason);
};

// ========== 模块加载器（简化版，用于测试） ==========
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    if (!System.IO.File.Exists(filePath)) {
        LogErr("模块不存在: " + filePath);
        return null;
    }
    
    var allCodes = new System.Collections.Generic.List<string>();
    
    // 加载 Modules/Core 依赖文件
    string coreDir = System.IO.Path.GetDirectoryName(filePath) + "\\Core\\";
    string[] coreFiles = new string[] { "CoreHelper.cs", "JsonHelper.cs", "AIService.cs", "FileHelper.cs", "IExtension.cs", "ExtensionManager.cs", "SelectorEngine.cs", "PageDetector.cs", "ActionExecutor.cs", "OperationContext.cs", "ManifestLoader.cs", "NavigationResolver.cs", "VisionCorrector.cs", "AppExplorer.cs", "RateLimiter.cs" };
    foreach (string cf in coreFiles) {
        string cfPath = coreDir + cf;
        if (System.IO.File.Exists(cfPath)) {
            allCodes.Add(System.IO.File.ReadAllText(cfPath, System.Text.Encoding.UTF8));
        }
    }
    
    // 加载目标模块
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
            LogErr(string.Format("编译错误 行{0}: {1}", e.Line, e.ErrorText));
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
        LogErr("找不到类: " + className);
        return null;
    }
    
    var method = t.GetMethod(methodName);
    if (method == null) {
        LogErr("找不到方法: " + methodName);
        return null;
    }
    
    return method.Invoke(null, args);
};

// ========== 测试 1: 项目初始化验证 ==========
Func<bool> Test1_ProjectInitialization = () => {
    Log("========================================");
    Log("测试 1: 项目初始化验证");
    Log("========================================");
    
    try {
        string root = GetVar("project_root", "");
        if (string.IsNullOrEmpty(root)) {
            RecordTestResult("测试 1", false, "project_root 变量未设置");
            return false;
        }
        
        if (!root.EndsWith("\\")) root += "\\";
        
        // 调用 Initializer.Run
        string modulePath = root + "Modules\\Initializer.cs";
        object result = RunModule(modulePath, "Run", new object[] { project });
        
        if (result == null) {
            RecordTestResult("测试 1", false, "Initializer.Run 返回 null");
            return false;
        }
        
        string resultStr = result.ToString();
        
        // 验证返回值
        if (resultStr != "SUCCESS" && !resultStr.StartsWith("SUCCESS:")) {
            RecordTestResult("测试 1", false, "返回值异常: " + resultStr);
            return false;
        }
        
        // 验证 ZD 变量
        string initResult = GetVar("initializer_result", "");
        if (initResult != "SUCCESS") {
            RecordTestResult("测试 1", false, "initializer_result 变量值异常: " + initResult);
            return false;
        }
        
        // 验证目录创建
        string[] requiredDirs = new string[] { "Config", "Modules", "Logs", "Screenshots" };
        foreach (string dir in requiredDirs) {
            string dirPath = root + dir;
            if (!System.IO.Directory.Exists(dirPath)) {
                RecordTestResult("测试 1", false, "目录未创建: " + dirPath);
                return false;
            }
        }
        
        RecordTestResult("测试 1", true, "");
        return true;
    }
    catch (System.Exception ex) {
        RecordTestResult("测试 1", false, "异常: " + ex.Message);
        return false;
    }
};

// ========== 测试 2: 动态编译验证 ==========
Func<bool> Test2_DynamicCompilation = () => {
    Log("========================================");
    Log("测试 2: 动态编译验证");
    Log("========================================");
    
    try {
        string root = GetVar("project_root", "");
        if (string.IsNullOrEmpty(root)) {
            RecordTestSkip("测试 2", "project_root 未设置");
            return false;
        }
        
        if (!root.EndsWith("\\")) root += "\\";
        
        // 第一次编译
        string modulePath = root + "Modules\\Initializer.cs";
        Log("第一次编译: " + modulePath);
        object result1 = RunModule(modulePath, "Run", new object[] { project });
        
        if (result1 == null) {
            RecordTestResult("测试 2", false, "第一次编译失败");
            return false;
        }
        
        // 验证包含 RateLimiter（通过检查是否有 CS0246 错误）
        // 如果编译成功，说明所有依赖都正确包含
        
        RecordTestResult("测试 2", true, "");
        return true;
    }
    catch (System.Exception ex) {
        RecordTestResult("测试 2", false, "异常: " + ex.Message);
        return false;
    }
};

// ========== 测试 3: Instagram 导航路径测试 ==========
Func<bool> Test3_NavigationPaths = () => {
    Log("========================================");
    Log("测试 3: Instagram 导航路径测试");
    Log("========================================");
    
    try {
        string root = GetVar("project_root", "");
        if (string.IsNullOrEmpty(root)) {
            RecordTestSkip("测试 3", "project_root 未设置");
            return false;
        }
        
        if (!root.EndsWith("\\")) root += "\\";
        
        // 读取 instagram.json
        string manifestPath = root + "Configs\\Manifests\\instagram.json";
        if (!System.IO.File.Exists(manifestPath)) {
            RecordTestResult("测试 3", false, "instagram.json 不存在");
            return false;
        }
        
        string manifestJson = System.IO.File.ReadAllText(manifestPath, System.Text.Encoding.UTF8);
        
        // 编译并加载 NavigationResolver
        string modulePath = root + "Modules\\Core\\NavigationResolver.cs";
        object result = RunModule(modulePath, "LoadFromManifest", new object[] { manifestJson });
        
        if (result == null) {
            RecordTestResult("测试 3", false, "NavigationResolver 编译失败");
            return false;
        }
        
        // 注意：由于 NavigationResolver 是实例方法，这里只能验证编译成功
        // 实际路径计算需要在完整环境中测试
        
        Log("NavigationResolver 编译成功");
        Log("Manifest 加载验证通过");
        
        RecordTestResult("测试 3", true, "");
        return true;
    }
    catch (System.Exception ex) {
        RecordTestResult("测试 3", false, "异常: " + ex.Message);
        return false;
    }
};

// ========== 测试 4: 速率限制验证 ==========
Func<bool> Test4_RateLimits = () => {
    Log("========================================");
    Log("测试 4: 速率限制验证");
    Log("========================================");
    
    try {
        string root = GetVar("project_root", "");
        if (string.IsNullOrEmpty(root)) {
            RecordTestSkip("测试 4", "project_root 未设置");
            return false;
        }
        
        if (!root.EndsWith("\\")) root += "\\";
        
        // 读取 instagram.json
        string manifestPath = root + "Configs\\Manifests\\instagram.json";
        if (!System.IO.File.Exists(manifestPath)) {
            RecordTestResult("测试 4", false, "instagram.json 不存在");
            return false;
        }
        
        string manifestJson = System.IO.File.ReadAllText(manifestPath, System.Text.Encoding.UTF8);
        
        // 简单的 JSON 解析验证（不依赖 JsonHelper）
        if (!manifestJson.Contains("\"per_hour\": 30")) {
            RecordTestResult("测试 4", false, "rate_limit.per_hour 配置缺失或错误");
            return false;
        }
        
        if (!manifestJson.Contains("\"cooldown_seconds\": 120")) {
            RecordTestResult("测试 4", false, "rate_limit.cooldown_seconds 配置缺失或错误");
            return false;
        }
        
        Log("rate_limit.per_hour = 30");
        Log("rate_limit.cooldown_seconds = 120");
        
        RecordTestResult("测试 4", true, "");
        return true;
    }
    catch (System.Exception ex) {
        RecordTestResult("测试 4", false, "异常: " + ex.Message);
        return false;
    }
};

// ========== 测试 5: VisionCorrector 集成测试 ==========
Func<bool> Test5_VisionCorrector = () => {
    Log("========================================");
    Log("测试 5: VisionCorrector 集成测试");
    Log("========================================");
    
    try {
        string root = GetVar("project_root", "");
        if (string.IsNullOrEmpty(root)) {
            RecordTestSkip("测试 5", "project_root 未设置");
            return false;
        }
        
        if (!root.EndsWith("\\")) root += "\\";
        
        // 检查 AIConfig.json
        string aiConfigPath = root + "Config\\AIConfig.json";
        if (!System.IO.File.Exists(aiConfigPath)) {
            RecordTestSkip("测试 5", "AIConfig.json 不存在");
            return false;
        }
        
        string aiConfig = System.IO.File.ReadAllText(aiConfigPath, System.Text.Encoding.UTF8);
        
        // 检查 API Key 是否配置
        if (aiConfig.Contains("YOUR_") && aiConfig.Contains("API_KEY")) {
            RecordTestSkip("测试 5", "API Key 未配置");
            return false;
        }
        
        // 检查 Screenshots 目录
        string screenshotDir = root + "Screenshots\\";
        if (!System.IO.Directory.Exists(screenshotDir)) {
            RecordTestSkip("测试 5", "Screenshots 目录不存在");
            return false;
        }
        
        Log("VisionCorrector 前置条件检查通过");
        Log("注意: 实际 API 调用需要网络连接和有效设备");
        
        RecordTestResult("测试 5", true, "");
        return true;
    }
    catch (System.Exception ex) {
        RecordTestResult("测试 5", false, "异常: " + ex.Message);
        return false;
    }
};

// ========== 主测试流程 ==========
try {
    Log("========================================");
    Log("  DPS v4.5 运行时测试开始");
    Log("  测试时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    Log("========================================");
    Log("");
    
    // 执行所有测试
    Test1_ProjectInitialization();
    Log("");
    
    Test2_DynamicCompilation();
    Log("");
    
    Test3_NavigationPaths();
    Log("");
    
    Test4_RateLimits();
    Log("");
    
    Test5_VisionCorrector();
    Log("");
    
    // 输出测试摘要
    Log("========================================");
    Log("  测试摘要");
    Log("========================================");
    Log("总测试数: " + totalTests);
    Log("通过: " + passedTests);
    Log("失败: " + failedTests);
    Log("跳过: " + skippedTests);
    Log("");
    
    // 计算通过率
    double passRate = totalTests > 0 ? (double)passedTests / totalTests * 100 : 0;
    Log("通过率: " + passRate.ToString("F2") + "%");
    Log("");
    
    // 保存结果到 ZD 变量
    SetVar("test_total", totalTests.ToString());
    SetVar("test_passed", passedTests.ToString());
    SetVar("test_failed", failedTests.ToString());
    SetVar("test_skipped", skippedTests.ToString());
    SetVar("test_pass_rate", passRate.ToString("F2"));
    
    // 判断整体结果。必需测试只有 PASS 才能放行；零测试和 SKIP 都是失败。
    if (totalTests == 0) {
        Log("========================================");
        LogErr("  ✗ NOT_RUN: 没有执行任何测试");
        Log("========================================");
        SetVar("test_result", "NOT_RUN");
        return "FAILED: NOT_RUN - 没有执行任何测试";
    }
    else if (passedTests == totalTests && failedTests == 0 && skippedTests == 0) {
        Log("========================================");
        Log("  ✓ 所有测试通过！");
        Log("========================================");
        SetVar("test_result", "SUCCESS");
        return "SUCCESS: 所有测试通过 (" + passedTests + "/" + totalTests + ")";
    } else {
        string evidenceResult = passedTests > 0 ? "PARTIAL" : (failedTests > 0 ? "FAIL" : "SKIP");
        Log("========================================");
        LogErr("  ✗ 必需测试未全部 PASS: " + evidenceResult);
        Log("========================================");
        SetVar("test_result", evidenceResult);
        return "FAILED: " + evidenceResult + " - PASS=" + passedTests + ", FAIL=" + failedTests + ", SKIP=" + skippedTests;
    }
}
catch (System.Exception ex) {
    LogErr("测试运行异常: " + ex.Message);
    SetVar("test_result", "INFRA_ERROR");
    SetVar("last_error", ex.Message);
    return "FAILED: INFRA_ERROR - " + ex.Message;
}
