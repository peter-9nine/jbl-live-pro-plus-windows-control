using System.ComponentModel;

namespace LiveProPlusControl;

/// <summary>Un gesto configurable (p.ej. "doble toque izquierdo") y su accion.</summary>
public sealed class GestureSlot : INotifyPropertyChanged
{
    bool _fromDevice;
    GestureAction? _selected;

    public GestureSlot(byte id, string label)
    {
        Id = id;
        Label = label;
        Actions = [.. Protocol.GestureActions];
    }

    public byte Id { get; }
    public string Label { get; }
    public List<GestureAction> Actions { get; }

    /// <summary>Se dispara cuando el usuario elige otra accion.</summary>
    public event Action<GestureSlot>? UserChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public GestureAction? Selected
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value) || value is null) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            if (!_fromDevice) UserChanged?.Invoke(this);
        }
    }

    /// <summary>Actualiza desde los auriculares sin reenviar el comando.</summary>
    public void SetFromDevice(byte action)
    {
        var a = Actions.FirstOrDefault(x => x.Value == action);
        if (a is null)
        {
            a = new GestureAction(action, $"Other (0x{action:X2})");
            Actions.Add(a);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Actions)));
        }
        _fromDevice = true;
        try { Selected = a; }
        finally { _fromDevice = false; }
    }
}
