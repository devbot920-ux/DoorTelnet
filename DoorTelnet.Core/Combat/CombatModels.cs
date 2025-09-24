using System;

namespace DoorTelnet.Core.Combat;

/// <summary>
/// Represents a single combat encounter with a monster
/// </summary>
public class CombatEntry
{
    public string MonsterName { get; set; } = string.Empty;
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
    public int ExperienceGained { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public bool IsCompleted { get; set; }
    public string Status { get; set; } = "Active"; // Active, Victory, Fled, Death
    
    /// <summary>
    /// Duration of the combat in seconds
    /// </summary>
    public double DurationSeconds 
    { 
        get 
        { 
            var end = EndTime ?? DateTime.UtcNow;
            return (end - StartTime).TotalSeconds; 
        } 
    }
    
    /// <summary>
    /// Damage per second dealt to the monster
    /// </summary>
    public double DpsDealt
    {
        get
        {
            var duration = DurationSeconds;
            return duration > 0 ? DamageDealt / duration : 0;
        }
    }
    
    /// <summary>
    /// Damage per second taken from the monster
    /// </summary>
    public double DpsTaken
    {
        get
        {
            var duration = DurationSeconds;
            return duration > 0 ? DamageTaken / duration : 0;
        }
    }
    
    /// <summary>
    /// Experience per second gained
    /// </summary>
    public double ExpPerSecond
    {
        get
        {
            var duration = DurationSeconds;
            return duration > 0 ? ExperienceGained / duration : 0;
        }
    }
}

/// <summary>
/// Tracks an ongoing combat encounter
/// </summary>
public class ActiveCombat
{
    public string MonsterName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
    public DateTime LastDamageTime { get; set; } = DateTime.UtcNow;
    public bool AwaitingExperience { get; set; }
    public DateTime? DeathTime { get; set; }
    
    /// <summary>
    /// Indicates if this monster is currently targeted for melee combat
    /// </summary>
    public bool IsTargeted { get; set; }
    
    /// <summary>
    /// When this monster was targeted for melee combat
    /// </summary>
    public DateTime? TargetedTime { get; set; }
    
    /// <summary>
    /// Duration of the combat in seconds
    /// </summary>
    public double DurationSeconds 
    { 
        get 
        { 
            var end = DeathTime ?? DateTime.UtcNow;
            return (end - StartTime).TotalSeconds; 
        } 
    }
    
    /// <summary>
    /// Marks the combat as complete and returns a CombatEntry
    /// </summary>
    public CombatEntry Complete(string status, int experienceGained = 0)
    {
        var endTime = DeathTime ?? DateTime.UtcNow;
        
        return new CombatEntry
        {
            MonsterName = MonsterName,
            DamageDealt = DamageDealt,
            DamageTaken = DamageTaken,
            ExperienceGained = experienceGained,
            StartTime = StartTime,
            EndTime = endTime,
            IsCompleted = true,
            Status = status
        };
    }
    
    /// <summary>
    /// Check if this combat should be considered stale and auto-completed
    /// </summary>
    public bool IsStale(TimeSpan timeout)
    {
        return DateTime.UtcNow - LastDamageTime > timeout;
    }
}

/// <summary>
/// Statistics summary for combat tracking
/// </summary>
public class CombatStatistics
{
    public int TotalCombats { get; set; }
    public int TotalExperience { get; set; }
    public int TotalDamageDealt { get; set; }
    public int TotalDamageTaken { get; set; }
    public double AverageDamageDealt { get; set; }
    public double AverageDamageTaken { get; set; }
    public double AverageExperience { get; set; }
    public double AverageDuration { get; set; }
    public int Victories { get; set; }
    public int Deaths { get; set; }
    public int Flees { get; set; }
    
    public double WinRate => TotalCombats > 0 ? (double)Victories / TotalCombats : 0;
    public double DeathRate => TotalCombats > 0 ? (double)Deaths / TotalCombats : 0;
    public double FleeRate => TotalCombats > 0 ? (double)Flees / TotalCombats : 0;
}