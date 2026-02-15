// =====================================================
// ZennoDroid_CoreAPI_Explore.cs
// 探索核心 Android 操作 API
// Action, Input, App, Hierarchy
// =====================================================

project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  ZennoDroid 核心 API 探索", true);
project.SendInfoToLog("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), true);
project.SendInfoToLog("========================================", true);

var droid = instance.DroidInstance;

// ========== 探索 1: Action API ==========
project.SendInfoToLog("[探索1] Action API (点击、滑动等)...", true);
try
{
    var action = droid.Action;
    System.Type actionType = action.GetType();
    project.SendInfoToLog("  类型: " + actionType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = actionType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    project.SendInfoToLog("  方法 (" + methods.Length + " 个):", true);
    foreach (System.Reflection.MethodInfo m in methods)
    {
        System.Reflection.ParameterInfo[] ps = m.GetParameters();
        string pStr = "";
        foreach (System.Reflection.ParameterInfo p in ps)
        {
            if (pStr.Length > 0) pStr += ", ";
            pStr += p.ParameterType.Name + " " + p.Name;
        }
        project.SendInfoToLog("    " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Action API 异常: " + ex.Message, true);
}

// ========== 探索 2: Input API ==========
project.SendInfoToLog("[探索2] Input API (键盘输入)...", true);
try
{
    var input = droid.Input;
    System.Type inputType = input.GetType();
    project.SendInfoToLog("  类型: " + inputType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = inputType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    project.SendInfoToLog("  方法 (" + methods.Length + " 个):", true);
    foreach (System.Reflection.MethodInfo m in methods)
    {
        System.Reflection.ParameterInfo[] ps = m.GetParameters();
        string pStr = "";
        foreach (System.Reflection.ParameterInfo p in ps)
        {
            if (pStr.Length > 0) pStr += ", ";
            pStr += p.ParameterType.Name + " " + p.Name;
        }
        project.SendInfoToLog("    " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Input API 异常: " + ex.Message, true);
}

// ========== 探索 3: App API ==========
project.SendInfoToLog("[探索3] App API (APP 管理)...", true);
try
{
    var app = droid.App;
    System.Type appType = app.GetType();
    project.SendInfoToLog("  类型: " + appType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = appType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    project.SendInfoToLog("  方法 (" + methods.Length + " 个):", true);
    foreach (System.Reflection.MethodInfo m in methods)
    {
        System.Reflection.ParameterInfo[] ps = m.GetParameters();
        string pStr = "";
        foreach (System.Reflection.ParameterInfo p in ps)
        {
            if (pStr.Length > 0) pStr += ", ";
            pStr += p.ParameterType.Name + " " + p.Name;
        }
        project.SendInfoToLog("    " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  App API 异常: " + ex.Message, true);
}

// ========== 探索 4: Hierarchy API ==========
project.SendInfoToLog("[探索4] Hierarchy API (UI 元素定位)...", true);
try
{
    var hierarchy = droid.Hierarchy;
    System.Type hierarchyType = hierarchy.GetType();
    project.SendInfoToLog("  类型: " + hierarchyType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = hierarchyType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    project.SendInfoToLog("  方法 (" + methods.Length + " 个):", true);
    foreach (System.Reflection.MethodInfo m in methods)
    {
        System.Reflection.ParameterInfo[] ps = m.GetParameters();
        string pStr = "";
        foreach (System.Reflection.ParameterInfo p in ps)
        {
            if (pStr.Length > 0) pStr += ", ";
            pStr += p.ParameterType.Name + " " + p.Name;
        }
        project.SendInfoToLog("    " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Hierarchy API 异常: " + ex.Message, true);
}

// ========== 探索 5: Screen API ==========
project.SendInfoToLog("[探索5] Screen API (屏幕操作)...", true);
try
{
    var screen = droid.Screen;
    System.Type screenType = screen.GetType();
    project.SendInfoToLog("  类型: " + screenType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = screenType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    project.SendInfoToLog("  方法 (" + methods.Length + " 个):", true);
    foreach (System.Reflection.MethodInfo m in methods)
    {
        System.Reflection.ParameterInfo[] ps = m.GetParameters();
        string pStr = "";
        foreach (System.Reflection.ParameterInfo p in ps)
        {
            if (pStr.Length > 0) pStr += ", ";
            pStr += p.ParameterType.Name + " " + p.Name;
        }
        project.SendInfoToLog("    " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Screen API 异常: " + ex.Message, true);
}

// ========== 探索完成 ==========
project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  核心 API 探索完成!", true);
project.SendInfoToLog("========================================", true);

return "SUCCESS: 核心 API 探索完成";
