// Plugin.cs — Avalonia UI: App + data models + MainWindow
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;

using Avalonia.Threading;

namespace Nikse.SubtitleEdit.Features.Tools.OstTable
{
    // ── Data models ──────────────────────────────────────────────────────────

    public class SubEntry
    {
        public Guid? SourceId { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime   { get; set; } = "";
        public string Text      { get; set; } = "";
    }

    public class OstEntry
    {
        public Guid? SourceId { get; set; }
        public string StartTime { get; set; } = "";
        public string Text      { get; set; } = "";
        public string Notes     { get; set; } = "";
    }

    public class ProjectState
    {
        public string         Version      { get; set; } = "5.0";
        public string         ProjectTitle { get; set; } = "";
        public string         Season       { get; set; } = "";
        public string         Episode      { get; set; } = "";
        public bool           IncludeNotes { get; set; }
        public List<OstEntry> Entries      { get; set; } = new();
    }

    public class OstItemVm : INotifyPropertyChanged
    {
        public Guid? SourceId { get; set; }
        private string _tc = "", _text = "", _notes = "";
        public string TC    { get => _tc;    set { _tc    = value; OnPC(); } }
        public string Text  { get => _text;  set { _text  = value; OnPC(); } }
        public string Notes { get => _notes; set { _notes = value; OnPC(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class SubItemVm : INotifyPropertyChanged
    {
        private bool _isAdded;
        public string StartTime { get; init; } = "";
        public string Text      { get; init; } = "";
        public int    OrigIndex { get; init; }

        public bool IsAdded
        {
            get => _isAdded;
            set { _isAdded = value; OnPC(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── MainWindow ───────────────────────────────────────────────────────────

    public class OstTableView : UserControl
    {
        // State
        private readonly List<SubEntry>             _allEntries;
        private readonly ObservableCollection<SubItemVm>  _subItems  = new();
        private readonly ObservableCollection<OstItemVm>  _ostItems  = new();
        private readonly bool   _isDark;
        private string _subtitleFile;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private bool _includeNotes;
        private bool _isDirty;
        private DispatcherTimer? _autoSaveTimer;
        private DispatcherTimer? _syncTimer;

        // Controls
        private TextBox   _txtTitle   = null!;
        private TextBox   _txtSeason  = null!;
        private TextBox   _txtEpisode = null!;
        private Control   _pnlSeasonEp = null!;
        private CheckBox  _chkSeasonEp = null!;
        private Button    _btnNotes    = null!;
        private DataGrid  _gridOst     = null!;
        private DataGrid  _gridSub     = null!;
        private TextBox   _txtSearch   = null!;
        private TextBlock _lblStatus   = null!;

        // Theme brushes
        private readonly IBrush _bg, _bg2, _fg, _headerBg, _toolbarBg, _mutedFg;
        private readonly IBrush _accentBrush;

        public OstTableView(List<SubEntry> entries, string baseName, bool isDark, string subtitleFile)
        {
            _allEntries    = entries;
            _isDark        = isDark;
            _subtitleFile  = subtitleFile;

            // Theme colors matching SE v5
            if (isDark)
            {
                _bg         = new SolidColorBrush(Color.Parse("#1E1E1E"));
                _bg2        = new SolidColorBrush(Color.Parse("#2D2D30"));
                _fg         = new SolidColorBrush(Color.Parse("#DCDCDC"));
                _headerBg   = new SolidColorBrush(Color.Parse("#121212"));
                _toolbarBg  = new SolidColorBrush(Color.Parse("#252526"));
                _mutedFg    = new SolidColorBrush(Color.Parse("#AEB8C6"));
                _accentBrush= new SolidColorBrush(Color.Parse("#2563EB"));
            }
            else
            {
                _bg         = new SolidColorBrush(Color.Parse("#F8FAFB"));
                _bg2        = new SolidColorBrush(Color.Parse("#F1F5F9"));
                _fg         = new SolidColorBrush(Color.Parse("#1E1E1E"));
                _headerBg   = new SolidColorBrush(Color.Parse("#334155"));
                _toolbarBg  = new SolidColorBrush(Color.Parse("#334155"));
                _mutedFg    = new SolidColorBrush(Color.Parse("#6B7280"));
                _accentBrush= new SolidColorBrush(Color.Parse("#C7D2FE"));
            }

            MinWidth = 300;
            FontSize = 16;
            FlowDirection = FlowDirection.RightToLeft;
            Background = _bg;

            Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
            BuildUI();

            _txtTitle.Text = baseName;
            PopulateSubGrid();
            _isDirty = false;
            TryAutoLoad();
            StartAutoSave();
            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _syncTimer.Tick += (_, __) => SyncLinkedRows();
            _syncTimer.Start();

        }

        public event Action? RequestHide;
        public Func<List<SubEntry>>? SelectedRowsProvider { get; set; }
        public Func<List<SubEntry>>? SourceRowsProvider { get; set; }
        public TopLevel? DialogOwner { get; set; }
        public Action<string>? StatusChanged { get; set; }
        private Avalonia.Platform.Storage.IStorageProvider StorageProvider => (DialogOwner ?? TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("OST dialog owner is missing")).StorageProvider;
        internal void RebindSubtitleFile(string subtitleFile)
        {
            _subtitleFile = subtitleFile;
            MarkDirty();
        }
        internal List<string> ValidateForExport()
        {
            SyncLinkedRows();
            return Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.CheckOst(_ostItems.Select(r => (r.TC, r.Text)));
        }
        internal (string Title, string Season, string Episode) GetDetails() => (_txtTitle.Text ?? "", _txtSeason.Text ?? "", _txtEpisode.Text ?? "");
        internal void SetDetails(string title, string season, string episode)
        {
            _txtTitle.Text = title; _chkSeasonEp.IsChecked = season.Length > 0 || episode.Length > 0;
            _txtSeason.Text = season; _txtEpisode.Text = episode; MarkDirty();
        }
        internal string ArchiveSnapshot() { SyncLinkedRows(); _gridOst.CommitEdit(); return SerializeState(BuildProjectState()); }
        internal Task ExportWordAsync() => BtnExport_ClickAsync();
        internal Task SaveProjectAsync() => BtnSave_ClickAsync();
        internal Task OpenProjectAsync() => BtnLoad_ClickAsync();
        internal HashSet<Guid> LinkedIds() => _ostItems.Where(r => r.SourceId.HasValue).Select(r => r.SourceId!.Value).ToHashSet();

        internal void RemoveMainSelection()
        {
            SyncLinkedRows();
            var ids = (SelectedRowsProvider?.Invoke() ?? new()).Where(r => r.SourceId.HasValue).Select(r => r.SourceId!.Value).ToHashSet();
            var rows = _ostItems.Where(r => r.SourceId.HasValue && ids.Contains(r.SourceId.Value)).ToList();
            foreach (var row in rows) _ostItems.Remove(row);
            ShowStatus($"הוסרו {rows.Count} שורות מה־OST");
        }

        public bool TrySave()
        {
            SyncLinkedRows();
            _gridOst.CommitEdit();
            return !_isDirty || DoAutoSave();
        }

        public bool TrySaveAndStop()
        {
            if (!TrySave()) return false;
            _autoSaveTimer?.Stop();
            _syncTimer?.Stop();
            return true;
        }

        public void Close()
        {
            _gridOst.CommitEdit();
            if (_isDirty && !DoAutoSave()) return;
            RequestHide?.Invoke();
        }

        internal void AddMainSelection()
        {
            SyncLinkedRows();
            var selected = SelectedRowsProvider?.Invoke() ?? new();
            int added = 0;
            foreach (var row in selected)
            {
                if (_ostItems.Any(r => row.SourceId.HasValue && r.SourceId == row.SourceId || !r.SourceId.HasValue && r.TC == row.StartTime && r.Text == row.Text)) continue;
                _ostItems.Add(new OstItemVm { SourceId = row.SourceId, TC = row.StartTime, Text = row.Text });
                added++;
            }
            SortOstByTime();
            ShowStatus($"נוספו {added} שורות");
        }

        internal void SyncLinkedRows()
        {
            if (SourceRowsProvider == null) return;
            var source = SourceRowsProvider();
            var byId = source.Where(r => r.SourceId.HasValue).GroupBy(r => r.SourceId!.Value).ToDictionary(g => g.Key, g => g.First());
            foreach (var row in _ostItems)
            {
                SubEntry? linked = null;
                if (row.SourceId.HasValue) byId.TryGetValue(row.SourceId.Value, out linked);
                if (linked == null)
                {
                    // Saved SRT files do not retain runtime IDs. Relink only an exact,
                    // unique match; never guess by row number after insertions/deletions.
                    var matches = source.Where(r => r.StartTime == row.TC && r.Text == row.Text).Take(2).ToList();
                    if (matches.Count == 1) linked = matches[0];
                }
                if (linked == null) continue;
                if (row.SourceId != linked.SourceId) { row.SourceId = linked.SourceId; MarkDirty(); }
                if (row.TC != linked.StartTime) row.TC = linked.StartTime;
                if (row.Text != linked.Text) row.Text = linked.Text;
            }
        }

        // ── Parse SRT ────────────────────────────────────────────────────────

        public static List<SubEntry> ParseSrt(string srtContent)
        {
            var result = new List<SubEntry>();
            var blocks = Regex.Split(srtContent.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n[ \t]*\n+");
            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                int t = Array.FindIndex(lines, l => l.Contains("-->"));
                if (t < 0 || t + 1 >= lines.Length) continue;
                var times = lines[t].Split(new[] { "-->" }, 2, StringSplitOptions.None);
                string text = string.Join("\n", lines.Skip(t + 1));
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(new SubEntry { StartTime = times[0].Trim(), EndTime = times[1].Trim(), Text = text });
            }
            return result;
        }

        // ── Auto-save ─────────────────────────────────────────────────────────

        private string? GetSidecarPath()
        {
            if (!string.IsNullOrEmpty(_subtitleFile))
                return Path.ChangeExtension(_subtitleFile, ".ostproject");
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Subtitle Edit", "OSTPlugin");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, _sessionId + ".ostproject");
            }
            catch { return null; }
        }

        private void TryAutoLoad()
        {
            string? path = GetSidecarPath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var st = DeserializeState(File.ReadAllText(path, Encoding.UTF8));
                if (st == null) return;
                ApplyProjectState(st);
                ShowStatus($"טבלה נטענה אוטומטית ({st.Entries.Count} שורות)");
                _isDirty = false;
            }
            catch { }
        }

        private void StartAutoSave()
        {
            _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _autoSaveTimer.Tick += (_, __) => { if (_isDirty) DoAutoSave(); };
            _autoSaveTimer.Start();
        }

        private bool DoAutoSave()
        {
            string? path = GetSidecarPath();
            if (path == null) { ShowStatus("לא ניתן לשמור אוטומטית. השתמש בשמור בשם.", true); return false; }
            try
            {
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, SerializeState(BuildProjectState()), Encoding.UTF8);
                File.Move(temporary, path, true);
                _isDirty = false;
                return true;
            }
            catch (Exception ex) { ShowStatus("שמירה אוטומטית נכשלה: " + ex.Message, true); return false; }
        }

        private void MarkDirty() => _isDirty = true;
        private void OnOstChanged(object? sender, PropertyChangedEventArgs e) { MarkDirty(); RefreshSubHighlights(); }

        // ── Build UI ─────────────────────────────────────────────────────────

        private void BuildUI()
        {
            // ── Header ────────────────────────────────────────────────
            var header = new Border
            {
                Background = _headerBg, Height = 40,
                Child = new TextBlock
                {
                    Text = "ממיר טקסט לטבלת OST",
                    Foreground = Brushes.White,
                    FontSize = 14, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    FlowDirection = FlowDirection.RightToLeft,
                }
            };

            // ── Title row ─────────────────────────────────────────────
            _txtTitle = new TextBox
            {
                Watermark = "כותרת הפרוייקט",
                FlowDirection = FlowDirection.RightToLeft,
            };
            _txtTitle.TextChanged += (_, __) => MarkDirty();

            _btnNotes = MakeButton("+ הערות", "#64748B");
            _btnNotes.Click += (_, __) =>
            {
                _includeNotes       = !_includeNotes;
                _btnNotes.Content   = _includeNotes ? "− הערות" : "+ הערות";
                _btnNotes.Background = _includeNotes
                    ? new SolidColorBrush(Color.Parse("#B91C1C"))
                    : new SolidColorBrush(Color.Parse("#64748B"));
                RebuildOstColumns();
                MarkDirty();
            };

            _chkSeasonEp = new CheckBox
            {
                Content     = "+ עונה ופרק",
                Foreground  = _fg,
                FlowDirection = FlowDirection.RightToLeft,
            };
            _chkSeasonEp.IsCheckedChanged += (_, __) =>
            {
                bool on = _chkSeasonEp.IsChecked == true;
                _pnlSeasonEp.IsVisible = on;
                _chkSeasonEp.Content   = on ? "− עונה ופרק" : "+ עונה ופרק";
                if (!on) { _txtSeason.Text = ""; _txtEpisode.Text = ""; }
                MarkDirty();
            };

            // Title row
            var titleGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
            };
            var lblTitle = new TextBlock
            {
                Text = "כותרת:", Foreground = _fg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(lblTitle,     0);
            Grid.SetColumn(_txtTitle,    1);
            Grid.SetColumn(_btnNotes,    0);
            Grid.SetRow(_btnNotes, 1);
            Grid.SetColumn(_chkSeasonEp, 1);
            Grid.SetRow(_chkSeasonEp, 1);
            _btnNotes.Margin    = new Thickness(8, 0, 4, 0);
            _chkSeasonEp.Margin = new Thickness(4, 0, 0, 0);
            titleGrid.Children.Add(lblTitle);
            titleGrid.Children.Add(_txtTitle);
            titleGrid.Children.Add(_btnNotes);
            titleGrid.Children.Add(_chkSeasonEp);
            var titleRowBorder = new Border
            {
                Background = _bg2, Padding = new Thickness(8, 5),
                Child = titleGrid
            };

            // ── Season/Episode row ────────────────────────────────────
            _txtSeason  = new TextBox { Width = 56, Watermark = "#", FlowDirection = FlowDirection.LeftToRight };
            _txtEpisode = new TextBox { Width = 56, Watermark = "#", FlowDirection = FlowDirection.LeftToRight };
            _txtSeason.TextChanged  += (_, __) => MarkDirty();
            _txtEpisode.TextChanged += (_, __) => MarkDirty();

            var seRowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                FlowDirection = FlowDirection.RightToLeft,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "עונה:", Foreground = _fg, VerticalAlignment = VerticalAlignment.Center },
                    _txtSeason,
                    new TextBlock { Text = "פרק:",  Foreground = _fg, VerticalAlignment = VerticalAlignment.Center },
                    _txtEpisode,
                }
            };
            _txtSeason.TextChanged += (_, __) => MarkDirty();
            _txtEpisode.TextChanged += (_, __) => MarkDirty();
            _pnlSeasonEp = new Border
            {
                Background  = _isDark
                    ? new SolidColorBrush(Color.Parse("#16161A"))
                    : new SolidColorBrush(Color.Parse("#E0E8F3")),
                Padding    = new Thickness(10, 6),
                IsVisible  = false,
                Child      = seRowPanel
            };

            // ── Toolbar (bottom) ──────────────────────────────────────
            var btnExport = MakeButton("📄 ייצוא Word", "#3730A3");
            var btnSave   = MakeButton("💾 שמור בשם",   "#059669");
            var btnLoad   = MakeButton("📂 פתח",        "#64748B");
            var btnClose  = MakeButton("סגור",          "#4B5563");
            _lblStatus    = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#86EFAC")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0),
                FontSize = 12,
            };
            btnExport.Click += async (_, __) => await BtnExport_ClickAsync();
            btnSave.Click   += async (_, __) => await BtnSave_ClickAsync();
            btnLoad.Click   += async (_, __) => await BtnLoad_ClickAsync();
            btnClose.Click  += (_, __) => Close();

            var toolbar = new Border
            {
                Background = _toolbarBg, Padding = new Thickness(8, 7),
                Child = new StackPanel
                {
                    Children =
                    {
                        new WrapPanel { Children = { btnClose, btnLoad, btnSave, btnExport } },
                        _lblStatus,
                    }
                }
            };

            BuildSubGrid();
            _txtSearch = new TextBox();
            var btnAdd = MakeButton("הוסף שורות מסומנות מהכתוביות", "#059669");
            btnAdd.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnAdd.Click += (_, __) => AddMainSelection();
            var instructions = new TextBlock
            {
                Text = "סמן שורות ברשימת הכתוביות הראשית ולחץ כאן להוספה.",
                Foreground = _mutedFg, FontSize = 14, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
            };

            // ── Right panel: OST table ────────────────────────────────
            BuildOstGrid();
            var btnRemove = MakeButton("✕ הסר נבחרים", "#B91C1C");
            var btnUp     = MakeButton("↑", "#64748B"); btnUp.Width   = 40;
            var btnDown   = MakeButton("↓", "#64748B"); btnDown.Width = 40;
            btnRemove.Click += (_, __) => RemoveSelectedFromOst();
            btnUp.Click     += (_, __) => MoveOstRow(-1);
            btnDown.Click   += (_, __) => MoveOstRow(+1);

            var rightPanel = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new Border
                    {
                        Background = _bg2, Padding = new Thickness(6, 5),
                        Child = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            FlowDirection = FlowDirection.RightToLeft,
                            Spacing = 4,
                            Children = { btnRemove, btnDown, btnUp }
                        },
                        [DockPanel.DockProperty] = Dock.Bottom,
                    },
                    _gridOst,
                }
            };

            // ── Assemble main layout ──────────────────────────────────
            var root = new DockPanel { LastChildFill = true };
            header.SetValue(DockPanel.DockProperty,        Dock.Top);
            titleRowBorder.SetValue(DockPanel.DockProperty, Dock.Top);
            _pnlSeasonEp.SetValue(DockPanel.DockProperty,  Dock.Top);
            toolbar.SetValue(DockPanel.DockProperty,        Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(titleRowBorder);
            root.Children.Add(_pnlSeasonEp);
            root.Children.Add(toolbar);
            root.Children.Add(btnAdd.WithDock(Dock.Top));
            root.Children.Add(instructions.WithDock(Dock.Top));
            root.Children.Add(rightPanel);

            Content = root;

            // Keyboard shortcuts
            KeyDown += OnWindowKeyDown;
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !e.Handled && e.Source is not TextBox) { Close(); e.Handled = true; }
        }

        // ── Grids ─────────────────────────────────────────────────────────────

        private void BuildSubGrid()
        {
            _gridSub = new DataGrid
            {
                ItemsSource          = _subItems,
                AutoGenerateColumns  = false,
                IsReadOnly           = true,
                SelectionMode        = DataGridSelectionMode.Extended,
                CanUserSortColumns   = false,
                CanUserReorderColumns= false,
                CanUserResizeColumns = true,
                GridLinesVisibility  = DataGridGridLinesVisibility.Horizontal,
                FlowDirection        = FlowDirection.RightToLeft,
                RowBackground        = _bg,
            };

            _gridSub.Columns.Add(new DataGridCheckBoxColumn
            {
                Header   = "✓",
                Binding  = new Binding("IsAdded") { Mode = BindingMode.OneWay },
                IsReadOnly = true,
                Width    = new DataGridLength(36),
            });
            _gridSub.Columns.Add(MakeTextColumn("TC", "StartTime", new DataGridLength(145), FlowDirection.LeftToRight));
            _gridSub.Columns.Add(MakeTextColumn("טקסט", "Text", new DataGridLength(1, DataGridLengthUnitType.Star)));

            _gridSub.DoubleTapped += (_, e) =>
            {
                if (_gridSub.SelectedItem is SubItemVm vm) ToggleSubInOst(vm.OrigIndex);
            };
            _gridSub.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    var sel = _gridSub.SelectedItems?.Cast<SubItemVm>().ToList() ?? new();
                    if (sel.Count == 1) ToggleSubInOst(sel[0].OrigIndex);
                    else if (sel.Count > 1) AddSelectedToOst();
                    e.Handled = true;
                }
            };
        }

        private void BuildOstGrid()
        {
            _gridOst = new DataGrid
            {
                ItemsSource           = _ostItems,
                AutoGenerateColumns   = false,
                IsReadOnly            = false,
                SelectionMode         = DataGridSelectionMode.Extended,
                CanUserSortColumns    = false,
                CanUserReorderColumns = false,
                CanUserResizeColumns  = true,
                GridLinesVisibility   = DataGridGridLinesVisibility.Horizontal,
                FlowDirection         = FlowDirection.RightToLeft,
                RowBackground         = _bg,
            };
            _ostItems.CollectionChanged += (_, e) =>
            {
                if (e.OldItems != null) foreach (OstItemVm vm in e.OldItems) vm.PropertyChanged -= OnOstChanged;
                if (e.NewItems != null) foreach (OstItemVm vm in e.NewItems) vm.PropertyChanged += OnOstChanged;
                RefreshSubHighlights(); MarkDirty();
            };
            AddOstColumns();

            _gridOst.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete && !e.Handled && e.Source is not TextBox) RemoveSelectedFromOst();
            };
        }

        private static DataGridTemplateColumn MakeTextColumn(string header, string property, DataGridLength width, FlowDirection direction = FlowDirection.RightToLeft) => new()
        {
            Header = header,
            Width = width,
            CellTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock
            {
                [!TextBlock.TextProperty] = new Binding(property) { Source = item },
                TextWrapping = TextWrapping.Wrap,
                FlowDirection = direction,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 4),
            }),
            CellEditingTemplate = new FuncDataTemplate<object>((item, _) => new TextBox
            {
                [!TextBox.TextProperty] = new Binding(property) { Source = item, Mode = BindingMode.TwoWay },
                AcceptsReturn = property != "TC",
                TextWrapping = TextWrapping.Wrap,
                FlowDirection = direction,
            }),
        };

        private void AddOstColumns()
        {
            _gridOst.Columns.Clear();
            _gridOst.Columns.Add(MakeTextColumn("TC", "TC", new DataGridLength(145), FlowDirection.LeftToRight));
            _gridOst.Columns.Add(MakeTextColumn("כיתוב", "Text", new DataGridLength(1, DataGridLengthUnitType.Star)));
            if (_includeNotes) _gridOst.Columns.Add(MakeTextColumn("הערות", "Notes", new DataGridLength(120)));
        }

        private void RebuildOstColumns()
        {
            _gridOst.CommitEdit();
            AddOstColumns();
        }

        // ── Data operations ───────────────────────────────────────────────────

        private void PopulateSubGrid()
        {
            _subItems.Clear();
            for (int i = 0; i < _allEntries.Count; i++)
                _subItems.Add(new SubItemVm
                {
                    StartTime = _allEntries[i].StartTime,
                    Text      = _allEntries[i].Text,
                    OrigIndex = i,
                });
            RefreshSubHighlights();
        }

        private void FilterSubGrid(string query)
        {
            query = query.Trim().ToLower();
            _subItems.Clear();
            for (int i = 0; i < _allEntries.Count; i++)
            {
                var e = _allEntries[i];
                if (!string.IsNullOrEmpty(query) &&
                    !e.StartTime.ToLower().Contains(query) &&
                    !e.Text.ToLower().Contains(query)) continue;
                _subItems.Add(new SubItemVm
                {
                    StartTime = e.StartTime,
                    Text      = e.Text,
                    OrigIndex = i,
                });
            }
            RefreshSubHighlights();
        }

        private void RefreshSubHighlights()
        {
            var added = new HashSet<string>(_ostItems.Select(r => SubKey(r.TC, r.Text)));
            foreach (var vm in _subItems)
                vm.IsAdded = added.Contains(SubKey(vm.StartTime, vm.Text));
        }

        private static string SubKey(string tc, string text) => tc + "|" + text.Trim();

        private void AddSelectedToOst()
        {
            var sel = _gridSub.SelectedItems?.Cast<SubItemVm>()
                              .OrderBy(v => v.OrigIndex).ToList() ?? new();
            if (sel.Count == 0) return;
            foreach (var vm in sel) AddSubToOst(vm.OrigIndex, skipDupe: true);
            SortOstByTime();
            ShowStatus($"נוספו {sel.Count} שורות לטבלה");
        }

        private void ToggleSubInOst(int idx)
        {
            if (idx < 0 || idx >= _allEntries.Count) return;
            var e = _allEntries[idx];
            var existing = _ostItems.FirstOrDefault(r => r.TC == e.StartTime && r.Text == e.Text);
            if (existing != null)
            {
                _ostItems.Remove(existing);
                ShowStatus("הוסר מהטבלה");
            }
            else
            {
                AddSubToOst(idx);
                ShowStatus("נוסף לטבלה");
            }
        }

        private void AddSubToOst(int idx, bool skipDupe = false)
        {
            if (idx < 0 || idx >= _allEntries.Count) return;
            var e = _allEntries[idx];
            if (skipDupe && _ostItems.Any(r => r.TC == e.StartTime && r.Text == e.Text)) return;
            _ostItems.Add(new OstItemVm { TC = e.StartTime, Text = e.Text });
            SortOstByTime();
        }

        private void RemoveSelectedFromOst()
        {
            var sel = _gridOst.SelectedItems?.Cast<OstItemVm>().ToList() ?? new();
            foreach (var vm in sel) _ostItems.Remove(vm);
        }

        private void MoveOstRow(int delta)
        {
            if (_gridOst.SelectedItem is not OstItemVm vm) return;
            int idx = _ostItems.IndexOf(vm);
            int dst = idx + delta;
            if (dst < 0 || dst >= _ostItems.Count) return;
            _ostItems.Move(idx, dst);
            _gridOst.SelectedItem = _ostItems[dst];
            MarkDirty();
        }

        private void SortOstByTime()
        {
            var sorted = _ostItems.OrderBy(r => ParseTimecodeToMs(r.TC)).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int cur = _ostItems.IndexOf(sorted[i]);
                if (cur != i) _ostItems.Move(cur, i);
            }
        }

        private static double ParseTimecodeToMs(string tc)
        {
            var m = Regex.Match(tc.Trim(), @"(\d+):(\d+):(\d+)[,.](\d+)");
            if (!m.Success) return 0;
            string ms = m.Groups[4].Value.PadRight(3, '0')[..3];
            return int.Parse(m.Groups[1].Value) * 3600000.0
                 + int.Parse(m.Groups[2].Value) * 60000.0
                 + int.Parse(m.Groups[3].Value) * 1000.0
                 + int.Parse(ms);
        }

        private List<OstEntry> OstRows() =>
            _ostItems.Select(r => new OstEntry
            {
                SourceId = r.SourceId, StartTime = r.TC, Text = r.Text, Notes = r.Notes
            }).ToList();

        private static OstItemVm ToVm(OstEntry e) =>
            new OstItemVm { SourceId = e.SourceId, TC = e.StartTime, Text = e.Text, Notes = e.Notes };

        // ── Project state ─────────────────────────────────────────────────────

        private ProjectState BuildProjectState() => new()
        {
            ProjectTitle = _txtTitle.Text?.Trim() ?? "",
            Season       = _txtSeason.Text?.Trim()  ?? "",
            Episode      = _txtEpisode.Text?.Trim() ?? "",
            IncludeNotes = _includeNotes,
            Entries      = OstRows()
        };

        private void ApplyProjectState(ProjectState st)
        {
            _txtTitle.Text   = st.ProjectTitle;
            _txtSeason.Text  = st.Season;
            _txtEpisode.Text = st.Episode;
            bool hasSE = st.Season.Length > 0 || st.Episode.Length > 0;
            _chkSeasonEp.IsChecked = hasSE;
            _pnlSeasonEp.IsVisible = hasSE;
            _includeNotes = st.IncludeNotes;
            _btnNotes.Content    = _includeNotes ? "− הערות" : "+ הערות";
            _btnNotes.Background = _includeNotes
                ? new SolidColorBrush(Color.Parse("#B91C1C"))
                : new SolidColorBrush(Color.Parse("#64748B"));
            _ostItems.Clear();
            AddOstColumns();
            foreach (var e in st.Entries) _ostItems.Add(ToVm(e));
            RefreshSubHighlights();
        }

        // ── File dialogs ──────────────────────────────────────────────────────

        private async Task BtnExport_ClickAsync()
        {
            _gridOst.CommitEdit();
            SyncLinkedRows();
            var rows = OstRows();
            if (rows.Count == 0) { ShowStatus("טבלה ריקה", true); return; }
            var warnings = ValidateForExport();
            if (warnings.Count > 0 && DialogOwner is Window owner)
            {
                var answer = await Nikse.SubtitleEdit.Features.Shared.MessageBox.Show(owner, "בדיקת OST לפני ייצוא",
                    string.Join("\n", warnings.Take(12)) + "\n\nלהמשיך בייצוא בכל זאת?",
                    Nikse.SubtitleEdit.Features.Shared.MessageBoxButtons.YesNo,
                    Nikse.SubtitleEdit.Features.Shared.MessageBoxIcon.Question);
                if (answer != Nikse.SubtitleEdit.Features.Shared.MessageBoxResult.Yes) return;
            }
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title            = "שמור קובץ Word",
                SuggestedFileName = Safe(BuildTitle()) + ".docx",
                FileTypeChoices  = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Word Document")
                        { Patterns = new[] { "*.docx" } }
                }
            });
            if (file == null) return;
            try
            {
                WriteDocx(file.Path.LocalPath, rows);
                ShowStatus($"✓ נשמר: {file.Name}");
            }
            catch (Exception ex) { ShowStatus("שגיאה: " + ex.Message, true); }
        }

        private async Task BtnSave_ClickAsync()
        {
            _gridOst.CommitEdit();
            SyncLinkedRows();
            var st   = BuildProjectState();
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title            = "שמור פרוייקט",
                SuggestedFileName = Safe(st.ProjectTitle.Length > 0 ? st.ProjectTitle : "project") + ".ostproject",
                FileTypeChoices  = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("OST Project")
                        { Patterns = new[] { "*.ostproject" } }
                }
            });
            if (file == null) return;
            try
            {
                await File.WriteAllTextAsync(file.Path.LocalPath, SerializeState(st), Encoding.UTF8);
                ShowStatus("פרוייקט נשמר");
                _isDirty = false;
            }
            catch (Exception ex) { ShowStatus("שגיאה: " + ex.Message, true); }
        }

        private async Task BtnLoad_ClickAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "פתח פרוייקט",
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("OST Project")
                        { Patterns = new[] { "*.ostproject" } }
                }
            });
            if (files.Count == 0) return;
            try
            {
                string json = await File.ReadAllTextAsync(files[0].Path.LocalPath, Encoding.UTF8);
                var st = DeserializeState(json);
                if (st == null) { ShowStatus("קובץ לא תקין", true); return; }
                ApplyProjectState(st);
                ShowStatus("פרוייקט נטען");
                MarkDirty();
            }
            catch (Exception ex) { ShowStatus("שגיאה: " + ex.Message, true); }
        }

        internal void WriteDocx(string path, List<OstEntry> entries)
        {
            const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            string P(string text, bool rtl = true) => "<w:p><w:pPr>" + (rtl ? "<w:bidi/>" : "") + "</w:pPr><w:r><w:t xml:space=\"preserve\">" + H(text).Replace("\n", "</w:t><w:br/><w:t xml:space=\"preserve\">") + "</w:t></w:r></w:p>";
            string Cell(string text, bool rtl = true) => "<w:tc>" + P(text, rtl) + "</w:tc>";
            var body = new StringBuilder(P(BuildTitle()));
            body.Append("<w:tbl><w:tblPr><w:bidiVisual/><w:tblBorders>");
            foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                body.Append($"<w:{edge} w:val=\"single\" w:sz=\"4\"/>");
            body.Append("</w:tblBorders></w:tblPr><w:tblGrid><w:gridCol w:w=\"1800\"/><w:gridCol w:w=\"6000\"/>" + (_includeNotes ? "<w:gridCol w:w=\"1800\"/>" : "") + "</w:tblGrid><w:tr>" + Cell("TC", false) + Cell("כיתוב") + (_includeNotes ? Cell("הערות") : "") + "</w:tr>");
            foreach (var e in entries)
                body.Append("<w:tr>" + Cell(e.StartTime, false) + Cell(e.Text) + (_includeNotes ? Cell(e.Notes) : "") + "</w:tr>");
            body.Append("</w:tbl><w:sectPr/>");
            using var stream = File.Create(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            void Part(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
            Part("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Part("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Part("word/document.xml", $"<w:document xmlns:w=\"{ns}\"><w:body>{body}</w:body></w:document>");
        }

        // ── Status / helpers ──────────────────────────────────────────────────

        private void ShowStatus(string msg, bool err = false)
        {
            StatusChanged?.Invoke(msg);
            _lblStatus.Text       = msg;
            _lblStatus.Foreground = err
                ? new SolidColorBrush(Color.Parse("#FCA5A5"))
                : new SolidColorBrush(Color.Parse("#86EFAC"));
        }

        private string BuildTitle()
        {
            string t = _txtTitle.Text?.Trim() ?? ""; if (t.Length == 0) t = "OST";
            if (_chkSeasonEp.IsChecked == true)
            {
                string se = _txtSeason.Text?.Trim() ?? "", ep = _txtEpisode.Text?.Trim() ?? "";
                if (se.Length > 0 && ep.Length > 0) t += $" - עונה {se} פרק {ep}";
                else if (se.Length > 0)              t += $" - עונה {se}";
                else if (ep.Length > 0)              t += $" - פרק {ep}";
            }
            if (!t.ToLower().Contains("ost")) t += " - OST";
            return t;
        }

        private static Button MakeButton(string text, string hexColor) => new Button
        {
            Content    = text,
            Background = new SolidColorBrush(Color.Parse(hexColor)),
            Foreground = Brushes.White,
            Padding    = new Thickness(10, 4),
            Margin     = new Thickness(4, 0),
            CornerRadius = new CornerRadius(4),
        };

        private static string H(string? s) =>
            (s ?? "").Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;");
        private static string Safe(string s) =>
            Regex.Replace(s, @"[^\w\s\-א-ת]", "").Trim();

        // ── JSON helpers (same as v4) ─────────────────────────────────────────

        internal static string SerializeState(ProjectState s) =>
            JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });

        internal static ProjectState? DeserializeState(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
                var state = JsonSerializer.Deserialize<ProjectState>(json);
                if (state == null || state.Entries == null || state.Entries.Any(e => e == null)) return null;
                state.ProjectTitle ??= ""; state.Season ??= ""; state.Episode ??= "";
                foreach (var e in state.Entries) { e.StartTime ??= ""; e.Text ??= ""; e.Notes ??= ""; }
                return state;
            }
            catch (JsonException) { return null; }
        }
    }

    // ── Extension helpers ─────────────────────────────────────────────────────

    internal static class AvaloniaExt
    {
        public static T WithCol<T>(this T ctrl, int col) where T : Control
        {
            Grid.SetColumn(ctrl, col); return ctrl;
        }
        public static T WithDock<T>(this T ctrl, Dock dock) where T : Control
        {
            DockPanel.SetDock(ctrl, dock); return ctrl;
        }
        public static T Also<T>(this T obj, Action<T> action) { action(obj); return obj; }
    }
}

