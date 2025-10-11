#!/usr/bin/env python3
"""
Specific test runner implementations
"""

import time
import json
from typing import Dict, Any


class TestRunners:
    """Collection of specific test implementations"""
    
    def __init__(self, mcp_client, llm_monitor):
        """Initialize test runners
        
        Args:
            mcp_client: MCP client instance
            llm_monitor: LLM monitor instance
        """
        self.mcp = mcp_client
        self.monitor = llm_monitor
    
    def run_extended_autogong(self, duration_seconds: int = 120, 
                             llm_check_interval: int = 10) -> Dict[str, Any]:
        """Extended AutoGong test - LLM-driven monitoring and intervention
        
        Args:
            duration_seconds: Total test duration
            llm_check_interval: How often (in seconds) to consult LLM for decisions
        """
        
        print(f"\n{'='*60}")
        print(f"Extended AutoGong Test ({duration_seconds}s)")
        print(f"{'='*60}\n")
        print(f"?? LLM Active Monitoring: Every {llm_check_interval} seconds")
        print(f"   The AI will analyze game state and make decisions\n")
        
        issues_found = []
        monitoring_data = {
            "cycles": 0,
            "monsters_killed": 0,
            "combat_events": [],
            "hp_changes": [],
            "errors": [],
            "interventions": [],
            "llm_decisions": []
        }
        
        # Enable AutoGong
        print("?? Enabling AutoGong...")
        enable_result = self.mcp.set_automation("autogong", True)
        if not enable_result.get("success"):
            return {
                "test": "extended_autogong",
                "error": "Failed to enable AutoGong",
                "result": enable_result
            }
        print("? AutoGong enabled\n")
        
        start_time = time.time()
        last_state = self.mcp.observe_state()
        last_monsters = set(last_state.get("location", {}).get("monsters", []))
        last_llm_check = start_time
        
        print(f"?? Monitoring for {duration_seconds} seconds...")
        print("   ???  LLM Safety Monitor: ACTIVE - AI controls the test\n")
        
        try:
            while (time.time() - start_time) < duration_seconds:
                time.sleep(5)  # Check every 5 seconds
                
                current_state = self.mcp.observe_state()
                current_monsters = set(current_state.get("location", {}).get("monsters", []))
                
                # Check for issues
                char = current_state.get("character", {})
                loc = current_state.get("location", {})
                combat_info = current_state.get("combat", {})
                automation = current_state.get("automation", {})
                
                elapsed = int(time.time() - start_time)
                
                # Monitor HP changes
                current_hp = char.get("hp", 0)
                last_hp = last_state.get("character", {}).get("hp", 0)
                if current_hp != last_hp:
                    hp_change = {
                        "time": elapsed,
                        "from": last_hp,
                        "to": current_hp,
                        "percent": char.get("hpPercent", 0)
                    }
                    monitoring_data["hp_changes"].append(hp_change)
                    print(f"  [{elapsed}s] HP: {last_hp} ? {current_hp} ({char.get('hpPercent')}%)")
                
                # Monitor monsters
                new_monsters = current_monsters - last_monsters
                dead_monsters = last_monsters - current_monsters
                
                if new_monsters:
                    print(f"  [{elapsed}s] ?? Monster spawned: {', '.join(new_monsters)}")
                    monitoring_data["cycles"] += 1
                
                if dead_monsters:
                    print(f"  [{elapsed}s] ?? Monster killed: {', '.join(dead_monsters)}")
                    monitoring_data["monsters_killed"] += 1
                
                # Monitor combat state
                if combat_info.get("inCombat"):
                    event = {
                        "time": elapsed,
                        "target": combat_info.get("targetedMonster"),
                        "hp": current_hp,
                        "hpPercent": char.get("hpPercent", 0)
                    }
                    monitoring_data["combat_events"].append(event)
                    print(f"  [{elapsed}s] ??  Combat: {event['target']} (HP: {event['hpPercent']}%)")
                
                # Check recent output for errors
                recent_output = self.mcp.get_recent_output(20)
                for line in recent_output:
                    if any(err in line.lower() for err in ["error", "can't afford", "failed", "invalid"]):
                        if line not in [e["message"] for e in monitoring_data["errors"]]:
                            error = {"time": elapsed, "message": line}
                            monitoring_data["errors"].append(error)
                            issue = f"[{elapsed}s] ??  ERROR: {line[:60]}..."
                            issues_found.append(issue)
                            print(f"  {issue}")
                
                # LLM ACTIVE MONITORING - Consult AI every N seconds
                time_since_llm_check = time.time() - last_llm_check
                if time_since_llm_check >= llm_check_interval:
                    print(f"\n  [{elapsed}s] ?? Consulting LLM for situation assessment...")
                    
                    # Ask LLM to analyze current situation
                    llm_decision = self.monitor.monitor_decision(
                        current_state=current_state,
                        recent_output=recent_output,
                        monitoring_data=monitoring_data,
                        elapsed_time=elapsed,
                        duration_seconds=duration_seconds
                    )
                    
                    monitoring_data["llm_decisions"].append({
                        "time": elapsed,
                        "decision": llm_decision
                    })
                    
                    # Handle LLM decision
                    if llm_decision.get("action") == "abort":
                        intervention = {
                            "time": elapsed,
                            "reason": "LLM_ABORT",
                            "action": llm_decision.get("reasoning"),
                            "details": llm_decision
                        }
                        monitoring_data["interventions"].append(intervention)
                        print(f"  ?? LLM Decision: ABORT TEST")
                        print(f"     Reason: {llm_decision.get('reasoning')}")
                        issue = f"[{elapsed}s] ?? LLM ABORT: {llm_decision.get('reasoning')}"
                        issues_found.append(issue)
                        break  # Exit test
                    
                    elif llm_decision.get("action") == "intervene":
                        intervention = {
                            "time": elapsed,
                            "reason": "LLM_INTERVENTION",
                            "action": llm_decision.get("reasoning"),
                            "commands": llm_decision.get("commands", []),
                            "details": llm_decision
                        }
                        monitoring_data["interventions"].append(intervention)
                        print(f"  ???  LLM Decision: INTERVENE")
                        print(f"     Reason: {llm_decision.get('reasoning')}")
                        
                        # Execute LLM's intervention commands
                        for cmd_idx, cmd in enumerate(llm_decision.get("commands", [])):
                            print(f"     Command {cmd_idx + 1}: {cmd}")
                            self.mcp.send_command(cmd)
                            time.sleep(1)  # Give time for command to process
                        
                        # Wait for LLM's specified duration if provided
                        wait_duration = llm_decision.get("wait_for_result", 3)
                        if wait_duration > 0:
                            print(f"     Waiting {wait_duration}s for intervention results...")
                            time.sleep(wait_duration)
                            
                            # Get updated state and send back to LLM
                            post_intervention_state = self.mcp.observe_state()
                            post_intervention_output = self.mcp.get_recent_output(20)
                            
                            print(f"  ?? Sending post-intervention state back to LLM...")
                            followup_decision = self.monitor.intervention_followup(
                                intervention=llm_decision,
                                post_state=post_intervention_state,
                                post_output=post_intervention_output,
                                elapsed_time=elapsed + wait_duration
                            )
                            
                            monitoring_data["llm_decisions"].append({
                                "time": elapsed + wait_duration,
                                "decision": followup_decision,
                                "type": "followup"
                            })
                            
                            print(f"  ?? LLM Followup: {followup_decision.get('assessment')}")
                            
                            # Handle followup decision
                            if followup_decision.get("action") == "abort":
                                print(f"  ?? LLM recommends aborting after intervention")
                                issue = f"[{elapsed}s] ?? LLM ABORT after intervention: {followup_decision.get('reasoning')}"
                                issues_found.append(issue)
                                break
                    
                    elif llm_decision.get("action") == "continue":
                        print(f"  ? LLM Decision: Continue test")
                        print(f"     Assessment: {llm_decision.get('reasoning')}")
                    
                    else:
                        print(f"  ??  Unknown LLM decision: {llm_decision.get('action')}")
                    
                    print()  # Blank line after LLM check
                    last_llm_check = time.time()
                
                last_state = current_state
                last_monsters = current_monsters
                
        except KeyboardInterrupt:
            print("\n\n??  Test interrupted by user")
        
        finally:
            # Always disable AutoGong
            print("\n?? Disabling AutoGong...")
            self.mcp.set_automation("autogong", False)
            print("? AutoGong disabled\n")
        
        # Generate report
        total_time = int(time.time() - start_time)
        
        print(f"\n{'='*60}")
        print(f"Extended Test Complete ({total_time}s)")
        print(f"{'='*60}\n")
        
        print(f"?? Statistics:")
        print(f"   Gong cycles: {monitoring_data['cycles']}")
        print(f"   Monsters killed: {monitoring_data['monsters_killed']}")
        print(f"   Combat events: {len(monitoring_data['combat_events'])}")
        print(f"   HP changes: {len(monitoring_data['hp_changes'])}")
        print(f"   Errors detected: {len(monitoring_data['errors'])}")
        print(f"   LLM decisions: {len(monitoring_data['llm_decisions'])}")
        print(f"   AI Interventions: {len(monitoring_data['interventions'])}")
        print(f"   Issues found: {len(issues_found)}\n")
        
        if monitoring_data["llm_decisions"]:
            print("?? LLM Decision Summary:")
            for decision in monitoring_data["llm_decisions"]:
                decision_type = decision.get("type", "periodic")
                action = decision["decision"].get("action", "unknown")
                print(f"   [{decision['time']}s] {decision_type}: {action}")
            print()
        
        if monitoring_data["interventions"]:
            print("???  AI Interventions:")
            for intervention in monitoring_data["interventions"]:
                print(f"   [{intervention['time']}s] {intervention['reason']}: {intervention['action'][:80]}")
            print()
        
        if issues_found:
            print("??  Issues Detected:")
            for issue in issues_found[-5:]:  # Show last 5
                print(f"   {issue}")
            print()
        
        # UPDATED LOGIC: Test fails if ANY interventions occurred OR if issues found
        # LLM intervention means the automation failed to handle the situation correctly
        test_passed = len(monitoring_data["interventions"]) == 0 and len(issues_found) == 0
        
        if len(monitoring_data["interventions"]) > 0:
            print("? TEST FAILED: LLM had to intervene (automation should handle all situations)")
            print(f"   {len(monitoring_data['interventions'])} intervention(s) required\n")
        elif len(issues_found) > 0:
            print("? TEST FAILED: Issues detected during test")
            print(f"   {len(issues_found)} issue(s) found\n")
        else:
            print("? TEST PASSED: No interventions or issues\n")
        
        result = {
            "test": "extended_autogong",
            "duration": total_time,
            "monitoring_data": monitoring_data,
            "issues": issues_found,
            "passed": test_passed,
            "failure_reason": None if test_passed else (
                f"{len(monitoring_data['interventions'])} LLM intervention(s) required" 
                if len(monitoring_data["interventions"]) > 0 
                else f"{len(issues_found)} issue(s) detected"
            ),
            "stats": {
                "cycles": monitoring_data["cycles"],
                "kills": monitoring_data["monsters_killed"],
                "combat_events": len(monitoring_data["combat_events"]),
                "hp_changes": len(monitoring_data["hp_changes"]),
                "errors": len(monitoring_data["errors"]),
                "llm_decisions": len(monitoring_data["llm_decisions"]),
                "interventions": len(monitoring_data["interventions"])
            }
        }
        
        # If test failed (interventions or issues), get LLM final analysis
        if not test_passed:
            print("?? Asking LLM for failure analysis...")
            
            # Build comprehensive bug description
            bug_desc_parts = []
            if len(monitoring_data["interventions"]) > 0:
                bug_desc_parts.append(f"{len(monitoring_data['interventions'])} LLM intervention(s) required during AutoGong test")
                for intervention in monitoring_data["interventions"]:
                    bug_desc_parts.append(f"  - [{intervention['time']}s] {intervention['reason']}: {intervention['action'][:100]}")
            if len(issues_found) > 0:
                bug_desc_parts.append(f"{len(issues_found)} issue(s) detected during test")
            
            bug_description = "\n".join(bug_desc_parts)
            
            analysis = self.monitor.analyze_bug(
                bug_description=bug_description,
                context={
                    "test_duration": total_time,
                    "interventions": monitoring_data["interventions"],
                    "issues": issues_found,
                    "monitoring_data": {
                        "cycles": monitoring_data["cycles"],
                        "kills": monitoring_data["monsters_killed"],
                        "combat_events": monitoring_data["combat_events"][-10:],
                        "hp_changes": monitoring_data["hp_changes"][-10:],
                        "errors": monitoring_data["errors"],
                        "llm_decisions": monitoring_data["llm_decisions"]
                    }
                }
            )
            result["llm_analysis"] = analysis
            print(f"\n{analysis}\n")
        
        return result
