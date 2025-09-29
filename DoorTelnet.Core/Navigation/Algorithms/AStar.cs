using DoorTelnet.Core.Navigation.Models;

namespace DoorTelnet.Core.Navigation.Algorithms;

/// <summary>
/// A* pathfinding algorithm implementation for room navigation
/// </summary>
public static class AStar
{
    /// <summary>
    /// Finds the optimal path between two rooms using A* algorithm
    /// </summary>
    public static NavigationPath FindPath(
        string startRoomId, 
        string targetRoomId,
        Func<string, GraphNode?> getNode,
        Func<string, List<GraphEdge>> getOutgoingEdges,
        NavigationConstraints? constraints = null)
    {
        constraints ??= new NavigationConstraints();

        var startNode = getNode(startRoomId);
        var targetNode = getNode(targetRoomId);

        if (startNode == null)
            return NavigationPath.Failed(startRoomId, targetRoomId, $"Start room '{startRoomId}' not found");

        if (targetNode == null)
            return NavigationPath.Failed(startRoomId, targetRoomId, $"Target room '{targetRoomId}' not found");

        if (startRoomId == targetRoomId)
            return NavigationPath.Success(startRoomId, targetRoomId, new List<NavigationStep>(), 0, 0);

        // Check if target room should be avoided
        if (constraints.ShouldAvoidRoom(targetNode))
            return NavigationPath.Failed(startRoomId, targetRoomId, "Target room violates navigation constraints");

        var openSet = new PriorityQueue<PathNode, double>();
        var closedSet = new HashSet<string>();
        var gScoreMap = new Dictionary<string, double>();
        var cameFrom = new Dictionary<string, (string roomId, GraphEdge edge)>();

        var startPathNode = new PathNode(startRoomId, 0, CalculateHeuristic(startNode, targetNode));
        openSet.Enqueue(startPathNode, startPathNode.FScore);
        gScoreMap[startRoomId] = 0;

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();

            if (current.RoomId == targetRoomId)
            {
                return ReconstructPath(startRoomId, targetRoomId, cameFrom, gScoreMap[targetRoomId], getNode);
            }

            closedSet.Add(current.RoomId);

            var currentRoom = getNode(current.RoomId);
            if (currentRoom == null) continue;

            var outgoingEdges = getOutgoingEdges(current.RoomId);

            foreach (var edge in outgoingEdges)
            {
                var neighborId = edge.Target;

                if (closedSet.Contains(neighborId))
                    continue;

                var neighborRoom = getNode(neighborId);
                if (neighborRoom == null) continue;

                // Apply constraint checks
                if (constraints.ShouldAvoidRoom(neighborRoom) || constraints.ShouldAvoidEdge(edge))
                    continue;

                var movementCost = edge.GetMovementCost() + constraints.GetRoomCostPenalty(neighborRoom);
                var tentativeGScore = gScoreMap[current.RoomId] + movementCost;

                if (!gScoreMap.ContainsKey(neighborId) || tentativeGScore < gScoreMap[neighborId])
                {
                    cameFrom[neighborId] = (current.RoomId, edge);
                    gScoreMap[neighborId] = tentativeGScore;

                    var heuristic = CalculateHeuristic(neighborRoom, targetNode);
                    var fScore = tentativeGScore + heuristic;

                    var neighborPathNode = new PathNode(neighborId, tentativeGScore, fScore);
                    openSet.Enqueue(neighborPathNode, fScore);
                }
            }

            // Prevent infinite loops by limiting search depth
            if (closedSet.Count > constraints.MaxPathLength * 2)
            {
                return NavigationPath.Failed(startRoomId, targetRoomId, "Search exceeded maximum depth limit");
            }
        }

        return NavigationPath.Failed(startRoomId, targetRoomId, "No path found between rooms");
    }

    /// <summary>
    /// Calculates the heuristic distance between two rooms using Euclidean distance
    /// </summary>
    private static double CalculateHeuristic(GraphNode from, GraphNode to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Reconstructs the path from the A* search results
    /// </summary>
    private static NavigationPath ReconstructPath(
        string startRoomId, 
        string targetRoomId,
        Dictionary<string, (string roomId, GraphEdge edge)> cameFrom,
        double totalDistance,
        Func<string, GraphNode?> getNode)
    {
        var steps = new List<NavigationStep>();
        var currentRoomId = targetRoomId;
        var totalCost = 0;

        // Trace back through the path
        while (cameFrom.ContainsKey(currentRoomId))
        {
            var (previousRoomId, edge) = cameFrom[currentRoomId];
            var fromRoom = getNode(previousRoomId);
            var toRoom = getNode(currentRoomId);

            var step = NavigationStep.FromEdge(edge, fromRoom, toRoom);
            steps.Insert(0, step); // Insert at beginning to reverse the order
            totalCost += step.MovementCost;

            currentRoomId = previousRoomId;
        }

        return NavigationPath.Success(startRoomId, targetRoomId, steps, totalDistance, totalCost);
    }

    /// <summary>
    /// Internal class representing a node in the A* search
    /// </summary>
    private class PathNode
    {
        public string RoomId { get; }
        public double GScore { get; }
        public double FScore { get; }

        public PathNode(string roomId, double gScore, double fScore)
        {
            RoomId = roomId;
            GScore = gScore;
            FScore = fScore;
        }
    }
}