namespace BpmMeasurer;

/// <summary>Stub of MainWindow.Loc() for parser tests. Returns the key itself as the fallback value.</summary>
public static class MainWindowStub
{
    public static string Loc(string key) => key;
}

/// <summary>Mimics the partial class MainWindow to satisfy TimingConfigParser's static Loc() call.</summary>
public partial class MainWindow
{
    public static string Loc(string key) => MainWindowStub.Loc(key);
}
