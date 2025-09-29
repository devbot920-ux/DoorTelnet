using System.Text.RegularExpressions;
using DoorTelnet.Core.Navigation.Models;
using DoorTelnet.Core.World;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// Service for matching RoomTracker parsed rooms with graph nodes
/// </summary>
public class RoomMatchingService
{
    private readonly GraphDataService _graphData;
    private readonly ILogger<RoomMatchingService> _logger;

    // Use ConcurrentDictionary instead of Dictionary + lock for thread safety
    private readonly ConcurrentDictionary<string, string> _nameToIdCache = new();
    private readonly ConcurrentDictionary<string, RoomMatchResult> _recentMatches = new();
    private const int MaxRecentMatches = 100;

    // Compiled regex for cleaning room names
    private static readonly Regex CleanNameRegex = new(@"[^\w\s]", RegexOptions.Compiled);

    public RoomMatchingService(GraphDataService graphData, ILogger<RoomMatchingService> logger)
    {
        _graphData = graphData;
        _logger = logger;
    }

    /// <summary>
    /// Finds the best matching graph node for a room state
    /// </summary>
    public RoomMatchResult? FindMatchingNode(RoomState roomState)
    {
        if (roomState == null || string.IsNullOrWhiteSpace(roomState.Name))
            return null;

        try
        {
            var roomName = roomState.Name.Trim();

            // Check recent matches cache first - no lock needed with ConcurrentDictionary
            if (_recentMatches.TryGetValue(roomName, out var cached))
            {
                if (DateTime.UtcNow - cached.MatchedAt < TimeSpan.FromMinutes(5))
                {
                    _logger.LogDebug("Using cached match for room: {RoomName}", roomName);
                    return cached;
                }
                else
                {
                    // Remove expired entry
                    _recentMatches.TryRemove(roomName, out _);
                }
            }

            var matchResult = PerformRoomMatching(roomState);

            if (matchResult != null)
            {
                // Cache the match - handle cache size management
                if (_recentMatches.Count >= MaxRecentMatches)
                {
                    // Remove some old entries (simple cleanup)
                    var expiredKeys = _recentMatches
                        .Where(kvp => DateTime.UtcNow - kvp.Value.MatchedAt > TimeSpan.FromMinutes(3))
                        .Take(10)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _recentMatches.TryRemove(key, out _);
                    }
                }
                
                _recentMatches.TryAdd(roomName, matchResult);

                _logger.LogDebug("Matched room '{RoomName}' to node {NodeId} with confidence {Confidence:P1}",
                    roomName, matchResult.Node.Id, matchResult.Confidence);
            }
            else
            {
                _logger.LogDebug("No match found for room: {RoomName}", roomName);
            }

            return matchResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching room: {RoomName}", roomState.Name);
            return null;
        }
    }

    /// <summary>
    /// Gets the current position confidence score (0.0 to 1.0)
    /// </summary>
    public double GetPositionConfidence(RoomState currentRoom)
    {
        var match = FindMatchingNode(currentRoom);
        return match?.Confidence ?? 0.0;
    }

    /// <summary>
    /// Attempts to find a graph node by exact or fuzzy room name matching
    /// </summary>
    public GraphNode? FindNodeByName(string roomName, double minimumConfidence = 0.7)
    {
        var match = FindBestNameMatch(roomName);
        return match != null && match.Confidence >= minimumConfidence ? match.Node : null;
    }

    /// <summary>
    /// Clears the matching cache
    /// </summary>
    public void ClearCache()
    {
        _nameToIdCache.Clear();
        _recentMatches.Clear();
        _logger.LogInformation("Room matching cache cleared");
    }

    private RoomMatchResult? PerformRoomMatching(RoomState roomState)
    {
        if (!_graphData.IsLoaded)
            return null;

        var roomName = roomState.Name.Trim();

        // Try exact label match first
        var exactMatch = FindExactLabelMatch(roomName);
        if (exactMatch != null)
        {
            return new RoomMatchResult(exactMatch, 1.0, "Exact label match");
        }

        // Try sector/description matching
        var sectorMatch = FindSectorMatch(roomName);
        if (sectorMatch != null)
        {
            var confidence = CalculateMatchConfidence(roomState, sectorMatch);
            if (confidence > 0.8)
            {
                return new RoomMatchResult(sectorMatch, confidence, "Sector match with high confidence");
            }
        }

        // Try fuzzy name matching
        var fuzzyMatch = FindBestNameMatch(roomName);
        if (fuzzyMatch != null && fuzzyMatch.Confidence > 0.7)
        {
            return fuzzyMatch;
        }

        // Try context-based matching using exits
        var contextMatch = FindContextMatch(roomState);
        if (contextMatch != null)
        {
            return contextMatch;
        }

        return null;
    }

    private GraphNode? FindExactLabelMatch(string roomName)
    {
        // Check exact label matches
        var nodes = _graphData.FindRooms(node => 
            string.Equals(node.Label, roomName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Sector, roomName, StringComparison.OrdinalIgnoreCase), 1);

        return nodes.FirstOrDefault();
    }

    private GraphNode? FindSectorMatch(string roomName)
    {
        // Match against sector field which often contains room descriptions
        var nodes = _graphData.FindRooms(node => 
            !string.IsNullOrEmpty(node.Sector) &&
            node.Sector.Contains(roomName, StringComparison.OrdinalIgnoreCase), 5);

        return nodes.FirstOrDefault();
    }

    private RoomMatchResult? FindBestNameMatch(string roomName)
    {
        var cleanRoomName = CleanNameRegex.Replace(roomName, "").ToLowerInvariant();
        var words = cleanRoomName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return null;

        var candidates = _graphData.FindRooms(node => 
            !string.IsNullOrEmpty(node.Sector) || !string.IsNullOrEmpty(node.Label), 50);

        var bestMatch = candidates
            .Select(node => new
            {
                Node = node,
                Confidence = CalculateNameSimilarity(roomName, node)
            })
            .Where(match => match.Confidence > 0.6)
            .OrderByDescending(match => match.Confidence)
            .FirstOrDefault();

        return bestMatch != null ? 
            new RoomMatchResult(bestMatch.Node, bestMatch.Confidence, "Fuzzy name match") : 
            null;
    }

    private RoomMatchResult? FindContextMatch(RoomState roomState)
    {
        // Try to match based on available exits
        if (roomState.Exits.Count == 0)
            return null;

        var exitSet = new HashSet<string>(
            roomState.Exits.Select(NormalizeDirection), 
            StringComparer.OrdinalIgnoreCase);

        var candidates = _graphData.FindRooms(node => true, 100);

        foreach (var candidate in candidates)
        {
            var nodeExits = _graphData.GetOutgoingEdges(candidate.Id)
                .Select(edge => NormalizeDirection(edge.Direction))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Calculate exit overlap
            var commonExits = exitSet.Intersect(nodeExits).Count();
            var totalExits = exitSet.Union(nodeExits).Count();

            if (totalExits > 0)
            {
                var exitSimilarity = (double)commonExits / totalExits;
                if (exitSimilarity > 0.7) // High exit similarity
                {
                    var confidence = exitSimilarity * 0.8; // Context matches are less certain
                    return new RoomMatchResult(candidate, confidence, "Context/exit match");
                }
            }
        }

        return null;
    }

    private double CalculateMatchConfidence(RoomState roomState, GraphNode node)
    {
        double confidence = 0.5; // Base confidence for sector match

        // Boost confidence for exact name matches
        if (string.Equals(roomState.Name, node.Sector, StringComparison.OrdinalIgnoreCase))
            confidence += 0.3;

        // Consider exit matches
        if (roomState.Exits.Count > 0)
        {
            var roomExits = new HashSet<string>(
                roomState.Exits.Select(NormalizeDirection), 
                StringComparer.OrdinalIgnoreCase);

            var nodeExits = _graphData.GetOutgoingEdges(node.Id)
                .Select(edge => NormalizeDirection(edge.Direction))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var commonExits = roomExits.Intersect(nodeExits).Count();
            var totalExits = Math.Max(roomExits.Count, nodeExits.Count);

            if (totalExits > 0)
            {
                var exitSimilarity = (double)commonExits / totalExits;
                confidence += exitSimilarity * 0.2;
            }
        }

        return Math.Min(1.0, confidence);
    }

    private double CalculateNameSimilarity(string roomName, GraphNode node)
    {
        var targets = new[] { node.Label, node.Sector, node.Description }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (!targets.Any())
            return 0.0;

        var maxSimilarity = targets
            .Select(target => CalculateStringSimilarity(roomName, target!))
            .Max();

        return maxSimilarity;
    }

    private double CalculateStringSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0.0;

        s1 = s1.ToLowerInvariant();
        s2 = s2.ToLowerInvariant();

        if (s1 == s2)
            return 1.0;

        if (s1.Contains(s2) || s2.Contains(s1))
            return 0.8;

        // Simple word overlap scoring
        var words1 = s1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = s2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var commonWords = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();
        var totalWords = words1.Union(words2, StringComparer.OrdinalIgnoreCase).Count();

        return totalWords > 0 ? (double)commonWords / totalWords : 0.0;
    }

    private static string NormalizeDirection(string direction)
    {
        return direction.ToLowerInvariant() switch
        {
            "n" => "north",
            "s" => "south",
            "e" => "east", 
            "w" => "west",
            "ne" => "northeast",
            "nw" => "northwest",
            "se" => "southeast", 
            "sw" => "southwest",
            "u" => "up",
            "d" => "down",
            _ => direction.ToLowerInvariant()
        };
    }
}

/// <summary>
/// Represents the result of matching a room state to a graph node
/// </summary>
public class RoomMatchResult
{
    public GraphNode Node { get; }
    public double Confidence { get; }
    public string MatchType { get; }
    public DateTime MatchedAt { get; }

    public RoomMatchResult(GraphNode node, double confidence, string matchType)
    {
        Node = node;
        Confidence = Math.Clamp(confidence, 0.0, 1.0);
        MatchType = matchType;
        MatchedAt = DateTime.UtcNow;
    }
}