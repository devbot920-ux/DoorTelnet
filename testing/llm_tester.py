#!/usr/bin/env python3
"""
LLM-powered game testing agent for DoorTelnet
Main entry point - coordinates all test components
"""

import sys
import time
import json
import argparse
from pathlib import Path

# Try to load .env file if python-dotenv is available
try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).parent / ".env")
except ImportError:
    pass  # python-dotenv not installed, will use manual parsing

# Import our modular components
from llm_client import LLMClient
from mcp_client import MCPClient
from llm_monitors import LLMMonitor
from test_runners import TestRunners
from game_tester import GameTester


def main():
    """Main entry point for the test runner"""
    parser = argparse.ArgumentParser(
        description="LLM-powered game testing agent for DoorTelnet",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Test AutoGong feature
  python llm_tester.py autogong
  
  # Extended AutoGong test (4 minutes)
  python llm_tester.py autogong-extended
  
  # Extended AutoGong test with custom duration (5 minutes)
  python llm_tester.py autogong-extended --duration 300
  
  # Custom test with your own prompt
  python llm_tester.py custom --feature "Combat System" --prompt "Test attacking and defending"
  
  # Use custom MCP or LLM URLs
  python llm_tester.py autogong --mcp-url http://localhost:3000 --llm-url http://localhost:1234
        """
    )
    
    parser.add_argument(
        "test",
        choices=["autogong", "autogong-extended", "custom"],
        help="Test to run: autogong (basic), autogong-extended (AI-monitored), or custom"
    )
    
    parser.add_argument(
        "--mcp-url",
        default="http://localhost:3000",
        help="MCP Bridge URL (default: http://localhost:3000)"
    )
    
    parser.add_argument(
        "--llm-url",
        default=None,
        help="LLM URL (default: auto-detect from API key or use http://localhost:1234)"
    )
    
    parser.add_argument(
        "--api-key",
        default=None,
        help="OpenAI API key (or set OPENAI_API_KEY environment variable)"
    )
    
    parser.add_argument(
        "--duration",
        type=int,
        default=240,
        help="Duration in seconds for extended test (default: 240)"
    )
    
    parser.add_argument(
        "--llm-interval",
        type=int,
        default=10,
        help="LLM check interval in seconds for extended test (default: 10)"
    )
    
    parser.add_argument(
        "--feature",
        default="Custom Feature",
        help="Feature name for custom test"
    )
    
    parser.add_argument(
        "--prompt",
        default=None,
        help="Custom test prompt (required for custom test)"
    )
    
    parser.add_argument(
        "--output",
        default=None,
        help="Output file path (default: auto-generated in testing/ directory)"
    )
    
    args = parser.parse_args()
    
    # Validate custom test requirements
    if args.test == "custom" and not args.prompt:
        parser.error("--prompt is required for custom test")
    
    # Initialize components
    print(f"\n{'='*60}")
    print(f"DoorTelnet LLM Test Runner")
    print(f"{'='*60}\n")
    
    # Create LLM clients
    # gpt-5-mini for monitoring (fast, frequent checks)
    llm_monitor_client = LLMClient(llm_url=args.llm_url, api_key=args.api_key, model_override="gpt-5-mini")
    
    # GPT-5 for summaries and test generation (more capable, less frequent)
    llm_summary_client = LLMClient(llm_url=args.llm_url, api_key=args.api_key, model_override="gpt-5-codex")
    
    mcp_client = MCPClient(mcp_url=args.mcp_url)
    
    # Create monitor (uses fast model for frequent checks)
    llm_monitor = LLMMonitor(llm_monitor_client, mcp_client)
    
    # Create test runners with summary client for final analysis
    test_runners = TestRunners(mcp_client, llm_monitor, llm_summary_client)
    
    # Create main tester (uses GPT-5 for test generation)
    tester = GameTester(mcp_client, llm_summary_client, llm_monitor, test_runners)
    
    # Run the selected test
    result = None
    timestamp = int(time.time())
    
    try:
        if args.test == "autogong":
            print("Running basic AutoGong test...\n")
            result = tester.test_feature(
                "AutoGong",
                """
                Test the AutoGong automation feature (CONTINUOUS COMBAT MODE):
                
                CRITICAL UNDERSTANDING:
                - AutoGong implements its own combat logic using shared attack methods
                - Maintains CONTINUOUS COMBAT - no idle time when AT/AC = 0
                
                Expected Behavior:
                1. Rings gong ("r g") immediately when AT/AC = 0 (~1.5s interval)
                2. Attacks all aggressive monsters continuously
                3. Loots gold/silver after kills
                4. Immediately rings gong again (no resting/idle periods)
                5. Repeats until HP < threshold or out of gold
                
                Test Steps:
                1. Verify current game state (character, location, stats)
                2. Check current automation state (should show autoGong: false initially)
                3. Enable AutoGong using set_automation
                4. Verify AutoGong is enabled (automation.autoGong should be true)
                5. Verify AutoAttack is enabled (automation.autoAttack should be true)
                6. Observe game output for gong activity ("r g" command)
                7. Observe immediate monster attacks after gong
                8. Verify NO extended idle periods (AT/AC = 0 for > 2 seconds)
                9. Monitor several gong cycles for consistency
                10. Disable AutoGong
                11. Verify AutoGong is disabled
                12. Verify AutoAttack is disabled
                
                Key Verification Points:
                - ✓ automation.autoGong changes to true
                - ✓ automation.autoattack changes to true
                - ✓ Gong rings every ~1.5 seconds when timers ready
                - ✓ Continuous combat maintained (no idle time)
                - ✓ Immediate attack response to summoned monsters
                - ✓ System respects HP thresholds
                """
            )
            output_filename = args.output or f"test_results_autogong_{timestamp}.json"
        
        elif args.test == "autogong-extended":
            print(f"Running extended AutoGong test ({args.duration}s with AI monitoring)...\n")
            result = test_runners.run_extended_autogong(
                duration_seconds=args.duration,
                llm_check_interval=args.llm_interval
            )
            output_filename = args.output or f"test_results_autogong-extended_{timestamp}.json"
        
        elif args.test == "custom":
            print(f"Running custom test: {args.feature}...\n")
            result = tester.test_with_custom_prompt(
                feature_name=args.feature,
                custom_prompt=args.prompt
            )
            feature_slug = args.feature.lower().replace(" ", "-")
            output_filename = args.output or f"test_results_{feature_slug}_{timestamp}.json"
        
        else:
            print(f"Error: Unknown test type '{args.test}'")
            return 1
        
        # Save results
        output_path = Path(__file__).parent / output_filename
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=2)
        
        print(f"\n{'='*60}")
        print(f"Results saved to: {output_filename}")
        print(f"{'='*60}\n")
        
        # If test failed, generate bug analysis using GPT-5
        if not result.get("passed", result.get("overall_pass", False)):
            print("Test failed. Generating bug analysis with GPT-5...\n")
            
            # Create a dedicated bug analysis monitor with GPT-5
            bug_analysis_monitor = LLMMonitor(llm_summary_client, mcp_client)
            
            analysis = bug_analysis_monitor.analyze_bug(
                bug_description=f"Test '{args.test}' failed",
                context=result
            )
            
            analysis_filename = f"bug_analysis_{args.test}_{timestamp}.md"
            analysis_path = Path(__file__).parent / analysis_filename
            
            with open(analysis_path, "w", encoding="utf-8") as f:
                f.write(f"# Bug Analysis: {args.test}\n\n")
                f.write(f"Generated: {time.strftime('%Y-%m-%d %H:%M:%S')}\n\n")
                f.write(analysis)
            
            print(f"Bug analysis saved to: {analysis_filename}\n")
            print(analysis)
            
            return 1  # Exit with error code
        
        return 0  # Success
    
    except KeyboardInterrupt:
        print("\n\n⚠️  Test interrupted by user")
        return 130  # Standard exit code for SIGINT
    
    except Exception as e:
        print(f"\n❌ Error running test: {e}")
        import traceback
        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
