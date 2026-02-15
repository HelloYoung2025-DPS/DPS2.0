// =====================================================
// ModuleLoader.cs - 通用模块加载器模板
// 复制此代码到 ZD 的 Own Code 动作块
// ⚠️ 规范: 这是通用模块加载器，用于调用外部 .cs 文件
// =====================================================
//
// === 使用说明 ===
// 1. 设置 project_root 变量为项目根目录
// 2. 将此代码复制到 ZD Own Code 动作块
// 3. 修改底部的 modulePath 和 methodName 调用对应模块
//
// === 工作原理 ===
// 1. 读取目标模块和所有 Core/*.cs 依赖
// 2. 提取所有 using 语句并合并到代码最前面
// 3. 动态编译并执行指定方法（带缓存优化）
// =====================================================

// ========== 静态缓存（跨调用持久化） ==========
// 缓存编译后的 MethodInfo，避免重复编译
static System.Collections.Generic.Dictionary<string, System.Reflection.MethodInfo> _methodCache;
// 缓存文件时间戳，用于检测变更
static System.Collections.Generic.Dictionary<string, long> _timestampCache;
// 缓存锁对象
static object _cacheLock = new object();

// ========== 缓存辅助函数 ==========
// 获取所有相关文件的时间戳列表
Func<string, System.Collections.Generic.Dictionary<string, long>> GetFileTimestamps = (filePath) => {
    var timestamps = new System.Collections.Generic.Dictionary<string, long>();
    
    // 添加模块文件时间戳
    if (System.IO.File.Exists(filePath)) {
        timestamps[filePath] = new System.IO.FileInfo(filePath).LastWriteTimeUtc.Ticks;
    }
    
    // 添加 Modules/Core 依赖文件时间戳
    string coreDir = System.IO.Path.GetDirectoryName(filePath) + "\\Core\\";
    string[] coreFiles = new string[] { "CoreHelper.cs", "JsonHelper.cs", "AIService.cs", "FileHelper.cs", "IExtension.cs", "ExtensionManager.cs", "SelectorEngine.cs", "PageDetector.cs", "ActionExecutor.cs" };
    foreach (string cf in coreFiles) {
        string cfPath = coreDir + cf;
        if (System.IO.File.Exists(cfPath)) {
            timestamps[cfPath] = new System.IO.FileInfo(cfPath).LastWriteTimeUtc.Ticks;
        }
    }
    
    // 注意：Core/ 引擎文件（ScriptHelpers 等）不参与编译，不需要跟踪时间戳
    // 它们是 Own Code 风格的裸代码，仅供 ZDProjects/*.cs 使用
    
    // 添加 Extensions 目录文件时间戳
    string extBaseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Extensions\\";
    if (System.IO.Directory.Exists(extBaseDir)) {
        string[] extFiles = System.IO.Directory.GetFiles(extBaseDir, "*.cs", System.IO.SearchOption.AllDirectories);
        foreach (string extFile in extFiles) {
            timestamps[extFile] = new System.IO.FileInfo(extFile).LastWriteTimeUtc.Ticks;
        }
    }
    
    // 添加同目录下的同级模块文件时间戳
    string moduleDir = System.IO.Path.GetDirectoryName(filePath);
    string[] peerModules = System.IO.Directory.GetFiles(moduleDir, "*.cs", System.IO.SearchOption.TopDirectoryOnly);
    foreach (string peerFile in peerModules) {
        if (peerFile != filePath) {
            timestamps[peerFile] = new System.IO.FileInfo(peerFile).LastWriteTimeUtc.Ticks;
        }
    }
    
    return timestamps;
};

// 构建缓存键（基于模块路径）
Func<string, string> BuildCacheKey = (filePath) => {
    // 使用规范化的绝对路径作为缓存键
    return System.IO.Path.GetFullPath(filePath).ToLowerInvariant();
};

// 检查是否有任何文件发生变更
Func<string, System.Collections.Generic.Dictionary<string, long>, bool> HasAnyFileChanged = null;
HasAnyFileChanged = (cacheKey, currentTimestamps) => {
    if (_timestampCache == null) return true;
    
    // 检查缓存中是否有此模块的时间戳记录
    foreach (var kvp in currentTimestamps) {
        string fileKey = cacheKey + "|" + kvp.Key;
        long cachedTicks;
        if (!_timestampCache.TryGetValue(fileKey, out cachedTicks)) {
            return true; // 新文件，需要重新编译
        }
        if (cachedTicks != kvp.Value) {
            return true; // 文件已修改
        }
    }
    
    return false;
};

// 更新时间戳缓存
Action<string, System.Collections.Generic.Dictionary<string, long>> UpdateTimestampCache = (cacheKey, timestamps) => {
    foreach (var kvp in timestamps) {
        string fileKey = cacheKey + "|" + kvp.Key;
        _timestampCache[fileKey] = kvp.Value;
    }
};

// ========== 模块加载器（带编译缓存优化） ==========
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    if (!System.IO.File.Exists(filePath)) {
        project.SendErrorToLog("[ModuleLoader] 模块不存在: " + filePath);
        return null;
    }
    
    lock (_cacheLock) {
        if (_methodCache == null) {
            _methodCache = new System.Collections.Generic.Dictionary<string, System.Reflection.MethodInfo>();
            _timestampCache = new System.Collections.Generic.Dictionary<string, long>();
        }
    }
    
    string cacheKey = BuildCacheKey(filePath);
    var currentTimestamps = GetFileTimestamps(filePath);
    
    lock (_cacheLock) {
        if (_methodCache.ContainsKey(cacheKey) && !HasAnyFileChanged(cacheKey, currentTimestamps)) {
            project.SendInfoToLog("[ModuleLoader] 缓存命中: " + filePath);
            return _methodCache[cacheKey].Invoke(null, args);
        }
    }
    
    project.SendInfoToLog("[ModuleLoader] 缓存未命中，开始编译: " + filePath);
    
    var allCodes = new System.Collections.Generic.List<string>();
    
    // ⚠️ Core/ 引擎文件（ScriptHelpers, HumanizationEngine, UILocator, ErrorRecovery）
    // 是 "Own Code" 风格的裸代码（Func/Action 委托），不能和 class 一起编译。
    // 它们仅供 ZDProjects/*.cs Own Code 动作块直接使用。
    // 编译模块已通过 CoreHelper/SelectorEngine/UIHelper 提供等效功能。
    // string engineDir = ...;  // 已移除，避免 CSharpCodeProvider 编译失败
    
    // 加载 Modules/Core 依赖文件
    string coreDir = System.IO.Path.GetDirectoryName(filePath) + "\\Core\\";
    string[] coreFiles = new string[] { "CoreHelper.cs", "JsonHelper.cs", "AIService.cs", "FileHelper.cs", "IExtension.cs", "ExtensionManager.cs", "SelectorEngine.cs", "PageDetector.cs", "ActionExecutor.cs" };
    foreach (string cf in coreFiles) {
        string cfPath = coreDir + cf;
        if (System.IO.File.Exists(cfPath)) {
            allCodes.Add(System.IO.File.ReadAllText(cfPath, System.Text.Encoding.UTF8));
        }
    }
    
    // 加载 Extensions 目录下所有扩展实现
    string extBaseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Extensions\\";
    if (System.IO.Directory.Exists(extBaseDir)) {
        string[] extFiles = System.IO.Directory.GetFiles(extBaseDir, "*.cs", System.IO.SearchOption.AllDirectories);
        foreach (string extFile in extFiles) {
            allCodes.Add(System.IO.File.ReadAllText(extFile, System.Text.Encoding.UTF8));
        }
    }
    
    // 加载同目录下的同级模块（如 RuleEngine.cs 等，排除目标文件本身）
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
            project.SendErrorToLog(string.Format("[ModuleLoader] 编译错误 行{0}: {1}", e.Line, e.ErrorText));
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
        project.SendErrorToLog("[ModuleLoader] 找不到类: " + className);
        return null;
    }
    
    var method = t.GetMethod(methodName);
    if (method == null) {
        project.SendErrorToLog("[ModuleLoader] 找不到方法: " + methodName);
        return null;
    }
    
    lock (_cacheLock) {
        _methodCache[cacheKey] = method;
        UpdateTimestampCache(cacheKey, currentTimestamps);
    }
    
    project.SendInfoToLog("[ModuleLoader] 编译完成并已缓存: " + filePath);
    
    return method.Invoke(null, args);
};

// ========== 主逻辑 ==========
try {
    string root = project.Variables["project_root"].Value;
    if (string.IsNullOrEmpty(root)) {
        project.SendErrorToLog("[ModuleLoader] project_root 未设置");
        return "ERROR: project_root 未设置";
    }
    if (!root.EndsWith("\\")) root += "\\";
    
    // === 修改此处调用不同模块 ===
    string modulePath = root + "Modules\\Main.cs";
    string methodName = "Run";
    
    project.SendInfoToLog("[ModuleLoader] 加载模块: " + modulePath);
    
    object result = RunModule(modulePath, methodName, new object[] { project });
    return result != null ? result.ToString() : "ERROR: 模块执行失败";
}
catch (System.Exception ex) {
    project.SendErrorToLog("[ModuleLoader] 异常: " + ex.Message);
    return "ERROR: " + ex.Message;
}
