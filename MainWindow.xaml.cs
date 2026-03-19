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

        private int _currentRotation; // 0, 90, 180, 270

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
                var barcodeInput = BarcodeTextBox?.Text?.Trim() ?? string.Empty;
                var autoInc = AutoIncrementCheckBox?.IsChecked == true;

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

                    zplBuilder.Append(GenerateBuilderZpl(barcodeInput, widthDots, heightDots, _currentRotation));
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
                        zplBuilder.Append(GenerateBuilderZpl(value, widthDots, heightDots, _currentRotation));
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

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation - 90 + 360) % 360;
            UpdateRotationLabel();
        }

        private void RotateRightButton_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation + 90) % 360;
            UpdateRotationLabel();
        }

        private void UpdateRotationLabel()
        {
            RotationLabel.Content = $"Rotation: {_currentRotation}°";
        }

        private static string EscapeZpl(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// Builds ZPL for one label: Code 128 + one text line (same as barcode value). 203 dpi assumed in PW/LL.
        /// </summary>
        private static string GenerateBuilderZpl(string barcodeValue, int widthDots, int heightDots, int rotation)
        {
            var humanReadable = barcodeValue;

            var b = new StringBuilder();
            b.AppendLine("^XA");

            char fwOrientation;
            switch (rotation)
            {
                case 0: fwOrientation = 'N'; break;
                case 90: fwOrientation = 'R'; break;
                case 180: fwOrientation = 'I'; break;
                case 270: fwOrientation = 'B'; break;
                default: fwOrientation = 'N'; break;
            }
            b.AppendLine($"^FW{fwOrientation}");

            b.AppendLine($"^PW{widthDots}");
            b.AppendLine($"^LL{heightDots}");

            const int padding = 20;
            var usableWidth = widthDots - 2 * padding;
            var usableHeight = heightDots - 2 * padding;
            var isWide = widthDots > heightDots;

            // N,N,N: no extra HRI from ^BC so exactly one human-readable line via ^A0
            if (isWide)
            {
                var barcodeWidth = (int)(usableWidth * 0.65);
                var barcodeHeight = usableHeight - 40;
                var barcodeX = padding;
                var barcodeY = padding + 20;

                b.AppendLine($"^FO{barcodeX},{barcodeY}");
                b.AppendLine($"^BCN,{barcodeHeight},N,N,N");
                b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");

                var textX = padding + barcodeWidth + 10;
                var textY = padding;

                b.AppendLine($"^FO{textX},{textY}");
                b.AppendLine("^A0N,24,20");
                b.AppendLine($"^FD{EscapeZpl(humanReadable)}^FS");
            }
            else
            {
                var barcodeHeight = (int)(usableHeight * 0.65);
                var barcodeX = padding;
                var barcodeY = padding;

                b.AppendLine($"^FO{barcodeX},{barcodeY}");
                b.AppendLine($"^BCN,{barcodeHeight},N,N,N");
                b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");

                var textX = padding;
                var textY = padding + barcodeHeight + 15;

                b.AppendLine($"^FO{textX},{textY}");
                b.AppendLine("^A0N,20,18");
                b.AppendLine($"^FD{EscapeZpl(humanReadable)}^FS");
            }

            b.AppendLine("^XZ");
            return b.ToString();
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
