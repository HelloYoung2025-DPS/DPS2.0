// =====================================================
// API_Quick_Check.cs - 快速检查 Screen 和 App API
// =====================================================

project.SendInfoToLog("=== API Quick Check ===", true);

var droid = instance.DroidInstance;

// 检查 Screen API
project.SendInfoToLog("[Screen API]", true);
try
{
    var screen = droid.Screen;
    System.Type screenType = screen.GetType();
    project.SendInfoToLog("  Type: " + screenType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = screenType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    
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
    project.SendErrorToLog("  Screen error: " + ex.Message, true);
}

// 检查 App API
project.SendInfoToLog("[App API]", true);
try
{
    var app = droid.App;
    System.Type appType = app.GetType();
    project.SendInfoToLog("  Type: " + appType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = appType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    
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
    project.SendErrorToLog("  App error: " + ex.Message, true);
}

// 检查 Input API
project.SendInfoToLog("[Input API]", true);
try
{
    var input = droid.Input;
    System.Type inputType = input.GetType();
    project.SendInfoToLog("  Type: " + inputType.FullName, true);
    
    System.Reflection.MethodInfo[] methods = inputType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    
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
    project.SendErrorToLog("  Input error: " + ex.Message, true);
}

project.SendInfoToLog("=== Done ===", true);
return "SUCCESS";
