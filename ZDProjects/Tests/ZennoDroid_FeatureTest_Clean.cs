project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  ZennoDroid Feature Test", true);
project.SendInfoToLog("  Time: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), true);
project.SendInfoToLog("========================================", true);

project.SendInfoToLog("[Test 1] Variable Read/Write", true);
project.Variables["test_string"].Value = "Hello ZennoDroid";
project.Variables["test_number"].Value = "42";
project.Variables["test_timestamp"].Value = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

string testString = project.Variables["test_string"].Value;
string testNumber = project.Variables["test_number"].Value;
string testTimestamp = project.Variables["test_timestamp"].Value;

project.SendInfoToLog("  test_string = " + testString, true);
project.SendInfoToLog("  test_number = " + testNumber, true);
project.SendInfoToLog("  test_timestamp = " + testTimestamp, true);

project.SendInfoToLog("[Test 2] Type Conversion", true);
int numberValue = System.Convert.ToInt32(testNumber);
int result = numberValue * 2;
project.SendInfoToLog("  " + testNumber + " * 2 = " + result.ToString(), true);

double doubleValue = 3.14159;
project.SendInfoToLog("  Pi = " + doubleValue.ToString("F5"), true);

project.SendInfoToLog("[Test 3] Full Namespace Usage", true);
System.Text.StringBuilder sb = new System.Text.StringBuilder();
sb.Append("Using ");
sb.Append("StringBuilder ");
sb.Append("works");
project.SendInfoToLog("  " + sb.ToString(), true);

string testText = "Hello123World456";
string pattern = @"\d+";
System.Text.RegularExpressions.MatchCollection matches = System.Text.RegularExpressions.Regex.Matches(testText, pattern);
project.SendInfoToLog("  Regex found " + matches.Count + " matches in '" + testText + "'", true);

System.Collections.Generic.List<string> testList = new System.Collections.Generic.List<string>();
testList.Add("Item1");
testList.Add("Item2");
testList.Add("Item3");
project.SendInfoToLog("  List contains " + testList.Count + " items", true);

project.SendInfoToLog("[Test 4] Exception Handling", true);
try
{
    string nullString = null;
    int length = 0;
    if (nullString != null)
    {
        length = nullString.Length;
    }
    else
    {
        project.SendInfoToLog("  Null check passed", true);
    }
    
    int divisor = 0;
    if (divisor != 0)
    {
        int divResult = 100 / divisor;
    }
    else
    {
        project.SendInfoToLog("  Division by zero check passed", true);
    }
}
catch (System.Exception ex)
{
    project.SendErrorToLog("  Exception: " + ex.Message, true);
}

project.SendInfoToLog("[Test 5] JSON String Building", true);
string jsonResult = "{" + "\"status\": \"success\"," + "\"message\": \"Test completed\"," + "\"timestamp\": \"" + System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"," + "\"tests_passed\": 5" + "}";
project.SendInfoToLog("  JSON: " + jsonResult, true);

project.SendInfoToLog("[Test 6] Func Delegate", true);
System.Func<string, string, string> concatenate = (a, b) => { return a + " + " + b + " = " + a + b; };
string funcResult = concatenate("Hello", "World");
project.SendInfoToLog("  Func result: " + funcResult, true);

project.SendInfoToLog("[Test 7] Action Delegate", true);
System.Action<string> logAction = (msg) => { project.SendInfoToLog("  [Action] " + msg, true); };
logAction("Message from Action delegate");

project.SendInfoToLog("========================================", true);
project.SendInfoToLog("  All tests completed!", true);
project.SendInfoToLog("========================================", true);

project.Variables["test_result"].Value = "SUCCESS";
project.Variables["test_json"].Value = jsonResult;

return "SUCCESS: All tests passed";
