#!/usr/bin/env python3
"""
Example script showing how to use llm_tester.py programmatically
"""

import sys
from pathlib import Path
from llm_tester import GameTester

def example_basic_test():
    """Example: Run a basic AutoGong test programmatically"""
    print("Example 1: Basic AutoGong Test")
    print("-" * 60)
    
    # Create tester instance
    tester = GameTester(
        mcp_url="http://localhost:3000",
        llm_url=None,  # Auto-detect
        api_key=None   # Use environment variable
    )
    
    # Run test
    result = tester.test_feature(
        "AutoGong",
        """
        Test the AutoGong automation feature:
        1. Verify current game state
        2. Enable AutoGong
        3. Verify AutoGong is enabled in state
        4. Observe game output for gong activity
        """
    )
    
    # Check result
    if result.get("overall_pass"):
        print("? Test passed!")
    else:
        print("? Test failed!")
    
    return result


def example_extended_test():
    """Example: Run extended AutoGong test with AI monitoring"""
    print("\nExample 2: Extended AutoGong Test (60 seconds)")
    print("-" * 60)
    
    tester = GameTester()
    
    # Run extended test with AI monitoring
    result = tester.test_extended_autogong(
        duration_seconds=60,
        llm_check_interval=10
    )
    
    print(f"\nTest completed:")
    print(f"  Duration: {result['duration']}s")
    print(f"  Gong cycles: {result['stats']['cycles']}")
    print(f"  Monsters killed: {result['stats']['kills']}")
    print(f"  LLM decisions: {result['stats']['llm_decisions']}")
    print(f"  AI interventions: {result['stats']['interventions']}")
    
    return result


def example_custom_test():
    """Example: Run a custom test with your own prompt"""
    print("\nExample 3: Custom Test - Combat System")
    print("-" * 60)
    
    tester = GameTester()
    
    # Run custom test
    result = tester.test_with_custom_prompt(
        feature_name="Combat System",
        custom_prompt="""
        Test the combat system:
        1. Check current location for monsters
        2. If no monsters, send an empty command to check status
        3. If aggressive monster present, attack it
        4. Monitor HP during combat
        5. Verify XP gain after combat
        """
    )
    
    if result.get("overall_pass"):
        print("? Custom test passed!")
    else:
        print("? Custom test failed!")
    
    return result


def example_observe_only():
    """Example: Just observe game state without running tests"""
    print("\nExample 4: Observe Game State")
    print("-" * 60)
    
    tester = GameTester()
    
    # Get current state
    state = tester.observe_state()
    
    print(f"Character: {state.get('character', {}).get('name', 'Unknown')}")
    print(f"Location: {state.get('location', {}).get('name', 'Unknown')}")
    print(f"HP: {state.get('character', {}).get('hp', 0)}")
    print(f"In combat: {state.get('combat', {}).get('inCombat', False)}")
    
    # Get recent output
    output = tester.get_recent_output(10)
    print(f"\nRecent output ({len(output)} lines):")
    for line in output[-5:]:
        print(f"  {line}")
    
    return state


def example_send_commands():
    """Example: Send commands to the game"""
    print("\nExample 5: Send Commands")
    print("-" * 60)
    
    tester = GameTester()
    
    # Send a look command
    result = tester.send_command("look")
    print(f"Look command result: {result}")
    
    # Wait for output
    wait_result = tester.wait_for_output("exits", timeout_ms=3000)
    if wait_result.get("found"):
        print(f"? Found exits in output")
    else:
        print(f"? Did not find exits in output")
    
    return result


if __name__ == "__main__":
    print("="*60)
    print("llm_tester.py - Programmatic Usage Examples")
    print("="*60)
    print("\nThese examples show how to use GameTester class directly")
    print("in your own Python scripts.\n")
    
    # Run examples (you can comment out ones you don't want)
    try:
        # Example 1: Basic test
        # example_basic_test()
        
        # Example 2: Extended test with AI monitoring
        # example_extended_test()
        
        # Example 3: Custom test
        # example_custom_test()
        
        # Example 4: Just observe (no test)
        example_observe_only()
        
        # Example 5: Send commands
        # example_send_commands()
        
    except KeyboardInterrupt:
        print("\n\n??  Interrupted by user")
        sys.exit(130)
    except Exception as e:
        print(f"\n? Error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
    
    print("\n" + "="*60)
    print("Examples completed")
    print("="*60)
