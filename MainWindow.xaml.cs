using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PrintPusher
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Maps a preset name (e.g. "2x3") to label width and height in inches.
        /// </summary>
        private sealed class LabelPreset
        {
            public LabelPreset(string displayName, double widthInches, double heightInches)
            {
                DisplayName = displayName;
                WidthInches = widthInches;
                HeightInches = heightInches;
            }

            public string DisplayName { get; }
            public double WidthInches { get; }
            public double HeightInches { get; }
        }

        /// <summary>
        /// All label size presets. Order matters: first item is the default (2x3).
        /// </summary>
        private static readonly IReadOnlyList<LabelPreset> LabelPresets = new List<LabelPreset>
        {
            new LabelPreset("2x3", 2, 3),
            new LabelPreset("3x2", 3, 2),
            new LabelPreset("4x6", 4, 6),
            new LabelPreset("6x4", 6, 4),
            new LabelPreset("4x4", 4, 4),
            new LabelPreset("2x1", 2, 1),
        };

        public MainWindow()
        {
            InitializeComponent();
            LabelPresetComboBox.ItemsSource = LabelPresets;
            LabelPresetComboBox.DisplayMemberPath = nameof(LabelPreset.DisplayName);
            LabelPresetComboBox.SelectedIndex = 0;
        }

        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            var host = PrinterIpTextBox?.Text?.Trim();
            var portText = PortTextBox?.Text?.Trim();

            if (string.IsNullOrEmpty(host))
            {
                StatusLabel.Content = "Invalid printer IP";
                Console.WriteLine("Invalid printer IP");
                return;
            }

            if (!int.TryParse(portText, out var port) || port < 1 || port > 65535)
            {
                StatusLabel.Content = "Invalid port";
                Console.WriteLine("Invalid port: " + portText);
                return;
            }

            TestConnectionButton.IsEnabled = false;
            StatusLabel.Content = "Testing connection...";
            Console.WriteLine($"Testing connection to {host}:{port}...");

            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));

            try
            {
                var finished = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (finished == connectTask)
                {
                    await connectTask.ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusLabel.Content = $"Connection successful to {host}:{port}";
                        Console.WriteLine($"Connection successful to {host}:{port}");
                    });
                }
                else
                {
                    try { client.Close(); } catch { /* ignore */ }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusLabel.Content = "Connection timed out";
                        Console.WriteLine("Connection timed out");
                    });
                }
            }
            catch (SocketException ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusLabel.Content = $"Connection failed: {ex.Message}";
                    Console.WriteLine("Connection failed: " + ex);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusLabel.Content = $"Connection failed: {ex.Message}";
                    Console.WriteLine("Connection failed: " + ex);
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => TestConnectionButton.IsEnabled = true);
            }
        }

        private async void GeneratePrintButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Always read current UI state and rebuild ZPL from scratch
                var barcodeInput = BarcodeTextBox?.Text?.Trim() ?? string.Empty;
                var autoInc = AutoIncrementCheckBox?.IsChecked == true;
                var rotation = GetSelectedRotationDegrees();

                if (LabelPresetComboBox?.SelectedItem is not LabelPreset preset)
                {
                    StatusLabel.Content = "Select a label size";
                    Console.WriteLine("Select a label size");
                    return;
                }

                const double dpi = 203.0;
                var widthDots = (int)(preset.WidthInches * dpi);
                var heightDots = (int)(preset.HeightInches * dpi);

                var zplBuilder = new StringBuilder();

                if (!autoInc)
                {
                    if (string.IsNullOrEmpty(barcodeInput))
                    {
                        StatusLabel.Content = "Barcode value required";
                        Console.WriteLine("Barcode value required");
                        return;
                    }

                    zplBuilder.Append(GenerateBuilderZpl(barcodeInput, widthDots, heightDots, rotation));
                }
                else
                {
                    if (!int.TryParse(StartValueTextBox?.Text?.Trim(), out var start))
                    {
                        StatusLabel.Content = "Invalid start value";
                        Console.WriteLine("Invalid start value: " + StartValueTextBox?.Text);
                        return;
                    }

                    if (!int.TryParse(CountTextBox?.Text?.Trim(), out var count) || count < 1)
                    {
                        StatusLabel.Content = "Invalid count";
                        Console.WriteLine("Invalid count: " + CountTextBox?.Text);
                        return;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var value = (start + i).ToString();
                        zplBuilder.Append(GenerateBuilderZpl(value, widthDots, heightDots, rotation));
                    }
                }

                var finalZpl = zplBuilder.ToString();

                RawZplTextBox.Text = finalZpl;
                try { MainTabControl.SelectedIndex = 1; } catch { /* ignore */ }

                GeneratePrintButton.IsEnabled = false;
                StatusLabel.Content = "Sending generated ZPL...";
                Console.WriteLine("Sending generated ZPL...");

                var result = await SendZplToPrinterAsync(finalZpl).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    StatusLabel.Content = result.Message;
                    Console.WriteLine(result.Message);
                    GeneratePrintButton.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                StatusLabel.Content = "Error generating ZPL";
                Console.WriteLine("Error generating ZPL: " + ex);
            }
        }

        private void SendRawZplButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SendRawZplAsync();
        }

        private async Task SendRawZplAsync()
        {
            var zpl = RawZplTextBox?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(zpl))
            {
                StatusLabel.Content = "ZPL is empty";
                Console.WriteLine("ZPL is empty");
                return;
            }

            SendRawZplButton.IsEnabled = false;
            StatusLabel.Content = "Sending raw ZPL...";
            Console.WriteLine("Sending raw ZPL...");

            var result = await SendZplToPrinterAsync(zpl).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                StatusLabel.Content = result.Message;
                Console.WriteLine(result.Message);
                SendRawZplButton.IsEnabled = true;
            });
        }

        /// <summary>
        /// Reads the currently selected rotation from the UI (source of truth). Returns 0, 90, 180, or 270.
        /// </summary>
        private int GetSelectedRotationDegrees()
        {
            if (Rotation0RadioButton?.IsChecked == true) return 0;
            if (Rotation90RadioButton?.IsChecked == true) return 90;
            if (Rotation180RadioButton?.IsChecked == true) return 180;
            if (Rotation270RadioButton?.IsChecked == true) return 270;
            return 0;
        }

        private void RotationRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            TryRefreshBuilderZpl();
        }

        private void LabelPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TryRefreshBuilderZpl();
        }

        /// <summary>
        /// If current Builder inputs are valid, regenerates ZPL from UI state and updates RawZplTextBox.
        /// Fails quietly (no popups, no status noise) when inputs are incomplete.
        /// </summary>
        private void TryRefreshBuilderZpl()
        {
            if (LabelPresetComboBox?.SelectedItem is not LabelPreset preset)
                return;

            var rotation = GetSelectedRotationDegrees();
            var autoInc = AutoIncrementCheckBox?.IsChecked == true;
            const double dpi = 203.0;
            var widthDots = (int)(preset.WidthInches * dpi);
            var heightDots = (int)(preset.HeightInches * dpi);
            var zplBuilder = new StringBuilder();

            if (!autoInc)
            {
                var barcodeInput = BarcodeTextBox?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(barcodeInput))
                    return;
                zplBuilder.Append(GenerateBuilderZpl(barcodeInput, widthDots, heightDots, rotation));
            }
            else
            {
                if (!int.TryParse(StartValueTextBox?.Text?.Trim(), out var start) ||
                    !int.TryParse(CountTextBox?.Text?.Trim(), out var count) || count < 1)
                    return;
                for (var i = 0; i < count; i++)
                {
                    var value = (start + i).ToString();
                    zplBuilder.Append(GenerateBuilderZpl(value, widthDots, heightDots, rotation));
                }
            }

            RawZplTextBox.Text = zplBuilder.ToString();
        }

        private static string EscapeZpl(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// Maps rotation degrees (0, 90, 180, 270) to ZPL orientation code used by ^BC and ^A0.
        /// 0° -> N, 90° -> R, 180° -> I, 270° -> B.
        /// </summary>
        private static char GetZplOrientationForRotation(int rotationDegrees)
        {
            return rotationDegrees switch
            {
                0 => 'N',
                90 => 'R',
                180 => 'I',
                270 => 'B',
                _ => 'N'
            };
        }

        /// <summary>
        /// Proportional layout regions for one label (all values in dots).
        /// Same layout model for portrait (tall) and landscape (wide); only proportions vary slightly.
        /// </summary>
        private sealed class LabelLayout
        {
            public int WidthDots { get; init; }
            public int HeightDots { get; init; }
            public bool IsPortrait { get; init; }
            public int OuterPadding { get; init; }
            public int UsableWidth { get; init; }
            public int UsableHeight { get; init; }
            public int BarcodeRegionWidth { get; init; }
            public int BarcodeRegionHeight { get; init; }
            public int BarcodeX { get; init; }
            public int BarcodeY { get; init; }
            public int GapBetweenBarcodeAndText { get; init; }
            public int TextY { get; init; }
            public int TextHeightDots { get; init; }
            public int TextWidthDots { get; init; }
            public int ModuleWidth { get; init; }
        }

        /// <summary>
        /// 1) Get preset dimensions and determine portrait vs landscape from width/height.
        /// </summary>
        private static (int widthDots, int heightDots, bool isPortrait) GetDimensionsAndStyle(int widthDots, int heightDots)
        {
            bool isPortrait = heightDots > widthDots; // portrait = tall label; landscape = wide or square
            return (widthDots, heightDots, isPortrait);
        }

        /// <summary>
        /// 2) Compute proportional regions from canvas size; one consistent model for all presets.
        /// </summary>
        private static LabelLayout ComputeProportionalLayout(int widthDots, int heightDots, bool isPortrait)
        {
            int minDimension = Math.Min(widthDots, heightDots);

            // Outer padding: about 5% of the smaller label dimension
            int outerPadding = Math.Max(10, (int)(0.05 * minDimension));
            int usableWidth = widthDots - 2 * outerPadding;
            int usableHeight = heightDots - 2 * outerPadding;

            // Barcode region: centered, ~80% of usable width, ~55–65% of usable height (portrait vs landscape)
            double barcodeHeightRatio = isPortrait ? 0.60 : 0.58;
            int barcodeRegionWidth = (int)(0.80 * usableWidth);
            int barcodeRegionHeight = (int)(barcodeHeightRatio * usableHeight);
            int barcodeX = outerPadding + (usableWidth - barcodeRegionWidth) / 2;
            int barcodeY = outerPadding;

            // Breathing room between barcode and text: ~3% of label height
            int gapBetweenBarcodeAndText = Math.Max(4, (int)(0.03 * heightDots));
            // Text region: shallow band below barcode; font height ~10% of label height (8–12% band)
            int textHeightDots = Math.Max(12, (int)(0.10 * heightDots));
            int textWidthDots = Math.Max(8, (int)(0.08 * minDimension)); // character width in dots
            int textY = barcodeY + barcodeRegionHeight + gapBetweenBarcodeAndText;

            // Module width for ^BY: scale with label so barcode bars are not too thick or thin
            int moduleWidth = Math.Clamp((int)(minDimension / 120), 1, 4);

            return new LabelLayout
            {
                WidthDots = widthDots,
                HeightDots = heightDots,
                IsPortrait = isPortrait,
                OuterPadding = outerPadding,
                UsableWidth = usableWidth,
                UsableHeight = usableHeight,
                BarcodeRegionWidth = barcodeRegionWidth,
                BarcodeRegionHeight = barcodeRegionHeight,
                BarcodeX = barcodeX,
                BarcodeY = barcodeY,
                GapBetweenBarcodeAndText = gapBetweenBarcodeAndText,
                TextY = textY,
                TextHeightDots = textHeightDots,
                TextWidthDots = textWidthDots,
                ModuleWidth = moduleWidth
            };
        }

        /// <summary>
        /// 3) Build ZPL from layout and content; uses explicit ^BCo and ^A0o (no global ^FW).
        /// </summary>
        private static string BuildZplFromLayout(string barcodeValue, LabelLayout layout, char orientation)
        {
            string humanReadable = barcodeValue;
            var b = new StringBuilder();

            b.AppendLine("^XA");
            b.AppendLine($"^PW{layout.WidthDots}");
            b.AppendLine($"^LL{layout.HeightDots}");

            // Barcode: module width then Code 128 with explicit orientation and height
            b.AppendLine($"^BY{layout.ModuleWidth},2,1");
            b.AppendLine($"^FO{layout.BarcodeX},{layout.BarcodeY}");
            b.AppendLine($"^BC{orientation},{layout.BarcodeRegionHeight},N,N,N");
            b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");

            // Text: centered in a block under the barcode; font size proportional to label
            b.AppendLine($"^FO{layout.BarcodeX},{layout.TextY}");
            b.AppendLine($"^FB{layout.BarcodeRegionWidth},1,0,0,C");
            b.AppendLine($"^A0{orientation},{layout.TextHeightDots},{layout.TextWidthDots}");
            b.AppendLine($"^FD{EscapeZpl(humanReadable)}^FS");

            b.AppendLine("^XZ");
            return b.ToString();
        }

        /// <summary>
        /// Builds ZPL for one label: proportional layout, one Code 128 barcode + one text line (same as barcode value).
        /// Rotation applied per element via ^BCo and ^A0o only.
        /// </summary>
        private static string GenerateBuilderZpl(string barcodeValue, int widthDots, int heightDots, int rotation)
        {
            char orientation = GetZplOrientationForRotation(rotation);

            // Step 1: dimensions and portrait vs landscape
            var (_, _, isPortrait) = GetDimensionsAndStyle(widthDots, heightDots);

            // Step 2: proportional regions (same rules for all preset sizes)
            LabelLayout layout = ComputeProportionalLayout(widthDots, heightDots, isPortrait);

            // Step 3: build ZPL
            return BuildZplFromLayout(barcodeValue, layout, orientation);
        }

        private async Task<(bool Success, string Message)> SendZplToPrinterAsync(string zpl)
        {
            var host = PrinterIpTextBox?.Text?.Trim();
            var portText = PortTextBox?.Text?.Trim();

            if (string.IsNullOrEmpty(host))
            {
                return (false, "Invalid printer IP");
            }

            if (!int.TryParse(portText, out var port) || port < 1 || port > 65535)
            {
                return (false, "Invalid port");
            }

            using var client = new TcpClient();

            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));

            try
            {
                var finished = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (finished != connectTask)
                {
                    try { client.Close(); } catch { /* ignore */ }
                    return (false, "Send timed out");
                }

                await connectTask.ConfigureAwait(false);

                using var networkStream = client.GetStream();
                var bytes = Encoding.UTF8.GetBytes(zpl);

                var writeTask = networkStream.WriteAsync(bytes, 0, bytes.Length);
                var finishedWrite = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                if (finishedWrite != writeTask)
                {
                    try { client.Close(); } catch { /* ignore */ }
                    return (false, "Send timed out");
                }

                await writeTask.ConfigureAwait(false);

                try
                {
                    await networkStream.FlushAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }

                try { client.Close(); } catch { /* ignore */ }

                return (true, $"Send successful to {host}:{port}");
            }
            catch (SocketException ex)
            {
                try { client.Close(); } catch { /* ignore */ }
                return (false, $"Send failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                try { client.Close(); } catch { /* ignore */ }
                return (false, $"Send failed: {ex.Message}");
            }
        }
    }
}
