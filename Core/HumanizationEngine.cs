// =====================================================
// HumanizationEngine.cs - Shared Humanization Behaviors
// =====================================================
// Purpose: Provides human-like behavior patterns for social media automation
//          Extracted from Reddit scripts for cross-platform reuse
//
// Usage:
//   1. Initialize profile and RNG:
//      string profileName = GetVar("humanization_profile", "casual");
//      var profile = GetProfileConfig(profileName);
//      var rng = new System.Random();
//
//   2. Use humanized delays:
//      System.Threading.Thread.Sleep(HumanizedDelay(3000, profile, rng));
//
//   3. Use humanized taps:
//      HumanizedTap(bounds, profile, rng);
//
//   4. Use humanized swipes:
//      HumanizedSwipe(startX, startY, endX, endY, duration, profile, rng);
//
//   5. Check probabilistic behaviors:
//      if (ShouldTriggerProbabilistic("accidental_back", profile, rng)) { ... }
//
// Profiles:
//   - speed_demon: Fast, precise, minimal variance (0.6x delays, 5px offset)
//   - casual: Balanced, moderate variance (1.0x delays, 15px offset) [DEFAULT]
//   - deep_reader: Slow, thoughtful, high variance (1.5x delays, 10px offset)
//   - distracted: Erratic, very high variance (1.2x delays, 20px offset)
//
// Dependencies:
//   - instance.DroidInstance.Input (ZennoDroid API)
//   - project.Variables (for GetVar/SetVar)
//   - System.Random, System.Collections.Generic.Dictionary
// =====================================================

// All functions defined in ScriptHelpers.cs (loaded first by ModuleLoader)
