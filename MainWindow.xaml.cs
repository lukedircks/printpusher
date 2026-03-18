using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PrintPusher
{
    public partial class MainWindow : Window
    {
        private int _currentRotation = 0; // 0, 90, 180, 270

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => UpdatePreviewAndZpl();
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

                // Validate width/height (in inches)
                if (!double.TryParse(widthText, out var widthInches) || widthInches <= 0)
                {
                    StatusLabel.Content = "Invalid width";
                    Console.WriteLine("Invalid width: " + widthText);
                    return;
                }

                if (!double.TryParse(heightText, out var heightInches) || heightInches <= 0)
                {
                    StatusLabel.Content = "Invalid height";
                    Console.WriteLine("Invalid height: " + heightText);
                    return;
                }

                // Convert inches to dots @203 dpi
                const double dpi = 203.0;
                var widthDots = (int)(widthInches * dpi);
                var heightDots = (int)(heightInches * dpi);

                var zplBuilder = new StringBuilder();

                if (!autoInc)
                {
                    if (string.IsNullOrEmpty(barcodeInput))
                    {
                        StatusLabel.Content = "Barcode value required";
                        Console.WriteLine("Barcode value required");
                        return;
                    }

                    var labelZpl = GenerateBuilderZpl(barcodeInput, textInput, widthDots, heightDots, _currentRotation);
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

                        var labelZpl = GenerateBuilderZpl(value, human, widthDots, heightDots, _currentRotation);
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

        private void UpdatePreviewAndZpl()
        {
            // TextChanged can fire while XAML is still initializing controls.
            if (!IsLoaded || PreviewCanvas == null || RawZplTextBox == null)
                return;

            try
            {
                var widthText = WidthTextBox?.Text?.Trim() ?? string.Empty;
                var heightText = HeightTextBox?.Text?.Trim() ?? string.Empty;
                var barcodeInput = BarcodeTextBox?.Text?.Trim() ?? string.Empty;
                var textInput = TextTextBox?.Text?.Trim() ?? string.Empty;

                // Try to parse dimensions
                if (!double.TryParse(widthText, out var widthInches) || widthInches <= 0)
                {
                    PreviewCanvas.Children.Clear();
                    return;
                }

                if (!double.TryParse(heightText, out var heightInches) || heightInches <= 0)
                {
                    PreviewCanvas.Children.Clear();
                    return;
                }

                // Convert inches to dots @203 dpi
                const double dpi = 203.0;
                var widthDots = (int)(widthInches * dpi);
                var heightDots = (int)(heightInches * dpi);

                // Generate ZPL
                string zpl = GenerateBuilderZpl(barcodeInput, textInput, widthDots, heightDots, _currentRotation);
                RawZplTextBox.Text = zpl;

                // Draw preview
                DrawPreview(widthInches, heightInches, _currentRotation);
            }
            catch
            {
                PreviewCanvas?.Children.Clear();
            }
        }

        private void DrawPreview(double widthInches, double heightInches, int rotation)
        {
            PreviewCanvas.Children.Clear();

            if (widthInches <= 0 || heightInches <= 0)
                return;

            // Scale to fit canvas
            var canvasWidth = PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth : 300;
            var canvasHeight = PreviewCanvas.ActualHeight > 0 ? PreviewCanvas.ActualHeight : 200;

            double scaleWidth = canvasWidth / widthInches;
            double scaleHeight = canvasHeight / heightInches;
            double scale = Math.Min(scaleWidth, scaleHeight) * 0.9; // 90% to leave margin

            double displayWidth = widthInches * scale;
            double displayHeight = heightInches * scale;

            // Center in canvas
            double offsetX = (canvasWidth - displayWidth) / 2;
            double offsetY = (canvasHeight - displayHeight) / 2;

            // Draw label border
            var labelRect = new Rectangle
            {
                Width = displayWidth,
                Height = displayHeight,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Fill = Brushes.White
            };
            Canvas.SetLeft(labelRect, offsetX);
            Canvas.SetTop(labelRect, offsetY);
            PreviewCanvas.Children.Add(labelRect);

            // Calculate content area (with padding)
            double padPercent = 0.08; // 8% padding
            double contentX = offsetX + displayWidth * padPercent;
            double contentY = offsetY + displayHeight * padPercent;
            double contentWidth = displayWidth * (1 - 2 * padPercent);
            double contentHeight = displayHeight * (1 - 2 * padPercent);

            // Draw approximate barcode area (70% of content)
            double barcodeHeight = contentHeight * 0.7;
            var barcodeRect = new Rectangle
            {
                Width = contentWidth,
                Height = barcodeHeight,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) // Light gray fill
            };
            Canvas.SetLeft(barcodeRect, contentX);
            Canvas.SetTop(barcodeRect, contentY);
            PreviewCanvas.Children.Add(barcodeRect);

            // Add barcode label
            var barcodeLabel = new TextBlock
            {
                Text = "Barcode",
                FontSize = 10,
                Foreground = Brushes.Gray
            };
            Canvas.SetLeft(barcodeLabel, contentX + 4);
            Canvas.SetTop(barcodeLabel, contentY + 4);
            PreviewCanvas.Children.Add(barcodeLabel);

            // Draw approximate text area (below barcode)
            double textY = contentY + barcodeHeight + 5;
            double textHeight = contentHeight * 0.25;
            var textRect = new Rectangle
            {
                Width = contentWidth,
                Height = textHeight,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)) // Very light gray fill
            };
            Canvas.SetLeft(textRect, contentX);
            Canvas.SetTop(textRect, textY);
            PreviewCanvas.Children.Add(textRect);

            // Add text label
            var textLabel = new TextBlock
            {
                Text = "Text",
                FontSize = 10,
                Foreground = Brushes.Gray
            };
            Canvas.SetLeft(textLabel, contentX + 4);
            Canvas.SetTop(textLabel, textY + 4);
            PreviewCanvas.Children.Add(textLabel);

            // Draw rotation indicator if not 0°
            if (rotation != 0)
            {
                var rotLabel = new TextBlock
                {
                    Text = $"{rotation}°",
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Blue
                };
                Canvas.SetLeft(rotLabel, offsetX + displayWidth - 40);
                Canvas.SetTop(rotLabel, offsetY + 5);
                PreviewCanvas.Children.Add(rotLabel);
            }
        }

        private void InputChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePreviewAndZpl();
        }

        private string EscapeZpl(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Keep it simple: remove newlines and control characters
            return input.Replace("\r", " ").Replace("\n", " ");
        }

        private string GenerateBuilderZpl(string barcodeValue, string humanText, int widthDots, int heightDots, int rotation)
        {
            // Generate ZPL with optimal layout for the given dimensions and rotation
            var b = new StringBuilder();

            b.AppendLine("^XA");

            // Set field orientation based on rotation
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

            // Set label dimensions
            b.AppendLine($"^PW{widthDots}");
            b.AppendLine($"^LL{heightDots}");

            // Calculate layout for this orientation
            const int padding = 20; // dots
            int usableWidth = widthDots - 2 * padding;
            int usableHeight = heightDots - 2 * padding;

            // Determine if label is wide or tall based on aspect ratio
            bool isWide = widthDots > heightDots;

            if (isWide)
            {
                // For wide labels (like 4x6): barcode on left, text on top-right or stacked
                int barcodeWidth = (int)(usableWidth * 0.65);
                int barcodeHeight = usableHeight - 40;
                int barcodeX = padding;
                int barcodeY = padding + 20;

                // Barcode
                b.AppendLine($"^FO{barcodeX},{barcodeY}");
                b.AppendLine($"^BCN,{barcodeHeight},Y,N,N");
                b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");

                // Text area on the right
                int textX = padding + barcodeWidth + 10;
                int textY = padding;
                int textWidth = usableWidth - barcodeWidth - 10;

                b.AppendLine($"^FO{textX},{textY}");
                b.AppendLine($"^A0N,24,20");
                b.AppendLine($"^FD{EscapeZpl(humanText)}^FS");
            }
            else
            {
                // For tall labels (like 3x2 or square): barcode on top, text below
                int barcodeHeight = (int)(usableHeight * 0.65);
                int barcodeX = padding;
                int barcodeY = padding;

                b.AppendLine($"^FO{barcodeX},{barcodeY}");
                b.AppendLine($"^BCN,{barcodeHeight},Y,N,N");
                b.AppendLine($"^FD{EscapeZpl(barcodeValue)}^FS");

                // Text below barcode
                int textX = padding;
                int textY = padding + barcodeHeight + 15;

                b.AppendLine($"^FO{textX},{textY}");
                b.AppendLine($"^A0N,20,18");
                b.AppendLine($"^FD{EscapeZpl(humanText)}^FS");
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
