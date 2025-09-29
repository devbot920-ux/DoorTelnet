using DoorTelnet.Core.Navigation.Algorithms;
using DoorTelnet.Core.Navigation.Models;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// Service for calculating optimal navigation paths between rooms
/// </summary>
public class PathfindingService
{
    private readonly GraphDataService _graphData;
    private readonly ILogger<PathfindingService> _logger;
    private readonly object _sync = new();
    
    // Path cache to improve performance for frequently requested routes
    private readonly Dictionary<string, CachedPath> _pathCache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(30);
    private const int MaxCacheSize = 1000;

    public PathfindingService(GraphDataService graphData, ILogger<PathfindingService> logger)
    {
        _graphData = graphData;
        _logger = logger;
    }

    /// <summary>
    /// Calculates the optimal path between two rooms
    /// </summary>
    public NavigationPath CalculatePath(NavigationRequest request)
    {
        try
        {
            if (!_graphData.IsLoaded)
            {
                return NavigationPath.Failed(request.FromRoomId, request.ToRoomId, 
                    "Graph data not loaded");
            }

            // Check cache first
            var cacheKey = GetCacheKey(request);
            var cachedPath = GetCachedPath(cacheKey);
            if (cachedPath != null)
            {
                _logger.LogDebug("Using cached path from {From} to {To}", 
                    request.FromRoomId, request.ToRoomId);
                return cachedPath;
            }

            _logger.LogInformation("Calculating path from {From} to {To}", 
                request.FromRoomId, request.ToRoomId);

            var path = AStar.FindPath(
                request.FromRoomId,
                request.ToRoomId,
                _graphData.GetNode,
                _graphData.GetOutgoingEdges,
                request.Constraints);

            if (path.IsValid)
            {
                _logger.LogInformation("Found path from {From} to {To}: {Steps} steps, cost {Cost}", 
                    request.FromRoomId, request.ToRoomId, path.StepCount, path.TotalCost);
                
                // Cache successful paths
                CachePath(cacheKey, path);
            }
            else
            {
                _logger.LogWarning("Failed to find path from {From} to {To}: {Error}", 
                    request.FromRoomId, request.ToRoomId, path.ErrorReason);
            }

            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating path from {From} to {To}", 
                request.FromRoomId, request.ToRoomId);
            
            return NavigationPath.Failed(request.FromRoomId, request.ToRoomId, 
                $"Pathfinding error: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the shortest path between two rooms with default constraints
    /// </summary>
    public NavigationPath FindShortestPath(string fromRoomId, string toRoomId)
    {
        var request = new NavigationRequest
        {
            FromRoomId = fromRoomId,
            ToRoomId = toRoomId,
            Constraints = new NavigationConstraints()
        };

        return CalculatePath(request);
    }

    /// <summary>
    /// Finds a safe path between two rooms, avoiding dangerous areas
    /// </summary>
    public NavigationPath FindSafePath(string fromRoomId, string toRoomId, int playerLevel = 1)
    {
        var request = new NavigationRequest
        {
            FromRoomId = fromRoomId,
            ToRoomId = toRoomId,
            Constraints = new NavigationConstraints
            {
                AvoidDangerousRooms = true,
                AvoidTraps = true,
                PlayerLevel = playerLevel,
                MaxDangerLevel = Math.Max(1, playerLevel / 5) // Scale danger tolerance with level
            }
        };

        return CalculatePath(request);
    }

    /// <summary>
    /// Finds the absolute shortest path between two rooms with no constraints (for distance calculation only)
    /// </summary>
    public NavigationPath FindAbsoluteShortestPath(string fromRoomId, string toRoomId)
    {
        var request = new NavigationRequest
        {
            FromRoomId = fromRoomId,
            ToRoomId = toRoomId,
            Constraints = new NavigationConstraints
            {
                AvoidDangerousRooms = false,  // Don't avoid dangerous rooms for distance calculation
                AvoidTraps = false,           // Don't avoid traps for distance calculation
                AvoidHiddenExits = false,
                AvoidDoors = false,
                MaxPathLength = 200,          // Higher limit for true shortest path
                MaxDangerLevel = int.MaxValue // No danger level limit
            }
        };

        return CalculatePath(request);
    }

    /// <summary>
    /// Validates if a path is still valid (rooms and edges still exist)
    /// </summary>
    public bool ValidatePath(NavigationPath path)
    {
        if (!path.IsValid || !_graphData.IsLoaded)
            return false;

        try
        {
            // Check if all rooms in the path still exist
            var fromRoom = _graphData.GetNode(path.FromRoomId);
            var toRoom = _graphData.GetNode(path.ToRoomId);

            if (fromRoom == null || toRoom == null)
                return false;

            // Check if all steps are still valid
            foreach (var step in path.Steps)
            {
                var stepFromRoom = _graphData.GetNode(step.FromRoomId);
                var stepToRoom = _graphData.GetNode(step.ToRoomId);

                if (stepFromRoom == null || stepToRoom == null)
                    return false;

                // Check if the edge still exists
                var outgoingEdges = _graphData.GetOutgoingEdges(step.FromRoomId);
                var edgeExists = outgoingEdges.Any(e => 
                    e.Target == step.ToRoomId && 
                    e.Direction.Equals(step.Direction, StringComparison.OrdinalIgnoreCase));

                if (!edgeExists)
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating path from {From} to {To}", 
                path.FromRoomId, path.ToRoomId);
            return false;
        }
    }

    /// <summary>
    /// Clears the path cache
    /// </summary>
    public void ClearCache()
    {
        lock (_sync)
        {
            _pathCache.Clear();
            _logger.LogInformation("Path cache cleared");
        }
    }

    /// <summary>
    /// Gets cache statistics for debugging
    /// </summary>
    public (int Count, int Hits, int Misses) GetCacheStats()
    {
        lock (_sync)
        {
            var totalHits = _pathCache.Values.Sum(p => p.HitCount);
            return (_pathCache.Count, totalHits, 0); // TODO: Track misses if needed
        }
    }

    private string GetCacheKey(NavigationRequest request)
    {
        // Create a cache key that includes constraints
        var constraints = request.Constraints;
        return $"{request.FromRoomId}->{request.ToRoomId}" +
               $"|danger:{constraints.AvoidDangerousRooms}" +
               $"|traps:{constraints.AvoidTraps}" +
               $"|hidden:{constraints.AvoidHiddenExits}" +
               $"|doors:{constraints.AvoidDoors}" +
               $"|level:{constraints.PlayerLevel}" +
               $"|maxdanger:{constraints.MaxDangerLevel}";
    }

    private NavigationPath? GetCachedPath(string cacheKey)
    {
        lock (_sync)
        {
            if (_pathCache.TryGetValue(cacheKey, out var cached))
            {
                if (DateTime.UtcNow - cached.CachedAt < _cacheExpiry)
                {
                    cached.HitCount++;
                    return cached.Path;
                }
                else
                {
                    _pathCache.Remove(cacheKey);
                }
            }

            return null;
        }
    }

    private void CachePath(string cacheKey, NavigationPath path)
    {
        lock (_sync)
        {
            // Remove oldest entries if cache is full
            if (_pathCache.Count >= MaxCacheSize)
            {
                var oldestKey = _pathCache
                    .OrderBy(kvp => kvp.Value.CachedAt)
                    .First().Key;
                _pathCache.Remove(oldestKey);
            }

            _pathCache[cacheKey] = new CachedPath(path, DateTime.UtcNow);
        }
    }

    private class CachedPath
    {
        public NavigationPath Path { get; }
        public DateTime CachedAt { get; }
        public int HitCount { get; set; }

        public CachedPath(NavigationPath path, DateTime cachedAt)
        {
            Path = path;
            CachedAt = cachedAt;
            HitCount = 0;
        }
    }
}