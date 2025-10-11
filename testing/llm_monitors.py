#!/usr/bin/env python3
"""
LLM monitoring and intervention logic for extended testing
"""

import json
from typing import Dict, List, Any
from pathlib import Path
from datetime import datetime


class LLMMonitor:
    """Handles LLM-based monitoring and decision making during tests"""
    
    def __init__(self, llm_client, mcp_client, game_context: str = None):
        """Initialize LLM monitor
        
        Args:
            llm_client: LLM client instance
            mcp_client: MCP client instance
            game_context: Optional game context from RoseGamePlay.md
        """
        self.llm = llm_client
        self.mcp = mcp_client
        self.game_context = game_context
        
        # Create prompts directory if it doesn't exist
        self.prompts_dir = Path(__file__).parent / "prompts_output"
        self.prompts_dir.mkdir(exist_ok=True)
    
    def _save_prompt_to_file(self, prompt: str, prompt_type: str, context_info: str = "") -> str:
        """Save prompt to a JSON file
        
        Args:
            prompt: The prompt text to save
            prompt_type: Type of prompt (monitor, verification, bug_analysis, etc.)
            context_info: Additional context for filename
            
        Returns:
            Path to the saved file
        """
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        safe_context = "".join(c if c.isalnum() or c in ('-', '_') else '_' for c in context_info) if context_info else ""
        filename = f"prompt_monitor_{prompt_type}_{safe_context}_{timestamp}.json" if safe_context else f"prompt_monitor_{prompt_type}_{timestamp}.json"
        filepath = self.prompts_dir / filename
        
        prompt_data = {
            "timestamp": datetime.now().isoformat(),
            "prompt_type": f"monitor_{prompt_type}",
            "context_info": context_info,
            "prompt_text": prompt,
            "prompt_length": len(prompt),
            "line_count": prompt.count('\n') + 1
        }
        
        with open(filepath, 'w', encoding='utf-8') as f:
            json.dump(prompt_data, f, indent=2, ensure_ascii=False)
        
        print(f"  💾 Monitor prompt saved to: {filename}")
        return str(filepath)
    
    def monitor_decision(self, current_state: Dict, recent_output: List[str], 
                        monitoring_data: Dict, elapsed_time: int, 
                        duration_seconds: int) -> Dict[str, Any]:
        """Ask LLM to analyze current situation and decide on action
        
        The LLM sees everything: game state, recent output, test progress
        It can decide to: continue, intervene, or abort
        """
        
        prompt = f"""You are actively monitoring an AutoGong test in "The Rose" MUD game.

TIME: {elapsed_time}s elapsed of {duration_seconds}s total

CURRENT GAME STATE note(AutoAttack is always off when doing AutoGong):
{json.dumps(current_state, indent=2)}

RECENT GAME OUTPUT (last 20 lines):
{chr(10).join(recent_output)}

TEST STATISTICS SO FAR:
- Gong cycles: {monitoring_data['cycles']}
- Monsters killed: {monitoring_data['monsters_killed']}
- Combat events: {len(monitoring_data['combat_events'])}
- HP changes: {len(monitoring_data['hp_changes'])}
- Errors: {len(monitoring_data['errors'])}

RECENT HP CHANGES:
{json.dumps(monitoring_data['hp_changes'][-5:], indent=2) if monitoring_data['hp_changes'] else "None yet"}

RECENT ERRORS:
{json.dumps(monitoring_data['errors'], indent=2) if monitoring_data['errors'] else "None"}

YOUR TASK:
Analyze the current situation and decide what to do.

DECISION OPTIONS:
1. "continue" - Everything looks good, continue testing
2. "intervene" - Send commands to fix a problem, then continue
3. "abort" - Critical issue detected, stop test immediately

WHEN TO INTERVENE:
- HP < 30% and dropping
- Aggressive monster attacking but not being fought
- AutoGong seems stuck or disabled
- Gold running low (might fail soon)
- Character not attacking/looting properly

WHEN TO ABORT:
- HP < 20% (critical danger)
- AutoGong failed completely
- Character appears dead or disconnected
- Unrecoverable error state

RESPOND WITH ONLY VALID JSON:
{{
  "action": "continue" | "intervene" | "abort",
  "reasoning": "Brief explanation of why you chose this action",
  "commands": ["stop", "look"],  // If intervene, what commands to send
  "wait_for_result": 3,  // If intervene, how many seconds to wait before checking result
  "assessment": "Current situation looks safe/dangerous/critical"
}}

Examples:

SAFE SITUATION:
{{
  "action": "continue",
  "reasoning": "HP at 85%, combat proceeding normally, no errors detected",
  "assessment": "All systems nominal"
}}

INTERVENTION NEEDED:
{{
  "action": "intervene",
  "reasoning": "HP dropped to 28%, need to stop combat and assess",
  "commands": ["stop"],
  "wait_for_result": 5,
  "assessment": "HP critically low, intervening"
}}
INTERVENTION NEEDED:
{{
  "action": "intervene",
  "reasoning": "HP is fine, but we are not attacking a monster that should be",
  "commands": ["attack orc"],
  "wait_for_result": 5,
  "assessment": "Automation failed to attack aggressive monster."
}}

ABORT NEEDED:
{{
  "action": "abort",
  "reasoning": "HP at 12%, character will die if we continue",
  "assessment": "Critical danger, aborting test"
}}

Analyze the situation and respond with JSON only.
"""
        
        # Save prompt before sending
        self._save_prompt_to_file(prompt, "decision", f"elapsed_{elapsed_time}s")
        
        try:
            llm_response = self.llm.call(prompt)
            
            # Parse JSON response
            if "```json" in llm_response:
                json_start = llm_response.find("```json") + 7
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            elif "```" in llm_response:
                json_start = llm_response.find("```") + 3
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            
            decision = json.loads(llm_response)
            return decision
            
        except Exception as e:
            # If LLM fails, default to continue but log the error
            return {
                "action": "continue",
                "reasoning": f"LLM error: {str(e)}, continuing cautiously",
                "assessment": "LLM consultation failed"
            }
    
    def intervention_followup(self, intervention: Dict, post_state: Dict, 
                             post_output: List[str], elapsed_time: int) -> Dict[str, Any]:
        """Ask LLM to assess the results of its intervention"""
        
        prompt = f"""You previously intervened in an AutoGong test. Now assess the results.

YOUR PREVIOUS INTERVENTION:
{json.dumps(intervention, indent=2)}

TIME: {elapsed_time}s

POST-INTERVENTION GAME STATE:
{json.dumps(post_state, indent=2)}

POST-INTERVENTION OUTPUT (last 20 lines):
{chr(10).join(post_output)}

YOUR TASK:
Did your intervention work? Should we continue the test?

RESPOND WITH ONLY VALID JSON:
{{
  "action": "continue" | "abort",
  "reasoning": "Did intervention work? What happened?",
  "assessment": "Intervention successful/failed",
  "next_concern": "What to watch for next" or null
}}

Examples:

INTERVENTION SUCCESSFUL:
{{
  "action": "continue",
  "reasoning": "Stop command worked, HP stabilized at 65%, combat ended safely",
  "assessment": "Intervention successful, safe to continue",
  "next_concern": "Monitor HP during next combat"
}}

INTERVENTION FAILED:
{{
  "action": "abort",
  "reasoning": "HP still dropping despite stop command, now at 15%, character in danger",
  "assessment": "Intervention failed, aborting for safety",
  "next_concern": null
}}

Respond with JSON only.
"""
        
        # Save prompt before sending
        self._save_prompt_to_file(prompt, "followup", f"elapsed_{elapsed_time}s")
        
        try:
            llm_response = self.llm.call(prompt)
            
            # Parse JSON response
            if "```json" in llm_response:
                json_start = llm_response.find("```json") + 7
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            elif "```" in llm_response:
                json_start = llm_response.find("```") + 3
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            
            decision = json.loads(llm_response)
            return decision
            
        except Exception as e:
            # If LLM fails, default to continue
            return {
                "action": "continue",
                "reasoning": f"LLM error: {str(e)}, continuing",
                "assessment": "Unable to assess intervention results"
            }
    
    def verify_output(self, action: str, params: Dict, result: Dict, expected: str) -> Dict[str, Any]:
        """Ask LLM to verify if the actual output matches expectations"""
        
        # Get recent game output for context
        game_output = self.mcp.get_recent_output(15)
        
        # Build minimal game context for verification
        game_context_hint = ""
        if self.game_context:
            game_context_hint = "\n\nNote: This is 'The Rose' MUD game. Consider game-specific mechanics when verifying.\n"
        
        verification_prompt = f"""You are verifying the outcome of a test step in a MUD game.{game_context_hint}
Action taken: {action}
Parameters: {json.dumps(params, indent=2)}
Expected outcome: {expected}

Actual result from MCP:
{json.dumps(result, indent=2)}

Recent game output (last 15 lines):
{chr(10).join(game_output)}

Analyze whether the actual outcome matches the expected outcome.
Consider:
1. Did the MCP tool succeed?
2. Does the game output show the expected behavior?
3. Are there any error messages or unexpected results?

Respond with ONLY valid JSON:
{{
  "passed": true or false,
  "analysis": "Brief explanation of why it passed or failed",
  "game_evidence": "Relevant line(s) from game output that support your conclusion"
}}
"""
        
        # Save prompt before sending
        self._save_prompt_to_file(verification_prompt, "verification", action)
        
        try:
            llm_response = self.llm.call(verification_prompt)
            
            # Parse response
            if "```json" in llm_response:
                json_start = llm_response.find("```json") + 7
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            elif "```" in llm_response:
                json_start = llm_response.find("```") + 3
                json_end = llm_response.find("```", json_start)
                llm_response = llm_response[json_start:json_end].strip()
            
            verification = json.loads(llm_response)
            return verification
            
        except Exception as e:
            return {
                "passed": False,
                "analysis": f"LLM verification failed: {str(e)}",
                "game_evidence": "N/A"
            }
    
    def analyze_bug(self, bug_description: str, context: Dict) -> str:
        """Have LLM analyze a bug and suggest a fix"""
        
        prompt = f"""You are debugging a MUD game client written in C# (.NET 8, WPF).

Bug description:
{bug_description}

Test context and results:
{json.dumps(context, indent=2)}

Analyze this bug and provide:
1. Root cause analysis - what is the likely cause of the failure?
2. Based on observations from the game itself, what is the most likely fix?
3. A detailed, but concise GitHub Copilot prompt that would fix this bug

The application has these main components, you can reference them, but dont assume the location of various classes/methods:
- DoorTelnet.Wpf: WPF UI layer with ViewModels and Services
- DoorTelnet.Core: Core game logic (Telnet, Automation, Combat, Navigation, World tracking)
- Services: AutomationFeatureService, NavigationFeatureService, GameApiService
- Trackers: StatsTracker, RoomTracker, CombatTracker
- TelnetClient: Handles game connection and command sending

Format your response as:
## Root Cause
[Your analysis]

## GitHub Copilot Prompt
```
[Detailed prompt that can be pasted into GitHub Copilot to fix the bug]
```
"""
        
        print("\n" + "="*60)
        print("Analyzing Bug...")
        print("="*60 + "\n")
        
        # Save prompt before sending
        safe_desc = "".join(c if c.isalnum() or c in ('-', '_') else '_' for c in bug_description[:30])
        self._save_prompt_to_file(prompt, "bug_analysis", safe_desc)
        
        analysis = self.llm.call(prompt)
        
        return analysis
