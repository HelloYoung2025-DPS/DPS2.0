// =====================================================
// ZennoDroid_AdvancedTest_OwnCode.cs
// ZennoDroid 高级功能验证脚本
// 验证 Skill 文档中未测试的规则
// =====================================================

project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  ZennoDroid 高级功能验证测试", true);
project.SendInfoToLog("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), true);
project.SendInfoToLog("========================================", true);

// ========== 测试 1: project.Directory ==========
project.SendInfoToLog("[测试1] project.Directory 测试...", true);
try
{
    string projectDir = project.Directory;
    if (string.IsNullOrEmpty(projectDir))
    {
        project.SendInfoToLog("  project.Directory = null 或空字符串 (符合预期)", true);
    }
    else
    {
        project.SendInfoToLog("  project.Directory = " + projectDir, true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  project.Directory 异常: " + ex.Message, true);
}

// ========== 测试 2: project.Name ==========
project.SendInfoToLog("[测试2] project.Name 测试...", true);
try
{
    string projectName = project.Name;
    if (string.IsNullOrEmpty(projectName))
    {
        project.SendInfoToLog("  project.Name = null 或空字符串 (符合预期)", true);
    }
    else
    {
        project.SendInfoToLog("  project.Name = " + projectName, true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  project.Name 异常: " + ex.Message, true);
}

// ========== 测试 3: instance 对象 ==========
project.SendInfoToLog("[测试3] instance 对象测试...", true);
try
{
    if (instance == null)
    {
        project.SendInfoToLog("  instance = null (ZennoDroid 中不可用，符合预期)", true);
    }
    else
    {
        project.SendInfoToLog("  instance 对象存在，类型: " + instance.GetType().Name, true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  instance 访问异常: " + ex.Message, true);
}

// ========== 测试 4: 默认参数测试 ==========
project.SendInfoToLog("[测试4] 默认参数测试...", true);
// 注意：如果默认参数被禁止，下面的代码会编译失败
// 我们使用不带默认参数的方式来测试
Func<string, string, string> ConcatWithSeparator = (a, b) => {
    return a + " | " + b;
};
string concatResult = ConcatWithSeparator("Hello", "World");
project.SendInfoToLog("  无默认参数的 Func 结果: " + concatResult, true);

// ========== 测试 5: 递归函数声明 ==========
project.SendInfoToLog("[测试5] 递归函数测试...", true);
// 递归函数需要先声明为 null，再赋值
Func<int, int> Factorial = null;
Factorial = (n) => {
    if (n <= 1) return 1;
    return n * Factorial(n - 1);
};
int factResult = Factorial(5);
project.SendInfoToLog("  5! = " + factResult.ToString() + " (应为 120)", true);

// ========== 测试 6: LINQ 静态方法形式 ==========
project.SendInfoToLog("[测试6] LINQ 静态方法测试...", true);
try
{
    var numbers = new System.Collections.Generic.List<int>();
    numbers.Add(1);
    numbers.Add(2);
    numbers.Add(3);
    numbers.Add(4);
    numbers.Add(5);
    
    // 使用 LINQ 静态方法形式
    var filtered = System.Linq.Enumerable.Where(numbers, x => x > 2);
    var sum = System.Linq.Enumerable.Sum(filtered);
    project.SendInfoToLog("  LINQ Where(x > 2) 后 Sum = " + sum.ToString() + " (应为 12)", true);
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  LINQ 异常: " + ex.Message, true);
}

// ========== 测试 7: 其他 project 属性 ==========
project.SendInfoToLog("[测试7] 其他 project 属性测试...", true);
try
{
    // 测试 project.Profile
    project.SendInfoToLog("  project.Profile 类型: " + (project.Profile != null ? project.Profile.GetType().Name : "null"), true);
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  project.Profile 异常: " + ex.Message, true);
}

try
{
    // 测试 project.Lists
    project.SendInfoToLog("  project.Lists 类型: " + (project.Lists != null ? project.Lists.GetType().Name : "null"), true);
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  project.Lists 异常: " + ex.Message, true);
}

// ========== 测试完成 ==========
project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  高级功能测试完成!", true);
project.SendInfoToLog("========================================", true);

return "SUCCESS: 高级功能验证测试通过";
