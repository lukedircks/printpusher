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
        /// Computed layout values for one label (all in dots). No ^FB; all positions from ^FO.
        /// </summary>
        private sealed class LabelLayout
        {
            public int WidthDots { get; init; }
            public int HeightDots { get; init; }
            public int PaddingX { get; init; }
            public int PaddingTop { get; init; }
            public int Spacing { get; init; }
            public int UsableWidth { get; init; }
            public int ModuleWidth { get; init; }
            public int BarcodeX { get; init; }
            public int BarcodeY { get; init; }
            public int BarcodeHeight { get; init; }
            public int TextX { get; init; }
            public int TextY { get; init; }
            public int FontHeight { get; init; }
            public int FontWidth { get; init; }
        }

        /// <summary>
        /// Step 1: Get label dimensions (preset already converted to dots at 203 dpi).
        /// </summary>
        private static (int widthDots, int heightDots) GetLabelDimensions(int widthDots, int heightDots)
        {
            return (widthDots, heightDots);
        }

        /// <summary>
        /// Step 2: Compute padding and spacing from label size (~5% each).
        /// </summary>
        private static (int paddingX, int paddingTop, int spacing, int usableWidth) ComputePadding(int widthDots, int heightDots)
        {
            // Horizontal padding: ~5% of label width
            int paddingX = Math.Max(10, (int)(widthDots * 0.05));
            // Top padding: ~5% of label height
            int paddingTop = Math.Max(10, (int)(heightDots * 0.05));
            // Spacing between barcode and text: ~5% of label height
            int spacing = Math.Max(4, (int)(heightDots * 0.05));
            // Usable width for barcode (full width minus side padding)
            int usableWidth = widthDots - (paddingX * 2);
            return (paddingX, paddingTop, spacing, usableWidth);
        }

        /// <summary>
        /// Step 3: Compute barcode size so it fills usable width. Code 128 module count approx (len*11)+35; module width clamped 2–6.
        /// Barcode is centered horizontally within the usable area.
        /// </summary>
        private static (int moduleWidth, int barcodeX, int barcodeY, int barcodeHeight) ComputeBarcodeSize(
            int paddingX, int paddingTop, int usableWidth, int heightDots, int valueLength)
        {
            // Approximate Code 128 modules for the data (start + data + check + stop)
            int estimatedModules = (valueLength * 11) + 35;
            if (estimatedModules < 1) estimatedModules = 1;
            // Module width so barcode fills usable width; clamp for readability
            int moduleWidth = usableWidth / estimatedModules;
            moduleWidth = Math.Clamp(moduleWidth, 2, 6);
            // Actual barcode width in dots (so we can center it)
            int barcodeWidthDots = estimatedModules * moduleWidth;
            int barcodeX = paddingX + (usableWidth - barcodeWidthDots) / 2;
            if (barcodeX < paddingX) barcodeX = paddingX;
            int barcodeY = paddingTop;
            // Barcode height: ~60% of label height
            int barcodeHeight = Math.Max(20, (int)(heightDots * 0.60));
            return (moduleWidth, barcodeX, barcodeY, barcodeHeight);
        }

        /// <summary>
        /// Step 4: Compute text position and font size. Text centered horizontally; position from estimated width.
        /// </summary>
        private static (int textX, int textY, int fontHeight, int fontWidth) ComputeTextPosition(
            int widthDots, int heightDots, int barcodeY, int barcodeHeight, int spacing, int charCount)
        {
            // Font size: ~10% of label height (in 8–12% range)
            int fontHeight = Math.Max(12, (int)(heightDots * 0.10));
            int fontWidth = Math.Max(8, (int)(fontHeight * 0.6));
            // Estimated total text width in dots: charCount * (fontWidth * 0.6)
            int estimatedTextWidth = (int)(charCount * (fontWidth * 0.6));
            if (estimatedTextWidth < 1) estimatedTextWidth = fontWidth;
            // Center text horizontally
            int textX = (widthDots - estimatedTextWidth) / 2;
            if (textX < 0) textX = 0;
            int textY = barcodeY + barcodeHeight + spacing;
            return (textX, textY, fontHeight, fontWidth);
        }

        /// <summary>
        /// Step 5: Build ZPL string from layout. Only ^FO for position; no ^FB. Rotation in ^BC and ^A0 only.
        /// </summary>
        private static string BuildZpl(string barcodeValue, LabelLayout layout, char orientation)
        {
            var b = new StringBuilder();
            b.AppendLine("^XA");
            b.AppendLine($"^PW{layout.WidthDots}");
            b.AppendLine($"^LL{layout.HeightDots}");
            b.AppendLine($"^BY{layout.ModuleWidth},2,1");
            b.AppendLine($"^FO{layout.BarcodeX},{layout.BarcodeY}");
            b.AppendLine($"^BC{orientation},{layout.BarcodeHeight},N,N,N");
            b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");
            b.AppendLine($"^FO{layout.TextX},{layout.TextY}");
            b.AppendLine($"^A0{orientation},{layout.FontHeight},{layout.FontWidth}");
            b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");
            b.AppendLine("^XZ");
            return b.ToString();
        }

        /// <summary>
        /// Builds ZPL for one label: deterministic layout from dimensions and barcode value, explicit ^FO only.
        /// Rotation does not change layout math; applied via ^BCo and ^A0o.
        /// </summary>
        private static string GenerateBuilderZpl(string barcodeValue, int widthDots, int heightDots, int rotation)
        {
            char orientation = GetZplOrientationForRotation(rotation);
            int valueLength = barcodeValue?.Length ?? 0;
            int charCount = Math.Max(1, valueLength);

            // Step 1: label dimensions (already in dots)
            var (w, h) = GetLabelDimensions(widthDots, heightDots);

            // Step 2: padding and usable width
            var (paddingX, paddingTop, spacing, usableWidth) = ComputePadding(w, h);

            // Step 3: barcode size and position
            var (moduleWidth, barcodeX, barcodeY, barcodeHeight) = ComputeBarcodeSize(paddingX, paddingTop, usableWidth, h, valueLength);

            // Step 4: text position and font (centered)
            var (textX, textY, fontHeight, fontWidth) = ComputeTextPosition(w, h, barcodeY, barcodeHeight, spacing, charCount);

            var layout = new LabelLayout
            {
                WidthDots = w,
                HeightDots = h,
                PaddingX = paddingX,
                PaddingTop = paddingTop,
                Spacing = spacing,
                UsableWidth = usableWidth,
                ModuleWidth = moduleWidth,
                BarcodeX = barcodeX,
                BarcodeY = barcodeY,
                BarcodeHeight = barcodeHeight,
                TextX = textX,
                TextY = textY,
                FontHeight = fontHeight,
                FontWidth = fontWidth
            };

            return BuildZpl(barcodeValue ?? string.Empty, layout, orientation);
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
