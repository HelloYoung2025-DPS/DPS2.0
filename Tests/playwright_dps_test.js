// DPS legacy configuration checks and explicitly requested Android smoke tests.
//
// Static/config mode has no Playwright dependency:
//   node Tests/playwright_dps_test.js --mode config
//
// Device mode is a real, required device gate and fails when Playwright or an
// authorized Android device is absent:
//   node Tests/playwright_dps_test.js --mode device

'use strict';

const fs = require('fs');
const path = require('path');

const PROJECT_ROOT = path.resolve(__dirname, '..');
const VALID_MODES = new Set(['config', 'device', 'all']);
const testResults = [];

function log(message) {
  console.log(`[DPS-Test] ${message}`);
}

function recordResult(name, passed, details, evidenceClass) {
  const result = {
    name,
    passed: passed === true,
    details: String(details),
    evidenceClass,
    timestamp: new Date().toISOString()
  };
  testResults.push(result);

  const status = result.passed ? '✓ PASS' : '✗ FAIL';
  console.log(`  ${status} - ${name}`);
  console.log(`    ${result.details}`);
}

function parseArguments(argv) {
  const options = {
    mode: 'config',
    report: path.join(PROJECT_ROOT, 'Reports/playwright_test_report.json')
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--mode') {
      index += 1;
      if (index >= argv.length) {
        throw new Error('--mode requires config, device, or all');
      }
      options.mode = argv[index];
    } else if (argument === '--report') {
      index += 1;
      if (index >= argv.length) {
        throw new Error('--report requires a path');
      }
      options.report = path.resolve(argv[index]);
    } else if (argument === '--help' || argument === '-h') {
      options.help = true;
    } else {
      throw new Error(`unknown argument: ${argument}`);
    }
  }

  if (!VALID_MODES.has(options.mode)) {
    throw new Error(`invalid mode: ${options.mode}`);
  }
  return options;
}

function printHelp() {
  console.log('Usage: node Tests/playwright_dps_test.js [options]');
  console.log('  --mode config|device|all  config is the safe default');
  console.log('  --report PATH             evidence report path');
}

function readJson(relativePath) {
  const absolutePath = path.join(PROJECT_ROOT, relativePath);
  return JSON.parse(fs.readFileSync(absolutePath, 'utf-8'));
}

async function testRedditIntentsConfig() {
  log('验证 Reddit 意图映射配置...');
  try {
    const config = readJson('Config/IntentMappings/reddit_intents.json');
    const hasLikeContent = Boolean(config.intents && config.intents.like_content);
    const hasFollowEntity = Boolean(config.intents && config.intents.follow_entity);
    const hasShareContent = Boolean(config.intents && config.intents.share_content);
    const hasLikeMapping = Boolean(
      config.action_to_intent && config.action_to_intent.like === 'like_content'
    );
    const passed = hasLikeContent && hasFollowEntity && hasShareContent && hasLikeMapping;
    recordResult(
      'Reddit 意图映射配置',
      passed,
      passed ? '必需意图和 like 映射存在' : '必需意图或 like 映射缺失',
      'REPOSITORY_STATIC'
    );
    return passed;
  } catch (error) {
    recordResult('Reddit 意图映射配置', false, error.message, 'REPOSITORY_STATIC');
    return false;
  }
}

async function testInstagramOperationsConfig() {
  log('验证 Instagram like 回退链配置...');
  try {
    const config = readJson('Config/Operations/instagram_operations.json');
    const likeOperation = config.operations && config.operations.like;
    const doubleTapOperation = config.operations && config.operations.double_tap_like;
    const likeSteps = likeOperation && likeOperation.steps;
    const hasIfExists = Boolean(
      Array.isArray(likeSteps) && likeSteps.some((step) => step.action === 'if_exists')
    );
    const hasDoubleTapCall = Boolean(
      likeOperation && JSON.stringify(likeOperation).includes('double_tap_like')
    );
    const hasDoubleTapOperation = Boolean(
      doubleTapOperation && Array.isArray(doubleTapOperation.steps)
    );
    const passed = hasIfExists && hasDoubleTapCall && hasDoubleTapOperation;
    recordResult(
      'Instagram like 回退链配置',
      passed,
      passed ? 'if_exists 与 double_tap_like 回退存在' : '回退链不完整',
      'REPOSITORY_STATIC'
    );
    return passed;
  } catch (error) {
    recordResult('Instagram like 回退链配置', false, error.message, 'REPOSITORY_STATIC');
    return false;
  }
}

async function testBabyCenterConfig() {
  log('验证 BabyCenter 配置集合...');
  try {
    const platforms = readJson('Config/PlatformsConfig.json');
    const platform = platforms.platforms && platforms.platforms.babycenter;
    const operationsPath = path.join(
      PROJECT_ROOT,
      'Config/Operations/babycenter_operations.json'
    );
    const intentsPath = path.join(
      PROJECT_ROOT,
      'Config/IntentMappings/babycenter_intents.json'
    );
    const passed = Boolean(
      platform && platform.enabled && fs.existsSync(operationsPath) && fs.existsSync(intentsPath)
    );
    recordResult(
      'BabyCenter 配置集合',
      passed,
      passed ? '平台、操作和意图配置存在' : '配置缺失或平台未启用',
      'REPOSITORY_STATIC'
    );
    return passed;
  } catch (error) {
    recordResult('BabyCenter 配置集合', false, error.message, 'REPOSITORY_STATIC');
    return false;
  }
}

async function testAppsConfig() {
  log('验证 Apps.json 应用开关...');
  try {
    const config = readJson('Config/Apps.json');
    const enabled = Boolean(
      config.apps && config.apps.babycenter && config.apps.babycenter.enabled
    );
    recordResult(
      'Apps.json 应用开关',
      enabled,
      enabled ? 'babycenter.enabled = true' : 'babycenter.enabled 不是 true',
      'REPOSITORY_STATIC'
    );
    return enabled;
  } catch (error) {
    recordResult('Apps.json 应用开关', false, error.message, 'REPOSITORY_STATIC');
    return false;
  }
}

function loadAndroidDriver() {
  try {
    // Loaded only for an explicit device/all run. Config mode remains dependency-free.
    return require('playwright')._android;
  } catch (error) {
    throw new Error(`Playwright Android driver is unavailable: ${error.message}`);
  }
}

async function testAndroidConnection(android) {
  log('验证必需 Android 设备连接...');
  try {
    const devices = await android.devices();
    if (!Array.isArray(devices) || devices.length === 0) {
      recordResult(
        'Android 设备连接',
        false,
        '没有检测到授权设备；必需设备测试不能 SKIP',
        'DEVICE'
      );
      return null;
    }
    const device = devices[0];
    const model = await device.model();
    const serial = await device.serial();
    recordResult('Android 设备连接', true, `${model} (${serial})`, 'DEVICE');
    return device;
  } catch (error) {
    recordResult('Android 设备连接', false, error.message, 'DEVICE');
    return null;
  }
}

async function testDeviceScreenshot(device) {
  log('验证设备截图原生结果...');
  try {
    const screenshotPath = path.join(PROJECT_ROOT, 'Screenshots/playwright_test.png');
    await device.screenshot({ path: screenshotPath });
    const stats = fs.statSync(screenshotPath);
    const passed = stats.isFile() && stats.size > 0;
    recordResult(
      '设备截图功能',
      passed,
      passed ? `已写入 ${stats.size} 字节` : '截图结果为空',
      'DEVICE'
    );
    return passed;
  } catch (error) {
    recordResult('设备截图功能', false, error.message, 'DEVICE');
    return false;
  }
}

function shellText(value) {
  if (Buffer.isBuffer(value)) {
    return value.toString('utf-8');
  }
  return String(value || '');
}

async function testRedditAppLaunch(device) {
  log('验证 Reddit APP 启动后置条件...');
  try {
    const launchResult = shellText(
      await device.shell('am start -n com.reddit.frontpage/.MainActivity')
    );
    if (/error|exception|does not exist/i.test(launchResult)) {
      throw new Error(`native launch rejected: ${launchResult.trim()}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 3000));
    const activityState = shellText(
      await device.shell('dumpsys activity activities')
    );
    const windowState = shellText(await device.shell('dumpsys window windows'));
    const foregroundVerified =
      /mResumedActivity[^\n]*com\.reddit\.frontpage/i.test(activityState) ||
      /mCurrentFocus[^\n]*com\.reddit\.frontpage/i.test(windowState);
    if (!foregroundVerified) {
      throw new Error('Reddit package was not verified as the resumed/focused app');
    }
    const screenshotPath = path.join(PROJECT_ROOT, 'Screenshots/reddit_launched.png');
    await device.screenshot({ path: screenshotPath });
    const screenshotSize = fs.statSync(screenshotPath).size;
    const passed = screenshotSize > 0;
    recordResult(
      'Reddit APP 启动',
      passed,
      passed ? '原生启动结果与前台包名均已验证' : '启动截图为空',
      'DEVICE'
    );
    return passed;
  } catch (error) {
    recordResult('Reddit APP 启动', false, error.message, 'DEVICE');
    return false;
  }
}

async function runConfigTests() {
  await testRedditIntentsConfig();
  await testInstagramOperationsConfig();
  await testBabyCenterConfig();
  await testAppsConfig();
}

async function runDeviceTests() {
  let android;
  try {
    android = loadAndroidDriver();
  } catch (error) {
    recordResult('Playwright Android 驱动', false, error.message, 'DEVICE');
    return;
  }
  const device = await testAndroidConnection(android);
  if (!device) {
    return;
  }
  await testDeviceScreenshot(device);
  await testRedditAppLaunch(device);
}

function buildReport(mode) {
  const passed = testResults.filter((result) => result.passed).length;
  const total = testResults.length;
  const failed = total - passed;
  return {
    schema_version: 'dps.playwright-evidence/v1',
    verification_level: mode === 'config' ? 'REPOSITORY_STATIC_ONLY' : null,
    mode,
    project_root: PROJECT_ROOT,
    timestamp: new Date().toISOString(),
    total,
    passed,
    failed,
    success: total > 0 && failed === 0,
    successRate: total > 0 ? Math.round((passed / total) * 100) : 0,
    results: testResults
  };
}

function writeReport(reportPath, report) {
  fs.mkdirSync(path.dirname(reportPath), { recursive: true });
  fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, 'utf-8');
}

async function main(argv) {
  const options = parseArguments(argv);
  if (options.help) {
    printHelp();
    return 0;
  }

  console.log('========================================');
  console.log('  DPS deterministic test runner');
  console.log(`  mode: ${options.mode}`);
  console.log(`  root: ${PROJECT_ROOT}`);
  console.log('========================================');

  if (options.mode === 'config' || options.mode === 'all') {
    await runConfigTests();
  }
  if (options.mode === 'device' || options.mode === 'all') {
    await runDeviceTests();
  }

  const report = buildReport(options.mode);
  writeReport(options.report, report);
  console.log('========================================');
  console.log(`总计: ${report.total}; 通过: ${report.passed}; 失败: ${report.failed}`);
  console.log(`报告: ${options.report}`);
  if (report.total === 0) {
    console.error('ERROR: zero tests cannot satisfy a required gate');
    return 1;
  }
  return report.success ? 0 : 1;
}

main(process.argv.slice(2))
  .then((exitCode) => {
    process.exitCode = exitCode;
  })
  .catch((error) => {
    console.error(`测试执行失败: ${error.stack || error.message}`);
    process.exitCode = 1;
  });
