using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BinScope.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) _vm.Open(true, args[1]);
        if (args.Length > 2 && File.Exists(args[2])) _vm.Open(false, args[2]);
    }

    private static string? PickFile()
    {
        var dialog = new OpenFileDialog { Filter = "PLC Studio binaries (*.plcbin)|*.plcbin|All files (*.*)|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? Dropped(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files[0] : null;

    private void OnOpenLeft(object sender, RoutedEventArgs e)
    {
        if (PickFile() is { } path) _vm.Open(true, path);
    }

    private void OnOpenRight(object sender, RoutedEventArgs e)
    {
        if (PickFile() is { } path) _vm.Open(false, path);
    }

    private void OnDropLeft(object sender, DragEventArgs e)
    {
        if (Dropped(e) is { } path) _vm.Open(true, path);
    }

    private void OnDropRight(object sender, DragEventArgs e)
    {
        if (Dropped(e) is { } path) _vm.Open(false, path);
    }

    private void OnCloseRight(object sender, RoutedEventArgs e) => _vm.CloseRight();

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => _vm.Selected = e.NewValue as DiffItem;

    private void OnApply(object sender, RoutedEventArgs e) => _vm.ApplyEdit();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.EditedBytes is not { } bytes) return;
        var dialog = new SaveFileDialog { FileName = _vm.SuggestedName, Filter = "PLC Studio binaries (*.plcbin)|*.plcbin" };
        if (dialog.ShowDialog() == true) File.WriteAllBytes(dialog.FileName, bytes);
    }
}
