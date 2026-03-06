# -*- coding: utf-8 -*-
"""
DPS v4.5 App Onboarder - 配置生成器
从 AppMap 数据结构自动生成平台配置、操作文件和 E2E 测试脚本。
"""

import json
import os
import copy
import datetime


class ConfigGenerator(object):
    """
    接收 app_map 字典，生成 DPS v4.5 所需的所有配置文件：
    - PlatformsConfig.json 中的平台条目
    - {platform}_operations.json 操作定义
    - {platform}_e2e_test.ps1 端到端测试脚本
    """

    def __init__(self, app_map, dps_root, output_dir=None):
        """
        初始化配置生成器。

        参数:
            app_map (dict): 由 AppExplorer.run() 生成的应用结构数据
            dps_root (str): DPS v4.5 项目根目录路径
            output_dir (str): 生成文件的输出目录，默认为 dps_root
        """
        self.app_map = app_map
        self.dps_root = dps_root
        self.output_dir = output_dir or dps_root
        self.platform_key = app_map.get("platform_key", "unknown")
        self.package_name = app_map.get("package_name", "")
        self.app_name = app_map.get("app_name", "")
        self.screen_w, self.screen_h = app_map.get("screen_size", (1440, 3120))
        self.feed_type = app_map.get("feed_type", "recycler_vertical")
        self.is_webview = app_map.get("post_detail_is_webview", False)

        # 路径: 配置始终写入 dps_root 下，E2E 测试写入 output_dir
        self.config_dir = os.path.join(dps_root, "Config")
        self.operations_dir = os.path.join(self.config_dir, "Operations")
        self.platforms_config_path = os.path.join(self.config_dir, "PlatformsConfig.json")

    # ================================================================
    # 公共接口
    # ================================================================

    def generate_all(self):
        """
        生成所有配置文件，返回汇总信息。

        返回:
            dict: 包含各生成文件路径和状态的汇总
        """
        summary = {
            "platform_key": self.platform_key,
            "generated_files": [],
            "errors": [],
            "timestamp": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        }

        # 1. 生成平台配置并合并到 PlatformsConfig.json
        try:
            platform_config = self.generate_platform_config()
            summary["generated_files"].append({
                "type": "platform_config",
                "path": self.platforms_config_path,
                "status": "ok",
            })
        except Exception as e:
            summary["errors"].append({
                "type": "platform_config",
                "error": str(e),
            })

        # 2. 生成操作文件
        try:
            operations = self.generate_operations()
            ops_path = os.path.join(
                self.operations_dir,
                "{0}_operations.json".format(self.platform_key)
            )
            self._ensure_dir(self.operations_dir)
            with open(ops_path, "w", encoding="utf-8") as f:
                json.dump(operations, f, indent=4, ensure_ascii=False)
            summary["generated_files"].append({
                "type": "operations",
                "path": ops_path,
                "status": "ok",
            })
        except Exception as e:
            summary["errors"].append({
                "type": "operations",
                "error": str(e),
            })

        # 3. 生成 E2E 测试脚本
        try:
            test_content = self.generate_e2e_test()
            test_dir = self.output_dir
            self._ensure_dir(test_dir)
            test_path = os.path.join(
                test_dir,
                "{0}_e2e_test.ps1".format(self.platform_key)
            )
            # UTF-8 with BOM: PowerShell 需要 BOM 才能正确处理中文注释
            with open(test_path, "wb") as f:
                f.write(b"\xef\xbb\xbf")  # UTF-8 BOM
                f.write(test_content.encode("utf-8"))
            summary["generated_files"].append({
                "type": "e2e_test",
                "path": test_path,
                "status": "ok",
            })
        except Exception as e:
            summary["errors"].append({
                "type": "e2e_test",
                "error": str(e),
            })

        summary["success"] = len(summary["errors"]) == 0

        # 构建简单的 type→path 映射，供 main.py 直接消费
        # main.py 的 step_generate/step_test/step_summary 期望 {"platform_config": path, "operations": path, "e2e_test": path}
        result_paths = {}
        for item in summary["generated_files"]:
            result_paths[item["type"]] = item["path"]
        # 对于生成失败的文件，设置为 None 以便 main.py 打印 "[跳过]"
        for err in summary["errors"]:
            result_paths[err["type"]] = None
        return result_paths

    def generate_platform_config(self):
        """
        生成平台配置条目，合并到 PlatformsConfig.json 并返回新条目。

        步骤:
            1. 读取现有 PlatformsConfig.json
            2. 构建新平台条目
            3. 添加到 platforms.{platform_key}
            4. 写回合并后的文件

        返回:
            dict: 新生成的平台配置字典
        """
        config = {
            "name": self.app_name,
            "package_name": self.package_name,
            "enabled": True,
            "rate_limits": self._build_rate_limits(),
            "ui_selectors": self._build_ui_selectors(),
            "page_signatures": self._build_page_signatures(),
            "action_weights": self._build_action_weights(),
            "scroll_config": self._build_scroll_config(),
            "error_thresholds": {
                "max_ui_not_found": 6,
                "max_app_crashes": 2,
                "max_network_errors": 8,
            },
            "verified": False,
            "verified_date": None,
            "notes": "由 App Onboarder 自动生成 ({0})".format(
                datetime.datetime.now().strftime("%Y-%m-%d")
            ),
        }

        # 读取、合并、写回 PlatformsConfig.json
        self._merge_platform_config(config)

        return config

    def generate_operations(self):
        """
        生成 {platform}_operations.json 操作定义。

        返回:
            dict: 完整的操作文件内容
        """
        ops = {
            "platform": self.platform_key,
            "version": "3.0",
            "verified": False,
            "verified_date": None,
            "architecture_notes": self._build_architecture_notes(),
            "operations": {},
        }

        ops["operations"]["navigate_to_feed"] = self._op_navigate_to_feed()
        ops["operations"]["browse"] = self._op_browse()
        ops["operations"]["open_post"] = self._op_open_post()
        ops["operations"]["read_post"] = self._op_read_post()

        # WebView 应用需要额外的滚动到反应按钮操作
        if self.is_webview:
            ops["operations"]["scroll_to_reactions"] = self._op_scroll_to_reactions()

        ops["operations"]["like"] = self._op_like()
        ops["operations"]["comment"] = self._op_comment()
        ops["operations"]["back_to_feed"] = self._op_back_to_feed()

        return ops

    def generate_e2e_test(self):
        """
        生成 PowerShell E2E 测试脚本内容。

        返回:
            str: PowerShell 脚本内容（不含 BOM，BOM 在写入时加）
        """
        # 从 app_map 提取必要信息
        feed_tab = self._get_feed_tab()
        feed_tab_id = feed_tab.get("short_id", "") if feed_tab else ""
        feed_tab_desc = feed_tab.get("text", "") if feed_tab else ""
        post_container_id = self.app_map.get("post_container_id", "postContainer")
        feed_container_id = self.app_map.get("feed_container_id", "posts")
        webview_container_id = self.app_map.get("webview_container_id", "webViewLayout")

        action_buttons = self.app_map.get("action_buttons", {})
        like_btn = action_buttons.get("like", {})
        comment_btn = action_buttons.get("comment", {})
        comment_input_btn = action_buttons.get("comment_input", {})
        back_btn = action_buttons.get("back", {})

        # 滑动参数：根据 feed_type 决定方向
        is_horizontal = self.feed_type == "viewpager_horizontal"

        # 屏幕中心坐标
        cx = self.screen_w // 2
        cy = self.screen_h // 2

        # 水平滑动参数
        if is_horizontal:
            swipe_start_x = int(self.screen_w * 0.76)
            swipe_end_x = int(self.screen_w * 0.21)
            swipe_y = int(self.screen_h * 0.58)
            swipe_desc = "横向浏览帖子（左滑 ViewPager）"
            swipe_comment = "水平滑动 ViewPager 查看下一张帖子卡片"
        else:
            swipe_start_x = cx
            swipe_end_x = cx
            swipe_y = 0  # 不用于垂直滑动
            swipe_desc = "纵向浏览帖子（上滑 RecyclerView）"
            swipe_comment = "垂直滚动 RecyclerView 查看更多帖子"

        # 底部导航中各 tab 的检测
        nav_tabs = self.app_map.get("bottom_nav_tabs", {})
        home_tab_info = None
        for key, tab in nav_tabs.items():
            if "home" in key.lower():
                home_tab_info = tab
                break

        # 构建 like 按钮查找逻辑
        like_strategy = like_btn.get("strategy", "text")
        like_value = like_btn.get("value", "")

        # 构建 comment 按钮查找逻辑
        comment_strategy = comment_btn.get("strategy", "text")
        comment_value = comment_btn.get("value", "")
        comment_fallback = comment_btn.get("fallback_value", "")

        # 构建 comment input 查找逻辑
        input_strategy = comment_input_btn.get("strategy", "text")
        input_value = comment_input_btn.get("value", "")

        # 构建 back 按钮查找逻辑
        back_strategy = back_btn.get("strategy", "content-desc")
        back_value = back_btn.get("value", "Navigate up")

        # 阶段数量
        total_phases = 7

        # ============================================================
        # 组装 PowerShell 脚本
        # ============================================================
        lines = []
        L = lines.append

        L("# {0} Android E2E Humanized Test Script (ADB + UIAutomator)".format(self.app_name))
        L("# 流程: Launch -> Navigate to Feed -> Browse Feed -> Open Post -> Scroll/Reactions -> Like -> Comment -> Back")
        L("# 版本: 1.0 (自动生成 {0})".format(datetime.datetime.now().strftime("%Y-%m-%d")))
        L("")
        L("Set-StrictMode -Version Latest")
        L('$ErrorActionPreference = "Continue"')
        L("")

        # 全局配置
        L("# ========================================")
        L("# 全局配置")
        L("# ========================================")
        L('$PackageName = "{0}"'.format(self.package_name))
        L('$WorkDir = "{0}"'.format(os.path.expanduser("~").replace("\\", "\\\\")))
        L('$UiRemotePath = "/sdcard/window_dump.xml"')
        L("")

        # 阶段结果追踪
        L("# ========================================")
        L("# 阶段结果追踪")
        L("# ========================================")
        L("$results = @{")
        for i in range(1, total_phases + 1):
            L("    {0} = $false".format(i))
        L("}")
        L("$phaseDetails = @{")
        for i in range(1, total_phases + 1):
            L('    {0} = ""'.format(i))
        L("}")
        L("")
        L("# 保存 Phase 5 找到的 reaction 按钮供 Phase 6 使用")
        L("$script:reactionElement = $null")
        L("")

        # Helper Functions（与参考模板保持一致）
        L("# ========================================")
        L("# Helper Functions")
        L("# ========================================")
        L("function LogPhase {")
        L("    param(")
        L("        [int]$phase,")
        L("        [string]$message")
        L("    )")
        L('    Write-Host ("[Phase {0}] {1}" -f $phase, $message) -ForegroundColor Cyan')
        L("}")
        L("")
        L("function LogResult {")
        L("    param(")
        L("        [int]$phase,")
        L("        [bool]$passed,")
        L("        [string]$details")
        L("    )")
        L('    $tag = if ($passed) { "PASS" } else { "FAIL" }')
        L('    $color = if ($passed) { "Green" } else { "Red" }')
        L('    Write-Host ("[{0}] Phase {1}: {2}" -f $tag, $phase, $details) -ForegroundColor $color')
        L("}")
        L("")
        L("function Mark-Phase {")
        L("    param(")
        L("        [int]$phase,")
        L("        [bool]$passed,")
        L("        [string]$details")
        L("    )")
        L("    $script:results[$phase] = $passed")
        L("    $script:phaseDetails[$phase] = $details")
        L("    LogResult -phase $phase -passed $passed -details $details")
        L("}")
        L("")
        L("function HumanDelay {")
        L("    param(")
        L("        [int]$minMs,")
        L("        [int]$maxMs")
        L("    )")
        L("    if ($maxMs -lt $minMs) {")
        L("        $tmp = $minMs; $minMs = $maxMs; $maxMs = $tmp")
        L("    }")
        L("    $delay = Get-Random -Minimum $minMs -Maximum ($maxMs + 1)")
        L("    Start-Sleep -Milliseconds $delay")
        L("}")
        L("")
        L("function Parse-Bounds {")
        L("    param(")
        L("        [string]$bounds")
        L("    )")
        L('    $m = [regex]::Match($bounds, "\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]")')
        L("    if (-not $m.Success) { return $null }")
        L("")
        L("    $x1 = [int]$m.Groups[1].Value")
        L("    $y1 = [int]$m.Groups[2].Value")
        L("    $x2 = [int]$m.Groups[3].Value")
        L("    $y2 = [int]$m.Groups[4].Value")
        L("    $cx = [math]::Floor(($x1 + $x2) / 2)")
        L("    $cy = [math]::Floor(($y1 + $y2) / 2)")
        L("")
        L("    return @{")
        L("        x1 = $x1; y1 = $y1; x2 = $x2; y2 = $y2")
        L("        cx = $cx; cy = $cy")
        L("        bounds = $bounds")
        L("    }")
        L("}")
        L("")
        L("function DumpUI {")
        L("    param(")
        L("        [string]$name")
        L("    )")
        L('    $localPath = Join-Path $WorkDir ("{0}_e2e_{{0}}.xml" -f $name)'.format(
            self.platform_key
        ))
        L("")
        L("    $dumpResult = & adb shell uiautomator dump $UiRemotePath 2>&1")
        L("    Start-Sleep -Milliseconds 800")
        L("    $pullResult = & adb pull $UiRemotePath $localPath 2>&1")
        L("")
        L("    if (-not (Test-Path $localPath)) {")
        L('        throw "DumpUI($name) 失败: 未找到本地 XML: $localPath"')
        L("    }")
        L("")
        L("    $xml = Get-Content -Path $localPath -Raw -Encoding UTF8")
        L("    if ([string]::IsNullOrWhiteSpace($xml)) {")
        L('        throw "DumpUI($name) XML 内容为空"')
        L("    }")
        L("    return $xml")
        L("}")
        L("")
        L("function FindByText {")
        L("    param(")
        L("        [string]$xml,")
        L("        [string]$text")
        L("    )")
        L("    $escaped = [regex]::Escape($text)")
        L('    $pattern = \'text="\' + $escaped + \'"[^>]*bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"\'')
        L("    $allMatches = [regex]::Matches($xml, $pattern)")
        L("    if ($allMatches.Count -eq 0) {")
        L('        $pattern2 = \'bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"[^>]*text="\' + $escaped + \'"\'')
        L("        $allMatches = [regex]::Matches($xml, $pattern2)")
        L("    }")
        L("    if ($allMatches.Count -eq 0) { return $null }")
        L("")
        L("    foreach ($m in $allMatches) {")
        L("        $parsed = Parse-Bounds -bounds $m.Groups[1].Value")
        L("        if ($null -ne $parsed -and -not ($parsed.x1 -eq 0 -and $parsed.y1 -eq 0 -and $parsed.x2 -eq 0 -and $parsed.y2 -eq 0)) {")
        L("            return $parsed")
        L("        }")
        L("    }")
        L("    return Parse-Bounds -bounds $allMatches[0].Groups[1].Value")
        L("}")
        L("")
        L("function FindByResourceId {")
        L("    param(")
        L("        [string]$xml,")
        L("        [string]$id")
        L("    )")
        L("    $escaped = [regex]::Escape($id)")
        L('    $pattern = \'resource-id="[^"]*\' + $escaped + \'"[^>]*bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"\'')
        L("    $matches2 = [regex]::Matches($xml, $pattern)")
        L("    if ($matches2.Count -eq 0) {")
        L('        $pattern2 = \'bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"[^>]*resource-id="[^"]*\' + $escaped + \'"\'')
        L("        $matches2 = [regex]::Matches($xml, $pattern2)")
        L("    }")
        L("    if ($matches2.Count -eq 0) { return $null }")
        L("")
        L("    foreach ($m2 in $matches2) {")
        L("        $parsed = Parse-Bounds -bounds $m2.Groups[1].Value")
        L("        if ($null -ne $parsed -and ($parsed.x2 - $parsed.x1) -gt 50) {")
        L("            return $parsed")
        L("        }")
        L("    }")
        L("    return Parse-Bounds -bounds $matches2[0].Groups[1].Value")
        L("}")
        L("")
        L("function FindByContentDesc {")
        L("    param(")
        L("        [string]$xml,")
        L("        [string]$desc")
        L("    )")
        L("    $escaped = [regex]::Escape($desc)")
        L('    $pattern = \'content-desc="\' + $escaped + \'"[^>]*bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"\'')
        L("    $m = [regex]::Match($xml, $pattern)")
        L("    if (-not $m.Success) {")
        L('        $pattern2 = \'bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"[^>]*content-desc="\' + $escaped + \'"\'')
        L("        $m = [regex]::Match($xml, $pattern2)")
        L("        if (-not $m.Success) { return $null }")
        L("    }")
        L("    return Parse-Bounds -bounds $m.Groups[1].Value")
        L("}")
        L("")
        L("function TapElement {")
        L("    param(")
        L("        [hashtable]$element")
        L("    )")
        L("    if ($null -eq $element) {")
        L('        throw "TapElement 收到空元素"')
        L("    }")
        L("    $offsetX = Get-Random -Minimum -5 -Maximum 6")
        L("    $offsetY = Get-Random -Minimum -5 -Maximum 6")
        L("    $x = [int]$element.cx + $offsetX")
        L("    $y = [int]$element.cy + $offsetY")
        L("    & adb shell input tap $x $y 2>&1 | Out-Null")
        L("}")
        L("")
        L("function SwipeScreen {")
        L("    param(")
        L("        [int]$startX, [int]$startY,")
        L("        [int]$endX, [int]$endY,")
        L("        [int]$duration")
        L("    )")
        L("    $sx = $startX + (Get-Random -Minimum -8 -Maximum 9)")
        L("    $sy = $startY + (Get-Random -Minimum -6 -Maximum 7)")
        L("    $ex = $endX + (Get-Random -Minimum -8 -Maximum 9)")
        L("    $ey = $endY + (Get-Random -Minimum -6 -Maximum 7)")
        L("    $dur = $duration + (Get-Random -Minimum -60 -Maximum 61)")
        L("    if ($dur -lt 200) { $dur = 200 }")
        L("    & adb shell input swipe $sx $sy $ex $ey $dur 2>&1 | Out-Null")
        L("}")
        L("")
        L("function ScrollDown {")
        L("    param(")
        L("        [int]$distance = 800")
        L("    )")
        L("    $centerX = {0} + (Get-Random -Minimum -15 -Maximum 16)".format(cx))
        L("    $startY = {0} + (Get-Random -Minimum -20 -Maximum 21)".format(
            int(self.screen_h * 0.65)
        ))
        L("    $endY = $startY - $distance")
        L("    if ($endY -lt 300) { $endY = 300 }")
        L("    $dur = 600 + (Get-Random -Minimum -60 -Maximum 61)")
        L("    & adb shell input swipe $centerX $startY $centerX $endY $dur 2>&1 | Out-Null")
        L("}")
        L("")
        L("function Is-NonZeroBounds {")
        L("    param(")
        L("        [hashtable]$element")
        L("    )")
        L("    if ($null -eq $element) { return $false }")
        L("    return -not ($element.x1 -eq 0 -and $element.y1 -eq 0 -and $element.x2 -eq 0 -and $element.y2 -eq 0)")
        L("}")
        L("")

        # Pre-check
        L("# ========================================")
        L("# Pre-check: ADB 连接状态")
        L("# ========================================")
        L('Write-Host "========================================" -ForegroundColor Yellow')
        L('Write-Host "  {0} E2E Humanized Test" -ForegroundColor Yellow'.format(self.app_name))
        L('Write-Host "========================================" -ForegroundColor Yellow')
        L('Write-Host ""')
        L("")
        L("try {")
        L('    $state = (& adb get-state 2>&1) -join ""')
        L('    if ($state -notmatch "device") {')
        L('        Write-Host "[FATAL] ADB 设备未连接。状态: $state" -ForegroundColor Red')
        L("        exit 1")
        L("    }")
        L('    Write-Host "[OK] ADB 设备已连接" -ForegroundColor Green')
        L('    Write-Host ""')
        L("}")
        L("catch {")
        L('    Write-Host "[FATAL] ADB 检查失败: $($_.Exception.Message)" -ForegroundColor Red')
        L("    exit 1")
        L("}")
        L("")

        # Phase 1: Launch
        L("# ========================================")
        L("# Phase 1: Launch & Initial State")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 1 -message "启动 {0} 应用"'.format(self.app_name))
        L("    & adb shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null")
        L("    HumanDelay -minMs 8000 -maxMs 9500")
        L("")
        L('    $xml = DumpUI -name "p1_launch"')
        L("")
        L("    # 检查并关闭弹窗")
        L('    $closeBtn = FindByResourceId -xml $xml -id "closeBtn"')
        L("    if ($null -ne $closeBtn) {")
        L('        LogPhase -phase 1 -message "检测到弹窗，正在关闭..."')
        L("        TapElement -element $closeBtn")
        L("        HumanDelay -minMs 1000 -maxMs 2000")
        L('        $xml = DumpUI -name "p1_after_popup"')
        L("    }")
        L("")
        L("    # 验证主界面")

        # 根据底部导航 tab 确定检测元素
        nav_check_ids = []
        for key, tab in nav_tabs.items():
            if tab.get("short_id"):
                nav_check_ids.append(tab["short_id"])
        if not nav_check_ids:
            nav_check_ids = ["bottom_navigator", "menu_home"]

        for i, rid in enumerate(nav_check_ids[:2]):
            var_name = "navCheck{0}".format(i + 1)
            L('    ${0} = FindByResourceId -xml $xml -id "{1}"'.format(var_name, rid))

        check_expr = " -or ".join(
            ["($null -ne $navCheck{0})".format(i + 1) for i in range(min(2, len(nav_check_ids)))]
        )
        L("")
        L("    if ({0}) {{".format(check_expr))
        L('        Mark-Phase -phase 1 -passed $true -details "应用已启动，主界面可见"')
        L("    } else {")
        L('        Mark-Phase -phase 1 -passed $false -details "未检测到底部导航元素"')
        L("    }")
        L("}")
        L("catch {")
        L('    Mark-Phase -phase 1 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Phase 2: Navigate to Feed
        L("# ========================================")
        L("# Phase 2: Navigate to Feed")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 2 -message "导航到 Feed 标签"')
        L('    $xml = DumpUI -name "p2_before"')
        L("")
        if feed_tab_id:
            L('    $feedTab = FindByResourceId -xml $xml -id "{0}"'.format(feed_tab_id))
            if feed_tab_desc:
                L("    if ($null -eq $feedTab) {")
                L('        $feedTab = FindByContentDesc -xml $xml -desc "{0}"'.format(feed_tab_desc))
                L("    }")
        elif feed_tab_desc:
            L('    $feedTab = FindByContentDesc -xml $xml -desc "{0}"'.format(feed_tab_desc))
        else:
            L("    $feedTab = $null")

        L("    if ($null -eq $feedTab) {")
        L('        throw "未找到 Feed 标签 ({0} / {1})"'.format(feed_tab_id, feed_tab_desc))
        L("    }")
        L("")
        L("    TapElement -element $feedTab")
        L("    HumanDelay -minMs 5000 -maxMs 7000")
        L("")
        L('    $xml2 = DumpUI -name "p2_after"')
        L('    $postContainer = FindByResourceId -xml $xml2 -id "{0}"'.format(post_container_id))
        L('    $feedView = FindByResourceId -xml $xml2 -id "{0}"'.format(feed_container_id))
        L("")
        L("    if (($null -ne $postContainer) -or ($null -ne $feedView)) {")
        L('        Mark-Phase -phase 2 -passed $true -details "Feed 可见 ({0}/{1})"'.format(
            post_container_id, feed_container_id
        ))
        L("    } else {")
        L('        Mark-Phase -phase 2 -passed $false -details "未检测到 Feed 核心元素"')
        L("    }")
        L("}")
        L("catch {")
        L('    Mark-Phase -phase 2 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Phase 3: Browse Feed
        L("# ========================================")
        L("# Phase 3: Browse Feed ({0})".format("Horizontal Swipe" if is_horizontal else "Vertical Scroll"))
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 3 -message "{0}"'.format(swipe_desc))
        L("")

        if is_horizontal:
            L("    # 第一次左滑")
            L("    SwipeScreen -startX {0} -startY {1} -endX {2} -endY {1} -duration 500".format(
                swipe_start_x, swipe_y, swipe_end_x
            ))
            L("    HumanDelay -minMs 2000 -maxMs 4000")
            L("")
            L('    $xml = DumpUI -name "p3_swipe1"')
            L('    $postAfter1 = FindByResourceId -xml $xml -id "{0}"'.format(post_container_id))
            L("    if ($null -eq $postAfter1) {")
            L('        LogPhase -phase 3 -message "警告: 第一次左滑后未检测到 {0}，继续尝试"'.format(post_container_id))
            L("    }")
            L("")
            L("    # 第二次左滑")
            L("    SwipeScreen -startX {0} -startY {1} -endX {2} -endY {1} -duration 500".format(
                swipe_start_x, swipe_y, swipe_end_x
            ))
            L("    HumanDelay -minMs 2000 -maxMs 3500")
            L("")
            L('    $xml2 = DumpUI -name "p3_swipe2"')
            L('    $postAfter2 = FindByResourceId -xml $xml2 -id "{0}"'.format(post_container_id))
        else:
            vert_start_y = int(self.screen_h * 0.7)
            vert_end_y = int(self.screen_h * 0.3)
            L("    # 第一次向上滑动")
            L("    SwipeScreen -startX {0} -startY {1} -endX {0} -endY {2} -duration 500".format(
                cx, vert_start_y, vert_end_y
            ))
            L("    HumanDelay -minMs 2000 -maxMs 4000")
            L("")
            L('    $xml = DumpUI -name "p3_scroll1"')
            L('    $postAfter1 = FindByResourceId -xml $xml -id "{0}"'.format(post_container_id))
            L("    if ($null -eq $postAfter1) {")
            L('        LogPhase -phase 3 -message "警告: 第一次滚动后未检测到 {0}，继续尝试"'.format(post_container_id))
            L("    }")
            L("")
            L("    # 第二次向上滑动")
            L("    SwipeScreen -startX {0} -startY {1} -endX {0} -endY {2} -duration 500".format(
                cx, vert_start_y, vert_end_y
            ))
            L("    HumanDelay -minMs 2000 -maxMs 3500")
            L("")
            L('    $xml2 = DumpUI -name "p3_scroll2"')
            L('    $postAfter2 = FindByResourceId -xml $xml2 -id "{0}"'.format(post_container_id))

        L("")
        L("    if (($null -ne $postAfter1) -or ($null -ne $postAfter2)) {")
        L('        Mark-Phase -phase 3 -passed $true -details "已完成 2 次浏览操作"')
        L("    } else {")
        L('        Mark-Phase -phase 3 -passed $false -details "浏览后未检测到 {0}"'.format(post_container_id))
        L("    }")
        L("}")
        L("catch {")
        L('    Mark-Phase -phase 3 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Phase 4: Open Post Detail
        L("# ========================================")
        L("# Phase 4: Open Post Detail")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 4 -message "打开帖子详情"')
        L('    $xml = DumpUI -name "p4_before"')
        L("")
        L('    $targetPost = FindByResourceId -xml $xml -id "{0}"'.format(post_container_id))
        L("    if ($null -eq $targetPost) {")
        L('        throw "未找到 {0} 可点击"'.format(post_container_id))
        L("    }")
        L("")
        L("    TapElement -element $targetPost")

        if self.is_webview:
            L("    HumanDelay -minMs 8000 -maxMs 11000  # WebView 加载较慢")
        else:
            L("    HumanDelay -minMs 4000 -maxMs 6000")

        L("")
        L('    $xml2 = DumpUI -name "p4_after"')

        if self.is_webview:
            L('    $webView = FindByResourceId -xml $xml2 -id "{0}"'.format(webview_container_id))
            L("    if ($null -eq $webView) {")
            L("        # 兜底：检查 WebView class")
            L('        $wvPattern = \'class="android.webkit.WebView"[^>]*bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"\'')
            L("        $wvMatch = [regex]::Match($xml2, $wvPattern)")
            L("        if ($wvMatch.Success) {")
            L("            $webView = Parse-Bounds -bounds $wvMatch.Groups[1].Value")
            L("        }")
            L("    }")
            L("")
            L("    if ($null -ne $webView) {")
            L('        Mark-Phase -phase 4 -passed $true -details "WebView 已加载 ({0} 存在)"'.format(webview_container_id))
            L("    } else {")
            L('        Mark-Phase -phase 4 -passed $false -details "未检测到 WebView，可能未加载"')
            L("    }")
        else:
            # 原生详情页：检查 back 按钮或 detail 页面的标志元素
            L("    # 原生详情页验证")
            if back_strategy == "content-desc":
                L('    $detailCheck = FindByContentDesc -xml $xml2 -desc "{0}"'.format(back_value))
            else:
                L('    $detailCheck = FindByResourceId -xml $xml2 -id "{0}"'.format(back_value))
            L("")
            L("    if ($null -ne $detailCheck) {")
            L('        Mark-Phase -phase 4 -passed $true -details "帖子详情页已加载"')
            L("    } else {")
            L('        Mark-Phase -phase 4 -passed $false -details "未检测到详情页标志元素"')
            L("    }")

        L("}")
        L("catch {")
        L('    Mark-Phase -phase 4 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Phase 5: Read & Scroll to Reactions
        L("# ========================================")
        L("# Phase 5: Read & Scroll to Reactions")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 5 -message "模拟阅读并滚动到 reactions 区域"')
        L("")
        L("    # 模拟阅读停留")
        L("    HumanDelay -minMs 3000 -maxMs 6000")
        L("")
        L("    $foundReaction = $false")
        L("    $maxTry = 8")
        L("")
        L("    for ($i = 1; $i -le $maxTry; $i++) {")
        L('        LogPhase -phase 5 -message "滚动尝试 $i/$maxTry ..."')
        L("        ScrollDown -distance 600")
        L("        HumanDelay -minMs 1500 -maxMs 2500")
        L("")
        L('        $xml = DumpUI -name ("p5_scroll_$i")')

        # 根据 like 按钮策略查找
        if like_strategy == "text":
            L('        $candidate = FindByText -xml $xml -text "{0}"'.format(like_value))
        elif like_strategy == "resource-id":
            L('        $candidate = FindByResourceId -xml $xml -id "{0}"'.format(like_value))
        elif like_strategy == "content-desc":
            L('        $candidate = FindByContentDesc -xml $xml -desc "{0}"'.format(like_value))
        else:
            L('        $candidate = FindByText -xml $xml -text "{0}"'.format(like_value))

        L("")
        L("        if ($null -ne $candidate) {")
        L("            if (Is-NonZeroBounds -element $candidate) {")
        L("                $script:reactionElement = $candidate")
        L("                $foundReaction = $true")
        L('                LogPhase -phase 5 -message "找到 reaction 按钮! bounds=$($candidate.bounds) cx=$($candidate.cx) cy=$($candidate.cy)"')
        L("                break")
        L("            } else {")
        L('                LogPhase -phase 5 -message "找到 reaction 按钮但 bounds=[0,0][0,0]，继续滚动..."')
        L("            }")
        L("        }")
        L("    }")
        L("")
        L("    if ($foundReaction) {")
        L('        Mark-Phase -phase 5 -passed $true -details "已找到 reaction 按钮且 bounds 非零 (尝试 $i 次)"')
        L("    } else {")
        L('        Mark-Phase -phase 5 -passed $false -details "滚动 $maxTry 次后仍未定位有效 reaction 按钮"')
        L("    }")
        L("}")
        L("catch {")
        L('    Mark-Phase -phase 5 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Phase 6: Like Post
        L("# ========================================")
        L("# Phase 6: Like Post")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 6 -message "点击 reaction 按钮并尝试点赞"')
        L("")
        L("    if ($null -eq $script:reactionElement) {")
        L('        throw "未从 Phase 5 获取到 reaction 按钮位置"')
        L("    }")
        L("")
        L("    TapElement -element $script:reactionElement")
        L("    HumanDelay -minMs 2500 -maxMs 3500")
        L("")
        L('    $xml = DumpUI -name "p6_after_reaction_tap"')
        L("")
        L('    $reactionTexts = @("Love", "Like", "Haha", "Wow", "Sad", "Angry")')
        L("    $picked = $null")
        L("    foreach ($rt in $reactionTexts) {")
        L("        $el = FindByText -xml $xml -text $rt")
        L("        if ($null -ne $el -and (Is-NonZeroBounds -element $el)) {")
        L("            $picked = $el")
        L('            LogPhase -phase 6 -message "找到表情选项: $rt at bounds=$($el.bounds)"')
        L("            break")
        L("        }")
        L("    }")
        L("")
        L("    if ($null -ne $picked) {")
        L("        TapElement -element $picked")
        L("        HumanDelay -minMs 1500 -maxMs 2500")
        L('        Mark-Phase -phase 6 -passed $true -details "reaction 按钮可点击，已选择表情"')
        L("    } else {")
        L('        Mark-Phase -phase 6 -passed $true -details "reaction 按钮可点击（表情选择器选项未明确识别，可能已直接点赞）"')
        L("    }")
        L("}")
        L("catch {")
        L('    Mark-Phase -phase 6 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Phase 7: Comment
        L("# ========================================")
        L("# Phase 7: Comment (Attempt)")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 7 -message "尝试评论流程"')
        L("")
        L('    $xml = DumpUI -name "p7_before"')
        L("")
        L("    # 查找评论按钮")
        if comment_strategy == "text":
            L('    $commentBtn = FindByText -xml $xml -text "{0}"'.format(comment_value))
            L("    if ($null -eq $commentBtn -or -not (Is-NonZeroBounds -element $commentBtn)) {")
            if comment_fallback:
                L('        LogPhase -phase 7 -message "{0} 未找到或 bounds 为零，尝试 {1}..."'.format(
                    comment_value, comment_fallback
                ))
                L('        $commentBtn = FindByText -xml $xml -text "{0}"'.format(comment_fallback))
            else:
                L('        LogPhase -phase 7 -message "评论按钮未找到或 bounds 为零"')
            L("    }")
        elif comment_strategy == "resource-id":
            L('    $commentBtn = FindByResourceId -xml $xml -id "{0}"'.format(comment_value))
        elif comment_strategy == "content-desc":
            L('    $commentBtn = FindByContentDesc -xml $xml -desc "{0}"'.format(comment_value))
        else:
            L('    $commentBtn = FindByText -xml $xml -text "{0}"'.format(comment_value))

        L("")
        L("    if ($null -eq $commentBtn -or -not (Is-NonZeroBounds -element $commentBtn)) {")
        L('        LogPhase -phase 7 -message "评论按钮不可见，尝试向上滚动..."')
        L("        & adb shell input swipe {0} 800 {0} 2000 600 2>&1 | Out-Null".format(cx))
        L("        HumanDelay -minMs 1500 -maxMs 2500")
        L('        $xml = DumpUI -name "p7_scroll_up"')
        if comment_strategy == "text":
            L('        $commentBtn = FindByText -xml $xml -text "{0}"'.format(comment_value))
            if comment_fallback:
                L("        if ($null -eq $commentBtn) {")
                L('            $commentBtn = FindByText -xml $xml -text "{0}"'.format(comment_fallback))
                L("        }")
        elif comment_strategy == "resource-id":
            L('        $commentBtn = FindByResourceId -xml $xml -id "{0}"'.format(comment_value))
        elif comment_strategy == "content-desc":
            L('        $commentBtn = FindByContentDesc -xml $xml -desc "{0}"'.format(comment_value))
        L("    }")
        L("")
        L("    if ($null -eq $commentBtn) {")
        L('        throw "未找到评论按钮 ({0})"'.format(
            " / ".join(filter(None, [comment_value, comment_fallback]))
        ))
        L("    }")
        L("")
        L('    LogPhase -phase 7 -message "找到评论按钮 at bounds=$($commentBtn.bounds)"')
        L("    TapElement -element $commentBtn")
        L("    HumanDelay -minMs 2500 -maxMs 4000")
        L("")
        L('    $xml2 = DumpUI -name "p7_after_tap"')
        L("")
        L("    # 查找评论输入区域")
        if input_strategy == "text":
            L('    $inputEl = FindByText -xml $xml2 -text "{0}"'.format(input_value))
        elif input_strategy == "resource-id":
            L('    $inputEl = FindByResourceId -xml $xml2 -id "{0}"'.format(input_value))
        else:
            L('    $inputEl = FindByText -xml $xml2 -text "{0}"'.format(input_value))

        L("    if ($null -eq $inputEl) {")
        L("        # 兜底: 查找 EditText")
        L('        $pattern = \'class="android\\.widget\\.EditText"[^>]*bounds="(\\[[0-9,]+\\]\\[[0-9,]+\\])"\'')
        L("        $m = [regex]::Match($xml2, $pattern)")
        L("        if ($m.Success) {")
        L("            $inputEl = Parse-Bounds -bounds $m.Groups[1].Value")
        L("        }")
        L("    }")
        L("")
        L("    if ($null -eq $inputEl) {")
        L('        throw "未找到评论输入区域 ({0} / EditText)"'.format(input_value))
        L("    }")
        L("")
        L('    LogPhase -phase 7 -message "找到评论输入区域 at bounds=$($inputEl.bounds)"')
        L("    TapElement -element $inputEl")
        L("    HumanDelay -minMs 500 -maxMs 1200")
        L("")
        L("    # 输入测试评论")
        L('    & adb shell input text "Great_post!" 2>&1 | Out-Null')
        L("    HumanDelay -minMs 1500 -maxMs 2500")
        L("")
        L('    LogPhase -phase 7 -message "已输入评论文本"')
        L("")
        L("    # 尝试查找并点击提交按钮")
        L('    $xml3 = DumpUI -name "p7_after_input"')
        L('    $submitCandidates = @("Post", "POST", "Send", "SEND", "Submit")')
        L("    $submitEl = $null")
        L("    foreach ($s in $submitCandidates) {")
        L("        $candidate = FindByText -xml $xml3 -text $s")
        L("        if ($null -ne $candidate -and (Is-NonZeroBounds -element $candidate)) {")
        L("            $submitEl = $candidate")
        L('            LogPhase -phase 7 -message "找到提交按钮: $s"')
        L("            break")
        L("        }")
        L("    }")
        L("")
        L("    if ($null -ne $submitEl) {")
        L("        TapElement -element $submitEl")
        L("        HumanDelay -minMs 1500 -maxMs 2500")
        L('        Mark-Phase -phase 7 -passed $true -details "已定位输入框、输入评论文本并尝试提交"')
        L("    } else {")
        L('        Mark-Phase -phase 7 -passed $true -details "已定位输入框并输入评论文本（未识别到明确提交按钮）"')
        L("    }")
        L("}")
        L("catch {")
        L('    Mark-Phase -phase 7 -passed $false -details ("异常: " + $_.Exception.Message)')
        L("}")
        L("")

        # Cleanup
        L("# ========================================")
        L("# Cleanup: 返回 Feed")
        L("# ========================================")
        L("try {")
        L('    LogPhase -phase 7 -message "清理：返回 Feed"')
        L("")
        L("    # 第一次返回")
        L("    & adb shell input keyevent KEYCODE_BACK 2>&1 | Out-Null")
        L("    HumanDelay -minMs 1500 -maxMs 2500")
        L("")
        L('    $xmlBack = DumpUI -name "cleanup_back1"')
        L('    $feedBack = FindByResourceId -xml $xmlBack -id "{0}"'.format(post_container_id))
        L("")
        L("    if ($null -eq $feedBack) {")
        L("        # 第二次返回")
        L("        & adb shell input keyevent KEYCODE_BACK 2>&1 | Out-Null")
        L("        HumanDelay -minMs 1500 -maxMs 2500")
        L("")
        L('        $xmlBack2 = DumpUI -name "cleanup_back2"')
        L('        $feedBack = FindByResourceId -xml $xmlBack2 -id "{0}"'.format(post_container_id))
        L("")
        L("        if ($null -eq $feedBack) {")
        L("            # 第三次返回")
        L("            & adb shell input keyevent KEYCODE_BACK 2>&1 | Out-Null")
        L("            HumanDelay -minMs 1500 -maxMs 2500")
        L("        }")
        L("    }")
        L("")
        L('    $xmlFinal = DumpUI -name "cleanup_final"')

        if feed_tab_id:
            L('    $navCheck = FindByResourceId -xml $xmlFinal -id "{0}"'.format(feed_tab_id))
        else:
            L("    $navCheck = $null")

        L('    $postCheck = FindByResourceId -xml $xmlFinal -id "{0}"'.format(post_container_id))
        L("")
        L("    if (($null -ne $navCheck) -or ($null -ne $postCheck)) {")
        L('        Write-Host "[CLEANUP] 已返回 Feed（校验通过）" -ForegroundColor Green')
        L("    } else {")
        L('        Write-Host "[CLEANUP] 返回状态不确定，请人工确认当前页面" -ForegroundColor Yellow')
        L("    }")
        L("}")
        L("catch {")
        L('    Write-Host "[CLEANUP] 异常: $($_.Exception.Message)" -ForegroundColor Yellow')
        L("}")
        L("")

        # Summary
        L("# ========================================")
        L("# Summary")
        L("# ========================================")
        L("$passCount = 0")
        L("foreach ($k in 1..{0}) {{".format(total_phases))
        L("    if ($results[$k]) { $passCount++ }")
        L("}")
        L("")
        L('Write-Host ""')
        L('Write-Host "========================================" -ForegroundColor Yellow')
        L('Write-Host "  {0} E2E Test Results" -ForegroundColor Yellow'.format(self.app_name))
        L('Write-Host "========================================" -ForegroundColor Yellow')
        L("")
        L("$phaseNames = @{")
        phase_names = [
            "Launch", "Navigate to Feed", "Browse", "Open Post",
            "Scroll/Reactions", "Like", "Comment"
        ]
        for i, name in enumerate(phase_names, 1):
            L('    {0} = "{1}"'.format(i, name))
        L("}")
        L("")
        L("foreach ($k in 1..{0}) {{".format(total_phases))
        L('    $status = if ($results[$k]) { "PASS" } else { "FAIL" }')
        L('    $color = if ($results[$k]) { "Green" } else { "Red" }')
        L('    $label = ("{0,-19}" -f ("Phase $k - " + $phaseNames[$k] + ":"))')
        L('    Write-Host ("  {0} [{1}]" -f $label, $status) -ForegroundColor $color')
        L("}")
        L("")
        L('Write-Host "========================================" -ForegroundColor Yellow')
        L('$totalColor = if ($passCount -eq {0}) {{ "Green" }} elseif ($passCount -ge {1}) {{ "Yellow" }} else {{ "Red" }}'.format(
            total_phases, max(1, total_phases - 2)
        ))
        L('Write-Host ("  Total: {{0}}/{0} passed" -f $passCount) -ForegroundColor $totalColor'.format(
            total_phases
        ))
        L('Write-Host "========================================" -ForegroundColor Yellow')
        L('Write-Host ""')
        L("")
        L("# 详细结果")
        L('Write-Host "详细结果:" -ForegroundColor White')
        L("foreach ($k in 1..{0}) {{".format(total_phases))
        L('    $state = if ($results[$k]) { "PASS" } else { "FAIL" }')
        L('    $color = if ($results[$k]) { "Green" } else { "Red" }')
        L('    Write-Host ("  [Phase {0}] [{1}] {2}" -f $k, $state, $phaseDetails[$k]) -ForegroundColor $color')
        L("}")
        L('Write-Host ""')

        return "\n".join(lines)

    # ================================================================
    # 平台配置构建辅助方法
    # ================================================================

    def _build_rate_limits(self):
        """根据应用类型生成合理的速率限制。"""
        return {
            "max_actions_per_hour": 80,
            "max_likes_per_hour": 24,
            "max_comments_per_hour": 12,
            "max_follows_per_hour": 8,
            "min_action_delay_ms": 2500,
            "max_action_delay_ms": 10000,
        }

    def _build_ui_selectors(self):
        """从 app_map 构建 UI 选择器配置。"""
        selectors = {}

        # 帖子容器
        post_container_id = self.app_map.get("post_container_id", "")
        if post_container_id:
            selectors["post_unit"] = {
                "strategy": "resource-id",
                "value": post_container_id,
                "fallback_strategy": "class",
                "fallback_value": "android.view.ViewGroup",
                "note": "由 App Onboarder 自动映射: 帖子卡片容器",
            }

        # 帖子内元素
        post_elements = self.app_map.get("post_elements", {})
        element_key_map = {
            "title": "post_title",
            "body": "post_body",
            "author": "post_author",
            "group": "post_group",
            "comment_count": "comment_count",
            "reactions_count": "reactions_count",
        }
        for src_key, dst_key in element_key_map.items():
            elem = post_elements.get(src_key)
            if elem:
                selectors[dst_key] = {
                    "strategy": elem.get("strategy", "resource-id"),
                    "value": elem.get("value", ""),
                    "note": "由 App Onboarder 自动映射",
                }

        # 操作按钮
        action_buttons = self.app_map.get("action_buttons", {})
        button_key_map = {
            "like": "like_button",
            "comment": "comment_button",
            "comment_input": "comment_input",
            "share": "share_button",
            "bookmark": "bookmark_button",
            "back": "back_button",
        }
        for src_key, dst_key in button_key_map.items():
            btn = action_buttons.get(src_key)
            if btn:
                selector = {
                    "strategy": btn.get("strategy", "text"),
                    "value": btn.get("value", ""),
                }
                if btn.get("fallback_value"):
                    # 推断 fallback_strategy
                    fallback_strat = btn.get("fallback_strategy", "text")
                    selector["fallback_strategy"] = fallback_strat
                    selector["fallback_value"] = btn["fallback_value"]
                selector["note"] = "由 App Onboarder 自动映射"
                selectors[dst_key] = selector

        # 提交按钮（通用）
        selectors["submit_button"] = {
            "strategy": "text",
            "value": "Post",
            "fallback_strategy": "content-desc",
            "fallback_value": "Submit",
            "note": "由 App Onboarder 自动映射: 评论提交按钮",
        }

        # 底部导航标签
        nav_tabs = self.app_map.get("bottom_nav_tabs", {})
        for tab_key, tab_info in nav_tabs.items():
            selector_key = "{0}_tab".format(tab_key.replace("menu_", ""))
            selectors[selector_key] = {
                "strategy": "resource-id",
                "value": tab_info.get("short_id", tab_key),
            }
            if tab_info.get("text"):
                selectors[selector_key]["fallback_strategy"] = "content-desc"
                selectors[selector_key]["fallback_value"] = tab_info["text"]
                selectors[selector_key]["note"] = "由 App Onboarder 自动映射: 底部导航 {0}".format(
                    tab_info["text"]
                )

        # Feed 容器
        feed_container_id = self.app_map.get("feed_container_id", "")
        if feed_container_id:
            selectors["feed_container"] = {
                "strategy": "resource-id",
                "value": feed_container_id,
                "note": "由 App Onboarder 自动映射: 帖子列表容器",
            }

        # WebView 容器
        if self.is_webview:
            wv_id = self.app_map.get("webview_container_id", "webViewLayout")
            selectors["web_view"] = {
                "strategy": "resource-id",
                "value": wv_id,
                "fallback_strategy": "class",
                "fallback_value": "android.webkit.WebView",
                "note": "由 App Onboarder 自动映射: WebView 容器",
            }

        return selectors

    def _build_page_signatures(self):
        """从 app_map 的 pages 信息构建页面签名。"""
        signatures = {}
        pages = self.app_map.get("pages", {})
        post_container_id = self.app_map.get("post_container_id", "postContainer")
        feed_container_id = self.app_map.get("feed_container_id", "posts")

        # Feed 页签名
        feed_signals = []
        if post_container_id:
            feed_signals.append({
                "strategy": "resource-id",
                "value": post_container_id,
                "weight": 0.3,
            })
        if feed_container_id:
            feed_signals.append({
                "strategy": "resource-id",
                "value": feed_container_id,
                "weight": 0.2,
            })

        # 从底部导航 tab 取信号
        nav_tabs = self.app_map.get("bottom_nav_tabs", {})
        feed_tab_key = self.app_map.get("feed_tab_key", "")
        for key, tab in nav_tabs.items():
            if tab.get("text"):
                feed_signals.append({
                    "strategy": "content-desc",
                    "value": tab["text"],
                    "weight": 0.15 if key == feed_tab_key else 0.1,
                })
                if len(feed_signals) >= 5:
                    break

        # 归一化权重
        feed_signals = self._normalize_weights(feed_signals)
        if feed_signals:
            signatures["feed"] = {
                "threshold": 0.5,
                "signals": feed_signals,
            }

        # Post Detail 页签名
        detail_signals = []
        if self.is_webview:
            wv_id = self.app_map.get("webview_container_id", "webViewLayout")
            detail_signals.append({
                "strategy": "resource-id",
                "value": wv_id,
                "weight": 0.3,
            })

        action_buttons = self.app_map.get("action_buttons", {})
        back_btn = action_buttons.get("back", {})
        if back_btn:
            detail_signals.append({
                "strategy": back_btn.get("strategy", "content-desc"),
                "value": back_btn.get("value", ""),
                "weight": 0.2,
            })

        share_btn = action_buttons.get("share", {})
        if share_btn:
            detail_signals.append({
                "strategy": share_btn.get("strategy", "resource-id"),
                "value": share_btn.get("value", ""),
                "weight": 0.15,
            })

        like_btn = action_buttons.get("like", {})
        if like_btn:
            detail_signals.append({
                "strategy": like_btn.get("strategy", "text"),
                "value": like_btn.get("value", ""),
                "weight": 0.2,
            })

        comment_btn = action_buttons.get("comment", {})
        if comment_btn:
            detail_signals.append({
                "strategy": comment_btn.get("strategy", "text"),
                "value": comment_btn.get("value", ""),
                "weight": 0.15,
            })

        detail_signals = self._normalize_weights(detail_signals)
        if detail_signals:
            signatures["post_detail"] = {
                "threshold": 0.5,
                "signals": detail_signals,
            }

        # Comment 页签名
        comment_input_btn = action_buttons.get("comment_input", {})
        if comment_input_btn:
            comment_signals = [
                {
                    "strategy": comment_input_btn.get("strategy", "text"),
                    "value": comment_input_btn.get("value", ""),
                    "weight": 0.4,
                },
                {
                    "strategy": "class",
                    "value": "android.widget.EditText",
                    "weight": 0.3,
                },
                {
                    "strategy": "text",
                    "value": "Post",
                    "weight": 0.3,
                },
            ]
            signatures["comment"] = {
                "threshold": 0.6,
                "signals": comment_signals,
            }

        # 从 pages 信息补充 home 页签名
        home_page = pages.get("home", {})
        if home_page and home_page.get("key_elements"):
            home_signals = []
            for elem in home_page["key_elements"][:5]:
                if isinstance(elem, dict):
                    home_signals.append({
                        "strategy": elem.get("strategy", "resource-id"),
                        "value": elem.get("value", ""),
                        "weight": 0.2,
                    })
                elif isinstance(elem, str):
                    home_signals.append({
                        "strategy": "resource-id",
                        "value": elem,
                        "weight": 0.2,
                    })
            home_signals = self._normalize_weights(home_signals)
            if home_signals:
                signatures["home"] = {
                    "threshold": 0.5,
                    "signals": home_signals,
                }

        return signatures

    def _build_action_weights(self):
        """构建操作权重（基于可用的操作按钮）。"""
        weights = {
            "browse": 40,
            "open_post": 20,
            "read_post": 7,
        }

        action_buttons = self.app_map.get("action_buttons", {})

        if "like" in action_buttons:
            weights["like"] = 20
        else:
            weights["like"] = 0

        if "comment" in action_buttons:
            weights["comment"] = 10
        else:
            weights["comment"] = 0

        if "share" in action_buttons:
            weights["share"] = 3
        else:
            weights["share"] = 0

        weights["follow"] = 0

        return weights

    def _build_scroll_config(self):
        """构建滚动配置。"""
        config = dict()  # type: dict
        config["min_scroll_distance"] = 850
        config["max_scroll_distance"] = 1250
        config["scroll_duration_ms"] = 550

        if self.feed_type == "viewpager_horizontal":
            config["note"] = "Feed 使用 ViewPager 水平滑动，browse 操作应使用水平 swipe 而非垂直 scroll"

        return config

    # ================================================================
    # 操作生成辅助方法
    # ================================================================

    def _op_navigate_to_feed(self):
        """生成 navigate_to_feed 操作。"""
        feed_tab = self._get_feed_tab()
        feed_tab_id = feed_tab.get("short_id", "") if feed_tab else ""
        feed_tab_desc = feed_tab.get("text", "") if feed_tab else ""

        steps = list()  # type: list
        steps.append({"action": "log", "message": "导航到 {0} Feed 标签".format(self.app_name)})

        # 使用 resource-id 查找 feed tab
        if feed_tab_id:
            steps.append({
                "action": "find",
                "strategy": "resource-id",
                "value": feed_tab_id,
                "save_as": "feed_tab",
                "on_fail": "abort",
            })
        elif feed_tab_desc:
            steps.append({
                "action": "find",
                "strategy": "content-desc",
                "value": feed_tab_desc,
                "save_as": "feed_tab",
                "on_fail": "abort",
            })

        steps.extend([
            {"action": "delay", "min_ms": 300, "max_ms": 800, "humanized": "true"},
            {"action": "tap", "context_ref": "feed_tab", "on_fail": "abort", "humanized": "true"},
            {"action": "delay", "min_ms": 3000, "max_ms": 6000, "humanized": "true"},
            {"action": "refresh_layout"},
            {
                "action": "verify",
                "selector": "post_unit",
                "on_fail": "retry",
                "max_retries": 3,
                "retry_delay_ms": 2000,
            },
            {"action": "set_var", "name": "current_page", "value": "feed"},
        ])

        return {
            "description": "从任意页面导航到 Feed 标签",
            "steps": steps,
        }

    def _op_browse(self):
        """生成 browse 操作。"""
        is_horizontal = self.feed_type == "viewpager_horizontal"
        cx = self.screen_w // 2

        steps = [
            {"action": "log", "message": "开始浏览 {0} 帖子".format(self.app_name)},
            {"action": "find", "selector": "post_unit", "save_as": "posts", "on_fail": "skip"},
            {
                "action": "verify",
                "selector": "post_unit",
                "on_fail": "retry",
                "max_retries": 2,
                "retry_delay_ms": 2000,
            },
            {"action": "delay", "min_ms": 2500, "max_ms": 6500, "humanized": "true"},
        ]

        if is_horizontal:
            swipe_start_x = int(self.screen_w * 0.76)
            swipe_end_x = int(self.screen_w * 0.21)
            swipe_y = int(self.screen_h * 0.58)
            swipe_step = {
                "action": "swipe",
                "direction": "left",
                "start_x": swipe_start_x,
                "start_y": swipe_y,
                "end_x": swipe_end_x,
                "end_y": swipe_y,
                "duration": 500,
                "humanized": "true",
                "note": "水平滑动 ViewPager 查看下一张帖子卡片",
            }
        else:
            vert_start_y = int(self.screen_h * 0.7)
            vert_end_y = int(self.screen_h * 0.3)
            swipe_step = {
                "action": "swipe",
                "direction": "up",
                "start_x": cx,
                "start_y": vert_start_y,
                "end_x": cx,
                "end_y": vert_end_y,
                "duration": 500,
                "humanized": "true",
                "note": "垂直滚动 RecyclerView 查看更多帖子",
            }

        # 第一次滑动
        steps.append(copy.deepcopy(swipe_step))
        steps.extend([
            {"action": "delay", "min_ms": 1500, "max_ms": 4000, "humanized": "true"},
            {"action": "refresh_layout"},
            {"action": "find", "selector": "post_unit", "save_as": "posts", "on_fail": "skip"},
            {"action": "delay", "min_ms": 2000, "max_ms": 5000, "humanized": "true"},
        ])

        # 第二次滑动
        steps.append(copy.deepcopy(swipe_step))
        steps.extend([
            {"action": "delay", "min_ms": 800, "max_ms": 2000, "humanized": "true"},
            {"action": "refresh_layout"},
            {"action": "set_var", "name": "last_action", "value": "browse"},
        ])

        return {
            "description": "浏览帖子列表（{0}）".format(
                "水平滑动 ViewPager" if is_horizontal else "垂直滚动 RecyclerView"
            ),
            "require_page": "feed",
            "steps": steps,
        }

    def _op_open_post(self):
        """生成 open_post 操作。"""
        steps = [
            {"action": "log", "message": "打开 {0} 帖子详情".format(self.app_name)},
            {"action": "find", "selector": "post_unit", "save_as": "target_post", "on_fail": "skip"},
            {"action": "delay", "min_ms": 400, "max_ms": 1200, "humanized": "true"},
            {"action": "tap", "context_ref": "target_post", "on_fail": "abort", "humanized": "true"},
        ]

        if self.is_webview:
            steps.append({
                "action": "delay",
                "min_ms": 3000,
                "max_ms": 6000,
                "humanized": "true",
                "note": "WebView 页面加载较慢，需要较长等待",
            })
            steps.append({"action": "refresh_layout"})
            steps.append({
                "action": "verify",
                "selector": "web_view",
                "on_fail": "retry",
                "max_retries": 3,
                "retry_delay_ms": 2000,
                "note": "验证进入 WebView 详情页",
            })
        else:
            steps.append({
                "action": "delay",
                "min_ms": 2000,
                "max_ms": 4000,
                "humanized": "true",
            })
            steps.append({"action": "refresh_layout"})
            steps.append({
                "action": "verify",
                "selector": "back_button",
                "on_fail": "retry",
                "max_retries": 3,
                "retry_delay_ms": 2000,
                "note": "验证进入详情页（存在返回按钮）",
            })

        steps.append({"action": "set_var", "name": "current_page", "value": "post_detail"})

        return {
            "description": "打开帖子详情（{0}）".format(
                "进入 WebView 页面" if self.is_webview else "原生详情页"
            ),
            "require_page": "feed",
            "steps": steps,
        }

    def _op_read_post(self):
        """生成 read_post 操作。"""
        steps = list()  # type: list
        steps.append({"action": "log", "message": "阅读 {0} 帖子".format(self.app_name)})

        if self.is_webview:
            steps.append({
                "action": "find",
                "selector": "web_view",
                "save_as": "webview",
                "on_fail": "skip",
            })

        steps.extend([
            {
                "action": "delay",
                "min_ms": 4000,
                "max_ms": 12000,
                "humanized": "true",
                "note": "模拟阅读停留",
            },
            {
                "action": "scroll",
                "direction": "down",
                "distance": 500,
                "duration": 600,
                "humanized": "true",
                "note": "向下滚动阅读更多内容",
            },
            {"action": "delay", "min_ms": 3000, "max_ms": 8000, "humanized": "true"},
            {
                "action": "scroll",
                "direction": "down",
                "distance": 400,
                "duration": 500,
                "humanized": "true",
            },
            {"action": "delay", "min_ms": 2000, "max_ms": 6000, "humanized": "true"},
            {"action": "set_var", "name": "last_action", "value": "read_post"},
        ])

        return {
            "description": "阅读帖子内容（{0}）".format(
                "在 WebView 内滚动" if self.is_webview else "原生页面滚动"
            ),
            "require_page": "post_detail",
            "steps": steps,
        }

    def _op_scroll_to_reactions(self):
        """生成 scroll_to_reactions 操作（WebView 应用专用）。"""
        action_buttons = self.app_map.get("action_buttons", {})
        like_btn = action_buttons.get("like", {})
        like_strategy = like_btn.get("strategy", "text")
        like_value = like_btn.get("value", "")

        cx = self.screen_w // 2

        steps = [
            {"action": "log", "message": "滚动到反应按钮区域"},
            {
                "action": "swipe",
                "direction": "up",
                "start_x": cx,
                "start_y": int(self.screen_h * 0.65),
                "end_x": cx,
                "end_y": int(self.screen_h * 0.39),
                "duration": 600,
                "humanized": "true",
                "note": "向下滚动 WebView，使反应按钮进入可视区域",
            },
            {"action": "delay", "min_ms": 1500, "max_ms": 3000, "humanized": "true"},
            {"action": "refresh_layout"},
            {
                "action": "find",
                "strategy": like_strategy,
                "value": like_value,
                "save_as": "reaction_btn",
                "on_fail": "retry",
                "max_retries": 3,
                "retry_delay_ms": 2000,
                "note": "查找点赞/反应按钮，如果未找到会重试（每次重试前自动再滚动一次）",
            },
            {"action": "delay", "min_ms": 800, "max_ms": 2000, "humanized": "true"},
            {"action": "set_var", "name": "last_action", "value": "scroll_to_reactions"},
        ]

        return {
            "description": "在 WebView 内滚动直到反应按钮可见且 bounds 非零",
            "require_page": "post_detail",
            "steps": steps,
        }

    def _op_like(self):
        """生成 like 操作。"""
        action_buttons = self.app_map.get("action_buttons", {})
        like_btn = action_buttons.get("like", {})
        like_strategy = like_btn.get("strategy", "text")
        like_value = like_btn.get("value", "")

        cx = self.screen_w // 2

        steps = list()  # type: list
        steps.append({"action": "log", "message": "{0} 点赞操作".format(self.app_name)})

        # 如果是 WebView，需先滚动到可见区域
        if self.is_webview:
            steps.extend([
                {
                    "action": "swipe",
                    "direction": "up",
                    "start_x": cx,
                    "start_y": int(self.screen_h * 0.65),
                    "end_x": cx,
                    "end_y": int(self.screen_h * 0.39),
                    "duration": 600,
                    "humanized": "true",
                    "note": "向下滚动使反应按钮可见",
                },
                {"action": "delay", "min_ms": 1500, "max_ms": 3000, "humanized": "true"},
                {"action": "refresh_layout"},
            ])

        steps.extend([
            {
                "action": "find",
                "strategy": like_strategy,
                "value": like_value,
                "save_as": "reaction_btn",
                "on_fail": "retry",
                "max_retries": 2,
                "retry_delay_ms": 2000,
                "note": "定位反应/点赞按钮",
            },
            {"action": "delay", "min_ms": 500, "max_ms": 1200, "humanized": "true"},
            {
                "action": "tap",
                "context_ref": "reaction_btn",
                "on_fail": "skip",
                "humanized": "true",
                "note": "点击反应按钮",
            },
            {
                "action": "delay",
                "min_ms": 2000,
                "max_ms": 4000,
                "humanized": "true",
                "note": "等待反应效果",
            },
            {"action": "refresh_layout"},
            {
                "action": "find",
                "strategy": "text",
                "value": "Love",
                "save_as": "love_emoji",
                "on_fail": "skip",
                "note": "尝试找到 Love 表情选项（如果有表情选择器）",
            },
            {
                "action": "tap",
                "context_ref": "love_emoji",
                "on_fail": "skip",
                "humanized": "true",
                "note": "选择 Love 表情",
            },
            {"action": "delay", "min_ms": 1000, "max_ms": 2500, "humanized": "true"},
            {"action": "set_var", "name": "last_action", "value": "like"},
        ])

        op = {
            "description": "点赞当前帖子",
            "require_page": "post_detail",
            "steps": steps,
        }

        if self.is_webview:
            op["reliability"] = "medium"

        return op

    def _op_comment(self):
        """生成 comment 操作。"""
        action_buttons = self.app_map.get("action_buttons", {})
        comment_btn = action_buttons.get("comment", {})
        comment_strategy = comment_btn.get("strategy", "text")
        comment_value = comment_btn.get("value", "")
        comment_fallback = comment_btn.get("fallback_value", "")

        comment_input_btn = action_buttons.get("comment_input", {})
        input_strategy = comment_input_btn.get("strategy", "text")
        input_value = comment_input_btn.get("value", "")
        # fallback 策略可能与主策略不同（如主策略是 text，fallback 是 content-desc）
        comment_fallback_strategy = comment_btn.get("fallback_strategy", comment_strategy)

        steps = list()  # type: list
        steps.append({"action": "log", "message": "{0} 评论操作".format(self.app_name)})
        steps.append({
            "action": "find",
            "strategy": comment_strategy,
            "value": comment_value,
            "save_as": "comment_btn",
            "on_fail": "skip",
            "note": "查找主评论按钮",
        })

        if comment_fallback:
            steps.append({
                "action": "find",
                "strategy": comment_fallback_strategy,
                "value": comment_fallback,
                "save_as": "comment_btn_fallback",
                "on_fail": "skip",
                "note": "查找备用评论按钮",
            })

        steps.extend([
            {"action": "delay", "min_ms": 500, "max_ms": 1200, "humanized": "true"},
            {
                "action": "tap",
                "context_ref": "comment_btn",
                "on_fail": "skip",
                "humanized": "true",
                "note": "点击评论按钮",
            },
            {
                "action": "delay",
                "min_ms": 2000,
                "max_ms": 4000,
                "humanized": "true",
                "note": "等待评论输入界面出现",
            },
            {"action": "refresh_layout"},
            {
                "action": "find",
                "strategy": input_strategy,
                "value": input_value,
                "save_as": "comment_input",
                "on_fail": "skip",
                "note": "查找评论输入区域",
            },
            {
                "action": "find",
                "strategy": "class",
                "value": "android.widget.EditText",
                "save_as": "comment_input_fallback",
                "on_fail": "skip",
                "note": "兜底查找 EditText 输入框",
            },
            {
                "action": "tap",
                "context_ref": "comment_input",
                "on_fail": "skip",
                "humanized": "true",
                "note": "点击评论输入区域获取焦点",
            },
            {"action": "delay", "min_ms": 500, "max_ms": 1000, "humanized": "true"},
            {
                "action": "input_text",
                "text": "{{comment_text}}",
                "note": "输入评论文本（来自变量）",
            },
            {"action": "delay", "min_ms": 1000, "max_ms": 2500, "humanized": "true"},
            {
                "action": "find",
                "strategy": "text",
                "value": "Post",
                "save_as": "submit_btn",
                "on_fail": "skip",
                "note": "查找提交按钮",
            },
            {
                "action": "tap",
                "context_ref": "submit_btn",
                "on_fail": "skip",
                "humanized": "true",
                "note": "提交评论",
            },
            {"action": "delay", "min_ms": 2000, "max_ms": 4000, "humanized": "true"},
            {"action": "set_var", "name": "last_action", "value": "comment"},
        ])

        op = {
            "description": "在帖子下发表评论",
            "require_page": "post_detail",
            "steps": steps,
        }

        if self.is_webview:
            op["reliability"] = "medium"

        return op

    def _op_back_to_feed(self):
        """生成 back_to_feed 操作。"""
        steps = [
            {"action": "log", "message": "返回 {0} Feed 列表".format(self.app_name)},
            {"action": "find", "selector": "back_button", "save_as": "back_btn", "on_fail": "skip"},
            {"action": "tap", "context_ref": "back_btn", "on_fail": "skip", "humanized": "true"},
            {"action": "delay", "min_ms": 500, "max_ms": 1500, "humanized": "true"},
            {
                "action": "back",
                "note": "KEYCODE_BACK 兜底，确保返回",
            },
            {"action": "delay", "min_ms": 1200, "max_ms": 2800, "humanized": "true"},
            {"action": "refresh_layout"},
            {
                "action": "verify",
                "selector": "post_unit",
                "on_fail": "retry",
                "max_retries": 3,
                "retry_delay_ms": 2000,
            },
            {"action": "set_var", "name": "current_page", "value": "feed"},
        ]

        return {
            "description": "从帖子详情页返回 Feed 列表",
            "require_page": "post_detail",
            "steps": steps,
        }

    # ================================================================
    # 内部工具方法
    # ================================================================

    def _get_feed_tab(self):
        """获取 feed 对应的底部导航 tab 信息。"""
        feed_tab_key = self.app_map.get("feed_tab_key", "")
        nav_tabs = self.app_map.get("bottom_nav_tabs", {})

        if feed_tab_key and feed_tab_key in nav_tabs:
            return nav_tabs[feed_tab_key]

        # 尝试匹配 key 中包含 feed/community/home 的 tab
        for key in ["feed", "community", "birthclub"]:
            for tab_key, tab_info in nav_tabs.items():
                if key in tab_key.lower():
                    return tab_info

        # 返回第一个非-home tab，或第一个 tab
        for tab_key, tab_info in nav_tabs.items():
            if "home" not in tab_key.lower():
                return tab_info

        if nav_tabs:
            return list(nav_tabs.values())[0]

        return None

    def _build_architecture_notes(self):
        """构建架构说明。"""
        notes_parts = []

        if self.is_webview:
            wv_id = self.app_map.get("webview_container_id", "webViewLayout")
            has_acc = self.app_map.get("webview_has_accessibility", False)
            notes_parts.append(
                "帖子详情使用 WebView 渲染 (container: {0})。".format(wv_id)
            )
            if has_acc:
                notes_parts.append(
                    "WebView 内部 DOM 元素作为 accessibility nodes 暴露给 uiautomator。"
                )

        action_buttons = self.app_map.get("action_buttons", {})
        btn_descs = []
        for btn_name, btn_info in action_buttons.items():
            if btn_name in ("like", "comment", "comment_input"):
                btn_descs.append("{0}: {1}='{2}'".format(
                    btn_name,
                    btn_info.get("strategy", ""),
                    btn_info.get("value", ""),
                ))
        if btn_descs:
            notes_parts.append("操作按钮: {0}。".format(", ".join(btn_descs)))

        if self.feed_type == "viewpager_horizontal":
            notes_parts.append("Feed 使用 ViewPager 水平滑动帖子卡片。")
        else:
            notes_parts.append("Feed 使用 RecyclerView 垂直滚动。")

        return " ".join(notes_parts)

    def _merge_platform_config(self, platform_config):
        """将新平台配置合并到现有 PlatformsConfig.json 中。"""
        # 读取现有配置
        existing = {}
        if os.path.exists(self.platforms_config_path):
            try:
                with open(self.platforms_config_path, "r", encoding="utf-8") as f:
                    existing = json.load(f)
            except (ValueError, IOError):
                existing = {}

        if "version" not in existing:
            existing["version"] = "2.0"
        if "platforms" not in existing:
            existing["platforms"] = {}

        # 添加/更新平台
        existing["platforms"][self.platform_key] = platform_config

        # 写回
        self._ensure_dir(os.path.dirname(self.platforms_config_path))
        with open(self.platforms_config_path, "w", encoding="utf-8") as f:
            json.dump(existing, f, indent=4, ensure_ascii=False)

    def _normalize_weights(self, signals):
        """归一化信号权重使其总和为 1.0。"""
        if not signals:
            return signals

        total = sum(s.get("weight", 0) for s in signals)
        if total <= 0:
            return signals

        for s in signals:
            raw = s.get("weight", 0)
            s["weight"] = round(raw / total, 2)

        # 修正尾差
        diff = 1.0 - sum(s["weight"] for s in signals)
        if abs(diff) > 0.001 and signals:
            signals[0]["weight"] = round(signals[0]["weight"] + diff, 2)

        return signals

    def _ensure_dir(self, path):
        """确保目录存在。"""
        if path and not os.path.exists(path):
            os.makedirs(path)
