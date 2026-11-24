namespace CssLauncher.Core;

/// <summary>Prevents overlapping launch requests while the game process is starting or running.</summary>
public sealed class LaunchGate
{
    private int _isLaunching;

    public bool IsLaunching => Volatile.Read(ref _isLaunching) != 0;

    public bool TryBegin() => Interlocked.CompareExchange(ref _isLaunching, 1, 0) == 0;

    public void Complete() => Volatile.Write(ref _isLaunching, 0);
}
