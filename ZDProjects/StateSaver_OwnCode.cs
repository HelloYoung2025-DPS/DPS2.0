// =====================================================
// StateSaver_OwnCode.cs - ZD Own Code 入口
// 复制此代码到 ZennoDroid 的 Own Code 动作块
// ⚠️ 规范: 仅包含模块加载器，业务逻辑在外部 .cs 文件
// =====================================================

// ========== 模块加载器（完整依赖加载，与 ModuleLoader.cs 一致） ==========
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    if (!System.IO.File.Exists(filePath)) { project.SendErrorToLog("[StateSaver] 模块不存在: " + filePath); return null; }
    var allCodes = new System.Collections.Generic.List<string>();
    string engineDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Core\\";
    foreach (string ef in new string[] { "ScriptHelpers.cs", "HumanizationEngine.cs", "UILocator.cs", "ErrorRecovery.cs" }) {
        string efPath = engineDir + ef; if (System.IO.File.Exists(efPath)) { allCodes.Add(System.IO.File.ReadAllText(efPath, System.Text.Encoding.UTF8)); }
    }
    string coreDir = System.IO.Path.GetDirectoryName(filePath) + "\\Core\\";
    foreach (string cf in new string[] { "CoreHelper.cs", "JsonHelper.cs", "AIService.cs", "FileHelper.cs", "IExtension.cs", "ExtensionManager.cs", "SelectorEngine.cs", "PageDetector.cs", "ActionExecutor.cs", "OperationContext.cs" }) {
        string cfPath = coreDir + cf; if (System.IO.File.Exists(cfPath)) { allCodes.Add(System.IO.File.ReadAllText(cfPath, System.Text.Encoding.UTF8)); }
    }
    string extBaseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Extensions\\";
    if (System.IO.Directory.Exists(extBaseDir)) { foreach (string extFile in System.IO.Directory.GetFiles(extBaseDir, "*.cs", System.IO.SearchOption.AllDirectories)) { allCodes.Add(System.IO.File.ReadAllText(extFile, System.Text.Encoding.UTF8)); } }
    string moduleDir = System.IO.Path.GetDirectoryName(filePath); string targetFileName = System.IO.Path.GetFileName(filePath);
    foreach (string peerFile in System.IO.Directory.GetFiles(moduleDir, "*.cs", System.IO.SearchOption.TopDirectoryOnly)) { if (System.IO.Path.GetFileName(peerFile) != targetFileName) { allCodes.Add(System.IO.File.ReadAllText(peerFile, System.Text.Encoding.UTF8)); } }
    allCodes.Add(System.IO.File.ReadAllText(filePath, System.Text.Encoding.UTF8));
    var usings = new System.Collections.Generic.HashSet<string>(); var codeBody = new System.Text.StringBuilder();
    foreach (string fileCode in allCodes) { foreach (string line in fileCode.Split(new string[] { "\r\n", "\n" }, System.StringSplitOptions.None)) { string trimmed = line.Trim(); if (trimmed.StartsWith("using ") && trimmed.EndsWith(";") && !trimmed.Contains("(")) { usings.Add(trimmed); } else { codeBody.AppendLine(line); } } }
    var finalCode = new System.Text.StringBuilder(); foreach (string u in usings) { finalCode.AppendLine(u); } finalCode.AppendLine(); finalCode.Append(codeBody.ToString()); string code = finalCode.ToString();
    var provider = new Microsoft.CSharp.CSharpCodeProvider(); var param = new System.CodeDom.Compiler.CompilerParameters();
    param.ReferencedAssemblies.Add("System.dll"); param.ReferencedAssemblies.Add("System.Core.dll"); param.ReferencedAssemblies.Add("System.Data.dll"); param.ReferencedAssemblies.Add("System.Xml.dll"); param.ReferencedAssemblies.Add("Microsoft.CSharp.dll"); param.GenerateInMemory = true;
    var result = provider.CompileAssemblyFromSource(param, code);
    if (result.Errors.HasErrors) { foreach (System.CodeDom.Compiler.CompilerError e in result.Errors) { project.SendErrorToLog(string.Format("[StateSaver] 编译错误 行{0}: {1}", e.Line, e.ErrorText)); } return null; }
    string className = System.IO.Path.GetFileNameWithoutExtension(filePath); System.Type t = result.CompiledAssembly.GetType(className);
    if (t == null) { var types = result.CompiledAssembly.GetExportedTypes(); if (types.Length > 0) t = types[0]; }
    if (t == null) { project.SendErrorToLog("[StateSaver] 找不到类: " + className); return null; }
    var method = t.GetMethod(methodName); if (method == null) { project.SendErrorToLog("[StateSaver] 找不到方法: " + methodName); return null; }
    return method.Invoke(null, args);
};

// ========== 主逻辑 ==========
try {
    string root = project.Variables["project_root"].Value;
    if (string.IsNullOrEmpty(root)) { project.SendErrorToLog("[StateSaver] project_root 未设置"); return "ERROR: project_root 未设置"; }
    if (!root.EndsWith("\\")) root += "\\";
    string modulePath = root + "Modules\\StateSaver.cs";
    project.SendInfoToLog("[StateSaver] 加载模块: " + modulePath);
    object result = RunModule(modulePath, "Run", new object[] { project });
    return result != null ? result.ToString() : "ERROR: 模块执行失败";
}
catch (System.Exception ex) { project.SendErrorToLog("[StateSaver] 异常: " + ex.Message); return "ERROR: " + ex.Message; }
