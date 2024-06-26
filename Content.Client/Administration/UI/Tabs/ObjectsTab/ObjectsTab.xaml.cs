using Content.Client.Salvage;
using Content.Client.Station;
using Content.Server._NF.Worldgen.Components.Debris;
using Content.Shared.Shipyard.Components;
using Robust.Client.AutoGenerated;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Client.Administration.UI.Tabs.ObjectsTab;

[GenerateTypedNameReferences]
public sealed partial class ObjectsTab : Control
{
    [Dependency] private readonly EntityManager _entityManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly List<ObjectsTabEntry> _objects = new();
    private List<ObjectsTabSelection> _selections = new();

    public event Action<ObjectsTabEntry, GUIBoundKeyEventArgs>? OnEntryKeyBindDown;

    // Listen I could either have like 4 different event subscribers (for map / grid / station changes) and manage their lifetimes in AdminUIController
    // OR
    // I can do this.
    private TimeSpan _updateFrequency = TimeSpan.FromSeconds(2);

    private TimeSpan _nextUpdate = TimeSpan.FromSeconds(2);

    public ObjectsTab()
    {
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);

        ObjectTypeOptions.OnItemSelected += ev =>
        {
            ObjectTypeOptions.SelectId(ev.Id);
            RefreshObjectList(_selections[ev.Id]);
        };

        foreach (var type in Enum.GetValues(typeof(ObjectsTabSelection)))
        {
            _selections.Add((ObjectsTabSelection)type!);
            ObjectTypeOptions.AddItem(Enum.GetName((ObjectsTabSelection)type)!);
        }

        RefreshObjectList();
    }

    private void RefreshObjectList()
    {
        RefreshObjectList(_selections[ObjectTypeOptions.SelectedId]);
    }

    private void RefreshObjectList(ObjectsTabSelection selection)
    {
        var entities = new List<(string Name, NetEntity Entity)>();
        switch (selection)
        {
            case ObjectsTabSelection.Stations:
                entities.AddRange(_entityManager.EntitySysManager.GetEntitySystem<StationSystem>().Stations);
                break;
            case ObjectsTabSelection.Grids:
            {
                var query = _entityManager.AllEntityQueryEnumerator<MapGridComponent, MetaDataComponent>();
                while (query.MoveNext(out var uid, out _, out var metadata))
                {
                    entities.Add((metadata.EntityName, _entityManager.GetNetEntity(uid)));
                }

                break;
            }
            case ObjectsTabSelection.Maps:
            {
                var query = _entityManager.AllEntityQueryEnumerator<MapComponent, MetaDataComponent>();
                while (query.MoveNext(out var uid, out _, out var metadata))
                {
                    entities.Add((metadata.EntityName, _entityManager.GetNetEntity(uid)));
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(selection), selection, null);
        }

        foreach (var control in _objects)
        {
            ObjectList.RemoveChild(control);
        }

        _objects.Clear();

        foreach (var (name, nent) in entities)
        {
            var ctrl = new ObjectsTabEntry(name, nent);
            _objects.Add(ctrl);
            ObjectList.AddChild(ctrl);
            ctrl.OnKeyBindDown += args => OnEntryKeyBindDown?.Invoke(ctrl, args);
        }

        var shuttlesCount = 0;
        var shuttlesQuery = _entityManager.AllEntityQueryEnumerator<ShuttleDeedComponent, MapGridComponent>();
        while (shuttlesQuery.MoveNext(out var uid, out _, out _))
        {
            shuttlesCount++;
        }

        ShuttlesCount.Text = $"Шаттлы: {shuttlesCount}";

        var debrisCount = 0;
        var debrisQuery = _entityManager.AllEntityQueryEnumerator<SpaceDebrisComponent, MetaDataComponent>();
        while (debrisQuery.MoveNext(out var uid, out _, out _))
        {
            debrisCount++;
        }

        DebrisCount.Text = $"Обломки: {debrisCount}";

        var expeditionCount = 0;
        var expeditionQuery = _entityManager.AllEntityQueryEnumerator<SalvageExpeditionComponent, MetaDataComponent>();
        while (expeditionQuery.MoveNext(out var uid, out _, out _))
        {
            expeditionCount++;
        }

        ExpeditionCount.Text = $"Экспедиции: {expeditionCount}";
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (_timing.CurTime < _nextUpdate)
            return;

        // I do not care for precision.
        _nextUpdate = _timing.CurTime + _updateFrequency;

        RefreshObjectList();
    }

    private enum ObjectsTabSelection
    {
        Grids,
        Maps,
        Stations,
    }
}

