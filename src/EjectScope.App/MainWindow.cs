using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;
using EjectScope.Core;

namespace EjectScope.App
{
    /// <summary>
    /// The whole product, in one window.
    ///
    /// The UI is built in code rather than XAML on purpose: the project is
    /// compiled by the C# compiler that ships with Windows, which cannot run the
    /// XAML build task. Keeping the markup in C# is what makes "clone and run
    /// build.ps1" work on a machine with no SDK installed.
    ///
    /// Reading order of the window, top to bottom, is the order of the question
    /// the user is actually asking: which drive - is it safe - who is holding it.
    ///
    /// v0.1 is read-only. There is no button in here that can change anything on
    /// the user's disk or end anybody's process.
    /// </summary>
    internal sealed class MainWindow : Window
    {
        private readonly ComboBox _driveBox = new ComboBox();
        private readonly Button _scanButton = new Button();
        private readonly Button _elevateButton = new Button();
        private readonly TextBlock _elevationNote = new TextBlock();

        private readonly Border _banner = new Border();
        private readonly TextBlock _headline = new TextBlock();
        private readonly TextBlock _subline = new TextBlock();
        private readonly TextBlock _hint = new TextBlock();

        private readonly TextBlock _status = new TextBlock();
        private readonly ListView _list = new ListView();
        private readonly TextBlock _detail = new TextBlock();
        private readonly TextBlock _footer = new TextBlock();

        private readonly List<VolumeInfo> _volumes = new List<VolumeInfo>();
        private bool _scanning;
        private Diagnosis _last;

        private readonly string _screenshotPath;

        internal MainWindow(string screenshotPath)
        {
            _screenshotPath = screenshotPath;
            Title = "EjectScope - 谁在用我的 U 盘";
            Width = 1200;
            Height = 820;
            MinWidth = 1000;
            MinHeight = 640;
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;

            BuildUi();
            LoadVolumes();

            Loaded += delegate { Scan(); };
        }

        // ---------------------------------------------------------------- layout

        private void BuildUi()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(22, 18, 22, 18);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(BuildHeader());

            _banner.Background = new SolidColorBrush(UiText.Grey);
            _banner.CornerRadius = new CornerRadius(8);
            _banner.Padding = new Thickness(20, 16, 20, 16);
            _banner.Margin = new Thickness(0, 16, 0, 14);

            StackPanel bannerText = new StackPanel();
            _headline.Foreground = Brushes.White;
            _headline.FontSize = 22;
            _headline.FontWeight = FontWeights.Bold;
            _headline.Text = "选择要诊断的盘";
            _subline.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            _subline.FontSize = 13;
            _subline.TextWrapping = TextWrapping.Wrap;
            _subline.Margin = new Thickness(0, 8, 0, 0);
            _subline.Text = "插着 U 盘、弹出失败的时候，这个窗口会告诉你到底是谁握着它。";
            _hint.Foreground = Brushes.White;
            _hint.FontSize = 13;
            _hint.FontWeight = FontWeights.SemiBold;
            _hint.TextWrapping = TextWrapping.Wrap;
            _hint.Margin = new Thickness(0, 10, 0, 0);
            bannerText.Children.Add(_headline);
            bannerText.Children.Add(_subline);
            bannerText.Children.Add(_hint);
            _banner.Child = bannerText;
            Grid.SetRow(_banner, 1);
            root.Children.Add(_banner);

            root.Children.Add(BuildBody());
            Grid.SetRow(root.Children[root.Children.Count - 1], 2);

            BuildFooter();
            Grid.SetRow(_footer, 3);
            root.Children.Add(_footer);

            Content = root;
        }

        private UIElement BuildHeader()
        {
            DockPanel bar = new DockPanel();

            StackPanel title = new StackPanel();
            TextBlock name = new TextBlock();
            name.Text = "EjectScope";
            name.FontSize = 21;
            name.FontWeight = FontWeights.Bold;
            name.Foreground = new SolidColorBrush(UiText.Ink);
            TextBlock tag = new TextBlock();
            tag.Text = "只看不动：不读文件内容、不结束进程、不联网。";
            tag.FontSize = 12;
            tag.Margin = new Thickness(0, 3, 0, 0);
            tag.Foreground = new SolidColorBrush(UiText.Muted);
            title.Children.Add(name);
            title.Children.Add(tag);
            DockPanel.SetDock(title, Dock.Left);
            bar.Children.Add(title);

            StackPanel controls = new StackPanel();
            controls.Orientation = Orientation.Horizontal;
            controls.HorizontalAlignment = HorizontalAlignment.Right;
            controls.VerticalAlignment = VerticalAlignment.Center;

            _driveBox.Width = 300;
            _driveBox.Height = 30;
            _driveBox.VerticalContentAlignment = VerticalAlignment.Center;
            _driveBox.SelectionChanged += delegate { UpdateElevationNote(); };
            _driveBox.DisplayMemberPath = "Text";
            controls.Children.Add(_driveBox);

            _scanButton.Content = "重新扫描";
            _scanButton.Height = 30;
            _scanButton.MinWidth = 92;
            _scanButton.Margin = new Thickness(8, 0, 0, 0);
            _scanButton.Click += delegate { Scan(); };
            controls.Children.Add(_scanButton);

            _elevateButton.Content = "以管理员身份重启";
            _elevateButton.Height = 30;
            _elevateButton.MinWidth = 150;
            _elevateButton.Margin = new Thickness(8, 0, 0, 0);
            _elevateButton.Click += delegate { RelaunchElevated(); };
            controls.Children.Add(_elevateButton);

            _elevationNote.FontSize = 11;
            _elevationNote.Margin = new Thickness(10, 0, 0, 0);
            _elevationNote.VerticalAlignment = VerticalAlignment.Center;
            _elevationNote.Foreground = new SolidColorBrush(UiText.Muted);
            controls.Children.Add(_elevationNote);

            DockPanel.SetDock(controls, Dock.Right);
            bar.Children.Add(controls);
            return bar;
        }

        private UIElement BuildBody()
        {
            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });

            _status.FontSize = 12;
            _status.Foreground = new SolidColorBrush(UiText.Muted);
            _status.Margin = new Thickness(2, 0, 0, 6);
            _status.Text = "准备就绪。";
            body.Children.Add(_status);

            GridView view = new GridView();
            view.Columns.Add(Column("进程", "Process", 210));
            view.Columns.Add(Column("公司", "Company", 150));
            view.Columns.Add(Column("句柄", "Handles", 56));
            view.Columns.Add(Column("权限", "Access", 60));
            view.Columns.Add(Column("示例路径", "Path", 420));

            _list.View = view;
            _list.SelectionMode = SelectionMode.Single;
            _list.BorderBrush = new SolidColorBrush(UiText.Rule);
            _list.FontSize = 12.5;
            _list.SelectionChanged += delegate { ShowDetail(); };

            ContextMenu menu = new ContextMenu();
            MenuItem copy = new MenuItem();
            copy.Header = "复制该路径";
            copy.Click += delegate { CopySelectedPath(); };
            menu.Items.Add(copy);
            _list.ContextMenu = menu;

            Grid.SetRow(_list, 1);
            body.Children.Add(_list);

            Border detailBox = new Border();
            detailBox.Background = Brushes.White;
            detailBox.BorderBrush = new SolidColorBrush(UiText.Rule);
            detailBox.BorderThickness = new Thickness(1);
            detailBox.Margin = new Thickness(0, 10, 0, 0);
            detailBox.Padding = new Thickness(12, 10, 12, 10);
            _detail.FontSize = 12;
            _detail.TextWrapping = TextWrapping.Wrap;
            _detail.Foreground = new SolidColorBrush(UiText.Ink);
            _detail.Text = "选中上面某一行，这里会列出它打开的具体文件路径。";
            ScrollViewer scroller = new ScrollViewer();
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.Content = _detail;
            detailBox.Child = scroller;
            Grid.SetRow(detailBox, 2);
            body.Children.Add(detailBox);

            return body;
        }

        private static GridViewColumn Column(string header, string path, double width)
        {
            GridViewColumn c = new GridViewColumn();
            c.Header = header;
            c.Width = width;
            c.DisplayMemberBinding = new Binding(path);
            return c;
        }

        private void BuildFooter()
        {
            _footer.FontSize = 11;
            _footer.TextWrapping = TextWrapping.Wrap;
            _footer.Margin = new Thickness(2, 12, 2, 0);
            _footer.Foreground = new SolidColorBrush(UiText.Muted);
            _footer.Text = "本工具不读取文件内容、不修改任何文件、不结束任何进程；它只查询系统里已经存在的句柄元数据。";
        }

        // ----------------------------------------------------------------- data

        private void LoadVolumes()
        {
            _volumes.Clear();
            List<DriveItem> items = new List<DriveItem>();

            List<string> letters = VolumeLocator.EnumerateDriveLetters();
            foreach (string letter in letters)
            {
                VolumeInfo v;
                try { v = VolumeLocator.Describe(letter); }
                catch (Exception) { continue; }
                if (v == null) continue;
                _volumes.Add(v);
                items.Add(new DriveItem(v));
            }

            // A USB stick is almost always the thing the user is trying to eject,
            // so it goes first in the list and is selected by default.
            items.Sort(delegate(DriveItem a, DriveItem b)
            {
                int ua = a.Volume.IsUsb ? 0 : 1;
                int ub = b.Volume.IsUsb ? 0 : 1;
                if (ua != ub) return ua.CompareTo(ub);
                return a.Volume.Letter.CompareTo(b.Volume.Letter);
            });

            _driveBox.ItemsSource = items;
            if (items.Count > 0) _driveBox.SelectedIndex = 0;

            if (_driveBox.SelectedItem == null)
            {
                _status.Text = "没有找到可诊断的盘。";
                _scanButton.IsEnabled = false;
            }
        }

        private VolumeInfo SelectedVolume()
        {
            DriveItem item = _driveBox.SelectedItem as DriveItem;
            return item == null ? null : item.Volume;
        }

        private void UpdateElevationNote()
        {
            bool elevated = IsElevated();
            _elevateButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
            _elevationNote.Text = elevated ? "已以管理员身份运行" : "部分系统进程需要管理员权限";
        }

        // ---------------------------------------------------------------- scan

        private void Scan()
        {
            if (_scanning) return;

            VolumeInfo volume = SelectedVolume();
            if (volume == null) return;

            _scanning = true;
            SetBusy(true);
            _status.Text = string.Format("正在扫描 {0} 的句柄，通常需要几秒钟…", volume.RootPath);

            string spec = volume.RootPath;
            Thread worker = new Thread(delegate() { ScanWorker(spec); });
            worker.IsBackground = true;
            worker.Name = "ejectscope-scan";
            worker.Start();
        }

        private void ScanWorker(string spec)
        {
            Diagnosis result = null;
            string error = null;
            try
            {
                result = Diagnoser.Run(spec);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            Dispatcher.BeginInvoke(new Action(delegate() { OnScanFinished(result, error); }));
        }

        private void OnScanFinished(Diagnosis diag, string error)
        {
            _scanning = false;
            SetBusy(false);

            if (error != null)
            {
                _status.Text = "扫描失败：" + error;
                _status.Foreground = new SolidColorBrush(UiText.Red);
                return;
            }

            _last = diag;
            Render(diag);

            // Used by the README screenshots and by anyone who needs a picture
            // of the real window without a screen-capture tool.
            if (!string.IsNullOrEmpty(_screenshotPath))
            {
                // Wait for a real render pass; RenderTargetBitmap otherwise grabs
                // the last composed frame, which is still the busy state.
                string path = _screenshotPath;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate() { CaptureTo(path); }));
            }
        }

        private void SetBusy(bool busy)
        {
            _driveBox.IsEnabled = !busy;
            _scanButton.IsEnabled = !busy;
            _scanButton.Content = busy ? "扫描中…" : "重新扫描";
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private void Render(Diagnosis d)
        {
            _banner.Background = new SolidColorBrush(UiText.ColorFor(d.Verdict));
            _headline.Text = UiText.Headline(d);
            _subline.Text = UiText.Subline(d);

            string hint = UiText.ActionHint(d);
            _hint.Text = hint;
            _hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;

            List<HolderRow> rows = new List<HolderRow>();
            foreach (Holder h in d.Holders) rows.Add(new HolderRow(h));
            _list.ItemsSource = rows;

            // Show the first holder's paths immediately: the point of the window
            // is to let a person judge one specific process, not to stare at a
            // list and guess.
            if (rows.Count > 0) _list.SelectedIndex = 0;

            _status.Foreground = new SolidColorBrush(UiText.Muted);
            _status.Text = string.Format(
                "{0}  ·  用时 {1} ms  ·  可见持有者 {2}  ·  无权查看的进程 {3}  ·  无法归类的句柄 {4}  ·  覆盖{5}",
                d.Volume.DisplayName,
                d.ScanDurationMs,
                d.Holders.Count,
                d.InaccessibleProcessCount,
                d.UnresolvedHandleCount,
                d.CoverageComplete ? "完整" : "不完整");

            if (d.Holders.Count == 0)
            {
                _detail.Text = "没有找到持有这个盘上任何文件的进程。";
            }

            _footer.Text = string.Format(
                "卷 {0}  ·  序列号 {1:X8}  ·  独占打开测试：{2}{3}",
                d.Volume.NtDeviceName,
                d.Volume.SerialNumber,
                d.ExclusiveOpen.ToString(),
                string.IsNullOrEmpty(d.ExclusiveOpenDetail) ? "" : "  ·  " + d.ExclusiveOpenDetail);
        }

        private void CaptureTo(string path)
        {
            // Developer flag only. Refuse anything that is not a PNG so a stale or
            // crafted command line cannot turn this into "overwrite an arbitrary
            // file with PNG bytes".
            if (path == null || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }

            try
            {
                UpdateLayout();
                int w = (int)Math.Round(ActualWidth);
                int h = (int)Math.Round(ActualHeight);
                if (w <= 0 || h <= 0) return;
                RenderTargetBitmap target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                target.Render(this);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using (System.IO.FileStream stream = System.IO.File.Create(path))
                {
                    encoder.Save(stream);
                }
            }
            catch (Exception)
            {
                // A screenshot we could not take must never break the diagnosis.
            }
            Close();
        }

        private void ShowDetail()
        {
            HolderRow row = _list.SelectedItem as HolderRow;
            if (row == null)
            {
                _detail.Text = "选中上面某一行，这里会列出它打开的具体文件路径。";
                return;
            }

            Holder h = row.Holder;
            List<string> lines = new List<string>();
            lines.Add(string.Format("进程：{0}", string.IsNullOrEmpty(h.ProcessPath) ? "(路径未知)" : h.ProcessPath));
            if (!string.IsNullOrEmpty(h.CompanyName)) lines.Add("公司：" + h.CompanyName);
            lines.Add(string.Format("持有 {0} 个句柄，权限：{1}", h.HandleCount, UiText.AccessText(h.Access)));
            lines.Add("");
            lines.Add("打开的文件：");
            if (h.Samples.Count == 0)
            {
                lines.Add("  (这批句柄没有带回可显示的路径样本)");
            }
            else
            {
                foreach (HandleRef r in h.Samples)
                {
                    string p = !string.IsNullOrEmpty(r.DosPath) ? r.DosPath : r.NtPath;
                    if (string.IsNullOrEmpty(p)) p = "(路径未知)";
                    lines.Add("  " + p + (r.IsVolumeRoot ? "   [整个卷]" : ""));
                }
            }

            _detail.Text = string.Join(Environment.NewLine, lines.ToArray());
        }

        private void CopySelectedPath()
        {
            HolderRow row = _list.SelectedItem as HolderRow;
            if (row == null) return;
            if (string.IsNullOrEmpty(row.Path)) return;
            try { Clipboard.SetText(row.Path); }
            catch (Exception) { }
        }

        // ----------------------------------------------------------- elevation

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void RelaunchElevated()
        {
            try
            {
                string exe = Assembly.GetEntryAssembly().Location;
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = exe;
                info.UseShellExecute = true;
                info.Verb = "runas";
                Process.Start(info);
                Close();
            }
            catch (Exception)
            {
                _status.Text = "提权被取消，仍以当前权限继续（部分系统进程会看不到）。";
            }
        }

        // ------------------------------------------------------------- view rows

        /// <summary>
        /// One row in the drive picker. These are properties, not fields, because
        /// WPF bindings only see properties - a field-bound combo box renders as an
        /// empty rectangle and gives no hint why.
        /// </summary>
        internal sealed class DriveItem
        {
            public VolumeInfo Volume { get; private set; }
            public string Text { get; private set; }

            internal DriveItem(VolumeInfo v)
            {
                Volume = v;
                string kind = v.IsUsb ? "USB" : KindName(v.DriveType);
                string label = string.IsNullOrEmpty(v.Label) ? "" : " · " + v.Label;
                Text = string.Format("{0} · {1} · {2}{3}", v.Letter, kind, v.SizeText(), label);
            }

            private static string KindName(uint t)
            {
                if (t == 2) return "可移动";
                if (t == 3) return "本地磁盘";
                if (t == 5) return "光驱";
                if (t == 4) return "网络";
                return "卷";
            }
        }

        internal sealed class HolderRow
        {
            public string Process { get; private set; }
            public string Company { get; private set; }
            public string Handles { get; private set; }
            public string Access { get; private set; }
            public string Path { get; private set; }

            internal Holder Holder { get; private set; }

            internal HolderRow(Holder h)
            {
                Holder = h;
                Process = UiText.HolderName(h);
                Company = UiText.Company(h);
                Handles = h.HandleCount.ToString();
                Access = UiText.AccessText(h.Access);
                Path = UiText.SamplePath(h);
            }
        }
    }
}
