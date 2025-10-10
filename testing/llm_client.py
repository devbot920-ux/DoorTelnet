#!/usr/bin/env python3
"""
LLM client for communicating with OpenAI or local LLM instances
"""

import requests
import time
import os
from typing import Optional
from pathlib import Path

# Try to import OpenAI SDK (optional)
try:
    from openai import OpenAI
    OPENAI_SDK_AVAILABLE = True
except ImportError:
    OPENAI_SDK_AVAILABLE = False


class LLMClient:
    """Handles communication with LLM services (OpenAI or local)"""
    
    def __init__(self, llm_url: str = None, api_key: str = None):
        """Initialize LLM client
        
        Args:
            llm_url: Custom LLM URL, or None to auto-detect
            api_key: API key for OpenAI, or None to auto-detect
        """
        # Determine LLM configuration
        if llm_url is None:
            # Check for API key to use OpenAI
            self.api_key = api_key or self._load_api_key()
            if self.api_key:
                self.llm_url = "https://api.openai.com"
                self.llm_provider = "openai"
                self.model = "gpt-5-mini"  # Efficient and cost-effective
                
                # Initialize OpenAI client if SDK available
                if OPENAI_SDK_AVAILABLE:
                    self.openai_client = OpenAI(api_key=self.api_key)
                    print(f"? Using OpenAI gpt-5-mini with SDK (API key found)")
                else:
                    self.openai_client = None
                    print(f"? Using OpenAI gpt-5-mini via requests (API key found)")
                
                print(f"  Note: gpt-5-mini is optimized for speed and cost")
            else:
                # Fallback to local LM Studio
                self.llm_url = "http://localhost:1234"
                self.llm_provider = "local"
                self.model = "gpt-oss-20b"
                self.api_key = None
                self.openai_client = None
                print(f"??  Using local LLM at {self.llm_url} (no API key found)")
                print(f"   Tip: Set OPENAI_API_KEY for faster, more reliable tests")
        else:
            # Custom LLM URL provided
            self.llm_url = llm_url
            self.llm_provider = "custom"
            self.model = "custom-model"
            self.api_key = api_key
            self.openai_client = None
        
        self.last_llm_call = 0  # Track last API call time for rate limiting
    
    def _load_api_key(self) -> Optional[str]:
        """Load API key from secure location (environment variable or .env file)"""
        # Priority 1: Environment variable (set by python-dotenv or system)
        api_key = os.environ.get("OPENAI_API_KEY")
        if api_key:
            return api_key
        
        # Priority 2: Manual .env file parsing (fallback if python-dotenv not installed)
        env_file = Path(__file__).parent / ".env"
        if env_file.exists():
            try:
                with open(env_file, "r") as f:
                    for line in f:
                        line = line.strip()
                        if line and not line.startswith("#") and "=" in line:
                            key, value = line.split("=", 1)
                            if key.strip() == "OPENAI_API_KEY":
                                return value.strip().strip('"').strip("'")
            except Exception as e:
                print(f"Warning: Could not read .env file: {e}")
        
        return None
    
    def call(self, prompt: str) -> str:
        """Call LLM (OpenAI or local)"""
        try:
            # Rate limiting for OpenAI free tier (3 requests/min)
            if self.llm_provider == "openai123":
                elapsed = time.time() - self.last_llm_call
                if elapsed < 20:  # Wait 20 seconds between calls
                    wait_time = 20 - elapsed
                    print(f"  ??  Rate limiting: waiting {wait_time:.1f}s...")
                    time.sleep(wait_time)
                self.last_llm_call = time.time()
            
            if self.llm_provider == "openai":
                return self._call_openai(prompt)
            else:
                return self._call_local_llm(prompt)
        except Exception as e:
            return f"LLM Error: {str(e)}"
    
    def _call_openai(self, prompt: str) -> str:
        """Call OpenAI API using Responses API"""
        # Use SDK if available (better error handling)
        if self.openai_client:
            try:
                # Use Responses API (not chat.completions)
                response = self.openai_client.responses.create(
                    model=self.model,
                    input=prompt
                )
                # Responses API returns output_text directly
                return response.output_text
            except Exception as e:
                # Handle rate limiting specially
                if "429" in str(e) or "rate_limit" in str(e).lower():
                    print(f"  ??  Rate limit hit!")
                    print(f"     Waiting 60 seconds before retry...")
                    time.sleep(60)
                    # Retry once
                    response = self.openai_client.responses.create(
                        model=self.model,
                        input=prompt
                    )
                    return response.output_text
                raise
        
        # Fallback to requests (if SDK not available)
        response = requests.post(
            f"{self.llm_url}/v1/responses",
            headers={
                "Authorization": f"Bearer {self.api_key}",
                "Content-Type": "application/json"
            },
            json={
                "model": self.model,
                "input": prompt
            },
            timeout=30
        )
        
        # Handle rate limiting
        if response.status_code == 429:
            print(f"  ??  Rate limit hit!")
            print(f"     Waiting 60 seconds before retry...")
            time.sleep(60)
            response = requests.post(
                f"{self.llm_url}/v1/responses",
                headers={
                    "Authorization": f"Bearer {self.api_key}",
                    "Content-Type": "application/json"
                },
                json={
                    "model": self.model,
                    "input": prompt
                },
                timeout=30
            )
        
        response.raise_for_status()
        result = response.json()
        # Responses API returns output_text
        return result.get("output_text", result.get("output", ""))
    
    def _call_local_llm(self, prompt: str) -> str:
        """Call local LLM (LM Studio)"""
        response = requests.post(
            f"{self.llm_url}/v1/chat/completions",
            json={
                "model": self.model,
                "messages": [{"role": "user", "content": prompt}],
                "max_tokens": 2000,
                "temperature": 0.7,
                "stream": False
            },
            timeout=60  # Local models can be slower
        )
        response.raise_for_status()
        result = response.json()
        
        # Handle different response formats
        message = result["choices"][0]["message"]
        
        # Some models return reasoning separately
        if "reasoning" in message and message.get("content", "").strip() == "":
            return message["reasoning"]
        
        return message["content"]
