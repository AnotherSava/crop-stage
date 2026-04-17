using System.Windows;
using System.Windows.Controls;

using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace CropStage.Features.SizingFrame;

public partial class SizingDialogWindow : Window
{
    private const double FilenameBoxExpandedWidth = 192;
    private const double FilenameBoxCompactWidth = 154;

    private bool _isCompact;
    private string _screenshotTip = "Screenshot";
    private const string OffScreenTip = "Frame is partly off-screen";

    public event EventHandler? CommitRequested;
    public event EventHandler? DimensionsChanged;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? BrowseRequested;
    public event EventHandler? CompactModeChanged;

    public SizingDialogWindow()
    {
        InitializeComponent();

        WidthBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        HeightBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        FolderBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        FilenameBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);

        // Live frame resize while typing — separate from CommitRequested so we
        // don't write state.json on every keystroke.
        WidthBox.TextChanged += (_, _) => DimensionsChanged?.Invoke(this, EventArgs.Empty);
        HeightBox.TextChanged += (_, _) => DimensionsChanged?.Invoke(this, EventArgs.Empty);

        ScreenshotButton.Click += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);
        BrowseButton.Click += (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty);
        ExpandedToggleButton.Click += (_, _) => ToggleCompactMode();
        CompactToggleButton.Click += (_, _) => ToggleCompactMode();

        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Frameless window: drag from any non-input element (labels, background, border).
        // Double-click on same areas toggles compact mode.
        if (e.OriginalSource is TextBlock or Border or Grid or Window)
        {
            if (e.ClickCount == 2)
            {
                ToggleCompactMode();
            }
            else
            {
                try { DragMove(); }
                catch (InvalidOperationException) { /* mouse released before DragMove ran */ }
            }
        }
    }

    private void ToggleCompactMode()
    {
        ApplyCompactVisuals(!_isCompact);
        CompactModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCompactMode(bool compact)
    {
        if (_isCompact == compact) return;
        ApplyCompactVisuals(compact);
    }

    private void ApplyCompactVisuals(bool compact)
    {
        _isCompact = compact;
        var expandedVis = compact ? Visibility.Collapsed : Visibility.Visible;
        var compactVis = compact ? Visibility.Visible : Visibility.Collapsed;
        SizeLabel.Visibility = expandedVis;
        SizeFields.Visibility = expandedVis;
        ExpandedToggleButton.Visibility = expandedVis;
        FolderLabel.Visibility = expandedVis;
        FolderBox.Visibility = expandedVis;
        BrowseButton.Visibility = expandedVis;
        CompactToggleButton.Visibility = compactVis;
        FilenameBox.Width = compact ? FilenameBoxCompactWidth : FilenameBoxExpandedWidth;
    }

    public void SetScreenshotShortcut(string shortcut)
    {
        _screenshotTip = string.IsNullOrWhiteSpace(shortcut) ? "Screenshot" : $"Screenshot ({shortcut})";
        if (ScreenshotButton.IsEnabled) ScreenshotButton.ToolTip = _screenshotTip;
    }

    public void SetScreenshotEnabled(bool enabled)
    {
        ScreenshotButton.IsEnabled = enabled;
        ScreenshotButton.ToolTip = enabled ? _screenshotTip : OffScreenTip;
    }

    public int WidthValue => int.TryParse(WidthBox.Text, out var v) ? v : 0;
    public int HeightValue => int.TryParse(HeightBox.Text, out var v) ? v : 0;
    public string FolderValue => FolderBox.Text;
    public string FilenameValue => FilenameBox.Text;

    public void SetFields(int width, int height, string folder, string filename)
    {
        WidthBox.Text = width.ToString();
        HeightBox.Text = height.ToString();
        FolderBox.Text = folder;
        FilenameBox.Text = filename;
    }
}
