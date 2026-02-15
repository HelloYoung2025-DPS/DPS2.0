// =====================================================
// ZennoDroid_DroidInstance_Explore.cs
// 深度探索 instance.DroidInstance 对象
// 这是 ZennoDroid 特有的 Android 操作接口
// =====================================================

project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  DroidInstance 深度探索", true);
project.SendInfoToLog("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), true);
project.SendInfoToLog("========================================", true);

// ========== 探索 DroidInstance ==========
project.SendInfoToLog("[探索1] instance.DroidInstance 属性枚举...", true);
try
{
    var droidInstance = instance.DroidInstance;
    if (droidInstance == null)
    {
        project.SendInfoToLog("  DroidInstance = null", true);
        return "ERROR: DroidInstance is null";
    }
    
    System.Type droidType = droidInstance.GetType();
    project.SendInfoToLog("  DroidInstance 类型: " + droidType.FullName, true);
    
    // 获取所有公共属性
    System.Reflection.PropertyInfo[] properties = droidType.GetProperties();
    project.SendInfoToLog("  发现 " + properties.Length + " 个属性:", true);
    
    foreach (System.Reflection.PropertyInfo prop in properties)
    {
        try
        {
            object value = prop.GetValue(droidInstance, null);
            string valueStr = "null";
            if (value != null)
            {
                valueStr = value.GetType().Name;
                if (value is string)
                {
                    string strVal = value.ToString();
                    if (strVal.Length > 80) strVal = strVal.Substring(0, 80) + "...";
                    valueStr = valueStr + " = \"" + strVal + "\"";
                }
                else if (value is int || value is bool || value is double || value is long)
                {
                    valueStr = valueStr + " = " + value.ToString();
                }
            }
            project.SendInfoToLog("    - " + prop.Name + ": " + valueStr, true);
        }
        catch (System.Exception ex)
        {
            project.SendInfoToLog("    - " + prop.Name + ": [异常] " + ex.Message, true);
        }
    }
    
    // 获取所有公共方法
    project.SendInfoToLog("[探索2] DroidInstance 方法枚举...", true);
    System.Reflection.MethodInfo[] methods = droidType.GetMethods(
        System.Reflection.BindingFlags.Public | 
        System.Reflection.BindingFlags.Instance | 
        System.Reflection.BindingFlags.DeclaredOnly
    );
    project.SendInfoToLog("  发现 " + methods.Length + " 个方法:", true);
    
    foreach (System.Reflection.MethodInfo method in methods)
    {
        // 获取参数信息
        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
        string paramStr = "";
        foreach (System.Reflection.ParameterInfo param in parameters)
        {
            if (paramStr.Length > 0) paramStr += ", ";
            paramStr += param.ParameterType.Name + " " + param.Name;
        }
        project.SendInfoToLog("    - " + method.ReturnType.Name + " " + method.Name + "(" + paramStr + ")", true);
    }
    
    // 探索嵌套对象
    project.SendInfoToLog("[探索3] DroidInstance 嵌套对象探索...", true);
    
    // 检查是否有 Device 属性
    System.Reflection.PropertyInfo deviceProp = droidType.GetProperty("Device");
    if (deviceProp != null)
    {
        try
        {
            object device = deviceProp.GetValue(droidInstance, null);
            if (device != null)
            {
                project.SendInfoToLog("  Device 对象类型: " + device.GetType().FullName, true);
                
                // 枚举 Device 的方法
                System.Reflection.MethodInfo[] deviceMethods = device.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.DeclaredOnly
                );
                project.SendInfoToLog("  Device 方法 (" + deviceMethods.Length + " 个):", true);
                foreach (System.Reflection.MethodInfo m in deviceMethods)
                {
                    System.Reflection.ParameterInfo[] ps = m.GetParameters();
                    string pStr = "";
                    foreach (System.Reflection.ParameterInfo p in ps)
                    {
                        if (pStr.Length > 0) pStr += ", ";
                        pStr += p.ParameterType.Name + " " + p.Name;
                    }
                    project.SendInfoToLog("      " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")", true);
                }
            }
        }
        catch (System.Exception ex)
        {
            project.SendInfoToLog("  Device 探索异常: " + ex.Message, true);
        }
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  DroidInstance 探索异常: " + ex.Message, true);
}

// ========== 探索完成 ==========
project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  DroidInstance 探索完成!", true);
project.SendInfoToLog("========================================", true);

return "SUCCESS: DroidInstance 探索完成";
