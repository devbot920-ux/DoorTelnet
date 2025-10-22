using System.Text.RegularExpressions;
using DoorTelnet.Core.Navigation.Models;
using DoorTelnet.Core.World;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// Enhanced navigation context for room matching
/// </summary>
public class NavigationContext
{
    public string? PreviousRoomId { get; set; }
    public string? ExpectedRoomId { get; set; }
    public string? LastDirection { get; set; }
    public NavigationPath? CurrentPath { get; set; }
    public int? CurrentStepIndex { get; set; }
    public double PreviousConfidence { get; set; }
    public DateTime LastMovement { get; set; }
    
    public bool HasPathContext => CurrentPath != null && CurrentStepIndex.HasValue;
    public bool HasPreviousRoom => !string.IsNullOrEmpty(PreviousRoomId);
    public bool HasExpectedRoom => !string.IsNullOrEmpty(ExpectedRoomId);
}

/// <summary>
/// Enhanced room match result with contextual information
/// </summary>
public class EnhancedRoomMatchResult : RoomMatchResult
{
    public double ContextConfidence { get; set; }
    public double NameConfidence { get; set; }
    public double ExitConfidence { get; set; }
    public double PathConfidence { get; set; }
    public List<string> MatchReasons { get; set; } = new();
    public List<GraphNode> AlternativeCandidates { get; set; } = new();

    public EnhancedRoomMatchResult(GraphNode node, double confidence, string matchType) 
        : base(node, confidence, matchType)
    {
    }

    public static EnhancedRoomMatchResult FromBasic(RoomMatchResult basic)
    {
        return new EnhancedRoomMatchResult(basic.Node, basic.Confidence, basic.MatchType)
        {
            NameConfidence = basic.Confidence
        };
    }
}

/// <summary>
/// Metadata about a cached room match to determine cacheability
/// </summary>
internal class CachedRoomMatch
{
    public RoomMatchResult MatchResult { get; set; } = null!;
    public int DuplicateRoomCount { get; set; }
    public bool IsCacheable { get; set; }
    public string ExitSignature { get; set; } = string.Empty;
}

/// <summary>
/// Service for matching RoomTracker parsed rooms with graph nodes using enhanced contextual analysis
/// </summary>
public class RoomMatchingService
{
    private readonly GraphDataService _graphData;
    private readonly ILogger<RoomMatchingService> _logger;

    // Use ConcurrentDictionary instead of Dictionary + lock for thread safety
    private readonly ConcurrentDictionary<string, string> _nameToIdCache = new();
    private readonly ConcurrentDictionary<string, CachedRoomMatch> _recentMatches = new();
    private const int MaxRecentMatches = 100;

    // Navigation context tracking
    private NavigationContext _currentContext = new();
    private readonly ConcurrentDictionary<string, DateTime> _roomVisitHistory = new();

    // Compiled regex for cleaning room names
    private static readonly Regex CleanNameRegex = new(@"[^\w\s]", RegexOptions.Compiled);

    public RoomMatchingService(GraphDataService graphData, ILogger<RoomMatchingService> logger)
    {
        _graphData = graphData;
        _logger = logger;
    }

    /// <summary>
    /// Updates the navigation context for enhanced room matching
    /// </summary>
    public void UpdateNavigationContext(NavigationContext context)
    {
        _currentContext = context ?? new NavigationContext();
        _logger.LogDebug("Navigation context updated: Previous={Previous}, Expected={Expected}, Step={Step}", 
            context?.PreviousRoomId, context?.ExpectedRoomId, context?.CurrentStepIndex);
    }

    /// <summary>
    /// Finds the best matching graph node for a room state using enhanced contextual analysis
    /// </summary>
    public RoomMatchResult? FindMatchingNode(RoomState roomState)
    {
        if (roomState == null || string.IsNullOrWhiteSpace(roomState.Name))
            return null;

        try
        {
            var roomName = roomState.Name.Trim();
            var exitSignature = GenerateExitSignature(roomState.Exits);

            // Check recent matches cache first - but only use if it's cacheable
            var cacheKey = $"{roomName}|{exitSignature}|{_currentContext.PreviousRoomId}|{_currentContext.LastDirection}";
            if (_recentMatches.TryGetValue(cacheKey, out var cached)&& _currentContext.LastDirection == null)
            {
                if (DateTime.UtcNow - cached.MatchResult.MatchedAt < TimeSpan.FromMinutes(2) ) // Shorter cache for contextual matches
                {
                    // Only use cache if the room was cacheable (not a hallway/duplicate)
                    if (cached.IsCacheable)
                    {
                        _logger.LogDebug("Using cached match for unique room: {RoomName} (duplicates: {Count})", 
                            roomName, cached.DuplicateRoomCount);
                        
                        // Update the RoomState with the cached room ID
                        roomState.RoomId = cached.MatchResult.Node.Id;
                        return cached.MatchResult;
                    }
                    else
                    {
                        _logger.LogDebug("Cache hit for ambiguous room '{RoomName}' (duplicates: {Count}) - re-matching with context", 
                            roomName, cached.DuplicateRoomCount);
                        // Don't use cache - fall through to re-match
                    }
                }
                else
                {
                    // Remove expired entry
                    _recentMatches.TryRemove(cacheKey, out _);
                }
            }

            var matchResult = PerformEnhancedRoomMatching(roomState);

            if (matchResult != null)
            {
                // CRITICAL: Update the RoomState with the matched graph node ID
                roomState.RoomId = matchResult.Node.Id;
                
                // Analyze if this room is cacheable (not a duplicate/hallway)
                var nameMatches = FindAllNameMatches(roomName);
                var exitsMatchingRooms = nameMatches.Where(node => 
                    GenerateExitSignature(GetNodeExits(node)) == exitSignature).ToList();
                
                var duplicateCount = exitsMatchingRooms.Count;
                var isCacheable = duplicateCount == 1; // Only cache if there's exactly one room with this name+exit pattern
                
                if (!isCacheable)
                {
                    _logger.LogInformation("Room '{RoomName}' with exits [{Exits}] has {Count} duplicates - NOT caching (likely hallway/corridor)", 
                        roomName, exitSignature, duplicateCount);
                }
                else
                {
                    _logger.LogDebug("Room '{RoomName}' is unique - caching for future lookups", roomName);
                }
                
                // Store the match with cacheability metadata
                var cachedMatch = new CachedRoomMatch
                {
                    MatchResult = matchResult,
                    DuplicateRoomCount = duplicateCount,
                    IsCacheable = isCacheable,
                    ExitSignature = exitSignature
                };
                
                // Cache the match - handle cache size management
                if (_recentMatches.Count >= MaxRecentMatches)
                {
                    // Remove some old entries (simple cleanup)
                    var expiredKeys = _recentMatches
                        .Where(kvp => DateTime.UtcNow - kvp.Value.MatchResult.MatchedAt > TimeSpan.FromMinutes(1))
                        .Take(10)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _recentMatches.TryRemove(key, out _);
                    }
                }
                
                _recentMatches.TryAdd(cacheKey, cachedMatch);

                // Update visit history
                _roomVisitHistory.TryAdd(matchResult.Node.Id, DateTime.UtcNow);

                _logger.LogInformation("Enhanced match: '{RoomName}' -> {NodeId} (confidence: {Confidence:P1}, type: {Type}, cacheable: {Cacheable})",
                    roomName, matchResult.Node.Id, matchResult.Confidence, matchResult.MatchType, isCacheable);

                if (matchResult is EnhancedRoomMatchResult enhanced)
                {
                    _logger.LogDebug("Match breakdown - Name: {Name:P1}, Exit: {Exit:P1}, Path: {Path:P1}, Context: {Context:P1}",
                        enhanced.NameConfidence, enhanced.ExitConfidence, enhanced.PathConfidence, enhanced.ContextConfidence);
                    
                    if (enhanced.MatchReasons.Count > 0)
                    {
                        _logger.LogDebug("Match reasons: {Reasons}", string.Join(", ", enhanced.MatchReasons));
                    }
                }
            }
            else
            {
                _logger.LogWarning("No enhanced match found for room: {RoomName} (Context: Prev={Prev}, Expected={Exp})",
                    roomName, _currentContext.PreviousRoomId, _currentContext.ExpectedRoomId);
            }

            return matchResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in enhanced room matching for: {RoomName}", roomState.Name);
            return null;
        }
    }

    /// <summary>
    /// Generates a normalized exit signature for duplicate detection
    /// </summary>
    private string GenerateExitSignature(List<string> exits)
    {
        if (exits == null || exits.Count == 0)
            return "none";
        
        // Normalize and sort exits to create a consistent signature
        var normalized = exits.Select(NormalizeDirection).OrderBy(e => e);
        return string.Join(",", normalized);
    }

    /// <summary>
    /// Gets the list of exit directions for a graph node
    /// </summary>
    private List<string> GetNodeExits(GraphNode node)
    {
        return _graphData.GetOutgoingEdges(node.Id)
            .Select(edge => NormalizeDirection(edge.Direction))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Fast path room matching during navigation - checks expected room first
    /// </summary>
    public RoomMatchResult? FindMatchingNodeFast(RoomState roomState)
    {
        if (roomState == null || string.IsNullOrWhiteSpace(roomState.Name))
            return null;

        try
        {
            var roomName = roomState.Name.Trim();

            // FAST PATH: If we have an expected room during navigation, check it first
            if (_currentContext.HasExpectedRoom)
            {
                var expectedNode = _graphData.GetNode(_currentContext.ExpectedRoomId!);
                if (expectedNode != null)
                {
                    // Quick name match check
                    if (string.Equals(expectedNode.Label, roomName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(expectedNode.Sector, roomName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Fast navigation match: expected room {ExpectedId} matches current room '{RoomName}'", 
                            _currentContext.ExpectedRoomId, roomName);
                        
                        // Update the RoomState with the matched room ID
                        roomState.RoomId = expectedNode.Id;
                        
                        return new RoomMatchResult(expectedNode, 0.95, "Fast navigation match");
                    }
                }
            }

            // ENHANCED: If we have movement context, prioritize rooms in that direction
            if (_currentContext.HasPreviousRoom && !string.IsNullOrEmpty(_currentContext.LastDirection))
            {
                // Get the edge from previous room in the movement direction
                var previousRoomEdges = _graphData.GetOutgoingEdges(_currentContext.PreviousRoomId!);
                var movementEdge = previousRoomEdges.FirstOrDefault(e => 
                    NormalizeDirection(e.Direction) == NormalizeDirection(_currentContext.LastDirection!));

                if (movementEdge != null)
                {
                    var targetNode = _graphData.GetNode(movementEdge.Target);
                    if (targetNode != null)
                    {
                        // Check if this node matches the room name
                        if (string.Equals(targetNode.Label, roomName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(targetNode.Sector, roomName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("Movement direction match: moved {Direction} from {PreviousRoom} to {TargetRoom}", 
                                _currentContext.LastDirection, _currentContext.PreviousRoomId, targetNode.Id);
                            
                            // Update the RoomState with the matched room ID
                            roomState.RoomId = targetNode.Id;
                            
                            return new RoomMatchResult(targetNode, 0.90, "Movement direction match");
                        }
                    }
                }
            }

            // Fallback to full enhanced matching if fast path fails
            return FindMatchingNode(roomState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in fast room matching for: {RoomName}", roomState.Name);
            return FindMatchingNode(roomState); // Fallback to full matching
        }
    }

    /// <summary>
    /// Performs enhanced room matching using contextual information
    /// </summary>
    private RoomMatchResult? PerformEnhancedRoomMatching(RoomState roomState)
    {
        if (!_graphData.IsLoaded)
            return null;

        var roomName = roomState.Name.Trim();
        var candidates = new List<(GraphNode node, double confidence, List<string> reasons)>();

        // 1. Get all potential matches by name
        var nameMatches = FindAllNameMatches(roomName);
        
        if (nameMatches.Count == 0)
        {
            _logger.LogDebug("No name matches found for: {RoomName}", roomName);
            return null;
        }

        if (nameMatches.Count == 1)
        {
            // Only one match - still do contextual validation
            var single = nameMatches[0];
            var enhanced = CreateEnhancedResult(single, roomState, new List<string> { "Single name match" });
            return enhanced;
        }

        _logger.LogDebug("Found {Count} name matches for '{RoomName}', applying contextual analysis", 
            nameMatches.Count, roomName);

        // 2. PRIORITY FILTERING: If we have movement context, filter candidates by valid movement edges
        if (_currentContext.HasPreviousRoom && !string.IsNullOrEmpty(_currentContext.LastDirection))
        {
            var previousRoomEdges = _graphData.GetOutgoingEdges(_currentContext.PreviousRoomId!);
            var movementEdge = previousRoomEdges.FirstOrDefault(e => 
                NormalizeDirection(e.Direction) == NormalizeDirection(_currentContext.LastDirection!));

            if (movementEdge != null)
            {
                // Filter to only candidates reachable by the movement direction
                var reachableCandidate = nameMatches.FirstOrDefault(n => n.Id == movementEdge.Target);
                if (reachableCandidate != null)
                {
                    _logger.LogDebug("Movement validation: found reachable room {RoomId} via {Direction} from {PreviousRoom}", 
                        reachableCandidate.Id, _currentContext.LastDirection, _currentContext.PreviousRoomId);
                    
                    // This is the most likely match - prioritize it heavily
                    var enhanced = CreateEnhancedResult(reachableCandidate, roomState, 
                        new List<string> { "Reachable via movement direction", $"Moved {_currentContext.LastDirection}" });
                    return enhanced;
                }
                else
                {
                    _logger.LogWarning("Movement validation failed: no matching room reachable via {Direction} from {PreviousRoom} with name '{RoomName}'",
                        _currentContext.LastDirection, _currentContext.PreviousRoomId, roomName);
                }
            }
        }

        // 3. Apply contextual scoring to disambiguate multiple matches (fallback)
        foreach (var candidate in nameMatches)
        {
            var (confidence, reasons) = CalculateContextualConfidence(candidate, roomState);
            candidates.Add((candidate, confidence, reasons));
        }

        // 4. Select best candidate
        var bestCandidate = candidates.OrderByDescending(c => c.confidence).FirstOrDefault();
        
        if (bestCandidate.confidence > 0.5)
        {
            var enhanced = CreateEnhancedResult(bestCandidate.node, roomState, bestCandidate.reasons);
            
            // Store alternative candidates for debugging
            if (enhanced is EnhancedRoomMatchResult enhancedResult)
            {
                enhancedResult.AlternativeCandidates = [.. candidates
                    .Where(c => c.node.Id != bestCandidate.node.Id)
                    .OrderByDescending(c => c.confidence)
                    .Take(3)
                    .Select(c => c.node)];
            }
            
            return enhanced;
        }

        _logger.LogWarning("No candidate reached minimum confidence threshold for: {RoomName}", roomName);
        return null;
    }

    /// <summary>
    /// Finds all potential room matches by name/description
    /// </summary>
    private List<GraphNode> FindAllNameMatches(string roomName)
    {
        var matches = new List<GraphNode>();

        // Exact label/sector matches
        var exactMatches = _graphData.FindRooms(node => 
            string.Equals(node.Label, roomName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Sector, roomName, StringComparison.OrdinalIgnoreCase), 50);
        matches.AddRange(exactMatches);

        // Partial matches if no exact matches
        if (matches.Count == 0)
        {
            var partialMatches = _graphData.FindRooms(node => 
                (!string.IsNullOrEmpty(node.Label) && node.Label.Contains(roomName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(node.Sector) && node.Sector.Contains(roomName, StringComparison.OrdinalIgnoreCase)), 20);
            matches.AddRange(partialMatches);
        }

        return [.. matches.Distinct()];
    }

    /// <summary>
    /// Calculates contextual confidence for a room candidate
    /// </summary>
    private (double confidence, List<string> reasons) CalculateContextualConfidence(GraphNode candidate, RoomState roomState)
    {
        double confidence = 0.0;
        var reasons = new List<string>();

        // Base name match confidence
        double nameConfidence = CalculateNameSimilarity(roomState.Name, candidate);
        confidence += nameConfidence * 0.3;
        reasons.Add($"Name similarity: {nameConfidence:P1}");

        // Exit pattern matching (higher weight)
        double exitConfidence = CalculateExitConfidence(candidate, roomState);
        confidence += exitConfidence * 0.4;
        reasons.Add($"Exit pattern: {exitConfidence:P1}");

        // Path context confidence
        if (_currentContext.HasPathContext || _currentContext.HasExpectedRoom)
        {
            double pathConfidence = CalculatePathConfidence(candidate);
            confidence += pathConfidence * 0.25;
            reasons.Add($"Path context: {pathConfidence:P1}");
        }

        // Movement direction validation
        if (_currentContext.HasPreviousRoom && !string.IsNullOrEmpty(_currentContext.LastDirection))
        {
            double movementConfidence = ValidateMovementDirection(candidate);
            confidence += movementConfidence * 0.15;
            reasons.Add($"Movement validation: {movementConfidence:P1}");
        }

        // Recent visit penalty/bonus
        if (_roomVisitHistory.TryGetValue(candidate.Id, out var lastVisit))
        {
            var timeSinceVisit = DateTime.UtcNow - lastVisit;
            if (timeSinceVisit < TimeSpan.FromMinutes(5))
            {
                confidence += 0.1; // Small bonus for recently visited rooms
                reasons.Add("Recently visited");
            }
        }

        return (Math.Min(1.0, confidence), reasons);
    }

    /// <summary>
    /// Validates that movement from previous room in given direction leads to candidate
    /// </summary>
    private double ValidateMovementDirection(GraphNode candidate)
    {
        if (!_currentContext.HasPreviousRoom || string.IsNullOrEmpty(_currentContext.LastDirection))
            return 0.0;

        try
        {
            var previousRoomEdges = _graphData.GetOutgoingEdges(_currentContext.PreviousRoomId!);
            var expectedEdge = previousRoomEdges.FirstOrDefault(e => 
                NormalizeDirection(e.Direction) == NormalizeDirection(_currentContext.LastDirection!) &&
                e.Target == candidate.Id);

            if (expectedEdge != null)
            {
                return 0.9; // High confidence - movement direction is validated
            }

            // Check reverse direction as additional validation
            var candidateEdges = _graphData.GetOutgoingEdges(candidate.Id);
            var reverseDirection = GetReverseDirection(_currentContext.LastDirection!);
            if (!string.IsNullOrEmpty(reverseDirection))
            {
                var reverseEdge = candidateEdges.FirstOrDefault(e =>
                    NormalizeDirection(e.Direction) == NormalizeDirection(reverseDirection) &&
                    e.Target == _currentContext.PreviousRoomId);

                if (reverseEdge != null)
                {
                    return 0.7; // Good confidence - reverse path exists
                }
            }

            return 0.0; // Movement doesn't validate
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error validating movement direction for candidate {CandidateId}", candidate.Id);
            return 0.0;
        }
    }

    /// <summary>
    /// Calculates confidence based on expected path
    /// </summary>
    private double CalculatePathConfidence(GraphNode candidate)
    {
        double confidence = 0.0;

        // Direct expected room match - VERY HIGH priority during navigation
        if (_currentContext.HasExpectedRoom && candidate.Id == _currentContext.ExpectedRoomId)
        {
            confidence += 0.95; // Increased from 0.8 - this should be the strongest signal
            _logger.LogDebug("Strong path confidence: candidate {CandidateId} matches expected room {ExpectedId}", 
                candidate.Id, _currentContext.ExpectedRoomId);
        }

        // Path following validation
        if (_currentContext.HasPathContext)
        {
            var currentStep = _currentContext.CurrentStepIndex!.Value;
            if (currentStep >= 0 && currentStep < _currentContext.CurrentPath!.Steps.Count)
            {
                var expectedStep = _currentContext.CurrentPath.Steps[currentStep];
                if (expectedStep.ToRoomId == candidate.Id)
                {
                    confidence += 0.9; // Very high confidence - exactly on planned path
                    _logger.LogDebug("Path step validation: candidate {CandidateId} matches step {Step} destination", 
                        candidate.Id, currentStep);
                }
            }
        }

        return Math.Min(1.0, confidence);
    }

    /// <summary>
    /// Enhanced exit pattern matching with more sophisticated analysis
    /// </summary>
    private double CalculateExitConfidence(GraphNode candidate, RoomState roomState)
    {
        if (roomState.Exits.Count == 0)
            return 0.5; // Neutral confidence if no exit information

        var roomExits = new HashSet<string>(
            roomState.Exits.Select(NormalizeDirection), 
            StringComparer.OrdinalIgnoreCase);

        var nodeExits = _graphData.GetOutgoingEdges(candidate.Id)
            .Select(edge => NormalizeDirection(edge.Direction))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (nodeExits.Count == 0)
            return 0.0; // No exits in graph data

        // Calculate Jaccard similarity (intersection over union)
        var intersection = roomExits.Intersect(nodeExits).Count();
        var union = roomExits.Union(nodeExits).Count();

        if (union == 0)
            return 0.0;

        var jaccardSimilarity = (double)intersection / union;

        // Bonus for exact match
        if (roomExits.SetEquals(nodeExits))
        {
            return 1.0;
        }

        // Penalty for missing critical exits
        var missingExits = roomExits.Except(nodeExits).Count();
        var extraExits = nodeExits.Except(roomExits).Count();

        // Higher penalty for missing exits than extra exits
        var penalty = (missingExits * 0.2) + (extraExits * 0.1);

        return Math.Max(0.0, jaccardSimilarity - penalty);
    }

    /// <summary>
    /// Creates an enhanced result with detailed confidence breakdown
    /// </summary>
    private EnhancedRoomMatchResult CreateEnhancedResult(GraphNode node, RoomState roomState, List<string> reasons)
    {
        var nameConfidence = CalculateNameSimilarity(roomState.Name, node);
        var exitConfidence = CalculateExitConfidence(node, roomState);
        var pathConfidence = _currentContext.HasPathContext || _currentContext.HasExpectedRoom ? 
            CalculatePathConfidence(node) : 0.0;
        var contextConfidence = _currentContext.HasPreviousRoom ? 
            ValidateMovementDirection(node) : 0.0;

        // Adjust weighting based on navigation state
        double overallConfidence;
        if (_currentContext.HasExpectedRoom && pathConfidence > 0.8)
        {
            // During active navigation with strong path match, prioritize path context heavily
            overallConfidence = (nameConfidence * 0.2) + (exitConfidence * 0.3) + 
                               (pathConfidence * 0.4) + (contextConfidence * 0.1);
            _logger.LogDebug("Using navigation-prioritized weighting for strong path match");
        }
        else if (_currentContext.HasPathContext)
        {
            // During navigation but weaker path match, balance path and exit confidence
            overallConfidence = (nameConfidence * 0.25) + (exitConfidence * 0.35) + 
                               (pathConfidence * 0.3) + (contextConfidence * 0.1);
        }
        else
        {
            // Not navigating or no path context, use original weighting
            overallConfidence = (nameConfidence * 0.3) + (exitConfidence * 0.4) + 
                               (pathConfidence * 0.25) + (contextConfidence * 0.15);
        }

        var matchType = reasons.Count > 0 ? $"Enhanced: {string.Join(", ", reasons.Take(2))}" : "Enhanced match";

        return new EnhancedRoomMatchResult(node, Math.Min(1.0, overallConfidence), matchType)
        {
            NameConfidence = nameConfidence,
            ExitConfidence = exitConfidence,
            PathConfidence = pathConfidence,
            ContextConfidence = contextConfidence,
            MatchReasons = reasons
        };
    }

    /// <summary>
    /// Gets reverse direction for validation
    /// </summary>
    private string GetReverseDirection(string direction)
    {
        return NormalizeDirection(direction) switch
        {
            "north" => "south",
            "south" => "north",
            "east" => "west",
            "west" => "east",
            "northeast" => "southwest",
            "northwest" => "southeast",
            "southeast" => "northwest",
            "southwest" => "northeast",
            "up" => "down",
            "down" => "up",
            _ => string.Empty
        };
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
        _roomVisitHistory.Clear();
        _logger.LogInformation("Room matching cache cleared");
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