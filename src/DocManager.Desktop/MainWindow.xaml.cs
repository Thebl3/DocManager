using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using FormsScreen = System.Windows.Forms.Screen;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using DocManager.Core;
using DocManager.Dialux;
using DocManager.Photometry;
using DocManager.Signify;
using Microsoft.Win32;

namespace DocManager.Desktop;

public partial class MainWindow : Window
{
    private readonly MainState _state = new();
    private readonly ObservableCollection<PdfInput> _pdfs = [];
    private readonly PdfTextExtractor _pdfExtractor = new();
    private readonly DialuxParser _dialuxParser = new();
    private readonly ExcelExporter _excelExporter = new();
    private readonly CadSymbolExcelService _cadSymbolExcelService = new();
    private readonly CadSymbolRepository _cadSymbolRepository = new();
    private readonly ICadSymbolClipboard _cadSymbolClipboard = new WpfCadSymbolClipboard();
    private readonly ObservableCollection<CadSymbolListItem> _cadSymbolModels = [];
    private readonly PricelistReader _pricelistReader = new();
    private IReadOnlyList<ProductRecord> _pricelist = [];
    private IReadOnlyList<string> _cadTargetWorkbooks = [];
    private CancellationTokenSource? _cancellation;
    private string? _excelOutput;
    private LdtDocument? _ldtDocument;
    private IesSourceSnapshot? _iesSnapshot;
    private string? _iesCatalogueCandidatePath;
    private bool _ldtDirty;
    private bool _updatingLdtUi;
    private readonly WindowStateStore _windowStateStore = new();
    private readonly UserSettingsStore _userSettingsStore = new();
    private UserSettingsData _userSettings = new();
    private readonly IFileLauncher _fileLauncher;
    private bool _isClosing;
    private NotifyIcon? _trayIcon;
    private Task? _pricelistLoadTask;
    private CancellationTokenSource? _pricelistLoadCts;
    private IReadOnlyList<DownloadLogEntry>? _lastDownloadResults;
    private readonly ObservableCollection<DownloadResultRow> _downloadResultRows = [];
    private System.Windows.Media.Animation.Storyboard? _drawerStoryboard;
    private FloatingImageWindow? _floatingImageWindow;
    private string? _floatingImagePath;

    public MainWindow() : this(new ShellFileLauncher())
    {
    }

    internal MainWindow(IFileLauncher fileLauncher)
    {
        _fileLauncher = fileLauncher ?? throw new ArgumentNullException(nameof(fileLauncher));
        InitializeComponent();
        RestoreWindowState();
        DataContext = _state;
        PdfList.ItemsSource = _pdfs;
        _pdfs.CollectionChanged += (_, _) => UpdatePdfListPlaceholder();
        CadSymbolGrid.ItemsSource = _cadSymbolModels;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _userSettings = _userSettingsStore.LoadResolved();
        CataloguePathBox.Text = _userSettings.CatalogueRoot;
        DownloadRootBox.Text = _userSettings.CatalogueRoot;
        PricelistBox.Text = _userSettings.PricelistPath;
        ExistingFormBox.Text = _userSettings.ExistingFormExcelPath;
        CodesBox.Text = _userSettings.PendingCodes;
        MergeCheck.IsChecked = _userSettings.MergeReports;
        AutoUpdateCadSymbolsCheck.IsChecked = _userSettings.AutoUpdateCadSymbols ?? true;
        DownloadIesCheck.IsChecked = _userSettings.DownloadIes ?? true;
        UpdatePdfListPlaceholder();
        UpdateLdtEditorPlaceholder();
        DownloadResultsGrid.ItemsSource = _downloadResultRows;
        DownloadResultsOverlay.Visibility = System.Windows.Visibility.Hidden;
        FloatingOpacitySlider.Value = _userSettings.FloatingImageOpacityPercent;
        FloatingImageTopmostCheck.IsChecked = _userSettings.FloatingImageTopmost;
        FloatingImageClickThroughCheck.IsChecked = _userSettings.FloatingImageClickThrough;
        if (!string.IsNullOrEmpty(_userSettings.LastFloatingImagePath))
        {
            _floatingImagePath = _userSettings.LastFloatingImagePath;
            FloatingImagePathText.Text = _userSettings.LastFloatingImagePath;
            UpdateFloatingImagePreview();
        }
    }

    private void SelectPdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            Multiselect = true,
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastPdfInputDirectory)
        };
        if (dialog.ShowDialog() == true) AddPdfs(dialog.FileNames);
    }

    private void PdfList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) AddPdfs((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    private void AddPdfs(IEnumerable<string> paths)
    {
        var accepted = paths.Where(File.Exists)
            .Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .ToArray();
        foreach (var path in accepted)
            if (!_pdfs.Any(item => item.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) _pdfs.Add(new PdfInput(path));
        if (accepted.Length > 0)
        {
            _userSettings = _userSettings with { LastPdfInputDirectory = Path.GetDirectoryName(accepted[^1])! };
            SaveUserSettings();
        }
    }

    private void RemovePdf_Click(object sender, RoutedEventArgs e) { if (PdfList.SelectedItem is PdfInput selected) _pdfs.Remove(selected); }
    private void SelectExcelOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = "Lighting Schedule.xlsx",
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastExcelOutputDirectory)
        };
        if (dialog.ShowDialog() != true) return;
        _excelOutput = Path.GetFullPath(dialog.FileName);
        _userSettings = _userSettings with { LastExcelOutputDirectory = Path.GetDirectoryName(_excelOutput)! };
        SaveUserSettings();
        Log(DialuxLog, $"Output: {_excelOutput}");
    }

    private async void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        var sources = _pdfs.Select(item => item.FullPath).ToArray();
        if (sources.Length == 0) { MessageBox.Show("Hãy chọn ít nhất một PDF."); return; }
        var merge = MergeCheck.IsChecked == true;
        var selectedOutput = _excelOutput;
        var cataloguePath = CataloguePathBox.Text.Trim();
        if (!merge && sources.Length > 1 && selectedOutput is not null)
            Log(DialuxLog, $"Xuất riêng: dùng thư mục của output đã chọn ({Path.GetDirectoryName(selectedOutput)}); tên tệp được tạo từ từng PDF.");

        await RunBusy(async token =>
        {
            var parsed = await ParseReports(sources, token);
            var catalogue = await BuildCatalogue(cataloguePath, token);
            var created = new List<string>();
            if (merge || parsed.Count == 1)
            {
                var output = selectedOutput ?? Path.ChangeExtension(sources[0], ".xlsx");
                var saved = await RunSynchronousSave(() => _excelExporter.Save(parsed.Select(item => item.Report), output, catalogue, parsed.Count > 1), token);
                created.Add(saved);
                Log(DialuxLog, $"Đã lưu: {saved}");
            }
            else
            {
                var outputs = DesktopExportPaths.ForSeparateExports(sources, selectedOutput)
                    .ToDictionary(item => item.SourcePath, item => item.OutputPath, StringComparer.OrdinalIgnoreCase);
                foreach (var item in parsed)
                {
                    token.ThrowIfCancellationRequested();
                    var saved = await RunSynchronousSave(() => _excelExporter.Save([item.Report], outputs[item.SourcePath], catalogue), token);
                    created.Add(saved);
                    Log(DialuxLog, $"Đã lưu: {saved}");
                }
            }
            await RefreshCadSymbolsAfterWorkbookSaveAsync(created, token);
        }, DialuxLog);
    }

    private sealed record PdfInput(string FullPath)
    {
        public string DisplayName => Path.GetFileName(FullPath);
        public ImageSource? Icon => WindowsFileIconProvider.GetSmallIcon(FullPath);
    }

    private sealed record ParsedReport(string SourcePath, DialuxReport Report);

    private async void CheckMissing_Click(object sender, RoutedEventArgs e)
    {
        var sources = _pdfs.Select(item => item.FullPath).ToArray();
        if (sources.Length == 0) return;
        var cataloguePath = CataloguePathBox.Text.Trim();
        await RunBusy(async token =>
        {
            var parsed = await ParseReports(sources, token);
            var reports = parsed.Select(item => item.Report).ToArray();
            var catalogue = await BuildCatalogue(cataloguePath, token);
            if (catalogue is null) throw new InvalidOperationException("Chưa cấu hình thư mục catalogue hợp lệ.");
            var missing = new DialuxConversionService().FindMissingCatalogueCodes(reports, catalogue);
            if (missing.Count == 0) { Log(DialuxLog, "Không thiếu catalogue."); return; }
            var allLuminaires = reports.SelectMany(report => report.Luminaires.Concat(report.Rooms.SelectMany(room => room.Luminaires))).ToArray();
            var handoff = missing.Select(code =>
            {
                var luminaire = allLuminaires.FirstOrDefault(item => item.ArticleNumber.Equals(code, StringComparison.OrdinalIgnoreCase));
                return luminaire is not null && !string.IsNullOrWhiteSpace(luminaire.Name) ? luminaire.Name : code;
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            CodesBox.Text = string.Join(Environment.NewLine, handoff); SelectTab(CatalogueTab); Log(DialuxLog, $"Đã chuyển {handoff.Length} mã/tên đầy đủ sang Downloader.");
        }, DialuxLog);
    }

    private async void AppendForm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExistingFormBox.Text))
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                InitialDirectory = ExistingDirectoryOrNull(DirectoryForFile(_userSettings.ExistingFormExcelPath) ?? _userSettings.LastExcelOutputDirectory)
            };
            if (dialog.ShowDialog() != true) return;
            ExistingFormBox.Text = Path.GetFullPath(dialog.FileName);
            PersistExistingForm();
        }
        var formPath = ExistingFormBox.Text.Trim();
        var sources = _pdfs.Select(item => item.FullPath).ToArray();
        var cataloguePath = CataloguePathBox.Text.Trim();
        await RunBusy(async token =>
        {
            if (sources.Length == 0)
            {
                var form = await Task.Run(() => _excelExporter.DetectLightingForm(formPath), token);
                Log(DialuxLog, $"Form: {form.WorksheetName}, header {form.HeaderTopRow}-{form.HeaderBottomRow}");
                return;
            }
            var parsed = await ParseReports(sources, token);
            var catalogue = await BuildCatalogue(cataloguePath, token);
            var result = await RunSynchronousSave(() => _excelExporter.Append(formPath, parsed.Select(item => item.Report), catalogue), token);
            Log(DialuxLog, $"Đã thêm {result.AddedRows}, bỏ trùng {result.SkippedDuplicates}.");
            await RefreshCadSymbolsAfterWorkbookSaveAsync([formPath], token);
        }, DialuxLog);
    }

    private async Task<IReadOnlyList<ParsedReport>> ParseReports(IReadOnlyList<string> sources, CancellationToken token) => await Task.Run(() =>
    {
        var reports = new List<ParsedReport>(sources.Count);
        foreach (var path in sources)
        {
            token.ThrowIfCancellationRequested();
            Log(DialuxLog, $"Đọc {Path.GetFileName(path)}");
            reports.Add(new ParsedReport(path, _dialuxParser.Parse(_pdfExtractor.Extract(path), Path.GetFileName(path))));
        }
        return (IReadOnlyList<ParsedReport>)reports;
    }, token);

    private async Task<CatalogueIndex?> BuildCatalogue(string path, CancellationToken token)
    {
        if (!Directory.Exists(path)) { Log(DialuxLog, "Không có catalogue; tiếp tục với dữ liệu DiaLux."); return null; }
        var catalogue = new CatalogueIndex();
        await Task.Run(() => catalogue.Build(path, token), token);
        return catalogue;
    }

    // ClosedXML append/save APIs are synchronous and cannot be interrupted once started.
    // Check cancellation only before starting so a completed save is never reported as cancelled.
    private static async Task RunSynchronousSave(Action save, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Task.Run(save);
    }

    private static async Task<T> RunSynchronousSave<T>(Func<T> save, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await Task.Run(save);
    }

    private async void SelectPricelist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            InitialDirectory = ExistingDirectoryOrNull(DirectoryForFile(_userSettings.PricelistPath) ?? Path.GetDirectoryName(_userSettings.CatalogueRoot))
        };
        if (dialog.ShowDialog() != true) return;
        PricelistBox.Text = Path.GetFullPath(dialog.FileName);
        PersistPricelist();
        await LoadPricelistAsync();
    }
    private void BrowseCatalogue_Click(object sender, RoutedEventArgs e) { var selected = BrowseFolder(CataloguePathBox.Text); if (selected is not null) SetCatalogueRoot(selected, true); }
    private void BrowseDownloadRoot_Click(object sender, RoutedEventArgs e) { var selected = BrowseFolder(DownloadRootBox.Text); if (selected is not null) SetCatalogueRoot(selected, true); }
    private async Task<bool> LoadPricelistAsync()
    {
        CancelInflightPricelistLoad();
        _pricelistLoadCts = new CancellationTokenSource();
        var token = _pricelistLoadCts.Token;
        var path = PricelistBox.Text.Trim();
        var task = Task.Run(() => LoadPricelistCore(path), token);
        _pricelistLoadTask = task;
        try
        {
            var (success, message, records) = await task;
            if (!token.IsCancellationRequested)
            {
                _pricelist = records;
                Log(DownloadLog, message);
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private (bool Success, string Message, IReadOnlyList<ProductRecord> Records) LoadPricelistCore(string path)
    {
        if (!File.Exists(path)) return (false, "Pricelist không tồn tại; đã xóa dữ liệu nạp trước đó.", []);
        try
        {
            var records = _pricelistReader.Read(path);
            return (true, $"Đã nạp {records.Count} sản phẩm từ {Path.GetFileName(path)}.", records);
        }
        catch (Exception exception)
        {
            return (false, ExceptionLogFormatter.Format($"Không nạp được {Path.GetFileName(path)}; đã xóa dữ liệu cũ", exception), []);
        }
    }

    private void CancelInflightPricelistLoad()
    {
        if (_pricelistLoadTask is not null && !_pricelistLoadTask.IsCompleted)
        {
            _pricelistLoadCts?.Cancel();
        }
    }
    private async void CodesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pricelist.Count == 0 && File.Exists(PricelistBox.Text))
        {
            await LoadPricelistAsync();
        }
        var query = SignifyTextInput.CurrentQuery(CodesBox.Text, CodesBox.CaretIndex);
        SuggestionsList.ItemsSource = _pricelistReader.Search(_pricelist, query);
    }
    private void SuggestionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionsList.SelectedItem is not ProductRecord record) return;
        var replacement = SignifyTextInput.ReplaceQueryAtCaret(CodesBox.Text, CodesBox.CaretIndex, record.MaterialDescription);
        CodesBox.Text = replacement.Text;
        CodesBox.CaretIndex = replacement.CaretIndex;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var codes = Codes();
        if (codes.Count == 0) return;
        if (!HasDownloadRoot(DownloadRootBox.Text))
        {
            Log(DownloadLog, "Product Family root không được để trống.");
            return;
        }
        if (_pricelist.Count == 0 && File.Exists(PricelistBox.Text) && !await LoadPricelistAsync()) return;
        var options = Options();
        var records = codes.Select(code => _pricelistReader.ResolveCode(_pricelist, code)).ToArray();
        var invalidFamily = records.FirstOrDefault(record => string.IsNullOrWhiteSpace(record.ProductFamily));
        if (invalidFamily is not null)
        {
            Log(DownloadLog, $"Không tải '{invalidFamily.MaterialDescription}': Product Family không được để trống.");
            return;
        }
        await RunBusy(async token =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var service = new CatalogueDownloadService(new SignifyClient(http));
            var csvPath = Path.Combine(options.OutputRoot, "download_log.csv");
            var progress = new Progress<DocManager.Core.OperationProgress>(value => { if (!_isClosing) { DownloadProgress.Value = value.Percentage; Log(DownloadLog, value.Message); } });
            var logs = await service.DownloadAsync(records, options, progress, token, LogDownloadAssets);
            try { CatalogueDownloadService.AppendCsvLog(csvPath, logs); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log(DownloadLog, ExceptionLogFormatter.Format($"Không ghi được CSV '{csvPath}'", exception));
            }
            if (!_isClosing) SetCatalogueRoot(options.OutputRoot, true);
            Log(DownloadLog, DownloadCompletionSummary(logs, options.DownloadIes));
            if (!_isClosing)
            {
                PopulateDownloadResults(logs);
                OpenDownloadResultsDrawer();
            }
        }, DownloadLog);
    }

    private async void CheckExisting_Click(object sender, RoutedEventArgs e)
    {
        var root = DownloadRootBox.Text.Trim();
        var queries = Codes().Select(code => ResolveFileQuery(code, root)).ToArray();
        await RunBusy(async token =>
        {
            if (!Directory.Exists(root)) { Log(DownloadLog, "Product Family chưa tồn tại."); return; }
            var manifest = new CanonicalAssetManifest(root);
            var result = await Task.Run(() => queries.Select(query =>
            {
                if (query.IsAmbiguous) return new { query.Code, Files = Array.Empty<string>(), Error = "Mã/tên khớp nhiều sản phẩm trong pricelist; không kiểm tra variant bất kỳ." };
                var lookup = query.Record is null ? null : manifest.Resolve(query.Record);
                if (lookup?.HasCollision == true) return new { query.Code, Files = Array.Empty<string>(), Error = "Manifest canonical có xung đột identity." };
                var files = lookup?.Entry is null
                    ? LegacyFiles(query, root, token)
                    : manifest.ExistingPaths(lookup.Entry).ToArray();
                return new { query.Code, Files = files, Error = string.Empty };
            }).ToArray(), token);
            foreach (var item in result)
            {
                if (item.Error.Length > 0) Log(DownloadLog, $"{item.Code}: {item.Error}");
                else Log(DownloadLog, $"{item.Code}: PDF {item.Files.Count(path => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))}, IES {item.Files.Count(path => path.EndsWith(".ies", StringComparison.OrdinalIgnoreCase))}, LDT {item.Files.Count(path => path.EndsWith(".ldt", StringComparison.OrdinalIgnoreCase))}");
            }
        }, DownloadLog);
    }

    private async void ConvertCodes_Click(object sender, RoutedEventArgs e)
    {
        var codes = Codes();
        var root = DownloadRootBox.Text.Trim();
        await RunBusy(async token =>
        {
            using var http = new HttpClient();
            var service = new CatalogueDownloadService(new SignifyClient(http));
            var records = codes.Select(code => _pricelistReader.ResolveCode(_pricelist, code)).ToArray();
            var conversions = await Task.Run(() => service.ConvertProducts(records, root, new Progress<string>(file => Log(DownloadLog, $"LDT: {file}")), token), token);
            if (conversions.Count > 0)
            {
                var audit = conversions.Select(item => new DownloadLogEntry
                {
                    ProductFamily = item.Context.ProductFamily,
                    Material = item.Context.Material,
                    MaterialDescription = item.Context.ProductName,
                    OutputFile = item.Context.PdfPath,
                    IesFile = item.Context.IesPath,
                    LdtFile = item.Result.OutputPath,
                    Status = "converted"
                }).ToArray();
                CatalogueDownloadService.AppendCsvLog(Path.Combine(Path.GetFullPath(root), "download_log.csv"), audit);
            }
            Log(DownloadLog, $"Đã tạo {conversions.Count} LDT.");
        }, DownloadLog);
    }

    private async void ExportLdtFiles_Click(object sender, RoutedEventArgs e) =>
        await ExportCatalogueFiles("LDT", (codes, records, root, destinationParent, token) =>
        {
            var result = new CatalogueLdtExportService().Export(codes, records, root, destinationParent, token);
            return (result.Items.Select(item => (item.Code, item.Message)).ToArray(), result.Summary);
        });

    private async void ExportPdfFiles_Click(object sender, RoutedEventArgs e) =>
        await ExportCatalogueFiles("PDF", (codes, records, root, destinationParent, token) =>
        {
            var result = new CataloguePdfExportService().Export(codes, records, root, destinationParent, token);
            return (result.Items.Select(item => (item.Code, item.Message)).ToArray(), result.Summary);
        });

    private async Task ExportCatalogueFiles(
        string assetLabel,
        Func<IReadOnlyList<string>, IReadOnlyList<ProductRecord>, string, string, CancellationToken, (IReadOnlyList<(string Code, string Message)> Items, string Summary)> export)
    {
        var codes = RawCodes();
        if (codes.Count == 0)
        {
            Log(DownloadLog, $"Chưa nhập mã để xuất {assetLabel}.");
            return;
        }
        if (_pricelist.Count == 0 && File.Exists(PricelistBox.Text) && !await LoadPricelistAsync()) return;
        if (_pricelist.Count == 0)
        {
            Log(DownloadLog, "Chưa nạp pricelist để resolve mã và Product Family.");
            return;
        }

        var destinationParent = BrowseFolder(DownloadRootBox.Text);
        if (destinationParent is null) return;
        var root = DownloadRootBox.Text.Trim();
        var records = _pricelist;
        await RunBusy(async token =>
        {
            var result = await Task.Run(() => export(codes, records, root, destinationParent, token), token);
            foreach (var item in result.Items) Log(DownloadLog, $"{item.Code}: {item.Message}");
            Log(DownloadLog, result.Summary);
        }, DownloadLog);
    }

    private async void OpenProductFolder_Click(object sender, RoutedEventArgs e)
    {
        var root = DownloadRootBox.Text.Trim();
        var code = Codes().FirstOrDefault();
        var query = code is null ? null : ResolveFileQuery(code, root);
        await RunBusy(async token =>
        {
            if (query is null || !Directory.Exists(root)) return;
            if (query.IsAmbiguous)
            {
                Log(DownloadLog, $"{query.Code}: mã/tên khớp nhiều sản phẩm trong pricelist; không mở folder variant bất kỳ.");
                return;
            }
            var manifest = new CanonicalAssetManifest(root);
            var found = await Task.Run(() =>
            {
                var lookup = query.Record is null ? null : manifest.Resolve(query.Record);
                if (lookup?.HasCollision == true) return null;
                return lookup?.Entry is null ? LegacyFiles(query, root, token).FirstOrDefault() : manifest.ExistingPaths(lookup.Entry).FirstOrDefault();
            }, token);
            if (found is not null) OpenFolder(ProductFamilyDirectory(found, root));
        }, DownloadLog);
    }

    private async void OpenProductPdf_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeForProductPdf(CodesBox.Text, CodesBox.CaretIndex);
        if (code is null)
        {
            const string message = "Đặt con trỏ vào mã cần mở, hoặc chỉ nhập một mã.";
            Log(DownloadLog, message);
            MessageBox.Show(message, "Mở PDF theo mã");
            return;
        }

        if (!await EnsureCurrentPricelistForProductPdfAsync()) return;
        var root = DownloadRootBox.Text.Trim();
        var records = _pricelist;
        await RunBusy(async token =>
        {
            var resolution = await Task.Run(() => new CataloguePdfResolver(records).Resolve(code, root, token), token);
            if (!resolution.IsFound || resolution.PdfPath is null)
            {
                Log(DownloadLog, resolution.Message);
                return;
            }

            _fileLauncher.Open(resolution.PdfPath);
            Log(DownloadLog, $"Đã mở PDF cho '{code}': {resolution.PdfPath}");
        }, DownloadLog);
    }

    private async Task<bool> EnsureCurrentPricelistForProductPdfAsync()
    {
        var path = PricelistBox.Text.Trim();
        if (!File.Exists(path))
        {
            _pricelist = [];
            Log(DownloadLog, "Pricelist không tồn tại; không mở PDF theo dữ liệu cũ.");
            return false;
        }
        return await LoadPricelistAsync();
    }

    private void OpenLdt_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmReplaceDirtyLdt()) return;
        var dialog = new OpenFileDialog
        {
            Filter = "Eulumdat (*.ldt)|*.ldt",
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastLdtDirectory)
        };
        if (dialog.ShowDialog() != true) return;
        RememberEditorDirectory(dialog.FileName, EditorPathKind.Ldt);
        LoadLdt(dialog.FileName);
    }

    private async void OpenIes_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmReplaceDirtyLdt()) return;
        var dialog = new OpenFileDialog
        {
            Filter = "IES photometry (*.ies)|*.ies",
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastIesDirectory)
        };
        if (dialog.ShowDialog() != true) return;
        RememberEditorDirectory(dialog.FileName, EditorPathKind.Ies);
        await PreviewIesAsync(dialog.FileName, null);
    }

    private void LdtEditor_DragOver(object sender, DragEventArgs e)
    {
        var route = ResolvePhotometryDrop(e.Data);
        e.Effects = !_state.IsBusy && !_isClosing && route.IsValid ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void LdtEditor_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_state.IsBusy || _isClosing) return;

        var route = ResolvePhotometryDrop(e.Data);
        if (!route.IsValid || route.DocumentPath is null)
        {
            MessageBox.Show(route.Message, "Kéo thả LDT/IES");
            return;
        }

        CommitPendingLdtEdit();
        if (!ConfirmReplaceDirtyLdt()) return;
        SelectTab(LdtEditorTab);
        if (route.Kind == PhotometryDropKind.Ldt)
        {
            RememberEditorDirectory(route.DocumentPath, EditorPathKind.Ldt);
            LoadLdt(route.DocumentPath);
            return;
        }

        RememberEditorDirectory(route.DocumentPath, EditorPathKind.Ies);
        if (route.CataloguePdfPath is not null) RememberEditorDirectory(route.CataloguePdfPath, EditorPathKind.PdfCatalogue);
        await PreviewIesAsync(route.DocumentPath, route.CataloguePdfPath);
    }

    private static PhotometryDropRoute ResolvePhotometryDrop(System.Windows.IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths
                ? PhotometryDropRoute.Resolve(paths)
                : PhotometryDropRoute.Resolve(null);
        }
        catch
        {
            return PhotometryDropRoute.Resolve(null);
        }
    }

    private async Task PreviewIesAsync(string iesPath, string? catalogueCandidatePath)
    {
        if (_state.IsBusy) return;
        string sourcePath;
        try { sourcePath = Path.GetFullPath(iesPath); }
        catch (Exception exception) { ShowLdtError(exception, "Không thể mở IES"); return; }

        _state.IsBusy = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            SetLdtStatus("Đang đọc IES...");
            var prepared = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var snapshot = new IesSourceReader().ReadFile(sourcePath);
                var projection = IesPreviewProjection.Create(snapshot);
                cancellation.Token.ThrowIfCancellationRequested();
                return (Snapshot: snapshot, Projection: projection);
            }, cancellation.Token);
            if (_isClosing) return;
            SetIesPreview(prepared.Snapshot, prepared.Projection, catalogueCandidatePath);
        }
        catch (OperationCanceledException) { if (!_isClosing) SetLdtStatus("Đã hủy đọc IES."); }
        catch (Exception exception)
        {
            if (!_isClosing) ShowLdtError(exception, "Không thể đọc IES");
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_isClosing) _state.IsBusy = false;
        }
    }

    private void SetIesPreview(IesSourceSnapshot snapshot, IesPreviewProjection projection, string? catalogueCandidatePath)
    {
        _iesSnapshot = snapshot;
        _iesCatalogueCandidatePath = catalogueCandidatePath is null ? null : Path.GetFullPath(catalogueCandidatePath);
        _ldtDocument = null;
        _updatingLdtUi = true;
        try
        {
            LdtRawBox.Text = snapshot.DecodedText;
            LdtGrid.ItemsSource = projection.Rows;
            LdtPathText.Text = snapshot.SourcePath;
        }
        finally { _updatingLdtUi = false; }
        _ldtDirty = false;
        _state.IsIesPreview = true;
        _state.HasPhotometryDocument = true;
        UpdateLdtEditorPlaceholder();
        var readiness = snapshot.Document.LumensPerLamp == -1 ? "absolute: cần PDF có flux dương" : "relative: sẵn sàng chuyển";
        var candidate = _iesCatalogueCandidatePath is null ? string.Empty : $", đã ghi nhớ PDF {Path.GetFileName(_iesCatalogueCandidatePath)}";
        SetLdtStatus($"IES {snapshot.Document.Version}; {snapshot.EncodingDisplayName}; {snapshot.NewLines.DisplayName}; {snapshot.ByteCount:N0} byte; Gamma {snapshot.Document.VerticalAngleCount}, C {snapshot.Document.HorizontalAngleCount}; {readiness}{candidate}.");
    }

    private async void ConvertIes_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _iesSnapshot;
        if (_state.IsBusy || snapshot is null) return;
        var sourcePath = snapshot.SourcePath;
        var canonicalName = ProductAssetName.CanonicalBaseName(Path.GetFileNameWithoutExtension(sourcePath));
        var pdfPath = _iesCatalogueCandidatePath;
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            var resolution = IesCataloguePdfResolver.Resolve(sourcePath, canonicalName);
            if (resolution.IsFound)
            {
                pdfPath = resolution.PdfPath;
                if (pdfPath is not null) RememberEditorDirectory(pdfPath, EditorPathKind.PdfCatalogue);
                SetLdtStatus(resolution.Message);
            }
            else
            {
                var select = MessageBox.Show(
                    $"{resolution.Message}{Environment.NewLine}{Environment.NewLine}Chọn catalogue PDF?",
                    "Chọn catalogue PDF?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (select == MessageBoxResult.Yes)
                {
                    var pdfDialog = new OpenFileDialog
                    {
                        Filter = "Catalogue PDF (*.pdf)|*.pdf",
                        InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastPdfCatalogueDirectory) ?? DefaultIesFamilyDirectory(sourcePath)
                    };
                    if (pdfDialog.ShowDialog() != true) return;
                    pdfPath = pdfDialog.FileName;
                    RememberEditorDirectory(pdfPath, EditorPathKind.PdfCatalogue);
                }
            }
        }

        if (snapshot.Document.LumensPerLamp == -1 && string.IsNullOrWhiteSpace(pdfPath))
        {
            const string message = "IES absolute (lumens per lamp = -1) bắt buộc có catalogue PDF chứa quang thông kỹ thuật dương.";
            SetLdtStatus(message);
            MessageBox.Show(message, "Thiếu catalogue PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Filter = "Eulumdat (*.ldt)|*.ldt",
            DefaultExt = ".ldt",
            AddExtension = true,
            FileName = canonicalName + ".ldt",
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastLdtDirectory) ??
                               ExistingDirectoryOrNull(DefaultLdtDirectory(sourcePath)) ??
                               Path.GetDirectoryName(sourcePath)
        };
        if (saveDialog.ShowDialog() != true) return;
        var destination = Path.GetFullPath(saveDialog.FileName);
        RememberEditorDirectory(destination, EditorPathKind.Ldt);
        var overwrite = File.Exists(destination);
        if (overwrite && MessageBox.Show(
                $"Tệp đã tồn tại:{Environment.NewLine}{destination}{Environment.NewLine}{Environment.NewLine}Ghi đè và tạo .bak?",
                "Xác nhận ghi đè",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _state.IsBusy = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            SetLdtStatus("Đang chuyển snapshot IES → LDT...");
            var request = new IesToLdtSaveAsRequest(sourcePath, destination, pdfPath, canonicalName, overwrite);
            var result = await Task.Run(() => new IesToLdtSaveAsService().SaveAs(snapshot, request, cancellation.Token), cancellation.Token);
            if (_isClosing) return;

            SetLdtDocument(result.Document);
            var warningText = result.Warnings.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Select(warning => $"- {warning.Message}"));
            SetLdtStatus($"Đã chuyển snapshot và nạp {Path.GetFileName(result.Conversion.OutputPath)}.{warningText}");
            if (result.Warnings.Count > 0)
                MessageBox.Show($"Đã chuyển và lưu LDT.{warningText}", "IES → LDT", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { if (!_isClosing) SetLdtStatus("Đã hủy chuyển IES → LDT."); }
        catch (Exception exception) { if (!_isClosing) ShowLdtError(exception, "Không chuyển được IES → LDT"); }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_isClosing) _state.IsBusy = false;
        }
    }

    private void LoadLdt(string path)
    {
        try { SetLdtDocument(LdtDocument.Load(path)); }
        catch (Exception exception) { ShowLdtError(exception, "Không mở được LDT"); }
    }

    private void SetLdtDocument(LdtDocument document)
    {
        _ldtDocument = document;
        _iesSnapshot = null;
        _iesCatalogueCandidatePath = null;
        _state.IsIesPreview = false;
        _state.HasPhotometryDocument = true;
        _updatingLdtUi = true;
        try
        {
            LdtRawBox.Text = document.RawText;
            RefreshGrid();
            LdtPathText.Text = document.Path;
        }
        finally { _updatingLdtUi = false; }
        _ldtDirty = false;
        SetLdtStatus($"{document.LineCount} dòng, {document.Encoding.EncodingName}");
        UpdateLdtEditorPlaceholder();
    }

    private void RefreshGrid() { LdtGrid.ItemsSource = _ldtDocument?.Fields.Select(field => new EditableLdtField { Index = field.Index, Name = field.Name, Value = field.Value }).ToArray(); }
    private void SyncLdt_Click(object sender, RoutedEventArgs e)
    {
        if (_ldtDocument is null) return;
        CommitPendingLdtEdit();
        _updatingLdtUi = true;
        try
        {
            RefreshGrid();
            LdtRawBox.Text = _ldtDocument.RawText;
        }
        finally { _updatingLdtUi = false; }
        SetLdtStatus("Đã đồng bộ grid → raw.");
    }
    private void LdtGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel || _ldtDocument is null || e.Row.Item is not EditableLdtField item || e.EditingElement is not TextBox box) return;
        _ldtDocument.SetValue(item.Index, box.Text);
        _updatingLdtUi = true;
        try { LdtRawBox.Text = _ldtDocument.RawText; }
        finally { _updatingLdtUi = false; }
        _ldtDirty = true;
        SetLdtStatus("Có thay đổi chưa lưu.");
    }
    private void LdtRawBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingLdtUi || _ldtDocument is null) return;
        _ldtDocument.ReplaceRawText(LdtRawBox.Text);
        _ldtDirty = true;
        SetLdtStatus("Có thay đổi chưa lưu.");
    }
    private async void ReloadLdt_Click(object sender, RoutedEventArgs e)
    {
        if (_iesSnapshot is not null)
        {
            var sourcePath = _iesSnapshot.SourcePath;
            var candidate = _iesCatalogueCandidatePath;
            await PreviewIesAsync(sourcePath, candidate);
            return;
        }
        if (_ldtDocument is not null && ConfirmReplaceDirtyLdt()) LoadLdt(_ldtDocument.Path);
    }
    private void SaveLdt_Click(object sender, RoutedEventArgs e)
    {
        if (_ldtDocument is null) return;
        try
        {
            PrepareLdtForSave();
            _ldtDocument.Save();
            RefreshSavedLdt("Đã lưu và tạo .bak khi ghi đè.");
        }
        catch (Exception exception) { ShowLdtError(exception, "Không lưu được LDT"); }
    }
    private void SaveAsLdt_Click(object sender, RoutedEventArgs e)
    {
        if (_ldtDocument is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Eulumdat (*.ldt)|*.ldt",
            FileName = Path.GetFileName(_ldtDocument.Path),
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastLdtDirectory) ?? Path.GetDirectoryName(_ldtDocument.Path)
        };
        if (dialog.ShowDialog() != true) return;
        RememberEditorDirectory(dialog.FileName, EditorPathKind.Ldt);
        try
        {
            PrepareLdtForSave();
            _ldtDocument.Save(dialog.FileName);
            RefreshSavedLdt("Đã lưu thành tệp đã chọn.");
        }
        catch (Exception exception) { ShowLdtError(exception, "Không lưu được LDT"); }
    }
    private void CommitPendingLdtEdit()
    {
        LdtGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LdtGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
    private void PrepareLdtForSave() => CommitPendingLdtEdit();
    private void RefreshSavedLdt(string message)
    {
        _updatingLdtUi = true;
        try
        {
            RefreshGrid();
            LdtRawBox.Text = _ldtDocument!.RawText;
            LdtPathText.Text = _ldtDocument.Path;
        }
        finally { _updatingLdtUi = false; }
        _ldtDirty = false;
        SetLdtStatus($"{message} {_ldtDocument.LineCount} dòng, {_ldtDocument.Encoding.EncodingName}");
    }
    private bool ConfirmReplaceDirtyLdt() => !_ldtDirty || MessageBox.Show(
        "LDT hiện tại có thay đổi chưa lưu. Bỏ thay đổi và thay bằng tài liệu khác?",
        "Thay tài liệu LDT",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private static string DefaultIesFamilyDirectory(string iesPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(iesPath))!);
        return directory.Name.Equals("IES", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null
            ? directory.Parent.FullName
            : directory.FullName;
    }
    private static string DefaultLdtDirectory(string iesPath)
    {
        var sourceDirectory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(iesPath))!);
        return sourceDirectory.Name.Equals("IES", StringComparison.OrdinalIgnoreCase) && sourceDirectory.Parent is not null
            ? Path.Combine(sourceDirectory.Parent.FullName, "LDT")
            : sourceDirectory.FullName;
    }
    private void SetLdtStatus(string message) => LdtStatus.Text = message;
    private void ShowLdtError(Exception exception, string title)
    {
        SetLdtStatus($"Lỗi: {exception.Message}");
        MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void OpenLdtFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _iesSnapshot?.SourcePath ?? _ldtDocument?.Path;
        if (path is not null) OpenFolder(Path.GetDirectoryName(path)!);
    }

    private async Task RunBusy(Func<CancellationToken, Task> action, TextBox log)
    {
        if (_state.IsBusy || _isClosing) return;
        _state.IsBusy = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var operationCompleted = false;
        try { await action(cancellation.Token); operationCompleted = true; }
        catch (OperationCanceledException) when (!operationCompleted) { if (!_isClosing) Log(log, "Đã hủy."); }
        catch (Exception exception) { if (!_isClosing) { Log(log, ExceptionLogFormatter.Format("Lỗi", exception)); MessageBox.Show(exception.Message, "DocManager"); } }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_isClosing) _state.IsBusy = false;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel) return;
        if (!_isClosing && _ldtDirty && MessageBox.Show(
                "LDT hiện tại có thay đổi chưa lưu. Đóng và bỏ thay đổi?",
                "Đóng LDT Editor",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SaveWindowState();
        SaveManualPathSettings();
        if (_floatingImageWindow is not null)
        {
            _floatingImageWindow.Close();
            _floatingImageWindow = null;
        }
        _isClosing = true;
        _cancellation?.Cancel();
        _state.IsBusy = false;
        DisposeTrayIcon();
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            Hide();
            EnsureTrayIconExists();
            _trayIcon!.Visible = true;
        }
        else
        {
            if (_trayIcon is not null) _trayIcon.Visible = false;
        }
    }

    private void EnsureTrayIconExists()
    {
        if (_trayIcon is not null) return;

        var icon = GetApplicationIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "DocManager",
            Visible = false,
            ContextMenuStrip = CreateTrayContextMenu()
        };
        _trayIcon.MouseClick += TrayIcon_MouseClick;
        _trayIcon.MouseDoubleClick += TrayIcon_MouseDoubleClick;
    }

    private static System.Drawing.Icon GetApplicationIcon()
    {
        // Try to load from pack URI resource
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch
        {
        }

        // Fall back to extract from process
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
        }

        // Fall back to cloned system icon (so we own the instance and can dispose it safely)
        try
        {
            return new System.Drawing.Icon(System.Drawing.SystemIcons.Application, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch
        {
        }

        // Final fallback to system icon
        return System.Drawing.SystemIcons.Application;
    }

    private ContextMenuStrip CreateTrayContextMenu()
    {
        var menu = new ContextMenuStrip();
        var restoreItem = new ToolStripMenuItem("Mở cửa sổ", null, (s, e) => RestoreWindowFromTray());

        var toggleImageItem = new ToolStripMenuItem("Ẩn/hiện ảnh ghim", null, (s, e) => ToggleFloatingImageVisibility());
        var clickThroughItem = new ToolStripMenuItem("Xuyên chuột", null, (s, e) => ToggleFloatingImageClickThrough());

        menu.Items.Add(restoreItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggleImageItem);
        menu.Items.Add(clickThroughItem);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Thoát", null, (s, e) => Close());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void ToggleFloatingImageVisibility()
    {
        if (_floatingImageWindow is null || string.IsNullOrEmpty(_floatingImagePath)) return;
        if (_floatingImageWindow.IsVisible)
            _floatingImageWindow.Hide();
        else
            FloatingImage_ShowOverlay_Click(null!, null!);
    }

    private void ToggleFloatingImageClickThrough()
    {
        if (_floatingImageWindow is null) return;
        FloatingImageClickThroughCheck.IsChecked = !FloatingImageClickThroughCheck.IsChecked;
    }

    private void TrayIcon_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreWindowFromTray();
    }

    private void TrayIcon_MouseDoubleClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreWindowFromTray();
    }

    private void RestoreWindowFromTray()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.MouseClick -= TrayIcon_MouseClick;
        _trayIcon.MouseDoubleClick -= TrayIcon_MouseDoubleClick;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void RestoreWindowState()
    {
        if (!_windowStateStore.TryLoad(out var state) || state is null) return;

        var workAreas = FormsScreen.AllScreens
            .Select(screen => new WindowBounds(
                screen.WorkingArea.Left,
                screen.WorkingArea.Top,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height))
            .ToArray();
        if (!WindowPlacement.TryCreateFixedSize(state, workAreas, Width, Height, out var placement) || placement is null) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Bounds.Left;
        Top = placement.Bounds.Top;
        WindowState = WindowState.Normal;
    }

    private void SaveWindowState()
    {
        _windowStateStore.Save(new WindowStateData
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            WindowState = "Normal"
        });
    }

    private async Task RefreshCadSymbolsAfterWorkbookSaveAsync(IReadOnlyList<string> workbookPaths, CancellationToken token)
    {
        var fullPaths = workbookPaths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _cadTargetWorkbooks = fullPaths;
        ShowCadTarget("File vừa xuất", fullPaths);
        var scans = await Task.Run(() => fullPaths.Select(_cadSymbolExcelService.ScanModels).ToArray(), token);
        var merged = scans.SelectMany(scan => scan.Models)
            .GroupBy(model => model.Key, StringComparer.Ordinal)
            .Select(group => new CadSymbolScannedModel(group.Key, group.First().DisplayModel, group.Sum(model => model.UsageCount)))
            .OrderBy(model => model.DisplayModel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        await Task.Run(() => _cadSymbolRepository.RegisterModels(merged.Select(model => new CadSymbolModelRegistration(model.DisplayModel, "workbook-export"))), token);
        RefreshCadSymbolList(merged);
        var missing = merged.Count(model => _cadSymbolRepository.GetMapping(model.Key) is null);
        if (AutoUpdateCadSymbolsCheck.IsChecked == true)
            await ApplyCadSymbolsToTargetWorkbooksAsync([], token);
        if (missing > 0)
        {
            SelectTab(CadSymbolTab);
            SetCadSymbolStatus($"{merged.Length} model; còn {missing} model chưa có ảnh.");
        }
        else
        {
            Log(DialuxLog, $"Đã áp dụng CAD symbol đã lưu cho {merged.Length} model.");
            SetCadSymbolStatus($"Đã có mapping cho toàn bộ {merged.Length} model của batch vừa xuất.");
        }
    }

    private async void SelectExistingCadExcel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            Multiselect = false,
            InitialDirectory = ExistingDirectoryOrNull(_userSettings.LastCadExcelDirectory) ??
                               ExistingDirectoryOrNull(_userSettings.LastExcelOutputDirectory)
        };
        if (dialog.ShowDialog() != true) return;

        string fullPath;
        try { fullPath = Path.GetFullPath(dialog.FileName); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            ShowCadExcelSelectionError(exception.Message);
            return;
        }

        try
        {
            var scan = await Task.Run(() => _cadSymbolExcelService.ScanTargetWorkbook(fullPath));
            await Task.Run(() => _cadSymbolRepository.RegisterModels(scan.Models.Select(model =>
                new CadSymbolModelRegistration(model.DisplayModel, "workbook-selected"))));
            _cadTargetWorkbooks = [scan.WorkbookPath];
            RefreshCadSymbolList(scan.Models);
            ShowCadTarget("File đã chọn", _cadTargetWorkbooks);
            var directory = Path.GetDirectoryName(scan.WorkbookPath)!;
            _userSettings = _userSettings with { LastCadExcelDirectory = directory };
            SaveUserSettings();
            var missing = scan.Models.Count(model => _cadSymbolRepository.GetMapping(model.Key) is null);
            SelectTab(CadSymbolTab);
            SetCadSymbolStatus($"Đã đọc lại {scan.Models.Count} model từ file đã chọn; còn {missing} model chưa có ảnh. File chưa bị thay đổi.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowCadExcelSelectionError(exception.Message);
        }
    }

    private void ShowCadExcelSelectionError(string message)
    {
        SetCadSymbolStatus(message);
        MessageBox.Show(message, "Chọn file Excel có sẵn", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowCadTarget(string kind, IReadOnlyList<string> paths)
    {
        CadTargetKindText.Text = kind;
        CadTargetPathText.Text = paths.Count switch
        {
            0 => string.Empty,
            1 => paths[0],
            _ => $"{paths[0]} (+{paths.Count - 1} file)"
        };
        CadTargetPathText.ToolTip = paths.Count == 0 ? null : string.Join(Environment.NewLine, paths);
    }

    private void RefreshCadSymbolList(IReadOnlyList<CadSymbolScannedModel> scanned)
    {
        var selectedKey = (CadSymbolGrid.SelectedItem as CadSymbolListItem)?.Key;
        _cadSymbolModels.Clear();
        foreach (var model in scanned)
        {
            var item = new CadSymbolListItem { Key = model.Key, DisplayModel = model.DisplayModel, UsageCount = model.UsageCount };
            SetCadSymbolItemMapping(item, _cadSymbolRepository.GetMapping(model.Key));
            _cadSymbolModels.Add(item);
        }
        var selected = _cadSymbolModels.FirstOrDefault(item => item.Key.Equals(selectedKey, StringComparison.Ordinal)) ?? _cadSymbolModels.FirstOrDefault();
        CadSymbolGrid.SelectedItem = selected;
    }

    private void SetCadSymbolItemMapping(CadSymbolListItem item, CadSymbolMapping? mapping)
    {
        BitmapSource? preview = null;
        if (mapping is not null)
        {
            try { preview = DecodePreview(_cadSymbolRepository.ReadImage(mapping)); }
            catch (Exception exception) { SetCadSymbolStatus($"Không đọc được preview '{item.DisplayModel}': {exception.Message}"); }
        }
        item.SetMapping(mapping, preview);
        if (ReferenceEquals(CadSymbolGrid.SelectedItem, item)) ShowCadSymbolSelection(item);
    }

    private static BitmapSource DecodePreview(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private void CadSymbolGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowCadSymbolSelection(CadSymbolGrid.SelectedItem as CadSymbolListItem);

    private void ShowCadSymbolSelection(CadSymbolListItem? item)
    {
        CadSymbolSelectedModel.Text = item?.DisplayModel ?? "Chưa chọn model";
        CadSymbolPreview.Source = item?.Preview;
        CadSymbolDimensions.Text = item?.Dimensions ?? string.Empty;
        CadSymbolSource.Text = item is null || string.IsNullOrWhiteSpace(item.Source) ? string.Empty : $"Nguồn: {item.Source}";
    }

    private async void PasteCadSymbol_Click(object sender, RoutedEventArgs e)
    {
        if (CadSymbolGrid.SelectedItem is not CadSymbolListItem item) { SetCadSymbolStatus("Hãy chọn một model."); return; }
        try
        {
            _state.IsBusy = true;
            var image = await CadSymbolImageImport.FromClipboardAsync(_cadSymbolClipboard);
            await SaveCadSymbolImageAsync(item, image);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            SetCadSymbolStatus(exception.Message);
            MessageBox.Show(exception.Message, "Dán ảnh CAD Symbol", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _state.IsBusy = false;
        }
    }

    private async void ChooseCadSymbolImage_Click(object sender, RoutedEventArgs e)
    {
        if (CadSymbolGrid.SelectedItem is not CadSymbolListItem item) { SetCadSymbolStatus("Hãy chọn một model."); return; }
        var dialog = new OpenFileDialog
        {
            Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _state.IsBusy = true;
            var image = await Task.Run(() => CadSymbolImageImport.FromFile(dialog.FileName));
            await SaveCadSymbolImageAsync(item, image);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            SetCadSymbolStatus(exception.Message);
            MessageBox.Show(exception.Message, "Chọn ảnh CAD Symbol", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _state.IsBusy = false;
        }
    }

    private async Task SaveCadSymbolImageAsync(CadSymbolListItem item, CanonicalCadSymbolImage image)
    {
        var mapping = await Task.Run(() => _cadSymbolRepository.Upsert(item.DisplayModel, image.PngBytes, image.Source));
        SetCadSymbolItemMapping(item, mapping);
        SetCadSymbolStatus($"Đã lưu ảnh cho {item.DisplayModel} ({mapping.PixelWidth} × {mapping.PixelHeight} px).");
        if (AutoUpdateCadSymbolsCheck.IsChecked == true && _cadTargetWorkbooks.Count > 0)
            await RunCadSymbolUpdateAsync([item.Key]);
    }

    private async void RemoveCadSymbolImage_Click(object sender, RoutedEventArgs e)
    {
        if (CadSymbolGrid.SelectedItem is not CadSymbolListItem item) { SetCadSymbolStatus("Hãy chọn một model."); return; }
        try
        {
            await Task.Run(() => _cadSymbolRepository.RemoveMapping(item.Key));
            SetCadSymbolItemMapping(item, null);
            SetCadSymbolStatus($"Đã xóa mapping ảnh của {item.DisplayModel}.");
            if (AutoUpdateCadSymbolsCheck.IsChecked == true && _cadTargetWorkbooks.Count > 0)
                await RunCadSymbolUpdateAsync([item.Key]);
        }
        catch (Exception exception) { SetCadSymbolStatus($"Không xóa được ảnh: {exception.Message}"); }
    }

    private async void UpdateCadSymbolWorkbooks_Click(object sender, RoutedEventArgs e)
    {
        if (_cadTargetWorkbooks.Count == 0) { SetCadSymbolStatus("Chưa có file Excel hiện tại. Hãy xuất mới hoặc chọn file Excel có sẵn."); return; }
        await RunCadSymbolUpdateBusyAsync([]);
    }

    private async Task RunCadSymbolUpdateBusyAsync(IReadOnlyList<string> removedKeys)
    {
        await RunBusy(async token => await ApplyCadSymbolsToTargetWorkbooksAsync(removedKeys, token), DialuxLog);
    }

    private async Task RunCadSymbolUpdateAsync(IReadOnlyList<string> removedKeys)
    {
        if (_state.IsBusy || _isClosing) return;
        _state.IsBusy = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try { await ApplyCadSymbolsToTargetWorkbooksAsync(removedKeys, cancellation.Token); }
        catch (OperationCanceledException) { if (!_isClosing) SetCadSymbolStatus("Đã hủy cập nhật workbook."); }
        catch (Exception exception) { if (!_isClosing) { SetCadSymbolStatus($"Lỗi: {exception.Message}"); MessageBox.Show(exception.Message, "CAD Symbol"); } }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            if (!_isClosing) _state.IsBusy = false;
        }
    }

    private async Task ApplyCadSymbolsToTargetWorkbooksAsync(IReadOnlyList<string> removedKeys, CancellationToken token)
    {
        var mappings = await Task.Run(() => _cadSymbolRepository.GetMappings().Select(mapping =>
            new CadSymbolExcelMapping(mapping.Key, mapping.DisplayModel, _cadSymbolRepository.ReadImage(mapping), mapping.ImageHash)).ToArray(), token);
        var results = await Task.Run(() =>
        {
            var output = new List<CadSymbolExcelUpdateResult>();
            foreach (var path in _cadTargetWorkbooks)
            {
                token.ThrowIfCancellationRequested();
                output.Add(_cadSymbolExcelService.ApplyMappings(path, mappings, removedKeys));
            }
            return output.ToArray();
        }, token);
        var updatedRows = results.Sum(result => result.UpdatedRows);
        var conflicts = results.Sum(result => result.Conflicts);
        var missing = results.SelectMany(result => result.MissingMappings).Select(model => model.Key).Distinct(StringComparer.Ordinal).Count();
        SetCadSymbolStatus($"Đã cập nhật {updatedRows} dòng trong {results.Length} workbook; thiếu {missing} mapping; conflict {conflicts}.");
        foreach (var result in results)
            foreach (var diagnostic in result.Diagnostics) Log(DialuxLog, diagnostic);
    }

    private void SetCadSymbolStatus(string message) => CadSymbolStatus.Text = message;
    private void SelectTab(TabItem tab) => MainTabs.SelectedItem = tab;

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    internal static string? CodeForProductPdf(string? text, int caretIndex)
    {
        var caretCode = SignifyTextInput.CodeAtCaret(text, caretIndex);
        if (caretCode is not null) return caretCode;
        var codes = ParseCodes(text);
        return codes.Count == 1 ? codes[0] : null;
    }
    internal static bool HasDownloadRoot(string? path) => !string.IsNullOrWhiteSpace(path);
    private DownloadOptions Options() => new()
    {
        OutputRoot = Path.GetFullPath(DownloadRootBox.Text),
        DownloadIes = DownloadIesCheck.IsChecked == true,
        Force = ForceCheck.IsChecked == true
    };
    private IReadOnlyList<string> Codes() => ParseCodes(CodesBox?.Text);
    private IReadOnlyList<string> RawCodes() => ParseCodes(CodesBox?.Text, preserveDuplicates: true);
    internal static IReadOnlyList<string> ParseCodes(string? text, bool preserveDuplicates = false)
    {
        var codes = (text ?? string.Empty).Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return preserveDuplicates ? codes : codes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private sealed record FileQuery(string Code, string MatchCode, string? CanonicalProductName, string? FamilyDirectory, ProductRecord? Record, bool IsAmbiguous = false);
    private FileQuery ResolveFileQuery(string code, string root)
    {
        var product = CatalogueProductResolver.Resolve(_pricelist, code);
        if (product.Status == CatalogueProductResolutionStatus.Missing)
            return new FileQuery(code, code, null, null, null);
        if (product.Status == CatalogueProductResolutionStatus.Ambiguous)
            return new FileQuery(code, code, null, null, null, IsAmbiguous: true);

        var record = product.Record!;
        var matchCode = string.IsNullOrWhiteSpace(record.Material) ? record.MaterialDescription : record.Material;
        var canonicalName = DocManager.Core.ProductAssetName.CanonicalBaseName(record.MaterialDescription);
        var family = Path.Combine(Path.GetFullPath(root), DocManager.Core.WindowsFileName.Sanitize(record.ProductFamily));
        return new FileQuery(code, matchCode, canonicalName, family, record);
    }
    private static string[] LegacyFiles(FileQuery query, string root, CancellationToken token)
    {
        if (query.Record is null) return [];
        var scanRoot = query.FamilyDirectory is not null && Directory.Exists(query.FamilyDirectory) ? query.FamilyDirectory : root;
        return EnumerateCatalogueFiles(scanRoot, token)
            .Where(path => SignifyTextInput.FileNameMatchesCode(path, query.MatchCode, query.CanonicalProductName))
            .ToArray();
    }
    internal static string[] EnumerateCatalogueFiles(string root, CancellationToken token) =>
        FileSystemSafety.EnumerateFilesRecursivelyWithoutReparsePoints(root, "*.*", token)
            .Where(path => new[] { ".pdf", ".ies", ".ldt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();

    internal static string ProductFamilyDirectory(string foundPath, string root)
    {
        var rootFull = FileSystemSafety.NormalizeDirectoryPath(root);
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(foundPath))!);
        if (directory.Name.Equals("IES", StringComparison.OrdinalIgnoreCase) || directory.Name.Equals("LDT", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent ?? directory;
        var candidate = FileSystemSafety.NormalizeDirectoryPath(directory.FullName);
        return FileSystemSafety.IsSafePathWithinDirectory(candidate, rootFull) ? candidate : rootFull;
    }
    private string? BrowseFolder(string initialPath)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = ExistingDirectoryOrNull(initialPath) ?? ExistingDirectoryOrNull(_userSettings.CatalogueRoot) ?? _userSettingsStore.AdjacentProductFamilyRoot,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? Path.GetFullPath(dialog.FolderName) : null;
    }

    private void CataloguePathBox_LostFocus(object sender, RoutedEventArgs e) => PersistCatalogueRoot(CataloguePathBox.Text);
    private void DownloadRootBox_LostFocus(object sender, RoutedEventArgs e) => PersistCatalogueRoot(DownloadRootBox.Text);
    private void PricelistBox_LostFocus(object sender, RoutedEventArgs e) => PersistPricelist();
    private void ExistingFormBox_LostFocus(object sender, RoutedEventArgs e) => PersistExistingForm();

    private void PersistCatalogueRoot(string value)
    {
        if (UserSettingsStore.TryNormalizePath(value, out var normalized)) SetCatalogueRoot(normalized, true);
    }

    private void SetCatalogueRoot(string value, bool save)
    {
        if (!UserSettingsStore.TryNormalizePath(value, out var normalized)) return;
        CataloguePathBox.Text = normalized;
        DownloadRootBox.Text = normalized;
        _userSettings = _userSettings with { CatalogueRoot = normalized };
        if (save) SaveUserSettings();
    }

    private void PersistPricelist()
    {
        var path = ExistingFile(PricelistBox.Text, ".xlsx");
        PricelistBox.Text = path;
        _userSettings = _userSettings with { PricelistPath = path };
        SaveUserSettings();
    }

    private void PersistExistingForm()
    {
        var path = ExistingFile(ExistingFormBox.Text, ".xlsx");
        ExistingFormBox.Text = path;
        _userSettings = _userSettings with { ExistingFormExcelPath = path };
        SaveUserSettings();
    }

    private void SaveManualPathSettings()
    {
        if (UserSettingsStore.TryNormalizePath(CataloguePathBox.Text, out var catalogue))
            _userSettings = _userSettings with { CatalogueRoot = catalogue };
        else if (UserSettingsStore.TryNormalizePath(DownloadRootBox.Text, out var download))
            _userSettings = _userSettings with { CatalogueRoot = download };
        _userSettings = _userSettings with
        {
            PricelistPath = ExistingFile(PricelistBox.Text, ".xlsx"),
            ExistingFormExcelPath = ExistingFile(ExistingFormBox.Text, ".xlsx"),
            PendingCodes = CodesBox.Text,
            MergeReports = MergeCheck.IsChecked == true,
            AutoUpdateCadSymbols = AutoUpdateCadSymbolsCheck.IsChecked == true,
            DownloadIes = DownloadIesCheck.IsChecked == true
        };
        SaveUserSettings();
    }

    private void RememberEditorDirectory(string path, EditorPathKind kind)
    {
        var directory = DirectoryForFile(path);
        if (directory is null || !Directory.Exists(directory)) return;
        _userSettings = kind switch
        {
            EditorPathKind.Ldt => _userSettings with { LastLdtDirectory = directory },
            EditorPathKind.Ies => _userSettings with { LastIesDirectory = directory },
            EditorPathKind.PdfCatalogue => _userSettings with { LastPdfCatalogueDirectory = directory },
            _ => _userSettings
        };
        SaveUserSettings();
    }

    private void SaveUserSettings() => _userSettingsStore.Save(_userSettings);

    private void FloatingImage_ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            LoadFloatingImage(dialog.FileName);
        }
    }

    private void FloatingImage_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var imageFile = files.FirstOrDefault(f =>
                new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }
                    .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
            if (imageFile is not null)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void FloatingImage_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var imageFile = files.FirstOrDefault(f =>
                new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }
                    .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
            if (imageFile is not null)
            {
                LoadFloatingImage(imageFile);
                e.Handled = true;
            }
        }
    }

    private void LoadFloatingImage(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                MessageBox.Show($"Tệp không tồn tại: {path}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _floatingImagePath = fullPath;
            FloatingImagePathText.Text = fullPath;
            UpdateFloatingImagePreview();
            _userSettings = _userSettings with { LastFloatingImagePath = fullPath };
            SaveUserSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể tải ảnh: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateFloatingImagePreview()
    {
        if (string.IsNullOrEmpty(_floatingImagePath))
        {
            FloatingImagePreview.Source = null;
            return;
        }

        try
        {
            using (var stream = File.OpenRead(_floatingImagePath))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var frame = decoder.Frames[0];
                    int previewWidth = (int)FloatingImagePreview.Width;
                    if (previewWidth > 0 && previewWidth < frame.PixelWidth)
                    {
                        var scaledFrame = new TransformedBitmap(frame, new ScaleTransform(previewWidth / (double)frame.PixelWidth, previewWidth / (double)frame.PixelWidth));
                        scaledFrame.Freeze();
                        FloatingImagePreview.Source = scaledFrame;
                    }
                    else
                    {
                        frame.Freeze();
                        FloatingImagePreview.Source = frame;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể hiển thị xem trước: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            FloatingImagePreview.Source = null;
        }
    }

    private void FloatingImage_ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_floatingImagePath))
        {
            MessageBox.Show("Hãy chọn ảnh trước.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_floatingImageWindow is null)
        {
            _floatingImageWindow = new FloatingImageWindow();
            _floatingImageWindow.OpacityChanged += (s, e) =>
            {
                var newOpacity = _floatingImageWindow.CurrentOpacityPercent;
                FloatingOpacitySlider.Value = newOpacity;
                _userSettings = _userSettings with { FloatingImageOpacityPercent = newOpacity };
                SaveUserSettings();
            };
            _floatingImageWindow.BoundsChanged += (s, e) =>
            {
                if (_floatingImageWindow.GetCurrentBounds() is var bounds and not null)
                {
                    _userSettings = _userSettings with
                    {
                        FloatingImageLeft = bounds.Value.Item1,
                        FloatingImageTop = bounds.Value.Item2,
                        FloatingImageWidth = bounds.Value.Item3,
                        FloatingImageHeight = bounds.Value.Item4
                    };
                    SaveUserSettings();
                }
            };
            _floatingImageWindow.ClosedByUser += (s, e) =>
            {
                if (_floatingImageWindow.GetCurrentBounds() is var bounds and not null)
                {
                    _userSettings = _userSettings with
                    {
                        FloatingImageLeft = bounds.Value.Item1,
                        FloatingImageTop = bounds.Value.Item2,
                        FloatingImageWidth = bounds.Value.Item3,
                        FloatingImageHeight = bounds.Value.Item4
                    };
                    SaveUserSettings();
                }
                _floatingImageWindow = null;
            };
        }

        try
        {
            _floatingImageWindow.LoadImage(_floatingImagePath);
            _floatingImageWindow.SetOpacityPercent(FloatingOpacitySlider.Value);
            _floatingImageWindow.SetTopmost(FloatingImageTopmostCheck.IsChecked == true);
            _floatingImageWindow.SetClickThrough(FloatingImageClickThroughCheck.IsChecked == true);
            _floatingImageWindow.ApplySavedBounds(_userSettings.FloatingImageLeft, _userSettings.FloatingImageTop,
                                                  _userSettings.FloatingImageWidth, _userSettings.FloatingImageHeight);

            if (_floatingImageWindow.Owner == null)
                _floatingImageWindow.Owner = this;

            _floatingImageWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể hiển thị ảnh: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FloatingImage_HideOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_floatingImageWindow is not null && _floatingImageWindow.IsVisible)
        {
            _floatingImageWindow.Hide();
        }
    }

    private void FloatingOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)FloatingOpacitySlider.Value;
        FloatingOpacityPercent.Text = $"{value}%";
        FloatingImageOpacityLabel.Text = $"{value}%";

        if (_floatingImageWindow is not null && _floatingImageWindow.IsVisible)
        {
            _floatingImageWindow.SetOpacityPercent(value);
        }

        _userSettings = _userSettings with { FloatingImageOpacityPercent = value };
        SaveUserSettings();
    }

    private void FloatingImageTopmostCheck_Changed(object sender, RoutedEventArgs e)
    {
        var isChecked = FloatingImageTopmostCheck.IsChecked == true;
        if (_floatingImageWindow is not null)
        {
            _floatingImageWindow.SetTopmost(isChecked);
        }
        _userSettings = _userSettings with { FloatingImageTopmost = isChecked };
        SaveUserSettings();
    }

    private void FloatingImageClickThroughCheck_Changed(object sender, RoutedEventArgs e)
    {
        var isChecked = FloatingImageClickThroughCheck.IsChecked == true;
        if (_floatingImageWindow is not null)
        {
            _floatingImageWindow.SetClickThrough(isChecked);
        }
        _userSettings = _userSettings with { FloatingImageClickThrough = isChecked };
        SaveUserSettings();
    }

    private static string ExistingFile(string? path, string extension)
    {
        if (!UserSettingsStore.TryNormalizePath(path, out var normalized) ||
            !File.Exists(normalized) ||
            !Path.GetExtension(normalized).Equals(extension, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return normalized;
    }

    private static string? DirectoryForFile(string? path)
    {
        if (!UserSettingsStore.TryNormalizePath(path, out var normalized)) return null;
        return Path.GetDirectoryName(normalized);
    }

    private static string? ExistingDirectoryOrNull(string? path) =>
        UserSettingsStore.TryNormalizePath(path, out var normalized) && Directory.Exists(normalized) ? normalized : null;

    private enum EditorPathKind { Ldt, Ies, PdfCatalogue }

    private void Log(TextBox target, string value)
    {
        if (_isClosing || target.Dispatcher.HasShutdownStarted || target.Dispatcher.HasShutdownFinished) return;
        void Append() { if (_isClosing) return; target.AppendText($"[{DateTime.Now:HH:mm:ss}] {value}{Environment.NewLine}"); target.ScrollToEnd(); }
        if (target.Dispatcher.CheckAccess()) Append(); else target.Dispatcher.BeginInvoke((Action)Append);
    }
    private void LogDownloadAssets(DownloadLogEntry entry)
    {
        if (entry.AssetResults.Count == 0)
        {
            var error = string.IsNullOrWhiteSpace(entry.Error) ? string.Empty : $" ({entry.Error})";
            Log(DownloadLog, $"{entry.MaterialDescription} — {entry.Status}{error}");
            return;
        }

        foreach (var asset in entry.AssetResults)
        {
            var action = asset.Status switch
            {
                DownloadAssetStatus.Downloaded => "đã tải/tạo",
                DownloadAssetStatus.Reused => "đã dùng lại",
                DownloadAssetStatus.Unavailable => "bỏ qua vì không có nguồn",
                DownloadAssetStatus.Skipped => "bỏ qua",
                DownloadAssetStatus.Failed => "lỗi",
                _ => asset.Status.ToString()
            };
            var file = string.IsNullOrWhiteSpace(asset.FilePath) ? string.Empty : $": {asset.FilePath}";
            var detail = string.IsNullOrWhiteSpace(asset.Detail) ? string.Empty : $" ({asset.Detail})";
            Log(DownloadLog, $"{entry.MaterialDescription} — {asset.Kind}: {action}{file}{detail}");
        }
    }

    internal static (int TotalProducts, int Downloaded, int Reused, int Skipped, int PartialFailure, int CompleteFailure, int Unavailable) AnalyzeDownloadResults(IReadOnlyList<DownloadLogEntry> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var products = logs.GroupBy(item => $"{item.Sheet}{item.ExcelRow}{item.ProductFamily}{item.Material}{item.MaterialDescription}", StringComparer.Ordinal).ToArray();
        var assets = logs.SelectMany(item => item.AssetResults).ToArray();

        var downloaded = assets.Count(asset => asset.Status == DownloadAssetStatus.Downloaded) +
            logs.Count(item => item.AssetResults.Count == 0 && item.Status == "converted");
        var reused = assets.Count(asset => asset.Status == DownloadAssetStatus.Reused) +
            logs.Count(item => item.AssetResults.Count == 0 && item.Status == "conversion_exists");
        var skipped = assets.Count(asset => asset.Status == DownloadAssetStatus.Skipped);

        var completeFailure = products.Count(product => product.Any(item =>
            item.AssetResults.Any(asset => asset.Status == DownloadAssetStatus.Failed) ||
            (item.AssetResults.Count == 0 && item.Status is "download_failed" or "name_collision" or "conversion_failed")));
        var partialFailure = products.Count(product => product.Any(item =>
            item.AssetResults.Count == 0 && item.Status is "pdf_failed_ies_converted" or "ies_failed_pdf_saved" or "pdf_only" or "ies_converted_no_pdf"));
        var unavailable = products.Count(product => product.Any(item =>
            item.AssetResults.Any(asset => asset.Status == DownloadAssetStatus.Unavailable) ||
            (item.AssetResults.Count == 0 && item.Status == "assets_unavailable")));

        return (products.Length, downloaded, reused, skipped, partialFailure, completeFailure, unavailable);
    }

    internal static string DownloadCompletionSummary(IReadOnlyList<DownloadLogEntry> logs, bool downloadIes)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var products = logs.GroupBy(item => $"{item.Sheet}{item.ExcelRow}{item.ProductFamily}{item.Material}{item.MaterialDescription}", StringComparer.Ordinal).ToArray();
        var assets = logs.SelectMany(item => item.AssetResults).ToArray();
        var downloaded = assets.Count(asset => asset.Status == DownloadAssetStatus.Downloaded) +
            logs.Count(item => item.AssetResults.Count == 0 && item.Status == "converted");
        var reused = assets.Count(asset => asset.Status == DownloadAssetStatus.Reused) +
            logs.Count(item => item.AssetResults.Count == 0 && item.Status == "conversion_exists");
        var skipped = assets.Count(asset => asset.Status == DownloadAssetStatus.Skipped);
        var completeFailure = products.Count(product => product.Any(item =>
            item.AssetResults.Any(asset => asset.Status == DownloadAssetStatus.Failed) ||
            (item.AssetResults.Count == 0 && item.Status is "download_failed" or "name_collision" or "conversion_failed")));
        var partialFailure = products.Count(product => product.Any(item =>
            item.AssetResults.Count == 0 && item.Status is "pdf_failed_ies_converted" or "ies_failed_pdf_saved" or "pdf_only" or "ies_converted_no_pdf"));
        var unavailable = products.Count(product => product.Any(item =>
            item.AssetResults.Any(asset => asset.Status == DownloadAssetStatus.Unavailable) ||
            (item.AssetResults.Count == 0 && item.Status == "assets_unavailable")));
        return $"Hoàn tất {products.Length}: đã tải/tạo {downloaded}; đã dùng lại {reused}; bỏ qua {skipped}; không có nguồn {unavailable}; thiếu một phần {partialFailure}; lỗi {completeFailure}.";
    }

    private void ClearDialuxLog_Click(object sender, RoutedEventArgs e) => DialuxLog.Clear();
    private void ClearDownloadLog_Click(object sender, RoutedEventArgs e) => DownloadLog.Clear();
    private void OpenDialuxFolder_Click(object sender, RoutedEventArgs e) { var path = _excelOutput ?? _pdfs.FirstOrDefault()?.FullPath; if (path is not null) OpenFolder(Path.GetDirectoryName(path)!); }
    private static void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }

    private void UpdatePdfListPlaceholder()
    {
        PdfListPlaceholder.Visibility = _pdfs.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
    }

    private void UpdateLdtEditorPlaceholder()
    {
        LdtEditorPlaceholder.Visibility = _ldtDocument is null && _iesSnapshot is null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DownloadResultsOverlay.Visibility == System.Windows.Visibility.Visible)
        {
            CloseDownloadResultsDrawer();
            e.Handled = true;
        }
    }

    private void ShowDownloadResultsDrawer_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDownloadResults is not null)
            OpenDownloadResultsDrawer();
    }

    private void DownloadResultsBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == DownloadResultsOverlay)
            CloseDownloadResultsDrawer();
    }

    private void CloseDownloadResultsDrawer_Click(object sender, RoutedEventArgs e) => CloseDownloadResultsDrawer();

    private void CloseDownloadResultsDrawer()
    {
        StopDrawerAnimation();
        var storyboard = new Storyboard();

        var translateAnimation = new DoubleAnimation(0, 520, Duration.Automatic)
        {
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(translateAnimation, DrawerTransform);
        Storyboard.SetTargetProperty(translateAnimation, new PropertyPath(TranslateTransform.XProperty));
        storyboard.Children.Add(translateAnimation);

        var backdropAnimation = new ColorAnimation(System.Windows.Media.Color.FromArgb(170, 0, 0, 0), System.Windows.Media.Color.FromArgb(0, 0, 0, 0), Duration.Automatic)
        {
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var solidBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, 0, 0, 0));
        DownloadResultsOverlay.Background = solidBrush;
        Storyboard.SetTarget(backdropAnimation, solidBrush);
        Storyboard.SetTargetProperty(backdropAnimation, new PropertyPath(SolidColorBrush.ColorProperty));
        storyboard.Children.Add(backdropAnimation);

        storyboard.Completed += (_, _) =>
        {
            if (!_isClosing)
                DownloadResultsOverlay.Visibility = System.Windows.Visibility.Hidden;
        };

        _drawerStoryboard = storyboard;
        storyboard.Begin();
    }

    private void OpenDownloadResultsDrawer()
    {
        StopDrawerAnimation();
        DownloadResultsOverlay.Visibility = System.Windows.Visibility.Visible;
        var storyboard = new Storyboard();

        var translateAnimation = new DoubleAnimation(520, 0, Duration.Automatic)
        {
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(translateAnimation, DrawerTransform);
        Storyboard.SetTargetProperty(translateAnimation, new PropertyPath(TranslateTransform.XProperty));
        storyboard.Children.Add(translateAnimation);

        var backdropAnimation = new ColorAnimation(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), System.Windows.Media.Color.FromArgb(170, 0, 0, 0), Duration.Automatic)
        {
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var solidBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0));
        DownloadResultsOverlay.Background = solidBrush;
        Storyboard.SetTarget(backdropAnimation, solidBrush);
        Storyboard.SetTargetProperty(backdropAnimation, new PropertyPath(SolidColorBrush.ColorProperty));
        storyboard.Children.Add(backdropAnimation);

        _drawerStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopDrawerAnimation()
    {
        if (_drawerStoryboard is not null)
        {
            _drawerStoryboard.Stop();
            _drawerStoryboard = null;
        }
    }

    private void DownloadResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DownloadResultsGrid.SelectedItem is DownloadResultRow row)
        {
            OpenProductFolderForCode(row.Code);
        }
    }

    private void OpenProductFolderForCode(string code)
    {
        var root = DownloadRootBox.Text.Trim();
        var query = code is null ? null : ResolveFileQuery(code, root);
        if (query is null || !Directory.Exists(root)) return;
        if (query.IsAmbiguous)
        {
            Log(DownloadLog, $"{query.Code}: mã/tên khớp nhiều sản phẩm trong pricelist; không mở folder variant bất kỳ.");
            return;
        }
        var manifest = new CanonicalAssetManifest(root);
        var lookup = query.Record is null ? null : manifest.Resolve(query.Record);
        if (lookup?.HasCollision == true) return;
        var found = lookup?.Entry is null ? LegacyFiles(query, root, CancellationToken.None).FirstOrDefault() : manifest.ExistingPaths(lookup.Entry).FirstOrDefault();
        if (found is not null) OpenFolder(ProductFamilyDirectory(found, root));
    }

    private void PopulateDownloadResults(IReadOnlyList<DownloadLogEntry> logs)
    {
        _lastDownloadResults = logs;
        _downloadResultRows.Clear();
        var products = logs.GroupBy(item => $"{item.Sheet}{item.ExcelRow}{item.ProductFamily}{item.Material}{item.MaterialDescription}", StringComparer.Ordinal).ToArray();
        var (total, downloaded, reused, skipped, partialFailure, completeFailure, unavailable) = AnalyzeDownloadResults(logs);

        foreach (var group in products)
        {
            var entry = group.First();
            var code = entry.Material ?? entry.MaterialDescription;
            var assets = group.SelectMany(item => item.AssetResults).ToArray();

            var pdfStatus = GetAssetStatus(assets, DownloadAssetKind.Pdf, entry.Status);
            var iesStatus = GetAssetStatus(assets, DownloadAssetKind.Ies, entry.Status);
            var ldtStatus = GetAssetStatus(assets, DownloadAssetKind.Ldt, entry.Status);

            _downloadResultRows.Add(new DownloadResultRow
            {
                Code = code,
                PdfStatus = pdfStatus,
                IesStatus = iesStatus,
                LdtStatus = ldtStatus,
                StatusDescription = entry.Status
            });
        }

        DownloadResultsHeader.Text = $"Hoàn tất {total}: đã tải/tạo {downloaded}, dùng lại {reused}, bỏ qua {skipped}, không có nguồn {unavailable}, thiếu phần {partialFailure}, lỗi {completeFailure}";
    }

    private string GetAssetStatus(IReadOnlyList<DownloadAssetResult> assets, DownloadAssetKind kind, string productStatus)
    {
        var asset = assets.FirstOrDefault(a => a.Kind == kind);
        if (asset is null) return "-";
        return asset.Status switch
        {
            DownloadAssetStatus.Downloaded => "✓",
            DownloadAssetStatus.Reused => "↻",
            DownloadAssetStatus.Unavailable => "○",
            DownloadAssetStatus.Skipped => "⊘",
            DownloadAssetStatus.Failed => "✗",
            _ => "?"
        };
    }

    private sealed record DownloadResultRow
    {
        public required string Code { get; init; }
        public required string PdfStatus { get; init; }
        public required string IesStatus { get; init; }
        public required string LdtStatus { get; init; }
        public required string StatusDescription { get; init; }
    }
}
