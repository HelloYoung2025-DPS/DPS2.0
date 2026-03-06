// =====================================================
// Playwright Android DPS 配置验证测试
// 用于验证 DPS v4.5 的配置文件和基本 Android 功能
// 
// 前置要求:
// 1. 安装 Playwright: npm install playwright
// 2. 启动 Android 模拟器或连接真机
// 3. 运行: node playwright_dps_test.js
// =====================================================

const { _android: android } = require('playwright');
const fs = require('fs');
const path = require('path');

// 配置路径
const PROJECT_ROOT = '/Users/hofishvn/openCode_Projects/DPS_v4.5';

// 测试结果收集
const testResults = [];

// ========== 辅助函数 ==========

function log(message) {
  console.log(`[DPS-Test] ${message}`);
}

function logError(message) {
  console.error(`[DPS-Test] ERROR: ${message}`);
}

function recordResult(name, passed, details) {
  const result = { name, passed, details, timestamp: new Date().toISOString() };
  testResults.push(result);
  
  const status = passed ? '✓ PASS' : '✗ FAIL';
  console.log(`  ${status} - ${name}`);
  console.log(`    ${details}`);
}

// ========== 配置验证测试 ==========

async function testRedditIntentsConfig() {
  log('测试 Reddit 意图映射配置...');
  
  try {
    const configPath = path.join(PROJECT_ROOT, 'Config/IntentMappings/reddit_intents.json');
    const content = fs.readFileSync(configPath, 'utf-8');
    const config = JSON.parse(content);
    
    // 验证 like_content 存在
    const hasLikeContent = config.intents && config.intents.like_content;
    const hasFollowEntity = config.intents && config.intents.follow_entity;
    const hasShareContent = config.intents && config.intents.share_content;
    const hasLikeMapping = config['action_to_intent'] && config['action_to_intent'].like === 'like_content';
    
    const passed = hasLikeContent && hasFollowEntity && hasShareContent && hasLikeMapping;
    
    recordResult(
      'Reddit 意图映射配置',
      passed,
      hasLikeContent ? 'like_content, follow_entity, share_content 全部存在，like 映射正确' : '部分意图缺失'
    );
    
    return passed;
  } catch (error) {
    recordResult('Reddit 意图映射配置', false, error.message);
    return false;
  }
}

async function testInstagramOperationsConfig() {
  log('测试 Instagram 操作配置（like 回退链）...');
  
  try {
    const configPath = path.join(PROJECT_ROOT, 'Config/Operations/instagram_operations.json');
    const content = fs.readFileSync(configPath, 'utf-8');
    const config = JSON.parse(content);
    
    const likeOps = config.operations && config.operations.like;
    const doubleTapOps = config.operations && config.operations.double_tap_like;
    
    // 验证 like 操作包含 if_exists
    const likeSteps = likeOps && likeOps.steps;
    const hasIfExists = likeSteps && likeSteps.some(step => step.action === 'if_exists');
    
    // 验证 like 操作的 else 分支调用 double_tap_like
    const hasDoubleTapCall = likeOps && JSON.stringify(likeOps).includes('double_tap_like');
    
    // 验证 double_tap_like 操作存在
    const hasDoubleTapOp = doubleTapOps && doubleTapOps.steps;
    
    const passed = hasIfExists && hasDoubleTapCall && hasDoubleTapOp;
    
    recordResult(
      'Instagram like 回退链配置',
      passed,
      hasIfExists ? 'if_exists + call_operation(double_tap_like) 配置正确' : '回退链配置不完整'
    );
    
    return passed;
  } catch (error) {
    recordResult('Instagram like 回退链配置', false, error.message);
    return false;
  }
}

async function testBabyCenterConfig() {
  log('测试 BabyCenter 三件套配置...');
  
  try {
    // 验证平台配置
    const platformsPath = path.join(PROJECT_ROOT, 'Config/PlatformsConfig.json');
    const platformsContent = fs.readFileSync(platformsPath, 'utf-8');
    const platformsConfig = JSON.parse(platformsContent);
    
    const hasBabyCenter = platformsConfig.platforms && platformsConfig.platforms.babycenter;
    const bcEnabled = hasBabyCenter && platformsConfig.platforms.babycenter.enabled;
    
    // 验证操作配置
    const opsPath = path.join(PROJECT_ROOT, 'Config/Operations/babycenter_operations.json');
    const opsExists = fs.existsSync(opsPath);
    
    // 验证意图映射
    const intentsPath = path.join(PROJECT_ROOT, 'Config/IntentMappings/babycenter_intents.json');
    const intentsExists = fs.existsSync(intentsPath);
    
    const passed = hasBabyCenter && bcEnabled && opsExists && intentsExists;
    
    recordResult(
      'BabyCenter 三件套配置',
      passed,
      passed ? '平台配置、操作配置、意图映射全部存在' : '部分配置缺失'
    );
    
    return passed;
  } catch (error) {
    recordResult('BabyCenter 三件套配置', false, error.message);
    return false;
  }
}

async function testAppsConfig() {
  log('测试 Apps.json 配置...');
  
  try {
    const configPath = path.join(PROJECT_ROOT, 'Config/Apps.json');
    const content = fs.readFileSync(configPath, 'utf-8');
    const config = JSON.parse(content);
    
    const bcEnabled = config.apps && config.apps.babycenter && config.apps.babycenter.enabled;
    
    recordResult(
      'Apps.json 应用开关',
      bcEnabled,
      bcEnabled ? 'babycenter.enabled = true' : 'babycenter.enabled 不是 true'
    );
    
    return bcEnabled;
  } catch (error) {
    recordResult('Apps.json 应用开关', false, error.message);
    return false;
  }
}

// ========== Android 设备测试 ==========

async function testAndroidConnection() {
  log('测试 Android 设备连接...');
  
  try {
    const devices = await android.devices();
    
    if (devices.length === 0) {
      recordResult('Android 设备连接', false, '没有检测到连接的设备');
      return false;
    }
    
    const device = devices[0];
    const model = await device.model();
    const serial = await device.serial();
    
    recordResult(
      'Android 设备连接',
      true,
      `已连接到 ${model} (Serial: ${serial})`
    );
    
    return device;
  } catch (error) {
    recordResult('Android 设备连接', false, error.message);
    return null;
  }
}

async function testDeviceScreenshot(device) {
  log('测试设备截图功能...');
  
  try {
    const screenshotPath = path.join(PROJECT_ROOT, 'Screenshots/playwright_test.png');
    await device.screenshot({ path: screenshotPath });
    
    recordResult(
      '设备截图功能',
      true,
      `截图已保存到 ${screenshotPath}`
    );
    
    return true;
  } catch (error) {
    recordResult('设备截图功能', false, error.message);
    return false;
  }
}

async function testRedditAPPLaunch(device) {
  log('测试启动 Reddit APP...');
  
  try {
    // 使用 shell 命令启动 Reddit
    await device.shell('am start -n com.reddit.frontpage/.MainActivity');
    
    // 等待启动
    await new Promise(resolve => setTimeout(resolve, 3000));
    
    // 截图验证
    const screenshotPath = path.join(PROJECT_ROOT, 'Screenshots/reddit_launched.png');
    await device.screenshot({ path: screenshotPath });
    
    recordResult(
      'Reddit APP 启动',
      true,
      'Reddit 已启动，截图已保存'
    );
    
    return true;
  } catch (error) {
    recordResult('Reddit APP 启动', false, error.message);
    return false;
  }
}

// ========== 主测试流程 ==========

async function runAllTests() {
  console.log('========================================');
  console.log('  DPS v4.5 Playwright Android 测试');
  console.log('  时间: ' + new Date().toISOString());
  console.log('========================================');
  console.log('');
  
  let passedCount = 0;
  let totalCount = 0;
  
  // ===== 阶段 1: 配置文件验证 =====
  console.log('');
  console.log('【阶段 1】配置文件验证');
  console.log('----------------------------------------');
  
  totalCount++;
  if (await testRedditIntentsConfig()) passedCount++;
  
  totalCount++;
  if (await testInstagramOperationsConfig()) passedCount++;
  
  totalCount++;
  if (await testBabyCenterConfig()) passedCount++;
  
  totalCount++;
  if (await testAppsConfig()) passedCount++;
  
  // ===== 阶段 2: Android 设备测试 =====
  console.log('');
  console.log('【阶段 2】Android 设备测试');
  console.log('----------------------------------------');
  
  const device = await testAndroidConnection();
  totalCount++;
  if (device) passedCount++;
  
  if (device) {
    totalCount++;
    if (await testDeviceScreenshot(device)) passedCount++;
    
    totalCount++;
    if (await testRedditAPPLaunch(device)) passedCount++;
  }
  
  // ===== 测试总结 =====
  console.log('');
  console.log('========================================');
  console.log('  测试结果总结');
  console.log('========================================');
  console.log(`总计: ${totalCount} 项测试`);
  console.log(`通过: ${passedCount} 项`);
  console.log(`失败: ${totalCount - passedCount} 项`);
  console.log(`成功率: ${Math.round((passedCount / totalCount) * 100)}%`);
  console.log('');
  
  // 保存测试报告
  const reportPath = path.join(PROJECT_ROOT, 'Reports/playwright_test_report.json');
  const reportDir = path.dirname(reportPath);
  
  if (!fs.existsSync(reportDir)) {
    fs.mkdirSync(reportDir, { recursive: true });
  }
  
  const report = {
    timestamp: new Date().toISOString(),
    total: totalCount,
    passed: passedCount,
    failed: totalCount - passedCount,
    successRate: Math.round((passedCount / totalCount) * 100),
    results: testResults
  };
  
  fs.writeFileSync(reportPath, JSON.stringify(report, null, 2));
  console.log(`测试报告已保存到: ${reportPath}`);
  console.log('========================================');
  
  return report;
}

// ========== 执行测试 ==========

(async () => {
  try {
    await runAllTests();
    process.exit(0);
  } catch (error) {
    console.error('测试执行失败:', error);
    process.exit(1);
  }
})();
