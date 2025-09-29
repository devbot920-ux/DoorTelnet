using System.Text.Json.Serialization;

namespace DoorTelnet.Core.Navigation.Models;

/// <summary>
/// Represents a room node in the navigation graph with comprehensive metadata
/// </summary>
public class GraphNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("s")]
    public string Sector { get; set; } = string.Empty;

    [JsonPropertyName("l")]
    public string? Description { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("r")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("tp")]
    public int Transport { get; set; }

    [JsonPropertyName("peaceful")]
    public int Peaceful { get; set; }

    [JsonPropertyName("is_tavern")]
    public int IsTavern { get; set; }

    [JsonPropertyName("is_store")]
    public int IsStore { get; set; }

    [JsonPropertyName("is_quest")]
    public int IsQuest { get; set; }

    [JsonPropertyName("is_spell_trainer")]
    public int IsSpellTrainer { get; set; }

    [JsonPropertyName("has_trap")]
    public int HasTrap { get; set; }

    [JsonPropertyName("portal")]
    public int Portal { get; set; }

    [JsonPropertyName("spawn_total")]
    public int SpawnTotal { get; set; }

    // Spawn slots (8 total)
    [JsonPropertyName("spawn1_id")]
    public int? Spawn1Id { get; set; }

    [JsonPropertyName("spawn1_name")]
    public string? Spawn1Name { get; set; }

    [JsonPropertyName("spawn1_type")]
    public string? Spawn1Type { get; set; }

    [JsonPropertyName("spawn1_count")]
    public int? Spawn1Count { get; set; }

    [JsonPropertyName("spawn2_id")]
    public int? Spawn2Id { get; set; }

    [JsonPropertyName("spawn2_name")]
    public string? Spawn2Name { get; set; }

    [JsonPropertyName("spawn2_type")]
    public string? Spawn2Type { get; set; }

    [JsonPropertyName("spawn2_count")]
    public int? Spawn2Count { get; set; }

    [JsonPropertyName("spawn3_id")]
    public int? Spawn3Id { get; set; }

    [JsonPropertyName("spawn3_name")]
    public string? Spawn3Name { get; set; }

    [JsonPropertyName("spawn3_type")]
    public string? Spawn3Type { get; set; }

    [JsonPropertyName("spawn3_count")]
    public int? Spawn3Count { get; set; }

    [JsonPropertyName("spawn4_id")]
    public int? Spawn4Id { get; set; }

    [JsonPropertyName("spawn4_name")]
    public string? Spawn4Name { get; set; }

    [JsonPropertyName("spawn4_type")]
    public string? Spawn4Type { get; set; }

    [JsonPropertyName("spawn4_count")]
    public int? Spawn4Count { get; set; }

    [JsonPropertyName("spawn5_id")]
    public int? Spawn5Id { get; set; }

    [JsonPropertyName("spawn5_name")]
    public string? Spawn5Name { get; set; }

    [JsonPropertyName("spawn5_type")]
    public string? Spawn5Type { get; set; }

    [JsonPropertyName("spawn5_count")]
    public int? Spawn5Count { get; set; }

    [JsonPropertyName("spawn6_id")]
    public int? Spawn6Id { get; set; }

    [JsonPropertyName("spawn6_name")]
    public string? Spawn6Name { get; set; }

    [JsonPropertyName("spawn6_type")]
    public string? Spawn6Type { get; set; }

    [JsonPropertyName("spawn6_count")]
    public int? Spawn6Count { get; set; }

    [JsonPropertyName("spawn7_id")]
    public int? Spawn7Id { get; set; }

    [JsonPropertyName("spawn7_name")]
    public string? Spawn7Name { get; set; }

    [JsonPropertyName("spawn7_type")]
    public string? Spawn7Type { get; set; }

    [JsonPropertyName("spawn7_count")]
    public int? Spawn7Count { get; set; }

    [JsonPropertyName("spawn8_id")]
    public int? Spawn8Id { get; set; }

    [JsonPropertyName("spawn8_name")]
    public string? Spawn8Name { get; set; }

    [JsonPropertyName("spawn8_type")]
    public string? Spawn8Type { get; set; }

    [JsonPropertyName("spawn8_count")]
    public int? Spawn8Count { get; set; }

    // Attributes and skills
    [JsonPropertyName("attr_code")]
    public int? AttrCode { get; set; }

    [JsonPropertyName("attr_name")]
    public string? AttrName { get; set; }

    [JsonPropertyName("attr_min")]
    public int? AttrMin { get; set; }

    [JsonPropertyName("attr_max")]
    public int? AttrMax { get; set; }

    [JsonPropertyName("skill_code")]
    public int? SkillCode { get; set; }

    [JsonPropertyName("skill_name")]
    public string? SkillName { get; set; }

    [JsonPropertyName("skill_min")]
    public int? SkillMin { get; set; }

    [JsonPropertyName("skill_max")]
    public int? SkillMax { get; set; }

    [JsonPropertyName("spells_count")]
    public int? SpellsCount { get; set; }

    [JsonPropertyName("quest_text")]
    public string? QuestText { get; set; }

    [JsonPropertyName("promo_max")]
    public int? PromoMax { get; set; }

    /// <summary>
    /// Helper property to check if this room is peaceful
    /// </summary>
    public bool IsPeaceful => Peaceful == 1;

    /// <summary>
    /// Helper property to check if this room has facilities
    /// </summary>
    public bool HasFacilities => IsTavern == 1 || IsStore == 1 || IsSpellTrainer == 1;

    /// <summary>
    /// Helper property to check if this room is dangerous
    /// </summary>
    public bool IsDangerous => HasTrap == 1 || (!IsPeaceful && SpawnTotal > 0);

    /// <summary>
    /// Gets all spawn names in this room
    /// </summary>
    public IEnumerable<string> GetSpawnNames()
    {
        var spawns = new[] { Spawn1Name, Spawn2Name, Spawn3Name, Spawn4Name, Spawn5Name, Spawn6Name, Spawn7Name, Spawn8Name };
        return spawns.Where(s => !string.IsNullOrWhiteSpace(s))!;
    }
}