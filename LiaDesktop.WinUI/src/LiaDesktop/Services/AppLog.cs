namespace LiaDesktop.Services;

public sealed class AppLog
{
    readonly List<(string Line, string? Detail)> _lines = [];
    public event Action<(string Line, string? Detail)>? Added;

    public IReadOnlyList<(string Line, string? Detail)> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public void Info(string line) => Push(line, null);
    public void Error(string line, string? detail = null) => Push(line, detail);

    void Push(string line, string? detail)
    {
        lock (_lines)
        {
            _lines.Add((line, detail));
            if (_lines.Count > 200) _lines.RemoveAt(0);
        }
        Added?.Invoke((line, detail));
    }
}
