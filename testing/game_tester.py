#!/usr/bin/env python3
"""
Main GameTester class with core testing logic
"""

import json
import time
from typing import Dict, List, Any
from pathlib import Path


class GameTester:
    """Main game testing orchestrator"""
    
    def __init__(self, mcp_client, llm_client, llm_monitor, test_runners):
        """Initialize game tester
        
        Args:
            mcp_client: MCP client instance
            llm_client: LLM client instance
            llm_monitor: LLM monitor instance
            test_runners: Test runners instance
        """
        self.mcp = mcp_client
        self.llm = llm_client
        self.monitor = llm_monitor
        self.runners = test_runners
        self.test_results = []
        
        # Load game context from RoseGamePlay.md
        self.game_context = self._load_game_context()
        
        # Show game context status
        if self.game_context:
            print(f"? Game context loaded from RoseGamePlay.md")
        else:
            print(f"??  Game context not found (optional)")
    
    def _load_game_context(self) -> str:
        """Load game context from RoseGamePlay.md"""
        try:
            context_file = Path(__file__).parent / "RoseGamePlay.md"
            if context_file.exists():
                with open(context_file, "r", encoding="utf-8") as f:
                    return f.read()
        except Exception as e:
            print(f"Warning: Could not load game context: {e}")
        return None
    
    def test_feature(self, feature_name: str, test_instructions: str) -> Dict[str, Any]:
        """Have LLM test a specific feature"""
        
        print(f"\n{'='*60}")
        print(f"Testing: {feature_name}")
        print(f"{'='*60}\n")
        
        # Get current state
        state = self.mcp.observe_state()
        recent_output = self.mcp.get_recent_output(30)
        
        # Build game context section
        game_context_section = ""
        if self.game_context:
            game_context_section = f"""
GAME CONTEXT - The Rose (Council of Guardians):
{self.game_context}

"""
        
        # Ask LLM to generate test plan
        prompt = f"""You are testing the '{feature_name}' feature in a MUD game client for "The Rose - Council of Guardians".

{game_context_section}Current game state:
{json.dumps(state, indent=2)}

Recent game output (last 30 lines):
{chr(10).join(recent_output[-10:])}

Test instructions:
{test_instructions}

CRITICAL: Understanding AutoGong vs AutoAttack:
????????????????????????????????????????????
**AutoGong Behavior (if testing AutoGong)**:
- AutoGong does NOT enable the "AutoAttack" feature
- AutoGong implements its OWN combat logic internally
- Uses the same attack methods as AutoAttack, but operates independently
- **CONTINUOUS COMBAT MODE**: NO idle time when AT/AC = 0
  - Rings gong ("r g") immediately when timers reset (~1.5s interval)
  - Attacks all aggressive monsters continuously
  - Loots gold/silver after kills
  - Immediately rings gong again (NO resting periods)
  - Repeats until HP drops below threshold or out of gold

**Expected AutoGong Cycle** (continuous loop):
1. Wait for AT/AC = 0 (very brief, < 2 seconds)
2. Ring gong "r g" (costs gold)
3. Monster(s) spawn as aggressive
4. IMMEDIATELY attack all aggressive monsters
5. Loot gold/silver when dead
6. GOTO step 1 (no delay except timer cooldown)

**What to Verify During AutoGong Test**:
- ? Gong rings every ~1.5 seconds when AT/AC = 0
- ? NO extended idle periods (AT/AC = 0 for > 2 seconds)
- ? Immediate attack when monster spawns
- ? Continuous combat maintained
- ? automation.autoGong = true (NOT automation.autoAttack = true)
- ? System stops when HP < GongMinHpPercent
????????????????????????????????????????????

IMPORTANT AUTONOMY AND SAFETY GUIDELINES:
You have FULL AUTHORITY to intervene during testing to protect the player:

1. INTERVENTION AUTHORITY:
   - Send commands at ANY TIME if you detect danger (aggressive monsters, low HP)
   - Use 'send_command' with {{"command": ""}} (empty string) to press ENTER and check room status
   - Compare game output vs. tracked state to verify accuracy
   - Attack aggressive monsters immediately if they're attacking the player
   - Disconnect if player HP drops critically low (< 20%)

2. SAFETY PRIORITIES (in order):
   - Player survival is PARAMOUNT - intervene immediately if threatened
   - If HP < 30% and under attack: send 'stop' command and assess
   - If HP < 20%: disconnect immediately (send 'stop' first)
   - If aggressive monster detected: send 'look' to verify, then 'attack <monster>' if confirmed

3. STATE VERIFICATION:
   - Use 'send_command' with empty string ("") to see current room/status
   - Compare observed game output with tracked state (room, combat, stats)
   - If discrepancy found: investigate before proceeding with test
   - Trust game output over tracked state if they conflict

4. PROACTIVE MONITORING:
   - Check 'observe_game_state' frequently during combat tests
   - Monitor for aggressive monsters in output even if not testing combat
   - Watch HP changes - if dropping rapidly, take protective action
   - Verify automation features match expected state
   - **For AutoGong**: Verify AT/AC timers are not idle for long periods

5. INTERVENTION EXAMPLES:
   - See "goblin attacks you" ? immediately 'attack goblin'
   - HP drops below 30% ? 'stop' then assess situation
   - Tracked state says "safe room" but output shows monster ? 'look' to verify
   - Test expects AutoGong enabled but state shows disabled ? investigate why
   - **AutoGong idle (AT/AC = 0 for > 3 seconds)** ? investigate and send "" to check status

Available MCP tools:
- observe_game_state: Get current game state (character, location, combat, automation)
- send_command: Send a command to the game (use "" to press ENTER for status check)
- wait_for_output: Wait for specific text in game output
- get_recent_output: Get last N lines from game
- set_automation: Enable/disable automation features (autogong, autoattack, autoshield)
- navigate_to: Navigate to a destination
- verify_stat_change: Check if a stat changed as expected
- verify_room_change: Check if room changed
- verify_combat_initiated: Check if combat started

IMPORTANT: Respond with ONLY valid JSON in this exact format (no explanations, no markdown):
{{{{
  "test_name": "AutoGong Feature Test",
  "description": "Test AutoGong automation feature - continuous combat mode",
  "steps": [
    {{{{
      "action": "observe_game_state",
      "params": {{}},
      "expected": "Current state retrieved"
    }}}},
    {{{{
      "action": "send_command",
      "params": {{"command": ""}},
      "expected": "Room status displayed",
      "verify_output": true
    }}}},
    {{{{
      "action": "set_automation",
      "params": {{"feature": "autogong", "enabled": true}},
      "expected": "AutoGong enabled",
      "wait_for": "AutoGong",
      "verify_output": true
    }}}},
    {{{{
      "action": "observe_game_state",
      "params": {{}},
      "expected": "automation.autoGong: true (NOT autoAttack)"
    }}}},
    {{{{
      "action": "get_recent_output",
      "params": {{"count": 10}},
      "expected": "game output retrieved",
      "verify_output": true
    }}}}
  ]
}}}}

REMEMBER:
- You can add intervention steps at ANY point in the test
- Insert 'send_command' with empty string to check status whenever uncertain
- Add 'observe_game_state' steps frequently during risky operations
- **For AutoGong tests**: Monitor for idle time (AT/AC = 0 with no action)
- Prioritize player safety over completing the test
- If you see danger in output, intervene IMMEDIATELY in next step

Set "verify_output": true on steps where you want to verify the actual game output matches expectations.
Keep test steps simple and verifiable. Each step should have a clear expected outcome.
Return ONLY the JSON, nothing else.
"""
        
        print("Asking LLM to generate test plan...")
        llm_response = self.llm.call(prompt)
        
        print(f"\nLLM Response:\n{llm_response}\n")
        
        # Parse test plan from LLM response
        try:
            # Try to extract JSON from markdown code blocks if present
            if "```json" in llm_response:
                json_start = llm_response.find("```json") + 7
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            elif "```" in llm_response:
                json_start = llm_response.find("```") + 3
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            
            test_plan = json.loads(llm_response)
        except json.JSONDecodeError as e:
            print(f"Failed to parse LLM response as JSON: {e}")
            return {
                "feature": feature_name,
                "error": "Failed to parse test plan",
                "llm_response": llm_response
            }
        
        # Execute test plan
        print(f"\nExecuting test plan: {test_plan.get('test_name', 'Unknown')}")
        print(f"Description: {test_plan.get('description', 'No description')}\n")
        
        results = []
        for i, step in enumerate(test_plan.get("steps", [])):
            action = step.get("action")
            params = step.get("params", {})
            expected = step.get("expected", "")
            wait_for = step.get("wait_for")
            verify_output = step.get("verify_output", False)
            
            print(f"Step {i+1}: {action} with {params}")
            
            result = self.mcp.call_tool(action, params)
            
            # Wait for expected output if specified
            if wait_for:
                print(f"  Waiting for: '{wait_for}'")
                wait_result = self.mcp.wait_for_output(wait_for, 5000)
                if wait_result.get("found"):
                    print(f"  ? Found: {wait_result.get('matching_line', '')[:60]}...")
                else:
                    print(f"  ? Not found (timeout)")
            
            # Ask LLM to verify the output if requested
            if verify_output:
                verification = self.monitor.verify_output(action, params, result, expected)
                result["llm_verification"] = verification
                passed = verification.get("passed", False)
                print(f"  ?? LLM Verification: {verification.get('analysis', 'N/A')}")
            else:
                # Check if expected outcome occurred (simple check)
                passed = self._check_expectation(result, expected)
            
            status = "? PASS" if passed else "? FAIL"
            print(f"  {status}: {expected}\n")
            
            results.append({
                "step": step,
                "result": result,
                "passed": passed
            })
            
            # Small delay between steps
            time.sleep(0.5)
        
        overall_pass = all(r["passed"] for r in results)
        
        print(f"\n{'='*60}")
        print(f"Test Result: {'? PASSED' if overall_pass else '? FAILED'}")
        print(f"{'='*60}\n")
        
        return {
            "feature": feature_name,
            "test_plan": test_plan,
            "results": results,
            "overall_pass": overall_pass
        }
    
    def test_with_custom_prompt(self, feature_name: str, custom_prompt: str) -> Dict[str, Any]:
        """Test with a completely custom LLM prompt"""
        
        print(f"\n{'='*60}")
        print(f"Custom Prompt Test: {feature_name}")
        print(f"{'='*60}\n")
        
        # Get current state for context
        state = self.mcp.observe_state()
        recent_output = self.mcp.get_recent_output(20)
        
        # Build game context section
        game_context_section = ""
        if self.game_context:
            game_context_section = f"""
GAME CONTEXT - The Rose (Council of Guardians):
{self.game_context}

"""
        
        # Build full prompt with context
        full_prompt = f"""You are testing the '{feature_name}' feature in "The Rose - Council of Guardians" MUD game client.

{game_context_section}
Current game state:
{json.dumps(state, indent=2)}

Recent game output (last 20 lines):
{chr(10).join(recent_output[-20:])}

CRITICAL: Understanding AutoGong vs AutoAttack:
????????????????????????????????????????????
**AutoGong Behavior**:
- AutoGong does NOT enable the "AutoAttack" feature flag
- AutoGong implements its OWN combat logic (shared methods with AutoAttack)
- **CONTINUOUS COMBAT MODE**: Maintains constant activity
  - Rings gong ("r g") every ~1.5 seconds when AT/AC = 0
  - NO idle periods - immediately rings gong when timers ready
  - Attacks all aggressive monsters continuously
  - Loots gold/silver after kills
  - Repeats until HP threshold or out of gold

**AutoAttack Behavior** (separate feature):
- Reactive: only attacks existing aggressive monsters
- Independent feature that runs when AutoGong is OFF
- Shares attack methods with AutoGong but different trigger logic

**Key Difference**:
- AutoGong = Proactive grinding (creates combat by ringing gong)
- AutoAttack = Reactive defense (responds to existing threats)
- They use same attack methods but are SEPARATE features
????????????????????????????????????????????

CRITICAL AUTONOMY AND SAFETY GUIDELINES:
You have FULL AUTHORITY to intervene at ANY TIME to protect the player:

INTERVENTION POWERS:
- Send ANY command if you detect danger or the tested feature stopped working and you want context on the situation.
- Use send_command with {{"command": ""}} (empty) to press ENTER and check status
- Attack aggressive monsters if they threaten the player
- Stop combat if HP drops dangerously low
- Disconnect player if HP < 20%

SAFETY RULES:
1. Player survival > Test completion
2. HP < 30% + under attack ? send 'stop' immediately
3. HP < 20% ? disconnect (send 'stop' first, then assess)
4. Aggressive monster attacking ? 'attack <monster>' immediately
5. Always verify state with 'look' or send "" (ENTER) if uncertain
6. **AutoGong idle check**: If AT/AC = 0 for > 3 seconds during AutoGong, investigate

STATE VERIFICATION:
- Use send_command("") frequently to check current status
- Compare game output vs tracked state (room, combat, HP)
- Trust game output over tracked state if they conflict
- If discrepancy: investigate before proceeding
- **For AutoGong**: Monitor AT/AC timers - should reset and ring gong quickly

MONITORING:
- Check observe_game_state during any risky operation
- Watch for aggressive monsters in output
- Monitor HP changes - intervene if dropping rapidly
- Verify automation matches expected state
- **For AutoGong**: Ensure continuous combat (no extended idle periods)

Available MCP tools:
- observe_game_state: Get current game state
- send_command: Send a command to the game (use "" for ENTER/status check)
- wait_for_output: Wait for specific text in output
- get_recent_output: Get last N lines from game
- set_automation: Enable/disable automation features
- navigate_to: Navigate to a destination
- verify_stat_change: Check if a stat changed
- verify_room_change: Check if room changed
- verify_combat_initiated: Check if combat started

USER'S CUSTOM TEST REQUEST:
{custom_prompt}

Generate a test plan as JSON in this format:
{{{{
  "test_name": "...",
  "description": "...",
  "steps": [
    {{{{
      "action": "tool_name",
      "params": {{}},
      "expected": "expected outcome",
      "verify_output": true/false
    }}}}
  ]
}}}}

IMPORTANT:
- Add safety checks (observe_game_state, send_command "") throughout
- Insert intervention steps if you detect danger in output
- Prioritize player safety over test objectives
- Use verify_output: true when you want to analyze actual game output
- **For AutoGong tests**: Add steps to verify continuous combat (no idle time)
- **Clarify**: AutoGong does NOT enable AutoAttack feature

Return ONLY valid JSON, no explanations.
"""
        
        print("?? Asking LLM to generate test plan from your prompt...")
        llm_response = self.llm.call(full_prompt)
        
        print(f"\nLLM Response:\n{llm_response}\n")
        
        # Parse and execute (same as test_feature)
        try:
            if "```json" in llm_response:
                json_start = llm_response.find("```json") + 7
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            elif "```" in llm_response:
                json_start = llm_response.find("```") + 3
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            
            test_plan = json.loads(llm_response)
        except json.JSONDecodeError as e:
            print(f"Failed to parse LLM response as JSON: {e}")
            return {
                "feature": feature_name,
                "error": "Failed to parse test plan",
                "llm_response": llm_response
            }
        
        # Execute test plan
        print(f"\nExecuting test plan: {test_plan.get('test_name', 'Unknown')}")
        print(f"Description: {test_plan.get('description', 'No description')}\n")
        
        results = []
        for i, step in enumerate(test_plan.get("steps", [])):
            action = step.get("action")
            params = step.get("params", {})
            expected = step.get("expected", "")
            verify_output = step.get("verify_output", False)
            
            print(f"Step {i+1}: {action} with {params}")
            
            result = self.mcp.call_tool(action, params)
            
            if verify_output:
                verification = self.monitor.verify_output(action, params, result, expected)
                result["llm_verification"] = verification
                passed = verification.get("passed", False)
                print(f"  ?? LLM Verification: {verification.get('analysis', 'N/A')}")
            else:
                passed = self._check_expectation(result, expected)
            
            status = "? PASS" if passed else "? FAIL"
            print(f"  {status}: {expected}\n")
            
            results.append({
                "step": step,
                "result": result,
                "passed": passed
            })
            
            time.sleep(0.5)
        
        overall_pass = all(r["passed"] for r in results)
        
        print(f"\n{'='*60}")
        print(f"Test Result: {'? PASSED' if overall_pass else '? FAILED'}")
        print(f"{'='*60}\n")
        
        return {
            "feature": feature_name,
            "test_plan": test_plan,
            "results": results,
            "overall_pass": overall_pass,
            "custom_prompt": custom_prompt
        }
    
    def _check_expectation(self, result: Dict, expected: str) -> bool:
        """Check if result matches expectation (simple check)"""
        # Simple string matching
        result_str = json.dumps(result).lower()
        
        # Check for explicit success
        if result.get("success"):
            return True
        
        # Check for expected keywords
        if expected and expected.lower() in result_str:
            return True
        
        # Check for errors
        if "error" in result:
            return False
        
        return True
