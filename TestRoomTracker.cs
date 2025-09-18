using DoorTelnet.Core.World;
using System.Diagnostics;

public class RoomTrackerTest
{
    public static void RunSummoningTest()
    {
        Debug.WriteLine("?? === ROOM TRACKER SUMMONING TEST ===");
        
        var tracker = new RoomTracker();
        
        // Step 1: Simulate being in a room initially
        var initialRoomText = @"
[Hp=100/Mp=50/Mv=200] n
Forest Path
This is a winding path through the forest.
A goblin is here.
Exits: North, South
";
        
        Debug.WriteLine("?? Setting up initial room...");
        var updated = tracker.TryUpdateRoom("testuser", "testchar", initialRoomText);
        Debug.WriteLine($"Initial room setup: {updated}");
        Debug.WriteLine($"Current room monsters: {tracker.CurrentRoom?.Monsters.Count ?? 0}");
        
        // Step 2: Simulate summoning
        var summonText = @"
[Hp=90/Mp=40/Mv=200] cast summon
You begin casting summon.
A gremlin is summoned for combat!
The gremlin snarls at you menacingly.
";
        
        Debug.WriteLine("\n?? Simulating summoning...");
        updated = tracker.TryUpdateRoom("testuser", "testchar", summonText);
        Debug.WriteLine($"Summoning update: {updated}");
        Debug.WriteLine($"Current room monsters after summoning: {tracker.CurrentRoom?.Monsters.Count ?? 0}");
        
        if (tracker.CurrentRoom != null)
        {
            Debug.WriteLine("?? Final monster list:");
            foreach (var monster in tracker.CurrentRoom.Monsters)
            {
                Debug.WriteLine($"  ?? {monster.Name} ({monster.Disposition})");
            }
        }
        
        // Step 3: Test room grid data (what the UI would see)
        Debug.WriteLine("\n?? Testing room grid data (UI view):");
        var gridData = tracker.GetRoomGrid("testuser", "testchar");
        if (gridData.Center != null)
        {
            Debug.WriteLine($"Center room: {gridData.Center.Name}");
            Debug.WriteLine($"Monster count: {gridData.Center.MonsterCount}");
            Debug.WriteLine($"Has monsters: {gridData.Center.HasMonsters}");
            Debug.WriteLine($"Has aggressive: {gridData.Center.HasAggressiveMonsters}");
        }
        
        Debug.WriteLine("?? === TEST COMPLETE ===\n");
    }
    
    public static void RunDeathTest()
    {
        Debug.WriteLine("?? === ROOM TRACKER DEATH TEST ===");
        
        var tracker = new RoomTracker();
        
        // Step 1: Setup room with creatures
        var setupText = @"
[Hp=100/Mp=50/Mv=200] look
Forest Path
This is a winding path through the forest.
A goblin is here.
A gremlin (summoned) is here.
Exits: North, South
";
        
        Debug.WriteLine("?? Setting up room with creatures...");
        tracker.TryUpdateRoom("testuser", "testchar", setupText);
        Debug.WriteLine($"Setup monsters: {tracker.CurrentRoom?.Monsters.Count ?? 0}");
        
        // Step 2: Simulate killing the gremlin
        var deathText = @"
[Hp=95/Mp=45/Mv=195] kill gremlin
You attack the gremlin!
The gremlin falls to earth!
";
        
        Debug.WriteLine("\n?? Simulating creature death...");
        var updated = tracker.TryUpdateRoom("testuser", "testchar", deathText);
        Debug.WriteLine($"Death update: {updated}");
        Debug.WriteLine($"Remaining monsters: {tracker.CurrentRoom?.Monsters.Count ?? 0}");
        
        if (tracker.CurrentRoom != null)
        {
            Debug.WriteLine("?? Remaining creatures:");
            foreach (var monster in tracker.CurrentRoom.Monsters)
            {
                Debug.WriteLine($"  ?? {monster.Name} ({monster.Disposition})");
            }
        }
        
        Debug.WriteLine("?? === DEATH TEST COMPLETE ===\n");
    }
}