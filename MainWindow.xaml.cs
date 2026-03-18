using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace PrintPusher
{
    public partial class MainWindow : Window
    {
        private int _currentRotation = 0; // 0, 90, 180, 270

        public MainWindow()
        {
            InitializeComponent();
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
                    // Observe any exceptions
                    await connectTask.ConfigureAwait(false);

                    // Back on UI thread to update status
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusLabel.Content = $"Connection successful to {host}:{port}";
                        Console.WriteLine($"Connection successful to {host}:{port}");
                    });
                }
                else
                {
                    // timeout
                    try { client.Close(); } catch { }
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
                var textInput = TextTextBox?.Text?.Trim() ?? string.Empty;
                var widthText = WidthTextBox?.Text?.Trim() ?? string.Empty;
                var heightText = HeightTextBox?.Text?.Trim() ?? string.Empty;
                var autoInc = AutoIncrementCheckBox?.IsChecked == true;

                // Validate width/height (in mm)
                if (!int.TryParse(widthText, out var widthMm) || widthMm <= 0)
                {
                    StatusLabel.Content = "Invalid width";
                    Console.WriteLine("Invalid width: " + widthText);
                    return;
                }

                if (!int.TryParse(heightText, out var heightMm) || heightMm <= 0)
                {
                    StatusLabel.Content = "Invalid height";
                    Console.WriteLine("Invalid height: " + heightText);
                    return;
                }

                // Convert mm to dots @203 dpi
                const double dpi = 203.0;
                var widthDots = (int)(widthMm * dpi / 25.4);
                var heightDots = (int)(heightMm * dpi / 25.4);

                var zplBuilder = new StringBuilder();

                if (!autoInc)
                {
                    if (string.IsNullOrEmpty(barcodeInput))
                    {
                        StatusLabel.Content = "Barcode value required";
                        Console.WriteLine("Barcode value required");
                        return;
                    }

                    var labelZpl = BuildSingleLabelZpl(barcodeInput, textInput, widthDots, lengthDots, _currentRotation);
                    zplBuilder.Append(labelZpl);
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
                        string human;
                        if (string.IsNullOrWhiteSpace(textInput))
                        {
                            human = value;
                        }
                        else
                        {
                            human = textInput + " " + value;
                        }

                        var labelZpl = BuildSingleLabelZpl(value, human, widthDots, heightDots, _currentRotation);
                        zplBuilder.Append(labelZpl);
                    }
                }

                var finalZpl = zplBuilder.ToString();

                RawZplTextBox.Text = finalZpl;
                // Switch to Raw ZPL tab (index 1)
                try { MainTabControl.SelectedIndex = 1; } catch { }

                // Send the generated ZPL to the printer
                GeneratePrintButton.IsEnabled = false;
                StatusLabel.Content = "Sending generated ZPL...";
                Console.WriteLine("Sending generated ZPL...");

                var result = await SendZplToPrinterAsync(finalZpl).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    StatusLabel.Content = result.Success ? result.Message : result.Message;
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
                StatusLabel.Content = result.Success ? result.Message : result.Message;
                Console.WriteLine(result.Message);
                SendRawZplButton.IsEnabled = true;
            });
        }

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation - 90 + 360) % 360;
            UpdateRotationLabel();
            UpdatePreviewAndZpl();
        }

        private void RotateRightButton_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation + 90) % 360;
            UpdateRotationLabel();
            UpdatePreviewAndZpl();
        }

        private void UpdateRotationLabel()
        {
            RotationLabel.Content = $"Rotation: {_currentRotation}°";
        }

        private string EscapeZpl(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Keep it simple: remove newlines and control characters
            return input.Replace("\r", " ").Replace("\n", " ");
        }

        private string BuildSingleLabelZpl(string barcodeValue, string humanText, int widthDots, int lengthDots, int rotation)
        {
            // Improved ZPL for better scaling and rotation
            var b = new StringBuilder();

            // Start label
            b.AppendLine("^XA");

            // Set default field orientation based on rotation
            char fwOrientation;
            switch (rotation)
            {
                case 0: fwOrientation = 'N'; break; // Normal
                case 90: fwOrientation = 'R'; break; // Rotated 90
                case 180: fwOrientation = 'I'; break; // Inverted
                case 270: fwOrientation = 'B'; break; // Rotated 270
                default: fwOrientation = 'N'; break;
            }
            b.AppendLine($"^FW{fwOrientation}");

            // Set label width and length
            b.AppendLine($"^PW{widthDots}");
            b.AppendLine($"^LL{lengthDots}");

            // Calculate usable area with padding
            const int padding = 20; // dots
            var usableWidth = widthDots - 2 * padding;
            var usableLength = lengthDots - 2 * padding;

            // Barcode height: take most of the length, but leave room for text
            var barcodeHeight = (int)(usableLength * 0.7); // 70% of usable length
            var textHeight = 30; // fixed text height
            var spacing = 10;

            // Position barcode at top-left of usable area
            var barcodeX = padding;
            var barcodeY = padding;

            // Text below barcode
            var textX = padding;
            var textY = barcodeY + barcodeHeight + spacing;

            // Orientation for barcode: N for normal, but since rotation is global, use N
            char barcodeOrientation = 'N';

            b.AppendLine($"^FO{barcodeX},{barcodeY}");
            b.AppendLine($"^BC{barcodeOrientation},{barcodeHeight},Y,N,N");
            b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");

            // Human readable text
            b.AppendLine($"^FO{textX},{textY}");
            b.AppendLine($"^A0N,{textHeight},{textHeight}");
            b.AppendLine($"^FD{EscapeZpl(humanText)}^FS");

            // End label
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
                    try { client.Close(); } catch { }
                    return (false, "Send timed out");
                }

                // observe exceptions
                await connectTask.ConfigureAwait(false);

                using var networkStream = client.GetStream();
                var bytes = Encoding.UTF8.GetBytes(zpl);

                var writeTask = networkStream.WriteAsync(bytes, 0, bytes.Length);
                var finishedWrite = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                if (finishedWrite != writeTask)
                {
                    try { client.Close(); } catch { }
                    return (false, "Send timed out");
                }

                await writeTask.ConfigureAwait(false);

                try
                {
                    await networkStream.FlushAsync().ConfigureAwait(false);
                }
                catch { }

                try { client.Close(); } catch { }

                return (true, $"Send successful to {host}:{port}");
            }
            catch (SocketException ex)
            {
                try { client.Close(); } catch { }
                return (false, $"Send failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                try { client.Close(); } catch { }
                return (false, $"Send failed: {ex.Message}");
            }
        }
    }
}
