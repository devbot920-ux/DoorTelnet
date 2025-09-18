using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Test to verify the TelnetClient ANSI stripping fix
class TestAnsiStrippingFix
{
    static void Main()
    {
        Console.WriteLine("Testing ANSI escape sequence stripping for summoning events...");
        
        // Simulate the new ProcessLinesFromPayload logic
        var receivedLines = new List<string>();
        
        // Test various scenarios
        var testCases = new List<(string description, byte[] payload, string expectedLine)>
        {
            ("Simple summoning without ANSI", 
             Encoding.ASCII.GetBytes("A rat is summoned for combat!\r\n"),
             "A rat is summoned for combat!"),
             
            ("Summoning with color codes", 
             Encoding.ASCII.GetBytes("\x1B[32mA rat is summoned for combat!\x1B[0m\r\n"),
             "A rat is summoned for combat!"),
             
            ("Summoning with multiple ANSI sequences", 
             Encoding.ASCII.GetBytes("\x1B[1;31mA \x1B[33mdragon\x1B[0m is summoned for combat!\x1B[0m\r\n"),
             "A dragon is summoned for combat!"),
             
            ("Summoning mixed with cursor positioning", 
             Encoding.ASCII.GetBytes("\x1B[H\x1B[2JAn orc is summoned for combat!\x1B[0m\r\n"),
             "An orc is summoned for combat!"),
        };
        
        foreach (var (description, payload, expectedLine) in testCases)
        {
            Console.WriteLine($"\n--- {description} ---");
            Console.WriteLine($"Input bytes: {string.Join(" ", payload.Select(b => b.ToString("X2")))}");
            
            var actualLine = ProcessPayloadForLines(payload);
            Console.WriteLine($"Expected: '{expectedLine}'");
            Console.WriteLine($"Actual:   '{actualLine}'");
            
            bool matches = actualLine == expectedLine;
            Console.WriteLine($"Result: {(matches ? "? PASS" : "? FAIL")}");
            
            if (matches)
            {
                // Test regex matching
                var summonPattern = @"^(?:A|An)\s+(.+?)\s+is\s+summoned\s+for\s+combat!?\s*$";
                var regexMatch = Regex.IsMatch(actualLine, summonPattern, RegexOptions.IgnoreCase);
                Console.WriteLine($"Regex match: {(regexMatch ? "? MATCHES" : "? NO MATCH")}");
                
                if (regexMatch)
                {
                    var match = Regex.Match(actualLine, summonPattern, RegexOptions.IgnoreCase);
                    var creatureName = match.Groups[1].Value.Trim();
                    Console.WriteLine($"Extracted creature: '{creatureName}'");
                }
            }
        }
        
        Console.WriteLine("\n=== Testing edge cases ===");
        
        // Test fragmented ANSI sequences
        var fragmentedPayload = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("\x1B[3"),
            Encoding.ASCII.GetBytes("2mA rat is summon"),
            Encoding.ASCII.GetBytes("ed for combat!\x1B["),
            Encoding.ASCII.GetBytes("0m\r\n")
        };
        
        Console.WriteLine("Testing fragmented ANSI sequence...");
        string fragmentedResult = "";
        foreach (var fragment in fragmentedPayload)
        {
            var partialResult = ProcessPayloadForLines(fragment, preservePartialLine: true);
            fragmentedResult += partialResult;
        }
        
        Console.WriteLine($"Fragmented result: '{fragmentedResult}'");
        // Note: This test may not work perfectly due to state management complexity
        // The real implementation would need to maintain ANSI parsing state across calls
    }
    
    /// <summary>
    /// Simulate the new ProcessLinesFromPayload logic
    /// </summary>
    static string ProcessPayloadForLines(byte[] payload, bool preservePartialLine = false)
    {
        var currentLine = new StringBuilder();
        bool inAnsiSequence = false;
        var ansiBuffer = new List<byte>();
        var completedLines = new List<string>();
        
        foreach (var ch in payload)
        {
            char c = (char)ch;
            
            // Handle ANSI escape sequences
            if (c == '\x1B') // ESC character
            {
                inAnsiSequence = true;
                ansiBuffer.Clear();
                continue;
            }
            
            if (inAnsiSequence)
            {
                ansiBuffer.Add(ch);
                
                // ANSI sequence typically ends with a letter (A-Z, a-z) or certain symbols
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '@')
                {
                    // End of ANSI sequence - don't add anything to the line
                    inAnsiSequence = false;
                    ansiBuffer.Clear();
                }
                // If the sequence gets too long, abandon it
                else if (ansiBuffer.Count > 20)
                {
                    inAnsiSequence = false;
                    ansiBuffer.Clear();
                }
                continue;
            }
            
            // Regular character processing for line building
            if (c == '\n')
            {
                var line = currentLine.ToString();
                currentLine.Clear();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    completedLines.Add(line);
                }
            }
            else if (c != '\r') // Skip carriage returns
            {
                currentLine.Append(c);
            }
        }
        
        // Return the first completed line, or partial line if requested
        if (completedLines.Count > 0)
        {
            return completedLines[0];
        }
        else if (preservePartialLine)
        {
            return currentLine.ToString();
        }
        else
        {
            return "";
        }
    }
}