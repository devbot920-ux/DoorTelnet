using System.Text.Json;
using System.Text.Json.Serialization;
using DoorTelnet.Core.Navigation.Models;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// Service responsible for loading and managing the room graph data from JSON
/// </summary>
public class GraphDataService
{
    private readonly ILogger<GraphDataService> _logger;
    private readonly object _sync = new();
    
    private Dictionary<string, GraphNode> _nodes = new();
    private Dictionary<string, List<GraphEdge>> _outgoingEdges = new();
    private Dictionary<string, List<GraphEdge>> _incomingEdges = new();
    private Dictionary<string, string> _regionNames = new();
    
    private bool _isLoaded = false;

    public GraphDataService(ILogger<GraphDataService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the total number of nodes in the graph
    /// </summary>
    public int NodeCount 
    { 
        get 
        { 
            lock (_sync) 
                return _nodes.Count; 
        } 
    }

    /// <summary>
    /// Gets the total number of edges in the graph
    /// </summary>
    public int EdgeCount 
    { 
        get 
        { 
            lock (_sync) 
                return _outgoingEdges.Values.Sum(edges => edges.Count); 
        } 
    }

    /// <summary>
    /// Indicates whether the graph data has been successfully loaded
    /// </summary>
    public bool IsLoaded 
    { 
        get 
        { 
            lock (_sync) 
                return _isLoaded; 
        } 
    }

    /// <summary>
    /// Loads graph data from the specified JSON file path
    /// </summary>
    public async Task<bool> LoadGraphDataAsync(string jsonFilePath)
    {
        try
        {
            if (!File.Exists(jsonFilePath))
            {
                _logger.LogError("Graph JSON file not found: {FilePath}", jsonFilePath);
                return false;
            }

            _logger.LogInformation("Loading graph data from: {FilePath}", jsonFilePath);
            
            var jsonContent = await File.ReadAllTextAsync(jsonFilePath);
            var graphData = JsonSerializer.Deserialize<GraphData>(jsonContent);

            if (graphData == null)
            {
                _logger.LogError("Failed to deserialize graph data from JSON");
                return false;
            }

            lock (_sync)
            {
                ProcessGraphData(graphData);
                _isLoaded = true;
            }

            _logger.LogInformation("Successfully loaded graph data: {NodeCount} nodes, {EdgeCount} edges, {RegionCount} regions", 
                NodeCount, EdgeCount, _regionNames.Count);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load graph data from {FilePath}", jsonFilePath);
            return false;
        }
    }

    /// <summary>
    /// Gets a room node by ID
    /// </summary>
    public GraphNode? GetNode(string roomId)
    {
        if (string.IsNullOrEmpty(roomId))
            return null;

        lock (_sync)
        {
            return _nodes.TryGetValue(roomId, out var node) ? node : null;
        }
    }

    /// <summary>
    /// Gets all outgoing edges from a room
    /// </summary>
    public List<GraphEdge> GetOutgoingEdges(string roomId)
    {
        if (string.IsNullOrEmpty(roomId))
            return new List<GraphEdge>();

        lock (_sync)
        {
            return _outgoingEdges.TryGetValue(roomId, out var edges) ? 
                edges.ToList() : new List<GraphEdge>();
        }
    }

    /// <summary>
    /// Gets all incoming edges to a room
    /// </summary>
    public List<GraphEdge> GetIncomingEdges(string roomId)
    {
        if (string.IsNullOrEmpty(roomId))
            return new List<GraphEdge>();

        lock (_sync)
        {
            return _incomingEdges.TryGetValue(roomId, out var edges) ? 
                edges.ToList() : new List<GraphEdge>();
        }
    }

    /// <summary>
    /// Gets the human-readable name for a region ID
    /// </summary>
    public string GetRegionName(string regionId)
    {
        if (string.IsNullOrEmpty(regionId))
            return "Unknown";

        lock (_sync)
        {
            return _regionNames.TryGetValue(regionId, out var name) ? name : regionId;
        }
    }

    /// <summary>
    /// Finds rooms matching the given search criteria
    /// </summary>
    public List<GraphNode> FindRooms(Func<GraphNode, bool> predicate, int maxResults = 50)
    {
        lock (_sync)
        {
            return _nodes.Values
                .Where(predicate)
                .Take(maxResults)
                .ToList();
        }
    }

    /// <summary>
    /// Gets all regions and their names
    /// </summary>
    public Dictionary<string, string> GetAllRegions()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>(_regionNames);
        }
    }

    private void ProcessGraphData(GraphData graphData)
    {
        // Clear existing data
        _nodes.Clear();
        _outgoingEdges.Clear();
        _incomingEdges.Clear();
        _regionNames.Clear();

        // Load region names
        if (graphData.RegionNames != null)
        {
            foreach (var kvp in graphData.RegionNames)
            {
                _regionNames[kvp.Key] = kvp.Value;
            }
        }

        // Load nodes
        if (graphData.Nodes != null)
        {
            foreach (var node in graphData.Nodes)
            {
                if (!string.IsNullOrEmpty(node.Id))
                {
                    _nodes[node.Id] = node;
                    _outgoingEdges[node.Id] = new List<GraphEdge>();
                    _incomingEdges[node.Id] = new List<GraphEdge>();
                }
            }
        }

        // Load edges
        if (graphData.Edges != null)
        {
            foreach (var edge in graphData.Edges)
            {
                if (!string.IsNullOrEmpty(edge.Source) && !string.IsNullOrEmpty(edge.Target))
                {
                    // Add to outgoing edges from source
                    if (_outgoingEdges.ContainsKey(edge.Source))
                    {
                        _outgoingEdges[edge.Source].Add(edge);
                    }

                    // Add to incoming edges to target
                    if (_incomingEdges.ContainsKey(edge.Target))
                    {
                        _incomingEdges[edge.Target].Add(edge);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Internal class for JSON deserialization
    /// </summary>
    private class GraphData
    {
        [JsonPropertyName("regionNames")]
        public Dictionary<string, string>? RegionNames { get; set; }

        [JsonPropertyName("nodes")]
        public List<GraphNode>? Nodes { get; set; }

        [JsonPropertyName("edges")]
        public List<GraphEdge>? Edges { get; set; }
    }
}