using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using BinScope.Core.Diff;
using BinScope.Core.Model;
using BinScope.Core.Plugins;

namespace BinScope.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

/// <summary>One entry of the plugin stack.</summary>
public sealed class PluginItem(StackEntry entry, MainViewModel owner) : ObservableObject
{
    public IBinaryPlugin Plugin => entry.Plugin;
    public string Name => entry.Plugin.Name;
    public string Info => $"{entry.Plugin.Id} · {(entry.Plugin is IWritablePlugin ? "read/write" : "read-only")}";
    public string Description => entry.Plugin.Description;

    public bool Enabled
    {
        get => entry.Enabled;
        set
        {
            if (entry.Enabled == value) return;
            owner.Registry.SetEnabled(entry.Plugin.Id, value);
            Raise();
            owner.Reload();
        }
    }

    private string _reads = "";
    /// <summary>"A", "B" or "A B": which of the open files this plugin reads.</summary>
    public string Reads { get => _reads; set => Set(ref _reads, value); }

    public ICommand Up => new RelayCommand(() => owner.MovePlugin(this, -1));
    public ICommand Down => new RelayCommand(() => owner.MovePlugin(this, 1));
}

/// <summary>A compare filter with its on/off state and whether it applies to the open files.</summary>
public sealed class FilterItem(IDiffFilter filter, MainViewModel owner) : ObservableObject
{
    public IDiffFilter Filter => filter;
    public string Name => filter.Name;
    public string Description => filter.Description;

    private bool _isChecked = filter.ByDefault;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) owner.Refresh();
        }
    }

    private bool _isApplicable = true;
    public bool IsApplicable { get => _isApplicable; set => Set(ref _isApplicable, value); }

    private int _hidden;
    public int Hidden { get => _hidden; set { if (Set(ref _hidden, value)) Raise(nameof(HiddenText)); } }
    public string HiddenText => _hidden > 0 ? _hidden.ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>One row of the compare tree.</summary>
public sealed class DiffItem(DiffNode node, bool compare) : ObservableObject
{
    private static readonly Brush ChangedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xCD)));
    private static readonly Brush AddedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xF7, 0xE8)));
    private static readonly Brush RemovedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xE4, 0xE6)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public DiffNode Node => node;
    public string Label => node.IgnoredBy is null ? node.Label : $"{node.Label}  [{node.IgnoredBy}]";
    public string LeftValue => Show(node.Left);
    public string RightValue => compare ? Show(node.Right) : "";
    public ObservableCollection<DiffItem> Children { get; } = new();

    public Brush Background => !compare ? Brushes.Transparent : node.Status switch
    {
        DiffStatus.Changed => ChangedBrush,
        DiffStatus.Added => AddedBrush,
        DiffStatus.Removed => RemovedBrush,
        _ => Brushes.Transparent,
    };

    public Brush Foreground => node.IgnoredBy is null ? Brushes.Black : Brushes.Gray;

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private static string Show(TreeNode? n) =>
        n is null ? "—" : n.Value is null ? "" : n.IsText ? $"{n.Value.Split('\n').Length} lines of source" : n.Value;
}

public sealed class MainViewModel : ObservableObject
{
    public PluginRegistry Registry { get; } = new();
    public ObservableCollection<PluginItem> Plugins { get; } = new();
    public ObservableCollection<FilterItem> Filters { get; } = new();
    public ObservableCollection<DiffItem> Tree { get; } = new();

    private LoadedFile? _left;
    private LoadedFile? _right;
    private bool _leftEdited;
    private DiffNode? _lastRoot;

    public MainViewModel()
    {
        foreach (var e in Registry.Stack) Plugins.Add(new PluginItem(e, this));
        foreach (var f in DiffEngine.Filters) Filters.Add(new FilterItem(f, this));
    }

    private string _leftTitle = "A · drop a .plcbin file here or use Open";
    public string LeftTitle { get => _leftTitle; set => Set(ref _leftTitle, value); }

    private string _rightTitle = "B · drop a second file to compare";
    public string RightTitle { get => _rightTitle; set => Set(ref _rightTitle, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private string _message = "";
    public string Message { get => _message; set => Set(ref _message, value); }

    private string _detail = "Select an element to see its bytes, the source diff, or to edit it.";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private string _editValue = "";
    public string EditValue { get => _editValue; set => Set(ref _editValue, value); }

    private string _editHint = "";
    public string EditHint { get => _editHint; set => Set(ref _editHint, value); }

    private bool _canEdit;
    public bool CanEdit { get => _canEdit; set => Set(ref _canEdit, value); }

    private bool _canSave;
    public bool CanSave { get => _canSave; set => Set(ref _canSave, value); }

    private bool _onlyDifferences;
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (Set(ref _onlyDifferences, value)) Refresh();
        }
    }

    private DiffItem? _selected;
    public DiffItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) ShowDetail();
        }
    }

    public bool HasRight => _right is not null;

    // ------------------------------------------------------------------ files

    public void Open(bool left, string path)
    {
        try
        {
            var f = Registry.Load(File.ReadAllBytes(path), Path.GetFileName(path));
            if (left)
            {
                _left = f;
                _leftEdited = false;
            }
            else _right = f;
            Message = "";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
        Refresh();
    }

    public void CloseRight()
    {
        _right = null;
        Refresh();
    }

    /// <summary>Re-read both files after the plugin stack changed.</summary>
    public void Reload()
    {
        try
        {
            if (_left is not null) _left = Registry.Load(_left.Bytes, _left.FileName);
            if (_right is not null) _right = Registry.Load(_right.Bytes, _right.FileName);
        }
        catch (InvalidDataException e)
        {
            Message = e.Message;
        }
        Refresh();
    }

    public void MovePlugin(PluginItem item, int delta)
    {
        Registry.Move(item.Plugin.Id, delta);
        var i = Plugins.IndexOf(item);
        var j = i + delta;
        if (i >= 0 && j >= 0 && j < Plugins.Count) Plugins.Move(i, j);
        Reload();
    }

    // ------------------------------------------------------------------ compare

    public void Refresh()
    {
        Raise(nameof(HasRight));
        LeftTitle = _left is null ? "A · drop a .plcbin file here or use Open" : Title("A", _left) + (_leftEdited ? " (edited)" : "");
        RightTitle = _right is null ? "B · drop a second file to compare" : Title("B", _right);
        CanSave = _leftEdited && _left?.Plugin is IWritablePlugin;

        var applicable = DiffEngine.ApplicableFilters(_left?.Result.Version, _right?.Result.Version).Select(f => f.Id).ToHashSet();
        foreach (var f in Filters) f.IsApplicable = applicable.Contains(f.Filter.Id) || (_left?.Result.Version is null && _right?.Result.Version is null);
        foreach (var p in Plugins)
        {
            var reads = new List<string>();
            if (_left?.Plugin == p.Plugin) reads.Add("A");
            if (_right?.Plugin == p.Plugin) reads.Add("B");
            p.Reads = string.Join(' ', reads);
        }

        Tree.Clear();
        Selected = null;
        if (_left is null)
        {
            Summary = "";
            return;
        }
        var compare = _right is not null;
        var active = compare ? Filters.Where(f => f.IsChecked && f.IsApplicable).Select(f => f.Filter).ToList() : new List<IDiffFilter>();
        var result = DiffEngine.Compare(_left.Result.Tree, (_right ?? _left).Result.Tree, active);
        _lastRoot = result.Root;

        var hidden = new Dictionary<string, int>();
        void Count(DiffNode d, bool parent)
        {
            if (d.IgnoredBy is not null && !parent) hidden[d.IgnoredBy] = hidden.GetValueOrDefault(d.IgnoredBy) + 1;
            foreach (var c in d.Children) Count(c, parent || d.IgnoredBy is not null);
        }
        Count(result.Root, false);
        foreach (var f in Filters) f.Hidden = hidden.GetValueOrDefault(f.Filter.Id);

        var s = result.Stats;
        Summary = compare
            ? $"{s.Changed} changed · {s.Added} added · {s.Removed} removed · {s.Ignored} hidden by filters{(s.Identical ? "  ✔ identical with the active filters" : "")}"
            : "Viewing one file - open a second one to compare";
        Tree.Add(Build(result.Root, compare, 0));
    }

    private static string Title(string side, LoadedFile f) =>
        $"{side} · {f.FileName} - {f.Bytes.Length} bytes · {f.Plugin.Name}{(f.Result.Version is int v ? $" · v{v}" : "")}"
        + string.Concat(f.Result.Warnings.Select(w => $"{Environment.NewLine}⚠ {w}"));

    private DiffItem Build(DiffNode d, bool compare, int depth)
    {
        var item = new DiffItem(d, compare) { IsExpanded = depth == 0 || (compare && d.Status == DiffStatus.Changed && d.Children.Count > 0) };
        foreach (var c in d.Children)
        {
            if (OnlyDifferences && compare && c.Status == DiffStatus.Same && c.IgnoredBy is null) continue;
            item.Children.Add(Build(c, compare, depth + 1));
        }
        return item;
    }

    // ------------------------------------------------------------------ detail and editing

    private void ShowDetail()
    {
        CanEdit = false;
        EditHint = "";
        EditValue = "";
        var d = _selected?.Node;
        if (d is null || _left is null)
        {
            Detail = "Select an element to see its bytes, the source diff, or to edit it.";
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine(d.PathText.Length > 0 ? d.PathText : "file");
        if (_right is not null) sb.AppendLine(d.IgnoredBy is not null ? $"different, hidden by filter \"{d.IgnoredBy}\"" : d.Status.ToString().ToLowerInvariant());
        if (_right is not null && d.Left is { IsText: true } l && d.Right is { IsText: true } r)
        {
            sb.AppendLine().AppendLine("Source diff:");
            foreach (var op in DiffEngine.LineDiff(l.Value ?? "", r.Value ?? "")) sb.Append(op.Op).Append(' ').AppendLine(op.Text);
        }
        else if (_right is null && d.Left is { IsText: true } src)
        {
            sb.AppendLine().AppendLine(src.Value);
        }
        AppendBytes(sb, "A", _left, d.Left);
        if (_right is not null) AppendBytes(sb, "B", _right, d.Right);
        Detail = sb.ToString();

        if (_right is null && d.Left?.Edit is { } edit && _left.Plugin is IWritablePlugin && !d.Left.IsText)
        {
            CanEdit = true;
            EditHint = $"Edit ({edit.Hint})";
            EditValue = d.Left.Value ?? "";
        }
    }

    private static void AppendBytes(StringBuilder sb, string side, LoadedFile f, TreeNode? n)
    {
        if (n?.Offset is not int offset) return;
        var len = Math.Min(n.Length ?? 16, 160);
        len = Math.Min(len, f.Bytes.Length - offset);
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{side}: offset {offset} (0x{offset:x}), {n.Length?.ToString(CultureInfo.InvariantCulture) ?? "?"} bytes");
        for (var i = 0; i < len; i += 16)
        {
            var chunk = f.Bytes.AsSpan(offset + i, Math.Min(16, len - i)).ToArray();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{offset + i:x6}  {string.Join(' ', chunk.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))}");
        }
    }

    public void ApplyEdit()
    {
        var n = _selected?.Node.Left;
        if (_left is null || n?.Edit is null || _left.Plugin is not IWritablePlugin writer || _left.Result.Model is null) return;
        var problem = n.Edit.Apply(EditValue.Trim());
        if (problem is not null)
        {
            Message = problem;
            return;
        }
        var bytes = writer.Write(_left.Result.Model);
        _left = _left with { Bytes = bytes, Result = _left.Plugin.Read(bytes, _left.FileName) };
        _leftEdited = true;
        Message = $"Edited {_left.FileName} - written again by {_left.Plugin.Name}. Save it with Save as….";
        Refresh();
    }

    public byte[]? EditedBytes => _leftEdited ? _left?.Bytes : null;

    public string SuggestedName => _left is null ? "edited.plcbin" : Path.GetFileNameWithoutExtension(_left.FileName) + "_edited" + Path.GetExtension(_left.FileName);
}
