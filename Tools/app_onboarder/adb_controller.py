# -*- coding: utf-8 -*-
"""
ADB Controller — ADB 命令封装模块
DPS v4.5 App Onboarder 工具

封装所有 ADB 交互，提供统一的设备操作接口。
"""

import subprocess
import os
import time
import random


class ADBController:
    """ADB 命令封装，提供设备控制、UI dump、截图等操作"""

    # 设备端临时文件路径
    # 注意: Android 11+ Scoped Storage 可能导致 /sdcard 写入失败，
    # 使用 /data/local/tmp 作为主路径（不受 Scoped Storage 限制）
    REMOTE_DUMP_PATH = "/data/local/tmp/window_dump.xml"
    REMOTE_SCREENSHOT_PATH = "/data/local/tmp/onboarder_screenshot.png"

    # 已知的 ADB 可执行文件搜索路径（按优先级排列）
    KNOWN_ADB_PATHS = [
        r"D:\Program Files\ZennoLab\EN\ZennoDroid Enterprise\2.4.7.0\Progs\adb.exe",
        r"C:\Program Files\ZennoLab\EN\ZennoDroid Enterprise\2.4.7.0\Progs\adb.exe",
        r"D:\Program Files (x86)\ZennoLab\EN\ZennoDroid Enterprise\2.4.7.0\Progs\adb.exe",
    ]

    def __init__(self, device_id=None, work_dir=None, adb_path=None):
        """
        初始化 ADB 控制器

        Args:
            device_id: 指定设备 ID（多设备时使用），None 则使用默认设备
            work_dir: 本地工作目录，用于存放 dump/截图文件
            adb_path: ADB 可执行文件的完整路径。None 则自动探测：
                      先尝试系统 PATH 中的 'adb'，再搜索已知 ZennoDroid 安装路径。
        """
        self.device_id = device_id
        self.work_dir = work_dir or os.path.expanduser("~")
        self._screen_size = None  # 缓存屏幕分辨率
        self.adb_path = adb_path or self._detect_adb()

    def _detect_adb(self):
        """
        自动探测 ADB 可执行文件路径。

        优先使用系统 PATH 中的 adb，其次搜索已知的 ZennoDroid 安装路径。

        Returns:
            str: ADB 可执行文件路径
        """
        import shutil
        # 1. 尝试系统 PATH
        system_adb = shutil.which("adb")
        if system_adb:
            return system_adb

        # 2. 搜索已知路径
        for candidate in self.KNOWN_ADB_PATHS:
            if os.path.isfile(candidate):
                return candidate

        # 3. 兜底：返回 "adb"，让 subprocess 报 FileNotFoundError
        return "adb"

    def run_cmd(self, args, timeout=30):
        """
        执行 adb 命令

        Args:
            args: 命令参数列表，如 ["shell", "input", "tap", "100", "200"]
            timeout: 超时秒数

        Returns:
            tuple: (exit_code, stdout_output)
        """
        cmd = [self.adb_path]
        if self.device_id:
            cmd.extend(["-s", self.device_id])
        if isinstance(args, str):
            cmd.extend(args.split())
        else:
            cmd.extend([str(a) for a in args])

        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=timeout,
                encoding="utf-8",
                errors="replace"
            )
            output = result.stdout.strip()
            if result.returncode != 0 and result.stderr.strip():
                # 有些 adb 命令把正常输出写到 stderr（如 pull 的进度信息）
                output = output or result.stderr.strip()
            return result.returncode, output
        except subprocess.TimeoutExpired:
            return -1, "TIMEOUT"
        except FileNotFoundError:
            return -2, "ADB_NOT_FOUND"

    # === 设备状态 ===

    def is_connected(self):
        """检查 ADB 设备是否已连接"""
        code, output = self.run_cmd(["get-state"])
        return code == 0 and "device" in output

    def get_screen_size(self):
        """
        获取屏幕分辨率

        Returns:
            tuple: (width, height)，如 (1440, 3120)
        """
        if self._screen_size:
            return self._screen_size
        code, output = self.run_cmd(["shell", "wm", "size"])
        if code == 0 and "x" in output:
            # 输出格式: "Physical size: 1440x3120"
            # 可能还有 "Override size: ..." 行，取最后一行
            for line in reversed(output.strip().split("\n")):
                line = line.strip()
                if "x" in line:
                    size_part = line.split(":")[-1].strip()
                    parts = size_part.split("x")
                    if len(parts) == 2:
                        try:
                            self._screen_size = (int(parts[0]), int(parts[1]))
                            return self._screen_size
                        except ValueError:
                            continue
        # 兜底默认值（使用更常见的 1080p 分辨率而非旗舰机 1440p）
        return (1080, 2340)

    def get_current_activity(self):
        """
        获取当前前台 Activity 名

        Returns:
            str: Activity 完整名，如 "com.app/.MainActivity"
        """
        code, output = self.run_cmd(
            ["shell", "dumpsys", "activity", "activities"],
            timeout=10
        )
        if code != 0:
            return ""
        # 解析 mResumedActivity 行
        for line in output.split("\n"):
            if "mResumedActivity" in line or "mFocusedActivity" in line:
                # 格式: mResumedActivity: ActivityRecord{xxx u0 com.app/.Activity t123}
                parts = line.split()
                for part in parts:
                    if "/" in part and "." in part:
                        return part.rstrip("}")
        return ""

    def get_device_info(self):
        """
        获取完整设备信息

        Returns:
            dict: {
                "model": "Pixel 6",
                "brand": "google",
                "android_version": "13",
                "sdk_version": "33",
                "screen_size": (1080, 2400),
                "dpi": 420,
                "serial": "XXXX"
            }
        """
        info = {
            "model": "",
            "brand": "",
            "android_version": "",
            "sdk_version": "",
            "screen_size": self.get_screen_size(),
            "dpi": 0,
            "serial": self.device_id or "",
        }

        # Model
        code, output = self.run_cmd(["shell", "getprop", "ro.product.model"])
        if code == 0 and output:
            info["model"] = output.strip()

        # Brand
        code, output = self.run_cmd(["shell", "getprop", "ro.product.brand"])
        if code == 0 and output:
            info["brand"] = output.strip()

        # Android version
        code, output = self.run_cmd(["shell", "getprop", "ro.build.version.release"])
        if code == 0 and output:
            info["android_version"] = output.strip()

        # SDK version
        code, output = self.run_cmd(["shell", "getprop", "ro.build.version.sdk"])
        if code == 0 and output:
            info["sdk_version"] = output.strip()

        # DPI
        code, output = self.run_cmd(["shell", "wm", "density"])
        if code == 0 and output:
            for line in output.strip().split("\n"):
                line = line.strip()
                if ":" in line:
                    try:
                        info["dpi"] = int(line.split(":")[-1].strip())
                    except ValueError:
                        pass

        # Serial (if not provided)
        if not info["serial"]:
            code, output = self.run_cmd(["get-serialno"])
            if code == 0 and output:
                info["serial"] = output.strip()

        return info

    # === APP 控制 ===

    def launch_app(self, package_name):
        """
        通过 monkey 启动 APP

        Args:
            package_name: APP 包名
        """
        self.run_cmd([
            "shell", "monkey",
            "-p", package_name,
            "-c", "android.intent.category.LAUNCHER",
            "1"
        ])

    def force_stop(self, package_name):
        """强制停止 APP"""
        self.run_cmd(["shell", "am", "force-stop", package_name])

    # === UI 操作 ===

    def dump_ui(self, name="dump", max_retries=3):
        """
        执行 uiautomator dump 并拉取 XML

        Args:
            name: dump 文件名标识（用于区分不同阶段的 dump）
            max_retries: 最大重试次数

        Returns:
            str: XML 内容
        """
        local_path = os.path.join(
            self.work_dir,
            "onboarder_{}.xml".format(name)
        )

        for attempt in range(1, max_retries + 1):
            # 清除本地旧文件，防止读到过期数据
            if os.path.exists(local_path):
                try:
                    os.remove(local_path)
                except OSError:
                    pass

            # 清除设备端旧文件
            self.run_cmd(["shell", "rm", "-f", self.REMOTE_DUMP_PATH])

            # dump（自适应等待：每次重试增加等待时间）
            wait_time = 0.8 + (attempt - 1) * 0.8
            code, output = self.run_cmd(
                ["shell", "uiautomator", "dump", self.REMOTE_DUMP_PATH],
                timeout=15
            )

            # 检查 dump 命令是否成功
            if code != 0 or "Killed" in output:
                # ZennoDroid/Appium UIAutomator2 Server 可能占用 uiautomator
                # 尝试 kill 它后重试
                if attempt == 1 and (code == 137 or "Killed" in output):
                    self.run_cmd(
                        ["shell", "am", "force-stop", "io.appium.uiautomator2.server"]
                    )
                    time.sleep(1.5)
                if attempt < max_retries:
                    time.sleep(wait_time)
                    continue
                raise RuntimeError(
                    "dump_ui 失败: uiautomator dump 返回错误 (code={}, output={})".format(
                        code, output[:200]
                    )
                )

            time.sleep(wait_time)

            # pull
            pull_code, pull_output = self.run_cmd(["pull", self.REMOTE_DUMP_PATH, local_path])
            if pull_code != 0:
                if attempt < max_retries:
                    time.sleep(0.5)
                    continue
                raise RuntimeError("dump_ui 失败: pull 失败 (code={})".format(pull_code))

            if not os.path.exists(local_path):
                if attempt < max_retries:
                    time.sleep(0.5)
                    continue
                raise RuntimeError("dump_ui 失败: 未找到本地 XML: {}".format(local_path))

            with open(local_path, "r", encoding="utf-8") as f:
                content = f.read()

            # 验证 XML 内容有效（至少包含 hierarchy 根节点）
            if not content.strip() or "<hierarchy" not in content:
                if attempt < max_retries:
                    time.sleep(0.5)
                    continue
                raise RuntimeError("dump_ui 失败: XML 内容无效或为空")

            return content

        raise RuntimeError("dump_ui 失败: 超过最大重试次数 {}".format(max_retries))

    def screenshot(self, name="screen"):
        """
        截图并拉取到本地

        Args:
            name: 截图文件名标识

        Returns:
            str: 本地截图文件路径
        """
        local_path = os.path.join(
            self.work_dir,
            "onboarder_{}.png".format(name)
        )

        self.run_cmd(["shell", "screencap", "-p", self.REMOTE_SCREENSHOT_PATH])
        time.sleep(0.5)
        self.run_cmd(["pull", self.REMOTE_SCREENSHOT_PATH, local_path])

        if not os.path.exists(local_path):
            raise RuntimeError("screenshot 失败: 未找到: {}".format(local_path))

        return local_path

    def tap(self, x, y, humanized=True):
        """
        点击坐标

        Args:
            x: X 坐标
            y: Y 坐标
            humanized: 是否加入随机偏移（±5px）
        """
        if humanized:
            x += random.randint(-5, 5)
            y += random.randint(-5, 5)
        self.run_cmd(["shell", "input", "tap", str(int(x)), str(int(y))])

    def swipe(self, sx, sy, ex, ey, duration=500, humanized=True):
        """
        滑动手势

        Args:
            sx, sy: 起点坐标
            ex, ey: 终点坐标
            duration: 滑动持续时间(ms)
            humanized: 是否加入随机偏移
        """
        if humanized:
            sx += random.randint(-8, 8)
            sy += random.randint(-6, 6)
            ex += random.randint(-8, 8)
            ey += random.randint(-6, 6)
            duration += random.randint(-50, 50)
            if duration < 200:
                duration = 200
        self.run_cmd([
            "shell", "input", "swipe",
            str(int(sx)), str(int(sy)),
            str(int(ex)), str(int(ey)),
            str(int(duration))
        ])

    def scroll_down(self, distance=800):
        """
        向下滚动（从屏幕中部向上滑动）

        Args:
            distance: 滚动距离(px)
        """
        w, h = self.get_screen_size()
        cx = w // 2
        start_y = int(h * 0.65)
        end_y = start_y - distance
        if end_y < int(h * 0.1):
            end_y = int(h * 0.1)
        self.swipe(cx, start_y, cx, end_y, duration=600)

    def scroll_up(self, distance=800):
        """向上滚动"""
        w, h = self.get_screen_size()
        cx = w // 2
        start_y = int(h * 0.35)
        end_y = start_y + distance
        if end_y > int(h * 0.9):
            end_y = int(h * 0.9)
        self.swipe(cx, start_y, cx, end_y, duration=600)

    def swipe_left(self, y=None):
        """水平左滑（查看下一项）"""
        w, h = self.get_screen_size()
        if y is None:
            y = h // 2
        self.swipe(int(w * 0.8), y, int(w * 0.2), y, duration=500)

    def swipe_right(self, y=None):
        """水平右滑（查看上一项）"""
        w, h = self.get_screen_size()
        if y is None:
            y = h // 2
        self.swipe(int(w * 0.2), y, int(w * 0.8), y, duration=500)

    def input_text(self, text):
        """
        输入文本

        Args:
            text: 要输入的文本（空格会被替换为 %s）
        """
        # Android input text 不支持空格，用 %s 替代
        safe_text = text.replace(" ", "%s")
        self.run_cmd(["shell", "input", "text", safe_text])

    def press_back(self):
        """按返回键 (KEYCODE_BACK)"""
        self.run_cmd(["shell", "input", "keyevent", "KEYCODE_BACK"])

    def press_home(self):
        """按 Home 键"""
        self.run_cmd(["shell", "input", "keyevent", "KEYCODE_HOME"])

    def press_enter(self):
        """按回车键"""
        self.run_cmd(["shell", "input", "keyevent", "KEYCODE_ENTER"])

    # === 工具方法 ===

    def human_delay(self, min_ms=1000, max_ms=3000):
        """拟人化延迟"""
        delay = random.randint(min_ms, max_ms) / 1000.0
        time.sleep(delay)
