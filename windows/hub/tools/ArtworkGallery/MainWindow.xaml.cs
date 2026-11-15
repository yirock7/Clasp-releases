using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Views;
using AppleDeviceHub.Core.Ble;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ArtworkGallery;

/// <summary>
/// Review harness for the real renderer: the legacy group grid, an eight-model page, one-model view,
/// the cycle, and the real controls. With --capture, it saves the visible root to a PNG and exits.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly ArtworkGroup[] Groups =
    [
        ArtworkGroup.StemEarbuds, ArtworkGroup.ProEarbuds, ArtworkGroup.BudsWithCase, ArtworkGroup.EarHookWithCase,
        ArtworkGroup.MaxHeadphones,
        ArtworkGroup.OverEarHeadphones, ArtworkGroup.OnEarHeadphones, ArtworkGroup.EarHookCorded,
        ArtworkGroup.Neckband, ArtworkGroup.Generic,
    ];

    private const int Columns = 5;
    private const double CellWidth = 180;
    private const double CellHeight = 164;
    private const double WindowPadding = 10;

    private const double ModelViewHeight = 300;

    private sealed record ModelSpec(ushort Id, string Name);

    // These are the eight devices selected for the current artwork review.
    private static readonly ModelSpec[] Models =
    [
        new(0x0320, "Powerbeats 3"),
        new(0x0D20, "Powerbeats 4"),
        new(0x0520, "BeatsX"),
        new(0x1020, "Beats Flex"),
        new(0x0B20, "Powerbeats Pro"),
        new(0x1220, "Beats Fit Pro"),
        new(0x2620, "Beats Solo Buds"),
        new(0x0920, "Beats Studio 3"),
    ];

    private const int ModelColumns = 4;
    private const double ModelCellWidth = 200;
    private const double ModelCellHeight = 178;
    private const double ModelImageHeight = 142;

    private const double ControlsWidth = 760;
    private const double ControlsHeight = 560;

    private static string? s_resultPath;

    private readonly bool _open;
    private readonly string _state;
    private readonly string? _capturePath;
    private readonly List<ModelArtwork> _artworks = [];
    private DispatcherQueueTimer? _timer;
    private int _step;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        Title = "ArtworkGallery";

        // Both "--name=value" and "--name value" work.
        string? Value(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(name.Length + 1)..];
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }
        bool cycle = args.Contains("--cycle", StringComparer.OrdinalIgnoreCase);
        bool controls = string.Equals(Value("--page"), "controls", StringComparison.OrdinalIgnoreCase);
        bool modelsPage = string.Equals(Value("--page"), "models", StringComparison.OrdinalIgnoreCase);
        // Same rules the product applies from Settings; they reach the controls through EffectsPolicy.Changed.
        EffectsPolicy.SetOverrides(
            args.Contains("--reduce-motion", StringComparer.OrdinalIgnoreCase),
            args.Contains("--reduce-transparency", StringComparer.OrdinalIgnoreCase));
        s_resultPath = Value("--result");
        _state = (Value("--state") ?? "open").ToLowerInvariant();
        _open = _state != "closed";
        _capturePath = Value("--capture");
        if (_capturePath is not null
            && (!Path.IsPathFullyQualified(_capturePath)
                || !string.Equals(Path.GetExtension(_capturePath), ".png", StringComparison.OrdinalIgnoreCase)))
        {
            Fail(new ArgumentException("--capture must be an absolute PNG path."));
        }

        // --model=0x2720 (hex or decimal): one large per-model drawing, in the state --state asks for.
        string? model = Value("--model");
        bool modelPage = model is not null;
        Root.RequestedTheme = string.Equals(Value("--theme"), "dark", StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Dark : ElementTheme.Light;

        if (controls) BuildControls();
        else if (modelPage) BuildModel(ParseModelId(model!), Value("--colorway"));
        else if (modelsPage) BuildModels();
        else if (cycle) BuildCycle();
        else BuildGrid();

        // Fixed size, scaled for the monitor DPI once the content knows it.
        Root.Loaded += (_, _) =>
        {
            double rows = modelsPage
                ? Math.Ceiling(Models.Length / (double)ModelColumns)
                : cycle ? 1 : Math.Ceiling(Groups.Length / (double)Columns);
            double columns = modelsPage ? ModelColumns : cycle ? 1 : Columns;
            double cellWidth = modelsPage ? ModelCellWidth : CellWidth;
            double cellHeight = modelsPage ? ModelCellHeight : CellHeight;
            double columnSpacing = modelsPage ? 4 : 0;
            double rowSpacing = modelsPage ? 2 : 0;
            double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            double width = columns * cellWidth + Math.Max(0, columns - 1) * columnSpacing + 2 * WindowPadding;
            double height = rows * cellHeight + Math.Max(0, rows - 1) * rowSpacing
                + 2 * WindowPadding + 32 + (modelsPage && _state == "cues" ? 26 : 0);
            if (controls)
            {
                width = ControlsWidth;
                height = ControlsHeight;
            }
            else if (modelPage)
            {
                width = ModelViewHeight * 4 / 3 + 2 * WindowPadding;
                height = ModelViewHeight + 2 * WindowPadding + 32;
            }
            AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }
            // A case drawing shows itself open when it loads, so the requested state goes on afterwards.
            DispatcherQueue.TryEnqueue(ApplyState);
            if (_capturePath is { } capturePath)
            {
                DispatcherQueue.TryEnqueue(() => _ = CaptureAndFinishAsync(capturePath));
            }
            else if (cycle && !controls) StartCycle();
        };
    }

    /// <summary>
    /// The controls page: the real GlassToggleStyle / GlassRowToggleStyle / GlassSliderStyle (linked GlassStyles.xaml)
    /// on the real readable glass surface, every state labelled. Light or dark comes from --theme.
    /// </summary>
    private void BuildControls()
    {
        static Style Glass(string key) => (Style)Application.Current.Resources[key];
        // Code cannot ask for a {ThemeResource}, and the app theme may differ from this window's theme, so the brush
        // is taken from the matching theme dictionary of the linked GlassTokens.xaml.
        string themeName = Root.RequestedTheme == ElementTheme.Dark ? "Dark" : "Light";
        Brush BrushOf(string key)
        {
            foreach (var merged in Application.Current.Resources.MergedDictionaries)
            {
                if (merged.ThemeDictionaries.TryGetValue(themeName, out var theme)
                    && theme is ResourceDictionary dictionary
                    && dictionary.TryGetValue(key, out var value))
                    return (Brush)value;
            }
            throw new InvalidOperationException("No theme brush " + key);
        }

        TextBlock Caption(string text) => new()
        {
            Text = text,
            Style = Glass("GlassCaptionTextStyle"),
            Margin = new Thickness(0, 10, 0, 2),
        };

        ToggleSwitch Toggle(string? header, bool on, bool enabled = true, string style = "GlassToggleStyle") => new()
        {
            Header = header,
            IsOn = on,
            IsEnabled = enabled,
            Style = Glass(style),
        };

        Slider Slide(double value, bool enabled = true, string? header = null) => new()
        {
            Header = header,
            Minimum = 0,
            Maximum = 100,
            Value = value,
            IsEnabled = enabled,
            Style = Glass("GlassSliderStyle"),
        };

        // A row the way Settings and the flyout draw it: the label belongs to the row, the toggle has no Header.
        Grid Row(string label, bool on, bool enabled = true)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = label,
                Style = Glass("GlassLabelTextStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf(enabled ? "GlassTextPrimaryBrush" : "GlassTextDisabledBrush"),
            });
            var toggle = Toggle(null, on, enabled, "GlassRowToggleStyle");
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            return row;
        }

        // The flyout's volume slider: quiet and loud speaker glyphs at the ends, dimmed with the slider.
        Grid VolumeRow(bool enabled)
        {
            FontIcon Icon(string glyph) => new()
            {
                Glyph = glyph,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("GlassTextSecondaryBrush"),
                Opacity = GlassBind.DimIfDisabled(enabled),
                IsHitTestVisible = false,
            };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Icon(""));
            var slider = Slide(55, enabled);
            slider.IsThumbToolTipEnabled = false;
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            var loud = Icon("");
            Grid.SetColumn(loud, 2);
            row.Children.Add(loud);
            return row;
        }

        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(Caption("Toggle: off, on"));
        left.Children.Add(Toggle("Off", false));
        left.Children.Add(Toggle("On", true));
        left.Children.Add(Caption("Toggle: disabled off, disabled on"));
        left.Children.Add(Toggle("Disabled off", false, enabled: false));
        left.Children.Add(Toggle("Disabled on", true, enabled: false));
        left.Children.Add(Caption("Toggle with a long header that wraps"));
        left.Children.Add(Toggle(
            "Allow clasp:// links so other programs on this PC can ask Clasp to connect, switch sound or open settings",
            true));
        left.Children.Add(Caption("Row toggle, no header (off, on, disabled on)"));
        left.Children.Add(Row("Row toggle off", false));
        left.Children.Add(Row("Row toggle on", true));
        left.Children.Add(Row("Row toggle disabled", true, enabled: false));

        var right = new StackPanel { Spacing = 4 };
        right.Children.Add(Caption("Slider: 0, 40, 100"));
        right.Children.Add(Slide(0));
        right.Children.Add(Slide(40));
        right.Children.Add(Slide(100));
        right.Children.Add(Caption("Slider: disabled"));
        right.Children.Add(Slide(60, enabled: false));
        right.Children.Add(Caption("Slider with a header"));
        right.Children.Add(Slide(40, header: "Sample slider"));
        right.Children.Add(Caption("Volume slider with end icons: enabled, disabled"));
        right.Children.Add(VolumeRow(enabled: true));
        right.Children.Add(VolumeRow(enabled: false));

        var columns = new Grid { ColumnSpacing = 28 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(left);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);

        Root.Children.Add(new GlassSurface { Style = Glass("GlassReadableSurfaceStyle"), Content = columns });
    }

    private void BuildModels()
    {
        var page = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (_state == "cues")
        {
            page.Children.Add(new TextBlock
            {
                Text = "Connected · Charging · Worn (simulated review state)",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnSpacing = 4,
            RowSpacing = 2,
        };
        for (int column = 0; column < ModelColumns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ModelCellWidth) });
        }
        for (int row = 0; row < Math.Ceiling(Models.Length / (double)ModelColumns); row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ModelCellHeight) });
        }

        for (int i = 0; i < Models.Length; i++)
        {
            var spec = Models[i];
            var art = new ModelArtwork { ModelId = spec.Id };
            _artworks.Add(art);

            var cell = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cell.Children.Add(new Viewbox
            {
                Height = ModelImageHeight,
                Width = ModelCellWidth - 12,
                Stretch = Stretch.Uniform,
                Child = art,
            });
            cell.Children.Add(new TextBlock
            {
                Text = $"{spec.Name} · 0x{spec.Id:X4}",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
            });

            Grid.SetColumn(cell, i % ModelColumns);
            Grid.SetRow(cell, i / ModelColumns);
            grid.Children.Add(cell);
        }

        page.Children.Add(grid);
        Root.Children.Add(page);
    }

    private static ushort ParseModelId(string text)
    {
        var t = text.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.Parse(t[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
            : ushort.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
    }

    private void BuildModel(ushort modelId, string? colorway)
    {
        var art = new ModelArtwork();
        art.SetModel(modelId, colorway);
        _artworks.Add(art);
        Root.Children.Add(new Viewbox
        {
            Height = ModelViewHeight,
            Stretch = Stretch.Uniform,
            Child = art,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void BuildGrid()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int c = 0; c < Columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellWidth) });
        for (int r = 0; r < Math.Ceiling(Groups.Length / (double)Columns); r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellHeight) });
        for (int i = 0; i < Groups.Length; i++)
        {
            var art = new ModelArtwork { Group = Groups[i] };
            _artworks.Add(art);
            var cell = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(new Viewbox { Height = 120, Stretch = Stretch.Uniform, Child = art });
            cell.Children.Add(new TextBlock { Text = Groups[i].ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 });
            Grid.SetColumn(cell, i % Columns);
            Grid.SetRow(cell, i / Columns);
            grid.Children.Add(cell);
        }
        Root.Children.Add(grid);
    }

    private void BuildCycle()
    {
        var art = new ModelArtwork();
        _artworks.Add(art);
        Root.Children.Add(new Viewbox
        {
            Height = 120,
            Stretch = Stretch.Uniform,
            Child = art,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void ApplyState()
    {
        foreach (var art in _artworks)
        {
            switch (_state)
            {
                case "closed": art.ShowClosed(); break;
                case "play": art.PlayOpen(); break;
                case "loop": art.StartCaseLoop(4, 0.6, 2.6); break;
                case "cues":
                    art.ShowOpen();
                    art.SetDemoStatusCues(connected: true, charging: true, worn: true);
                    break;
                default:
                    if (_open) art.ShowOpen();
                    else art.ShowClosed();
                    break;
            }
        }
    }

    private void StartCycle()
    {
        // Every group twice, 200 ms apart, rotating PlayOpen, ShowClosed, ShowOpen.
        int total = Groups.Length * 2;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) =>
        {
            try
            {
                if (_step >= total)
                {
                    _timer!.Stop();
                    Finish("cycle ok", exitCode: 0);
                    return;
                }
                var art = _artworks[0];
                art.Group = Groups[_step % Groups.Length];
                switch (_step % 3)
                {
                    case 0: art.PlayOpen(); break;
                    case 1: art.ShowClosed(); break;
                    default: art.ShowOpen(); break;
                }
                _step++;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        };
        _timer.Start();
    }

    private async Task CaptureAndFinishAsync(string path)
    {
        try
        {
            // Let the requested state and window resize reach the compositor before taking the snapshot.
            // The cue animations (320 ms) have settled by then.
            await Task.Delay(_state == "cues" ? 800 : 180);

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Root);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                throw new InvalidOperationException("The gallery root had no pixels to capture.");
            }

            string folderPath = Path.GetDirectoryName(path)
                ?? throw new ArgumentException("The PNG path needs a parent folder.", nameof(path));
            Directory.CreateDirectory(folderPath);
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var pixels = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[checked((int)pixels.Length)];
            reader.ReadBytes(bytes);

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                bytes);
            await encoder.FlushAsync();
            Finish($"capture ok: {path}", exitCode: 0);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    internal static void Fail(Exception ex) => Finish(ex.Message, exitCode: 1);

    private static void Finish(string message, int exitCode)
    {
        try
        {
            if (!string.IsNullOrEmpty(s_resultPath)) File.WriteAllText(s_resultPath, message);
        }
        catch
        {
            // Nothing more to report to; the exit code still says what happened.
        }
        Environment.Exit(exitCode);
    }
}
