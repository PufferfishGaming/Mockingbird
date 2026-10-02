using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The keys that work in every program, without touching Windows: what was registered and paused is recorded, and what other programs use is set by the test.</summary>
internal sealed class FakeHotkeys : IHotkeys
{
    public Dictionary<string, KeyCombo> Registered { get; } = [];
    public Dictionary<string, Action> Handlers { get; } = [];
    public Dictionary<string, KeyCombo> Claims { get; } = [];
    public List<(string Action, KeyCombo Combo)> Registrations { get; } = [];
    public HashSet<KeyCombo> TakenByOthers { get; } = [];

    /// <summary>What a registration answers; false is "another program has these keys".</summary>
    public bool RegisterResult { get; set; } = true;

    public int PauseCalls { get; private set; }
    public int Paused { get; private set; }

    public bool Register(string action, KeyCombo combo, Action pressed)
    {
        Claim(action, combo);
        Registrations.Add((action, combo));
        if (combo.IsNone) { Registered.Remove(action); Handlers.Remove(action); return true; }
        if (!RegisterResult) return false;
        Registered[action] = combo; Handlers[action] = pressed;
        return true;
    }

    public void Unregister(string action) { Registered.Remove(action); Handlers.Remove(action); }

    public void Claim(string action, KeyCombo combo) { if (combo.IsNone) Claims.Remove(action); else Claims[action] = combo; }

    public bool IsFree(KeyCombo combo, string action) =>
        !TakenByOthers.Contains(combo) && !Claims.Any(claim => claim.Key != action && claim.Value == combo);

    public IDisposable Pause()
    {
        PauseCalls++; Paused++;
        return new Resume(this);
    }

    /// <summary>The person presses the keys of an action.</summary>
    public void Press(string action) => Handlers[action]();

    private sealed class Resume(FakeHotkeys owner) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; owner.Paused--; }
    }

    /// <summary>The person chooses keys the way the window does: Change keys, press them, Done.</summary>
    public static void Choose(KeybindViewModel keys, KeyCombo combo)
    {
        keys.BeginCommand.Execute(null);
        keys.Press(combo.Key, combo.Modifiers);
        keys.DoneCommand.Execute(null);
    }
}
