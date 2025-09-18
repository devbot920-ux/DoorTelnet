using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// Test to debug line processing in TelnetClient
class TestLineProcessing
{
    static void Main()
    {
        Console.WriteLine("Testing line processing logic from TelnetClient...");
        
        // Simulate the line building logic from TelnetClient
        var currentLine = new StringBuilder();
        var receivedLines = new List<string>();
        
        // Simulate receiving "A rat is summoned for combat!" with various line endings
        var testPayloads = new List<byte[]>
        {
            // Test 1: Simple case with \r\n
            Encoding.ASCII.GetBytes("A rat is summoned for combat!\r\n"),
            
            // Test 2: Just \n
            Encoding.ASCII.GetBytes("A rat is summoned for combat!\n"),
            
            // Test 3: Mixed with other content and ANSI sequences
            Encoding.ASCII.GetBytes("\x1B[32mA rat is summoned for combat!\x1B[0m\r\n"),
            
            // Test 4: Fragmented across multiple packets
            Encoding.ASCII.GetBytes("A rat is summon"),
            Encoding.ASCII.GetBytes("ed for combat!\n"),
            
            // Test 5: With stats line before
            Encoding.ASCII.GetBytes("[Hp=100/Mp=50/Mv=200] summon rat\r\nA rat is summoned for combat!\r\n")
        };
        
        Console.WriteLine("\nProcessing test payloads...");
        
        for (int testNum = 0; testNum < testPayloads.Count; testNum++)
        {
            Console.WriteLine($"\n--- Test {testNum + 1} ---");
            var payload = testPayloads[testNum];
            
            Console.WriteLine($"Input bytes: {string.Join(" ", payload.Select(b => b.ToString("X2")))}");
            Console.WriteLine($"Input string: '{Encoding.ASCII.GetString(payload).Replace('\r', '\\').Replace('\n', '|')}'");
            
            // Simulate TelnetClient line processing logic
            foreach (var ch in payload)
            {
                char c = (char)ch;
                if (c == '\n')
                {
                    var line = currentLine.ToString();
                    currentLine.Clear();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        receivedLines.Add(line);
                        Console.WriteLine($"  LINE RECEIVED: '{line}'");
                        
                        // Check if this would match the summoning pattern
                        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(?:A|An)\s+(.+?)\s+is\s+summoned\s+for\s+combat!?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            Console.WriteLine($"  ? MATCHES SUMMONING PATTERN!");
                        }
                        else
                        {
                            Console.WriteLine($"  ? Does not match summoning pattern");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"  Empty line ignored");
                    }
                }
                else if (c != '\r' && c != 0x1B)
                {
                    currentLine.Append(c);
                }
                // Note: \r and ESC (0x1B) are ignored in TelnetClient
            }
            
            if (currentLine.Length > 0)
            {
                Console.WriteLine($"  Partial line in buffer: '{currentLine}'");
            }
        }
        
        Console.WriteLine($"\nTotal lines received: {receivedLines.Count}");
        foreach (var line in receivedLines)
        {
            Console.WriteLine($"  '{line}'");
        }
        
        // Test the summoning regex specifically
        Console.WriteLine("\n--- Testing summoning regex directly ---");
        var testLines = new[]
        {
            "A rat is summoned for combat!",
            "An orc is summoned for combat!",
            "A mighty dragon is summoned for combat!",
            "The goblin is summoned for combat!", // This should NOT match (starts with "The")
            "A rat is summoned for combat", // No exclamation
            "A rat was summoned for combat!", // "was" instead of "is"
        };
        
        var summonPattern = @"^(?:A|An)\s+(.+?)\s+is\s+summoned\s+for\s+combat!?\s*$";
        
        foreach (var testLine in testLines)
        {
            var match = System.Text.RegularExpressions.Regex.IsMatch(testLine, summonPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Console.WriteLine($"'{testLine}' -> {(match ? "? MATCH" : "? NO MATCH")}");
        }
    }
}