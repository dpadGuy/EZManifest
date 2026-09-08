using System.Collections.ObjectModel;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EZManifest.Views.Dialogs;

public sealed partial class FileExplorerPickerDialog : UserControl
{
    private const string KindFolder = "folder";
    private const string KindThisPc = "thispc";
    private const string KindHome = "home";

    private readonly ObservableCollection<FileExplorerPlace> _places = new();
    private readonly ObservableCollection<FileExplorerItem> _items = new();
    private readonly List<Location> _back = new();
    private readonly List<Location> _forward = new();
    private readonly List<FileExplorerItem> _currentItems = new();

    private FileExplorerPickerOptions _options = new();
    private Location _current = Location.ThisPc();
    private int _listVersion;
    private bool _suppressPlaceClick;

    public FileExplorerPickerDialog()
    {
        InitializeComponent();
        PlacesList.ItemsSource = _places;
        FilesView.ItemsSource = _items;
    }

    public IReadOnlyList<string> SelectedPaths { get; private set; } = Array.Empty<string>();

    public event Action? Confirmed;

    public void Configure(FileExplorerPickerOptions options)
    {
        _options = options;
        FilesView.SelectionMode = options.AllowMultiSelect
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.Single;
        NameLabel.Text = options.FoldersOnly ? "Folder name:" : "File name:";
        NameBox.IsReadOnly = options.FoldersOnly;
        TypeCombo.Items.Clear();
        TypeCombo.Items.Add(options.FoldersOnly
            ? "Folder"
            : FormatFilterLabel(options.FileExtensions));
        TypeCombo.SelectedIndex = 0;
        BuildPlaces();
        Location start = ResolveStartLocation(options);
        Navigate(start, recordHistory: false);
        _ = LoadPlaceIconsAsync();
    }

    public bool TryCommit()
    {
        if (!TryCollectSelection(out IReadOnlyList<string> paths) || paths.Count == 0)
        {
            StatusText.Text = _options.FoldersOnly
                ? "Open a folder, or select one in the list."
                : "Select a file to open.";
            return false;
        }

        SelectedPaths = paths;
        return true;
    }

    private void BuildPlaces()
    {
        _places.Clear();
        AddPlace("Home", KindHome, null, "\uE80F", 0);
        AddPlace("Desktop", KindFolder, KnownExplorerFolders.Desktop, "\uE8FC", 0);
        AddPlace("Downloads", KindFolder, KnownExplorerFolders.Downloads, "\uE896", 0);
        AddPlace("Documents", KindFolder, KnownExplorerFolders.Documents, "\uE8A5", 0);
        AddPlace("Pictures", KindFolder, KnownExplorerFolders.Pictures, "\uE91B", 0);
        AddPlace("Music", KindFolder, KnownExplorerFolders.Music, "\uE8D6", 0);
        AddPlace("Videos", KindFolder, KnownExplorerFolders.Videos, "\uE714", 0);

        if (Directory.Exists(AppPaths.DataDirectory))
            AddPlace("EZManifest", KindFolder, AppPaths.DataDirectory, "\uE8B7", 0);

        AddPlace("This PC", KindThisPc, null, "\uE7F4", 0);

        foreach (FileExplorerItem drive in EnumerateDrives())
        {
            _places.Add(new FileExplorerPlace
            {
                Title = drive.Name,
                Kind = KindFolder,
                Path = drive.Path,
                Glyph = "\uEDA2",
                Indent = 16
            });
        }
    }

    private void AddPlace(string title, string kind, string? path, string glyph, double indent)
    {
        if (kind == KindFolder && (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)))
            return;

        _places.Add(new FileExplorerPlace
        {
            Title = title,
            Kind = kind,
            Path = path,
            Glyph = glyph,
            Indent = indent
        });
    }

    private async Task LoadPlaceIconsAsync()
    {
        foreach (FileExplorerPlace place in _places.ToList())
        {
            if (string.IsNullOrWhiteSpace(place.Path) || !Directory.Exists(place.Path))
                continue;

            var icon = await ShellThumbnail.GetAsync(place.Path, isDirectory: true, 32);
            if (icon is not null)
                place.Icon = icon;
        }
    }

    private static Location ResolveStartLocation(FileExplorerPickerOptions options)
    {
        if (options.StartAtThisPc)
            return Location.ThisPc();

        if (!string.IsNullOrWhiteSpace(options.InitialPath))
        {
            try
            {
                string full = Path.GetFullPath(options.InitialPath);
                if (Directory.Exists(full))
                    return Location.Folder(full);
            }
            catch
            {
            }
        }

        string desktop = KnownExplorerFolders.Desktop;
        return Directory.Exists(desktop) ? Location.Folder(desktop) : Location.ThisPc();
    }

    private void Navigate(Location location, bool recordHistory)
    {
        if (recordHistory && !_current.Equals(location))
        {
            _back.Add(_current);
            _forward.Clear();
        }

        _current = location;
        SearchBox.Text = string.Empty;
        ReloadCurrent();
        UpdateChrome();
        SyncPlaceSelection();
    }

    private void ReloadCurrent()
    {
        _listVersion++;
        int version = _listVersion;
        _currentItems.Clear();
        StatusText.Text = string.Empty;
        NewFolderButton.IsEnabled = _current.Kind == KindFolder && Directory.Exists(_current.Path);

        try
        {
            if (_current.Kind == KindThisPc || _current.Kind == KindHome)
            {
                _currentItems.AddRange(_current.Kind == KindHome ? BuildHomeItems() : EnumerateDrives());
            }
            else
            {
                _currentItems.AddRange(EnumerateFolder(_current.Path));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }

        ApplyFilter();
        UpdateNameFromSelection();
        _ = LoadItemIconsAsync(version);
    }

    private IEnumerable<FileExplorerItem> BuildHomeItems()
    {
        yield return PlaceItem("Desktop", KnownExplorerFolders.Desktop, "\uE8FC");
        yield return PlaceItem("Downloads", KnownExplorerFolders.Downloads, "\uE896");
        yield return PlaceItem("Documents", KnownExplorerFolders.Documents, "\uE8A5");
        yield return PlaceItem("Pictures", KnownExplorerFolders.Pictures, "\uE91B");
        yield return PlaceItem("Music", KnownExplorerFolders.Music, "\uE8D6");
        yield return PlaceItem("Videos", KnownExplorerFolders.Videos, "\uE714");
        foreach (FileExplorerItem drive in EnumerateDrives())
            yield return drive;
    }

    private static FileExplorerItem PlaceItem(string name, string path, string glyph)
    {
        return new FileExplorerItem
        {
            Name = name,
            Path = path,
            IsDirectory = true,
            Glyph = glyph
        };
    }

    private static IEnumerable<FileExplorerItem> EnumerateDrives()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = drive.Name;
            string title = root.TrimEnd('\\');
            try
            {
                if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    title = $"{drive.VolumeLabel} ({root.TrimEnd('\\')})";
                else if (drive.DriveType == DriveType.Fixed)
                    title = $"Local Disk ({root.TrimEnd('\\')})";
                else
                    title = $"{drive.DriveType} ({root.TrimEnd('\\')})";
            }
            catch
            {
                title = root.TrimEnd('\\');
            }

            yield return new FileExplorerItem
            {
                Name = title,
                Path = root,
                IsDirectory = true,
                IsDrive = true,
                Glyph = "\uEDA2"
            };
        }
    }

    private IEnumerable<FileExplorerItem> EnumerateFolder(string path)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
        };

        var items = new List<(string Path, bool IsDirectory, DateTime Written)>();
        foreach (string folder in Directory.EnumerateDirectories(path, "*", options))
            items.Add((folder, true, GetLastWriteUtc(folder)));

        if (!_options.FoldersOnly)
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", options).Where(MatchesFilter))
                items.Add((file, false, GetLastWriteUtc(file)));
        }

        return items
            .OrderByDescending(item => item.Written)
            .ThenBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
            .Take(1500)
            .Select(item => new FileExplorerItem
            {
                Name = Path.GetFileName(item.Path),
                Path = item.Path,
                IsDirectory = item.IsDirectory,
                Glyph = item.IsDirectory ? "\uE8B7" : "\uE7C3"
            });
    }

    private static DateTime GetLastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private bool MatchesFilter(string filePath)
    {
        if (_options.FileExtensions.Count == 0)
            return true;

        string ext = Path.GetExtension(filePath);
        return _options.FileExtensions.Any(filter =>
            filter == "*" ||
            filter == ".*" ||
            ext.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
            ext.Equals("." + filter.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        _items.Clear();
        foreach (FileExplorerItem item in _currentItems)
        {
            if (query.Length == 0 ||
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _items.Add(item);
            }
        }

        StatusText.Text = _items.Count == 0 && _currentItems.Count > 0
            ? "No matching items."
            : string.Empty;
    }

    private async Task LoadItemIconsAsync(int version)
    {
        foreach (FileExplorerItem item in _items.ToList())
        {
            if (version != _listVersion)
                return;

            if (item.IsDirectory && !Directory.Exists(item.Path))
                continue;
            if (!item.IsDirectory && !File.Exists(item.Path))
                continue;

            var icon = await ShellThumbnail.GetAsync(item.Path, item.IsDirectory, 64);
            if (version != _listVersion)
                return;
            if (icon is not null)
                item.Icon = icon;
        }
    }

    private void UpdateChrome()
    {
        AddressBox.Text = _current.Kind switch
        {
            KindThisPc => "This PC",
            KindHome => "Home",
            _ => _current.Path
        };
        SearchBox.PlaceholderText = _current.Kind switch
        {
            KindThisPc => "Search This PC",
            KindHome => "Search Home",
            _ => $"Search {Path.GetFileName(_current.Path.TrimEnd('\\'))}"
        };
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        UpButton.IsEnabled = _current.Kind == KindFolder;
        NewFolderButton.IsEnabled = _current.Kind == KindFolder && Directory.Exists(_current.Path);
    }

    private void SyncPlaceSelection()
    {
        _suppressPlaceClick = true;
        FileExplorerPlace? match = null;
        if (_current.Kind == KindThisPc)
            match = _places.FirstOrDefault(p => p.Kind == KindThisPc);
        else if (_current.Kind == KindHome)
            match = _places.FirstOrDefault(p => p.Kind == KindHome);
        else
        {
            match = _places
                .Where(p => p.Kind == KindFolder && !string.IsNullOrWhiteSpace(p.Path))
                .OrderByDescending(p => p.Path!.Length)
                .FirstOrDefault(p => PathsEqual(_current.Path, p.Path!) ||
                                     _current.Path.StartsWith(AppendSlash(p.Path!), StringComparison.OrdinalIgnoreCase));
        }

        PlacesList.SelectedItem = match;
        foreach (FileExplorerPlace place in _places)
            place.IsCurrent = ReferenceEquals(place, match);
        _suppressPlaceClick = false;
    }

    private void UpdateNameFromSelection()
    {
        var selected = FilesView.SelectedItems.OfType<FileExplorerItem>().ToList();
        if (selected.Count == 1)
            NameBox.Text = selected[0].Name;
        else if (selected.Count > 1)
            NameBox.Text = $"{selected.Count} items";
        else if (_current.Kind == KindFolder)
            NameBox.Text = Path.GetFileName(_current.Path.TrimEnd('\\'));
        else
            NameBox.Text = _current.Kind == KindThisPc ? "This PC" : "Home";
    }

    private bool TryCollectSelection(out IReadOnlyList<string> paths)
    {
        var selected = FilesView.SelectedItems.OfType<FileExplorerItem>().ToList();
        if (_options.FoldersOnly)
        {
            var folders = selected
                .Where(item => item.IsDirectory && Directory.Exists(item.Path))
                .Select(item => Path.GetFullPath(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders.Count > 0)
            {
                paths = folders;
                return true;
            }

            if (_current.Kind == KindFolder && Directory.Exists(_current.Path))
            {
                paths = new[] { Path.GetFullPath(_current.Path) };
                return true;
            }

            paths = Array.Empty<string>();
            return false;
        }

        var files = selected
            .Where(item => !item.IsDirectory && File.Exists(item.Path) && MatchesFilter(item.Path))
            .Select(item => Path.GetFullPath(item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0 && _current.Kind == KindFolder)
        {
            string typed = NameBox.Text?.Trim() ?? string.Empty;
            if (typed.Length > 0 &&
                !typed.EndsWith(" items", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = Path.IsPathRooted(typed)
                    ? typed
                    : Path.Combine(_current.Path, typed);
                if (File.Exists(candidate) && MatchesFilter(candidate))
                    files.Add(Path.GetFullPath(candidate));
            }
        }

        paths = files;
        return files.Count > 0;
    }

    private void OpenItem(FileExplorerItem item)
    {
        if (item.IsDirectory)
        {
            if (!Directory.Exists(item.Path))
            {
                StatusText.Text = $"'{item.Name}' is not available.";
                return;
            }

            Navigate(Location.Folder(item.Path), recordHistory: true);
            return;
        }

        if (!_options.FoldersOnly && TryCommit())
            Confirmed?.Invoke();
    }

    private void PlacesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_suppressPlaceClick || e.ClickedItem is not FileExplorerPlace place)
            return;

        Location target = place.Kind switch
        {
            KindThisPc => Location.ThisPc(),
            KindHome => Location.Home(),
            _ => Location.Folder(place.Path ?? string.Empty)
        };
        Navigate(target, recordHistory: true);
    }

    private void FilesView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileExplorerItem item)
            NameBox.Text = item.Name;
    }

    private void FilesView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FilesView.SelectedItem is FileExplorerItem item)
            OpenItem(item);
    }

    private void FilesView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateNameFromSelection();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count == 0)
            return;

        _forward.Add(_current);
        Location previous = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _current = previous;
        SearchBox.Text = string.Empty;
        ReloadCurrent();
        UpdateChrome();
        SyncPlaceSelection();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_forward.Count == 0)
            return;

        _back.Add(_current);
        Location next = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _current = next;
        SearchBox.Text = string.Empty;
        ReloadCurrent();
        UpdateChrome();
        SyncPlaceSelection();
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Kind != KindFolder)
            return;

        try
        {
            var parent = Directory.GetParent(_current.Path);
            Navigate(parent is null ? Location.ThisPc() : Location.Folder(parent.FullName), recordHistory: true);
        }
        catch
        {
            Navigate(Location.ThisPc(), recordHistory: true);
        }
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        string text = AddressBox.Text?.Trim() ?? string.Empty;
        if (text.Equals("This PC", StringComparison.OrdinalIgnoreCase))
        {
            Navigate(Location.ThisPc(), recordHistory: true);
            return;
        }

        if (text.Equals("Home", StringComparison.OrdinalIgnoreCase))
        {
            Navigate(Location.Home(), recordHistory: true);
            return;
        }

        try
        {
            string full = Path.GetFullPath(text.Trim('"'));
            if (Directory.Exists(full))
                Navigate(Location.Folder(full), recordHistory: true);
            else
                StatusText.Text = "That folder does not exist.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Kind == KindThisPc)
            BuildPlaces();
        ReloadCurrent();
        UpdateChrome();
        SyncPlaceSelection();
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Kind != KindFolder || !Directory.Exists(_current.Path))
            return;

        string created = CreateUniqueFolder(_current.Path, "New folder");
        ReloadCurrent();
        FileExplorerItem? item = _items.FirstOrDefault(i =>
            i.Path.Equals(created, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            FilesView.SelectedItem = item;
    }

    private static string CreateUniqueFolder(string parent, string baseName)
    {
        string path = Path.Combine(parent, baseName);
        int n = 2;
        while (Directory.Exists(path) || File.Exists(path))
        {
            path = Path.Combine(parent, $"{baseName} ({n})");
            n++;
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static string FormatFilterLabel(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0)
            return "All files (*.*)";

        string joined = string.Join(";", extensions.Select(ext =>
            ext.StartsWith("*.", StringComparison.Ordinal) ? ext :
            ext.StartsWith('.') ? "*" + ext : "*." + ext));
        return $"Files ({joined})";
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string AppendSlash(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        return path + Path.DirectorySeparatorChar;
    }

    private readonly record struct Location(string Kind, string Path)
    {
        public static Location ThisPc() => new(KindThisPc, string.Empty);
        public static Location Home() => new(KindHome, string.Empty);
        public static Location Folder(string path) => new(KindFolder, System.IO.Path.GetFullPath(path));
    }
}
