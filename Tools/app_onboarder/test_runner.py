# -*- coding: utf-8 -*-
# pyright: reportGeneralTypeIssues=false, reportArgumentType=false, reportAttributeAccessIssue=false, reportIndexIssue=false, reportOptionalSubscript=false, reportCallIssue=false, reportOperatorIssue=false, reportMissingTypeArgument=false
"""
DPS v4.5 App Onboarder - 测试运行器
执行 E2E PowerShell 测试脚本，分析失败原因，自动修复配置并重试。

支持的修复类型:
  - delay_increase: 增加等待时间（乘以 1.5 倍）
  - selector_swap: 将 strategy/value 切换为 fallback_strategy/fallback_value
  - scroll_adjust: 增加滚动距离或最大滚动尝试次数
  - coordinate_adjust: 根据 screen_size 重新计算滑动坐标
  - webview_wait_increase: 增加 WebView 加载等待时间
"""

import json
import os
import re
import subprocess
import datetime
import copy


class TestRunner(object):
    """
    运行 E2E 测试脚本，分析失败，自动修复配置文件后重试。

    典型用法::

        runner = TestRunner(
            adb=adb_controller,
            test_script_path="path/to/e2e_test.ps1",
            config_path="path/to/PlatformsConfig.json",
            operations_path="path/to/platform_operations.json",
            platform_key="babycenter",
            max_fix_attempts=3,
        )
        report = runner.run_and_fix()
    """

    # 已知的修复类型
    FIX_TYPE_DELAY_INCREASE = "delay_increase"
    FIX_TYPE_SELECTOR_SWAP = "selector_swap"
    FIX_TYPE_SCROLL_ADJUST = "scroll_adjust"
    FIX_TYPE_COORDINATE_ADJUST = "coordinate_adjust"
    FIX_TYPE_WEBVIEW_WAIT_INCREASE = "webview_wait_increase"

    def __init__(self, adb, test_script_path, config_path, operations_path,
                 platform_key, max_fix_attempts=3):
        """
        初始化测试运行器。

        参数:
            adb: ADBController 实例（用于获取屏幕尺寸等设备信息）
            test_script_path (str): PowerShell E2E 测试脚本路径
            config_path (str): PlatformsConfig.json 完整路径
            operations_path (str): {platform}_operations.json 完整路径
            platform_key (str): 平台标识符（如 'babycenter'）
            max_fix_attempts (int): 最大自动修复循环次数
        """
        self.adb = adb
        self.test_script_path = os.path.abspath(test_script_path)
        self.config_path = os.path.abspath(config_path)
        self.operations_path = os.path.abspath(operations_path)
        self.platform_key = platform_key
        self.max_fix_attempts = max_fix_attempts

        # 记录所有修复历史
        self.fix_history = []
        # 日志缓冲区
        self._log_buffer = []
        # 记录各阶段截图路径
        self._phase_screenshots = {}

    # ================================================================
    # 日志
    # ================================================================

    def _log(self, level, message):
        """
        记录带时间戳的日志，同时输出到控制台。

        参数:
            level (str): 日志级别 (INFO / WARN / ERROR)
            message (str): 日志内容
        """
        ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        entry = "[{ts}] [{level}] {msg}".format(ts=ts, level=level, msg=message)
        self._log_buffer.append(entry)
        # 同时输出到控制台，便于实时观察测试进度
        print(entry)

    def _log_info(self, message):
        self._log("INFO", message)

    def _log_warn(self, message):
        self._log("WARN", message)

    def _log_error(self, message):
        self._log("ERROR", message)

    def _capture_phase_screenshot(self, phase):
        """
        在每个测试阶段后截图保存

        Args:
            phase (int): 阶段号

        Returns:
            str: 截图路径，失败返回空字符串
        """
        try:
            screenshot_path = self.adb.screenshot("e2e_phase_{}".format(phase))
            self._log_info("Phase {} 截图已保存: {}".format(phase, screenshot_path))
            self._phase_screenshots[phase] = screenshot_path
            return screenshot_path
        except Exception as e:
            self._log_warn("Phase {} 截图失败: {}".format(phase, str(e)))
            return ""

    # ================================================================
    # 公共接口
    # ================================================================

    def run_and_fix(self):
        """
        主循环: 运行测试 → 分析失败 → 应用修复 → 重试。

        返回:
            dict: 汇总报告，格式::

                {
                    "total_attempts": N,
                    "final_results": {...},
                    "fixes_applied": [...],
                    "success": True/False,
                    "summary": "可读的汇总字符串"
                }
        """
        self._log_info("========== TestRunner 启动 ==========")
        self._log_info("平台: {0}, 脚本: {1}".format(
            self.platform_key, self.test_script_path
        ))
        self._log_info("最大修复尝试次数: {0}".format(self.max_fix_attempts))

        all_fixes_applied = []
        final_results = None
        attempt_num = 0

        for attempt_num in range(1, self.max_fix_attempts + 1):
            self._log_info("---------- 第 {0}/{1} 次尝试 ----------".format(
                attempt_num, self.max_fix_attempts
            ))

            # 1. 运行测试
            try:
                results = self.run_test()
                final_results = results
            except Exception as e:
                self._log_error("测试执行异常: {0}".format(str(e)))
                final_results = {
                    "total": 0,
                    "pass_count": 0,
                    "phases": {},
                    "raw_output": "",
                }
                continue

            self._log_info("测试结果: {0}/{1} 通过".format(
                results.get("pass_count", 0), results.get("total", 0)
            ))

            # 对失败的 Phase 截图记录
            for phase_num, phase_data in results.get("phases", {}).items():
                if not phase_data.get("passed"):
                    self._capture_phase_screenshot(phase_num)

            # 2. 检查是否全部通过
            if results["pass_count"] == results["total"] and results["total"] > 0:
                self._log_info("所有阶段均通过，无需修复。")
                return self._build_report(
                    total_attempts=attempt_num,
                    final_results=results,
                    fixes_applied=all_fixes_applied,
                    success=True,
                )

            # 3. 分析失败
            fixes = self.analyze_failures(results)
            if not fixes:
                self._log_warn("存在失败但无法诊断出可用的修复方案。")
                if attempt_num == self.max_fix_attempts:
                    break
                continue

            self._log_info("诊断出 {0} 个修复建议。".format(len(fixes)))

            # 4. 应用修复
            applied_this_round = []
            for fix in fixes:
                try:
                    ok = self.apply_fixes([fix])
                    if ok:
                        fix_record = copy.deepcopy(fix)
                        fix_record["applied_at"] = datetime.datetime.now().strftime(
                            "%Y-%m-%d %H:%M:%S"
                        )
                        fix_record["attempt"] = attempt_num
                        applied_this_round.append(fix_record)
                        self.fix_history.append(fix_record)
                        self._log_info("已应用修复: {0}".format(
                            fix.get("description", fix.get("fix_type", "unknown"))
                        ))
                    else:
                        self._log_warn("修复跳过（无法应用）: {0}".format(
                            fix.get("description", fix.get("fix_type", "unknown"))
                        ))
                except Exception as e:
                    self._log_error("修复应用异常: {0}".format(str(e)))

            all_fixes_applied.extend(applied_this_round)

            if not applied_this_round:
                self._log_warn("本轮未能成功应用任何修复，停止重试。")
                break

        # 循环结束
        success = (
            final_results is not None
            and final_results.get("pass_count", 0) == final_results.get("total", 0)
            and final_results.get("total", 0) > 0
        )

        return self._build_report(
            total_attempts=attempt_num,
            final_results=final_results or {"total": 0, "pass_count": 0, "phases": {}},
            fixes_applied=all_fixes_applied,
            success=success,
        )

    def run_test(self):
        """
        执行 PowerShell E2E 测试脚本，解析输出。

        返回:
            dict: 结构化结果::

                {
                    "total": 7,
                    "pass_count": N,
                    "phases": {
                        1: {"passed": True, "details": "..."},
                        2: {"passed": False, "details": "异常: 未找到 Community 标签"},
                        ...
                    },
                    "raw_output": "full stdout"
                }

        异常:
            IOError: 测试脚本不存在
            RuntimeError: PowerShell 执行超时或无法启动
        """
        if not os.path.exists(self.test_script_path):
            raise IOError("测试脚本不存在: {0}".format(self.test_script_path))

        self._log_info("执行测试脚本: {0}".format(self.test_script_path))

        # 执行 PowerShell 脚本
        cmd = [
            "powershell.exe",
            "-ExecutionPolicy", "Bypass",
            "-File", self.test_script_path,
        ]

        proc = None
        try:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                shell=False,
            )
            stdout_bytes, stderr_bytes = proc.communicate(timeout=300)
        except subprocess.TimeoutExpired:
            if proc is not None:
                proc.kill()
                proc.communicate()
            raise RuntimeError("测试脚本执行超时 (300秒)")
        except OSError as e:
            raise RuntimeError("无法启动 PowerShell: {0}".format(str(e)))

        # 解码输出（尝试 utf-8，回退 gbk）
        raw_output = self._decode_output(stdout_bytes)
        raw_stderr = self._decode_output(stderr_bytes)

        self._log_info("脚本退出码: {0}".format(proc.returncode))

        # 解析 [PASS] 和 [FAIL] 行
        phases = {}
        pass_count = 0
        total = 0

        # 匹配格式: [PASS] Phase N: details  或  [FAIL] Phase N: details
        pattern = re.compile(
            r'\[(PASS|FAIL)\]\s*Phase\s+(\d+)\s*:\s*(.*)',
            re.IGNORECASE
        )

        for line in raw_output.splitlines():
            stripped = line.strip()
            match = pattern.search(stripped)
            if match:
                status = match.group(1).upper()
                phase_num = int(match.group(2))
                details = match.group(3).strip()
                passed = (status == "PASS")

                phases[phase_num] = {
                    "passed": passed,
                    "details": details,
                }

                total += 1
                if passed:
                    pass_count += 1

        # 额外解析 summary 中的 Total 行作为备用
        if total == 0:
            total_pattern = re.compile(
                r'Total:\s*(\d+)/(\d+)\s*passed', re.IGNORECASE
            )
            for line in raw_output.splitlines():
                tmatch = total_pattern.search(line.strip())
                if tmatch:
                    pass_count = int(tmatch.group(1))
                    total = int(tmatch.group(2))
                    break

        self._log_info("解析结果: {0}/{1} 通过, {2} 个阶段".format(
            pass_count, total, len(phases)
        ))

        # 截图保存当前状态用于测试报告
        try:
            screenshot_path = self.adb.screenshot("test_final_state")
            self._log_info("测试结束截图: {}".format(screenshot_path))
        except Exception as e:
            self._log_warn("测试截图失败: {}".format(str(e)))
            screenshot_path = ""

        return {
            "total": total,
            "pass_count": pass_count,
            "phases": phases,
            "raw_output": raw_output,
            "screenshot": screenshot_path,
        }

    def analyze_failures(self, results):
        """
        分析测试失败原因，生成修复建议列表。

        参数:
            results (dict): run_test() 的返回值

        返回:
            list[dict]: 修复建议列表，每项包含::

                {
                    "target": "config" | "operations" | "test",
                    "file": "文件路径",
                    "description": "修复说明",
                    "fix_type": "delay_increase" | "selector_swap" | ...,
                    "phase": int,
                    "changes": [
                        {"path": "json.path.to.key", "old_value": ..., "new_value": ...}
                    ]
                }
        """
        fixes = []
        phases = results.get("phases", {})

        for phase_num in sorted(phases.keys()):
            phase_data = phases[phase_num]
            if phase_data.get("passed"):
                continue

            details = phase_data.get("details", "")
            diagnosed = self._diagnose_failure(phase_num, details)
            if diagnosed:
                fixes.extend(diagnosed)

        return fixes

    def apply_fixes(self, fixes):
        """
        应用修复到配置/操作/测试文件。

        对于 JSON 文件 (config/operations): 读取 → 修改 → 写回。
        对于 PS1 文件 (test): 使用字符串替换。

        参数:
            fixes (list[dict]): 修复建议列表

        返回:
            bool: 是否成功应用了至少一个修复
        """
        any_applied = False

        for fix in fixes:
            fix_type = fix.get("fix_type", "")
            applied = False

            if fix_type == self.FIX_TYPE_DELAY_INCREASE:
                applied = self._fix_delay_increase(fix)
            elif fix_type == self.FIX_TYPE_SELECTOR_SWAP:
                applied = self._fix_selector_swap(fix)
            elif fix_type == self.FIX_TYPE_SCROLL_ADJUST:
                applied = self._fix_scroll_adjust(fix)
            elif fix_type == self.FIX_TYPE_COORDINATE_ADJUST:
                applied = self._fix_coordinate_adjust(fix)
            elif fix_type == self.FIX_TYPE_WEBVIEW_WAIT_INCREASE:
                applied = self._fix_webview_wait_increase(fix)
            else:
                self._log_warn("未知的修复类型: {0}".format(fix_type))

            if applied:
                any_applied = True

        return any_applied

    # ================================================================
    # 失败诊断
    # ================================================================

    def _diagnose_failure(self, phase, details):
        """
        根据失败阶段和详情诊断具体问题，返回修复建议。

        参数:
            phase (int): 失败阶段号
            details (str): 失败详情文本

        返回:
            list[dict]: 修复建议（可能为空列表）
        """
        fixes = []
        details_lower = details.lower() if details else ""

        # ---- Phase 1: 启动阶段 ----
        if phase == 1:
            # "未检测到 bottom_navigator" → 增加启动延迟，检查弹窗处理
            if self._match_any(details_lower, [
                "未检测到", "bottom_navigator", "menu_home", "not found"
            ]):
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 1 启动后未检测到主界面，增加启动等待时间",
                    "fix_type": self.FIX_TYPE_DELAY_INCREASE,
                    "phase": 1,
                    "changes": [{
                        "path": "Phase1.launch_delay",
                        "old_value": "HumanDelay -minMs 8000 -maxMs 9500",
                        "new_value": "HumanDelay -minMs 12000 -maxMs 15000",
                    }],
                })

        # ---- Phase 2: 导航到 Feed ----
        elif phase == 2:
            # "未找到 Community 标签" → 尝试替代选择器
            if self._match_any(details_lower, ["未找到", "标签", "tab"]):
                # 尝试 selector_swap: 将 resource-id 策略切换为 content-desc
                fixes.append({
                    "target": "config",
                    "file": self.config_path,
                    "description": "Phase 2 Feed 标签未找到，尝试替代选择器 (content-desc vs resource-id)",
                    "fix_type": self.FIX_TYPE_SELECTOR_SWAP,
                    "phase": 2,
                    "changes": [{
                        "path": "platforms.{0}.ui_selectors.community_tab".format(
                            self.platform_key
                        ),
                        "old_value": "strategy → fallback_strategy",
                        "new_value": "fallback_strategy → strategy",
                    }],
                })
            else:
                # 通用: 增加导航后等待
                fixes.append({
                    "target": "operations",
                    "file": self.operations_path,
                    "description": "Phase 2 Feed 页面加载不完全，增加导航后等待",
                    "fix_type": self.FIX_TYPE_DELAY_INCREASE,
                    "phase": 2,
                    "changes": [{
                        "path": "operations.navigate_to_feed.steps[delay]",
                        "old_value": "current",
                        "new_value": "current * 1.5",
                    }],
                })

        # ---- Phase 3: 浏览 Feed ----
        elif phase == 3:
            # "滑动后未检测到 postContainer" → 调整滑动坐标
            if self._match_any(details_lower, [
                "postcontainer", "未检测到", "浏览后"
            ]):
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 3 滑动后帖子容器未检测到，根据屏幕尺寸调整滑动坐标",
                    "fix_type": self.FIX_TYPE_COORDINATE_ADJUST,
                    "phase": 3,
                    "changes": [{
                        "path": "Phase3.swipe_coordinates",
                        "old_value": "hardcoded",
                        "new_value": "screen_size_based",
                    }],
                })

        # ---- Phase 4: 打开帖子详情 (WebView) ----
        elif phase == 4:
            # "未检测到 web_view" → 增加 WebView 加载延迟
            if self._match_any(details_lower, [
                "webview", "web_view", "webkit", "未加载"
            ]):
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 4 WebView 加载超时，增加 WebView 等待时间",
                    "fix_type": self.FIX_TYPE_WEBVIEW_WAIT_INCREASE,
                    "phase": 4,
                    "changes": [{
                        "path": "Phase4.webview_delay",
                        "old_value": "HumanDelay -minMs 8000 -maxMs 11000",
                        "new_value": "HumanDelay -minMs 14000 -maxMs 18000",
                    }],
                })
            elif self._match_any(details_lower, ["未检测到", "未找到"]):
                fixes.append({
                    "target": "operations",
                    "file": self.operations_path,
                    "description": "Phase 4 帖子详情页未加载，增加等待",
                    "fix_type": self.FIX_TYPE_DELAY_INCREASE,
                    "phase": 4,
                    "changes": [{
                        "path": "operations.open_post.steps[delay]",
                        "old_value": "current",
                        "new_value": "current * 1.5",
                    }],
                })

        # ---- Phase 5: 滚动到 Reactions ----
        elif phase == 5:
            # "未定位有效 reaction 按钮" → 增加滚动尝试次数和滚动距离
            if self._match_any(details_lower, [
                "仍未定位", "reaction", "滚动"
            ]):
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 5 滚动次数不足，增加最大滚动尝试次数",
                    "fix_type": self.FIX_TYPE_SCROLL_ADJUST,
                    "phase": 5,
                    "changes": [{
                        "path": "Phase5.maxTry",
                        "old_value": "$maxTry = 8",
                        "new_value": "$maxTry = 12",
                    }],
                })
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 5 单次滚动距离不足，增加滚动距离",
                    "fix_type": self.FIX_TYPE_SCROLL_ADJUST,
                    "phase": 5,
                    "changes": [{
                        "path": "Phase5.scroll_distance",
                        "old_value": "ScrollDown -distance 600",
                        "new_value": "ScrollDown -distance 900",
                    }],
                })
            elif self._match_any(details_lower, ["未找到", "未检测"]):
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 5 反应按钮未找到，增加滚动尝试",
                    "fix_type": self.FIX_TYPE_SCROLL_ADJUST,
                    "phase": 5,
                    "changes": [{
                        "path": "Phase5.maxTry",
                        "old_value": "$maxTry = 8",
                        "new_value": "$maxTry = 12",
                    }],
                })

        # ---- Phase 6: 点赞 ----
        elif phase == 6:
            # "未从 Phase 5 获取到 reaction" → 跳过（依赖 Phase 5）
            if self._match_any(details_lower, ["phase 5", "获取到"]):
                self._log_info(
                    "Phase 6 失败依赖于 Phase 5，跳过独立修复。"
                )
            elif self._match_any(details_lower, ["未找到", "未检测"]):
                fixes.append({
                    "target": "operations",
                    "file": self.operations_path,
                    "description": "Phase 6 点赞按钮点击后未出现预期 UI，增加等待",
                    "fix_type": self.FIX_TYPE_DELAY_INCREASE,
                    "phase": 6,
                    "changes": [{
                        "path": "operations.like.steps[delay]",
                        "old_value": "current",
                        "new_value": "current * 1.5",
                    }],
                })

        # ---- Phase 7: 评论 ----
        elif phase == 7:
            # "未找到评论按钮" → 尝试替代评论选择器文本
            if self._match_any(details_lower, ["评论按钮", "comment"]):
                fixes.append({
                    "target": "config",
                    "file": self.config_path,
                    "description": "Phase 7 评论按钮未找到，尝试替代评论选择器文本",
                    "fix_type": self.FIX_TYPE_SELECTOR_SWAP,
                    "phase": 7,
                    "changes": [{
                        "path": "platforms.{0}.ui_selectors.comment_button".format(
                            self.platform_key
                        ),
                        "old_value": "strategy → fallback_strategy",
                        "new_value": "fallback_strategy → strategy",
                    }],
                })
            elif self._match_any(details_lower, ["输入", "input"]):
                fixes.append({
                    "target": "test",
                    "file": self.test_script_path,
                    "description": "Phase 7 评论输入区未出现，增加等待",
                    "fix_type": self.FIX_TYPE_DELAY_INCREASE,
                    "phase": 7,
                    "changes": [{
                        "path": "Phase7.comment_tap_delay",
                        "old_value": "HumanDelay -minMs 2500 -maxMs 4000",
                        "new_value": "HumanDelay -minMs 4000 -maxMs 6000",
                    }],
                })

        return fixes

    def _match_any(self, text, patterns):
        """
        检查文本是否包含任意一个模式字符串（不区分大小写）。

        参数:
            text (str): 已转为小写的文本
            patterns (list[str]): 模式字符串列表

        返回:
            bool
        """
        for p in patterns:
            if p.lower() in text:
                return True
        return False

    # ================================================================
    # 修复实现
    # ================================================================

    def _fix_delay_increase(self, fix):
        """
        delay_increase: 增加等待时间。

        对于 target="test": 在 PS1 脚本中替换 HumanDelay 参数值。
        对于 target="operations": 在 operations.json 中对应操作的 delay 步骤时间 × 1.5。
        """
        target = fix.get("target", "")
        changes = fix.get("changes", [])

        if target == "test":
            # PS1 脚本中的字符串替换
            for change in changes:
                old_val = change.get("old_value", "")
                new_val = change.get("new_value", "")
                if old_val and new_val and old_val != new_val:
                    if self._replace_in_file(self.test_script_path, old_val, new_val):
                        return True
            return False

        elif target == "operations":
            # 从 changes 中推断操作名
            operation_name = self._extract_operation_name(changes)
            return self._increase_ops_delays(operation_name, 1.5)

        return False

    def _fix_selector_swap(self, fix):
        """
        selector_swap: 在 PlatformsConfig.json 中交换 strategy/value 与 fallback。

        查找指定选择器键，将 strategy/value 与 fallback_strategy/fallback_value 互换。
        """
        changes = fix.get("changes", [])
        if not changes:
            return False

        if not os.path.exists(self.config_path):
            self._log_warn("配置文件不存在: {0}".format(self.config_path))
            return False

        try:
            with open(self.config_path, "r", encoding="utf-8") as f:
                config = json.load(f)
        except (ValueError, IOError) as e:
            self._log_error("读取配置文件失败: {0}".format(str(e)))
            return False

        modified = False

        for change in changes:
            json_path = change.get("path", "")
            # 解析路径: platforms.{key}.ui_selectors.{selector_key}
            parts = json_path.split(".")
            # 导航到目标对象
            obj = config
            try:
                for part in parts:
                    obj = obj[part]
            except (KeyError, TypeError, IndexError):
                self._log_warn("JSON 路径不存在: {0}".format(json_path))
                continue

            # 执行交换: strategy/value ↔ fallback_strategy/fallback_value
            if isinstance(obj, dict):
                has_fallback = (
                    "fallback_strategy" in obj and "fallback_value" in obj
                )
                if has_fallback:
                    old_strategy = obj.get("strategy", "")
                    old_value = obj.get("value", "")
                    fb_strategy = obj.get("fallback_strategy", "")
                    fb_value = obj.get("fallback_value", "")

                    obj["strategy"] = fb_strategy
                    obj["value"] = fb_value
                    obj["fallback_strategy"] = old_strategy
                    obj["fallback_value"] = old_value
                    obj["auto_swapped"] = True
                    obj["auto_swap_note"] = (
                        "TestRunner 自动交换: {0}/{1} → {2}/{3} ({4})".format(
                            old_strategy, old_value,
                            fb_strategy, fb_value,
                            datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                        )
                    )
                    modified = True
                else:
                    # 无 fallback 可交换，标记需要人工验证
                    obj["needs_verification"] = True
                    obj["auto_fix_note"] = (
                        "TestRunner 标记: 选择器在 E2E 测试中失败，无 fallback 可用 ({0})".format(
                            datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
                        )
                    )
                    modified = True

        if modified:
            try:
                with open(self.config_path, "w", encoding="utf-8") as f:
                    json.dump(config, f, indent=4, ensure_ascii=False)
                return True
            except IOError as e:
                self._log_error("写入配置文件失败: {0}".format(str(e)))
                return False

        return False

    def _fix_scroll_adjust(self, fix):
        """
        scroll_adjust: 增加滚动距离或最大滚动尝试次数。

        目标: 测试脚本中的 $maxTry 和 ScrollDown -distance 参数。
        """
        changes = fix.get("changes", [])
        any_applied = False

        for change in changes:
            old_val = change.get("old_value", "")
            new_val = change.get("new_value", "")
            if old_val and new_val and old_val != new_val:
                if self._replace_in_file(self.test_script_path, old_val, new_val):
                    any_applied = True

        return any_applied

    def _fix_coordinate_adjust(self, fix):
        """
        coordinate_adjust: 根据 screen_size 重新计算滑动坐标。

        从 ADB 获取实际屏幕尺寸，然后按比例替换测试脚本中的硬编码坐标。
        """
        # 获取实际屏幕尺寸
        try:
            screen_w, screen_h = self.adb.get_screen_size()
        except Exception:
            screen_w, screen_h = 1440, 3120  # 兜底默认值
            self._log_warn("无法获取屏幕尺寸，使用默认值 {0}x{1}".format(
                screen_w, screen_h
            ))

        # 读取测试脚本
        if not os.path.exists(self.test_script_path):
            return False

        try:
            with open(self.test_script_path, "r", encoding="utf-8") as f:
                content = f.read()
        except IOError:
            return False

        original_content = content

        # 查找并替换 SwipeScreen 调用中的坐标
        # 模式: SwipeScreen -startX NNNN -startY NNNN -endX NNNN -endY NNNN
        swipe_pattern = re.compile(
            r'(SwipeScreen\s+-startX\s+)(\d+)(\s+-startY\s+)(\d+)'
            r'(\s+-endX\s+)(\d+)(\s+-endY\s+)(\d+)',
            re.IGNORECASE
        )

        def _recalculate(match):
            """根据新的屏幕尺寸按比例调整坐标。"""
            # TODO(P3): 硬编码的原始屏幕尺寸，应从生成的 PS1 脚本中解析
            # $OrigScreenW/$OrigScreenH 变量，或由 config_generator 写入注释行
            orig_w, orig_h = 1440, 3120
            ratio_w = float(screen_w) / orig_w
            ratio_h = float(screen_h) / orig_h

            sx = int(int(match.group(2)) * ratio_w)
            sy = int(int(match.group(4)) * ratio_h)
            ex = int(int(match.group(6)) * ratio_w)
            ey = int(int(match.group(8)) * ratio_h)

            return "{0}{1}{2}{3}{4}{5}{6}{7}".format(
                match.group(1), sx,
                match.group(3), sy,
                match.group(5), ex,
                match.group(7), ey,
            )

        content = swipe_pattern.sub(_recalculate, content)

        if content == original_content:
            # 没有实际变更
            return False

        return self._write_file_preserving_bom(self.test_script_path, content)

    def _fix_webview_wait_increase(self, fix):
        """
        webview_wait_increase: 增加 WebView 加载等待时间。

        在测试脚本中查找 Phase 4 附近的 HumanDelay 并增大参数。
        """
        changes = fix.get("changes", [])

        for change in changes:
            old_val = change.get("old_value", "")
            new_val = change.get("new_value", "")
            if old_val and new_val and old_val != new_val:
                if self._replace_in_file(self.test_script_path, old_val, new_val):
                    return True

        # 如果 changes 没有明确的搜索/替换对，使用通用策略
        # 搜索 Phase 4 区域的 HumanDelay 并按 1.5 倍增加
        return self._increase_phase_delay_in_script(4, 1.5)

    # ================================================================
    # 内部工具方法
    # ================================================================

    def _build_report(self, total_attempts, final_results, fixes_applied, success):
        """
        构建最终汇总报告。

        返回:
            dict: 报告字典
        """
        pass_count = final_results.get("pass_count", 0)
        total = final_results.get("total", 0)

        if success:
            summary = "全部 {0}/{1} 阶段通过，共尝试 {2} 次".format(
                pass_count, total, total_attempts
            )
            if fixes_applied:
                summary += "，应用了 {0} 个自动修复".format(len(fixes_applied))
        else:
            failed_phases = []
            phases = final_results.get("phases", {})
            for pnum in sorted(phases.keys()):
                if not phases[pnum].get("passed"):
                    failed_phases.append(str(pnum))
            summary = "{0}/{1} 阶段通过，{2} 次尝试后仍有失败".format(
                pass_count, total, total_attempts
            )
            if failed_phases:
                summary += " (失败阶段: {0})".format(", ".join(failed_phases))
            if fixes_applied:
                summary += "，已应用 {0} 个修复".format(len(fixes_applied))

        report = {
            "total_attempts": total_attempts,
            "final_results": final_results,
            "fixes_applied": fixes_applied,
            "success": success,
            "summary": summary,
            "log": list(self._log_buffer),
            "timestamp": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            "phase_screenshots": getattr(self, '_phase_screenshots', {}),
        }
        report["screenshots"] = []
        # Collect any screenshots from fixes
        for fix in fixes_applied:
            if fix.get("screenshot"):
                report["screenshots"].append(fix["screenshot"])
        return report

    def _replace_in_file(self, file_path, search, replace):
        """
        在文件中执行字符串替换（仅替换第一个匹配）。

        参数:
            file_path (str): 文件路径
            search (str): 搜索字符串
            replace (str): 替换字符串

        返回:
            bool: 是否执行了替换
        """
        if not search or not replace:
            return False

        if not os.path.exists(file_path):
            return False

        try:
            with open(file_path, "r", encoding="utf-8") as f:
                content = f.read()
        except IOError:
            return False

        if search not in content:
            # 检查是否已经应用过此修复
            if replace in content:
                self._log_info("修复已存在（之前已应用），跳过。")
                return False
            return False

        new_content = content.replace(search, replace, 1)  # 仅替换第一个匹配
        return self._write_file_preserving_bom(file_path, new_content)

    def _write_file_preserving_bom(self, file_path, content):
        """
        写入文件，保留原有 UTF-8 BOM（如果存在）。

        参数:
            file_path (str): 文件路径
            content (str): 文件内容

        返回:
            bool: 是否成功写入
        """
        try:
            # 检查原文件是否有 BOM
            has_bom = False
            if os.path.exists(file_path):
                with open(file_path, "rb") as f:
                    raw_head = f.read(3)
                has_bom = (raw_head[:3] == b"\xef\xbb\xbf")

            with open(file_path, "wb") as f:
                if has_bom:
                    f.write(b"\xef\xbb\xbf")
                f.write(content.encode("utf-8"))
            return True
        except IOError as e:
            self._log_error("写入文件失败 {0}: {1}".format(file_path, str(e)))
            return False

    def _increase_ops_delays(self, operation_name, factor):
        """
        增加 operations.json 中指定操作的所有 delay 步骤的时间。

        参数:
            operation_name (str): 操作名称（如 "navigate_to_feed"、"like"）
            factor (float): 增加倍数

        返回:
            bool: 是否成功修改
        """
        if not operation_name:
            return False

        if not os.path.exists(self.operations_path):
            self._log_warn("操作文件不存在: {0}".format(self.operations_path))
            return False

        try:
            with open(self.operations_path, "r", encoding="utf-8") as f:
                ops_data = json.load(f)
        except (ValueError, IOError) as e:
            self._log_error("读取操作文件失败: {0}".format(str(e)))
            return False

        operations = ops_data.get("operations", {})
        op = operations.get(operation_name)
        if not op:
            self._log_warn("操作不存在: {0}".format(operation_name))
            return False

        steps = op.get("steps", [])
        modified = False

        for step in steps:
            if step.get("action") == "delay":
                old_min = step.get("min_ms", 0)
                old_max = step.get("max_ms", 0)
                if old_min > 0:
                    step["min_ms"] = int(old_min * factor)
                    modified = True
                if old_max > 0:
                    step["max_ms"] = int(old_max * factor)
                    modified = True

        if modified:
            try:
                with open(self.operations_path, "w", encoding="utf-8") as f:
                    json.dump(ops_data, f, indent=4, ensure_ascii=False)
                return True
            except IOError as e:
                self._log_error("写入操作文件失败: {0}".format(str(e)))
                return False

        return False

    def _increase_phase_delay_in_script(self, phase, factor):
        """
        在测试脚本中查找指定 Phase 区域的 HumanDelay 并按倍数增加。

        参数:
            phase (int): 阶段号
            factor (float): 增加倍数

        返回:
            bool: 是否成功修改
        """
        if not os.path.exists(self.test_script_path):
            return False

        try:
            with open(self.test_script_path, "r", encoding="utf-8") as f:
                content = f.read()
        except IOError:
            return False

        # 定位 Phase N 区域: 从 "# Phase N:" 到下一个 "# Phase" 或文件末尾
        phase_marker = "Phase {0}".format(phase)
        next_phase_marker = "Phase {0}".format(phase + 1)

        start_idx = content.find(phase_marker)
        if start_idx < 0:
            return False

        end_idx = content.find(next_phase_marker, start_idx + len(phase_marker))
        if end_idx < 0:
            end_idx = len(content)

        section = content[start_idx:end_idx]
        original_section = section

        # 匹配 HumanDelay -minMs NNNN -maxMs NNNN
        delay_pattern = re.compile(
            r'(HumanDelay\s+-minMs\s+)(\d+)(\s+-maxMs\s+)(\d+)'
        )

        def _multiply(match):
            """按倍数增加 delay 参数。"""
            min_val = int(int(match.group(2)) * factor)
            max_val = int(int(match.group(4)) * factor)
            return "{0}{1}{2}{3}".format(
                match.group(1), min_val,
                match.group(3), max_val,
            )

        new_section = delay_pattern.sub(_multiply, section, count=1)

        if new_section == original_section:
            return False

        new_content = content[:start_idx] + new_section + content[end_idx:]
        return self._write_file_preserving_bom(self.test_script_path, new_content)

    def _extract_operation_name(self, changes):
        """
        从 changes 列表的 path 中提取操作名称。

        例如 path="operations.navigate_to_feed.steps[delay]" → "navigate_to_feed"
        """
        for change in changes:
            path = change.get("path", "")
            if path.startswith("operations."):
                parts = path.split(".")
                if len(parts) >= 2:
                    return parts[1]
        return ""

    def _decode_output(self, raw_bytes):
        """
        尝试解码 subprocess 输出。优先 utf-8，回退 gbk/cp936。

        参数:
            raw_bytes (bytes): 原始字节

        返回:
            str: 解码后的字符串
        """
        if not raw_bytes:
            return ""

        # 跳过 UTF-8 BOM
        if raw_bytes[:3] == b"\xef\xbb\xbf":
            raw_bytes = raw_bytes[3:]

        # 尝试 UTF-8
        try:
            return raw_bytes.decode("utf-8")
        except (UnicodeDecodeError, ValueError):
            pass

        # 尝试 GBK (Windows 中文环境常用)
        try:
            return raw_bytes.decode("gbk")
        except (UnicodeDecodeError, ValueError):
            pass

        # 尝试 cp936
        try:
            return raw_bytes.decode("cp936")
        except (UnicodeDecodeError, ValueError):
            pass

        # 最终兜底: lossy decode
        return raw_bytes.decode("utf-8", errors="replace")
