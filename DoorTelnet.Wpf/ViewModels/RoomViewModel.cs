using System;
using System.Collections.ObjectModel;
using System.Linq;
using DoorTelnet.Core.World;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DoorTelnet.Wpf.ViewModels;

/// <summary>
/// Stage 4: Room information ViewModel.
/// Simplified: removed directional grid/mini-map support.
/// </summary>
public class RoomViewModel : ViewModelBase
{
    private readonly RoomTracker _roomTracker;

    public class MonsterDisplay
    {
        public string Name { get; set; } = string.Empty;
        public string Disposition { get; set; } = "neutral";
        public bool IsAggressive => string.Equals(Disposition, "aggressive", StringComparison.OrdinalIgnoreCase);
        public bool TargetingYou { get; set; }
        public int? Count { get; set; }
        public string DisplayName => Count.HasValue && Count.Value > 1 ? $"{Name} x{Count}" : Name;
    }

    public RoomViewModel(RoomTracker roomTracker, ILogger<RoomViewModel> logger) : base(logger)
    {
        _roomTracker = roomTracker;
        Monsters = new ObservableCollection<MonsterDisplay>();
        Items = new ObservableCollection<string>();
        Exits = new ObservableCollection<string>();
        Refresh();
    }

    private string _roomName = "Unknown"; public string RoomName { get => _roomName; private set => SetProperty(ref _roomName, value); }
    private DateTime _lastUpdated; public DateTime LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }

    public ObservableCollection<string> Exits { get; }
    public ObservableCollection<MonsterDisplay> Monsters { get; }
    public ObservableCollection<string> Items { get; }

    public int MonsterCount => Monsters.Count;
    public bool HasAggressive => Monsters.Any(m => m.IsAggressive || m.TargetingYou);

    public void Refresh()
    {
        var room = _roomTracker.CurrentRoom;
        if (room == null)
        {
            RoomName = "Unknown";
            ClearCollections();
            return;
        }

        RoomName = string.IsNullOrWhiteSpace(room.Name) ? "(No Name)" : room.Name;
        LastUpdated = room.LastUpdated;

        UpdateCollection(Exits, room.Exits);
        UpdateMonsterCollection(room);
        UpdateCollection(Items, room.Items);

        OnPropertyChanged(nameof(MonsterCount));
        OnPropertyChanged(nameof(HasAggressive));
    }

    private void ClearCollections()
    {
        Exits.Clear();
        Monsters.Clear();
        Items.Clear();
    }

    private void UpdateMonsterCollection(RoomState room)
    {
        // remove stale
        for (int i = Monsters.Count - 1; i >= 0; i--)
        {
            var existing = Monsters[i];
            if (!room.Monsters.Any(m => NamesMatch(m.Name, existing.Name)))
            {
                Monsters.RemoveAt(i);
            }
        }
        // add/update
        foreach (var m in room.Monsters)
        {
            var match = Monsters.FirstOrDefault(x => NamesMatch(m.Name, x.Name));
            if (match == null)
            {
                Monsters.Add(new MonsterDisplay
                {
                    Name = m.Name,
                    Disposition = m.Disposition,
                    TargetingYou = m.TargetingYou,
                    Count = m.Count
                });
            }
            else
            {
                match.Disposition = m.Disposition;
                match.TargetingYou = m.TargetingYou;
                match.Count = m.Count;
            }
        }
    }

    private static bool NamesMatch(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void UpdateCollection(ObservableCollection<string> col, System.Collections.Generic.IEnumerable<string> src)
    {
        var arr = src.ToList();
        for (int i = col.Count - 1; i >= 0; i--)
            if (!arr.Contains(col[i])) col.RemoveAt(i);
        foreach (var s in arr)
            if (!col.Contains(s)) col.Add(s);
    }
}
