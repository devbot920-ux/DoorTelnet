#!/usr/bin/env python3
"""
MCP Bridge client for communicating with the DoorTelnet MCP Bridge
"""

import requests
from typing import Dict, List, Any


class MCPClient:
    """Handles communication with MCP Bridge server"""
    
    def __init__(self, mcp_url: str = "http://localhost:3000"):
        """Initialize MCP client
        
        Args:
            mcp_url: URL of the MCP Bridge server
        """
        self.mcp_url = mcp_url
    
    def call_tool(self, method: str, params: Dict = None) -> Dict[str, Any]:
        """Call an MCP tool
        
        Args:
            method: MCP tool method name
            params: Parameters for the tool
            
        Returns:
            Tool result dictionary
        """
        try:
            response = requests.post(
                self.mcp_url,
                json={"method": method, "params": params or {}},
                timeout=10
            )
            response.raise_for_status()
            return response.json()["result"]
        except Exception as e:
            return {"error": str(e)}
    
    def observe_state(self) -> Dict[str, Any]:
        """Get current game state"""
        return self.call_tool("observe_game_state")
    
    def send_command(self, command: str) -> Dict[str, Any]:
        """Send a game command"""
        return self.call_tool("send_command", {"command": command})
    
    def wait_for_output(self, pattern: str, timeout_ms: int = 5000) -> Dict[str, Any]:
        """Wait for specific output"""
        return self.call_tool("wait_for_output", {
            "pattern": pattern,
            "timeout_ms": timeout_ms
        })
    
    def get_recent_output(self, count: int = 20) -> List[str]:
        """Get recent game output lines"""
        result = self.call_tool("get_recent_output", {"count": count})
        return result.get("lines", [])
    
    def set_automation(self, feature: str, enabled: bool) -> Dict[str, Any]:
        """Enable/disable automation feature"""
        return self.call_tool("set_automation", {
            "feature": feature,
            "enabled": enabled
        })
    
    def navigate_to(self, destination: str) -> Dict[str, Any]:
        """Navigate to a destination"""
        return self.call_tool("navigate_to", {"destination": destination})
    
    def verify_stat_change(self, stat: str, expected_change: str) -> Dict[str, Any]:
        """Verify a stat change"""
        return self.call_tool("verify_stat_change", {
            "stat": stat,
            "expected_change": expected_change
        })
    
    def verify_room_change(self) -> Dict[str, Any]:
        """Verify room changed"""
        return self.call_tool("verify_room_change")
    
    def verify_combat_initiated(self) -> Dict[str, Any]:
        """Verify combat was initiated"""
        return self.call_tool("verify_combat_initiated")
