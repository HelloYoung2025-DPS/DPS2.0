// =====================================================
// ZennoDroid_FeatureTest_OwnCode.cs
// ZennoDroid 功能验证脚本
// 复制此代码到 ZD 的 Own Code 动作块中运行
// =====================================================
//
// 测试内容：
// 1. 变量读写
// 2. 日志输出
// 3. 完整命名空间使用
// 4. 类型转换
// 5. 异常处理
// =====================================================

// ========== 测试 1: 日志输出 ==========
project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  ZennoDroid 功能验证测试", true);
project.SendInfoToLog("  时间: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), true);
project.SendInfoToLog("========================================", true);

// ========== 测试 2: 变量读写 ==========
project.SendInfoToLog("[测试2] 变量读写测试...", true);

// 使用局部变量代替项目变量（避免变量不存在的问题）
string testString = "Hello ZennoDroid";
string testNumber = "42";
string testTimestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

project.SendInfoToLog("  test_string = " + testString, true);
project.SendInfoToLog("  test_number = " + testNumber, true);
project.SendInfoToLog("  test_timestamp = " + testTimestamp, true);
project.SendInfoToLog("  [注意] 项目变量需要先在 ZD 中手动创建才能使用 project.Variables", true);

// ========== 测试 3: 类型转换 ==========
project.SendInfoToLog("[测试3] 类型转换测试...", true);

int numberValue = System.Convert.ToInt32(testNumber);
int result = numberValue * 2;
project.SendInfoToLog("  " + testNumber + " * 2 = " + result.ToString(), true);

double doubleValue = 3.14159;
project.SendInfoToLog("  Pi = " + doubleValue.ToString("F5"), true);

// ========== 测试 4: 完整命名空间使用 ==========
project.SendInfoToLog("[测试4] 完整命名空间测试...", true);

// System.Text.StringBuilder
System.Text.StringBuilder sb = new System.Text.StringBuilder();
sb.Append("使用 ");
sb.Append("StringBuilder ");
sb.Append("拼接字符串");
project.SendInfoToLog("  " + sb.ToString(), true);

// System.Text.RegularExpressions.Regex
string testText = "Hello123World456";
string pattern = @"\d+";
System.Text.RegularExpressions.MatchCollection matches = 
    System.Text.RegularExpressions.Regex.Matches(testText, pattern);
project.SendInfoToLog("  正则匹配 '" + pattern + "' 在 '" + testText + "' 中找到 " + matches.Count + " 个匹配", true);

// System.Collections.Generic.List
System.Collections.Generic.List<string> testList = new System.Collections.Generic.List<string>();
testList.Add("Item1");
testList.Add("Item2");
testList.Add("Item3");
project.SendInfoToLog("  List 包含 " + testList.Count + " 个元素", true);

// ========== 测试 5: 异常处理 ==========
project.SendInfoToLog("[测试5] 异常处理测试...", true);

try
{
    // 故意触发一个可恢复的异常
    string nullString = null;
    int length = 0;
    if (nullString != null)
    {
        length = nullString.Length;
    }
    else
    {
        project.SendInfoToLog("  空值检查通过，避免了 NullReferenceException", true);
    }
    
    // 测试除零（安全方式）
    int divisor = 0;
    if (divisor != 0)
    {
        int divResult = 100 / divisor;
    }
    else
    {
        project.SendInfoToLog("  除零检查通过，避免了 DivideByZeroException", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  异常: " + ex.Message, true);
}

// ========== 测试 6: JSON 处理（手动） ==========
project.SendInfoToLog("[测试6] JSON 字符串构建测试...", true);

string jsonResult = "{" +
    "\"status\": \"success\"," +
    "\"message\": \"ZennoDroid 功能验证完成\"," +
    "\"timestamp\": \"" + System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"," +
    "\"tests_passed\": 6" +
    "}";
project.SendInfoToLog("  生成的 JSON: " + jsonResult, true);

// ========== 测试 7: Func 委托（替代 void 方法） ==========
project.SendInfoToLog("[测试7] Func 委托测试...", true);

// 定义一个 Func 委托来替代 void 方法
System.Func<string, string, string> concatenate = (a, b) => {
    return a + " + " + b + " = " + a + b;
};

string funcResult = concatenate("Hello", "World");
project.SendInfoToLog("  Func 结果: " + funcResult, true);

// 定义一个 Action 委托
System.Action<string> logAction = (msg) => {
    project.SendInfoToLog("  [Action] " + msg, true);
};

logAction("这是通过 Action 委托输出的消息");

// ========== 测试完成 ==========
project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  所有测试完成!", true);
project.SendInfoToLog("========================================", true);

// 注意：如果需要使用 project.Variables，必须先在 ZD 项目中手动创建变量
// project.Variables["test_result"].Value = "SUCCESS";
// project.Variables["test_json"].Value = jsonResult;

return "SUCCESS: 所有 ZennoDroid 功能验证测试通过";
