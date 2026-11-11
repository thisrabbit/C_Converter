using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CConvert.Core;
using Microsoft.Win32;

namespace CConvert.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileItem> _items = new();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        FileGrid.ItemsSource = _items;
        TargetBox.ItemsSource = new[] { CorelVersion.V2019 }.Select(v => $"CorelDRAW {v.Name}");
        // Files and folders passed on the command line (for example, from Windows “Send To”) are added automatically.
        AddPaths(Environment.GetCommandLineArgs().Skip(1));
        UpdateStatus();
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark Windows title bar for a matching theme (Windows 10 2004+ / 11; ignored on older versions).</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    // ---------- Add files ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _cts is null ? DragDropEffects.Copy : DragDropEffects.None;
        DropBorder.Opacity = e.Effects == DragDropEffects.Copy ? 1 : 0.55;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropBorder.Opacity = 0.55;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropBorder.Opacity = 0.55;
        if (_cts is null && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CorelDRAW (*.cdr)|*.cdr", Multiselect = true, Title = "Choose file CorelDRAW" };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder containing .cdr files (including subfolders)" };
        if (dlg.ShowDialog(this) == true) AddPaths([dlg.FolderName]);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (string p in paths)
        {
            IEnumerable<string> files = Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*.cdr", SearchOption.AllDirectories)
                : string.Equals(Path.GetExtension(p), ".cdr", StringComparison.OrdinalIgnoreCase) ? [p] : [];
            foreach (string f in files)
            {
                if (!known.Add(f) || f.EndsWith(".c_converter.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var (ver, desc) = CdrInfo.Probe(f);
                var item = new FileItem(f) { SourceVersion = ver is { IsExperimental: true } ? $"CorelDRAW {ver.Name} · experimental" : desc };
                if (ver is null) item.SetState(FileState.Skipped, "Skipped");
                else if (ver.Vrsn <= CorelVersion.V2019.Vrsn) item.SetState(FileState.Skipped, "Already 2019 or older");
                _items.Add(item);
            }
        }
        UpdateStatus();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        LogBox.Clear();
        UpdateStatus();
    }

    private void OnPickOutDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose output folder" };
        if (dlg.ShowDialog(this) == true) OutDirBox.Text = dlg.FolderName;
    }

    // ---------- Run ----------

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var todo = _items.Where(i => i.State is FileState.Pending or FileState.Failed).ToList();
        if (todo.Count == 0)
        {
            MessageBox.Show(this, "There are no files to convert. Add a file .cdr version 2020–2024 (2025/2026: experimental).", "C_Converter");
            return;
        }
        string? outDir = null;
        if (ToFolder.IsChecked == true)
        {
            outDir = OutDirBox.Text.Trim();
            if (outDir.Length == 0)
            {
                MessageBox.Show(this, "Choose an output folder.", "C_Converter");
                return;
            }
            Directory.CreateDirectory(outDir);
        }

        var target = CorelVersion.V2019;
        _cts = new CancellationTokenSource();
        SetRunning(true);
        Progress.Maximum = todo.Count;
        Progress.Value = 0;
        try
        {
            foreach (var item in todo)
            {
                if (_cts.IsCancellationRequested) break;
                item.SetState(FileState.Running, "Converting…");
                string output = CdrInfo.OutputPathFor(item.Path, target, outDir);
                // Each file runs independently; an error in one file does not stop the batch.
                var result = await Task.Run(() => Downgrader.Run(item.Path, output, target));
                item.Log = string.Join(Environment.NewLine, result.Log.Select(l => l.ToString()));
                item.WarningCount = result.Log.Count(l => l.Level == Core.LogLevel.Warning);
                if (result.Success)
                {
                    item.OutputPath = output;
                    if (result.Experimental)
                        item.SetState(FileState.DoneExperimental, "Done (experimental)");
                    else
                        item.SetState(result.HasWarnings ? FileState.DoneWithWarnings : FileState.Done,
                            result.HasWarnings ? "Done (with warnings)" : "Done");
                }
                else
                {
                    item.SetState(FileState.Failed, "Errors");
                }
                Progress.Value++;
                UpdateStatus();
                if (FileGrid.SelectedItem == item || FileGrid.SelectedItem is null) ShowLog();
            }
        }
        finally
        {
            foreach (var item in _items.Where(i => i.State == FileState.Running))
                item.SetState(FileState.Pending, "Pending");
            _cts = null;
            SetRunning(false);
            UpdateStatus();
            ShowLog();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping after the current file…";
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ClearButton.IsEnabled = !running;
        TargetBox.IsEnabled = !running;
    }

    private void UpdateStatus()
    {
        int total = _items.Count;
        int done = _items.Count(i => i.State is FileState.Done or FileState.DoneWithWarnings or FileState.DoneExperimental);
        int warn = _items.Count(i => i.State is FileState.DoneWithWarnings or FileState.DoneExperimental);
        int fail = _items.Count(i => i.State == FileState.Failed);
        int skip = _items.Count(i => i.State == FileState.Skipped);
        StatTotal.Text = total.ToString();
        StatDone.Text = done.ToString();
        StatWarn.Text = warn.ToString();
        StatFail.Text = fail.ToString();
        DropHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = total == 0
            ? "No files yet."
            : skip > 0 ? $"{done}/{total - skip} files · {skip} skipped" : $"{done}/{total} files";
    }

    // ---------- Log ----------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowLog();

    private void ShowLog()
    {
        if (FileGrid.SelectedItem is FileItem one)
        {
            LogTitle.Text = "LOG · " + one.FileName;
            LogBox.Text = $"{one.Path}{Environment.NewLine}{one.Log}";
            LogBox.ScrollToHome();
            return;
        }
        LogTitle.Text = "LOG";
        LogBox.Text = BuildFullLog();
        LogBox.ScrollToEnd();
    }

    private string BuildFullLog()
    {
        var sb = new StringBuilder();
        foreach (var i in _items.Where(i => i.Log.Length > 0 || i.State == FileState.Skipped))
        {
            sb.AppendLine($"=== {i.Path}  [{i.StatusText}]");
            if (i.State == FileState.Skipped) sb.AppendLine($"  Skipped: {i.SourceVersion}");
            foreach (string line in i.Log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine("  " + line);
        }
        return sb.ToString();
    }

    private void OnSaveLog(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = $"C_Converter_log_{DateTime.Now:yyyyMMdd_HHmm}.txt" };
        if (dlg.ShowDialog(this) == true)
            File.WriteAllText(dlg.FileName, BuildFullLog(), new UTF8Encoding(true));
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        string? dir = (FileGrid.SelectedItem as FileItem)?.OutputPath is { Length: > 0 } p
            ? Path.GetDirectoryName(p)
            : ToFolder.IsChecked == true && OutDirBox.Text.Length > 0 ? OutDirBox.Text
            : _items.FirstOrDefault(i => i.OutputPath.Length > 0)?.OutputPath is { } q ? Path.GetDirectoryName(q) : null;
        if (dir is not null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}

public enum FileState { Pending, Running, Done, DoneWithWarnings, DoneExperimental, Failed, Skipped }

public sealed class FileItem(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);

    private string _sourceVersion = "";
    public string SourceVersion { get => _sourceVersion; set => Set(ref _sourceVersion, value); }

    private FileState _state = FileState.Pending;
    public FileState State => _state;

    private string _statusText = "Pending";
    public string StatusText => _statusText;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public Brush StatusBrush => _state switch
    {
        FileState.Done => Res("Success"),
        FileState.DoneWithWarnings => Res("Warning"),
        FileState.DoneExperimental => Res("Info"),
        FileState.Failed => Res("Danger"),
        FileState.Running => Res("Info"),
        FileState.Skipped => Res("TextMuted"),
        _ => Res("Text"),
    };

    /// <summary>Tinted background for "chip" status.</summary>
    public Brush StatusBackground
    {
        get
        {
            var c = ((SolidColorBrush)StatusBrush).Color;
            var b = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }

    private int _warningCount;
    public int WarningCount { get => _warningCount; set => Set(ref _warningCount, value); }

    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }

    public string Log { get; set; } = "";

    public void SetState(FileState state, string text)
    {
        _state = state;
        _statusText = text;
        OnChanged(nameof(State));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(StatusBrush));
        OnChanged(nameof(StatusBackground));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
