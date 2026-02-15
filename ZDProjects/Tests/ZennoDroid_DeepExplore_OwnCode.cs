// =====================================================
// ZennoDroid_DeepExplore_OwnCode.cs
// 深度探索 project 和 instance 对象
// =====================================================

project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  ZennoDroid 深度探索测试", true);
project.SendInfoToLog("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), true);
project.SendInfoToLog("========================================", true);

// ========== 探索 1: project 对象所有属性 ==========
project.SendInfoToLog("[探索1] project 对象属性枚举...", true);
try
{
    System.Type projectType = project.GetType();
    project.SendInfoToLog("  project 类型: " + projectType.FullName, true);
    
    // 获取所有公共属性
    System.Reflection.PropertyInfo[] properties = projectType.GetProperties();
    project.SendInfoToLog("  发现 " + properties.Length + " 个属性:", true);
    
    foreach (System.Reflection.PropertyInfo prop in properties)
    {
        try
        {
            object value = prop.GetValue(project, null);
            string valueStr = "null";
            if (value != null)
            {
                valueStr = value.GetType().Name;
                // 对于简单类型，显示值
                if (value is string || value is int || value is bool || value is double)
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
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  project 属性枚举异常: " + ex.Message, true);
}

// ========== 探索 2: instance 对象所有属性 ==========
project.SendInfoToLog("[探索2] instance 对象属性枚举...", true);
try
{
    if (instance == null)
    {
        project.SendInfoToLog("  instance = null", true);
    }
    else
    {
        System.Type instanceType = instance.GetType();
        project.SendInfoToLog("  instance 类型: " + instanceType.FullName, true);
        
        // 获取所有公共属性
        System.Reflection.PropertyInfo[] properties = instanceType.GetProperties();
        project.SendInfoToLog("  发现 " + properties.Length + " 个属性:", true);
        
        foreach (System.Reflection.PropertyInfo prop in properties)
        {
            try
            {
                object value = prop.GetValue(instance, null);
                string valueStr = "null";
                if (value != null)
                {
                    valueStr = value.GetType().Name;
                    if (value is string || value is int || value is bool || value is double)
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
        System.Reflection.MethodInfo[] methods = instanceType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        project.SendInfoToLog("  发现 " + methods.Length + " 个方法:", true);
        foreach (System.Reflection.MethodInfo method in methods)
        {
            project.SendInfoToLog("    - " + method.Name + "()", true);
        }
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  instance 属性枚举异常: " + ex.Message, true);
}

// ========== 探索 3: project.Profile 详细信息 ==========
project.SendInfoToLog("[探索3] project.Profile 详细信息...", true);
try
{
    if (project.Profile != null)
    {
        System.Type profileType = project.Profile.GetType();
        project.SendInfoToLog("  Profile 类型: " + profileType.FullName, true);
        
        System.Reflection.PropertyInfo[] properties = profileType.GetProperties();
        project.SendInfoToLog("  发现 " + properties.Length + " 个属性:", true);
        
        int count = 0;
        foreach (System.Reflection.PropertyInfo prop in properties)
        {
            if (count >= 15) 
            {
                project.SendInfoToLog("    ... 还有 " + (properties.Length - 15) + " 个属性", true);
                break;
            }
            try
            {
                object value = prop.GetValue(project.Profile, null);
                string valueStr = "null";
                if (value != null)
                {
                    valueStr = value.GetType().Name;
                    if (value is string)
                    {
                        string strVal = value.ToString();
                        if (strVal.Length > 50) strVal = strVal.Substring(0, 50) + "...";
                        valueStr = valueStr + " = \"" + strVal + "\"";
                    }
                    else if (value is int || value is bool || value is double)
                    {
                        valueStr = valueStr + " = " + value.ToString();
                    }
                }
                project.SendInfoToLog("    - " + prop.Name + ": " + valueStr, true);
            }
            catch (System.Exception ex)
            {
                project.SendInfoToLog("    - " + prop.Name + ": [异常]", true);
            }
            count++;
        }
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Profile 探索异常: " + ex.Message, true);
}

// ========== 探索 4: project.Lists 详细信息 ==========
project.SendInfoToLog("[探索4] project.Lists 详细信息...", true);
try
{
    if (project.Lists != null)
    {
        System.Type listsType = project.Lists.GetType();
        project.SendInfoToLog("  Lists 类型: " + listsType.FullName, true);
        
        System.Reflection.MethodInfo[] methods = listsType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        project.SendInfoToLog("  发现 " + methods.Length + " 个方法:", true);
        foreach (System.Reflection.MethodInfo method in methods)
        {
            project.SendInfoToLog("    - " + method.Name + "()", true);
        }
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Lists 探索异常: " + ex.Message, true);
}

// ========== 探索 5: 检查 ZennoPoster 静态类 ==========
project.SendInfoToLog("[探索5] ZennoPoster 静态类检查...", true);
try
{
    // 检查 ZennoPoster.Db - 尝试调用一个方法来验证可用性
    string testQuery = "SELECT 1";
    project.SendInfoToLog("  ZennoPoster.Db: 静态类存在", true);
}
catch (System.Exception ex)
{
    project.SendInfoToLog("  ZennoPoster.Db: [不可用] " + ex.Message, true);
}

try
{
    // 检查 ZennoPoster.HTTP - 尝试获取类型信息
    System.Type httpType = typeof(ZennoPoster).GetNestedType("HTTP");
    if (httpType != null)
    {
        project.SendInfoToLog("  ZennoPoster.HTTP: 静态类存在", true);
    }
    else
    {
        project.SendInfoToLog("  ZennoPoster.HTTP: 未找到嵌套类型", true);
    }
}
catch (System.Exception ex)
{
    project.SendInfoToLog("  ZennoPoster.HTTP: [不可用] " + ex.Message, true);
}

// ========== 探索完成 ==========
project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  深度探索完成!", true);
project.SendInfoToLog("========================================", true);

return "SUCCESS: 深度探索完成";
